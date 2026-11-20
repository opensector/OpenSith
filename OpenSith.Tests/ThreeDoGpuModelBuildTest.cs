using System;
using System.IO;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class ThreeDoGpuModelBuildTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public ThreeDoGpuModelBuildTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestBuild3doGeometry()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));

        string[] testModels = ["00tabl.3do", "thout.3do", "rhstr.3do", "bottle4.3do"];

        foreach (var m in testModels)
        {
            string path = $"3do/{m}";
            Assert.True(vfs.FileExists(path), $"File exists: {path}");
            string text = vfs.ReadAllText(path);
            var model = ThreeDoReader.Parse(text);
            _output.WriteLine($"[PARSE OK] {m}: {model.Meshes.Count} meshes, {model.Nodes.Count} nodes");

            // Check faces and texture coordinates
            int totalFaces = 0;
            int totalVerts = 0;
            foreach (var mesh in model.Meshes)
            {
                totalFaces += mesh.Faces.Count;
                totalVerts += mesh.Vertices.Count;
            }
            _output.WriteLine($"  Total faces: {totalFaces}, total verts: {totalVerts}");
        }
    }
}
