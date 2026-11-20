using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using OpenSith.Core.World;

namespace OpenSith.Core.Cog;

/// <summary>
/// Runtime instance of a loaded Jedi Knight COG script with its own symbol table,
/// timers, and suspended coroutine continuation state.
/// </summary>
public sealed class CogInstance
{
    private readonly Dictionary<string, CogValue> _extraVariables = new(StringComparer.OrdinalIgnoreCase);

    public int CogId { get; set; } = -1;
    public CogScript Script { get; }
    public CogProgram Program { get; }
    public CogValue[] SymbolValues { get; }
    public Dictionary<string, int> SymbolSlots { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Timers and pulses
    public float TimerTimeLeft { get; set; } = -1f;
    public bool TimerActive { get; set; }

    public float PulseInterval { get; set; }
    public float PulseTimeLeft { get; set; }
    public bool PulseActive { get; set; }

    // Suspended coroutine continuation state
    public bool IsSleeping { get; set; }
    public float SleepTimeLeft { get; set; }
    public int WaitingForStopThingId { get; set; } = -1;
    public CogExecutionContext? SuspendedContext { get; set; }

    public CogInstance(CogScript script, int cogId = -1)
    {
        Script = script ?? throw new ArgumentNullException(nameof(script));
        CogId = cogId;
        SymbolValues = new CogValue[script.Symbols.Count];

        for (int i = 0; i < script.Symbols.Count; i++)
        {
            var sym = script.Symbols[i];
            SymbolSlots[sym.Name] = i;
            SymbolValues[i] = ParseSymbolDefault(sym);
        }

        Program = CogCompiler.Compile(script);
    }

    /// <summary>
    /// Binds positional arguments from a level's JklCogPlacement to non-local non-message symbols in declaration order.
    /// </summary>
    public void BindPlacementArgs(JklCogPlacement placement)
    {
        if (placement == null || placement.SymbolArgs.Count == 0) return;

        int argIdx = 0;
        for (int i = 0; i < Script.Symbols.Count && argIdx < placement.SymbolArgs.Count; i++)
        {
            var sym = Script.Symbols[i];
            if (!sym.IsLocal && sym.Type != CogSymbolType.Message)
            {
                string rawArg = placement.SymbolArgs[argIdx++];
                if (rawArg == "-" || string.IsNullOrWhiteSpace(rawArg))
                {
                    if (sym.DefaultValue != null)
                        SymbolValues[i] = ParseValue(sym.Type, sym.DefaultValue);
                }
                else
                {
                    SymbolValues[i] = ParseValue(sym.Type, rawArg);
                }
            }
        }
    }

    public CogValue GetSymbolValue(string name)
    {
        if (SymbolSlots.TryGetValue(name, out int slot))
            return SymbolValues[slot];
        if (_extraVariables.TryGetValue(name, out var val))
            return val;
        return CogValue.Null;
    }

    public void SetSymbolValue(string name, CogValue value)
    {
        if (SymbolSlots.TryGetValue(name, out int slot))
            SymbolValues[slot] = value;
        else
            _extraVariables[name] = value;
    }

    /// <summary>
    /// Consecutive array indexing: resolving door0[i] to symbol slot (base + i).
    /// </summary>
    public CogValue GetArrayElement(string baseName, int index)
    {
        if (SymbolSlots.TryGetValue(baseName, out int baseSlot))
        {
            int target = baseSlot + index;
            if (target >= 0 && target < SymbolValues.Length)
                return SymbolValues[target];
        }
        return CogValue.Null;
    }

    public void SetArrayElement(string baseName, int index, CogValue value)
    {
        if (SymbolSlots.TryGetValue(baseName, out int baseSlot))
        {
            int target = baseSlot + index;
            if (target >= 0 && target < SymbolValues.Length)
                SymbolValues[target] = value;
        }
    }

    /// <summary>
    /// Gets the event mask for a linked symbol of the given type and world reference ID,
    /// or <c>0x401</c> if no matching symbol is found.
    /// </summary>
    public int GetSymbolMask(CogSymbolType type, int refId)
    {
        for (int i = 0; i < Script.Symbols.Count && i < SymbolValues.Length; i++)
        {
            var sym = Script.Symbols[i];
            if (sym.Type == type &&
                SymbolValues[i].Type == CogValueType.Int &&
                SymbolValues[i].AsInt == refId)
            {
                return sym.Mask;
            }
        }
        return 0x401;
    }

    /// <summary>
    /// Returns true if any linked symbol matching <paramref name="type"/> and <paramref name="refId"/>
    /// permits an event from <paramref name="sourceThingTypeMask"/> (e.g. <c>1 &lt;&lt; thingType</c>, <c>0x400</c> for player).
    /// </summary>
    public bool MatchesSymbolMask(CogSymbolType type, int refId, int sourceThingTypeMask = 0x400)
    {
        bool foundAny = false;
        for (int i = 0; i < Script.Symbols.Count && i < SymbolValues.Length; i++)
        {
            var sym = Script.Symbols[i];
            if (sym.Type == type &&
                SymbolValues[i].Type == CogValueType.Int &&
                SymbolValues[i].AsInt == refId)
            {
                foundAny = true;
                if ((sym.Mask & sourceThingTypeMask) != 0)
                    return true;
            }
        }
        return !foundAny && (0x401 & sourceThingTypeMask) != 0;
    }

    private static CogValue ParseSymbolDefault(CogSymbol sym)
    {
        if (sym.DefaultValue != null)
            return ParseValue(sym.Type, sym.DefaultValue);

        return sym.Type switch
        {
            CogSymbolType.Int => new CogValue(0),
            CogSymbolType.Thing or CogSymbolType.Sector or CogSymbolType.Surface or CogSymbolType.Cog or CogSymbolType.Template => new CogValue(-1),
            CogSymbolType.Float or CogSymbolType.Flex => new CogValue(0f),
            CogSymbolType.Vector => new CogValue(Vector3.Zero),
            CogSymbolType.Sound or CogSymbolType.Material or CogSymbolType.Model or CogSymbolType.Keyframe or CogSymbolType.Ai => new CogValue(string.Empty),
            _ => CogValue.Null
        };
    }

    private static CogValue ParseValue(CogSymbolType type, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "-")
        {
            return type switch
            {
                CogSymbolType.Int => new CogValue(0),
                CogSymbolType.Float or CogSymbolType.Flex => new CogValue(0f),
                CogSymbolType.Vector => new CogValue(Vector3.Zero),
                CogSymbolType.Sound or CogSymbolType.Material or CogSymbolType.Model or CogSymbolType.Keyframe or CogSymbolType.Ai => new CogValue(string.Empty),
                _ => new CogValue(-1)
            };
        }

        switch (type)
        {
            case CogSymbolType.Float:
            case CogSymbolType.Flex:
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    return new CogValue(f);
                return new CogValue(0f);

            case CogSymbolType.Int:
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                    return new CogValue(iv);
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float fiv))
                    return new CogValue((int)fiv);
                return new CogValue(0);

            case CogSymbolType.Thing:
            case CogSymbolType.Sector:
            case CogSymbolType.Surface:
            case CogSymbolType.Cog:
            case CogSymbolType.Template:
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    return new CogValue(i);
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float fi))
                    return new CogValue((int)fi);
                return new CogValue(-1);

            case CogSymbolType.Vector:
                string vStr = raw.Trim('(', ')', '\'', '"');
                var parts = vStr.Split([' ', ',', '/'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float vx) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float vy) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float vz))
                {
                    return new CogValue(new Vector3(vx, vy, vz));
                }
                return new CogValue(Vector3.Zero);

            default:
                return new CogValue(raw);
        }
    }
}
