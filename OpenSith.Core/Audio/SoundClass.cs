using System;
using System.Collections.Generic;

namespace OpenSith.Core.Audio;

/// <summary>
/// Represents a Sith Engine Sound Class (.snd) consisting of foley sound entries
/// grouped by event type (e.g., footsteps, impacts, vocals, doors/elevators).
/// </summary>
public sealed class SoundClass
{
    private readonly Dictionary<SoundEvent, List<SoundClassEntry>> _byEvent = new();
    private readonly Dictionary<string, List<SoundClassEntry>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SoundClassEntry> _allEntries = new();

    /// <summary>The name or path identifier of this sound class (e.g. "ky.snd" or "misc/snd/ky.snd").</summary>
    public string Name { get; init; }

    /// <summary>Total number of sound entries across all events.</summary>
    public int EntryCount => _allEntries.Count;

    /// <summary>All sound entries defined in this sound class.</summary>
    public IReadOnlyList<SoundClassEntry> AllEntries => _allEntries;

    public SoundClass(string name = "")
    {
        Name = name;
    }

    /// <summary>Adds a sound entry to this sound class.</summary>
    public void AddEntry(SoundClassEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _allEntries.Add(entry);

        if (!_byEvent.TryGetValue(entry.Event, out var eventList))
        {
            eventList = new List<SoundClassEntry>();
            _byEvent[entry.Event] = eventList;
        }
        eventList.Add(entry);

        if (!_byName.TryGetValue(entry.EventName, out var nameList))
        {
            nameList = new List<SoundClassEntry>();
            _byName[entry.EventName] = nameList;
        }
        nameList.Add(entry);
    }

    /// <summary>Gets all entries associated with the given event.</summary>
    public IReadOnlyList<SoundClassEntry> this[SoundEvent evt] =>
        _byEvent.TryGetValue(evt, out var list) ? list : Array.Empty<SoundClassEntry>();

    /// <summary>Gets all entries associated with the given event name.</summary>
    public IReadOnlyList<SoundClassEntry> this[string eventName] =>
        _byName.TryGetValue(eventName, out var list) ? list : Array.Empty<SoundClassEntry>();

    /// <summary>Returns true if one or more entries are mapped to this event.</summary>
    public bool HasEvent(SoundEvent evt) => _byEvent.ContainsKey(evt);

    /// <summary>Returns true if one or more entries are mapped to this event name.</summary>
    public bool HasEvent(string eventName) => _byName.ContainsKey(eventName);

    /// <summary>Gets the first entry mapped to the given event, or null if none.</summary>
    public SoundClassEntry? GetFirst(SoundEvent evt) =>
        _byEvent.TryGetValue(evt, out var list) && list.Count > 0 ? list[0] : null;

    /// <summary>Gets the first entry mapped to the given event name, or null if none.</summary>
    public SoundClassEntry? GetFirst(string eventName) =>
        _byName.TryGetValue(eventName, out var list) && list.Count > 0 ? list[0] : null;

    /// <summary>
    /// Selects an entry using a normalized 0.0-1.0 ratio (as in Sith Engine sithSoundClass_PlayMode).
    /// Used for selecting pain/hit sound variation based on damage ratio.
    /// </summary>
    public SoundClassEntry? Select(SoundEvent evt, float selectionRatio)
    {
        if (!_byEvent.TryGetValue(evt, out var list) || list.Count == 0) return null;
        if (list.Count == 1) return list[0];

        int index = (int)(list.Count * Math.Clamp(selectionRatio, 0f, 1f));
        if (index >= list.Count) index = list.Count - 1;
        return list[index];
    }

    /// <summary>
    /// Selects an entry using a normalized 0.0-1.0 ratio by event name.
    /// </summary>
    public SoundClassEntry? Select(string eventName, float selectionRatio)
    {
        if (!_byName.TryGetValue(eventName, out var list) || list.Count == 0) return null;
        if (list.Count == 1) return list[0];

        int index = (int)(list.Count * Math.Clamp(selectionRatio, 0f, 1f));
        if (index >= list.Count) index = list.Count - 1;
        return list[index];
    }

    /// <summary>
    /// Selects an entry randomly among variations (as in Sith Engine sithSoundClass_PlayModeRandom).
    /// </summary>
    public SoundClassEntry? SelectRandom(SoundEvent evt, Random? random = null)
    {
        if (!_byEvent.TryGetValue(evt, out var list) || list.Count == 0) return null;
        if (list.Count == 1) return list[0];

        var rng = random ?? Random.Shared;
        return list[rng.Next(list.Count)];
    }

    /// <summary>
    /// Selects an entry randomly among variations by event name.
    /// </summary>
    public SoundClassEntry? SelectRandom(string eventName, Random? random = null)
    {
        if (!_byName.TryGetValue(eventName, out var list) || list.Count == 0) return null;
        if (list.Count == 1) return list[0];

        var rng = random ?? Random.Shared;
        return list[rng.Next(list.Count)];
    }
}
