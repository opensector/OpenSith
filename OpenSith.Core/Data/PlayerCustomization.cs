using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;

namespace OpenSith.Core.Data;

/// <summary>
/// Selectable lightsaber blade color option matching retail <c>misc/sabers.dat</c>.
/// </summary>
public sealed record SaberColorOption(
    int Index,
    string Name,
    string SideMaterial,
    string TipMaterial,
    Vector3 FallbackRgb);

/// <summary>
/// Descriptor for one of Jedi Knight's 14 configurable Force powers (inventory bins 21..34).
/// </summary>
public sealed record ForcePowerOption(
    int Index,
    int Bin,
    string Name,
    string Alignment);

/// <summary>
/// Preset Force loadout profiles for fast menu/CLI testing in SP and MP.
/// </summary>
public enum ForcePreset : byte
{
    Custom = 0,
    None = 1,
    LightJedi = 2,
    DarkJedi = 3,
    MasterSandbox = 4
}

/// <summary>
/// Mutable player customization configuration (model, saber blade color, Jedi rank,
/// Force power ranks 0..4, and optional All-Weapons sandbox grant).
/// </summary>
public sealed class PlayerLoadoutConfig
{
    public const int ForcePowerCount = 14;
    public const int MaxJediRank = 8;
    public const int MaxForcePowerRank = 4;

    public int ModelIndex { get; set; }
    public int SaberColorIndex { get; set; } = PlayerCustomizationCatalog.DefaultSaberColorIndex;
    public int JediRank { get; set; }
    public bool GrantAllWeapons { get; set; }
    public byte[] ForceRanks { get; } = new byte[ForcePowerCount];

    public PlayerLoadoutConfig Clone()
    {
        var copy = new PlayerLoadoutConfig
        {
            ModelIndex = ModelIndex,
            SaberColorIndex = SaberColorIndex,
            JediRank = JediRank,
            GrantAllWeapons = GrantAllWeapons
        };
        Array.Copy(ForceRanks, copy.ForceRanks, ForcePowerCount);
        return copy;
    }

    public void ApplyPreset(ForcePreset preset)
    {
        Array.Clear(ForceRanks);
        switch (preset)
        {
            case ForcePreset.None:
                JediRank = 0;
                break;

            case ForcePreset.LightJedi:
                JediRank = 6;
                for (int i = 0; i < ForcePowerCount; i++)
                {
                    string align = PlayerCustomizationCatalog.ForcePowers[i].Alignment;
                    if (align is "N" or "L")
                        ForceRanks[i] = 3;
                }
                break;

            case ForcePreset.DarkJedi:
                JediRank = 6;
                for (int i = 0; i < ForcePowerCount; i++)
                {
                    string align = PlayerCustomizationCatalog.ForcePowers[i].Alignment;
                    if (align is "N" or "D")
                        ForceRanks[i] = 3;
                }
                break;

            case ForcePreset.MasterSandbox:
                JediRank = MaxJediRank;
                GrantAllWeapons = true;
                for (int i = 0; i < ForcePowerCount; i++)
                    ForceRanks[i] = MaxForcePowerRank;
                break;
        }
    }

    public ForcePreset DetectPreset()
    {
        bool anyNonZero = false;
        for (int i = 0; i < ForcePowerCount; i++)
        {
            if (ForceRanks[i] > 0)
            {
                anyNonZero = true;
                break;
            }
        }

        if (JediRank == 0 && !anyNonZero)
            return ForcePreset.None;

        if (JediRank == MaxJediRank && GrantAllWeapons)
        {
            bool allMax = true;
            for (int i = 0; i < ForcePowerCount; i++)
            {
                if (ForceRanks[i] != MaxForcePowerRank)
                {
                    allMax = false;
                    break;
                }
            }
            if (allMax)
                return ForcePreset.MasterSandbox;
        }

        if (JediRank == 6)
        {
            bool matchesLight = true;
            bool matchesDark = true;
            for (int i = 0; i < ForcePowerCount; i++)
            {
                string align = PlayerCustomizationCatalog.ForcePowers[i].Alignment;
                byte expectedL = (byte)(align is "N" or "L" ? 3 : 0);
                byte expectedD = (byte)(align is "N" or "D" ? 3 : 0);
                if (ForceRanks[i] != expectedL) matchesLight = false;
                if (ForceRanks[i] != expectedD) matchesDark = false;
            }
            if (matchesLight) return ForcePreset.LightJedi;
            if (matchesDark) return ForcePreset.DarkJedi;
        }

        return ForcePreset.Custom;
    }

