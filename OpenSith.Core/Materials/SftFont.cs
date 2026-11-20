using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace OpenSith.Core.Materials;

/// <summary>
/// Glyph metrics within an SFT font's horizontal texture strip.
/// </summary>
public readonly record struct SftGlyph(char Character, int XOffset, int Width, int Height);

/// <summary>
/// A contiguous inclusive character range defined in an SFT font charset table.
/// </summary>
public readonly record struct SftCharsetRange(char FirstChar, char LastChar);

/// <summary>
/// Parser and representation of a LucasArts Sith Engine SFT ("SFNT") bitmap font file.
/// </summary>
public sealed class SftFont
{
    private const uint MagicSfnt = 0x544E4653; // "SFNT" little-endian
    private const int SupportedVersion = 10;
    private const int HeaderSize = 40;
    private const int CharsetHeaderSize = 4;
    private const int GlyphEntrySize = 8;

    private readonly Dictionary<char, SftGlyph> _glyphs;

    public int Version { get; }
    public int MarginY { get; }
    public int MarginX { get; }
    public char DefaultChar { get; }
    public int CharsetCount { get; }
    public int LineHeight => Bitmap.Height;
    public BmBitmap Bitmap { get; }
    public IReadOnlyList<SftCharsetRange> CharsetRanges { get; }
    public IReadOnlyDictionary<char, SftGlyph> Glyphs => _glyphs;

    private SftFont(
        int version,
        int marginY,
        int marginX,
        char defaultChar,
        int charsetCount,
        BmBitmap bitmap,
        IReadOnlyList<SftCharsetRange> charsetRanges,
        Dictionary<char, SftGlyph> glyphs)
    {
        Version = version;
        MarginY = marginY;
        MarginX = marginX;
        DefaultChar = defaultChar;
        CharsetCount = charsetCount;
        Bitmap = bitmap;
        CharsetRanges = charsetRanges;
        _glyphs = glyphs;
    }

    /// <summary>
    /// Parses a LucasArts "SFNT" font file from raw binary bytes.
    /// </summary>
    public static SftFont Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException($"Data too short for SFT header ({data.Length} bytes).");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        if (magic != MagicSfnt)
            throw new InvalidDataException($"Invalid SFT magic: 0x{magic:X8}. Expected 'SFNT'.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(data[4..8]);
        if (version != SupportedVersion)
            throw new InvalidDataException($"Unsupported SFT version: {version}. Expected {SupportedVersion}.");

        int marginY = BinaryPrimitives.ReadInt32LittleEndian(data[8..12]);
        int marginX = BinaryPrimitives.ReadInt32LittleEndian(data[12..16]);
        int defaultCharRaw = BinaryPrimitives.ReadInt32LittleEndian(data[16..20]);
        char defaultChar = (char)(ushort)defaultCharRaw;

        int charsetCount = BinaryPrimitives.ReadInt32LittleEndian(data[20..24]);
        if (charsetCount < 1 || charsetCount > 1024)
            throw new InvalidDataException($"Invalid SFT charset count: {charsetCount}.");

        int offset = HeaderSize;
        var ranges = new List<SftCharsetRange>(charsetCount);
        var pendingEntries = new List<(char Character, int XOffset, int Width)>();

        for (int c = 0; c < charsetCount; c++)
        {
            if (offset + CharsetHeaderSize > data.Length)
                throw new InvalidDataException($"SFT file truncated reading charset {c} header.");

            ushort charFirst = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            ushort charLast = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2));
            offset += CharsetHeaderSize;

            if (charLast < charFirst)
                throw new InvalidDataException($"Invalid SFT charset {c} range: {charFirst}..{charLast}.");

            int count = charLast - charFirst + 1;
            int entriesBytes = checked(count * GlyphEntrySize);
            if (offset + entriesBytes > data.Length)
                throw new InvalidDataException($"SFT file truncated reading charset {c} entries.");

            ranges.Add(new SftCharsetRange((char)charFirst, (char)charLast));

            for (int i = 0; i < count; i++)
            {
                int entryOffset = offset + (i * GlyphEntrySize);
                int glyphTexX = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(entryOffset, 4));
                int glyphWidth = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(entryOffset + 4, 4));
                pendingEntries.Add(((char)(charFirst + i), glyphTexX, glyphWidth));
            }

            offset += entriesBytes;
        }

        BmBitmap bitmap = BmBitmap.Parse(data.Slice(offset));
        int lineHeight = bitmap.Height;

        var glyphs = new Dictionary<char, SftGlyph>(pendingEntries.Count);
        foreach (var (ch, xOffset, width) in pendingEntries)
        {
            glyphs.TryAdd(ch, new SftGlyph(ch, xOffset, width, lineHeight));
        }

        return new SftFont(
            version,
            marginY,
            marginX,
            defaultChar,
            charsetCount,
            bitmap,
            ranges,
            glyphs);
    }

    /// <summary>
    /// Looks up glyph metrics for the specified character.
    /// </summary>
    public bool TryGetGlyph(char ch, out SftGlyph glyph) => _glyphs.TryGetValue(ch, out glyph);

    /// <summary>
    /// Looks up glyph metrics for the specified character, falling back to <see cref="DefaultChar"/> if missing.
    /// </summary>
    public bool TryGetGlyphOrDefault(char ch, out SftGlyph glyph) =>
        _glyphs.TryGetValue(ch, out glyph) || _glyphs.TryGetValue(DefaultChar, out glyph);

    /// <summary>
    /// Decodes the embedded font bitmap strip into a 32-bit RGBA byte buffer.
    /// </summary>
    public byte[] ToRgbaByteBuffer(CmpPalette? palette = null) => Bitmap.ToRgbaByteBuffer(palette);
}
