using System;
using System.Numerics;
using OpenSith.Core.Audio;

namespace OpenSith.Core.Networking;

/// <summary>
/// Headless <see cref="IAudioSink"/> implementation for <see cref="DedicatedGameServer"/>
/// that forwards triggered local and 3D positional sound cues to connected clients.
/// </summary>
public sealed class ServerAudioSink : IAudioSink
{
    private readonly Action<string, Vector3, float, bool> _onSound;
    private int _nextChannelId = 1;

    public ServerAudioSink(Action<string, Vector3, float, bool> onSound)
    {
        _onSound = onSound ?? throw new ArgumentNullException(nameof(onSound));
    }

    public bool IsAvailable => true;

    public int PlaySoundLocal(string wavName, float volume = 1.0f, bool looping = false)
    {
        if (string.IsNullOrWhiteSpace(wavName))
            return -1;
        if (!looping)
            _onSound(wavName, Vector3.Zero, volume, false);
        return _nextChannelId++;
    }

    public int PlaySoundPos(
        string wavName,
        Vector3 position,
        float volume = 1.0f,
        float minRadius = 0.5f,
        float maxRadius = 8.0f,
        bool looping = false)
    {
        if (string.IsNullOrWhiteSpace(wavName))
            return -1;
        if (!looping)
            _onSound(wavName, position, volume, true);
        return _nextChannelId++;
    }

    public void StopSound(int channelId, float fadeOut = 0f) { }
    public void ChangeSoundPitch(int channelId, float pitch, float changeTime = 0f) { }
    public void ChangeSoundVol(int channelId, float volume, float fadeTime = 0f) { }
    public void SetMusicVolume(float volume) { }
    public void SetSectorAmbient(string? wavName, float volume = 0.5f) { }
    public float GetSoundDuration(string wavName) => 0f;
}
