using System;
using System.Collections.Generic;

namespace OpenSith.Core.AI;

/// <summary>
/// Represents a Sith Engine AI Class definition loaded from a .AI file.
/// Contains actor behavior parameters (alignment, rank, sight/hearing distances, FOV, accuracy)
/// and a collection of behavioral instincts.
/// </summary>
public sealed class AiClass
{
    private float _fovDegrees = 180f;
    private float _fov = 0f;

    /// <summary>
    /// Index of this AI class within the world/level class table.
    /// </summary>
    public int Index { get; set; } = -1;

    /// <summary>
    /// Name or relative path of this AI class (e.g. "stdefault.ai").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Alignment rating (-1.0 for hostiles, +1.0 for friendlies). Defaults to 0.0.
    /// </summary>
    public float Alignment { get; set; } = 0.0f;

    /// <summary>
    /// Rank value of the AI actor. Defaults to 0.0.
    /// </summary>
    public float Rank { get; set; } = 0.0f;

    /// <summary>
    /// Maximum step height the AI can traverse (defaults to 0.5).
    /// </summary>
    public float MaxStep { get; set; } = 0.5f;

    /// <summary>
    /// Sight detection distance in game units (defaults to 20.0).
    /// </summary>
    public float SightDistance { get; set; } = 20.0f;

    /// <summary>
    /// Hearing detection distance in game units (defaults to 10.0).
    /// </summary>
    public float HeardDistance { get; set; } = 10.0f;

    /// <summary>
    /// Distance at which a sleeping AI actor wakes up (defaults to 0.0).
    /// </summary>
    public float WakeupDistance { get; set; } = 0.0f;

    /// <summary>
    /// Shooting accuracy ratio from 0.0 to 1.0 (defaults to 0.5).
    /// </summary>
    public float Accuracy { get; set; } = 0.5f;

    /// <summary>
    /// Field of view angle in degrees (defaults to 180).
    /// Setting this automatically recomputes <see cref="Fov"/> (cos(half angle)).
    /// </summary>
    public float FovDegrees
    {
        get => _fovDegrees;
        set
        {
            _fovDegrees = value;
            _fov = ComputeFovCos(value);
        }
    }

    /// <summary>
    /// Cosine of half the FOV angle, matching Sith Engine's internal representation.
    /// Used directly for dot-product cone checks (defaults to 0.0 = 180 degrees).
    /// </summary>
    public float Fov
    {
        get => _fov;
        set => _fov = value;
    }

    /// <summary>
    /// Ordered list of instincts configured for this AI class.
    /// </summary>
    public List<AiInstinct> Instincts { get; } = new();

    /// <summary>
    /// Parsed Saberfighting instinct configuration, if present.
    /// </summary>
    public AiSaberfightingConfig? Saberfighting { get; set; }

    /// <summary>
    /// Parsed ForcePowers instinct configuration, if present.
    /// </summary>
    public AiForcePowerConfig? ForcePowers { get; set; }

    /// <summary>
    /// Parsed SpecialAttack instinct configuration, if present.
    /// </summary>
    public AiSpecialAttackConfig? SpecialAttack { get; set; }

    /// <summary>
    /// Parsed CircleStrafe instinct configuration, if present.
    /// </summary>
    public AiCircleStrafeConfig? CircleStrafe { get; set; }

    /// <summary>
    /// Parsed Roam instinct configuration, if present.
    /// </summary>
    public AiRoamConfig? RoamConfig { get; set; }

    /// <summary>
    /// Interval in seconds between Force power checks (from ForcePowers instinct).
    /// </summary>
    public float ForceCheckIntervalSec => ForcePowers?.IntervalSec ?? 0f;

    /// <summary>
    /// Whether this AI class has the Saberfighting instinct.
    /// </summary>
    public bool HasSaberfighting => Saberfighting != null || HasInstinct("Saberfighting");

    /// <summary>
    /// Whether this AI class has the ForcePowers instinct.
    /// </summary>
    public bool HasForcePowers => ForcePowers != null || HasInstinct("ForcePowers");

    /// <summary>
    /// Whether this AI class has the SpecialAttack instinct.
    /// </summary>
    public bool HasSpecialAttack => SpecialAttack != null || HasInstinct("SpecialAttack");

    /// <summary>
    /// Whether this AI class has the CircleStrafe instinct.
    /// </summary>
    public bool HasCircleStrafe => CircleStrafe != null || HasInstinct("CircleStrafe");

    /// <summary>
    /// Whether this AI class has the Roam instinct.
    /// </summary>
    public bool HasRoam => RoamConfig != null || HasInstinct("Roam");

    /// <summary>
    /// Computes the cosine of half the specified FOV angle in degrees, matching Sith Engine conventions.
    /// </summary>
    public static float ComputeFovCos(float degrees)
    {
        if (MathF.Abs(degrees - 180f) < 1e-4f) return 0f;
        if (MathF.Abs(degrees - 360f) < 1e-4f) return -1f;
        if (MathF.Abs(degrees) < 1e-4f) return 1f;
        return MathF.Cos(degrees * 0.5f * (MathF.PI / 180.0f));
    }

