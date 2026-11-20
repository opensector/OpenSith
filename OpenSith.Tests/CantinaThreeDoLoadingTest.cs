using System;
using System.IO;
using System.Linq;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CantinaThreeDoLoadingTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public CantinaThreeDoLoadingTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void LoadCantina3doModels()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));
        vfs.Mount(GobArchive.FromFile(jk1Path));

        string jklText = vfs.ReadAllText("jkl/01narshadda.jkl");
        var level = JklReader.Parse(jklText, "01narshadda");

        // Check templates in JKL or misc/template.jkl
        string[] testModels = ["00table.3do", "reeyees.3do", "ky.3do", "tusken.3do"];

        foreach (var m in testModels)
        {
            string p = $"3do/{m}";
            if (vfs.FileExists(p))
            {
                string text = vfs.ReadAllText(p);
                var model = ThreeDoReader.Parse(text);
                _output.WriteLine($"[LOADED 3DO] {p}: {model.Meshes.Count} meshes, {model.Materials.Count} mats, {model.Nodes.Count} nodes");
            }
            else
            {
                _output.WriteLine($"[NOT FOUND] {p}");
            }
        }
    }
}
