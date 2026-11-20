using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class PlayerStartAnalysisTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public PlayerStartAnalysisTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AnalyzeSpawnsAndSectors()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var spawns = level.Things.Where(t => t.IsPlayerSpawn).ToList();
        _output.WriteLine($"Total player spawns: {spawns.Count}");
        foreach (var s in spawns)
        {
            var sec = level.Sectors[s.SectorIndex];
            _output.WriteLine($"Thing {s.ThingIndex} in Sector {s.SectorIndex} ({sec.Surfaces.Count} surfaces, Center={sec.Center}): Pos={s.Position}");
        }
    }
}
