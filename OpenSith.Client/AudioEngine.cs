using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSith.Core.Audio;
using OpenSith.Core.Vfs;
using Silk.NET.OpenAL;

namespace OpenSith.Client;

/// <summary>
/// Cross-platform OpenAL audio engine for playing looping music/ambience and 2D/3D WAV SFX from the VFS.
/// Gracefully degrades to a silent no-op if no OpenAL native library or audio device is available.
/// </summary>
public sealed unsafe class AudioEngine : IDisposable
{
    private const int MaxOneShotSources = 32;

    private readonly IVirtualFileSystem _vfs;
    private readonly Dictionary<string, uint> _bufferCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<uint> _oneShotSources = [];
    private readonly List<uint> _loopingSources = [];

    private ALContext? _alc;
    private AL? _al;
    private Device* _device;
    private Context* _context;
    private uint _musicSource;
    private bool _initialized;
    private bool _disposed;

    public bool IsAvailable => _initialized;

    public AudioEngine(IVirtualFileSystem vfs)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
        InitializeOpenAl();
    }

    private void InitializeOpenAl()
    {
        foreach (bool useSoft in new[] { false, true })
        {
            try
            {
                var alc = ALContext.GetApi(useSoft);
                var device = alc.OpenDevice(null);
                if (device == null)
                {
                    alc.Dispose();
                    continue;
                }

                var context = alc.CreateContext(device, null);
                if (context == null || !alc.MakeContextCurrent(context))
                {
                    if (context != null)
                        alc.DestroyContext(context);
                    alc.CloseDevice(device);
                    alc.Dispose();
                    continue;
                }

                var al = AL.GetApi(useSoft);
                al.DistanceModel(DistanceModel.InverseDistanceClamped);

                _alc = alc;
                _al = al;
                _device = device;
                _context = context;
                _initialized = true;
                Console.WriteLine($"[AUDIO] OpenAL initialized (soft={useSoft}).");
                return;
            }
            catch
            {
                // Try fallback variant or degrade gracefully
            }
        }

        Console.WriteLine("[AUDIO] OpenAL device/runtime unavailable; running muted.");
    }

    /// <summary>
    /// Updates the 3D OpenAL listener position and orientation, and reclaims finished one-shot sources.
    /// </summary>
    public void Update(Vector3 listenerPos, Vector3 forward, Vector3 up)
    {
        if (!_initialized || _al == null || _disposed)
            return;

        _al.SetListenerProperty(ListenerVector3.Position, listenerPos.X, listenerPos.Y, listenerPos.Z);

        Span<float> orientation = stackalloc float[6];
        orientation[0] = forward.X;
        orientation[1] = forward.Y;
        orientation[2] = forward.Z;
        orientation[3] = up.X;
        orientation[4] = up.Y;
        orientation[5] = up.Z;
        fixed (float* pOri = orientation)
        {
            _al.SetListenerProperty(ListenerFloatArray.Orientation, pOri);
        }

        ReclaimFinishedSources();
    }

    /// <summary>
    /// Starts or replaces the primary looping 2D music/ambience track.
    /// </summary>
    public void PlayLoopingMusic(string wavName, float volume = 0.4f)
    {
        if (!_initialized || _al == null || _disposed)
            return;

        if (_musicSource != 0)
        {
            _al.SourceStop(_musicSource);
            _al.DeleteSource(_musicSource);
            _musicSource = 0;
        }

        uint buffer = GetOrLoadBuffer(wavName);
        if (buffer == 0)
            return;

        uint src = _al.GenSource();
        _al.SetSourceProperty(src, SourceInteger.Buffer, buffer);
        _al.SetSourceProperty(src, SourceBoolean.SourceRelative, true);
        _al.SetSourceProperty(src, SourceVector3.Position, 0f, 0f, 0f);
        _al.SetSourceProperty(src, SourceBoolean.Looping, true);
        _al.SetSourceProperty(src, SourceFloat.Gain, Math.Clamp(volume, 0f, 2f));
        _al.SourcePlay(src);
        _musicSource = src;
    }

    /// <summary>
    /// Plays a 2D (non-spatialized) sound effect relative to the listener.
    /// </summary>
    public uint PlaySoundLocal(string wavName, float volume = 1.0f, bool looping = false)
    {
        if (!_initialized || _al == null || _disposed)
            return 0;

        uint buffer = GetOrLoadBuffer(wavName);
        if (buffer == 0)
            return 0;

        uint src = AcquireSource(looping);
        if (src == 0)
            return 0;

        _al.SetSourceProperty(src, SourceInteger.Buffer, buffer);
        _al.SetSourceProperty(src, SourceBoolean.SourceRelative, true);
        _al.SetSourceProperty(src, SourceVector3.Position, 0f, 0f, 0f);
        _al.SetSourceProperty(src, SourceBoolean.Looping, looping);
        _al.SetSourceProperty(src, SourceFloat.Gain, Math.Clamp(volume, 0f, 2f));
        _al.SourcePlay(src);
        return src;
    }

    /// <summary>
    /// Plays a 3D-positioned sound effect in world space.
    /// </summary>
    public uint PlaySoundPos(
        string wavName,
        Vector3 position,
        float volume = 1.0f,
        float minRadius = 0.5f,
        float maxRadius = 8.0f,
        bool looping = false)
    {
        if (!_initialized || _al == null || _disposed)
            return 0;

        uint buffer = GetOrLoadBuffer(wavName);
        if (buffer == 0)
            return 0;

        uint src = AcquireSource(looping);
        if (src == 0)
            return 0;

        float refDist = minRadius > 0.05f ? minRadius : 0.5f;
        float maxDist = maxRadius > refDist ? maxRadius : refDist + 6.0f;

        _al.SetSourceProperty(src, SourceInteger.Buffer, buffer);
        _al.SetSourceProperty(src, SourceBoolean.SourceRelative, false);
        _al.SetSourceProperty(src, SourceVector3.Position, position.X, position.Y, position.Z);
        _al.SetSourceProperty(src, SourceFloat.ReferenceDistance, refDist);
        _al.SetSourceProperty(src, SourceFloat.MaxDistance, maxDist);
        _al.SetSourceProperty(src, SourceFloat.RolloffFactor, 1.0f);
        _al.SetSourceProperty(src, SourceBoolean.Looping, looping);
        _al.SetSourceProperty(src, SourceFloat.Gain, Math.Clamp(volume, 0f, 2f));
        _al.SourcePlay(src);
        return src;
    }

    private uint AcquireSource(bool looping)
    {
        if (_al == null)
            return 0;

        if (!looping)
        {
            ReclaimFinishedSources();
            if (_oneShotSources.Count >= MaxOneShotSources)
            {
                uint oldest = _oneShotSources[0];
                _oneShotSources.RemoveAt(0);
                _al.SourceStop(oldest);
                _al.DeleteSource(oldest);
            }
        }

        uint src = _al.GenSource();
        if (src == 0)
            return 0;

        if (looping)
            _loopingSources.Add(src);
        else
            _oneShotSources.Add(src);

        return src;
    }

    private void ReclaimFinishedSources()
    {
        if (_al == null)
            return;

        for (int i = _oneShotSources.Count - 1; i >= 0; i--)
        {
            uint src = _oneShotSources[i];
            _al.GetSourceProperty(src, GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing && state != (int)SourceState.Paused)
            {
                _al.DeleteSource(src);
                _oneShotSources.RemoveAt(i);
            }
        }
    }

    private uint GetOrLoadBuffer(string wavName)
    {
        if (string.IsNullOrWhiteSpace(wavName) || _al == null)
            return 0;

        string key = wavName.Trim();
        if (_bufferCache.TryGetValue(key, out uint cached))
            return cached;

        string[] searchPaths =
        [
            $"sound/{key}",
            $"voice/{key}",
            key
        ];

        byte[]? wavBytes = null;
        foreach (string path in searchPaths)
        {
            if (_vfs.FileExists(path))
            {
                wavBytes = _vfs.ReadAllBytes(path);
                break;
            }
        }

        if (wavBytes == null ||
            !WavReader.TryParse(wavBytes, out var info) ||
            info == null ||
            !info.IsPcm ||
            info.DataSize <= 0 ||
            info.DataOffset + info.DataSize > wavBytes.Length)
        {
            _bufferCache[key] = 0;
            return 0;
        }

        BufferFormat? format = (info.Channels, info.BitsPerSample) switch
        {
            (1, 8) => BufferFormat.Mono8,
            (1, 16) => BufferFormat.Mono16,
            (2, 8) => BufferFormat.Stereo8,
            (2, 16) => BufferFormat.Stereo16,
            _ => null
        };

        if (!format.HasValue)
        {
            _bufferCache[key] = 0;
            return 0;
        }

        uint buffer = _al.GenBuffer();
        fixed (byte* pData = &wavBytes[info.DataOffset])
        {
            _al.BufferData(buffer, format.Value, pData, info.DataSize, (int)info.SampleRate);
        }

        _bufferCache[key] = buffer;
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (!_initialized)
            return;

        try
        {
            if (_al != null)
            {
                if (_musicSource != 0)
                {
                    _al.SourceStop(_musicSource);
                    _al.DeleteSource(_musicSource);
                    _musicSource = 0;
                }

                foreach (uint src in _oneShotSources)
                {
                    _al.SourceStop(src);
                    _al.DeleteSource(src);
                }
                _oneShotSources.Clear();

                foreach (uint src in _loopingSources)
                {
                    _al.SourceStop(src);
                    _al.DeleteSource(src);
                }
                _loopingSources.Clear();

                foreach (uint buf in _bufferCache.Values)
                {
                    if (buf != 0)
                        _al.DeleteBuffer(buf);
                }
                _bufferCache.Clear();

                _al.Dispose();
                _al = null;
            }

            if (_alc != null)
            {
                if (_context != null)
                {
                    _alc.MakeContextCurrent(null);
                    _alc.DestroyContext(_context);
                    _context = null;
                }

                if (_device != null)
                {
                    _alc.CloseDevice(_device);
                    _device = null;
                }

                _alc.Dispose();
                _alc = null;
            }
        }
        catch
        {
            // Ignore shutdown errors on partially initialized audio drivers
        }
    }
}
