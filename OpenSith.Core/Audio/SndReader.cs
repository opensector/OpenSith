using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenSith.Core.Audio;

/// <summary>
/// High-performance parser for LucasArts Sith Engine sound class definition (.snd) files.
/// </summary>
public static class SndReader
{
    private static readonly char[] CommentChars = { '#', ';' };
    private static readonly char[] TokenDelimiters = { ' ', '\t', ',' };

    /// <summary>
    /// Parses a sound class definition from text.
    /// </summary>
    public static SoundClass Parse(string text, string name = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return Read(reader, name);
    }

    /// <summary>
    /// Parses a sound class definition from a byte buffer.
    /// </summary>
    public static SoundClass Read(ReadOnlySpan<byte> bytes, string name = "")
    {
        string text = Encoding.ASCII.GetString(bytes);
        return Parse(text, name);
    }

    /// <summary>
    /// Parses a sound class definition from an open stream.
    /// </summary>
    public static SoundClass Read(Stream stream, string name = "")
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Read(reader, name);
    }

    /// <summary>
    /// Parses a sound class definition from a <see cref="TextReader"/>.
    /// </summary>
    public static SoundClass Read(TextReader reader, string name = "")
    {
        ArgumentNullException.ThrowIfNull(reader);
        var soundClass = new SoundClass(name);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Strip comments starting with '#' or ';'
            int commentIdx = line.IndexOfAny(CommentChars);
            if (commentIdx >= 0)
            {
                line = line[..commentIdx];
            }

            line = line.Trim();
            if (line.Length == 0)
                continue;

            string[] tokens = line.Split(TokenDelimiters, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                continue;

            string eventStr = tokens[0].ToLowerInvariant();
            string soundFile = tokens[1];

            // Resolve event enum
            SoundEvent evt = SoundEventExtensions.TryParse(eventStr, out var resolvedEvt)
                ? resolvedEvt
                : SoundEvent.Reserved;

            SoundFlags playFlags = SoundClassEntry.DefaultPlayFlags;
            float minRadius = SoundClassEntry.DefaultMinRadius;
            float maxRadius = SoundClassEntry.DefaultMaxRadius;
            float maxVolume = SoundClassEntry.DefaultMaxVolume;

            if (tokens.Length > 2)
            {
                playFlags = ParseFlags(tokens[2]);
            }

            if (tokens.Length > 3 && float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float rMin))
            {
                minRadius = rMin;
            }

            if (tokens.Length > 4 && float.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float rMax))
            {
                maxRadius = rMax;
            }

            if (tokens.Length > 5 && float.TryParse(tokens[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float vol))
            {
                maxVolume = vol;
            }

            soundClass.AddEntry(new SoundClassEntry(
                evt,
                eventStr,
                soundFile,
                playFlags,
                minRadius,
                maxRadius,
                maxVolume));
        }

        return soundClass;
    }

    private static SoundFlags ParseFlags(string token)
    {
        token = token.Trim();
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexVal))
                return (SoundFlags)hexVal;
        }
        else if (uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint decVal))
        {
            return (SoundFlags)decVal;
        }
        else if (uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexFallback))
        {
            return (SoundFlags)hexFallback;
        }

        return SoundClassEntry.DefaultPlayFlags;
    }
}
