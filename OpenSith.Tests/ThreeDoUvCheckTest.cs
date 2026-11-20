using System;
using System.IO;
using System.Linq;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class ThreeDoUvCheckTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public ThreeDoUvCheckTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Inspect3doUvs()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        using var archive = GobArchive.FromFile(res2Path);

        string[] models = ["00tabl.3do", "thout.3do"];
        foreach (var m in models)
        {
            string path = $"3do/{m}";
            var entry = archive.Entries.FirstOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (entry.Length > 0)
            {
                byte[] bytes = archive.ReadAllBytes(entry.Path);
                string text = System.Text.Encoding.ASCII.GetString(bytes);
                var model = ThreeDoReader.Parse(text);
                _output.WriteLine($"\n=== {m} ===");
                _output.WriteLine($"Materials: {string.Join(", ", model.Materials)}");
                if (model.Meshes.Count > 0)
                {
                    var mesh = model.Meshes[0];
                    _output.WriteLine($"Mesh 0: {mesh.Vertices.Count} verts, {mesh.TextureVertices.Count} UVs, {mesh.Faces.Count} faces");
                    foreach (var uv in mesh.TextureVertices.Take(5))
                    {
                        _output.WriteLine($"  UV: ({uv.X}, {uv.Y})");
                    }
                }
            }
        }
    }
}
