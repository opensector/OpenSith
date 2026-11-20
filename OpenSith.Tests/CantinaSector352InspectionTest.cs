using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CantinaSector352InspectionTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public CantinaSector352InspectionTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectSector352()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;

        using var archive = GobArchive.FromFile(jk1Path);
        var entry = archive.Entries.First(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        string jklText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
        var level = JklReader.Parse(jklText);

        var sec352 = level.Sectors[352];
        _output.WriteLine($"SECTOR 352: {sec352.Surfaces.Count} surfaces, Center={sec352.Center}");

        for (int i = 0; i < sec352.Surfaces.Count; i++)
        {
            var s = sec352.Surfaces[i];
            float sMinX = float.MaxValue, sMaxX = float.MinValue;
            float sMinY = float.MaxValue, sMaxY = float.MinValue;
            float sMinZ = float.MaxValue, sMaxZ = float.MinValue;

            for (int v = 0; v < s.VertexIndices.Length; v++)
            {
                var pt = level.Vertices[s.VertexIndices[v]];
                sMinX = MathF.Min(sMinX, pt.X);
                sMaxX = MathF.Max(sMaxX, pt.X);
                sMinY = MathF.Min(sMinY, pt.Y);
                sMaxY = MathF.Max(sMaxY, pt.Y);
                sMinZ = MathF.Min(sMinZ, pt.Z);
                sMaxZ = MathF.Max(sMaxZ, pt.Z);
            }

            string pType = s.IsPortal ? $"PORTAL -> Sector {s.MirrorSector?.SectorIndex}" : "SOLID";
            _output.WriteLine($"Surf {s.SurfaceIndex,4}: Normal=({s.Normal.X:F3}, {s.Normal.Y:F3}, {s.Normal.Z:F3}) [{pType}]");
            _output.WriteLine($"   X=[{sMinX:F3}, {sMaxX:F3}], Y=[{sMinY:F3}, {sMaxY:F3}], Z=[{sMinZ:F3}, {sMaxZ:F3}]");
        }
    }
}
