using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class EpisodeTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void Parse_SyntheticEpisode_ParsesMetadataEntriesAndNavigation()
    {
        const string sample = """
            # Leading comment
            "Test Campaign"

            TYPE 1 # single-player
            SEQ 5

            10: 1 1 CUT   intro.smk   0 0 20 -1
            20: 1 1 LEVEL 01start.jkl 0 0 30 -1 # Level 1: Start
            30: 1 1 DECIDE choice.smk 0 0 40 50 # Choose Path
            40: 1 2 LEVEL 02light.jkl 22 0 -1 -1 # Level 2A: Light
            50: 1 2 LEVEL 02dark.jkl  0 27 -1 -1 # Level 2B: Dark

            end
            """;

        var episode = EpisodeDefinition.Parse(sample);

        Assert.Equal("Test Campaign", episode.Title);
        Assert.Equal(1, episode.Type);
        Assert.Equal(5, episode.SequenceCount);
        Assert.Equal(5, episode.Entries.Count);

        var cut = episode.FindEntryByLine(10);
        Assert.NotNull(cut);
        Assert.Equal(EpisodeEntryType.Cut, cut.EntryType);
        Assert.Equal("intro.smk", cut.DisplayName);

        var start = episode.FindLevelEntry("jkl/01START");
        Assert.NotNull(start);
        Assert.Equal("Level 1: Start", start.DisplayName);
        Assert.Equal("01start.jkl", start.FileName);

        var levels = episode.GetPlayableLevels();
        Assert.Equal(3, levels.Count);

        var nextLight = episode.GetNextLevelAfter("01start.jkl", isLightSide: true);
        Assert.NotNull(nextLight);
        Assert.Equal("02light.jkl", nextLight.FileName);
        Assert.Equal(22, nextLight.LightPower);

        var nextDark = episode.GetNextLevelAfter("01start", isLightSide: false);
        Assert.NotNull(nextDark);
        Assert.Equal("02dark.jkl", nextDark.FileName);
        Assert.Equal(27, nextDark.DarkPower);

        Assert.Null(episode.GetNextLevelAfter("02light.jkl"));
        Assert.Null(episode.GetNextLevelAfter("nonexistent.jkl"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"Title\"\nBADTYPE 1\nSEQ 0\nend")]
    [InlineData("\"Title\"\nTYPE 1\nSEQ 2\n10: 1 1 CUT a.smk 0 0 -1 -1\nend")]
    [InlineData("\"Title\"\nTYPE 1\nSEQ 1\n10: 1 1 CUT a.smk 0 0 -1 -1")]
    [InlineData("\"Title\"\nTYPE 1\nSEQ 1\n10: 1 1 BOGUS a.smk 0 0 -1 -1\nend")]
    public void Parse_MalformedEpisode_ThrowsFormatException(string invalidText)
    {
        Assert.Throws<FormatException>(() => EpisodeDefinition.Parse(invalidText));
    }

    [Fact]
    public void Parse_RetailEpisodeJk_MatchesCampaignStructureAndProgression()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("episode.jk"));

        var episode = EpisodeDefinition.Parse(text);

        Assert.Equal("The Force Within", episode.Title);
        Assert.Equal(1, episode.Type);
        Assert.Equal(55, episode.SequenceCount);
        Assert.Equal(55, episode.Entries.Count);

        // Verify optional comment extraction and fallback
        var yun = episode.FindLevelEntry("07yun.jkl");
        Assert.NotNull(yun);
        Assert.Equal("Level 7: Yun [Pull]", yun.DisplayName);
        Assert.Equal(24, yun.LightPower);

        var introCut = episode.FindEntryByLine(10);
        Assert.NotNull(introCut);
        Assert.Equal(EpisodeEntryType.Cut, introCut.EntryType);
        Assert.Equal("03-04a.smk", introCut.DisplayName);

        // Case-insensitive lookup with/without jkl/ prefix or .jkl extension
        Assert.Same(yun, episode.FindLevelEntry("jkl/07YUN.JKL"));
        Assert.Same(yun, episode.FindLevelEntry("07yun"));

        // Linear progression: 01narshadda -> 02narshadda -> 03katarn
        var level2 = episode.GetNextLevelAfter("01narshadda.jkl");
        Assert.NotNull(level2);
        Assert.Equal("02narshadda.jkl", level2.FileName);

        var level3 = episode.GetNextLevelAfter(level2.FileName);
        Assert.NotNull(level3);
        Assert.Equal("03katarn.jkl", level3.FileName);

        // Branching progression at Decide node after 15maw.jkl
        var lightLevel15 = episode.GetNextLevelAfter("15maw.jkl", isLightSide: true);
        Assert.NotNull(lightLevel15);
        Assert.Equal("16aEscapeShip.jkl", lightLevel15.FileName);

        var darkLevel15 = episode.GetNextLevelAfter("15maw.jkl", isLightSide: false);
        Assert.NotNull(darkLevel15);
        Assert.Equal("16bEscapeShip.jkl", darkLevel15.FileName);

        // End of both campaign paths
        Assert.Null(episode.GetNextLevelAfter("21ajerec.jkl", isLightSide: true));
        Assert.Null(episode.GetNextLevelAfter("21bjerec.jkl", isLightSide: false));
    }

    [Fact]
    public void Parse_RetailEpisodeJk_All25UniqueLevelsLoadWithValidGeometryAndSpawn()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string episodeText = Encoding.ASCII.GetString(archive.ReadAllBytes("episode.jk"));
        var episode = EpisodeDefinition.Parse(episodeText);

        var uniqueLevelFiles = episode.GetPlayableLevels()
            .Select(e => e.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(25, uniqueLevelFiles.Count);

        foreach (string jklFile in uniqueLevelFiles)
        {
            string gobPath = $"jkl/{jklFile}";
            string jklText = Encoding.ASCII.GetString(archive.ReadAllBytes(gobPath));
            var level = JklReader.Parse(jklText, Path.GetFileNameWithoutExtension(jklFile));

            Assert.True(level.Sectors.Count > 0, $"{jklFile}: expected Sectors.Count > 0");
            Assert.True(level.ColorMaps.Count > 0, $"{jklFile}: expected ColorMaps.Count > 0");
            Assert.NotNull(level.GetPlayerSpawn());
        }
    }
}