    public void ClampToPolicy(int maxRank, bool allowAllWeapons, int modelCount, int saberCount)
    {
        int clampedMaxRank = Math.Clamp(maxRank, 0, MaxJediRank);
        ModelIndex = modelCount > 0 ? Math.Clamp(ModelIndex, 0, modelCount - 1) : 0;
        SaberColorIndex = saberCount > 0 ? Math.Clamp(SaberColorIndex, 0, saberCount - 1) : 0;
        JediRank = Math.Clamp(JediRank, 0, clampedMaxRank);
        if (!allowAllWeapons)
            GrantAllWeapons = false;

        int maxPower = JediRank == 0 ? 0 : MaxForcePowerRank;
        for (int i = 0; i < ForcePowerCount; i++)
            ForceRanks[i] = (byte)Math.Clamp((int)ForceRanks[i], 0, maxPower);
    }
}

/// <summary>
/// Retail catalogs for selectable player character models, lightsaber colors, and Force powers.
/// </summary>
public static class PlayerCustomizationCatalog
{
    public const int DefaultSaberColorIndex = 2; // sabergreen in retail misc/sabers.dat

    public static readonly IReadOnlyList<SaberColorOption> DefaultSaberColors =
    [
        new(0, "Blue",     "saberblue1.mat",   "saberblue0.mat",   new Vector3(0.20f, 0.58f, 1.00f)),
        new(1, "Dark Red", "saberdred1.mat",   "saberdred0.mat",   new Vector3(0.85f, 0.08f, 0.12f)),
        new(2, "Green",    "sabergreen1.mat",  "sabergreen0.mat",  new Vector3(0.12f, 1.00f, 0.32f)),
        new(3, "Orange",   "saberorange1.mat", "saberorange0.mat", new Vector3(1.00f, 0.55f, 0.08f)),
        new(4, "Red",      "saberred1.mat",    "saberred0.mat",    new Vector3(1.00f, 0.14f, 0.12f)),
        new(5, "Purple",   "saberpurple1.mat", "saberpurple0.mat", new Vector3(0.75f, 0.28f, 1.00f)),
        new(6, "Yellow",   "saberyellow1.mat", "saberyellow0.mat", new Vector3(1.00f, 0.92f, 0.15f)),
    ];

    public static readonly IReadOnlyList<ForcePowerOption> ForcePowers =
    [
        new(0,  PlayerInventory.BinForceJump,        "Jump",        "N"),
        new(1,  PlayerInventory.BinForceSpeed,       "Speed",       "N"),
        new(2,  PlayerInventory.BinForceSeeing,      "Seeing",      "N"),
        new(3,  PlayerInventory.BinForcePull,        "Pull",        "N"),
        new(4,  PlayerInventory.BinForceHeal,        "Heal",        "L"),
        new(5,  PlayerInventory.BinForcePersuasion,  "Persuasion",  "L"),
        new(6,  PlayerInventory.BinForceBlinding,    "Blinding",    "L"),
        new(7,  PlayerInventory.BinForceAbsorb,      "Absorb",      "L"),
        new(8,  PlayerInventory.BinForceProtection,  "Protection",  "L"),
        new(9,  PlayerInventory.BinForceThrow,       "Saber Throw", "D"),
        new(10, PlayerInventory.BinForceGrip,        "Grip",        "D"),
        new(11, PlayerInventory.BinForceLightning,   "Lightning",   "D"),
        new(12, PlayerInventory.BinForceDestruction, "Destruction", "D"),
        new(13, PlayerInventory.BinForceDeadlySight, "Deadly Sight","D"),
    ];

    public static IReadOnlyList<ModelSelectionEntry> LoadModels(IVirtualFileSystem? vfs)
    {
        if (vfs != null)
        {
            string? path = ResolvePath(vfs, "misc/models.dat", "models.dat");
            if (path != null)
            {
                try
                {
                    var parsed = ModelSelectionReader.Parse(vfs.ReadAllText(path));
                    if (parsed.Count > 0)
                        return parsed;
                }
                catch
                {
                }
            }
        }

        return [new ModelSelectionEntry(0, "ky.3do", "ky.snd", "Katarn")];
    }

    public static IReadOnlyList<SaberColorOption> LoadSaberColors(IVirtualFileSystem? vfs)
    {
        if (vfs != null)
        {
            string? path = ResolvePath(vfs, "misc/sabers.dat", "sabers.dat");
            if (path != null)
            {
                try
                {
                    var parsed = SaberSelectionReader.Parse(vfs.ReadAllText(path));
                    if (parsed.Count > 0)
                    {
                        var list = new List<SaberColorOption>(parsed.Count);
                        for (int i = 0; i < parsed.Count; i++)
                        {
                            var entry = parsed[i];
                            (string name, Vector3 rgb) = InferSaberColorStyle(entry.SideMaterial);
                            list.Add(new SaberColorOption(i, name, entry.SideMaterial, entry.TipMaterial, rgb));
                        }
                        return list;
                    }
                }
                catch
                {
                }
            }
        }

        return DefaultSaberColors;
    }

