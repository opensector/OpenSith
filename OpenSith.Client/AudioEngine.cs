using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private sealed class SoundRampState
    {
        public bool HasVolumeRamp;
        public float StartVolume;
        public float TargetVolume;
        public float VolumeElapsed;
        public float VolumeDuration;
        public bool StopOnVolumeEnd;

        public bool HasPitchRamp;
        public float StartPitch;
        public float TargetPitch;
        public float PitchElapsed;
        public float PitchDuration;
    }

    private readonly IVirtualFileSystem _vfs;
    private readonly Dictionary<string, uint> _bufferCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _durationCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<uint> _oneShotSources = [];
    private readonly List<uint> _loopingSources = [];
    private readonly Dictionary<int, uint> _channelSources = new();
    private readonly Dictionary<uint, int> _sourceChannels = new();
    private readonly Dictionary<int, SoundRampState> _ramps = new();

    private ALContext? _alc;
    private AL? _al;
    private Device* _device;
    private Context* _context;
    private uint _musicSource;
    private uint _ambientSource;
    private string? _ambientSound;
    private float _musicVolume = 0.4f;
    private int _nextChannelId = 1;
    private long _lastUpdateTicks = Stopwatch.GetTimestamp();
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

        long now = Stopwatch.GetTimestamp();
        float dt = Math.Clamp((float)(now - _lastUpdateTicks) / Stopwatch.Frequency, 0f, 0.2f);
        _lastUpdateTicks = now;

        ReclaimFinishedSources();
        UpdateRamps(dt);
    }

    /// <summary>
    /// Starts or replaces the primary looping 2D music/ambience track.
    /// </summary>
    public void PlayLoopingMusic(string wavName, float volume = 0.4f)
    {
        _musicVolume = Math.Clamp(volume, 0f, 2f);
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
        _al.SetSourceProperty(src, SourceFloat.Gain, _musicVolume);
        _al.SourcePlay(src);
        _musicSource = src;
    }

    /// <summary>
    /// Adjusts the active music track volume (<c>0.0..2.0</c>).
    /// </summary>
    public void SetMusicVolume(float volume)
    {
        _musicVolume = Math.Clamp(volume, 0f, 2f);
        if (_initialized && _al != null && !_disposed && _musicSource != 0)
        {
            _al.SetSourceProperty(_musicSource, SourceFloat.Gain, _musicVolume);
        }
    }

    /// <summary>
    /// Starts, updates, or clears the active sector ambient loop (<c>SectorSound</c>).
    /// </summary>
    public void SetSectorAmbient(string? wavName, float volume = 0.5f)
    {
        string? normalized = string.IsNullOrWhiteSpace(wavName) ? null : wavName.Trim();
        float clampedVol = Math.Clamp(volume, 0f, 2f);

        if (!_initialized || _al == null || _disposed)
        {
            _ambientSound = normalized;
            return;
        }

        if (string.Equals(_ambientSound, normalized, StringComparison.OrdinalIgnoreCase) && _ambientSource != 0)
        {
            if (normalized != null)
                _al.SetSourceProperty(_ambientSource, SourceFloat.Gain, clampedVol);
            return;
        }

        if (_ambientSource != 0)
        {
            _al.SourceStop(_ambientSource);
            _al.DeleteSource(_ambientSource);
            _ambientSource = 0;
        }

        _ambientSound = normalized;
        if (normalized == null || clampedVol <= 0f)
            return;

        uint buffer = GetOrLoadBuffer(normalized);
        if (buffer == 0)
            return;

        uint src = _al.GenSource();
        _al.SetSourceProperty(src, SourceInteger.Buffer, buffer);
        _al.SetSourceProperty(src, SourceBoolean.SourceRelative, true);
        _al.SetSourceProperty(src, SourceVector3.Position, 0f, 0f, 0f);
        _al.SetSourceProperty(src, SourceBoolean.Looping, true);
        _al.SetSourceProperty(src, SourceFloat.Gain, clampedVol);
        _al.SourcePlay(src);
        _ambientSource = src;
    }

    /// <summary>
    /// Plays a 2D (non-spatialized) sound effect relative to the listener and returns a channel handle.
    /// </summary>
    public int PlaySoundLocal(string wavName, float volume = 1.0f, bool looping = false)
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
        return RegisterChannel(src);
    }

    /// <summary>
    /// Plays a 3D-positioned sound effect in world space and returns a channel handle.
    /// </summary>
    public int PlaySoundPos(
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
        return RegisterChannel(src);
    }

    /// <summary>
    /// Stops (or fades out over <paramref name="fadeOut"/> seconds) an active sound channel.
    /// </summary>
    public void StopSound(int channelId, float fadeOut = 0f)
    {
        if (!_initialized || _al == null || _disposed || channelId <= 0)
            return;

        if (!_channelSources.TryGetValue(channelId, out uint src))
            return;

        if (fadeOut <= 0.001f)
        {
            StopAndDeleteSource(src);
            return;
        }

        _al.GetSourceProperty(src, SourceFloat.Gain, out float curVol);
        var ramp = GetOrCreateRamp(channelId);
        ramp.HasVolumeRamp = true;
        ramp.StartVolume = curVol;
        ramp.TargetVolume = 0f;
        ramp.VolumeElapsed = 0f;
        ramp.VolumeDuration = fadeOut;
        ramp.StopOnVolumeEnd = true;
    }

    /// <summary>
    /// Changes the pitch multiplier of an active sound channel, immediately or over <paramref name="changeTime"/> seconds.
    /// </summary>
    public void ChangeSoundPitch(int channelId, float pitch, float changeTime = 0f)
    {
        if (!_initialized || _al == null || _disposed || channelId <= 0)
            return;

        if (!_channelSources.TryGetValue(channelId, out uint src))
            return;

        float target = Math.Clamp(pitch, 0.1f, 4.0f);
        if (changeTime <= 0.001f)
        {
            _al.SetSourceProperty(src, SourceFloat.Pitch, target);
            if (_ramps.TryGetValue(channelId, out var existing))
                existing.HasPitchRamp = false;
            return;
        }

        _al.GetSourceProperty(src, SourceFloat.Pitch, out float curPitch);
        var ramp = GetOrCreateRamp(channelId);
        ramp.HasPitchRamp = true;
        ramp.StartPitch = curPitch > 0f ? curPitch : 1f;
        ramp.TargetPitch = target;
        ramp.PitchElapsed = 0f;
        ramp.PitchDuration = changeTime;
    }

    /// <summary>
    /// Changes the gain volume of an active sound channel, immediately or over <paramref name="fadeTime"/> seconds.
    /// </summary>
    public void ChangeSoundVol(int channelId, float volume, float fadeTime = 0f)
    {
        if (!_initialized || _al == null || _disposed || channelId <= 0)
            return;

        if (!_channelSources.TryGetValue(channelId, out uint src))
            return;

        float target = Math.Clamp(volume, 0f, 2f);
        if (fadeTime <= 0.001f)
        {
            _al.SetSourceProperty(src, SourceFloat.Gain, target);
            if (_ramps.TryGetValue(channelId, out var existing))
                existing.HasVolumeRamp = false;
            return;
        }

        _al.GetSourceProperty(src, SourceFloat.Gain, out float curVol);
        var ramp = GetOrCreateRamp(channelId);
        ramp.HasVolumeRamp = true;
        ramp.StartVolume = curVol;
        ramp.TargetVolume = target;
        ramp.VolumeElapsed = 0f;
        ramp.VolumeDuration = fadeTime;
        ramp.StopOnVolumeEnd = false;
    }

    /// <summary>
    /// Returns the playback length (in seconds) of a WAV file in the VFS, or <c>0f</c> if missing/unreadable.
    /// Works even when OpenAL hardware initialization is unavailable.
    /// </summary>
    public float GetSoundDuration(string wavName)
    {
        if (string.IsNullOrWhiteSpace(wavName))
            return 0f;

        string key = wavName.Trim();
        if (_durationCache.TryGetValue(key, out float cached))
            return cached;

        byte[]? wavBytes = ReadWavBytesFromVfs(key);
        float duration = 0f;
        if (wavBytes != null && WavReader.TryParse(wavBytes, out var info) && info != null)
            duration = (float)info.DurationSeconds;

        _durationCache[key] = duration;
        return duration;
    }

    private SoundRampState GetOrCreateRamp(int channelId)
    {
        if (!_ramps.TryGetValue(channelId, out var ramp))
        {
            ramp = new SoundRampState();
            _ramps[channelId] = ramp;
        }
        return ramp;
    }

    private void UpdateRamps(float dt)
    {
        if (_al == null || dt <= 0f || _ramps.Count == 0)
            return;

        List<int>? completed = null;
        List<uint>? toStop = null;

        foreach (var (channelId, ramp) in _ramps)
        {
            if (!_channelSources.TryGetValue(channelId, out uint src))
            {
                completed ??= [];
                completed.Add(channelId);
                continue;
            }

            if (ramp.HasVolumeRamp)
            {
                ramp.VolumeElapsed += dt;
                float t = ramp.VolumeDuration > 0f ? Math.Clamp(ramp.VolumeElapsed / ramp.VolumeDuration, 0f, 1f) : 1f;
                float vol = Math.Clamp(ramp.StartVolume + (ramp.TargetVolume - ramp.StartVolume) * t, 0f, 2f);
                _al.SetSourceProperty(src, SourceFloat.Gain, vol);
                if (t >= 1f)
                {
                    ramp.HasVolumeRamp = false;
                    if (ramp.StopOnVolumeEnd)
                    {
                        toStop ??= [];
                        toStop.Add(src);
                    }
                }
            }

            if (ramp.HasPitchRamp)
            {
                ramp.PitchElapsed += dt;
                float t = ramp.PitchDuration > 0f ? Math.Clamp(ramp.PitchElapsed / ramp.PitchDuration, 0f, 1f) : 1f;
                float pitch = Math.Clamp(ramp.StartPitch + (ramp.TargetPitch - ramp.StartPitch) * t, 0.1f, 4.0f);
                _al.SetSourceProperty(src, SourceFloat.Pitch, pitch);
                if (t >= 1f)
                    ramp.HasPitchRamp = false;
            }

            if (!ramp.HasVolumeRamp && !ramp.HasPitchRamp)
            {
                completed ??= [];
                completed.Add(channelId);
            }
        }

        if (completed != null)
        {
            foreach (int ch in completed)
                _ramps.Remove(ch);
        }

        if (toStop != null)
        {
            foreach (uint src in toStop)
                StopAndDeleteSource(src);
        }
    }

    private int RegisterChannel(uint src)
    {
        int channelId = _nextChannelId++;
        _channelSources[channelId] = src;
        _sourceChannels[src] = channelId;
        return channelId;
    }

    private void UnregisterChannel(uint src)
    {
        if (_sourceChannels.Remove(src, out int channelId))
        {
            _channelSources.Remove(channelId);
            _ramps.Remove(channelId);
        }
    }

    private void StopAndDeleteSource(uint src)
    {
        if (_al == null || src == 0)
            return;

        _al.SourceStop(src);
        _al.DeleteSource(src);
        _oneShotSources.Remove(src);
        _loopingSources.Remove(src);
        UnregisterChannel(src);
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
                UnregisterChannel(oldest);
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
                UnregisterChannel(src);
            }
        }
    }

    private byte[]? ReadWavBytesFromVfs(string key)
    {
        string[] searchPaths =
        [
            $"sound/{key}",
            $"voice/{key}",
            key
        ];

        foreach (string path in searchPaths)
        {
            if (_vfs.FileExists(path))
                return _vfs.ReadAllBytes(path);
        }

        return null;
    }

    private uint GetOrLoadBuffer(string wavName)
    {
        if (string.IsNullOrWhiteSpace(wavName) || _al == null)
            return 0;

        string key = wavName.Trim();
        if (_bufferCache.TryGetValue(key, out uint cached))
            return cached;

        byte[]? wavBytes = ReadWavBytesFromVfs(key);

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

        _durationCache[key] = (float)info.DurationSeconds;

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

                if (_ambientSource != 0)
                {
                    _al.SourceStop(_ambientSource);
                    _al.DeleteSource(_ambientSource);
                    _ambientSource = 0;
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
                _channelSources.Clear();
                _sourceChannels.Clear();
                _ramps.Clear();

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
