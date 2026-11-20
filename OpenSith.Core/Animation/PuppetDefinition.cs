using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OpenSith.Core.Animation;

/// <summary>
/// A single submode entry in a .PUP mode mapping an animation state to a .KEY track and submode flags.
/// </summary>
public readonly record struct PuppetSubmode(string Keyframe, int Flags);

/// <summary>
/// A puppet animation mode mapping states (stand, walk, fire) to .KEY animation tracks and flags.
/// </summary>
public sealed class PuppetMode
{
    public int ModeIndex { get; set; }
    public int BasedOn { get; set; } = -1;
    public Dictionary<string, string> StateToKeyframe { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> StateToFlags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PuppetSubmode> StateToSubmode { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Puppet state machine definition parsed from a LucasArts .PUP file.
/// </summary>
public sealed class PuppetDefinition
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<int, PuppetMode> Modes { get; } = new();
    public Dictionary<int, int> Joints { get; } = new();

    /// <summary>
    /// Gets the keyframe animation filename for a given mode and state (e.g. mode 0, "stand").
    /// Walks the Mode inheritance (basedon) chain if not defined in the current mode.
    /// </summary>
    public string? GetKeyframe(int modeIndex, string state)
    {
        return GetSubmode(modeIndex, state, out var keyframe, out _) ? keyframe : null;
    }

    /// <summary>
    /// Gets the keyframe filename and submode flags for a given mode and state.
    /// Walks the Mode inheritance (basedon) chain and falls back to Mode 0 if not found.
    /// </summary>
    public bool GetSubmode(int modeIndex, string state, out string? keyframe, out int flags)
    {
        int cur = modeIndex;
        int guard = 0;
        while (guard++ < 32 && Modes.TryGetValue(cur, out var mode))
        {
            if (mode.StateToSubmode.TryGetValue(state, out var submode))
            {
                keyframe = submode.Keyframe;
                flags = submode.Flags;
                return true;
            }
            if (mode.StateToKeyframe.TryGetValue(state, out var key))
            {
                keyframe = key;
                flags = mode.StateToFlags.GetValueOrDefault(state, 0);
                return true;
            }
            if (mode.BasedOn < 0 || mode.BasedOn == cur) break;
            cur = mode.BasedOn;
        }

        // Fallback to mode 0
        if (modeIndex != 0 && Modes.TryGetValue(0, out var mode0))
        {
            if (mode0.StateToSubmode.TryGetValue(state, out var submode))
            {
                keyframe = submode.Keyframe;
                flags = submode.Flags;
                return true;
            }
            if (mode0.StateToKeyframe.TryGetValue(state, out var key))
            {
                keyframe = key;
                flags = mode0.StateToFlags.GetValueOrDefault(state, 0);
                return true;
            }
        }

        keyframe = null;
        flags = 0;
        return false;
    }
}

/// <summary>
/// Parser for LucasArts Sith Engine .PUP puppet state machine files.
/// </summary>
public static class PuppetReader
{
    public static PuppetDefinition Parse(string text, string puppetName = "")
    {
        var puppet = new PuppetDefinition
        {
            Name = puppetName
        };

        using var reader = new StringReader(text);
        string? line;
        PuppetMode? currentMode = null;
        bool inJoints = false;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            int commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
            {
                line = line[..commentIdx].Trim();
            }

            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("joints", StringComparison.OrdinalIgnoreCase))
            {
                inJoints = true;
                continue;
            }

            if (line.StartsWith("end", StringComparison.OrdinalIgnoreCase))
            {
                inJoints = false;
                continue;
            }

            if (inJoints)
            {
                // Format: 0=14
                var parts = line.Split('=');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int jointIdx) && int.TryParse(parts[1].Trim(), out int nodeIdx))
                {
                    puppet.Joints[jointIdx] = nodeIdx;
                }
                continue;
            }

            if (line.StartsWith("Mode=", StringComparison.OrdinalIgnoreCase))
            {
                // Example: Mode=0 or Mode=1, basedon=0
                string modeData = line["Mode=".Length..].Trim();
                var parts = modeData.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[0].Trim(), out int mIdx))
                {
                    currentMode = new PuppetMode { ModeIndex = mIdx };
                    puppet.Modes[mIdx] = currentMode;

                    if (parts.Length > 1)
                    {
                        var basedOnPart = parts[1].Trim();
                        if (basedOnPart.StartsWith("basedon=", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(basedOnPart["basedon=".Length..].Trim(), out int baseIdx))
                            {
                                currentMode.BasedOn = baseIdx;
                            }
                        }
                    }
                }
                continue;
            }

            if (currentMode != null)
            {
                // Format: stand ryidleg.key 0x00 0 0
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2)
                {
                    string state = tokens[0].ToLowerInvariant();
                    string keyFile = tokens[1].Trim();
                    int flags = tokens.Length >= 3 ? ParseFlags(tokens[2].Trim()) : 0;
                    currentMode.StateToKeyframe[state] = keyFile;
                    currentMode.StateToFlags[state] = flags;
                    currentMode.StateToSubmode[state] = new PuppetSubmode(keyFile, flags);
                }
            }
        }

        return puppet;
    }

    private static int ParseFlags(string token)
    {
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)
                ? hex
                : 0;
        }

        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val)
            ? val
            : 0;
    }
}