    /// <summary>
    /// Finds the first instinct with the specified name (case-insensitive).
    /// </summary>
    public AiInstinct? FindInstinct(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Instincts.Find(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks whether an instinct with the specified name exists (case-insensitive).
    /// </summary>
    public bool HasInstinct(string name) => FindInstinct(name) != null;

    /// <summary>
    /// Gets all instincts matching the specified name (case-insensitive).
    /// </summary>
    public IEnumerable<AiInstinct> GetInstincts(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Instincts.FindAll(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() => $"AiClass '{Name}' (Align={Alignment:F2}, Rank={Rank:F2}, Instincts={Instincts.Count})";
}

/// <summary>
/// Configuration parsed from the <c>Saberfighting</c> instinct (Dark Jedi melee attacks).
/// </summary>
public sealed class AiSaberfightingConfig
{
    /// <summary>Probability [0..1] of skipping a swing opportunity (p0).</summary>
    public float SkipChance { get; init; }

    /// <summary>Range of primary saber attack 1 in world units (p1).</summary>
    public float Att1Range { get; init; }

    /// <summary>Damage of primary saber attack 1 (p2).</summary>
    public float Att1Damage { get; init; }

    /// <summary>Cooldown/duration of saber attack 1 in milliseconds (p3).</summary>
    public float Att1CooldownMs { get; init; }

    /// <summary>Range of saber attack 2 in world units (p4).</summary>
    public float Att2Range { get; init; }

    /// <summary>Damage of saber attack 2 (p5).</summary>
    public float Att2Damage { get; init; }

    /// <summary>Cooldown/duration of saber attack 2 in milliseconds (p6).</summary>
    public float Att2CooldownMs { get; init; }

    /// <summary>Optional range of saber attack 3 in world units (p7).</summary>
    public float Att3Range { get; init; }

    /// <summary>Optional damage of saber attack 3 (p8).</summary>
    public float Att3Damage { get; init; }

    /// <summary>Optional cooldown/duration of saber attack 3 in milliseconds (p9).</summary>
    public float Att3CooldownMs { get; init; }

    /// <summary>Optional range of saber attack 4 in world units (p10).</summary>
    public float Att4Range { get; init; }

    /// <summary>Optional damage of saber attack 4 (p11).</summary>
    public float Att4Damage { get; init; }

    /// <summary>Optional cooldown/duration of saber attack 4 in milliseconds (p12).</summary>
    public float Att4CooldownMs { get; init; }

    public float Att1CooldownSec => Att1CooldownMs / 1000.0f;
    public float Att2CooldownSec => Att2CooldownMs / 1000.0f;
}

/// <summary>
/// Parameters for a single Force power slot in <see cref="AiForcePowerConfig"/>.
/// </summary>
public sealed class AiForcePowerSlot
{
    public float MinDist { get; init; }
    public float MaxDist { get; init; }
    public float Probability { get; init; }
    public float CooldownMs { get; init; }
    public float CooldownSec => CooldownMs / 1000.0f;
}

/// <summary>
/// Configuration parsed from the <c>ForcePowers</c> instinct (Dark Jedi Force abilities).
/// </summary>
public sealed class AiForcePowerConfig
{
    /// <summary>Interval between Force power evaluation checks in milliseconds (p0).</summary>
    public float IntervalMs { get; init; }

    public float IntervalSec => IntervalMs / 1000.0f;

    public AiForcePowerSlot Power1 { get; init; } = new();
    public AiForcePowerSlot Power2 { get; init; } = new();
    public AiForcePowerSlot Power3 { get; init; } = new();

    /// <summary>
    /// Gets the Force power slot by 1-based index (1, 2, or 3).
    /// </summary>
    public AiForcePowerSlot GetPower(int oneBasedIndex) => oneBasedIndex switch
    {
        1 => Power1,
        2 => Power2,
        3 => Power3,
        _ => throw new ArgumentOutOfRangeException(nameof(oneBasedIndex), "Force power index must be 1, 2, or 3.")
    };
}

/// <summary>
/// Configuration parsed from the <c>SpecialAttack</c> instinct (e.g. Yun's YUflip.key lunge).
/// </summary>
public sealed class AiSpecialAttackConfig
{
    public float IntervalMs { get; init; }
    public float Probability { get; init; }
    public float MinDist { get; init; }
    public float MaxDist { get; init; }
    public float DurationMs { get; init; }
    public int AnimMode { get; init; }
    public float Reserved { get; init; }
    public float Damage { get; init; }
    public float CooldownMs { get; init; }

    public float IntervalSec => IntervalMs / 1000.0f;
    public float DurationSec => DurationMs / 1000.0f;
    public float CooldownSec => CooldownMs / 1000.0f;
}

/// <summary>
/// Configuration parsed from the <c>CircleStrafe</c> instinct.
/// </summary>
public sealed class AiCircleStrafeConfig
{
    public float IntervalMs { get; init; }
    public float AngleDeg { get; init; }
    public float Speed { get; init; }
    public float DurationMs { get; init; }
    public bool RandomStrafe { get; init; }

    public float IntervalSec => IntervalMs / 1000.0f;
    public float DurationSec => DurationMs / 1000.0f;
}

/// <summary>
/// Configuration parsed from the <c>Roam</c> instinct (idle wandering around home position).
/// </summary>
public sealed class AiRoamConfig
{
    /// <summary>Interval between roam direction changes in milliseconds (p0).</summary>
    public float IntervalMs { get; init; }

    /// <summary>Maximum roam distance/radius from home position in world units (p1).</summary>
    public float MaxDist { get; init; }

    public float IntervalSec => IntervalMs / 1000.0f;
}
