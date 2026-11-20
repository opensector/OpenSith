using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CantinaExactLocationTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public CantinaExactLocationTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void LocateCantinaThings()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var targets = level.Things.Where(t => 
            t.TemplateName.Equals("8t88", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Equals("nbartender", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Equals("tuskensoused", StringComparison.OrdinalIgnoreCase) ||
            t.TemplateName.Equals("00table", StringComparison.OrdinalIgnoreCase)
        ).ToList();

        foreach (var t in targets)
        {
            _output.WriteLine($"Thing {t.ThingIndex}: {t.TemplateName} at {t.Position} in Sector {t.SectorIndex}, PitchYawRoll={t.PitchYawRoll}");
        }

        // Find all things in the same sector(s) as the cantina
        if (targets.Count > 0)
        {
            var cantinaSectors = targets.Select(t => t.SectorIndex).Distinct().ToList();
            _output.WriteLine($"\nCantina Sectors: {string.Join(", ", cantinaSectors)}");

            foreach (var secIdx in cantinaSectors)
            {
                var thingsInSec = level.Things.Where(t => t.SectorIndex == secIdx).ToList();
                _output.WriteLine($"\nSector {secIdx} has {thingsInSec.Count} things:");
                foreach (var t in thingsInSec)
                {
                    _output.WriteLine($"  Thing {t.ThingIndex}: {t.TemplateName} at {t.Position}, Yaw={t.PitchYawRoll.Y}");
                }
            }
        }
    }
}
