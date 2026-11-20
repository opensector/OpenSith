using System;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class ConvexSectorGeometryTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public ConvexSectorGeometryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestInwardNormalsForSector0Center()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var sec0 = level.Sectors[0];
        var center = sec0.Center;
        _output.WriteLine($"Sector 0 Center: {center}");

        int insideCount = 0;
        int totalSurfaces = sec0.Surfaces.Count;

        foreach (var surf in sec0.Surfaces)
        {
            if (surf.VertexIndices.Length == 0) continue;
            var v0 = level.Vertices[surf.VertexIndices[0]];
            float dist = Vector3.Dot(center - v0, surf.Normal);
            _output.WriteLine($"  Surf {surf.SurfaceIndex} (Adjoin {surf.AdjoinIndex}): dist = {dist:F3}, normal = {surf.Normal}");
            if (dist >= -0.05f) insideCount++;
        }

        _output.WriteLine($"Surfaces with center in front of inward normal: {insideCount} / {totalSurfaces}");
        Assert.True(insideCount >= totalSurfaces - 2); // almost all or all surfaces define the convex hull
    }
}