    public static bool TryResolveModelIndex(IReadOnlyList<ModelSelectionEntry> models, string? token, out int index)
    {
        index = 0;
        if (models.Count == 0 || string.IsNullOrWhiteSpace(token))
            return false;

        string trimmed = token.Trim();
        if (int.TryParse(trimmed, out int num))
        {
            if (num >= 0 && num < models.Count)
            {
                index = num;
                return true;
            }
            for (int i = 0; i < models.Count; i++)
            {
                if (models[i].Index == num)
                {
                    index = i;
                    return true;
                }
            }
        }

        for (int i = 0; i < models.Count; i++)
        {
            if (models[i].DisplayName.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
                models[i].Model3D.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        for (int i = 0; i < models.Count; i++)
        {
            if (models[i].DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                models[i].Model3D.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    public static bool TryResolveSaberIndex(IReadOnlyList<SaberColorOption> sabers, string? token, out int index)
    {
        index = DefaultSaberColorIndex < sabers.Count ? DefaultSaberColorIndex : 0;
        if (sabers.Count == 0 || string.IsNullOrWhiteSpace(token))
            return false;

        string trimmed = token.Trim();
        if (int.TryParse(trimmed, out int num) && num >= 0 && num < sabers.Count)
        {
            index = num;
            return true;
        }

        for (int i = 0; i < sabers.Count; i++)
        {
            if (sabers[i].Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
                sabers[i].SideMaterial.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        if (trimmed.Equals("dred", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("dark red", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < sabers.Count; i++)
            {
                if (sabers[i].Name.Contains("Dark", StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }
        }

        for (int i = 0; i < sabers.Count; i++)
        {
            if (sabers[i].Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the canonical third-person weapon mesh attached to <c>k_rhand</c> (joint 12 / PRIMARYWEAP),
    /// or <see langword="null"/> for unarmed fists.
    /// </summary>
    public static string? GetThirdPersonWeaponModel(int weaponBin) => weaponBin switch
    {
        Cog.PlayerInventory.BinBryar => "bryg.3do",
        Cog.PlayerInventory.BinStRifle => "strg.3do",
        Cog.PlayerInventory.BinThermDet => "detg.3do",
        Cog.PlayerInventory.BinBowcaster => "bowg.3do",
        Cog.PlayerInventory.BinRepeater => "rptg.3do",
        Cog.PlayerInventory.BinRailDet => "rldg.3do",
        Cog.PlayerInventory.BinSeqCharge => "seqg.3do",
        Cog.PlayerInventory.BinConcRifle => "cong.3do",
        Cog.PlayerInventory.BinLightsaber => "sabg.3do",
        _ => null
    };

    public static SaberColorOption ResolveSaberOption(IReadOnlyList<SaberColorOption> sabers, int index)
    {
        if (sabers.Count == 0)
            return DefaultSaberColors[DefaultSaberColorIndex];
        return sabers[Math.Clamp(index, 0, sabers.Count - 1)];
    }

    public static SaberColorOption? FindSaberBySideMaterial(IReadOnlyList<SaberColorOption> sabers, string? sideMat)
    {
        if (string.IsNullOrWhiteSpace(sideMat))
            return null;
        foreach (var s in sabers)
        {
            if (s.SideMaterial.Equals(sideMat.Trim(), StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }

    private static (string Name, Vector3 Rgb) InferSaberColorStyle(string sideMat)
    {
        string lower = sideMat.ToLowerInvariant();
        if (lower.Contains("dred"))   return ("Dark Red", DefaultSaberColors[1].FallbackRgb);
        if (lower.Contains("red"))    return ("Red",      DefaultSaberColors[4].FallbackRgb);
        if (lower.Contains("blue"))   return ("Blue",     DefaultSaberColors[0].FallbackRgb);
        if (lower.Contains("green"))  return ("Green",    DefaultSaberColors[2].FallbackRgb);
        if (lower.Contains("orange")) return ("Orange",   DefaultSaberColors[3].FallbackRgb);
        if (lower.Contains("purple")) return ("Purple",   DefaultSaberColors[5].FallbackRgb);
        if (lower.Contains("yellow")) return ("Yellow",   DefaultSaberColors[6].FallbackRgb);
        return ("Custom", new Vector3(0.4f, 0.9f, 1.0f));
    }

    private static string? ResolvePath(IVirtualFileSystem vfs, params string[] candidates)
    {
        foreach (string c in candidates)
        {
            if (vfs.FileExists(c)) return c;
            string low = c.ToLowerInvariant();
            if (vfs.FileExists(low)) return low;
        }
        return null;
    }
}
