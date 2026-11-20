using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Unit and retail-asset regression tests for Force Power inventory bins,
/// availability tracking, Jedi Rank, and passive Force mana regeneration.
/// </summary>
public sealed class ForceInventoryTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void ForceBinConstants_MatchCanonicalValues()
    {
        Assert.Equal(14, PlayerInventory.BinForceMana);
        Assert.Equal(16, PlayerInventory.BinNewStars);
        Assert.Equal(17, PlayerInventory.BinForceStars);
        Assert.Equal(18, PlayerInventory.BinChoice);
        Assert.Equal(20, PlayerInventory.BinForceRank);
        Assert.Equal(21, PlayerInventory.BinForceJump);
        Assert.Equal(22, PlayerInventory.BinForceSpeed);
        Assert.Equal(23, PlayerInventory.BinForceSeeing);
        Assert.Equal(24, PlayerInventory.BinForcePull);
        Assert.Equal(25, PlayerInventory.BinForceHeal);
        Assert.Equal(26, PlayerInventory.BinForcePersuasion);
        Assert.Equal(27, PlayerInventory.BinForceBlinding);
        Assert.Equal(28, PlayerInventory.BinForceAbsorb);
        Assert.Equal(29, PlayerInventory.BinForceProtection);
        Assert.Equal(30, PlayerInventory.BinForceThrow);
        Assert.Equal(31, PlayerInventory.BinForceGrip);
        Assert.Equal(32, PlayerInventory.BinForceLightning);
        Assert.Equal(33, PlayerInventory.BinForceDestruction);
        Assert.Equal(34, PlayerInventory.BinForceDeadlySight);
        Assert.Equal(61, PlayerInventory.BinForceArmor);
        Assert.Equal(64, PlayerInventory.BinForceDarkSurge);
        Assert.Equal(65, PlayerInventory.BinForceLightSurge);
        Assert.Equal(74, PlayerInventory.BinForceMorality);
        Assert.Equal(75, PlayerInventory.BinAlignment);
    }

    [Fact]
    public void GetInvMax_ForceAndRelatedBins_MatchItemsDat()
    {
        Assert.Equal(400f, PlayerInventory.GetInvMax(PlayerInventory.BinForceMana));
        Assert.Equal(32f, PlayerInventory.GetInvMax(PlayerInventory.BinNewStars));
        Assert.Equal(32f, PlayerInventory.GetInvMax(PlayerInventory.BinForceStars));
        Assert.Equal(2f, PlayerInventory.GetInvMax(PlayerInventory.BinChoice));
        Assert.Equal(8f, PlayerInventory.GetInvMax(PlayerInventory.BinForceRank));

        for (int bin = PlayerInventory.BinForceJump; bin <= PlayerInventory.BinForceDeadlySight; bin++)
        {
            Assert.Equal(4f, PlayerInventory.GetInvMax(bin));
        }

        Assert.Equal(200f, PlayerInventory.GetInvMax(PlayerInventory.BinForceArmor));
        Assert.Equal(1f, PlayerInventory.GetInvMax(PlayerInventory.BinForceDarkSurge));
        Assert.Equal(1f, PlayerInventory.GetInvMax(PlayerInventory.BinForceLightSurge));
        Assert.Equal(40f, PlayerInventory.GetInvMax(PlayerInventory.BinForceMorality));
        Assert.Equal(-40f, PlayerInventory.GetInvMin(PlayerInventory.BinForceMorality));
        Assert.Equal(100f, PlayerInventory.GetInvMax(PlayerInventory.BinAlignment));
        Assert.Equal(-100f, PlayerInventory.GetInvMin(PlayerInventory.BinAlignment));
    }

    [Fact]
    public void ChangeInv_ForceBins_ClampedToValidRange()
    {
        var inv = new PlayerInventory();

        // Jedi Rank clamps to [0, 8]
        Assert.Equal(8f, inv.ChangeInv(PlayerInventory.BinForceRank, 20f));
        Assert.Equal(8, inv.ForceRank);
        Assert.Equal(0f, inv.ChangeInv(PlayerInventory.BinForceRank, -50f));
        Assert.Equal(0, inv.ForceRank);

        // Force powers clamp to [0, 4]
        Assert.Equal(4f, inv.ChangeInv(PlayerInventory.BinForceLightning, 10f));
        Assert.Equal(0f, inv.ChangeInv(PlayerInventory.BinForceLightning, -10f));

        // Force mana clamps to [0, 400]
        Assert.Equal(400f, inv.ChangeInv(PlayerInventory.BinForceMana, 999f));
        Assert.Equal(0f, inv.ChangeInv(PlayerInventory.BinForceMana, -999f));

        // Force stars clamp to [0, 32]
        inv.ForceStars = 50;
        Assert.Equal(32, inv.ForceStars);
        inv.ForceStars = -5;
        Assert.Equal(0, inv.ForceStars);

        // Morality & alignment support negative minimums
        Assert.Equal(-40f, inv.ChangeInv(PlayerInventory.BinForceMorality, -100f));
        Assert.Equal(40f, inv.ChangeInv(PlayerInventory.BinForceMorality, 200f));
        Assert.Equal(-100f, inv.ChangeInv(PlayerInventory.BinAlignment, -200f));
        Assert.Equal(100f, inv.ChangeInv(PlayerInventory.BinAlignment, 500f));
    }

    [Fact]
    public void SetInvAvailable_IsInvAvailable_TracksAvailabilityPerBin()
    {
        var inv = new PlayerInventory();

        Assert.False(inv.IsInvAvailable(PlayerInventory.BinForceJump));
        Assert.False(inv.IsInvAvailable(PlayerInventory.BinForceHeal));

        inv.SetInvAvailable(PlayerInventory.BinForceJump, true);
        inv.SetInvAvailable(PlayerInventory.BinForceHeal, true);
        Assert.True(inv.IsInvAvailable(PlayerInventory.BinForceJump));
        Assert.True(inv.IsInvAvailable(PlayerInventory.BinForceHeal));

        inv.SetInvAvailable(PlayerInventory.BinForceJump, false);
        Assert.False(inv.IsInvAvailable(PlayerInventory.BinForceJump));
        Assert.True(inv.IsInvAvailable(PlayerInventory.BinForceHeal));
    }

    [Fact]
    public void UpdateForceMana_RegeneratesScaledByRank_AndClampsToMaxForceMana()
    {
        var inv = new PlayerInventory();

        // Rank 0: MaxForceMana = 0, no passive regeneration
        inv.UpdateForceMana(1.0f);
        Assert.Equal(0f, inv.ForceMana);

        // Rank 2: MaxForceMana = 100, rate = 1.0 mana/sec
        inv.ForceRank = 2;
        Assert.Equal(100f, inv.MaxForceMana);
        inv.UpdateForceMana(10.0f);
        Assert.Equal(10f, inv.ForceMana, precision: 3);

        // Rank 4: MaxForceMana = 200, rate = 2.0 mana/sec
        inv.ForceRank = 4;
        Assert.Equal(200f, inv.MaxForceMana);
        inv.UpdateForceMana(5.0f);
        Assert.Equal(20f, inv.ForceMana, precision: 3);

        // Clamps at MaxForceMana (200 for rank 4)
        inv.UpdateForceMana(500.0f);
        Assert.Equal(200f, inv.ForceMana, precision: 3);

        // Rank 8: MaxForceMana = 400, rate = 4.0 mana/sec
        inv.ForceRank = 8;
        Assert.Equal(400f, inv.MaxForceMana);
        inv.UpdateForceMana(10.0f);
        Assert.Equal(240f, inv.ForceMana, precision: 3);

        // Dead player does not regenerate mana
        inv.Health = 0f;
        Assert.True(inv.IsDead);
        inv.UpdateForceMana(10.0f);
        Assert.Equal(240f, inv.ForceMana, precision: 3);

        // Non-positive dt is a no-op
        inv.Health = 100f;
        inv.UpdateForceMana(0f);
        inv.UpdateForceMana(-2.0f);
        Assert.Equal(240f, inv.ForceMana, precision: 3);
    }

    [Fact]
    public void RetailForceCogs_BinNumbersAndItemsDat_MatchPlayerInventory()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);

        // Verify items.dat bin IDs, mins, and maxes
        string itemsDat = Encoding.ASCII.GetString(archive.ReadAllBytes(@"misc\items.dat"));
        var expectedItems = new Dictionary<string, int>
        {
            ["forcemana"] = PlayerInventory.BinForceMana,
            ["new_stars"] = PlayerInventory.BinNewStars,
            ["spend_stars"] = PlayerInventory.BinForceStars,
            ["choice"] = PlayerInventory.BinChoice,
            ["jedi_rank"] = PlayerInventory.BinForceRank,
            ["f_jump"] = PlayerInventory.BinForceJump,
            ["f_speed"] = PlayerInventory.BinForceSpeed,
            ["f_seeing"] = PlayerInventory.BinForceSeeing,
            ["f_pull"] = PlayerInventory.BinForcePull,
            ["f_healing"] = PlayerInventory.BinForceHeal,
            ["f_persuasion"] = PlayerInventory.BinForcePersuasion,
            ["f_blinding"] = PlayerInventory.BinForceBlinding,
            ["f_absorb"] = PlayerInventory.BinForceAbsorb,
            ["f_protection"] = PlayerInventory.BinForceProtection,
            ["f_throw"] = PlayerInventory.BinForceThrow,
            ["f_grip"] = PlayerInventory.BinForceGrip,
            ["f_lightning"] = PlayerInventory.BinForceLightning,
            ["f_destruction"] = PlayerInventory.BinForceDestruction,
            ["f_deadlysight"] = PlayerInventory.BinForceDeadlySight,
            ["force_armor"] = PlayerInventory.BinForceArmor,
            ["s_dark"] = PlayerInventory.BinForceDarkSurge,
            ["s_light"] = PlayerInventory.BinForceLightSurge,
            ["force_morality"] = PlayerInventory.BinForceMorality,
            ["alignment"] = PlayerInventory.BinAlignment,
        };

        var matchedItems = new HashSet<string>();
        foreach (string rawLine in itemsDat.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.Equals("end", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = Regex.Split(line, @"\s+");
            if (parts.Length < 4)
                continue;

            if (expectedItems.TryGetValue(parts[0], out int expectedBin))
            {
                int bin = int.Parse(parts[1], CultureInfo.InvariantCulture);
                float min = float.Parse(parts[2], CultureInfo.InvariantCulture);
                float max = float.Parse(parts[3], CultureInfo.InvariantCulture);

                Assert.Equal(expectedBin, bin);
                Assert.Equal(min, PlayerInventory.GetInvMin(bin));
                Assert.Equal(max, PlayerInventory.GetInvMax(bin));
                matchedItems.Add(parts[0]);
            }
        }

        Assert.Equal(expectedItems.Count, matchedItems.Count);

        // Verify force_*.cog header bin comments against PlayerInventory constants
        var expectedForceCogs = new Dictionary<string, int>
        {
            [@"cog\force_well.cog"] = PlayerInventory.BinForceRank,
            [@"cog\force_jump.cog"] = PlayerInventory.BinForceJump,
            [@"cog\force_speed.cog"] = PlayerInventory.BinForceSpeed,
            [@"cog\force_seeing.cog"] = PlayerInventory.BinForceSeeing,
            [@"cog\force_pull.cog"] = PlayerInventory.BinForcePull,
            [@"cog\force_heal.cog"] = PlayerInventory.BinForceHeal,
            [@"cog\force_persuasion.cog"] = PlayerInventory.BinForcePersuasion,
            [@"cog\force_blinding.cog"] = PlayerInventory.BinForceBlinding,
            [@"cog\force_absorb.cog"] = PlayerInventory.BinForceAbsorb,
            [@"cog\force_protection.cog"] = PlayerInventory.BinForceProtection,
            [@"cog\force_throw.cog"] = PlayerInventory.BinForceThrow,
            [@"cog\force_grip.cog"] = PlayerInventory.BinForceGrip,
            [@"cog\force_lightning.cog"] = PlayerInventory.BinForceLightning,
            [@"cog\force_destruction.cog"] = PlayerInventory.BinForceDestruction,
            [@"cog\force_deadlysight.cog"] = PlayerInventory.BinForceDeadlySight,
        };

        foreach (var (cogPath, expectedBin) in expectedForceCogs)
        {
            string cogText = Encoding.ASCII.GetString(archive.ReadAllBytes(cogPath));
            Match match = Regex.Match(cogText, @"#\s*Bin\s+(\d+)", RegexOptions.IgnoreCase);
            Assert.True(match.Success, $"Expected '# Bin <N>' header in {cogPath}");
            int cogBin = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.Equal(expectedBin, cogBin);
        }
    }
}
