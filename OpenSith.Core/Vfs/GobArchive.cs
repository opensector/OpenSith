using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenSith.Core.Vfs;

/// <summary>
/// Parser and reader for LucasArts GOB (Game OBject) and GOO archives.
/// Features zero-copy reading where possible and fully cross-platform case-insensitive path normalization.
/// </summary>
public sealed class GobArchive : IDisposable, IEnumerable<GobEntry>
{
    private const uint MagicGob = 0x20424F47; // "GOB " little-endian
    private const uint MagicGoo = 0x204F4F47; // "GOO " little-endian
    private const int EntryHeaderSize = 136;   // 4 (offset) + 4 (length) + 128 (name)

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Dictionary<string, GobEntry> _entriesByPath;
    private readonly List<GobEntry> _entries;

    public string? FilePath { get; }
    public int EntryCount => _entries.Count;
    public IReadOnlyList<GobEntry> Entries => _entries;

    private GobArchive(Stream stream, string? filePath, bool leaveOpen)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        FilePath = filePath;
        _entriesByPath = new Dictionary<string, GobEntry>(StringComparer.OrdinalIgnoreCase);
        _entries = new List<GobEntry>();

        ParseIndex();
    }

    /// <summary>
    /// Opens a GOB archive from a file on disk.
    /// </summary>
    public static GobArchive FromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"GOB file not found: {path}", path);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        return new GobArchive(stream, path, leaveOpen: false);
    }

    /// <summary>
    /// Opens a GOB archive from an existing seekable stream.
    /// </summary>
    public static GobArchive FromStream(Stream stream, bool leaveOpen = false)
    {
        return new GobArchive(stream, null, leaveOpen);
    }

    private void ParseIndex()
    {
        if (!_stream.CanSeek)
            throw new InvalidOperationException("GOB stream must be seekable.");

        Span<byte> header = stackalloc byte[16];
        _stream.Seek(0, SeekOrigin.Begin);
        int read = _stream.Read(header);
        if (read < 16)
            throw new InvalidDataException("GOB file is too small to contain a valid header.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..4]);
        if (magic != MagicGob && magic != MagicGoo)
        {
            throw new InvalidDataException($"Invalid GOB signature: 0x{magic:X8}. Expected 'GOB ' or 'GOO '.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        if (version != 20)
        {
            throw new InvalidDataException($"Unsupported GOB version: {version}. Expected version 20.");
        }

        int indexOffset = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        int fileCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);

        if (fileCount < 0 || fileCount > 100_000)
            throw new InvalidDataException($"Unreasonable file count in GOB: {fileCount}");

        _stream.Seek(indexOffset + 4, SeekOrigin.Begin); // Index begins right after the 4-byte count

        byte[] entryBuffer = new byte[fileCount * EntryHeaderSize];
        int totalIndexBytes = _stream.Read(entryBuffer);
        if (totalIndexBytes < entryBuffer.Length)
            throw new InvalidDataException("Incomplete GOB index table.");

        ReadOnlySpan<byte> indexSpan = entryBuffer;

        for (int i = 0; i < fileCount; i++)
        {
            ReadOnlySpan<byte> slice = indexSpan.Slice(i * EntryHeaderSize, EntryHeaderSize);
            int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(slice[0..4]);
            int dataLength = BinaryPrimitives.ReadInt32LittleEndian(slice[4..8]);

            ReadOnlySpan<byte> nameBytes = slice.Slice(8, 128);
            int nullPos = nameBytes.IndexOf((byte)0);
            if (nullPos >= 0)
                nameBytes = nameBytes[..nullPos];

            string rawName = Encoding.ASCII.GetString(nameBytes);
            string normalizedPath = NormalizePath(rawName);

            var entry = new GobEntry(normalizedPath, dataOffset, dataLength);
            _entries.Add(entry);
            _entriesByPath[normalizedPath] = entry;
        }
    }

    /// <summary>
    /// Checks if a file path exists within the archive (case-insensitive, slash-agnostic).
    /// </summary>
    public bool Contains(string path) => _entriesByPath.ContainsKey(NormalizePath(path));

    /// <summary>
    /// Retrieves entry metadata for a given path.
    /// </summary>
    public bool TryGetEntry(string path, out GobEntry entry) =>
        _entriesByPath.TryGetValue(NormalizePath(path), out entry);

    /// <summary>
    /// Reads the entire contents of a file inside the archive into a byte array.
    /// </summary>
    public byte[] ReadAllBytes(string path)
    {
        if (!TryGetEntry(path, out var entry))
            throw new FileNotFoundException($"Entry '{path}' not found in GOB archive '{FilePath ?? "<stream>"}'.");

        byte[] data = new byte[entry.Length];
        lock (_stream)
        {
            _stream.Seek(entry.Offset, SeekOrigin.Begin);
            int read = _stream.Read(data);
            if (read != entry.Length)
                throw new EndOfStreamException($"Expected {entry.Length} bytes for '{path}', but read {read} bytes.");
        }
        return data;
    }

    /// <summary>
    /// Opens a readable sub-stream for a file inside the archive without copying the whole payload.
    /// </summary>
    public Stream OpenRead(string path)
    {
        if (!TryGetEntry(path, out var entry))
            throw new FileNotFoundException($"Entry '{path}' not found in GOB archive '{FilePath ?? "<stream>"}'.");

        byte[] data = ReadAllBytes(path);
        return new MemoryStream(data, writable: false);
    }

    /// <summary>
    /// Normalizes Windows-style backslashes to standard forward slashes and trims whitespace.
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Replace('\\', '/').TrimStart('/').Trim();
    }

    public IEnumerator<GobEntry> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
