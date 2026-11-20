using System;
using System.Collections.Generic;

namespace OpenSith.Core.AI;

/// <summary>
/// Bit flags representing actor AI states/modes in the Sith Engine.
/// </summary>
[Flags]
public enum AiMode : uint
{
    None = 0,
    Moving = 0x01,
    Attacking = 0x02,
    Searching = 0x04,
    Turning = 0x08,
    HasDest = 0x10,
    ToughSkin = 0x20,
    NoCheckForCliff = 0x40,
    Unk80 = 0x80,
    Unk100 = 0x100,
    Active = 0x200,
    TargetVisible = 0x400,
    Fleeing = 0x800,
    Sleeping = 0x1000,
    Disabled = 0x2000,
    WantAllEvents = 0x10000,
    WallCrawling = 0x200000,
    NoChasing = 0x2000000,
    ArmoredSkin = 0x8000000,
}

/// <summary>
/// Bit flags representing AI event types in the Sith Engine.
/// </summary>
[Flags]
public enum AiEventType : uint
{
    None = 0,
    HitSector = 0x1,
    Sound = 0x2,
    Touched = 0x4,
    Event8 = 0x8,
    Event10 = 0x10,
    Event20 = 0x20,
    MoveTargetChanged = 0x40,
    Event80 = 0x80,
    ModeChanged = 0x100,
    HitWall = 0x200,
    HitCliff = 0x400,
    GoalReached = 0x800,
    Targeted = 0x1000,
    GoalSet = 0x2000,
    Fire = 0x4000,
    HitThing = 0x8000,
    PreFire = 0x10000,
    PostFire = 0x20000,
    Event40000 = 0x40000,
    GoalUnreachable = 0x80000,
}

/// <summary>
/// Represents an AI instinct entry in a Sith Engine AI class definition.
/// An instinct defines a specific behavior (e.g. LookForTarget, PrimaryFire, Follow, Jump)
/// along with mode masks and up to 16 floating-point/integer arguments.
/// </summary>
public sealed class AiInstinct
{
    public const int MaxArguments = 16;

