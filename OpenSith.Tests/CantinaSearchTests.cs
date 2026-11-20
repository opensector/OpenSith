using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CantinaSearchTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public CantinaSearchTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FindCantinaAndActors()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        _output.WriteLine($"Total Things: {level.Things.Count}");

        // Look for tables, chairs, gran, corpses, etc.
        var cantinaThings = level.Things.Where(t => 
            t.TemplateName.Contains("table", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Contains("chair", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Contains("gran", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Contains("corpse", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Contains("dead", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Contains("reeyees", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Contains("bartender", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Contains("bar", StringComparison.OrdinalIgnoreCase)
        ).ToList();

        foreach (var t in cantinaThings)
        {
            _output.WriteLine($"Thing {t.ThingIndex}: {t.TemplateName} (name: {t.Name}) at {t.Position} in Sector {t.SectorIndex}");
        }

        // Also list all walkplayer things again with full detail
        var players = level.Things.Where(t => t.IsPlayerSpawn).ToList();
        _output.WriteLine("\nAll walkplayer things:");
        foreach (var p in players)
        {
            _output.WriteLine($"Thing {p.ThingIndex}: {p.TemplateName} at {p.Position} in Sector {p.SectorIndex}, Extra='{p.ExtraArguments}'");
        }
    }
}
