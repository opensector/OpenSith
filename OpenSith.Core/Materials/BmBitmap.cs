using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace OpenSith.Core.Materials;

/// <summary>
/// Pixel format descriptor stored in a Sith Engine BM bitmap header (matches rdTexFormat).
/// </summary>
public readonly record struct BmPixelFormat(
    bool Is16Bit,
    int BitsPerPixel,
    int RedBits,
    int GreenBits,
    int BlueBits,
    int RedShift,
    int GreenShift,
    int BlueShift,
    int RedBitDiff,
    int GreenBitDiff,
    int BlueBitDiff,
    int AlphaShift,
    int AlphaBits,
    int AlphaBitDiff);

/// <summary>
/// A single cel or mip frame within a Sith Engine BM bitmap.
/// </summary>
public sealed class BmFrame
{
    private readonly Rgba32[]? _embeddedPalette;

    public int Width { get; }
    public int Height { get; }
    public int BitsPerPixel { get; }
    public bool IsCompressed { get; }
    public uint TransparentColor { get; }
    public bool HasTransparency { get; }
    public BmPixelFormat Format { get; }
    public byte[] PixelData { get; }

    internal BmFrame(
        int width,
        int height,
        int bitsPerPixel,
        bool isCompressed,
        uint transparentColor,
        bool hasTransparency,
        BmPixelFormat format,
        byte[] pixelData,
        Rgba32[]? embeddedPalette)
    {
        Width = width;
        Height = height;
        BitsPerPixel = bitsPerPixel;
        IsCompressed = isCompressed;
        TransparentColor = transparentColor;
        HasTransparency = hasTransparency;
        Format = format;
        PixelData = pixelData;
        _embeddedPalette = embeddedPalette;
    }

    /// <summary>
    /// Decodes this frame into a contiguous 32-bit RGBA byte buffer (Width * Height * 4 bytes).
    /// Supports 8-bit paletted surfaces (external or embedded palette) and 16-bit RGB565 / RGBA5551
    /// surfaces with colorkey alpha=0 transparency.
    /// </summary>
    public byte[] ToRgbaByteBuffer(CmpPalette? palette = null)
    {
        int pixelCount = Width * Height;
        byte[] rgba = new byte[pixelCount * 4];

        switch (BitsPerPixel)
        {
            case 8:
                Decode8Bit(rgba, pixelCount, palette?.Colors ?? _embeddedPalette);
                break;
            case 16:
                Decode16Bit(rgba, pixelCount);
                break;
            case 24:
                Decode24Bit(rgba, pixelCount);
                break;
            case 32:
                Decode32Bit(rgba, pixelCount);
                break;
            default:
                throw new NotSupportedException($"Unsupported BM bit depth: {BitsPerPixel} bpp.");
        }

        return rgba;
    }

    private void Decode8Bit(byte[] rgba, int pixelCount, Rgba32[]? colors)
    {
        byte key = (byte)TransparentColor;

        for (int i = 0; i < pixelCount; i++)
        {
            byte index = PixelData[i];
            int dst = i * 4;

            if (HasTransparency && index == key)
            {
                rgba[dst + 0] = 0;
                rgba[dst + 1] = 0;
                rgba[dst + 2] = 0;
                rgba[dst + 3] = 0;
                continue;
            }

            if (colors is not null)
            {
                Rgba32 c = colors[index];
                rgba[dst + 0] = c.R;
                rgba[dst + 1] = c.G;
                rgba[dst + 2] = c.B;
                rgba[dst + 3] = 255;
            }
            else
            {
                rgba[dst + 0] = index;
                rgba[dst + 1] = index;
                rgba[dst + 2] = index;
                rgba[dst + 3] = 255;
            }
        }
    }

