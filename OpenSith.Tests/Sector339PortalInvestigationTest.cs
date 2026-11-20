using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class Sector339PortalInvestigationTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public Sector339PortalInvestigationTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectAllSurfacesOfSector339()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var sec339 = level.Sectors[339];
        _output.WriteLine($"Sector 339 Surfaces: {sec339.Surfaces.Count}");

        foreach (var s in sec339.Surfaces)
        {
            _output.WriteLine($"\nSurface {s.SurfaceIndex}: Mat={s.MaterialIndex}, Adjoin={s.AdjoinIndex}, Normal={s.Normal}, IsPortal={s.IsPortal}");
            if (s.IsPortal)
            {
                _output.WriteLine($"  Mirror Sector: {s.MirrorSector?.SectorIndex}, Mirror Surf: {s.MirrorSurface?.SurfaceIndex}");
            }
            _output.WriteLine($"  Verts ({s.VertexIndices.Length}):");
            for (int i = 0; i < s.VertexIndices.Length; i++)
            {
                var v = level.Vertices[s.VertexIndices[i]];
                _output.WriteLine($"    [{i}] (idx {s.VertexIndices[i]}): {v}");
            }
        }
    }
}
