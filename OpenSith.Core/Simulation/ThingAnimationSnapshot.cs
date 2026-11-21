namespace OpenSith.Core.Simulation;

/// <summary>
/// Read-only per-frame NPC/entity skeletal animation state snapshot produced by
/// <see cref="WorldSimulation.GetThingAnimationSnapshots"/> for client renderers.
/// </summary>
public readonly record struct ThingAnimationSnapshot(
    int ThingIndex,
    string Action,
    int Mode,
    bool Loop,
    float DistanceMoved);