    private void Decode16Bit(byte[] rgba, int pixelCount)
    {
        // Fall back to standard RGB565 if bitfield descriptors are zeroed.
        bool hasBitfields = Format.RedBits > 0 || Format.GreenBits > 0 || Format.BlueBits > 0;
        int rBits = hasBitfields ? Format.RedBits : 5;
        int gBits = hasBitfields ? Format.GreenBits : 6;
        int bBits = hasBitfields ? Format.BlueBits : 5;
        int rShift = hasBitfields ? Format.RedShift : 11;
        int gShift = hasBitfields ? Format.GreenShift : 5;
        int bShift = hasBitfields ? Format.BlueShift : 0;

        int rMask = (1 << rBits) - 1;
        int gMask = (1 << gBits) - 1;
        int bMask = (1 << bBits) - 1;
        int aMask = Format.AlphaBits > 0 ? (1 << Format.AlphaBits) - 1 : 0;

        bool is555 = rBits == 5 && gBits == 5 && bBits == 5;
        bool isRgba5551 = is555 && rShift == 11 && gShift == 6 && bShift == 1;
        ushort colorKey = (ushort)TransparentColor;

        ReadOnlySpan<byte> src = PixelData;
        for (int i = 0; i < pixelCount; i++)
        {
            ushort pixel = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
            int dst = i * 4;

            if (HasTransparency && pixel == colorKey)
            {
                rgba[dst + 0] = 0;
                rgba[dst + 1] = 0;
                rgba[dst + 2] = 0;
                rgba[dst + 3] = 0;
                continue;
            }

            byte r = ExpandBitsTo8((pixel >> rShift) & rMask, rBits);
            byte g = ExpandBitsTo8((pixel >> gShift) & gMask, gBits);
            byte b = ExpandBitsTo8((pixel >> bShift) & bMask, bBits);
            byte a;

            if (Format.AlphaBits > 0)
            {
                a = ExpandBitsTo8((pixel >> Format.AlphaShift) & aMask, Format.AlphaBits);
            }
            else if (is555)
            {
                int alphaBit = isRgba5551 ? (pixel & 0x0001) : ((pixel >> 15) & 0x0001);
                a = alphaBit != 0 ? (byte)255 : (byte)0;
            }
            else
            {
                a = 255;
            }

            rgba[dst + 0] = r;
            rgba[dst + 1] = g;
            rgba[dst + 2] = b;
            rgba[dst + 3] = a;
        }
    }

    private void Decode24Bit(byte[] rgba, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            int src = i * 3;
            int dst = i * 4;
            rgba[dst + 0] = PixelData[src + 2];
            rgba[dst + 1] = PixelData[src + 1];
            rgba[dst + 2] = PixelData[src + 0];
            rgba[dst + 3] = 255;
        }
    }

    private void Decode32Bit(byte[] rgba, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            rgba[offset + 0] = PixelData[offset + 2];
            rgba[offset + 1] = PixelData[offset + 1];
            rgba[offset + 2] = PixelData[offset + 0];
            rgba[offset + 3] = PixelData[offset + 3];
        }
    }

    private static byte ExpandBitsTo8(int value, int bitCount)
    {
        return bitCount switch
        {
            0 => 0,
            1 => value != 0 ? (byte)255 : (byte)0,
            5 => (byte)((value * 527 + 23) >> 6),
            6 => (byte)((value * 259 + 33) >> 6),
            8 => (byte)value,
            _ => (byte)((value * 255 + ((1 << bitCount) - 1) / 2) / ((1 << bitCount) - 1)),
        };
    }
}

/// <summary>
/// Parser and representation of a LucasArts Sith Engine BM ("BM  ") bitmap file.
/// Supports single-frame and multi-cel HUD/UI bitmaps in 8-bit paletted and 16-bit RGB565/RGBA5551 formats.
/// </summary>
public sealed class BmBitmap
{
    private const uint MagicBm = 0x20204D42; // "BM  " little-endian
    private const int SupportedVersion = 70;
    private const int HeaderSize = 128;
    private const int PaletteEntryCount = 256;
    private const int EmbeddedPaletteSize = PaletteEntryCount * 3;

    public int Version { get; }
    public bool IsCompressed { get; }
    public uint PaletteFormat { get; }
    public bool HasTransparency => (PaletteFormat & 1) != 0;
    public bool HasEmbeddedPalette => (PaletteFormat & 2) != 0;
    public int XPos { get; }
    public int YPos { get; }
    public uint TransparentColor { get; }
    public BmPixelFormat PixelFormat { get; }
    public Rgba32[]? EmbeddedPalette { get; }
    public List<BmFrame> Frames { get; }

    public int Width => Frames.Count > 0 ? Frames[0].Width : 0;
    public int Height => Frames.Count > 0 ? Frames[0].Height : 0;

    private BmBitmap(
        int version,
        bool isCompressed,
        uint paletteFormat,
        int xPos,
        int yPos,
        uint transparentColor,
        BmPixelFormat pixelFormat,
        Rgba32[]? embeddedPalette,
        List<BmFrame> frames)
    {
        Version = version;
        IsCompressed = isCompressed;
        PaletteFormat = paletteFormat;
        XPos = xPos;
        YPos = yPos;
        TransparentColor = transparentColor;
        PixelFormat = pixelFormat;
        EmbeddedPalette = embeddedPalette;
        Frames = frames;
    }

