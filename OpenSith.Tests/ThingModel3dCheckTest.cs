using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class ThingModel3dCheckTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public ThingModel3dCheckTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckThingsModel3d()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        int withModel = level.Things.Count(t => !string.IsNullOrEmpty(t.Model3D));
        int total = level.Things.Count;
        _output.WriteLine($"Things with Model3D: {withModel} / {total}");

        var cantinaThings = level.Things.Where(t => t.SectorIndex == 339 || t.SectorIndex == 340 || t.SectorIndex == 353).ToList();
        foreach (var t in cantinaThings)
        {
            _output.WriteLine($"Thing {t.ThingIndex}: {t.TemplateName} -> Model3D='{t.Model3D}', Pos={t.Position}");
        }
    }
}
