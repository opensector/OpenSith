using System;
using System.IO;
using System.Text;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class ThreeDoReaderTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void ParseRetailCrate3Do_MatchesExpectedGeometry()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("3do/00crte.3do"));
        var model = ThreeDoReader.Parse(text);

        Assert.Equal("3DO 2.1", model.Version);
        Assert.Equal(3, model.Materials.Count);
        Assert.Contains("00crtesd.mat", model.Materials);

        Assert.Single(model.Meshes);
        var mesh = model.Meshes[0];
        Assert.Equal("12x12crt", mesh.Name);
        Assert.Equal(16, mesh.Vertices.Count);
        Assert.Equal(10, mesh.Faces.Count);

        // Verify face vertices
        Assert.Equal(4, mesh.Faces[0].VertexIndices.Length);
        Assert.Equal(8, mesh.Faces[8].VertexIndices.Length); // 8-sided octagon cap!

        // Verify hierarchy
        Assert.Single(model.Nodes);
        Assert.Equal("12x12crt", model.Nodes[0].Name);
    }

    [Theory]
    [InlineData("3do/00tabl.3do")]
    [InlineData("3do/00tetris.3do")]
    [InlineData("3do/01c3x8.3do")]
    public void ParseMultipleRetail3Dos_Succeeds(string path)
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        if (!archive.Contains(path)) return;

        string text = Encoding.ASCII.GetString(archive.ReadAllBytes(path));
        var model = ThreeDoReader.Parse(text);

        Assert.NotEmpty(model.Meshes);
        Assert.True(model.Meshes[0].Vertices.Count > 0);
        Assert.True(model.Meshes[0].Faces.Count > 0);
    }
}
