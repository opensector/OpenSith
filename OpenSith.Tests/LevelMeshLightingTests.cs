using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Client;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class LevelMeshLightingTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public LevelMeshLightingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ComputeVertexIntensity_ClampsOverbrightAndNegativeValuesToUnitRange()
    {
        var surface = new JklSurface
        {
            SurfaceFlags = 0x4,
            ExtraLight = 0.0f,
            VertexIntensities = [70.44f, 100.0f, -0.5f, 0.42f]
        };

        Assert.Equal(1.0f, LevelMeshRenderer.ComputeVertexIntensity(surface, 0), precision: 4);
        Assert.Equal(1.0f, LevelMeshRenderer.ComputeVertexIntensity(surface, 1), precision: 4);
        Assert.Equal(0.0f, LevelMeshRenderer.ComputeVertexIntensity(surface, 2), precision: 4);
        Assert.Equal(0.42f, LevelMeshRenderer.ComputeVertexIntensity(surface, 3), precision: 4);

        // ExtraLight and sectorExtraLight are also clamped to [0.0f, 1.0f]
        surface.ExtraLight = 0.75f;
        Assert.Equal(1.0f, LevelMeshRenderer.ComputeVertexIntensity(surface, 3, sectorExtraLight: 0.25f), precision: 4);
        Assert.Equal(0.0f, LevelMeshRenderer.ComputeVertexIntensity(surface, 3, sectorExtraLight: -2.0f), precision: 4);
    }

    [Fact]
    public void MawJkl_AllDrawnSurfacesClampPrebakedIntensitiesToUnitRange()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string jklText = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/15maw.jkl"));
        var level = JklReader.Parse(jklText, "15maw");

        Assert.NotEmpty(level.Surfaces);

        var surfaceToSectorExtraLight = new Dictionary<int, float>();
        foreach (var sector in level.Sectors)
        {
            foreach (var secSurface in sector.Surfaces)
            {
                surfaceToSectorExtraLight[secSurface.SurfaceIndex] = sector.ExtraLight;
            }
        }

        // Surface 0 in 15maw.jkl has raw pre-baked vertex intensities > 70.0 that previously saturated textures to white
        var surface0 = level.Surfaces[0];
        Assert.Contains(surface0.VertexIntensities, v => v > 70.0f);
        float surface0SectorExtraLight = surfaceToSectorExtraLight.GetValueOrDefault(surface0.SurfaceIndex);
        for (int i = 0; i < surface0.VertexIndices.Length; i++)
        {
            float clamped = LevelMeshRenderer.ComputeVertexIntensity(surface0, i, surface0SectorExtraLight);
            Assert.InRange(clamped, 0.0f, 1.0f);
        }

        var drawnSurfaces = level.Surfaces
            .Where(s => s.GeometryMode != 0 && s.MaterialIndex >= 0 && s.VertexIndices.Length >= 3)
            .ToList();

        Assert.NotEmpty(drawnSurfaces);

        int overbrightRawVertices = 0;
        foreach (var surface in drawnSurfaces)
        {
            float sectorExtraLight = surfaceToSectorExtraLight.GetValueOrDefault(surface.SurfaceIndex);

            for (int i = 0; i < surface.VertexIndices.Length; i++)
            {
                if (i < surface.VertexIntensities.Length && surface.VertexIntensities[i] > 1.0f)
                    overbrightRawVertices++;

                float intensity = LevelMeshRenderer.ComputeVertexIntensity(surface, i, sectorExtraLight);
                Assert.InRange(intensity, 0.0f, 1.0f);
            }
        }

        _output.WriteLine($"15maw.jkl: checked {drawnSurfaces.Count} drawn surfaces ({overbrightRawVertices} vertices had raw intensity > 1.0).");
        Assert.True(overbrightRawVertices > 0, "Expected 15maw.jkl to contain overbright raw JKL vertex intensities > 1.0.");
    }
}
