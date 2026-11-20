using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class HudAssetTests
{
    private const string SteamResourceDir = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource";
    private static readonly string SteamRes1HiPath = Path.Combine(SteamResourceDir, "Res1hi.gob");
    private static readonly string SteamRes2Path = Path.Combine(SteamResourceDir, "Res2.gob");

    private static byte[] BuildSyntheticBm(
        int bpp,
        uint palFmt,
        uint colorKey,
        bool isCompressed,
        int rBits, int gBits, int bBits,
        int rShift, int gShift, int bShift,
        int aBits, int aShift,
        (int Width, int Height, byte[] Pixels)[] frames,
        byte[]? embeddedPaletteRgb24 = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes("BM  "));
        bw.Write(70);                               // version
        bw.Write(isCompressed ? 1u : 0u);           // field_8 / compressed
        bw.Write(palFmt);                           // palFmt
        bw.Write((uint)frames.Length);              // numMips / frames
        bw.Write(0);                                // xPos
        bw.Write(0);                                // yPos
        bw.Write(colorKey);                         // colorkey

        // rdTexFormat (14 * uint32 = 56 bytes)
        bw.Write(bpp == 16 ? 1u : 0u);              // is16bit
        bw.Write((uint)bpp);                        // bpp
        bw.Write((uint)rBits);
        bw.Write((uint)gBits);
        bw.Write((uint)bBits);
        bw.Write((uint)rShift);
        bw.Write((uint)gShift);
        bw.Write((uint)bShift);
        bw.Write(0u);                               // r_bitdiff
        bw.Write(0u);                               // g_bitdiff
        bw.Write(0u);                               // b_bitdiff
        bw.Write((uint)aShift);                     // unk_40
        bw.Write((uint)aBits);                      // unk_44
        bw.Write(0u);                               // unk_48

        // Remaining 40 bytes of 128-byte header
        for (int i = 0; i < 10; i++)
            bw.Write(0u);

        foreach (var (w, h, px) in frames)
        {
            bw.Write(w);
            bw.Write(h);
            bw.Write(px);
        }

        if ((palFmt & 2) != 0)
        {
            Assert.NotNull(embeddedPaletteRgb24);
            Assert.Equal(768, embeddedPaletteRgb24!.Length);
            bw.Write(embeddedPaletteRgb24);
        }

        return ms.ToArray();
    }

    private static CmpPalette BuildSyntheticCmp()
    {
        byte[] data = new byte[64 + 768];
        Encoding.ASCII.GetBytes("CMP ").CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 30);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), 1); // hasTransparency

        // Entry 0: black, Entry 1: pure red, Entry 2: pure green
        data[64 + (1 * 3) + 0] = 255;
        data[64 + (2 * 3) + 1] = 255;
        return CmpPalette.Parse(data);
    }

    [Fact]
    public void SyntheticBm_8BitPalettedWithColorkey_DecodesAlphaAndColors()
    {
        CmpPalette cmp = BuildSyntheticCmp();
        byte[] bmBytes = BuildSyntheticBm(
            bpp: 8,
            palFmt: 1,
            colorKey: 0,
            isCompressed: false,
            rBits: 0, gBits: 0, bBits: 0,
            rShift: 0, gShift: 0, bShift: 0,
            aBits: 0, aShift: 0,
            frames: new[] { (2, 1, new byte[] { 0, 1 }) });

        BmBitmap bm = BmBitmap.Parse(bmBytes);
        Assert.Equal(70, bm.Version);
        Assert.False(bm.IsCompressed);
        Assert.True(bm.HasTransparency);
        Assert.False(bm.HasEmbeddedPalette);
        Assert.Single(bm.Frames);
        Assert.Equal(2, bm.Width);
        Assert.Equal(1, bm.Height);
        Assert.Equal(8, bm.Frames[0].BitsPerPixel);

        byte[] rgba = bm.ToRgbaByteBuffer(cmp);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 0, 0, 255 }, rgba);
    }

    [Fact]
    public void SyntheticBm_8BitEmbeddedPalette_DecodesWithoutExternalCmp()
    {
        byte[] pal = new byte[768];
        pal[0] = 10; pal[1] = 20; pal[2] = 30;
        pal[3] = 40; pal[4] = 50; pal[5] = 60;

        byte[] bmBytes = BuildSyntheticBm(
            bpp: 8,
            palFmt: 2,
            colorKey: 0,
            isCompressed: false,
            rBits: 0, gBits: 0, bBits: 0,
            rShift: 0, gShift: 0, bShift: 0,
            aBits: 0, aShift: 0,
            frames: new[] { (2, 1, new byte[] { 0, 1 }) },
            embeddedPaletteRgb24: pal);

        BmBitmap bm = BmBitmap.Parse(bmBytes);
        Assert.True(bm.HasEmbeddedPalette);
        Assert.False(bm.HasTransparency);
        Assert.NotNull(bm.EmbeddedPalette);

        byte[] rgba = bm.ToRgbaByteBuffer();
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, rgba);
    }

    [Fact]
    public void SyntheticBm_16BitRgb565MultiFrameWithColorkey_DecodesCorrectly()
    {
        // Frame 0: colorkey (0xF81F), red (0xF800)
        byte[] f0 = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(f0.AsSpan(0, 2), 0xF81F);
        BinaryPrimitives.WriteUInt16LittleEndian(f0.AsSpan(2, 2), 0xF800);

        // Frame 1: green (0x07E0), blue (0x001F)
        byte[] f1 = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(f1.AsSpan(0, 2), 0x07E0);
        BinaryPrimitives.WriteUInt16LittleEndian(f1.AsSpan(2, 2), 0x001F);

        byte[] bmBytes = BuildSyntheticBm(
            bpp: 16,
            palFmt: 1,
            colorKey: 0xF81F,
            isCompressed: true,
            rBits: 5, gBits: 6, bBits: 5,
            rShift: 11, gShift: 5, bShift: 0,
            aBits: 0, aShift: 0,
            frames: new[] { (2, 1, f0), (1, 2, f1) });

        BmBitmap bm = BmBitmap.Parse(bmBytes);
        Assert.Equal(2, bm.Frames.Count);
        Assert.True(bm.IsCompressed);
        Assert.True(bm.Frames[0].IsCompressed);
        Assert.Equal(0xF81Fu, bm.Frames[0].TransparentColor);

        byte[] rgba0 = bm.Frames[0].ToRgbaByteBuffer();
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 0, 0, 255 }, rgba0);

        byte[] rgba1 = bm.Frames[1].ToRgbaByteBuffer();
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 0, 255, 255 }, rgba1);
    }

    [Fact]
    public void SyntheticBm_16BitRgba5551_DecodesOneBitAlpha()
    {
        // RGBA5551: R=11..15, G=6..10, B=1..5, A=0
        byte[] px = new byte[4];
        ushort transparentRed = (31 << 11) | 0; // A=0
        ushort opaqueBlue = (31 << 1) | 1;      // A=1
        BinaryPrimitives.WriteUInt16LittleEndian(px.AsSpan(0, 2), transparentRed);
        BinaryPrimitives.WriteUInt16LittleEndian(px.AsSpan(2, 2), opaqueBlue);

        byte[] bmBytes = BuildSyntheticBm(
            bpp: 16,
            palFmt: 0,
            colorKey: 0,
            isCompressed: false,
            rBits: 5, gBits: 5, bBits: 5,
            rShift: 11, gShift: 6, bShift: 1,
            aBits: 1, aShift: 0,
            frames: new[] { (2, 1, px) });

        BmBitmap bm = BmBitmap.Parse(bmBytes);
        byte[] rgba = bm.ToRgbaByteBuffer();
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 0, 255, 255 }, rgba);
    }

    [Fact]
    public void SyntheticSft_MultiCharset_ParsesGlyphsAndEmbeddedBitmap()
    {
        // 2 charsets: '0'..'1' and 'A'..'A', embedded 12x4 RGB565 BM with colorkey 0xF81F
        byte[] bmPx = new byte[12 * 4 * 2];
        for (int i = 0; i < 12 * 4; i++)
        {
            ushort color = (i % 2 == 0) ? (ushort)0xF81F : (ushort)0xFFFF;
            BinaryPrimitives.WriteUInt16LittleEndian(bmPx.AsSpan(i * 2, 2), color);
        }

        byte[] bmBytes = BuildSyntheticBm(
            bpp: 16,
            palFmt: 1,
            colorKey: 0xF81F,
            isCompressed: false,
            rBits: 5, gBits: 6, bBits: 5,
            rShift: 11, gShift: 5, bShift: 0,
            aBits: 0, aShift: 0,
            frames: new[] { (12, 4, bmPx) });

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("SFNT"));
        bw.Write(10);           // version
        bw.Write(1);            // marginY
        bw.Write(3);            // marginX
        bw.Write((int)'0');     // defaultChar
        bw.Write(2);            // numCharsets
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        // Charset 0: '0'..'1'
        bw.Write((ushort)'0');
        bw.Write((ushort)'1');
        bw.Write(0); bw.Write(4);   // '0': x=0, w=4
        bw.Write(4); bw.Write(3);   // '1': x=4, w=3

        // Charset 1: 'A'..'A'
        bw.Write((ushort)'A');
        bw.Write((ushort)'A');
        bw.Write(7); bw.Write(5);   // 'A': x=7, w=5

        bw.Write(bmBytes);

        SftFont font = SftFont.Parse(ms.ToArray());
        Assert.Equal(10, font.Version);
        Assert.Equal(1, font.MarginY);
        Assert.Equal(3, font.MarginX);
        Assert.Equal('0', font.DefaultChar);
        Assert.Equal(2, font.CharsetCount);
        Assert.Equal(4, font.LineHeight);
        Assert.Equal(12, font.Bitmap.Width);

        Assert.True(font.TryGetGlyph('0', out var g0));
        Assert.Equal(0, g0.XOffset);
        Assert.Equal(4, g0.Width);
        Assert.Equal(4, g0.Height);

        Assert.True(font.TryGetGlyph('1', out var g1));
        Assert.Equal(4, g1.XOffset);
        Assert.Equal(3, g1.Width);

        Assert.True(font.TryGetGlyph('A', out var gA));
        Assert.Equal(7, gA.XOffset);
        Assert.Equal(5, gA.Width);

        Assert.False(font.TryGetGlyph('Z', out _));
        Assert.True(font.TryGetGlyphOrDefault('Z', out var gFallback));
        Assert.Equal('0', gFallback.Character);

        byte[] rgba = font.ToRgbaByteBuffer();
        Assert.Equal(12 * 4 * 4, rgba.Length);
        Assert.Equal(0, rgba[3]);       // colorkey transparent pixel
        Assert.Equal(255, rgba[7]);     // opaque white pixel
    }

    [Fact]
    public void RetailGob_16BitAnd8BitHudBitmaps_ParseWithValidFramesAndRgbaPixels()
    {
        if (!File.Exists(SteamRes1HiPath) || !File.Exists(SteamRes2Path))
            return;

        using var res1Hi = GobArchive.FromFile(SteamRes1HiPath);
        using var res2 = GobArchive.FromFile(SteamRes2Path);

        CmpPalette uiPalette = CmpPalette.Parse(res2.ReadAllBytes("misc/cmp/uicolormap.cmp"));

        var expected16BitBitmaps = new (string Path, int MinFrames)[]
        {
            ("ui/bm/statusLeft16.bm", 1),
            ("ui/bm/statusRight16.bm", 1),
            ("ui/bm/stHealth16.bm", 6),
            ("ui/bm/stShield16.bm", 11),
            ("ui/bm/stFrc16.bm", 16),
            ("ui/bm/stBat16.bm", 7),
        };

        foreach (var (path, expectedFrames) in expected16BitBitmaps)
        {
            byte[] data = res1Hi.ReadAllBytes(path);
            BmBitmap bm = BmBitmap.Parse(data);

            Assert.Equal(expectedFrames, bm.Frames.Count);
            Assert.True(bm.HasTransparency, $"{path} should have colorkey transparency.");
            Assert.Equal(0xF81Fu, bm.TransparentColor);

            bool anyFrameHasColor = false;
            foreach (BmFrame frame in bm.Frames)
            {
                Assert.True(frame.Width > 0 && frame.Height > 0);
                Assert.Equal(16, frame.BitsPerPixel);

                byte[] rgba = frame.ToRgbaByteBuffer();
                Assert.Equal(frame.Width * frame.Height * 4, rgba.Length);

                bool hasTransparent = false;
                bool hasOpaque = false;
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    if (rgba[i + 3] == 0)
                    {
                        hasTransparent = true;
                    }
                    else if (rgba[i + 3] == 255)
                    {
                        hasOpaque = true;
                        if ((rgba[i] | rgba[i + 1] | rgba[i + 2]) != 0)
                            anyFrameHasColor = true;
                    }
                }

                Assert.True(hasTransparent, $"{path} expected colorkey alpha=0 pixels.");
                Assert.True(hasOpaque, $"{path} expected opaque alpha=255 pixels.");
            }

            Assert.True(anyFrameHasColor, $"{path} expected at least one frame with non-black RGB pixels.");
        }

        // 8-bit HUD equivalents
        var expected8BitBitmaps = new (string Path, int MinFrames)[]
        {
            ("ui/bm/statusLeft.bm", 1),
            ("ui/bm/statusRight.bm", 1),
            ("ui/bm/stHealth.bm", 6),
            ("ui/bm/stShield.bm", 11),
            ("ui/bm/stFrc.bm", 16),
            ("ui/bm/stBat.bm", 7),
        };

        foreach (var (path, expectedFrames) in expected8BitBitmaps)
        {
            BmBitmap bm = BmBitmap.Parse(res1Hi.ReadAllBytes(path));
            Assert.Equal(expectedFrames, bm.Frames.Count);
            Assert.Equal(8, bm.Frames[0].BitsPerPixel);

            byte[] rgba = bm.ToRgbaByteBuffer(uiPalette);
            Assert.Equal(bm.Width * bm.Height * 4, rgba.Length);
            Assert.Contains(Enumerable.Range(0, rgba.Length / 4), idx => rgba[idx * 4 + 3] == 255);
        }
    }

    [Fact]
    public void RetailGob_16BitAnd8BitHudFonts_ParseDigitGlyphsAndRgbaPixels()
    {
        if (!File.Exists(SteamRes1HiPath) || !File.Exists(SteamRes2Path))
            return;

        using var res1Hi = GobArchive.FromFile(SteamRes1HiPath);
        using var res2 = GobArchive.FromFile(SteamRes2Path);

        CmpPalette uiPalette = CmpPalette.Parse(res2.ReadAllBytes("misc/cmp/uicolormap.cmp"));

        string[] fonts16 =
        {
            "ui/sft/HelthNum16.sft",
            "ui/sft/ArmorNum16.sft",
            "ui/sft/AmoNums16.sft",
            "ui/sft/msgFont16.sft",
        };

        foreach (string path in fonts16)
        {
            SftFont font = SftFont.Parse(res1Hi.ReadAllBytes(path));
            Assert.True(font.LineHeight > 0, $"{path} invalid LineHeight.");
            Assert.True(font.Bitmap.Width > 0, $"{path} invalid Bitmap width.");
            Assert.Equal(16, font.Bitmap.PixelFormat.BitsPerPixel);

            for (char digit = '0'; digit <= '9'; digit++)
            {
                Assert.True(font.TryGetGlyph(digit, out var glyph), $"{path} missing digit '{digit}'.");
                Assert.True(glyph.Width > 0);
                Assert.True(glyph.XOffset >= 0);
                Assert.True(glyph.XOffset + glyph.Width <= font.Bitmap.Width);
                Assert.Equal(font.LineHeight, glyph.Height);
            }

            byte[] rgba = font.ToRgbaByteBuffer();
            Assert.Equal(font.Bitmap.Width * font.LineHeight * 4, rgba.Length);
            Assert.Contains(Enumerable.Range(0, rgba.Length / 4),
                idx => rgba[idx * 4 + 3] == 255 && (rgba[idx * 4] | rgba[idx * 4 + 1] | rgba[idx * 4 + 2]) != 0);
        }

        // msgFont16.sft uses colorkey 0xF81F transparency and 14 charsets
        SftFont msgFont16 = SftFont.Parse(res1Hi.ReadAllBytes("ui/sft/msgFont16.sft"));
        Assert.Equal(14, msgFont16.CharsetCount);
        Assert.True(msgFont16.TryGetGlyph('A', out _));
        byte[] msgRgba = msgFont16.ToRgbaByteBuffer();
        Assert.Contains(Enumerable.Range(0, msgRgba.Length / 4), idx => msgRgba[idx * 4 + 3] == 0);

        // 8-bit font equivalents
        string[] fonts8 =
        {
            "ui/sft/HelthNum.sft",
            "ui/sft/ArmorNum.sft",
            "ui/sft/AmoNums.sft",
            "ui/sft/msgFont.sft",
        };

        foreach (string path in fonts8)
        {
            SftFont font = SftFont.Parse(res1Hi.ReadAllBytes(path));
            Assert.True(font.LineHeight > 0);
            for (char digit = '0'; digit <= '9'; digit++)
            {
                Assert.True(font.TryGetGlyph(digit, out var glyph), $"{path} missing digit '{digit}'.");
                Assert.True(glyph.Width > 0);
            }

            byte[] rgba = font.ToRgbaByteBuffer(uiPalette);
            Assert.Equal(font.Bitmap.Width * font.LineHeight * 4, rgba.Length);
            Assert.Contains(Enumerable.Range(0, rgba.Length / 4), idx => rgba[idx * 4 + 3] == 255);
        }
    }

    [Fact]
    public void RetailGob_AllBmAndSftAssets_ParseWithoutErrors()
    {
        if (!File.Exists(SteamRes1HiPath))
            return;

        using var res1Hi = GobArchive.FromFile(SteamRes1HiPath);

        var bmEntries = res1Hi.Entries.Where(e => e.Path.EndsWith(".bm", StringComparison.OrdinalIgnoreCase)).ToList();
        var sftEntries = res1Hi.Entries.Where(e => e.Path.EndsWith(".sft", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(bmEntries);
        Assert.NotEmpty(sftEntries);

        foreach (var entry in bmEntries)
        {
            BmBitmap bm = BmBitmap.Parse(res1Hi.ReadAllBytes(entry.Path));
            Assert.NotEmpty(bm.Frames);
            byte[] rgba = bm.ToRgbaByteBuffer();
            Assert.Equal(bm.Width * bm.Height * 4, rgba.Length);
        }

        foreach (var entry in sftEntries)
        {
            SftFont sft = SftFont.Parse(res1Hi.ReadAllBytes(entry.Path));
            Assert.True(sft.LineHeight > 0);
            Assert.NotEmpty(sft.Glyphs);
            byte[] rgba = sft.ToRgbaByteBuffer();
            Assert.Equal(sft.Bitmap.Width * sft.LineHeight * 4, rgba.Length);
        }
    }
}
