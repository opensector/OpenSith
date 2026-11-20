using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class JklCogSoundTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void ParseSyntheticJkl_SoundsCogScriptsAndCogs_Succeeds()
    {
        const string synthetic = """
            SECTION: SOUNDS
            World sounds 3
            walk01.wav
            jump01.wav
            0: land01.wav
            end

            SECTION: COGSCRIPTS
            World scripts 3
            0: kyle.cog
            1: 00_door.cog
            2: test_script.cog
            end

            SECTION: COGS
            World cogs 2
            #Num Script Symbol values
            0: 00_door.cog 4 -1 8.0 2.0
            1: test_script.cog 100 200 alpha.wav
            end
            """;

        var level = JklReader.Parse(synthetic, "synthetic");

        Assert.Equal(3, level.Sounds.Count);
        Assert.Equal("walk01.wav", level.Sounds[0]);
        Assert.Equal("jump01.wav", level.Sounds[1]);
        Assert.Equal("land01.wav", level.Sounds[2]);

        Assert.Equal(3, level.CogScripts.Count);
        Assert.Equal("kyle.cog", level.CogScripts[0]);
        Assert.Equal("00_door.cog", level.CogScripts[1]);
        Assert.Equal("test_script.cog", level.CogScripts[2]);

        Assert.Equal(2, level.Cogs.Count);
        Assert.Equal(0, level.Cogs[0].Index);
        Assert.Equal("00_door.cog", level.Cogs[0].ScriptName);
        Assert.Equal(["4", "-1", "8.0", "2.0"], level.Cogs[0].SymbolArgs);

        Assert.Equal(1, level.Cogs[1].Index);
        Assert.Equal("test_script.cog", level.Cogs[1].ScriptName);
        Assert.Equal(["100", "200", "alpha.wav"], level.Cogs[1].SymbolArgs);
    }

    [Fact]
    public void ParseNarShaddaa01_LoadsAllCogScriptsPlacedCogsAndSounds()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        var level = JklReader.Parse(text, "01narshadda");

        // Verify Sounds
        Assert.NotEmpty(level.Sounds);
        Assert.Equal(181, level.Sounds.Count);
        Assert.Equal("walkmetaldry01.wav", level.Sounds[0]);
        Assert.Contains("df_door1-1.wav", level.Sounds);
        Assert.Contains("funicularstart01.wav", level.Sounds);
        Assert.Contains("accomplish1.wav", level.Sounds);

        // Verify CogScripts: 65 scripts defined
        Assert.Equal(65, level.CogScripts.Count);
        Assert.Equal("kyle.cog", level.CogScripts[0]);
        Assert.Equal("actor_rb.cog", level.CogScripts[1]);
        Assert.Contains("00_door.cog", level.CogScripts);
        Assert.Contains("00_std_elev.cog", level.CogScripts);
        Assert.Contains("01_bartender.cog", level.CogScripts);
        Assert.Equal("pow_power.cog", level.CogScripts[64]);

        // Verify Placed Cogs: exactly 42 placed cogs (indices 0 to 41)
        Assert.Equal(42, level.Cogs.Count);

        // Cog 0: 00_door.cog
        var doorCog = level.Cogs[0];
        Assert.Equal(0, doorCog.Index);
        Assert.Equal("00_door.cog", doorCog.ScriptName);
        Assert.Equal(["4", "-1", "-1", "-1", "8.000000", "2.000000", "0.500000"], doorCog.SymbolArgs);

        // Cog 1: 00_std_elev.cog
        var elevCog = level.Cogs[1];
        Assert.Equal(1, elevCog.Index);
        Assert.Equal("00_std_elev.cog", elevCog.ScriptName);
        Assert.Equal(["839", "854", "6", "0.250000", "2.000000", "4.000000"], elevCog.SymbolArgs);

        // Cog 29: 01_bartender.cog
        var bartenderCog = level.Cogs.FirstOrDefault(c => c.ScriptName.Equals("01_bartender.cog", System.StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bartenderCog);
        Assert.Equal(29, bartenderCog.Index);
        Assert.Equal(["208", "214", "207", "350", "370", "353"], bartenderCog.SymbolArgs);

        // Verify other notable placements
        var lastCog = level.Cogs[^1];
        Assert.Equal(41, lastCog.Index);
        Assert.Equal("01_neonsign.cog", lastCog.ScriptName);
        Assert.Equal(["3413", "2.000000"], lastCog.SymbolArgs);
    }

    [Theory]
    [InlineData("jkl/02narshadda.jkl")]
    [InlineData("jkl/03katarn.jkl")]
    [InlineData("jkl/07yun.jkl")]
    public void ParseOtherRetailCampaignLevels_LoadsSoundsAndCogs(string levelPath)
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        if (!archive.Contains(levelPath)) return;

        string text = Encoding.ASCII.GetString(archive.ReadAllBytes(levelPath));
        var level = JklReader.Parse(text, levelPath);

        Assert.NotEmpty(level.Sounds);
        Assert.NotEmpty(level.CogScripts);
        Assert.NotEmpty(level.Cogs);
        Assert.All(level.Cogs, cog =>
        {
            Assert.NotEmpty(cog.ScriptName);
            Assert.NotNull(cog.SymbolArgs);
        });
    }
}
