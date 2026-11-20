using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class TextureOrientationInspectionTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public TextureOrientationInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectSignTextureOrientation()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        using var archive = GobArchive.FromFile(res2Path);

        // Find neon or sign textures
        var entries = archive.Entries.Where(e => e.Path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) && 
            (e.Path.Contains("sign") || e.Path.Contains("arrow") || e.Path.Contains("door"))).Take(10).ToList();
        foreach (var e in entries)
        {
            _output.WriteLine($"Found: {e.Path}");
        }
    }
}
