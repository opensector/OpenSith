using System;
using System.IO;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CmpInspectionTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    public CmpInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void VerifyCmpStructure()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        byte[] bytes = archive.ReadAllBytes("misc/cmp/01narsh.cmp");
        
        // Header: 64 bytes
        // Palette: 256 * 3 = 768 bytes (at offset 64)
        // Light levels: remainder / 256
        int headerSize = 64;
        int paletteSize = 256 * 3;
        int lightMapsSize = bytes.Length - (headerSize + paletteSize);
        int lightLevels = lightMapsSize / 256;

        _output.WriteLine($"01narsh.cmp total: {bytes.Length}");
        _output.WriteLine($"Lightmaps size: {lightMapsSize} bytes ({lightLevels} levels of 256 bytes)");

        // Verify colors: color 0, color 1, color 2
        for (int i = 0; i < 5; i++)
        {
            int r = bytes[headerSize + (i * 3) + 0];
            int g = bytes[headerSize + (i * 3) + 1];
            int b = bytes[headerSize + (i * 3) + 2];
            _output.WriteLine($"Palette[{i}]: R={r}, G={g}, B={b}");
        }

        // Also check 01redmap.cmp
        byte[] redBytes = archive.ReadAllBytes("misc/cmp/01redmap.cmp");
        int redLightMapsSize = redBytes.Length - (headerSize + paletteSize);
        int redLightLevels = redLightMapsSize / 256;
        _output.WriteLine($"01redmap.cmp light levels: {redLightLevels}");
        Assert.Equal(64, redLightLevels);
        Assert.Equal(320, lightLevels);
    }
}
