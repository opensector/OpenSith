using System;

namespace OpenSith.Core.Audio;

/// <summary>
/// Audio encoding format tags defined in the RIFF/WAVE specification.
/// </summary>
public enum WaveAudioFormat : ushort
{
    Unknown = 0,
    Pcm = 1,
    IeeeFloat = 3,
    ALaw = 6,
    MuLaw = 7,
    Extensible = 0xFFFE,
}

/// <summary>
/// Audio header metadata and PCM sample chunk positioning parsed from a RIFF/WAVE file.
/// </summary>
public sealed record WavInfo
{
    /// <summary>Audio coding format (1 = standard uncompressed PCM).</summary>
    public WaveAudioFormat AudioFormat { get; init; } = WaveAudioFormat.Pcm;

    /// <summary>Number of audio channels (1 = mono, 2 = stereo).</summary>
    public ushort Channels { get; init; }

    /// <summary>Sampling rate in Hz (e.g. 11025, 22050, 44100).</summary>
    public uint SampleRate { get; init; }

    /// <summary>Average byte rate in bytes per second (SampleRate * BlockAlign).</summary>
    public uint ByteRate { get; init; }

    /// <summary>Frame block alignment in bytes (Channels * (BitsPerSample / 8)).</summary>
    public ushort BlockAlign { get; init; }

    /// <summary>Bit resolution per sample (typically 8 or 16 for Sith Engine assets).</summary>
    public ushort BitsPerSample { get; init; }

    /// <summary>Byte offset where the raw PCM audio samples begin within the source container.</summary>
    public int DataOffset { get; init; }

    /// <summary>Length in bytes of the raw PCM sample data chunk.</summary>
    public int DataSize { get; init; }

    /// <summary>Total number of sample frames (DataSize / BlockAlign).</summary>
    public int TotalFrames => BlockAlign > 0 ? DataSize / BlockAlign : 0;

    /// <summary>Total playback duration computed from sample count and sample rate.</summary>
    public TimeSpan Duration => SampleRate > 0 && BlockAlign > 0
        ? TimeSpan.FromSeconds((double)TotalFrames / SampleRate)
        : TimeSpan.Zero;

    /// <summary>Total duration in seconds.</summary>
    public double DurationSeconds => Duration.TotalSeconds;

    /// <summary>Total duration in milliseconds.</summary>
    public double DurationMilliseconds => Duration.TotalMilliseconds;

    /// <summary>True if audio is stereo (2 channels).</summary>
    public bool IsStereo => Channels == 2;

    /// <summary>True if uncompressed PCM.</summary>
    public bool IsPcm => AudioFormat == WaveAudioFormat.Pcm;

    /// <summary>Loop start frame index from optional 'smpl' chunk, or null if unlooped.</summary>
    public uint? LoopStart { get; init; }

    /// <summary>Loop end frame index from optional 'smpl' chunk, or null if unlooped.</summary>
    public uint? LoopEnd { get; init; }

    /// <summary>True if the audio file specifies hardware sampler loop points.</summary>
    public bool HasLoop => LoopStart.HasValue && LoopEnd.HasValue;

    public override string ToString() =>
        $"WAVE: {SampleRate}Hz, {BitsPerSample}-bit, {(IsStereo ? "Stereo" : "Mono")}, " +
        $"{TotalFrames} frames ({DurationMilliseconds:F1}ms), DataSize={DataSize}B";
}
