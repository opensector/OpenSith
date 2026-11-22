using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace OpenSith.Core.Materials;

/// <summary>
/// Single animation cel decoded from a Sith Engine MAT file.
/// </summary>
public sealed record MatCel(
    int Width,
    int Height,
    bool AlphaEnabled,
    int TransparentColorIndex,
    byte[] IndexedPixels,
    byte[]? DirectRgbaPixels = null);

/// <summary>
/// Parser and representation of a LucasArts Sith Engine MAT (Material/Texture) file.
/// Decodes 8-bit paletted texture surfaces, animation cels, and mipmaps into modern 32-bit RGBA buffers.
/// </summary>
public sealed class MatMaterial
{
    private const uint MagicMat = 0x2054414D; // "MAT " little-endian
    private const int MaterialHeaderSize = 76;   // rdMaterialHeader (0x4C)
    private const int TexinfoHeaderSize = 24;    // rdTexinfoHeader (0x18)
    private const int TexinfoExtHeaderSize = 16; // rdTexinfoExtHeader (0x10)
    private const int TextureHeaderSize = 24;    // rdTextureHeader (0x18)

    public int Version { get; }
    public int MaterialType { get; }
    public int MatType => MaterialType;
    public int RecordCount { get; }
    public int TextureCount { get; }
    public int BitDepth { get; }
    public int SolidColorIndex { get; }
    public bool AlphaEnabled { get; }
    public int TransparentColorIndex { get; }
    public int Width { get; }
    public int Height { get; }
    public int MipmapCount { get; }
    public byte[] IndexedPixels { get; }
    public IReadOnlyList<MatCel> Cels { get; }
    public int CelCount => Cels.Count;

    private MatMaterial(
        int version,
        int materialType,
        int recordCount,
        int textureCount,
        int bitDepth,
        int solidColorIndex,
        bool alphaEnabled,
        int transparentColorIndex,
        int width,
        int height,
        int mipmapCount,
        byte[] indexedPixels,
        IReadOnlyList<MatCel> cels)
    {
        Version = version;
        MaterialType = materialType;
        RecordCount = recordCount;
        TextureCount = textureCount;
        BitDepth = bitDepth;
        SolidColorIndex = solidColorIndex;
        AlphaEnabled = alphaEnabled;
        TransparentColorIndex = transparentColorIndex;
        Width = width;
        Height = height;
        MipmapCount = mipmapCount;
        IndexedPixels = indexedPixels;
        Cels = cels;
    }

