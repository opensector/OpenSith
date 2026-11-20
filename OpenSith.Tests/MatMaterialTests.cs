using System;
using System.IO;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class MatMaterialTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void RetailMat_DecodesToRgba32_Successfully()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        byte[] cmpBytes = archive.ReadAllBytes("misc/cmp/01narsh.cmp");
        var palette = CmpPalette.Parse(cmpBytes);

        byte[] matBytes = archive.ReadAllBytes("3do/mat/00fmtl1.mat");
        var mat = MatMaterial.Parse(matBytes);

        Assert.Equal(128, mat.Width);
        Assert.Equal(128, mat.Height);
        Assert.Equal(128 * 128, mat.IndexedPixels.Length);

        // Convert to RGBA
        Rgba32[] pixels = mat.ToRgba32(palette);
        Assert.Equal(128 * 128, pixels.Length);

        // Verify that pixels are valid colors and not all black
        int nonBlackCount = 0;
        foreach (var p in pixels)
        {
            if (p.R > 0 || p.G > 0 || p.B > 0)
                nonBlackCount++;
        }
        Assert.True(nonBlackCount > 1000, "Texture should have visible colored pixels.");

        // Convert to raw byte buffer for GPU upload
        byte[] gpuBuffer = mat.ToRgbaByteBuffer(palette);
        Assert.Equal(128 * 128 * 4, gpuBuffer.Length);
    }
}
