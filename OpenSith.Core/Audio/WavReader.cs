using System;
using System.Buffers.Binary;
using System.IO;

namespace OpenSith.Core.Audio;

/// <summary>
/// Fast zero-allocation parser and PCM sample extractor for RIFF/WAVE audio assets.
/// </summary>
public static class WavReader
{
    private const uint MagicRiff = 0x46464952; // "RIFF" little-endian
    private const uint MagicWave = 0x45564157; // "WAVE" little-endian
    private const uint ChunkFmt  = 0x20746D66; // "fmt " little-endian
    private const uint ChunkData = 0x61746164; // "data" little-endian
    private const uint ChunkSmpl = 0x6C706D73; // "smpl" little-endian

    /// <summary>
    /// Parses RIFF/WAVE header metadata from an in-memory byte span.
    /// </summary>
    public static WavInfo Parse(ReadOnlySpan<byte> wavData)
    {
        if (!TryParse(wavData, out var info, out string? error))
        {
            throw new InvalidDataException(error ?? "Invalid RIFF/WAVE audio data.");
        }
        return info!;
    }

    /// <summary>
    /// Attempts to parse RIFF/WAVE header metadata from a byte span.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> wavData, out WavInfo? info) =>
        TryParse(wavData, out info, out _);

    /// <summary>
    /// Attempts to parse RIFF/WAVE header metadata with detailed error message.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> wavData, out WavInfo? info, out string? error)
    {
        info = null;
        error = null;

        if (wavData.Length < 12)
        {
            error = $"WAV data too short ({wavData.Length} bytes). Minimum is 12 bytes.";
            return false;
        }

        uint riffMagic = BinaryPrimitives.ReadUInt32LittleEndian(wavData[0..4]);
        if (riffMagic != MagicRiff)
        {
            error = $"Invalid RIFF signature: 0x{riffMagic:X8}. Expected 'RIFF'.";
            return false;
        }

        uint waveMagic = BinaryPrimitives.ReadUInt32LittleEndian(wavData[8..12]);
        if (waveMagic != MagicWave)
        {
            error = $"Invalid WAVE signature: 0x{waveMagic:X8}. Expected 'WAVE'.";
            return false;
        }

        WaveAudioFormat audioFormat = WaveAudioFormat.Unknown;
        ushort channels = 0;
        uint sampleRate = 0;
        uint byteRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;

        int dataOffset = -1;
        int dataSize = -1;
        uint? loopStart = null;
        uint? loopEnd = null;

        bool hasFmt = false;
        bool hasData = false;

        int offset = 12;
        while (offset + 8 <= wavData.Length)
        {
            uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(wavData.Slice(offset, 4));
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wavData.Slice(offset + 4, 4));
            int chunkDataOffset = offset + 8;

            if (chunkId == ChunkFmt)
            {
                if (chunkSize < 16 || chunkDataOffset + 16 > wavData.Length)
                {
                    error = $"Malformed 'fmt ' chunk size: {chunkSize}.";
                    return false;
                }

                audioFormat = (WaveAudioFormat)BinaryPrimitives.ReadUInt16LittleEndian(wavData.Slice(chunkDataOffset, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wavData.Slice(chunkDataOffset + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wavData.Slice(chunkDataOffset + 4, 4));
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wavData.Slice(chunkDataOffset + 8, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wavData.Slice(chunkDataOffset + 12, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wavData.Slice(chunkDataOffset + 14, 2));
                hasFmt = true;
            }
            else if (chunkId == ChunkData)
            {
                dataOffset = chunkDataOffset;
                // Handle files where reported chunk size exceeds available buffer
                int available = Math.Max(0, wavData.Length - chunkDataOffset);
                dataSize = (int)Math.Min((long)chunkSize, available);
                hasData = true;
            }
            else if (chunkId == ChunkSmpl)
            {
                // Parse optional sampler loop points if present
                if (chunkSize >= 36 && chunkDataOffset + 36 <= wavData.Length)
                {
                    uint numSampleLoops = BinaryPrimitives.ReadUInt32LittleEndian(wavData.Slice(chunkDataOffset + 28, 4));
                    if (numSampleLoops > 0 && chunkDataOffset + 36 + 24 <= wavData.Length)
                    {
                        // First loop record starts at offset 36 within chunk data
                        int loopOffset = chunkDataOffset + 36;
                        loopStart = BinaryPrimitives.ReadUInt32LittleEndian(wavData.Slice(loopOffset + 8, 4));
                        loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(wavData.Slice(loopOffset + 12, 4));
                    }
                }
            }

            // Chunks are 2-byte aligned in RIFF containers
            offset = chunkDataOffset + (int)chunkSize;
            if ((chunkSize & 1) != 0)
            {
                offset++;
            }
        }

        if (!hasFmt)
        {
            error = "WAV file is missing required 'fmt ' chunk.";
            return false;
        }

        if (!hasData || dataOffset < 0)
        {
            error = "WAV file is missing required 'data' chunk.";
            return false;
        }

        info = new WavInfo
        {
            AudioFormat = audioFormat,
            Channels = channels,
            SampleRate = sampleRate,
            ByteRate = byteRate,
            BlockAlign = blockAlign,
            BitsPerSample = bitsPerSample,
            DataOffset = dataOffset,
            DataSize = dataSize,
            LoopStart = loopStart,
            LoopEnd = loopEnd,
        };

        return true;
    }

    /// <summary>
    /// Extracts a zero-copy slice containing the raw PCM audio samples from a WAV byte span.
    /// </summary>
    public static ReadOnlySpan<byte> ExtractPcmData(ReadOnlySpan<byte> wavData)
    {
        var info = Parse(wavData);
        return wavData.Slice(info.DataOffset, info.DataSize);
    }

    /// <summary>
    /// Extracts a zero-copy slice of raw PCM audio samples along with parsed metadata.
    /// </summary>
    public static ReadOnlySpan<byte> ExtractPcmData(ReadOnlySpan<byte> wavData, out WavInfo info)
    {
        info = Parse(wavData);
        return wavData.Slice(info.DataOffset, info.DataSize);
    }

    /// <summary>
    /// Extracts raw PCM audio samples as a newly allocated byte array.
    /// </summary>
    public static byte[] ExtractPcmBytes(ReadOnlySpan<byte> wavData) =>
        ExtractPcmData(wavData).ToArray();

    /// <summary>
    /// Parses a WAV file from a stream.
    /// </summary>
    public static WavInfo Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.ToArray());
    }

    /// <summary>
    /// Parses a WAV file from a byte array.
    /// </summary>
    public static WavInfo Parse(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Parse(buffer.AsSpan());
    }

    /// <summary>
    /// Parses a WAV file from a disk path.
    /// </summary>
    public static WavInfo ParseFile(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        return Parse(bytes);
    }
}
