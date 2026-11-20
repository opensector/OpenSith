using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class Level1NpcInspectionTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public Level1NpcInspectionTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectAllNpcsInLevel1()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;

        using var archive = GobArchive.FromFile(jk1Path);
        var entry = archive.Entries.First(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        string jklText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
        var level = JklReader.Parse(jklText);

        _output.WriteLine($"Total Things: {level.Things.Count}");
        foreach (var t in level.Things)
        {
            string model = t.Model3D;
            string puppet = "";
            string current = t.TemplateName;
            while (!string.IsNullOrEmpty(current) && !current.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (level.Templates.TryGetValue(current, out var tmpl))
                {
                    if (string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(tmpl.Model3D)) model = tmpl.Model3D;
                    if (string.IsNullOrEmpty(puppet) && tmpl.Parameters.TryGetValue("puppet", out var p)) puppet = p;
                    current = tmpl.BasedOn;
                }
                else break;
            }

            if (!string.IsNullOrEmpty(model) && (model.EndsWith(".3do", StringComparison.OrdinalIgnoreCase)))
            {
                if (t.SectorIndex == 339 || t.SectorIndex == 349 || t.SectorIndex == 337 || t.SectorIndex == 361 || t.SectorIndex == 370 || !string.IsNullOrEmpty(puppet))
                {
                    _output.WriteLine($"Thing {t.ThingIndex,3}: Sector={t.SectorIndex,3}, Template='{t.TemplateName,-15}', Model='{model,-12}', Puppet='{puppet,-12}', Pos={t.Position}, PYR={t.PitchYawRoll}");
                }
            }
        }
    }
}
