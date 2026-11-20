using System.Numerics;

namespace OpenSith.Core.World;

/// <summary>
/// Thing state and behavior bitflags matching OpenJKDF2 <c>SithThingFlag</c> (<c>SITH_TF_*</c> in <c>types_enums.h</c>).
/// </summary>
[Flags]
public enum SithThingFlags : uint
{
    None = 0,
    EmitLight = 0x1,
    Destroyed = 0x2,
    Invisible = 0x4,
    LevelGeo = 0x8,
    Bit10 = 0x10,
    Bit20 = 0x20,
    Standable = 0x40,
    Bit80 = 0x80,
    Invulnerable = 0x100,
    Dead = 0x200,
    Captured = 0x400,
    NoImpactDamage = 0x800,
    NoEasy = 0x1000,
    NoMedium = 0x2000,
    NoHard = 0x4000,
    NoMultiplayer = 0x8000,
    NoSingleplayer = 0x10000,
    PulseSet = 0x20000,
    TimerSet = 0x40000,
    Disabled = 0x80000,
    InCamFov = 0x100000,
    RenderWeapon = 0x200000,
    Metal = 0x400000,
    Earth = 0x800000,
    Bit1000000 = 0x1000000,
    Water = 0x2000000,
    IgnoreGouraudDistance = 0x4000000,
    Drowns = 0x8000000,
    WaterCreature = 0x10000000,
    Splashes = 0x20000000,
    Bit40000000 = 0x40000000,
    Bit80000000 = 0x80000000,
}

/// <summary>
/// A placed thing/entity in the Jedi Knight world (e.g. Player start, doors, elevators, enemies, pickups).
/// </summary>
public sealed class JklThing
{
    public int ThingIndex { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Vector3 Position { get; set; }
    public Vector3 PitchYawRoll { get; set; }
    public int SectorIndex { get; set; }
    public string Model3D { get; set; } = string.Empty;
    public string ExtraArguments { get; set; } = string.Empty;

    /// <summary>
    /// Raw <c>thingflags</c> bitmask parsed from the JKL Things section (see <see cref="SithThingFlags"/>).
    /// </summary>
    public uint ThingFlags { get; set; }

    /// <summary>
    /// Typed view of <see cref="ThingFlags"/>.
    /// </summary>
    public SithThingFlags Flags => (SithThingFlags)ThingFlags;

    /// <summary>
    /// True if <see cref="SithThingFlags.Disabled"/> (<c>0x80000</c>) is set.
    /// </summary>
    public bool IsDisabled => (ThingFlags & (uint)SithThingFlags.Disabled) != 0;

    /// <summary>
    /// Optional <c>numframes</c> parameter from thing arguments (0 if not specified).
    /// </summary>
    public int NumFrames { get; set; }

    /// <summary>
    /// Optional <c>frame=...</c> values from thing arguments.
    /// </summary>
    public List<string> Frames { get; init; } = [];

    /// <summary>
    /// Optional <c>light</c> override from thing arguments.
    /// </summary>
    public float? Light { get; set; }

    /// <summary>
    /// Optional <c>size</c> override from thing arguments.
    /// </summary>
    public float? Size { get; set; }

    /// <summary>
    /// Optional <c>movesize</c> override from thing arguments.
    /// </summary>
    public float? MoveSize { get; set; }

    /// <summary>
    /// When true (e.g. killed NPC corpses or collected/destroyed pickups), player physics skips solid collision.
    /// </summary>
    public bool IsNonSolid { get; set; }

    public bool IsPlayerSpawn => TemplateName.Equals("walkplayer", StringComparison.OrdinalIgnoreCase) ||
                                 TemplateName.Equals("kyle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a thing with the given <paramref name="thingFlags"/> should spawn
    /// for the specified <paramref name="difficulty"/> (0 = Easy, 1 = Medium, 2 = Hard) and mode,
    /// matching OpenJKDF2 <c>sithThing_ReadStaticThingsListText</c> (<c>sithThing.c:1547-1611</c>).
    /// </summary>
    public static bool ShouldSpawnForDifficulty(uint thingFlags, int difficulty, bool isMultiplayer = false)
    {
        int clampedDifficulty = Math.Clamp(difficulty, 0, 2);
        uint rejectMask = (uint)SithThingFlags.NoEasy << clampedDifficulty;
        rejectMask |= isMultiplayer ? (uint)SithThingFlags.NoMultiplayer : (uint)SithThingFlags.NoSingleplayer;
        return (thingFlags & rejectMask) == 0;
    }

    /// <summary>
    /// Instance convenience wrapper for <see cref="ShouldSpawnForDifficulty(uint, int, bool)"/>.
    /// </summary>
    public bool ShouldSpawnForDifficulty(int difficulty, bool isMultiplayer = false) =>
        ShouldSpawnForDifficulty(ThingFlags, difficulty, isMultiplayer);

    public override string ToString() =>
        $"Thing {ThingIndex}: {TemplateName} at {Position} in Sector {SectorIndex}";
}
