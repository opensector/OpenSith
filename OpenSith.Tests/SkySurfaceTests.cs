using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Client;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class SkySurfaceTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public SkySurfaceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(LevelMeshRenderer.SurfaceFlagHorizonSky, true)]
    [InlineData(LevelMeshRenderer.SurfaceFlagCeilingSky, true)]
    [InlineData(LevelMeshRenderer.SurfaceFlagSkyMask, true)]
    [InlineData(0x404, true)]
    [InlineData(0x204, true)]
    [InlineData(0x205, true)]
    [InlineData(0x0, false)]
    [InlineData(0x1, false)]
    [InlineData(0x4, false)]
    [InlineData(LevelMeshRenderer.SurfaceFlagScrolling, false)]
    public void IsSkySurface_MatchesHorizonAndCeilingSkyFlags(int surfaceFlags, bool expectedIsSky)
    {
        var surface = new JklSurface { SurfaceFlags = surfaceFlags };
        Assert.Equal(expectedIsSky, LevelMeshRenderer.IsSkySurface(surface));
    }

    [Fact]
    public void IsSkySurface_NullSurfaceReturnsFalse()
    {
        Assert.False(LevelMeshRenderer.IsSkySurface(null!));
    }

    [Theory]
    [InlineData(LevelMeshRenderer.SurfaceFlagScrolling, true)]
    [InlineData(LevelMeshRenderer.SurfaceFlagScrolling | 0x4, true)]
    [InlineData(0x0, false)]
    [InlineData(LevelMeshRenderer.SurfaceFlagHorizonSky, false)]
    [InlineData(LevelMeshRenderer.SurfaceFlagCeilingSky, false)]
    public void IsScrollingSurface_MatchesScrollingFlag(int surfaceFlags, bool expectedIsScrolling)
    {
        var surface = new JklSurface { SurfaceFlags = surfaceFlags };
        Assert.Equal(expectedIsScrolling, LevelMeshRenderer.IsScrollingSurface(surface));
    }

    [Fact]
    public void ComputeVertexIntensity_SkySurfaceIsAlwaysFullbright()
    {
        var horizonSky = new JklSurface
        {
            SurfaceFlags = LevelMeshRenderer.SurfaceFlagHorizonSky | 0x4,
            ExtraLight = -0.5f,
            VertexIntensities = [0.0f, 0.05f, 0.2f]
        };

        var ceilingSky = new JklSurface
        {
            SurfaceFlags = LevelMeshRenderer.SurfaceFlagCeilingSky | 0x4,
            ExtraLight = 0.0f,
            VertexIntensities = [0.07f, 1.36f]
        };

        for (int i = 0; i < horizonSky.VertexIntensities.Length; i++)
        {
            Assert.Equal(1.0f, LevelMeshRenderer.ComputeVertexIntensity(horizonSky, i, sectorExtraLight: -0.8f));
        }

        for (int i = 0; i < ceilingSky.VertexIntensities.Length; i++)
        {
            Assert.Equal(1.0f, LevelMeshRenderer.ComputeVertexIntensity(ceilingSky, i, sectorExtraLight: 0.25f));
        }
    }

    [Fact]
    public void ComputeVertexIntensity_NonSkySurfaceRespectsVertexAndExtraLights()
    {
        var wall = new JklSurface
        {
            SurfaceFlags = 0x4,
            ExtraLight = 0.1f,
            VertexIntensities = [0.25f, 0.5f]
        };

        Assert.Equal(0.35f, LevelMeshRenderer.ComputeVertexIntensity(wall, 0), precision: 4);
        Assert.Equal(0.85f, LevelMeshRenderer.ComputeVertexIntensity(wall, 1, sectorExtraLight: 0.25f), precision: 4);
        Assert.Equal(0.0f, LevelMeshRenderer.ComputeVertexIntensity(wall, 0, sectorExtraLight: -1.0f), precision: 4);
        Assert.Equal(1.0f, LevelMeshRenderer.ComputeVertexIntensity(wall, 5), precision: 4);
    }

    [Fact]
    public void IsTranslucentSurface_SkyRemainsOpaqueBeforeWindows()
    {
        var skyWithTranslucentFlag = new JklSurface
        {
            SurfaceFlags = LevelMeshRenderer.SurfaceFlagCeilingSky,
            FaceFlags = 0x2,
            GeometryMode = 4,
            AdjoinIndex = 0
        };

        var glassWindow = new JklSurface
        {
            SurfaceFlags = 0x4,
            FaceFlags = 0x2,
            GeometryMode = 4,
            AdjoinIndex = 10
        };

        var solidWall = new JklSurface
        {
            SurfaceFlags = 0x4,
            FaceFlags = 0x0,
            GeometryMode = 4,
            AdjoinIndex = -1
        };

        Assert.False(LevelMeshRenderer.IsTranslucentSurface(skyWithTranslucentFlag));
        Assert.True(LevelMeshRenderer.IsTranslucentSurface(glassWindow));
        Assert.False(LevelMeshRenderer.IsTranslucentSurface(solidWall));
    }

    [Fact]
    public void AdvanceUvOffset_AdvancesAndWrapsWithoutPrecisionDrift()
    {
        var velocity = new Vector2(0.5f, -0.25f);

        Vector2 step1 = LevelMeshRenderer.AdvanceUvOffset(Vector2.Zero, velocity, 0.5f);
        Assert.Equal(0.25f, step1.X, precision: 5);
        Assert.Equal(-0.125f, step1.Y, precision: 5);

        // Zero or negative dt should not advance
        Assert.Equal(step1, LevelMeshRenderer.AdvanceUvOffset(step1, velocity, 0.0f));
        Assert.Equal(step1, LevelMeshRenderer.AdvanceUvOffset(step1, velocity, -0.1f));

        // Large step wraps modulo 1.0
        Vector2 wrapped = LevelMeshRenderer.AdvanceUvOffset(step1, velocity, 4.0f);
        Assert.InRange(wrapped.X, -1.0f, 1.0f);
        Assert.InRange(wrapped.Y, -1.0f, 1.0f);
        Assert.Equal(0.25f, wrapped.X, precision: 5);
        Assert.Equal(-0.125f, wrapped.Y, precision: 5);
    }

    [Fact]
    public void IsSkySurface_IdentifiesNarShaddaaSkySurfacesAndDistinguishesWalls()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string jklText = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(jklText, "01narshadda");

        var allSkyFlagged = level.Surfaces.Where(LevelMeshRenderer.IsSkySurface).ToList();
        var drawnSky = allSkyFlagged
            .Where(s => s.GeometryMode != 0 && s.MaterialIndex >= 0 && s.VertexIndices.Length >= 3)
            .ToList();
        var normalDrawnSurfaces = level.Surfaces
            .Where(s => !LevelMeshRenderer.IsSkySurface(s) && s.GeometryMode != 0 && s.MaterialIndex >= 0 && s.VertexIndices.Length >= 3)
            .ToList();

        _output.WriteLine($"01narshadda.jkl: {allSkyFlagged.Count} sky-flagged surfaces ({drawnSky.Count} drawn), {normalDrawnSurfaces.Count} drawn non-sky surfaces.");

        Assert.NotEmpty(drawnSky);
        Assert.NotEmpty(normalDrawnSurfaces);
        Assert.True(drawnSky.Count >= 50, $"Expected at least 50 drawn sky surfaces in Nar Shaddaa, found {drawnSky.Count}");

        // Every drawn sky surface must have 0x200 or 0x400 set, be opaque, and resolve to fullbright 1.0f
        bool foundDimJklSkyVertex = false;
        foreach (var sky in drawnSky)
        {
            Assert.NotEqual(0, sky.SurfaceFlags & LevelMeshRenderer.SurfaceFlagSkyMask);
            Assert.False(LevelMeshRenderer.IsTranslucentSurface(sky));

            string matName = level.Materials[sky.MaterialIndex];
            Assert.Equal("00starry.mat", matName, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < sky.VertexIndices.Length; i++)
            {
                if (i < sky.VertexIntensities.Length && sky.VertexIntensities[i] < 0.5f)
                    foundDimJklSkyVertex = true;

                Assert.Equal(1.0f, LevelMeshRenderer.ComputeVertexIntensity(sky, i));
            }
        }

        Assert.True(foundDimJklSkyVertex, "Expected Nar Shaddaa sky surfaces to contain dim raw JKL intensities that require fullbright override.");

        // Sector 0 (Cantina booth) walls/floors/ceilings and city backdrop walls must not be flagged as sky
        var cantinaSurfaces = level.Sectors[0].Surfaces;
        Assert.NotEmpty(cantinaSurfaces);
        Assert.All(cantinaSurfaces, s => Assert.False(LevelMeshRenderer.IsSkySurface(s)));

        var cityWalls = normalDrawnSurfaces
            .Where(s => string.Equals(level.Materials[s.MaterialIndex], "00wcity2.mat", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(cityWalls);
        Assert.All(cityWalls, s => Assert.False(LevelMeshRenderer.IsSkySurface(s)));
    }
}
