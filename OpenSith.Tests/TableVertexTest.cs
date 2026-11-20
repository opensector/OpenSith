using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class TableVertexTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public TableVertexTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckTableVerticesAndPlacement()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));

        string text = vfs.ReadAllText("3do/00tabl.3do");
        var model = ThreeDoReader.Parse(text);

        _output.WriteLine($"Table Meshes: {model.Meshes.Count}");
        var mesh = model.Meshes[0];
        _output.WriteLine($"Mesh 0: {mesh.Vertices.Count} verts, {mesh.Faces.Count} faces");

        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (var v in mesh.Vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        _output.WriteLine($"Local bounds: Min={min}, Max={max}");

        // Now place at Thing 210 in Sector 339:
        // Pos = <-10.349864, -5.35009, -1.209447>
        Vector3 thingPos = new(-10.349864f, -5.35009f, -1.209447f);
        _output.WriteLine($"Placed at Thing 210: World Min={thingPos + min}, World Max={thingPos + max}");

        // Compare to Kyle spawn position:
        // Kyle = <-10.443433, -5.455094, -1.129886>
        Vector3 kylePos = new(-10.443433f, -5.455094f, -1.129886f);
        _output.WriteLine($"Kyle pos: {kylePos}");
        _output.WriteLine($"Distance from Kyle to Table: {Vector3.Distance(kylePos, thingPos)} meters");
        _output.WriteLine($"Vector from Kyle to Table: {thingPos - kylePos}");
    }
}
