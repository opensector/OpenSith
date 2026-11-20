using System;

namespace OpenSith.Core.Audio;

/// <summary>
/// A sound configuration entry within a Sith Engine SoundClass (.snd).
/// Maps an event mode to an audio file and 3D positioning/volume parameters.
/// </summary>
public sealed class SoundClassEntry
{
    public const SoundFlags DefaultPlayFlags = SoundFlags.Absolute; // 0x40
    public const float DefaultMinRadius = 0.5f;
    public const float DefaultMaxRadius = 2.5f;
    public const float DefaultMaxVolume = 1.0f;

    /// <summary>The sound event enum.</summary>
    public SoundEvent Event { get; init; }

    /// <summary>The original raw event name string (e.g. "lwalkhard").</summary>
    public string EventName { get; init; }

    /// <summary>The referenced WAV filename (e.g. "kwalk01.wav"). May be "none" or empty.</summary>
    public string SoundFileName { get; init; }

    /// <summary>Playback flags controlling looping, 3D panning, pre-caching, priority, etc.</summary>
    public SoundFlags PlayFlags { get; init; }

    /// <summary>Distance at which sound volume is at maximum (100%).</summary>
    public float MinRadius { get; init; }

    /// <summary>Distance at which sound is no longer audible (0%).</summary>
    public float MaxRadius { get; init; }

    /// <summary>Peak volume scalar (typically 0.0 to 1.0).</summary>
    public float MaxVolume { get; init; }

    /// <summary>Whether this entry refers to a playable audio file (not null, empty, or "none").</summary>
    public bool HasSound => !string.IsNullOrEmpty(SoundFileName) &&
                            !SoundFileName.Equals("none", StringComparison.OrdinalIgnoreCase);

    public SoundClassEntry(
        SoundEvent soundEvent,
        string eventName,
        string soundFileName,
        SoundFlags playFlags = DefaultPlayFlags,
        float minRadius = DefaultMinRadius,
        float maxRadius = DefaultMaxRadius,
        float maxVolume = DefaultMaxVolume)
    {
        Event = soundEvent;
        EventName = eventName ?? string.Empty;
        SoundFileName = soundFileName ?? string.Empty;
        PlayFlags = playFlags;
        MinRadius = minRadius;
        MaxRadius = maxRadius;
        MaxVolume = maxVolume;
    }

    public override string ToString() =>
        $"{EventName}: {SoundFileName} (Flags=0x{(uint)PlayFlags:X}, MinR={MinRadius}, MaxR={MaxRadius}, Vol={MaxVolume})";
}