    private static readonly Dictionary<string, (int Param1, int Param2, int Param3)> RegisteredInstincts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["listen"] = (0, 0, (int)(AiMode.Searching | AiMode.Attacking | AiMode.Moving)),
        ["lookfortarget"] = ((int)(AiMode.Active | AiMode.Searching), 0, 0),
        ["lookforopposingtarget"] = ((int)(AiMode.Active | AiMode.Searching), 0, 0),
        ["primaryfire"] = ((int)AiMode.Attacking, 0, (int)AiMode.Unk100),
        ["leap"] = ((int)AiMode.Attacking, 0, (int)(AiMode.Unk100 | AiMode.Searching)),
        ["charge"] = ((int)AiMode.Attacking, 0, (int)(AiMode.Unk100 | AiMode.Searching)),
        ["follow"] = ((int)AiMode.Attacking, (int)AiMode.Fleeing, (int)(AiMode.Fleeing | AiMode.TargetVisible | AiMode.Active)),
        ["turretfire"] = ((int)AiMode.Attacking, (int)AiMode.Fleeing, (int)AiMode.Unk100),
        ["opendoors"] = ((int)AiMode.Attacking, 0, 0),
        ["jump"] = (0, 0, (int)(AiMode.TargetVisible | AiMode.Active | AiMode.Searching)),
        ["randomturn"] = ((int)AiMode.Searching, 0, 0),
        ["roam"] = ((int)AiMode.Searching, 0, 0),
        ["flee"] = ((int)AiMode.Fleeing, 0, (int)(AiMode.Fleeing | AiMode.TargetVisible | AiMode.Active | AiMode.Unk100 | AiMode.Searching | AiMode.Moving)),
        ["sensedanger"] = ((int)AiMode.Searching, (int)AiMode.Fleeing, (int)(AiMode.Searching | AiMode.Attacking | AiMode.Moving)),
        ["hitandrun"] = ((int)(AiMode.Fleeing | AiMode.TargetVisible), 0, 0),
        ["retreat"] = ((int)AiMode.Attacking, (int)AiMode.Fleeing, 0),
        ["circlestrafe"] = ((int)AiMode.Attacking, (int)AiMode.Fleeing, 0),
        ["blindfire"] = ((int)AiMode.Attacking, (int)(AiMode.Fleeing | AiMode.TargetVisible), 0),
        ["returnhome"] = (0, 0, (int)(AiMode.Fleeing | AiMode.Unk100)),
        ["lobfire"] = ((int)AiMode.Attacking, 0, (int)AiMode.Unk100),
        ["talk"] = (0xFFFF, 0, 0),
        ["crouch"] = ((int)AiMode.Attacking, 0, (int)AiMode.Unk100),
        ["withdraw"] = ((int)AiMode.Fleeing, 0, (int)(AiMode.Fleeing | AiMode.TargetVisible | AiMode.Active | AiMode.Unk100 | AiMode.Searching | AiMode.Moving)),
        ["dodge"] = (0, 0, (int)(AiMode.Sleeping | AiMode.Attacking | AiMode.Moving)),
        ["saberfighting"] = (2, 0, 0),
        ["forcepowers"] = (2, 0, 0),
        ["sabermove"] = (2, 0, 0),
        ["specialattack"] = (2, 0, 4),
        ["touchofdeath"] = (6, 0, (int)AiEventType.Touched),
    };

    /// <summary>
    /// Gets the name of the instinct (e.g. "LookForTarget", "PrimaryFire", "Follow").
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Allowed AI modes (update modes) for this instinct to run (Param 1).
    /// </summary>
    public int Param1 { get; init; }

    /// <summary>
    /// Disallowed AI modes (block modes) that prevent this instinct from running (Param 2).
    /// </summary>
    public int Param2 { get; init; }

    /// <summary>
    /// Trigger events or modes for this instinct (Param 3).
    /// </summary>
    public int Param3 { get; init; }

    /// <summary>
    /// Number of arguments explicitly provided in the .ai file (0 to 16).
    /// </summary>
    public int ArgCount { get; init; }

    /// <summary>
    /// Array of 16 float arguments, matching Sith Engine's fltArg.
    /// Unspecified arguments default to 0.0f.
    /// </summary>
    public float[] FloatArgs { get; } = new float[MaxArguments];

    /// <summary>
    /// Array of 16 integer arguments, matching Sith Engine's intArg.
    /// Unspecified arguments default to 0.
    /// </summary>
    public int[] IntArgs { get; } = new int[MaxArguments];

    public AiInstinct() { }

    public AiInstinct(string name, ReadOnlySpan<float> args, int param1 = 0, int param2 = 0, int param3 = 0)
    {
        Name = name;
        Param1 = param1;
        Param2 = param2;
        Param3 = param3;
        ArgCount = Math.Min(args.Length, MaxArguments);
        for (int i = 0; i < ArgCount; i++)
        {
            FloatArgs[i] = args[i];
            IntArgs[i] = (int)args[i];
        }
    }

    /// <summary>
    /// Retrieves default parameter masks (Param1, Param2, Param3) for a registered Sith Engine instinct.
    /// </summary>
    public static bool TryGetDefaultParams(string name, out int param1, out int param2, out int param3)
    {
        if (RegisteredInstincts.TryGetValue(name, out var p))
        {
            param1 = p.Param1;
            param2 = p.Param2;
            param3 = p.Param3;
            return true;
        }

        param1 = 0;
        param2 = 0;
        param3 = 0;
        return false;
    }

    /// <summary>
    /// Gets the float argument at the specified index (0 to 15), or 0 if out of range.
    /// </summary>
    public float GetFloat(int index) => (uint)index < MaxArguments ? FloatArgs[index] : 0f;

    /// <summary>
    /// Gets the integer argument at the specified index (0 to 15), or 0 if out of range.
    /// </summary>
    public int GetInt(int index) => (uint)index < MaxArguments ? IntArgs[index] : 0;

    /// <summary>
    /// Convenience indexer for accessing float arguments.
    /// </summary>
    public float this[int index] => GetFloat(index);

    public override string ToString()
    {
        return ArgCount > 0
            ? $"{Name} ({string.Join(", ", FloatArgs.AsSpan(0, ArgCount).ToArray())})"
            : Name;
    }
}