    /// <summary>
    /// Parses a MAT file from a byte span, supporting solid-color (type 0) and multi-cel textured (type 2) materials.
    /// </summary>
    public static MatMaterial Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MaterialHeaderSize)
            throw new InvalidDataException($"Data too short for MAT material header ({data.Length} bytes).");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        if (magic != MagicMat)
            throw new InvalidDataException($"Invalid MAT magic: 0x{magic:X8}. Expected 'MAT '.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(data[4..8]);
        int materialType = BinaryPrimitives.ReadInt32LittleEndian(data[8..12]);
        int recordCount = BinaryPrimitives.ReadInt32LittleEndian(data[12..16]);
        int textureCount = BinaryPrimitives.ReadInt32LittleEndian(data[16..20]);
        int bitDepth = BinaryPrimitives.ReadInt32LittleEndian(data[24..28]);

        if (recordCount < 0)
            throw new InvalidDataException($"Invalid MAT record count: {recordCount}.");

        int offset = MaterialHeaderSize;
        int solidColorIndex = 0;

        for (int i = 0; i < recordCount; i++)
        {
            if (data.Length < offset + TexinfoHeaderSize)
                throw new InvalidDataException($"MAT file truncated reading texinfo record {i} ({data.Length} bytes).");

            uint texInfoType = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            uint solidColor = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            if (i == 0)
                solidColorIndex = (int)(solidColor & 0xFF);

            offset += TexinfoHeaderSize;

            if ((texInfoType & 8) != 0)
            {
                if (data.Length < offset + TexinfoExtHeaderSize)
                    throw new InvalidDataException($"MAT file truncated reading texinfo ext header {i} ({data.Length} bytes).");
                offset += TexinfoExtHeaderSize;
            }
        }

        if (materialType == 0 || textureCount <= 0)
        {
            byte[] solidPixels = [(byte)solidColorIndex];
            var solidCel = new MatCel(1, 1, AlphaEnabled: false, TransparentColorIndex: 0, solidPixels);
            return new MatMaterial(
                version,
                materialType,
                recordCount,
                textureCount,
                bitDepth,
                solidColorIndex,
                alphaEnabled: false,
                transparentColorIndex: 0,
                width: 1,
                height: 1,
                mipmapCount: 0,
                indexedPixels: solidPixels,
                cels: [solidCel]);
        }

        int bytesPerPixel = bitDepth == 16 ? 2 : 1;
        int remainingHeaders = Math.Max(0, (data.Length - offset) / TextureHeaderSize);
        var cels = new List<MatCel>(Math.Min(textureCount, remainingHeaders));
        int firstWidth = 0;
        int firstHeight = 0;
        bool firstAlpha = false;
        int firstTransparentColor = 0;
        int firstMipmapCount = 0;

        for (int celIdx = 0; celIdx < textureCount; celIdx++)
        {
            if (data.Length < offset + TextureHeaderSize)
            {
                if (celIdx == 0)
                    throw new InvalidDataException($"MAT file truncated reading texture header ({data.Length} bytes).");
                break;
            }

            int width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 0, 4));
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
            uint alphaEn = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 8, 4));
            uint colorTransparent = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 16, 4));
            int mipmapCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 20, 4));
            offset += TextureHeaderSize;

            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                throw new InvalidDataException($"Invalid texture dimensions: {width}x{height}");
            if (mipmapCount < 0 || mipmapCount > 32)
                throw new InvalidDataException($"Invalid mipmap count: {mipmapCount}");

            int mip0Bytes = width * height * bytesPerPixel;
            if (data.Length < offset + mip0Bytes)
            {
                if (celIdx == 0)
                    throw new InvalidDataException($"MAT file truncated: expected at least {offset + mip0Bytes} bytes, found {data.Length}.");
                break;
            }

            bool celAlpha = (alphaEn & 1) != 0;
            int celTransIdx = (int)(colorTransparent & (bytesPerPixel == 2 ? 0xFFFFu : 0xFFu));
            byte[] pixels = new byte[width * height];
            byte[]? directRgba = null;
            if (bytesPerPixel == 1)
            {
                data.Slice(offset, mip0Bytes).CopyTo(pixels);
            }
            else
            {
                directRgba = new byte[width * height * 4];
                for (int p = 0; p < width * height; p++)
                {
                    ushort px = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + p * 2, 2));
                    bool transparent = celAlpha && px == (ushort)celTransIdx;
                    byte r = (byte)(((px >> 11) & 0x1F) * 255 / 31);
                    byte g = (byte)(((px >> 5) & 0x3F) * 255 / 63);
                    byte b = (byte)((px & 0x1F) * 255 / 31);
                    int dOff = p * 4;
                    directRgba[dOff + 0] = r;
                    directRgba[dOff + 1] = g;
                    directRgba[dOff + 2] = b;
                    directRgba[dOff + 3] = transparent ? (byte)0 : (byte)255;
                }
            }
            offset += mip0Bytes;

            int totalMips = Math.Max(1, mipmapCount);
            for (int mip = 1; mip < totalMips; mip++)
            {
                int shift = Math.Min(mip, 31);
                int mipW = Math.Max(1, width >> shift);
                int mipH = Math.Max(1, height >> shift);
                int mipBytes = mipW * mipH * bytesPerPixel;
                if (data.Length < offset + mipBytes)
                {
                    offset = data.Length;
                    break;
                }
                offset += mipBytes;
            }

            if (celIdx == 0)
            {
                firstWidth = width;
                firstHeight = height;
                firstAlpha = celAlpha;
                firstTransparentColor = celTransIdx;
                firstMipmapCount = mipmapCount;
            }

            cels.Add(new MatCel(width, height, celAlpha, celTransIdx, pixels, directRgba));
        }

        return new MatMaterial(
            version,
            materialType,
            recordCount,
            textureCount,
            bitDepth,
            solidColorIndex,
            firstAlpha,
            firstTransparentColor,
            firstWidth,
            firstHeight,
            firstMipmapCount,
            cels[0].IndexedPixels,
            cels);
    }

    /// <summary>
    /// Converts cel 0 of the 8-bit indexed texture into a 32-bit RGBA pixel array using the specified CMP palette.
    /// </summary>
    public Rgba32[] ToRgba32(CmpPalette palette, int lightLevel = 0) =>
        ToRgba32(palette, lightLevel, celIndex: 0);

    /// <summary>
    /// Converts the specified cel into a 32-bit RGBA pixel array using the specified CMP palette.
    /// </summary>
    public Rgba32[] ToRgba32(CmpPalette palette, int lightLevel, int celIndex)
    {
        ArgumentNullException.ThrowIfNull(palette);
        MatCel cel = Cels[Math.Clamp(celIndex, 0, Cels.Count - 1)];

        var rgba = new Rgba32[cel.Width * cel.Height];
        if (cel.DirectRgbaPixels != null)
        {
            for (int i = 0; i < rgba.Length; i++)
            {
                int dOff = i * 4;
                rgba[i] = new Rgba32(
                    cel.DirectRgbaPixels[dOff + 0],
                    cel.DirectRgbaPixels[dOff + 1],
                    cel.DirectRgbaPixels[dOff + 2],
                    cel.DirectRgbaPixels[dOff + 3]);
            }
            return rgba;
        }

        for (int i = 0; i < cel.IndexedPixels.Length; i++)
        {
            byte paletteIndex = cel.IndexedPixels[i];
            rgba[i] = DecodeCelPixel(palette, cel, paletteIndex, lightLevel);
        }
        return rgba;
    }

    /// <summary>
    /// Converts cel 0 into a raw 32-bit RGBA byte buffer (Width * Height * 4 bytes)
    /// suitable for direct texture upload to OpenGL, Vulkan, Metal, or DirectX.
    /// </summary>
    public byte[] ToRgbaByteBuffer(CmpPalette palette, int lightLevel = 0) =>
        ToRgbaByteBuffer(palette, lightLevel, celIndex: 0);

    /// <summary>
    /// Converts the specified cel into a raw 32-bit RGBA byte buffer (Width * Height * 4 bytes)
    /// suitable for direct texture upload to OpenGL, Vulkan, Metal, or DirectX.
    /// </summary>
    public byte[] ToRgbaByteBuffer(CmpPalette palette, int lightLevel, int celIndex)
    {
        ArgumentNullException.ThrowIfNull(palette);
        MatCel cel = Cels[Math.Clamp(celIndex, 0, Cels.Count - 1)];
        if (cel.DirectRgbaPixels != null)
            return (byte[])cel.DirectRgbaPixels.Clone();

        byte[] buffer = new byte[cel.Width * cel.Height * 4];
        for (int i = 0; i < cel.IndexedPixels.Length; i++)
        {
            byte paletteIndex = cel.IndexedPixels[i];
            Rgba32 color = DecodeCelPixel(palette, cel, paletteIndex, lightLevel);

            int offset = i * 4;
            buffer[offset + 0] = color.R;
            buffer[offset + 1] = color.G;
            buffer[offset + 2] = color.B;
            buffer[offset + 3] = color.A;
        }
        return buffer;
    }

    private static Rgba32 DecodeCelPixel(CmpPalette palette, MatCel cel, byte paletteIndex, int lightLevel)
    {
        if (cel.AlphaEnabled && paletteIndex == cel.TransparentColorIndex)
            return new Rgba32(0, 0, 0, 0);

        return palette.GetShadedColor(paletteIndex, lightLevel, useColorKey: false);
    }
}
