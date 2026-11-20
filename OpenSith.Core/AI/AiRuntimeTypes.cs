using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenSith.Core.AI;

/// <summary>
/// Represents a perceivable entity/actor candidate in the game world.
/// </summary>
public sealed class AiTargetCandidate
{
    /// <summary>
    /// World thing index of the candidate.
    /// </summary>
    public int ThingIndex { get; init; }

    /// <summary>
    /// Current world position of the candidate.
    /// </summary>
    public Vector3 Position { get; init; }

    /// <summary>
    /// Alignment rating (-1.0 for hostiles, +1.0 for friendlies/player).
    /// </summary>
    public float Alignment { get; init; } = 1.0f;

    /// <summary>
    /// Whether the target is currently alive.
    /// </summary>
    public bool IsAlive { get; init; } = true;

    /// <summary>
    /// Whether the target is visible (not cloaked/invisible).
    /// </summary>
    public bool IsVisible { get; init; } = true;

    public AiTargetCandidate() { }

    public AiTargetCandidate(int thingIndex, Vector3 position, float alignment = 1.0f, bool isAlive = true, bool isVisible = true)
    {
        ThingIndex = thingIndex;
        Position = position;
        Alignment = alignment;
        IsAlive = isAlive;
        IsVisible = isVisible;
    }
}

/// <summary>
/// Represents a sound emission event in the game world.
/// </summary>
public sealed class AiSoundEvent
{
    /// <summary>
    /// World position where the sound originated.
    /// </summary>
    public Vector3 Position { get; init; }

    /// <summary>
    /// Additional sound radius added to the AI's HeardDistance.
    /// </summary>
    public float SoundRadius { get; init; }

    /// <summary>
    /// Thing index of the sound source, or -1 if environmental.
    /// </summary>
    public int SourceThingIndex { get; init; } = -1;

    public AiSoundEvent() { }

    public AiSoundEvent(Vector3 position, float soundRadius = 0.0f, int sourceThingIndex = -1)
    {
        Position = position;
        SoundRadius = soundRadius;
        SourceThingIndex = sourceThingIndex;
    }
}

/// <summary>
/// World context provided to <see cref="AiBrain.Update"/> during each AI simulation tick.
/// </summary>
public sealed class AiWorldContext
{
    /// <summary>
    /// Current world position of the AI actor.
    /// </summary>
    public Vector3 SelfPosition { get; init; } = Vector3.Zero;

    /// <summary>
    /// Current facing/forward look direction of the AI actor.
    /// </summary>
    public Vector3 SelfLookDir { get; init; } = Vector3.UnitZ;

    /// <summary>
    /// Current health of the AI actor. Defaults to 100.
    /// </summary>
    public float Health { get; init; } = 100.0f;

    /// <summary>
    /// Maximum health of the AI actor. Defaults to 100.
    /// </summary>
    public float MaxHealth { get; init; } = 100.0f;

    /// <summary>
    /// Whether the AI actor took damage during this frame or recently.
    /// </summary>
    public bool WasDamaged { get; init; }

    /// <summary>
    /// Thing index of the entity that inflicted damage, or null if unknown.
    /// </summary>
    public int? DamagedByThingIndex { get; init; }

    /// <summary>
    /// Position from which the damage originated.
    /// </summary>
    public Vector3? DamageSourcePosition { get; init; }

    /// <summary>
    /// List of potential target entities in the world.
    /// </summary>
    public IReadOnlyList<AiTargetCandidate> Targets { get; init; } = Array.Empty<AiTargetCandidate>();

    /// <summary>
    /// List of sound events occurring in the world this tick.
    /// </summary>
    public IReadOnlyList<AiSoundEvent> Sounds { get; init; } = Array.Empty<AiSoundEvent>();

    /// <summary>
    /// Optional line-of-sight obstruction test callback (fromPos, toPos -> true if unblocked).
    /// </summary>
    public Func<Vector3, Vector3, bool>? LineOfSightCheck { get; init; }

    /// <summary>
    /// Direct target position override for testing or scripted targeting.
    /// </summary>
    public Vector3? DirectTargetPosition { get; init; }

    /// <summary>
    /// Direct target thing index override.
    /// </summary>
    public int? DirectTargetIndex { get; init; }
}

/// <summary>
/// The decision output produced by <see cref="AiBrain.Update"/> on each tick.
/// </summary>
public sealed class AiDecision
{
    /// <summary>
    /// Desired movement vector (direction and magnitude, 0 if stationary).
    /// </summary>
    public Vector3 DesiredMovementDirection { get; init; } = Vector3.Zero;

    /// <summary>
    /// Point in world space the AI desires to face or look towards.
    /// </summary>
    public Vector3 DesiredLookTarget { get; init; } = Vector3.Zero;

    /// <summary>
    /// Whether the AI desires to fire primary weapon this frame.
    /// </summary>
    public bool ShouldFire { get; init; }

    /// <summary>
    /// Whether the AI is currently fleeing from a threat.
    /// </summary>
    public bool ShouldFlee { get; init; }

    /// <summary>
    /// The updated AI mode flags for this actor.
    /// </summary>
    public AiMode NewMode { get; init; }

    /// <summary>
    /// Thing index of the current target (-1 if no target).
    /// </summary>
    public int TargetThingIndex { get; init; } = -1;

    /// <summary>
    /// Whether the AI mode changed during this update tick.
    /// </summary>
    public bool ModeChanged { get; init; }

    /// <summary>
    /// Triggered event flags during this update tick.
    /// </summary>
    public AiEventType TriggeredEvents { get; init; } = AiEventType.None;

    /// <summary>
    /// Convenience property: true if the AI has non-zero desired movement.
    /// </summary>
    public bool HasMovement => DesiredMovementDirection.LengthSquared() > 1e-6f;
}
