using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;

namespace OpenSith.Tests;

public class ThingFlagsTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void ParseThings_ParsesHexDecimalAndExtraProperties_WhilePreservingExtraArguments()
    {
        string jkl = """
            Section: Things
            World things 3
            0:	walkplayer	kyle	0.0	0.0	0.0	0.0	90.0	0.0	0	thingflags=0x20000401 light=0.75 size=0.15 movesize=0.12
            1:	_actor	enemy	1.0	2.0	3.0	0.0	0.0	0.0	0	thingflags=1024 numframes=2 frame=(1/2/3:0/0/0) frame=(4/5/6:0/90/0)
            2:	_decor	highbit	0.0	0.0	0.0	0.0	0.0	0.0	0	thingflags=0x80000000
            end
            """;

        var level = JklReader.Parse(jkl, "synthetic");
        Assert.Equal(3, level.Things.Count);

        var t0 = level.Things[0];
        Assert.Equal(0x20000401u, t0.ThingFlags);
        Assert.True(t0.Flags.HasFlag(SithThingFlags.EmitLight));
        Assert.True(t0.Flags.HasFlag(SithThingFlags.Captured));
        Assert.True(t0.Flags.HasFlag(SithThingFlags.Splashes));
        Assert.Equal(0.75f, t0.Light);
        Assert.Equal(0.15f, t0.Size);
        Assert.Equal(0.12f, t0.MoveSize);
        Assert.Equal("thingflags=0x20000401 light=0.75 size=0.15 movesize=0.12", t0.ExtraArguments);

        var t1 = level.Things[1];
        Assert.Equal(1024u, t1.ThingFlags);
        Assert.True(t1.Flags.HasFlag(SithThingFlags.Captured));
        Assert.Equal(2, t1.NumFrames);
        Assert.Equal(2, t1.Frames.Count);
        Assert.Equal("(1/2/3:0/0/0)", t1.Frames[0]);
        Assert.Equal("(4/5/6:0/90/0)", t1.Frames[1]);

        var t2 = level.Things[2];
        Assert.Equal(0x80000000u, t2.ThingFlags);
        Assert.True(t2.Flags.HasFlag(SithThingFlags.Bit80000000));
    }

    [Fact]
    public void ShouldSpawnForDifficulty_MatchesOpenJkdf2BitmaskLogic()
    {
        // No difficulty or mode restrictions
        Assert.True(JklThing.ShouldSpawnForDifficulty(0u, difficulty: 0, isMultiplayer: false));
        Assert.True(JklThing.ShouldSpawnForDifficulty(0u, difficulty: 1, isMultiplayer: false));
        Assert.True(JklThing.ShouldSpawnForDifficulty(0u, difficulty: 2, isMultiplayer: false));
        Assert.True(JklThing.ShouldSpawnForDifficulty(0u, difficulty: 0, isMultiplayer: true));

        // NoEasy (0x1000)
        uint noEasy = (uint)SithThingFlags.NoEasy;
        Assert.False(JklThing.ShouldSpawnForDifficulty(noEasy, difficulty: 0));
        Assert.True(JklThing.ShouldSpawnForDifficulty(noEasy, difficulty: 1));
        Assert.True(JklThing.ShouldSpawnForDifficulty(noEasy, difficulty: 2));

        // NoMedium (0x2000)
        uint noMedium = (uint)SithThingFlags.NoMedium;
        Assert.True(JklThing.ShouldSpawnForDifficulty(noMedium, difficulty: 0));
        Assert.False(JklThing.ShouldSpawnForDifficulty(noMedium, difficulty: 1));
        Assert.True(JklThing.ShouldSpawnForDifficulty(noMedium, difficulty: 2));

        // NoHard (0x4000)
        uint noHard = (uint)SithThingFlags.NoHard;
        Assert.True(JklThing.ShouldSpawnForDifficulty(noHard, difficulty: 0));
        Assert.True(JklThing.ShouldSpawnForDifficulty(noHard, difficulty: 1));
        Assert.False(JklThing.ShouldSpawnForDifficulty(noHard, difficulty: 2));

        // NoMultiplayer (0x8000) vs NoSingleplayer (0x10000)
        uint noMp = (uint)SithThingFlags.NoMultiplayer;
        Assert.True(JklThing.ShouldSpawnForDifficulty(noMp, difficulty: 1, isMultiplayer: false));
        Assert.False(JklThing.ShouldSpawnForDifficulty(noMp, difficulty: 1, isMultiplayer: true));

        uint noSp = (uint)SithThingFlags.NoSingleplayer;
        Assert.False(JklThing.ShouldSpawnForDifficulty(noSp, difficulty: 1, isMultiplayer: false));
        Assert.True(JklThing.ShouldSpawnForDifficulty(noSp, difficulty: 1, isMultiplayer: true));
    }

    [Fact]
    public void JklWriter_RoundTripsThingFlags_WhenExtraArgumentsEmpty()
    {
        var level = new JklLevel();
        level.Things.Add(new JklThing
        {
            ThingIndex = 0,
            TemplateName = "walkplayer",
            Name = "kyle",
            SectorIndex = 0,
            ThingFlags = (uint)(SithThingFlags.Disabled | SithThingFlags.Captured)
        });

        string written = JklWriter.Write(level);
        var reparsed = JklReader.Parse(written, "roundtrip");

        Assert.Single(reparsed.Things);
        Assert.Equal((uint)(SithThingFlags.Disabled | SithThingFlags.Captured), reparsed.Things[0].ThingFlags);
        Assert.True(reparsed.Things[0].IsDisabled);
    }

    [Fact]
    public void Retail_01NarShaddaa_MpWalkplayersHaveDisabledFlag_WhileThing0DoesNot()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var thing0 = level.Things.First(t => t.ThingIndex == 0);
        Assert.True(thing0.IsPlayerSpawn);
        Assert.False(thing0.IsDisabled);
        Assert.Equal(0u, thing0.ThingFlags & (uint)SithThingFlags.Disabled);

        for (int idx = 166; idx <= 176; idx++)
        {
            var mpSpawn = level.Things.First(t => t.ThingIndex == idx);
            Assert.True(mpSpawn.IsPlayerSpawn, $"Thing {idx} should be a walkplayer spawn");
            Assert.True(mpSpawn.IsDisabled, $"Thing {idx} should have SITH_TF_DISABLED (0x80000)");
            Assert.True(mpSpawn.Flags.HasFlag(SithThingFlags.Disabled));
        }
    }

    [Fact]
    public void Retail_01NarShaddaa_DifficultyGatedThings_FilterAccurately()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        // Thing 221: thingflags=0x6400 (NoHard | NoMedium | Captured) -> Easy only
        var t221 = level.Things.First(t => t.ThingIndex == 221);
        Assert.Equal(0x6400u, t221.ThingFlags);
        Assert.True(t221.ShouldSpawnForDifficulty(0));
        Assert.False(t221.ShouldSpawnForDifficulty(1));
        Assert.False(t221.ShouldSpawnForDifficulty(2));

        // Thing 117: thingflags=0x20003400 (Splashes | NoMedium | NoEasy | Captured) -> Hard only
        var t117 = level.Things.First(t => t.ThingIndex == 117);
        Assert.Equal(0x20003400u, t117.ThingFlags);
        Assert.False(t117.ShouldSpawnForDifficulty(0));
        Assert.False(t117.ShouldSpawnForDifficulty(1));
        Assert.True(t117.ShouldSpawnForDifficulty(2));

        // Thing 195: thingflags=0x20001400 (Splashes | NoEasy | Captured) -> Medium and Hard only
        var t195 = level.Things.First(t => t.ThingIndex == 195);
        Assert.Equal(0x20001400u, t195.ThingFlags);
        Assert.False(t195.ShouldSpawnForDifficulty(0));
        Assert.True(t195.ShouldSpawnForDifficulty(1));
        Assert.True(t195.ShouldSpawnForDifficulty(2));
    }
}
