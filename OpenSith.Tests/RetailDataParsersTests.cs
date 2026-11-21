using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Data;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class RetailDataParsersTests
{
    private const string SteamJkDir = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";
    private static readonly string SteamRes1HiPath = Path.Combine(SteamJkDir, "Resource", "Res1hi.gob");
    private static readonly string SteamRes2Path = Path.Combine(SteamJkDir, "Resource", "Res2.gob");
    private static readonly string SteamJk1Path = Path.Combine(SteamJkDir, "Episode", "JK1.GOB");
    private static readonly string SteamJk1CtfPath = Path.Combine(SteamJkDir, "Episode", "JK1CTF.GOB");

    [Fact]
    public void UniStringTable_Parse_HandlesCommentsEscapesWhitespaceKeysAndEndSentinel()
    {
        const string sample = """
            MSGS 999   # <--- outdated count should not restrict parsing

            # Full line comment
            "ERROR"                    0 "Error"                  # trailing comment
            "ERR_MAY_BE_RELATED_TO"    0 "\n\nRelated:\n"
            "ESCAPED_QUOTE"            0 "He said \"hello\""
            "DIK_BACKSLASH"            0 "\"
            "DIK_Z"                    0 "Z"
            "FORWARD_K"                0 " Forward"
            "HUD_TEAMPLAYERS"          0 "# Players"
            "CREDITS_ small_7"         0 "Lead Conceptual Designer"
            "CREDITS_big_446"          0 Paul Zabierek
            "COG_00001"                0 "Fists"
            "COG_42"                   0 "Forty-Two"
            "GOAL_01000"               0 "Find 8t88"
            "GOAL_2000"                0 "Recover the disk"

            END   # stop before trailing garbage
            "IGNORED_AFTER_END"        0 "Should not load"
            O
            """;

        UniStringTable table = UniStringTable.Parse(sample);

        Assert.Equal(13, table.Count);
        Assert.True(table.TryGetValue("error", out string error));
        Assert.Equal("Error", error);

        Assert.True(table.TryGetValue("ERR_MAY_BE_RELATED_TO", out string related));
        Assert.Equal("\n\nRelated:\n", related);

        Assert.True(table.TryGetValue("ESCAPED_QUOTE", out string escaped));
        Assert.Equal("He said \"hello\"", escaped);

        Assert.True(table.TryGetValue("DIK_BACKSLASH", out string backslash));
        Assert.Equal("\\", backslash);

        Assert.True(table.TryGetValue("DIK_Z", out string dikZ));
        Assert.Equal("Z", dikZ);

        Assert.True(table.TryGetValue("FORWARD_K", out string forward));
        Assert.Equal(" Forward", forward);

        Assert.True(table.TryGetValue("HUD_TEAMPLAYERS", out string teamPlayers));
        Assert.Equal("# Players", teamPlayers);

        Assert.True(table.TryGetValue("CREDITS_ small_7", out string creditRole));
        Assert.Equal("Lead Conceptual Designer", creditRole);

        Assert.True(table.TryGetValue("CREDITS_big_446", out string creditName));
        Assert.Equal("Paul Zabierek", creditName);

        Assert.True(table.TryGetCogString(1, out string cog1));
        Assert.Equal("Fists", cog1);
        Assert.True(table.TryGetCogString(42, out string cog42));
        Assert.Equal("Forty-Two", cog42);
        Assert.False(table.TryGetCogString(999, out string missingCog));
        Assert.Equal(string.Empty, missingCog);

        Assert.True(table.TryGetGoalString(1000, out string goal1000));
        Assert.Equal("Find 8t88", goal1000);
        Assert.True(table.TryGetGoalString(2000, out string goal2000));
        Assert.Equal("Recover the disk", goal2000);
        Assert.False(table.TryGetGoalString(9999, out string missingGoal));
        Assert.Equal(string.Empty, missingGoal);

        Assert.False(table.TryGetValue("IGNORED_AFTER_END", out _));
        Assert.False(table.TryGetValue("O", out _));
    }

    [Fact]
    public void UniStringTable_Merge_OverwritesExistingKeysCaseInsensitively()
    {
        UniStringTable baseTable = UniStringTable.Parse("""
            MSGS 2
            "COG_01001" 0 "Locked!"
            "BASE_ONLY" 0 "Base Value"
            """);

        UniStringTable overlay = UniStringTable.Parse("""
            MSGS 2
            "cog_01001"    0 "Welcome to CTF!"
            "OVERLAY_ONLY" 0 "Overlay Value"
            """);

        baseTable.Merge(overlay);

        Assert.Equal(3, baseTable.Count);
        Assert.True(baseTable.TryGetCogString(1001, out string overridden));
        Assert.Equal("Welcome to CTF!", overridden);
        Assert.True(baseTable.TryGetValue("BASE_ONLY", out string baseOnly));
        Assert.Equal("Base Value", baseOnly);
        Assert.True(baseTable.TryGetValue("OVERLAY_ONLY", out string overlayOnly));
        Assert.Equal("Overlay Value", overlayOnly);
    }

    [Fact]
    public void UniStringTable_LoadFromVfs_MergesPriorityOrderIgnoresMissingAndResolvesCase()
    {
        var vfs = new CaseSensitiveMemoryVfs(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MISC/SITHSTRINGS.UNI"] = """
                MSGS 2
                "SHARED" 0 "From Sith"
                "SITH_ONLY" 0 "Sith"
                """,
            ["ui/jkStrings.uni"] = """
                MSGS 2
                "SHARED" 0 "From JK"
                "JK_ONLY" 0 "JK"
                """
            // misc/cogstrings.uni intentionally omitted
        });

        UniStringTable table = UniStringTable.LoadFromVfs(vfs);

        Assert.Equal(3, table.Count);
        Assert.True(table.TryGetValue("SHARED", out string shared));
        Assert.Equal("From JK", shared);
        Assert.True(table.TryGetValue("SITH_ONLY", out string sithOnly));
        Assert.Equal("Sith", sithOnly);
        Assert.True(table.TryGetValue("JK_ONLY", out string jkOnly));
        Assert.Equal("JK", jkOnly);
    }

    [Fact]
    public void UniStringTable_RetailGobs_LoadFromVfsResolvesRetailAndCtfStrings()
    {
        if (!File.Exists(SteamRes1HiPath) || !File.Exists(SteamRes2Path) || !File.Exists(SteamJk1Path))
            return;

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(SteamRes1HiPath));
        vfs.Mount(GobArchive.FromFile(SteamRes2Path));
        vfs.Mount(GobArchive.FromFile(SteamJk1Path));

        UniStringTable table = UniStringTable.LoadFromVfs(vfs);

        Assert.True(table.TryGetCogString(1, out string fists));
        Assert.Equal("Fists", fists);

        Assert.True(table.TryGetCogString(10, out string lightsaber));
        Assert.Equal("Lightsaber", lightsaber);

        Assert.True(table.TryGetCogString(1001, out string locked));
        Assert.Equal("Locked!", locked);

        Assert.True(table.TryGetGoalString(1000, out string goal01000));
        Assert.False(string.IsNullOrWhiteSpace(goal01000));

        // Verify edge-case retail entries in ui/jkstrings.uni and misc/sithstrings.uni
        Assert.True(table.TryGetValue("GAME_SAVED", out string gameSaved));
        Assert.Equal("Game saved.", gameSaved);

        Assert.True(table.TryGetValue("DIK_BACKSLASH", out string backslash));
        Assert.Equal("\\", backslash);

        Assert.True(table.TryGetValue("DIK_Z", out string dikZ));
        Assert.Equal("Z", dikZ);

        Assert.True(table.TryGetValue("ERR_MAY_BE_RELATED_TO", out string errRelated));
        Assert.Contains("\n", errRelated);

        Assert.False(table.TryGetValue("O", out _));

        // Verify ui/cutstrings.uni and ui/credits.uni also parse cleanly
        using var res1Hi = GobArchive.FromFile(SteamRes1HiPath);
        UniStringTable cutStrings = UniStringTable.Parse(Encoding.ASCII.GetString(res1Hi.ReadAllBytes("ui/cutstrings.uni")));
        Assert.True(cutStrings.TryGetValue("CUT_3", out string cut3));
        Assert.False(string.IsNullOrWhiteSpace(cut3));

        UniStringTable credits = UniStringTable.Parse(Encoding.ASCII.GetString(res1Hi.ReadAllBytes("ui/credits.uni")));
        Assert.True(credits.TryGetValue("CREDITS_ small_7", out string creditSmall7));
        Assert.Contains("Lead Conceptual Designer", creditSmall7);
        Assert.True(credits.TryGetValue("CREDITS_big_446", out string credit446));
        Assert.Equal("Paul Zabierek", credit446);

        if (!File.Exists(SteamJk1CtfPath))
            return;

        using var ctfArchive = GobArchive.FromFile(SteamJk1CtfPath);
        UniStringTable ctfDirect = UniStringTable.Parse(
            Encoding.ASCII.GetString(ctfArchive.ReadAllBytes("misc/cogstrings.uni")));
        Assert.True(ctfDirect.TryGetCogString(1001, out string ctfWelcome));
        Assert.Equal("Welcome to Jedi Knight CTF!", ctfWelcome);

        // Mounting JK1CTF.GOB on top overrides JK1.GOB's misc/cogstrings.uni
        vfs.Mount(GobArchive.FromFile(SteamJk1CtfPath));
        UniStringTable ctfMerged = UniStringTable.LoadFromVfs(vfs);
        Assert.True(ctfMerged.TryGetCogString(1001, out string ctfOverlayWelcome));
        Assert.Equal("Welcome to Jedi Knight CTF!", ctfOverlayWelcome);
    }

    [Fact]
    public void ItemsDatReader_ParseSynthetic_HandlesHexDecimalFlagsNegativeFloatsArgsAndComments()
    {
        const string sample = """
            # Header comment
            none              0   0     0     0x004
            fists             1   0     1     0x024   cog=weap_fists.cog
            custom_item       42  -1.5  12.75 164     cog=item_test.cog tag=alpha
            commented_arg     62  0     1.0   0x010   #cog=ignored.cog
            force_morality    74  -40   40    0x000
            end
            ignored_after_end 99  0     1     0x000
            """;

        IReadOnlyList<ItemDefinition> items = ItemsDatReader.Parse(sample);
        Dictionary<int, ItemDefinition> byId = ItemsDatReader.ParseById(sample);

        Assert.Equal(5, items.Count);
        Assert.Equal(5, byId.Count);

        Assert.Equal("none", byId[0].Name);
        Assert.Equal(0x004u, byId[0].Flags);
        Assert.Null(byId[0].CogName);
        Assert.Empty(byId[0].Args);

        Assert.Equal("fists", byId[1].Name);
        Assert.Equal(0x024u, byId[1].Flags);
        Assert.Equal("weap_fists.cog", byId[1].CogName);

        Assert.Equal(-1.5f, byId[42].MinValue);
        Assert.Equal(12.75f, byId[42].MaxValue);
        Assert.Equal(164u, byId[42].Flags);
        Assert.Equal("item_test.cog", byId[42].CogName);
        Assert.Equal("alpha", byId[42].Args["tag"]);

        Assert.Null(byId[62].CogName);
        Assert.Empty(byId[62].Args);

        Assert.Equal(-40f, byId[74].MinValue);
        Assert.Equal(40f, byId[74].MaxValue);
        Assert.False(byId.ContainsKey(99));
    }

    [Fact]
    public void ItemsDatReader_ParseMalformedLine_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => ItemsDatReader.Parse("broken_item 1 0"));
        Assert.Throws<FormatException>(() => ItemsDatReader.Parse("broken_id abc 0 1 0x004"));
        Assert.Throws<FormatException>(() => ItemsDatReader.Parse("broken_flags 1 0 1 nothex"));
    }

    [Fact]
    public void ItemsDatReader_RetailRes2Gob_ParsesWeaponsAmmoForceBinsAndCogs()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var res2 = GobArchive.FromFile(SteamRes2Path);
        string text = Encoding.ASCII.GetString(res2.ReadAllBytes("misc/items.dat"));

        IReadOnlyList<ItemDefinition> items = ItemsDatReader.Parse(text);
        Dictionary<int, ItemDefinition> byId = ItemsDatReader.ParseById(text);

        Assert.Equal(items.Count, byId.Count);

        // All 10 weapons (bins 1..10)
        (int BinId, string Name, string Cog)[] expectedWeapons =
        [
            (1, "fists", "weap_fists.cog"),
            (2, "bryar_pistol", "weap_bryar.cog"),
            (3, "stormtrooper_rifle", "weap_strifle.cog"),
            (4, "thermal_detonator", "weap_thermdet.cog"),
            (5, "tusken_prod", "weap_crossbow.cog"),
            (6, "repeater", "weap_repeater.cog"),
            (7, "rail_detonator", "weap_raildet.cog"),
            (8, "sequencer_charge", "weap_seqcharge.cog"),
            (9, "concussion_rifle", "weap_concrifle.cog"),
            (10, "lightsaber", "weap_saber.cog")
        ];

        foreach (var (binId, name, cog) in expectedWeapons)
        {
            Assert.True(byId.TryGetValue(binId, out ItemDefinition? weapon), $"Missing weapon bin {binId}");
            Assert.NotNull(weapon);
            Assert.Equal(name, weapon.Name);
            Assert.Equal(cog, weapon.CogName);
        }

        // Ammo bins
        Assert.Equal("energy", byId[11].Name);
        Assert.Equal(500f, byId[11].MaxValue);
        Assert.Equal("power", byId[12].Name);
        Assert.Equal(500f, byId[12].MaxValue);
        Assert.Equal("battery", byId[13].Name);
        Assert.Equal(200f, byId[13].MaxValue);
        Assert.Equal("railcharges", byId[15].Name);
        Assert.Equal(30f, byId[15].MaxValue);

        // Force mana + Force powers (bins 14 and 20..34)
        Assert.Equal("forcemana", byId[14].Name);
        Assert.Equal(400f, byId[14].MaxValue);
        Assert.Equal("jedi_rank", byId[20].Name);
        Assert.Equal("force_well.cog", byId[20].CogName);

        for (int forceBin = 21; forceBin <= 34; forceBin++)
        {
            Assert.True(byId.TryGetValue(forceBin, out ItemDefinition? power), $"Missing Force bin {forceBin}");
            Assert.NotNull(power);
            Assert.NotNull(power.CogName);
            Assert.False(string.IsNullOrWhiteSpace(power.CogName));
            Assert.StartsWith("force_", power.CogName, StringComparison.OrdinalIgnoreCase);
        }

        // Negative min values and inline commented cog args
        Assert.Equal(-40f, byId[74].MinValue);
        Assert.Equal(-100f, byId[75].MinValue);
        Assert.Null(byId[62].CogName);
    }

    [Fact]
    public void ModelSelectionReader_ParseSynthetic_ExtractsCommentOrFallsBackToModelStem()
    {
        const string sample = """
            # Sample models
            numModels: 3

            0:  ky.3do     ky.snd        #"Katarn"
            1:  kyB3.3do   kyMP4.snd     # unquoted comment should fall back
            2:  kyC5.3do   kyMP1.snd
            """;

        IReadOnlyList<ModelSelectionEntry> entries = ModelSelectionReader.Parse(sample);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new ModelSelectionEntry(0, "ky.3do", "ky.snd", "Katarn"), entries[0]);
        Assert.Equal(new ModelSelectionEntry(1, "kyB3.3do", "kyMP4.snd", "kyB3"), entries[1]);
        Assert.Equal(new ModelSelectionEntry(2, "kyC5.3do", "kyMP1.snd", "kyC5"), entries[2]);
    }

    [Fact]
    public void ModelSelectionReader_RetailRes2Gob_ParsesAll28Models()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var res2 = GobArchive.FromFile(SteamRes2Path);
        string text = Encoding.ASCII.GetString(res2.ReadAllBytes("misc/models.dat"));

        IReadOnlyList<ModelSelectionEntry> entries = ModelSelectionReader.Parse(text);

        Assert.Equal(28, entries.Count);
        Assert.Equal(0, entries[0].Index);
        Assert.Equal("ky.3do", entries[0].Model3D);
        Assert.Equal("ky.snd", entries[0].SoundClass);
        Assert.Equal("Katarn", entries[0].DisplayName);

        Assert.Equal(27, entries[27].Index);
        Assert.Equal("kyY0.3do", entries[27].Model3D);
        Assert.Equal("kyMP3.snd", entries[27].SoundClass);
        Assert.Equal("Orange", entries[27].DisplayName);

        for (int i = 0; i < entries.Count; i++)
        {
            Assert.Equal(i, entries[i].Index);
            Assert.EndsWith(".3do", entries[i].Model3D, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".snd", entries[i].SoundClass, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(entries[i].DisplayName));
        }
    }

    [Fact]
    public void SaberSelectionReader_ParseSynthetic_HandlesCommentsAndThrowsOnMalformed()
    {
        const string sample = """
            numSabers: 2
            # BM        sideMat          tipMat
            0: sabB2.BM saberblue1.mat   saberblue0.mat
            1: sabG2.BM sabergreen1.mat  sabergreen0.mat # trailing comment
            """;

        IReadOnlyList<SaberSelectionEntry> sabers = SaberSelectionReader.Parse(sample);

        Assert.Equal(2, sabers.Count);
        Assert.Equal(new SaberSelectionEntry(0, "sabB2.BM", "saberblue1.mat", "saberblue0.mat"), sabers[0]);
        Assert.Equal(new SaberSelectionEntry(1, "sabG2.BM", "sabergreen1.mat", "sabergreen0.mat"), sabers[1]);

        Assert.Throws<FormatException>(() => SaberSelectionReader.Parse("0: sabB2.BM missing_tip.mat"));
    }

    [Fact]
    public void SaberSelectionReader_RetailRes2Gob_ParsesAll7SaberColors()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var res2 = GobArchive.FromFile(SteamRes2Path);
        string text = Encoding.ASCII.GetString(res2.ReadAllBytes("misc/sabers.dat"));

        IReadOnlyList<SaberSelectionEntry> sabers = SaberSelectionReader.Parse(text);

        Assert.Equal(7, sabers.Count);

        string[] expectedColorTokens = ["blue", "green", "yellow", "red", "orange", "purple", "dred"];
        foreach (string color in expectedColorTokens)
        {
            Assert.Contains(sabers, s =>
                s.SideMaterial.Equals($"saber{color}1.mat", StringComparison.OrdinalIgnoreCase) &&
                s.TipMaterial.Equals($"saber{color}0.mat", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(0, sabers[0].Index);
        Assert.Equal("sabB2.BM", sabers[0].PreviewBitmap);
    }

    private sealed class CaseSensitiveMemoryVfs : IVirtualFileSystem
    {
        private readonly Dictionary<string, string> _files;

        public CaseSensitiveMemoryVfs(Dictionary<string, string> files) => _files = files;

        public bool FileExists(string path) => _files.ContainsKey(path);

        public byte[] ReadAllBytes(string path) => Encoding.ASCII.GetBytes(ReadAllText(path));

        public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

        public string ReadAllText(string path) =>
            _files.TryGetValue(path, out string? content)
                ? content
                : throw new FileNotFoundException($"Missing mock asset: {path}");

        public void Dispose() { }
    }
}
