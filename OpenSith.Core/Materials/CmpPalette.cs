using System;
using System.Buffers.Binary;
using System.IO;

namespace OpenSith.Core.Materials;

/// <summary>
/// Parser and representation of a Sith Engine CMP (Color Map) palette file.
/// Contains the 256 RGB color table plus precalculated 64-level shading/tinting lookup tables.
/// </summary>
public sealed class CmpPalette
{
    private const uint MagicCmp = 0x20504D43; // "CMP " little-endian
    private const int HeaderSize = 64;
    private const int PaletteEntryCount = 256;
    private const int PaletteDataSize = PaletteEntryCount * 3; // 768 bytes

    public int Version { get; }
    public bool HasTransparency { get; }
    public Rgba32[] Colors { get; }
    public int LightLevelCount { get; }
    public byte[] LightMapData { get; }

    private CmpPalette(int version, bool hasTransparency, Rgba32[] colors, int lightLevelCount, byte[] lightMapData)
    {
        Version = version;
        HasTransparency = hasTransparency;
        Colors = colors;
        LightLevelCount = lightLevelCount;
        LightMapData = lightMapData;
    }

    /// <summary>
    /// Parses a CMP palette from raw binary bytes.
    /// </summary>
    public static CmpPalette Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + PaletteDataSize)
            throw new InvalidDataException($"Data too short for valid CMP palette ({data.Length} bytes).");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        if (magic != MagicCmp)
            throw new InvalidDataException($"Invalid CMP magic: 0x{magic:X8}. Expected 'CMP '.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(data[4..8]);
        int transparency = BinaryPrimitives.ReadInt32LittleEndian(data[8..12]);
        bool hasTransparency = transparency != 0;

        var colors = new Rgba32[PaletteEntryCount];
        int paletteOffset = HeaderSize;

        for (int i = 0; i < PaletteEntryCount; i++)
        {
            byte r = data[paletteOffset + (i * 3) + 0];
            byte g = data[paletteOffset + (i * 3) + 1];
            byte b = data[paletteOffset + (i * 3) + 2];

            // Store all 256 palette entries as opaque RGB; color-key transparency on index 0
            // is gated per-material by MAT alpha_en & 1 rather than globally by CMP.
            colors[i] = new Rgba32(r, g, b, 255);
        }

        int lightMapOffset = HeaderSize + PaletteDataSize;
        int remainingBytes = data.Length - lightMapOffset;
        int lightLevelCount = remainingBytes / PaletteEntryCount;

        byte[] lightMapData = new byte[remainingBytes];
        data.Slice(lightMapOffset, remainingBytes).CopyTo(lightMapData);

        return new CmpPalette(version, hasTransparency, colors, lightLevelCount, lightMapData);
    }

    /// <summary>
    /// Resolves an 8-bit palette index through a lighting level to an RGBA color.
    /// When <paramref name="useColorKey"/> is true, palette index 0 is returned as transparent (0, 0, 0, 0).
    /// </summary>
    public Rgba32 GetShadedColor(byte colorIndex, int lightLevel = 0, bool useColorKey = false)
    {
        if (useColorKey && colorIndex == 0)
            return new Rgba32(0, 0, 0, 0);

        if (LightLevelCount == 0 || lightLevel <= 0)
            return Colors[colorIndex];

        int clampedLevel = Math.Clamp(lightLevel, 0, LightLevelCount - 1);
        int lookupIndex = (clampedLevel * PaletteEntryCount) + colorIndex;
        byte shadedColorIndex = LightMapData[lookupIndex];

        return Colors[shadedColorIndex];
    }

    /// <summary>
    /// Exports the 256 colors as a contiguous 32-bit RGBA byte array (1024 bytes) for GPU texture upload.
    /// </summary>
    public byte[] ToRgbaBuffer()
    {
        byte[] buffer = new byte[PaletteEntryCount * 4];
        for (int i = 0; i < PaletteEntryCount; i++)
        {
            buffer[(i * 4) + 0] = Colors[i].R;
            buffer[(i * 4) + 1] = Colors[i].G;
            buffer[(i * 4) + 2] = Colors[i].B;
            buffer[(i * 4) + 3] = Colors[i].A;
        }
        return buffer;
    }
}
