using System;
using System.IO;
using System.Linq;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class TextureDiagnosticTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public TextureDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestMaterialDimensionsAndUvs()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));
        vfs.Mount(GobArchive.FromFile(jk1Path));

        string jklText = vfs.ReadAllText("jkl/01narshadda.jkl");
        var level = JklReader.Parse(jklText, "01narshadda");

        _output.WriteLine($"Parsed TextureVertices count: {level.TextureVertices.Count}");
        Assert.Equal(10574, level.TextureVertices.Count);

        for (int i = 0; i < 5; i++)
        {
            _output.WriteLine($"  UV[{i}]: ({level.TextureVertices[i].X}, {level.TextureVertices[i].Y})");
        }
    }
}
