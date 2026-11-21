using System.Numerics;

namespace OpenSith.Core.Audio;

/// <summary>
/// Headless audio sink interface consumed by <see cref="OpenSith.Core.Simulation.WorldSimulation"/>
/// so world simulation and COG scripts run identically with OpenAL audio or on a headless dedicated server.
/// </summary>
public interface IAudioSink
{
    bool IsAvailable { get; }

    int PlaySoundLocal(string wavName, float volume = 1.0f, bool looping = false);

    int PlaySoundPos(
        string wavName,
        Vector3 position,
        float volume = 1.0f,
        float minRadius = 0.5f,
        float maxRadius = 8.0f,
        bool looping = false);

    void StopSound(int channelId, float fadeOut = 0f);

    void ChangeSoundPitch(int channelId, float pitch, float changeTime = 0f);

    void ChangeSoundVol(int channelId, float volume, float fadeTime = 0f);

    void SetMusicVolume(float volume);

    void SetSectorAmbient(string? wavName, float volume = 0.5f);

    float GetSoundDuration(string wavName);
}
