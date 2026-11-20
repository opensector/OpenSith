using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class JklWriterTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void Write_NullArguments_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => JklWriter.Write(null!));
        Assert.Throws<ArgumentNullException>(() => JklWriter.Write(new JklLevel(), null!));
        Assert.Throws<ArgumentNullException>(() => JklWriter.Write(null!, new StringWriter()));
    }

    [Fact]
    public void EmptyLevel_RoundTrip_Succeeds()
    {
        var level = new JklLevel { Name = "empty" };
        string serialized = JklWriter.Write(level);

        var parsed = JklReader.Parse(serialized, "empty");
        Assert.Empty(parsed.Vertices);
        Assert.Empty(parsed.TextureVertices);
        Assert.Empty(parsed.Surfaces);
        Assert.Empty(parsed.Sectors);
        Assert.Empty(parsed.Adjoins);
        Assert.Empty(parsed.Sounds);
        Assert.Empty(parsed.Materials);
        Assert.Empty(parsed.CogScripts);
        Assert.Empty(parsed.Cogs);
        Assert.Empty(parsed.Things);
        Assert.Empty(parsed.Templates);
    }

    [Fact]
    public void SyntheticLevel_RoundTrip_MatchesAllSections()
    {
        var original = new JklLevel
        {
            Name = "synthetic_test",
            Sounds = ["walk01.wav", "jump01.wav", "land01.wav"],
            Materials = ["hdback.mat", "bryvgnbk.mat", "wall01.mat"],
            ColorMaps = ["01narsh.cmp", "01redmap.cmp"],
            Vertices =
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 2f),
                new Vector3(1f, 0f, 2f),
                new Vector3(1f, 1f, 2f),
                new Vector3(0f, 1f, 2f)
            ],
            TextureVertices =
            [
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f)
            ],
            CogScripts = ["kyle.cog", "00_door.cog", "elev_switch.cog"],
            Cogs =
            [
                new JklCogPlacement
                {
                    Index = 0,
                    ScriptName = "00_door.cog",
                    SymbolArgs = ["4", "-1", "8.000000", "2.000000"]
                },
                new JklCogPlacement
                {
                    Index = 1,
                    ScriptName = "elev_switch.cog",
                    SymbolArgs = ["10", "20", "funicularstart01.wav"]
                }
            ],
            Adjoins =
            [
                new JklAdjoin
                {
                    AdjoinIndex = 0,
                    Flags = 1,
                    Mirror = 1,
                    Dist = 0.5f
                },
                new JklAdjoin
                {
                    AdjoinIndex = 1,
                    Flags = 1,
                    Mirror = 0,
                    Dist = 0.5f
                }
            ]
        };

        // Templates
        original.Templates["_actor"] = new JklTemplate
        {
            Name = "_actor",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "actor",
                ["health"] = "100.0"
            }
        };

        original.Templates["walkplayer"] = new JklTemplate
        {
            Name = "walkplayer",
            BasedOn = "_actor",
            Model3D = "ky.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "player",
                ["model3d"] = "ky.3do"
            }
        };

        // Surfaces
        var surf0 = new JklSurface
        {
            SurfaceIndex = 0,
            MaterialIndex = 0,
            SurfaceFlags = 0x4,
            FaceFlags = 0,
            GeometryMode = 4,
            LightingMode = 3,
            TextureMode = 1,
            AdjoinIndex = 0,
            ExtraLight = 0.1f,
            VertexIndices = [0, 1, 2, 3],
            TextureIndices = [0, 1, 2, 3],
            VertexIntensities = [0.5f, 0.6f, 0.7f, 0.8f],
            Normal = new Vector3(0f, 0f, 1f)
        };

        var surf1 = new JklSurface
        {
            SurfaceIndex = 1,
            MaterialIndex = 1,
            SurfaceFlags = 0x4,
            FaceFlags = 0,
            GeometryMode = 4,
            LightingMode = 3,
            TextureMode = 1,
            AdjoinIndex = 1,
            ExtraLight = 0.0f,
            VertexIndices = [4, 5, 6, 7],
            TextureIndices = [0, 1, 2, 3],
            VertexIntensities = [0.2f, 0.3f, 0.4f, 0.5f],
            Normal = new Vector3(0f, 0f, -1f)
        };

        original.Surfaces = [surf0, surf1];

        // Sectors
        var sec0 = new JklSector
        {
            SectorIndex = 0,
            Flags = 0x1000,
            AmbientLight = 0.25f,
            ExtraLight = 0.05f,
            ColorMapIndex = 0,
            Tint = new Vector3(0.1f, 0.2f, 0.3f),
            BoundBoxMin = new Vector3(0f, 0f, 0f),
            BoundBoxMax = new Vector3(1f, 1f, 1f),
            Center = new Vector3(0.5f, 0.5f, 0.5f),
            Radius = 1.0f,
            VertexIndices = [0, 1, 2, 3],
            FirstSurfaceIndex = 0,
            SurfaceCount = 1
        };

        var sec1 = new JklSector
        {
            SectorIndex = 1,
            Flags = 0x2000,
            AmbientLight = 0.35f,
            ExtraLight = 0.10f,
            ColorMapIndex = 1,
            Tint = new Vector3(0.0f, 0.0f, 0.0f),
            BoundBoxMin = new Vector3(0f, 0f, 1f),
            BoundBoxMax = new Vector3(1f, 1f, 2f),
            Center = new Vector3(0.5f, 0.5f, 1.5f),
            Radius = 1.0f,
            VertexIndices = [4, 5, 6, 7],
            FirstSurfaceIndex = 1,
            SurfaceCount = 1
        };

        original.Sectors = [sec0, sec1];

        // Things
        original.Things =
        [
            new JklThing
            {
                ThingIndex = 0,
                TemplateName = "walkplayer",
                Name = "kyle_start",
                Position = new Vector3(0.5f, 0.5f, 0.1f),
                PitchYawRoll = new Vector3(0f, 90f, 0f),
                SectorIndex = 0,
                ExtraArguments = "thingflags=0x20000401"
            },
            new JklThing
            {
                ThingIndex = 1,
                TemplateName = "_actor",
                Name = "enemy_1",
                Position = new Vector3(0.5f, 0.5f, 1.5f),
                PitchYawRoll = new Vector3(0f, 180f, 0f),
                SectorIndex = 1
            }
        ];

        // Write and Parse
        string jklText = JklWriter.Write(original);
        Assert.NotNull(jklText);
        Assert.NotEmpty(jklText);

        var roundtrip = JklReader.Parse(jklText, "synthetic_test");

        // Sounds
        Assert.Equal(original.Sounds.Count, roundtrip.Sounds.Count);
        for (int i = 0; i < original.Sounds.Count; i++)
        {
            Assert.Equal(original.Sounds[i], roundtrip.Sounds[i]);
        }

        // Materials
        Assert.Equal(original.Materials.Count, roundtrip.Materials.Count);
        for (int i = 0; i < original.Materials.Count; i++)
        {
            Assert.Equal(original.Materials[i], roundtrip.Materials[i]);
        }

        // ColorMaps
        Assert.Equal(original.ColorMaps.Count, roundtrip.ColorMaps.Count);
        for (int i = 0; i < original.ColorMaps.Count; i++)
        {
            Assert.Equal(original.ColorMaps[i], roundtrip.ColorMaps[i]);
        }

        // Vertices
        Assert.Equal(original.Vertices.Count, roundtrip.Vertices.Count);
        for (int i = 0; i < original.Vertices.Count; i++)
        {
            Assert.Equal(original.Vertices[i].X, roundtrip.Vertices[i].X, 0.0001f);
            Assert.Equal(original.Vertices[i].Y, roundtrip.Vertices[i].Y, 0.0001f);
            Assert.Equal(original.Vertices[i].Z, roundtrip.Vertices[i].Z, 0.0001f);
        }

        // Texture Vertices
        Assert.Equal(original.TextureVertices.Count, roundtrip.TextureVertices.Count);
        for (int i = 0; i < original.TextureVertices.Count; i++)
        {
            Assert.Equal(original.TextureVertices[i].X, roundtrip.TextureVertices[i].X, 0.0001f);
            Assert.Equal(original.TextureVertices[i].Y, roundtrip.TextureVertices[i].Y, 0.0001f);
        }

        // Surfaces
        Assert.Equal(original.Surfaces.Count, roundtrip.Surfaces.Count);
        for (int i = 0; i < original.Surfaces.Count; i++)
        {
            var sOrig = original.Surfaces[i];
            var sRound = roundtrip.Surfaces[i];
            Assert.Equal(sOrig.SurfaceIndex, sRound.SurfaceIndex);
            Assert.Equal(sOrig.MaterialIndex, sRound.MaterialIndex);
            Assert.Equal(sOrig.SurfaceFlags, sRound.SurfaceFlags);
            Assert.Equal(sOrig.FaceFlags, sRound.FaceFlags);
            Assert.Equal(sOrig.GeometryMode, sRound.GeometryMode);
            Assert.Equal(sOrig.LightingMode, sRound.LightingMode);
            Assert.Equal(sOrig.TextureMode, sRound.TextureMode);
            Assert.Equal(sOrig.AdjoinIndex, sRound.AdjoinIndex);
            Assert.Equal(sOrig.ExtraLight, sRound.ExtraLight, 0.0001f);
            Assert.Equal(sOrig.VertexIndices, sRound.VertexIndices);
            Assert.Equal(sOrig.TextureIndices, sRound.TextureIndices);
            for (int v = 0; v < sOrig.VertexIntensities.Length; v++)
            {
                Assert.Equal(sOrig.VertexIntensities[v], sRound.VertexIntensities[v], 0.0001f);
            }
            Assert.Equal(sOrig.Normal.X, sRound.Normal.X, 0.0001f);
            Assert.Equal(sOrig.Normal.Y, sRound.Normal.Y, 0.0001f);
            Assert.Equal(sOrig.Normal.Z, sRound.Normal.Z, 0.0001f);
        }

        // Adjoins
        Assert.Equal(original.Adjoins.Count, roundtrip.Adjoins.Count);
        for (int i = 0; i < original.Adjoins.Count; i++)
        {
            var aOrig = original.Adjoins[i];
            var aRound = roundtrip.Adjoins[i];
            Assert.Equal(aOrig.AdjoinIndex, aRound.AdjoinIndex);
            Assert.Equal(aOrig.Flags, aRound.Flags);
            Assert.Equal(aOrig.Mirror, aRound.Mirror);
            Assert.Equal(aOrig.Dist, aRound.Dist, 0.0001f);
        }

        // Sectors
        Assert.Equal(original.Sectors.Count, roundtrip.Sectors.Count);
        for (int i = 0; i < original.Sectors.Count; i++)
        {
            var secOrig = original.Sectors[i];
            var secRound = roundtrip.Sectors[i];
            Assert.Equal(secOrig.SectorIndex, secRound.SectorIndex);
            Assert.Equal(secOrig.Flags, secRound.Flags);
            Assert.Equal(secOrig.AmbientLight, secRound.AmbientLight, 0.0001f);
            Assert.Equal(secOrig.ExtraLight, secRound.ExtraLight, 0.0001f);
            Assert.Equal(secOrig.ColorMapIndex, secRound.ColorMapIndex);
            Assert.Equal(secOrig.Tint.X, secRound.Tint.X, 0.0001f);
            Assert.Equal(secOrig.Tint.Y, secRound.Tint.Y, 0.0001f);
            Assert.Equal(secOrig.Tint.Z, secRound.Tint.Z, 0.0001f);
            Assert.Equal(secOrig.BoundBoxMin.X, secRound.BoundBoxMin.X, 0.0001f);
            Assert.Equal(secOrig.BoundBoxMax.X, secRound.BoundBoxMax.X, 0.0001f);
            Assert.Equal(secOrig.Center.X, secRound.Center.X, 0.0001f);
            Assert.Equal(secOrig.Radius, secRound.Radius, 0.0001f);
            Assert.Equal(secOrig.VertexIndices, secRound.VertexIndices);
            Assert.Equal(secOrig.FirstSurfaceIndex, secRound.FirstSurfaceIndex);
            Assert.Equal(secOrig.SurfaceCount, secRound.SurfaceCount);
            Assert.Equal(secOrig.SurfaceCount, secRound.Surfaces.Count);
        }

        // Portal linkage
        Assert.NotNull(roundtrip.Surfaces[0].MirrorSurface);
        Assert.Equal(1, roundtrip.Surfaces[0].MirrorSurface!.SurfaceIndex);
        Assert.NotNull(roundtrip.Surfaces[0].MirrorSector);
        Assert.Equal(1, roundtrip.Surfaces[0].MirrorSector!.SectorIndex);

        // CogScripts
        Assert.Equal(original.CogScripts.Count, roundtrip.CogScripts.Count);
        for (int i = 0; i < original.CogScripts.Count; i++)
        {
            Assert.Equal(original.CogScripts[i], roundtrip.CogScripts[i]);
        }

        // Cogs
        Assert.Equal(original.Cogs.Count, roundtrip.Cogs.Count);
        for (int i = 0; i < original.Cogs.Count; i++)
        {
            var cogOrig = original.Cogs[i];
            var cogRound = roundtrip.Cogs[i];
            Assert.Equal(cogOrig.Index, cogRound.Index);
            Assert.Equal(cogOrig.ScriptName, cogRound.ScriptName);
            Assert.Equal(cogOrig.SymbolArgs, cogRound.SymbolArgs);
        }

        // Templates & 3DO resolution
        Assert.True(roundtrip.Templates.ContainsKey("walkplayer"));
        Assert.Equal("ky.3do", roundtrip.ResolveModel3D("walkplayer"));

        // Things
        Assert.Equal(original.Things.Count, roundtrip.Things.Count);
        for (int i = 0; i < original.Things.Count; i++)
        {
            var tOrig = original.Things[i];
            var tRound = roundtrip.Things[i];
            Assert.Equal(tOrig.ThingIndex, tRound.ThingIndex);
            Assert.Equal(tOrig.TemplateName, tRound.TemplateName);
            Assert.Equal(tOrig.Name, tRound.Name);
            Assert.Equal(tOrig.Position.X, tRound.Position.X, 0.0001f);
            Assert.Equal(tOrig.Position.Y, tRound.Position.Y, 0.0001f);
            Assert.Equal(tOrig.Position.Z, tRound.Position.Z, 0.0001f);
            Assert.Equal(tOrig.PitchYawRoll.X, tRound.PitchYawRoll.X, 0.0001f);
            Assert.Equal(tOrig.PitchYawRoll.Y, tRound.PitchYawRoll.Y, 0.0001f);
            Assert.Equal(tOrig.PitchYawRoll.Z, tRound.PitchYawRoll.Z, 0.0001f);
            Assert.Equal(tOrig.SectorIndex, tRound.SectorIndex);
            Assert.Equal(tOrig.ExtraArguments, tRound.ExtraArguments);
        }

        // Player Spawn
        var spawn = roundtrip.GetPlayerSpawn();
        Assert.NotNull(spawn);
        Assert.True(spawn.IsPlayerSpawn);
        Assert.Equal(0, spawn.SectorIndex);
        Assert.Equal("ky.3do", spawn.Model3D);
    }

    [Fact]
    public void Retail_01NarShaddaa_RoundTrip_MatchesRetailGeometryAndEntities()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        var original = JklReader.Parse(text, "01narshadda");

        // Serialize back to text
        string serialized = JklWriter.Write(original);

        // Parse serialized JKL
        var roundtrip = JklReader.Parse(serialized, "01narshadda");

        // Assert sector count, vertex count, surface count, adjoin count, thing count, cog count
        Assert.Equal(original.Sectors.Count, roundtrip.Sectors.Count);
        Assert.Equal(original.Vertices.Count, roundtrip.Vertices.Count);
        Assert.Equal(original.TextureVertices.Count, roundtrip.TextureVertices.Count);
        Assert.Equal(original.Surfaces.Count, roundtrip.Surfaces.Count);
        Assert.Equal(original.Adjoins.Count, roundtrip.Adjoins.Count);
        Assert.Equal(original.Things.Count, roundtrip.Things.Count);
        Assert.Equal(original.Cogs.Count, roundtrip.Cogs.Count);
        Assert.Equal(original.CogScripts.Count, roundtrip.CogScripts.Count);
        Assert.Equal(original.Sounds.Count, roundtrip.Sounds.Count);
        Assert.Equal(original.Materials.Count, roundtrip.Materials.Count);

        // Retail expected numbers
        Assert.Equal(462, roundtrip.Sectors.Count);
        Assert.Equal(4360, roundtrip.Vertices.Count);
        Assert.Equal(10574, roundtrip.TextureVertices.Count);
        Assert.Equal(4595, roundtrip.Surfaces.Count);
        Assert.Equal(1206, roundtrip.Adjoins.Count);
        Assert.Equal(352, roundtrip.Things.Count);
        Assert.Equal(42, roundtrip.Cogs.Count);
        Assert.Equal(65, roundtrip.CogScripts.Count);
        Assert.Equal(181, roundtrip.Sounds.Count);
        Assert.Equal(528, roundtrip.Materials.Count);

        // Player spawn verification
        var origSpawn = original.GetPlayerSpawn();
        var roundSpawn = roundtrip.GetPlayerSpawn();

        Assert.NotNull(origSpawn);
        Assert.NotNull(roundSpawn);
        Assert.True(roundSpawn.IsPlayerSpawn);
        Assert.Equal(origSpawn.SectorIndex, roundSpawn.SectorIndex);
        Assert.Equal(339, roundSpawn.SectorIndex);
        Assert.Equal(origSpawn.Position.X, roundSpawn.Position.X, 0.001f);
        Assert.Equal(origSpawn.Position.Y, roundSpawn.Position.Y, 0.001f);
        Assert.Equal(origSpawn.Position.Z, roundSpawn.Position.Z, 0.001f);
        Assert.Equal(origSpawn.PitchYawRoll.X, roundSpawn.PitchYawRoll.X, 0.001f);
        Assert.Equal(origSpawn.PitchYawRoll.Y, roundSpawn.PitchYawRoll.Y, 0.001f);
        Assert.Equal(origSpawn.PitchYawRoll.Z, roundSpawn.PitchYawRoll.Z, 0.001f);
        Assert.Equal("ky.3do", roundSpawn.Model3D);

        // Spot-check Sector 0
        var sec0Orig = original.Sectors[0];
        var sec0Round = roundtrip.Sectors[0];
        Assert.Equal(sec0Orig.SectorIndex, sec0Round.SectorIndex);
        Assert.Equal(sec0Orig.Surfaces.Count, sec0Round.Surfaces.Count);
        Assert.Equal(sec0Orig.VertexIndices.Length, sec0Round.VertexIndices.Length);
        Assert.Equal(sec0Orig.AmbientLight, sec0Round.AmbientLight, 0.001f);
        Assert.Equal(sec0Orig.Center.X, sec0Round.Center.X, 0.001f);
        Assert.Equal(sec0Orig.BoundBoxMin.X, sec0Round.BoundBoxMin.X, 0.001f);

        // Spot-check Cog 0 (door)
        Assert.Equal(original.Cogs[0].ScriptName, roundtrip.Cogs[0].ScriptName);
        Assert.Equal(original.Cogs[0].SymbolArgs, roundtrip.Cogs[0].SymbolArgs);
    }

    [Theory]
    [InlineData("jkl/02narshadda.jkl")]
    [InlineData("jkl/03katarn.jkl")]
    [InlineData("jkl/07yun.jkl")]
    public void Retail_OtherCampaignLevels_RoundTrip_Succeeds(string levelPath)
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        if (!archive.Contains(levelPath)) return;

        string text = Encoding.ASCII.GetString(archive.ReadAllBytes(levelPath));
        var original = JklReader.Parse(text, levelPath);

        string serialized = JklWriter.Write(original);
        var roundtrip = JklReader.Parse(serialized, levelPath);

        Assert.Equal(original.Sectors.Count, roundtrip.Sectors.Count);
        Assert.Equal(original.Vertices.Count, roundtrip.Vertices.Count);
        Assert.Equal(original.Surfaces.Count, roundtrip.Surfaces.Count);
        Assert.Equal(original.Adjoins.Count, roundtrip.Adjoins.Count);
        Assert.Equal(original.Things.Count, roundtrip.Things.Count);
        Assert.Equal(original.Cogs.Count, roundtrip.Cogs.Count);
    }
}
