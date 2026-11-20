using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class SpawnDiagnosticTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public SpawnDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckAllPlayerSpawns()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var playerThings = level.Things.Where(t => t.TemplateName.Contains("player", StringComparison.OrdinalIgnoreCase) ||
                                                  t.TemplateName.Contains("kyle", StringComparison.OrdinalIgnoreCase)).ToList();

        _output.WriteLine($"Total player things found: {playerThings.Count}");
        foreach (var p in playerThings)
        {
            _output.WriteLine($"Thing {p.ThingIndex}: Template={p.TemplateName}, Pos={p.Position}, Sector={p.SectorIndex}, Extra={p.ExtraArguments}");
        }

        // Check Sector 339
        if (playerThings.Count > 0)
        {
            int secIdx = playerThings[0].SectorIndex;
            var sector = level.Sectors.FirstOrDefault(s => s.SectorIndex == secIdx);
            if (sector != null)
            {
                _output.WriteLine($"\nSector {secIdx} bounds:");
                _output.WriteLine($"  Min: {sector.BoundBoxMin}");
                _output.WriteLine($"  Max: {sector.BoundBoxMax}");
                _output.WriteLine($"  Center: {sector.Center}, Radius: {sector.Radius}");
                _output.WriteLine($"  Surfaces count: {sector.Surfaces.Count}");
                foreach (var s in sector.Surfaces)
                {
                    _output.WriteLine($"    Surface {s.SurfaceIndex}: Mat={s.MaterialIndex}, Normal={s.Normal}, Verts={s.VertexIndices.Length}, Adjoin={s.AdjoinIndex}");
                    if (s.VertexIndices.Length > 0)
                    {
                        var v0 = level.Vertices[s.VertexIndices[0]];
                        _output.WriteLine($"      V0: {v0}");
                    }
                }
            }
        }
    }
}
