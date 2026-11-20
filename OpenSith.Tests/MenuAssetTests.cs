using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Client;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class MenuAssetTests
{
    private const string SteamResourceDir = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource";
    private static readonly string SteamRes1HiPath = Path.Combine(SteamResourceDir, "Res1hi.gob");
    private static readonly string SteamRes2Path = Path.Combine(SteamResourceDir, "Res2.gob");

    [Fact]
    public void RetailGob_MenuBackgroundBitmaps_Parse640x480WithEmbeddedPaletteAndNonEmptyRgba()
    {
        if (!File.Exists(SteamRes1HiPath))
            return;

        using var res1Hi = GobArchive.FromFile(SteamRes1HiPath);

        string[] menuBackgrounds =
        {
            "ui/bm/bkmain.bm",
            "ui/bm/bksingle.bm",
            "ui/bm/bkesc.bm",
            "ui/bm/bktally.bm",
            "ui/bm/bkloading.bm",
        };

        foreach (string path in menuBackgrounds)
        {
            Assert.True(
                res1Hi.Entries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)),
                $"Missing expected menu background: {path}");

            BmBitmap bm = BmBitmap.Parse(res1Hi.ReadAllBytes(path));
            Assert.Equal(640, bm.Width);
            Assert.Equal(480, bm.Height);
            Assert.NotEmpty(bm.Frames);
            Assert.Equal(8, bm.Frames[0].BitsPerPixel);
            Assert.True(bm.HasEmbeddedPalette, $"{path} should have an 8-bit embedded palette.");
            Assert.False(bm.HasTransparency, $"{path} background should be opaque (HasTransparency == false).");

            byte[] rgba = bm.ToRgbaByteBuffer();
            Assert.Equal(640 * 480 * 4, rgba.Length);
            Assert.Contains(
                Enumerable.Range(0, rgba.Length / 4),
                idx => rgba[idx * 4 + 3] == 255 && (rgba[idx * 4] | rgba[idx * 4 + 1] | rgba[idx * 4 + 2]) != 0);
        }
    }

    [Fact]
    public void RetailGob_MenuFonts_ParseWithValidGlyphsAndRgbaPixels()
    {
        if (!File.Exists(SteamRes1HiPath))
            return;

        using var res1Hi = GobArchive.FromFile(SteamRes1HiPath);

        string[] menuFonts =
        {
            "ui/sft/large0.sft",
            "ui/sft/large1.sft",
            "ui/sft/med0.sft",
            "ui/sft/med1.sft",
            "ui/sft/small0.sft",
        };

        foreach (string path in menuFonts)
        {
            Assert.True(
                res1Hi.Entries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)),
                $"Missing expected menu font: {path}");

            SftFont font = SftFont.Parse(res1Hi.ReadAllBytes(path));
            Assert.True(font.LineHeight > 0, $"{path} invalid LineHeight.");
            Assert.True(font.Bitmap.Width > 0, $"{path} invalid Bitmap width.");
            Assert.False(font.Bitmap.HasEmbeddedPalette, $"{path} should use uicolormap.cmp (HasEmbeddedPalette == false).");
            Assert.True(font.Bitmap.HasTransparency, $"{path} should have transparency enabled.");
            Assert.NotEmpty(font.Glyphs);
            Assert.True(font.TryGetGlyph('A', out var glyphA), $"{path} missing glyph 'A'.");
            Assert.True(glyphA.Width > 0);
            Assert.True(glyphA.XOffset >= 0);
            Assert.True(glyphA.XOffset + glyphA.Width <= font.Bitmap.Width);

            byte[] rgba = font.ToRgbaByteBuffer();
            Assert.Equal(font.Bitmap.Width * font.LineHeight * 4, rgba.Length);
            Assert.Contains(
                Enumerable.Range(0, rgba.Length / 4),
                idx => rgba[idx * 4 + 3] == 255 && (rgba[idx * 4] | rgba[idx * 4 + 1] | rgba[idx * 4 + 2]) != 0);
        }
    }

    [Fact]
    public void RetailGob_MenuFontPairs_NormalAndActiveHaveDistinctPaletteColors()
    {
        if (!File.Exists(SteamRes1HiPath))
            return;

        using var res1Hi = GobArchive.FromFile(SteamRes1HiPath);
        CmpPalette? uiPalette = null;
        if (File.Exists(SteamRes2Path))
        {
            using var res2 = GobArchive.FromFile(SteamRes2Path);
            if (res2.Entries.Any(e => e.Path.Equals("misc/cmp/uicolormap.cmp", StringComparison.OrdinalIgnoreCase)))
                uiPalette = CmpPalette.Parse(res2.ReadAllBytes("misc/cmp/uicolormap.cmp"));
        }

        (string Normal, string Active)[] pairs =
        {
            ("ui/sft/large0.sft", "ui/sft/large1.sft"),
            ("ui/sft/med0.sft", "ui/sft/med1.sft"),
        };

        foreach (var (normalPath, activePath) in pairs)
        {
            SftFont normal = SftFont.Parse(res1Hi.ReadAllBytes(normalPath));
            SftFont active = SftFont.Parse(res1Hi.ReadAllBytes(activePath));

            Assert.Equal(normal.LineHeight, active.LineHeight);
            Assert.Equal(normal.Bitmap.Width, active.Bitmap.Width);
            Assert.True(normal.TryGetGlyph('>', out _), $"{normalPath} should contain '>' indicator glyph.");
            Assert.True(active.TryGetGlyph('>', out _), $"{activePath} should contain '>' indicator glyph.");

            byte[] normalRgba = normal.ToRgbaByteBuffer(uiPalette);
            byte[] activeRgba = active.ToRgbaByteBuffer(uiPalette);

            Assert.Equal(normalRgba.Length, activeRgba.Length);
            Assert.False(normalRgba.AsSpan().SequenceEqual(activeRgba), $"{normalPath} and {activePath} should have distinct normal vs active palette colors.");
        }
    }

    [Fact]
    public void TryWindowToVirtual_MapsWidescreenPillarboxAndExactCoordinates()
    {
        // Exact 640x480 window
        Assert.True(MenuRenderer.TryWindowToVirtual(320f, 240f, 640, 480, out var exact));
        Assert.Equal(320f, exact.X, 0.01f);
        Assert.Equal(240f, exact.Y, 0.01f);

        // 1920x1080 (16:9): scale = 1080/480 = 2.25, viewport = 1440x1080, offsetX = 240
        Assert.False(MenuRenderer.TryWindowToVirtual(100f, 540f, 1920, 1080, out _));
        Assert.True(MenuRenderer.TryWindowToVirtual(240f, 0f, 1920, 1080, out var topLeft));
        Assert.Equal(0f, topLeft.X, 0.01f);
        Assert.Equal(0f, topLeft.Y, 0.01f);

        Assert.True(MenuRenderer.TryWindowToVirtual(960f, 540f, 1920, 1080, out var center));
        Assert.Equal(320f, center.X, 0.01f);
        Assert.Equal(240f, center.Y, 0.01f);

        // Invalid window dimensions
        Assert.False(MenuRenderer.TryWindowToVirtual(10f, 10f, 0, 480, out _));
        Assert.False(MenuRenderer.TryWindowToVirtual(10f, 10f, 640, -1, out _));
    }

    [Fact]
    public void MenuAction_FactoriesProduceExpectedKindsAndPayloads()
    {
        Assert.Equal(MenuActionKind.None, MenuAction.None.Kind);
        Assert.Equal(MenuActionKind.StartNewGame, MenuAction.StartNewGame.Kind);
        Assert.Equal(MenuActionKind.ResumeGame, MenuAction.ResumeGame.Kind);
        Assert.Equal(MenuActionKind.RestartLevel, MenuAction.RestartLevel.Kind);
        Assert.Equal(MenuActionKind.NextLevel, MenuAction.NextLevel.Kind);
        Assert.Equal(MenuActionKind.Quit, MenuAction.Quit.Kind);

        MenuAction load = MenuAction.LoadLevel("01narshadda.jkl", 2);
        Assert.Equal(MenuActionKind.LoadLevel, load.Kind);
        Assert.Equal("01narshadda.jkl", load.JklFile);
        Assert.Equal(2, load.Difficulty);
    }

    [Fact]
    public void SyntheticSft_SpaceCharacter_UsesSpaceWidthInsteadOfDefaultBoxGlyph()
    {
        SftFont fontWithMargin = BuildSyntheticSft(marginX: 5, marginY: 1, lineHeight: 16);
        Assert.False(fontWithMargin.TryGetGlyph(' ', out _));
        Assert.True(fontWithMargin.TryGetGlyphOrDefault(' ', out SftGlyph boxGlyph));
        Assert.Equal('?', boxGlyph.Character);
        Assert.Equal(12, boxGlyph.Width);

        // 'A' (5) + spacing (1) + 'B' (6) = 12
        Assert.Equal(12f, MenuRenderer.MeasureSftTextWidth(fontWithMargin, "AB"));
        // 'A' (5) + spacing (1) + space (5) + spacing (1) + 'B' (6) = 18 (NOT 25 from box glyph width 12)
        Assert.Equal(18f, MenuRenderer.MeasureSftTextWidth(fontWithMargin, "A B"));
        Assert.True(MenuRenderer.MeasureSftTextWidth(fontWithMargin, "A B") > MenuRenderer.MeasureSftTextWidth(fontWithMargin, "AB"));
        Assert.Equal(36f, HudRenderer.MeasureSftTextWidth(fontWithMargin, "A B", 2.0f));

        // When MarginX == 0, space width falls back cleanly without using the 12px box glyph
        SftFont fontZeroMargin = BuildSyntheticSft(marginX: 0, marginY: 1, lineHeight: 20);
        float spaceW = MenuRenderer.MeasureSftTextWidth(fontZeroMargin, " ");
        Assert.True(spaceW > 0f && spaceW < boxGlyph.Width);
        Assert.Equal(
            MenuRenderer.MeasureSftTextWidth(fontZeroMargin, "AB") + spaceW + fontZeroMargin.MarginY,
            MenuRenderer.MeasureSftTextWidth(fontZeroMargin, "A B"));
    }

    [Fact]
    public void RetailGob_SftFonts_SpaceDoesNotUseDefaultBoxGlyphAndMeasuresNaturally()
    {
        if (!File.Exists(SteamRes1HiPath))
            return;

        using var res1Hi = GobArchive.FromFile(SteamRes1HiPath);

        string[] fontPaths =
        {
            "ui/sft/large0.sft",
            "ui/sft/med0.sft",
            "ui/sft/small0.sft",
            "ui/sft/msgFont16.sft",
        };

        foreach (string path in fontPaths)
        {
            SftFont font = SftFont.Parse(res1Hi.ReadAllBytes(path));

            float abWidth = MenuRenderer.MeasureSftTextWidth(font, "AB");
            float aSpaceBWidth = MenuRenderer.MeasureSftTextWidth(font, "A B");
            float spaceWidth = MenuRenderer.MeasureSftTextWidth(font, " ");

            Assert.True(aSpaceBWidth > abWidth, $"{path}: 'A B' should be wider than 'AB'.");
            Assert.True(spaceWidth > 0f && spaceWidth <= font.LineHeight, $"{path}: unexpected space width {spaceWidth} for LineHeight {font.LineHeight}.");
            Assert.Equal(abWidth + spaceWidth + font.MarginY, aSpaceBWidth);
            Assert.Equal(aSpaceBWidth, HudRenderer.MeasureSftTextWidth(font, "A B"));
        }
    }

    private static SftFont BuildSyntheticSft(int marginX, int marginY, int lineHeight)
    {
        // 3 glyphs: '?' (x=0, w=12), 'A' (x=12, w=5), 'B' (x=17, w=6) => total width 23
        const int bmpW = 23;
        byte[] bmHeader = new byte[128];
        Encoding.ASCII.GetBytes("BM  ").CopyTo(bmHeader, 0);
        BitConverter.GetBytes(70).CopyTo(bmHeader, 4);      // version
        BitConverter.GetBytes(1).CopyTo(bmHeader, 12);      // palFmt (1 = external/colorkey)
        BitConverter.GetBytes(1).CopyTo(bmHeader, 16);      // numImages
        BitConverter.GetBytes(1).CopyTo(bmHeader, 28);      // transparent
        BitConverter.GetBytes(0).CopyTo(bmHeader, 32);      // is16Bit = 0
        BitConverter.GetBytes(8).CopyTo(bmHeader, 36);      // bpp = 8

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("SFNT"));
        bw.Write(10);           // version
        bw.Write(marginY);      // marginY
        bw.Write(marginX);      // marginX
        bw.Write((int)'?');     // defaultChar (box fallback)
        bw.Write(2);            // numCharsets
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        // Charset 0: '?'..'?'
        bw.Write((ushort)'?');
        bw.Write((ushort)'?');
        bw.Write(0); bw.Write(12);  // '?': x=0, w=12

        // Charset 1: 'A'..'B'
        bw.Write((ushort)'A');
        bw.Write((ushort)'B');
        bw.Write(12); bw.Write(5);  // 'A': x=12, w=5
        bw.Write(17); bw.Write(6);  // 'B': x=17, w=6

        // Embedded 8-bit BM
        bw.Write(bmHeader);
        bw.Write(bmpW);
        bw.Write(lineHeight);
        bw.Write(new byte[bmpW * lineHeight]);
        bw.Flush();

        return SftFont.Parse(ms.ToArray());
    }
}
