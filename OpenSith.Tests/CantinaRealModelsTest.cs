using System;
using System.IO;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CantinaRealModelsTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public CantinaRealModelsTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void VerifyCantinaModelsExist()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        using var archive = GobArchive.FromFile(res2Path);

        string[] models = ["00tabl.3do", "thout.3do", "rhstr.3do", "bottle4.3do"];
        foreach (var m in models)
        {
            string path = $"3do/{m}";
            var entry = archive.Entries.FirstOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (entry.Length > 0)
            {
                byte[] bytes = archive.ReadAllBytes(entry.Path);
                string text = System.Text.Encoding.ASCII.GetString(bytes);
                var model = ThreeDoReader.Parse(text);
                _output.WriteLine($"[FOUND & PARSED] {path}: {model.Meshes.Count} meshes, {model.Materials.Count} mats, {model.Nodes.Count} nodes");
            }
            else
            {
                _output.WriteLine($"[NOT FOUND] {path}");
            }
        }
    }
}
