using System;
using System.Buffers.Binary;
using System.IO;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class MatSolidAndAlphaTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    private static byte[] BuildSyntheticCmp(bool hasTransparency, Rgba32 color0, Rgba32 color1, Rgba32 color2)
    {
        const int headerSize = 64;
        const int paletteSize = 256 * 3;
        byte[] data = new byte[headerSize + paletteSize];

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x20504D43); // "CMP "
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 30);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), hasTransparency ? 1 : 0);

        WriteRgb(data, headerSize + 0 * 3, color0);
        WriteRgb(data, headerSize + 1 * 3, color1);
        WriteRgb(data, headerSize + 2 * 3, color2);
        return data;
    }

    private static void WriteRgb(byte[] data, int offset, Rgba32 color)
    {
        data[offset + 0] = color.R;
        data[offset + 1] = color.G;
        data[offset + 2] = color.B;
    }

    private static byte[] BuildSyntheticSolidMat(int solidColorIndex)
    {
        byte[] data = new byte[100];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x2054414D); // "MAT "
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 0x32);       // revision '2'
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), 0);          // type = 0 (solid color)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), 1);         // num_texinfo = 1
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 0);         // num_textures = 0
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), 8);         // bpp = 8
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(76, 4), 0);         // texinfo.texture_type = 0 (color)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(80, 4), solidColorIndex);
        return data;
    }

    private static byte[] BuildSyntheticTexturedMat(int width, int height, uint alphaEn, byte[] pixels)
    {
        byte[] data = new byte[140 + pixels.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x2054414D); // "MAT "
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 0x32);       // revision '2'
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), 2);          // type = 2 (textured)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), 1);         // num_texinfo = 1
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 1);         // num_textures = 1
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), 8);         // bpp = 8
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(76, 4), 8);        // texinfo.texture_type = 8 (texture)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(116, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(120, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(124, 4), alphaEn);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(136, 4), 1);        // num_mipmaps = 1
        pixels.CopyTo(data.AsSpan(140));
        return data;
    }

    [Fact]
    public void SyntheticSolidMat_Type0_ParsesAndDecodesPaletteColor()
    {
        var palette = CmpPalette.Parse(BuildSyntheticCmp(
            hasTransparency: true,
            new Rgba32(10, 20, 30, 255),
            new Rgba32(220, 40, 50, 255),
            new Rgba32(60, 200, 70, 255)));

        var mat = MatMaterial.Parse(BuildSyntheticSolidMat(solidColorIndex: 2));

        Assert.Equal(0, mat.MatType);
        Assert.Equal(2, mat.SolidColorIndex);
        Assert.False(mat.AlphaEnabled);
        Assert.Equal(1, mat.Width);
        Assert.Equal(1, mat.Height);

        byte[] rgba = mat.ToRgbaByteBuffer(palette);
        Assert.Equal(new byte[] { 60, 200, 70, 255 }, rgba);
    }

    [Fact]
    public void SyntheticTexturedMat_ColorKeyGatedByAlphaEnabled()
    {
        // Even with CMP.HasTransparency == true, index 0 must only be transparent when MAT alpha_en & 1 is set.
        var palette = CmpPalette.Parse(BuildSyntheticCmp(
            hasTransparency: true,
            new Rgba32(15, 25, 35, 255),
            new Rgba32(200, 100, 50, 255),
            new Rgba32(0, 0, 0, 255)));

        byte[] pixels = [0, 1];

        var opaqueMat = MatMaterial.Parse(BuildSyntheticTexturedMat(2, 1, alphaEn: 0, pixels));
        Assert.Equal(2, opaqueMat.MatType);
        Assert.False(opaqueMat.AlphaEnabled);
        Rgba32[] opaqueRgba = opaqueMat.ToRgba32(palette);
        Assert.Equal((byte)255, opaqueRgba[0].A);
        Assert.Equal((byte)15, opaqueRgba[0].R);
        Assert.Equal((byte)255, opaqueRgba[1].A);

        var alphaMat = MatMaterial.Parse(BuildSyntheticTexturedMat(2, 1, alphaEn: 1, pixels));
        Assert.Equal(2, alphaMat.MatType);
        Assert.True(alphaMat.AlphaEnabled);
        Rgba32[] alphaRgba = alphaMat.ToRgba32(palette);
        Assert.Equal((byte)0, alphaRgba[0].A);
        Assert.Equal((byte)255, alphaRgba[1].A);
        Assert.Equal((byte)200, alphaRgba[1].R);
    }

    private static MatMaterial LoadMat(GobArchive gob, string name)
    {
        string path = gob.Contains($"3do/mat/{name}") ? $"3do/mat/{name}" : $"mat/{name}";
        return MatMaterial.Parse(gob.ReadAllBytes(path));
    }

    [Fact]
    public void RetailSolidMats_Res2Gob_ParseWithDistinctColors()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var palette = CmpPalette.Parse(archive.ReadAllBytes("misc/cmp/01narsh.cmp"));

        var bry0 = LoadMat(archive, "bry0side.mat");
        var bry1 = LoadMat(archive, "bry1side.mat");
        var str0 = LoadMat(archive, "str0side.mat");
        var rep0 = LoadMat(archive, "rep0side.mat");

        foreach (var mat in new[] { bry0, bry1, str0, rep0 })
        {
            Assert.Equal(0, mat.MatType);
            Assert.False(mat.AlphaEnabled);
            Assert.Equal(1, mat.Width);
            Assert.Equal(1, mat.Height);
        }

        Rgba32 bry0Color = bry0.ToRgba32(palette)[0];
        Rgba32 bry1Color = bry1.ToRgba32(palette)[0];
        Rgba32 str0Color = str0.ToRgba32(palette)[0];
        Rgba32 rep0Color = rep0.ToRgba32(palette)[0];

        // bry0 / str0: red laser bolt; bry1: green laser bolt; rep0: gold/yellow repeater bolt
        Assert.True(bry0Color.R > bry0Color.G && bry0Color.R > bry0Color.B, $"Expected red bry0side, got {bry0Color}");
        Assert.True(str0Color.R > str0Color.G && str0Color.R > str0Color.B, $"Expected red str0side, got {str0Color}");
        Assert.True(bry1Color.G > bry1Color.R && bry1Color.G > bry1Color.B, $"Expected green bry1side, got {bry1Color}");
        Assert.True(rep0Color.R > rep0Color.B && rep0Color.G > rep0Color.B, $"Expected gold/yellow rep0side, got {rep0Color}");

        Assert.NotEqual(bry0.SolidColorIndex, bry1.SolidColorIndex);
        Assert.NotEqual(bry0.SolidColorIndex, rep0.SolidColorIndex);
        Assert.NotEqual(bry1.SolidColorIndex, rep0.SolidColorIndex);
    }

    [Fact]
    public void RetailSaberGreenMats_Res2Gob_GateColorKeyByAlphaEnabled()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var palette = CmpPalette.Parse(archive.ReadAllBytes("misc/cmp/01narsh.cmp"));
        Assert.True(palette.HasTransparency);

        var saber0 = LoadMat(archive, "sabergreen0.mat");
        var saber1 = LoadMat(archive, "sabergreen1.mat");

        Assert.Equal(2, saber0.MatType);
        Assert.True(saber0.AlphaEnabled);

        Assert.Equal(2, saber1.MatType);
        Assert.False(saber1.AlphaEnabled);

        // Verify color-key index 0 pixels are transparent in saber0 and opaque in saber1
        Rgba32[] rgba0 = saber0.ToRgba32(palette);
        int idx0 = Array.IndexOf(saber0.IndexedPixels, (byte)0);
        Assert.True(idx0 >= 0, "Expected sabergreen0.mat to contain palette index 0 pixels.");
        Assert.Equal((byte)0, rgba0[idx0].A);

        Rgba32[] rgba1 = saber1.ToRgba32(palette);
        for (int i = 0; i < rgba1.Length; i++)
        {
            Assert.Equal((byte)255, rgba1[i].A);
        }
    }
}
