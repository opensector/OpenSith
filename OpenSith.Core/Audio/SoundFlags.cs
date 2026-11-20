using System;

namespace OpenSith.Core.Audio;

/// <summary>
/// Sith Engine sound playback and caching flags (SITHSOUNDFLAG).
/// Matches LucasArts Jedi Knight engine bitmask definitions.
/// </summary>
[Flags]
public enum SoundFlags : uint
{
    /// <summary>No special playback flags.</summary>
    None = 0,

    /// <summary>Loop playback indefinitely until stopped.</summary>
    Loop = 0x1,

    /// <summary>Sound is currently fading.</summary>
    Fading = 0x2,

    /// <summary>Ambient sound with no 3D stereo panning.</summary>
    AmbientNoPan = 0x4,

    /// <summary>Calculate Doppler effect pitch shift.</summary>
    Doppler = 0x8,

    /// <summary>Fade sound in on play.</summary>
    FadeIn = 0x10,

    /// <summary>Fade sound out on stop.</summary>
    FadeOut = 0x20,

    /// <summary>Play at absolute fixed world position.</summary>
    Absolute = 0x40,

    /// <summary>Sound follows attached game thing position.</summary>
    FollowsThing = 0x80,

    /// <summary>High priority playback channel.</summary>
    HighPriority = 0x100,

    /// <summary>Highest priority playback channel.</summary>
    HighestPriority = 0x200,

    /// <summary>Mutually exclusive playback across all sources (do not overlap globally).</summary>
    MutuallyExclusivePlaybackAbsolute = 0x400,

    /// <summary>Mutually exclusive playback on the source thing (do not overlap per thing).</summary>
    MutuallyExclusivePlaybackThing = 0x800,

    /// <summary>Velocity-dependent pitch bending.</summary>
    PerVelocityPitchBend = 0x1000,

    /// <summary>Underwater sound filtering.</summary>
    Underwater = 0x2000,

    /// <summary>Precache audio sample into memory during level initialization.</summary>
    Precached = 0x4000,

    /// <summary>Use duplicate audio playback buffer.</summary>
    UseDuplicateBuffer = 0x8000,

    /// <summary>Dialogue / voice line.</summary>
    Voice = 0x10000,

    /// <summary>Sound is actively playing.</summary>
    Playing = 0x20000,

    /// <summary>Sound playback is paused.</summary>
    Paused = 0x40000,

    /// <summary>Bypass 3D positional audio processing.</summary>
    No3D = 0x80000,
}
