using System.Collections.Generic;
using OpenSith.Core.Simulation;

namespace OpenSith.Client;

/// <summary>
/// Client rendering bridge extensions adapting headless <see cref="WorldSimulation"/>
/// animation snapshots to <see cref="ThingMeshRenderer"/>.
/// </summary>
public static class WorldSimulationClientExtensions
{
    /// <summary>
    /// Synchronizes all NPC animation states and movement distances with <see cref="ThingMeshRenderer"/>.
    /// </summary>
    public static void SyncThingAnimations(this WorldSimulation sim, ThingMeshRenderer renderer)
    {
        var snapshots = new List<ThingAnimationSnapshot>();
        sim.GetThingAnimationSnapshots(snapshots);
        foreach (var snap in snapshots)
        {
            renderer.SetThingAnimation(snap.ThingIndex, snap.Action, snap.Mode, snap.Loop);
            renderer.SetThingDistanceMoved(snap.ThingIndex, snap.DistanceMoved);
        }
    }

    /// <summary>
    /// Applies all NPC animation states and movement distances to <see cref="ThingMeshRenderer"/>.
    /// </summary>
    public static void ApplyThingAnimationState(this WorldSimulation sim, ThingMeshRenderer renderer) =>
        sim.SyncThingAnimations(renderer);
}