    /// <summary>
    /// Parses a LucasArts "BM  " bitmap from raw binary bytes.
    /// </summary>
    public static BmBitmap Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException($"Data too short for BM header ({data.Length} bytes).");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        if (magic != MagicBm)
            throw new InvalidDataException($"Invalid BM magic: 0x{magic:X8}. Expected 'BM  '.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(data[4..8]);
        if (version != SupportedVersion)
            throw new InvalidDataException($"Unsupported BM version: {version}. Expected {SupportedVersion}.");

        bool isCompressed = BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]) != 0;
        uint paletteFormat = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]);
        int frameCount = BinaryPrimitives.ReadInt32LittleEndian(data[16..20]);
        if (frameCount < 0 || frameCount > 4096)
            throw new InvalidDataException($"Invalid BM frame count: {frameCount}.");

        int xPos = BinaryPrimitives.ReadInt32LittleEndian(data[20..24]);
        int yPos = BinaryPrimitives.ReadInt32LittleEndian(data[24..28]);
        uint transparentColor = BinaryPrimitives.ReadUInt32LittleEndian(data[28..32]);

        bool is16Bit = BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]) != 0;
        int bpp = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[36..40]);
        if (bpp == 0)
            bpp = is16Bit ? 16 : 8;

        if (bpp is not (8 or 16 or 24 or 32))
            throw new InvalidDataException($"Unsupported BM bits-per-pixel: {bpp}.");

        var pixelFormat = new BmPixelFormat(
            Is16Bit: is16Bit,
            BitsPerPixel: bpp,
            RedBits: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[40..44]),
            GreenBits: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[44..48]),
            BlueBits: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[48..52]),
            RedShift: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[52..56]),
            GreenShift: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[56..60]),
            BlueShift: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[60..64]),
            RedBitDiff: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[64..68]),
            GreenBitDiff: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[68..72]),
            BlueBitDiff: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[72..76]),
            AlphaShift: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[76..80]),
            AlphaBits: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[80..84]),
            AlphaBitDiff: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[84..88]));

        bool hasTransparency = (paletteFormat & 1) != 0;
        bool hasEmbeddedPalette = (paletteFormat & 2) != 0;
        int bytesPerPixel = bpp / 8;

        int offset = HeaderSize;
        var rawFrameSlices = new List<(int Width, int Height, byte[] Pixels)>(frameCount);

        for (int i = 0; i < frameCount; i++)
        {
            if (offset + 8 > data.Length)
                throw new InvalidDataException($"BM file truncated reading frame {i} dimensions.");

            int width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
            offset += 8;

            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                throw new InvalidDataException($"Invalid BM frame {i} dimensions: {width}x{height}.");

            int pixelBytes = checked(width * height * bytesPerPixel);
            if (offset + pixelBytes > data.Length)
                throw new InvalidDataException($"BM file truncated reading frame {i} pixels (expected {pixelBytes} bytes).");

            byte[] pixels = new byte[pixelBytes];
            data.Slice(offset, pixelBytes).CopyTo(pixels);
            offset += pixelBytes;

            rawFrameSlices.Add((width, height, pixels));
        }

        Rgba32[]? embeddedPalette = null;
        if (hasEmbeddedPalette)
        {
            if (offset + EmbeddedPaletteSize > data.Length)
                throw new InvalidDataException($"BM file truncated reading embedded palette.");

            embeddedPalette = new Rgba32[PaletteEntryCount];
            for (int i = 0; i < PaletteEntryCount; i++)
            {
                byte r = data[offset + (i * 3) + 0];
                byte g = data[offset + (i * 3) + 1];
                byte b = data[offset + (i * 3) + 2];
                byte a = (hasTransparency && i == (int)transparentColor) ? (byte)0 : (byte)255;
                embeddedPalette[i] = new Rgba32(r, g, b, a);
            }
        }

        var frames = new List<BmFrame>(frameCount);
        foreach (var (width, height, pixels) in rawFrameSlices)
        {
            frames.Add(new BmFrame(
                width,
                height,
                bpp,
                isCompressed,
                transparentColor,
                hasTransparency,
                pixelFormat,
                pixels,
                embeddedPalette));
        }

        return new BmBitmap(
            version,
            isCompressed,
            paletteFormat,
            xPos,
            yPos,
            transparentColor,
            pixelFormat,
            embeddedPalette,
            frames);
    }

    /// <summary>
    /// Converts the specified frame (default 0) into a 32-bit RGBA byte buffer.
    /// </summary>
    public byte[] ToRgbaByteBuffer(CmpPalette? palette = null, int frameIndex = 0)
    {
        if ((uint)frameIndex >= (uint)Frames.Count)
            throw new ArgumentOutOfRangeException(nameof(frameIndex), $"Frame index {frameIndex} out of range (count: {Frames.Count}).");

        return Frames[frameIndex].ToRgbaByteBuffer(palette);
    }
}
