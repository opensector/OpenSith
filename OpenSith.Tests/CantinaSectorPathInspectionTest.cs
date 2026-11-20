using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CantinaSectorPathInspectionTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public CantinaSectorPathInspectionTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectCantinaSectorsAndAdjoins()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;

        using var archive = GobArchive.FromFile(jk1Path);
        var entry = archive.Entries.First(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        string jklText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
        var level = JklReader.Parse(jklText);

        int[] sectorsToInspect = [339, 349, 361, 337, 347];

        foreach (int secIdx in sectorsToInspect)
        {
            if (secIdx >= level.Sectors.Count) continue;
            var sec = level.Sectors[secIdx];
            _output.WriteLine($"\n========================================================");
            _output.WriteLine($"SECTOR {sec.SectorIndex}: {sec.Surfaces.Count} surfaces, Center={sec.Center}");
            _output.WriteLine($"========================================================");

            for (int sIdx = 0; sIdx < sec.Surfaces.Count; sIdx++)
            {
                var s = sec.Surfaces[sIdx];
                string portalInfo = s.IsPortal
                    ? $"PORTAL -> Sector {s.MirrorSector?.SectorIndex ?? -1} (AdjoinIdx={s.AdjoinIndex})"
                    : "SOLID";

                _output.WriteLine($"  Surf {s.SurfaceIndex,4}: Normal=({s.Normal.X:F3}, {s.Normal.Y:F3}, {s.Normal.Z:F3}) [{portalInfo}]");

                for (int v = 0; v < s.VertexIndices.Length; v++)
                {
                    int vIdx = s.VertexIndices[v];
                    var pt = level.Vertices[vIdx];
                    _output.WriteLine($"     v[{v}] (#{vIdx}): ({pt.X:F3}, {pt.Y:F3}, {pt.Z:F3})");
                }
            }
        }
    }
}
