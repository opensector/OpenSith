using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenSith.Core.World;

/// <summary>
/// Type of sequence entry in an <c>episode.jk</c> campaign definition.
/// </summary>
public enum EpisodeEntryType
{
    Level = 0,
    Cut = 1,
    Decide = 2
}

/// <summary>
/// A single sequence entry parsed from an <c>episode.jk</c> file.
/// </summary>
public sealed class EpisodeEntry
{
    public int LineNumber { get; init; }
    public int CdNumber { get; init; }
    public int LevelNumber { get; init; }
    public EpisodeEntryType EntryType { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int LightPower { get; init; }
    public int DarkPower { get; init; }
    public int GotoA { get; init; }
    public int GotoB { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// Parsed representation of a Jedi Knight <c>episode.jk</c> campaign sequence file.
/// </summary>
public sealed class EpisodeDefinition
{
    public string Title { get; init; } = string.Empty;
    public int Type { get; init; }
    public int SequenceCount { get; init; }
    public IReadOnlyList<EpisodeEntry> Entries { get; init; } = [];

    /// <summary>
    /// Parses an <c>episode.jk</c> text payload following <c>jkEpisode_Load</c> rules.
    /// </summary>
    public static EpisodeDefinition Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Split('\n');
        int index = 0;

        string? titleLine = NextContentLine(lines, ref index);
        if (titleLine is null)
            throw new FormatException("Missing episode title in episode.jk.");

        string title = ExtractQuotedTitle(titleLine);

        string? typeLine = NextContentLine(lines, ref index);
        if (typeLine is null || !TryParseHeaderInt(typeLine, "TYPE", out int episodeType))
            throw new FormatException("Bad or missing 'TYPE n' line in episode.jk.");

        string? seqLine = NextContentLine(lines, ref index);
        if (seqLine is null || !TryParseHeaderInt(seqLine, "SEQ", out int seqCount) || seqCount < 0)
            throw new FormatException("Bad or missing 'SEQ n' line in episode.jk.");

        var entries = new List<EpisodeEntry>(seqCount);
        for (int i = 0; i < seqCount; i++)
        {
            string? entryLine = NextContentLine(lines, ref index);
            if (entryLine is null)
                throw new FormatException($"Unexpected end of episode.jk after {i} of {seqCount} entries.");

            if (StartsWithEndToken(entryLine))
                throw new FormatException($"Premature 'end' found in episode.jk after {i} entries (expected {seqCount}).");

            entries.Add(ParseEntryLine(entryLine));
        }

        string? endLine = NextContentLine(lines, ref index);
        if (endLine is null || !StartsWithEndToken(endLine))
            throw new FormatException("Missing trailing 'end' token in episode.jk.");

        return new EpisodeDefinition
        {
            Title = title,
            Type = episodeType,
            SequenceCount = seqCount,
            Entries = entries
        };
    }

    /// <summary>
    /// Returns all playable <see cref="EpisodeEntryType.Level"/> entries in sequence order.
    /// </summary>
    public IReadOnlyList<EpisodeEntry> GetPlayableLevels() =>
        Entries.Where(e => e.EntryType == EpisodeEntryType.Level).ToList();

    /// <summary>
    /// Finds the episode entry with the specified <see cref="EpisodeEntry.LineNumber"/>, or <c>null</c>.
    /// </summary>
    public EpisodeEntry? FindEntryByLine(int lineNumber) =>
        Entries.FirstOrDefault(e => e.LineNumber == lineNumber);

    /// <summary>
    /// Finds the first <see cref="EpisodeEntryType.Level"/> entry matching <paramref name="jklFileName"/>
    /// (case-insensitive, with or without <c>jkl/</c> prefix or <c>.jkl</c> extension).
    /// </summary>
    public EpisodeEntry? FindLevelEntry(string jklFileName)
    {
        if (string.IsNullOrWhiteSpace(jklFileName))
            return null;

        string target = NormalizeJklName(jklFileName);
        return Entries.FirstOrDefault(e =>
            e.EntryType == EpisodeEntryType.Level &&
            string.Equals(NormalizeJklName(e.FileName), target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Advances from <paramref name="currentJklFileName"/> along <see cref="EpisodeEntry.GotoA"/>
    /// (or <see cref="EpisodeEntry.GotoB"/> on <see cref="EpisodeEntryType.Decide"/> entries when
    /// <paramref name="isLightSide"/> is <c>false</c>) until the next <see cref="EpisodeEntryType.Level"/>
    /// entry is reached or the sequence terminates (<c>-1</c>).
    /// </summary>
    public EpisodeEntry? GetNextLevelAfter(string currentJklFileName, bool isLightSide = true)
    {
        var current = FindLevelEntry(currentJklFileName);
        if (current is null)
            return null;

        int nextLine = current.GotoA;
        var visited = new HashSet<int>();

        while (nextLine >= 0 && visited.Add(nextLine))
        {
            var entry = FindEntryByLine(nextLine);
            if (entry is null)
                return null;

            if (entry.EntryType == EpisodeEntryType.Level)
                return entry;

            nextLine = entry.EntryType == EpisodeEntryType.Decide && !isLightSide
                ? entry.GotoB
                : entry.GotoA;
        }

        return null;
    }

    private static string? NextContentLine(string[] lines, ref int index)
    {
        while (index < lines.Length)
        {
            string trimmed = lines[index++].Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            return trimmed;
        }

        return null;
    }

    private static string ExtractQuotedTitle(string line)
    {
        int firstQuote = line.IndexOf('"');
        if (firstQuote >= 0)
        {
            int lastQuote = line.LastIndexOf('"');
            if (lastQuote > firstQuote)
                return line.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
        }

        return StripInlineComment(line).Trim();
    }

    private static bool TryParseHeaderInt(string line, string keyword, out int value)
    {
        value = 0;
        string clean = StripInlineComment(line);
        var tokens = clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !tokens[0].Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool StartsWithEndToken(string line)
    {
        string clean = StripInlineComment(line);
        var tokens = clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && tokens[0].Equals("end", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripInlineComment(string line)
    {
        int hashIndex = line.IndexOf('#');
        return hashIndex >= 0 ? line[..hashIndex].Trim() : line.Trim();
    }

    private static EpisodeEntry ParseEntryLine(string line)
    {
        int hashIndex = line.IndexOf('#');
        string dataPart = hashIndex >= 0 ? line[..hashIndex] : line;
        string comment = hashIndex >= 0 ? line[(hashIndex + 1)..].Trim() : string.Empty;

        var tokens = dataPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 9)
            throw new FormatException($"Expected 9 fields in episode entry line, got {tokens.Length}: '{line}'.");

        string lineToken = tokens[0].TrimEnd(':');
        if (!int.TryParse(lineToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineNum))
            throw new FormatException($"Invalid line number '{tokens[0]}' in episode entry: '{line}'.");

        int cdNum = ParseInt(tokens[1], "cd", line);
        int levelNum = ParseInt(tokens[2], "level", line);
        EpisodeEntryType entryType = ParseEntryType(tokens[3], line);
        string fileName = tokens[4];
        int lightPow = ParseInt(tokens[5], "lightpow", line);
        int darkPow = ParseInt(tokens[6], "darkpow", line);
        int gotoA = ParseInt(tokens[7], "gotoA", line);
        int gotoB = ParseInt(tokens[8], "gotoB", line);

        return new EpisodeEntry
        {
            LineNumber = lineNum,
            CdNumber = cdNum,
            LevelNumber = levelNum,
            EntryType = entryType,
            FileName = fileName,
            LightPower = lightPow,
            DarkPower = darkPow,
            GotoA = gotoA,
            GotoB = gotoB,
            DisplayName = comment.Length > 0 ? comment : fileName
        };
    }

    private static int ParseInt(string token, string fieldName, string line)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new FormatException($"Invalid integer for {fieldName} ('{token}') in episode entry: '{line}'.");

        return value;
    }

    private static EpisodeEntryType ParseEntryType(string token, string line)
    {
        if (token.Equals("LEVEL", StringComparison.OrdinalIgnoreCase))
            return EpisodeEntryType.Level;
        if (token.Equals("CUT", StringComparison.OrdinalIgnoreCase))
            return EpisodeEntryType.Cut;
        if (token.Equals("DECIDE", StringComparison.OrdinalIgnoreCase))
            return EpisodeEntryType.Decide;

        throw new FormatException($"Unknown episode entry type '{token}' in line: '{line}'.");
    }

    private static string NormalizeJklName(string jklFileName)
    {
        string normalized = jklFileName.Trim().Replace('\\', '/');
        if (normalized.StartsWith("jkl/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        if (normalized.EndsWith(".jkl", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        return normalized;
    }
}
