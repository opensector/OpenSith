using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class JklReaderTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void ParseNarShaddaaLevel_MatchesRetailGeometry()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        var level = JklReader.Parse(text, "01narshadda");

        Assert.Equal("01narshadda", level.Name);
        Assert.True(level.Materials.Count > 500, $"Expected >500 materials, got {level.Materials.Count}");
        Assert.Contains("01narsh.cmp", level.ColorMaps);
        Assert.Contains("01redmap.cmp", level.ColorMaps);

        Assert.Equal(4360, level.Vertices.Count);
        Assert.Equal(4595, level.Surfaces.Count);
        Assert.Equal(462, level.Sectors.Count);
        Assert.Equal(352, level.Things.Count);

        // Check Sector 0
        var sector0 = level.Sectors[0];
        Assert.Equal(0, sector0.SectorIndex);
        Assert.Equal(30, sector0.Surfaces.Count);
        Assert.Equal(55, sector0.VertexIndices.Length);

        // Check Portals
        int portalCount = level.Surfaces.Count(s => s.IsPortal);
        Assert.True(portalCount > 500, $"Expected >500 portals, got {portalCount}");

        // Verify normal vectors are non-zero for textured surfaces
        var firstTextured = level.Surfaces.First(s => s.MaterialIndex >= 0);
        Assert.True(firstTextured.Normal.LengthSquared() > 0.5f);

        // Verify Kyle Katarn spawn point in Sector 339 (Cantina booth)
        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);
        Assert.True(spawn.IsPlayerSpawn);
        Assert.Equal(339, spawn.SectorIndex);
        Assert.Equal(-10.443433f, spawn.Position.X, 0.001f);
        Assert.Equal(-5.455094f, spawn.Position.Y, 0.001f);
        Assert.Equal(-1.129886f, spawn.Position.Z, 0.001f);
    }

    [Theory]
    [InlineData("jkl/02narshadda.jkl")]
    [InlineData("jkl/03katarn.jkl")]
    [InlineData("jkl/07yun.jkl")]
    public void ParseOtherRetailCampaignLevels_Succeeds(string levelPath)
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        if (!archive.Contains(levelPath)) return;

        string text = Encoding.ASCII.GetString(archive.ReadAllBytes(levelPath));
        var level = JklReader.Parse(text, levelPath);

        Assert.NotEmpty(level.Sectors);
        Assert.NotEmpty(level.Surfaces);
        Assert.NotEmpty(level.Vertices);
        Assert.NotEmpty(level.Things);
    }
}
