using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using OpenSith.Core.Vfs;

namespace OpenSith.Core.Data;

/// <summary>
/// Case-insensitive string lookup table parsed from LucasArts Sith Engine <c>.uni</c> localization files
/// (<c>sithstrings.uni</c>, <c>jkstrings.uni</c>, <c>cogstrings.uni</c>, <c>cutstrings.uni</c>).
/// </summary>
public sealed class UniStringTable
{
    private static readonly string[] VfsLoadOrder =
    [
        "misc/sithstrings.uni",
        "ui/jkstrings.uni",
        "misc/cogstrings.uni"
    ];

    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes an empty <see cref="UniStringTable"/>.
    /// </summary>
    public UniStringTable()
    {
        Entries = new ReadOnlyDictionary<string, string>(_entries);
    }

    /// <summary>
    /// Number of key/value pairs stored in this string table.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Read-only view of all parsed key/value entries (case-insensitive keys).
    /// </summary>
    public IReadOnlyDictionary<string, string> Entries { get; }

    /// <summary>
    /// Parses a <c>.uni</c> text payload into a <see cref="UniStringTable"/>.
    /// </summary>
    public static UniStringTable Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var table = new UniStringTable();
        using var reader = new StringReader(content);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ReadOnlySpan<char> span = line.AsSpan().TrimStart();
            if (span.IsEmpty || span[0] == '#')
                continue;

            if (StartsWithKeyword(span, "MSGS"))
                continue;

            if (StartsWithKeyword(span, "END"))
                break;

            if (TryParseEntryLine(span, out string key, out string value))
            {
                table._entries[key] = value;
            }
        }

        return table;
    }

    /// <summary>
    /// Merges all entries from <paramref name="overlay"/> into this table, overwriting existing keys (case-insensitive).
    /// </summary>
    public void Merge(UniStringTable overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        foreach (var kvp in overlay._entries)
        {
            _entries[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Looks up a string by its exact key (case-insensitive).
    /// </summary>
    public bool TryGetValue(string key, out string value)
    {
        if (string.IsNullOrEmpty(key))
        {
            value = string.Empty;
            return false;
        }

        if (_entries.TryGetValue(key, out string? found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Looks up a COG localized string by numeric ID, checking <c>COG_{id:D5}</c> then <c>COG_{id}</c>.
    /// </summary>
    public bool TryGetCogString(int strId, out string value) =>
        TryGetValue($"COG_{strId:D5}", out value) ||
        TryGetValue($"COG_{strId}", out value);

    /// <summary>
    /// Looks up a mission goal string by numeric ID, checking <c>GOAL_{id:D5}</c> then <c>GOAL_{id:D4}</c>.
    /// </summary>
    public bool TryGetGoalString(int goalId, out string value) =>
        TryGetValue($"GOAL_{goalId:D5}", out value) ||
        TryGetValue($"GOAL_{goalId:D4}", out value);

    /// <summary>
    /// Loads and merges string tables from <paramref name="vfs"/> in canonical priority order:
    /// <c>misc/sithstrings.uni</c> -&gt; <c>ui/jkstrings.uni</c> -&gt; <c>misc/cogstrings.uni</c>.
    /// Missing files are ignored cleanly.
    /// </summary>
    public static UniStringTable LoadFromVfs(IVirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        var table = new UniStringTable();
        foreach (string relativePath in VfsLoadOrder)
        {
            string? resolved = ResolveVfsPath(vfs, relativePath);
            if (resolved is null)
                continue;

            string text = vfs.ReadAllText(resolved);
            table.Merge(Parse(text));
        }

        return table;
    }

    private static bool StartsWithKeyword(ReadOnlySpan<char> span, string keyword)
    {
        return span.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
               (span.Length == keyword.Length || char.IsWhiteSpace(span[keyword.Length]) || span[keyword.Length] == '#');
    }

    private static bool TryParseEntryLine(ReadOnlySpan<char> span, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        if (span.IsEmpty || span[0] != '"')
            return false;

        int pos = 0;
        if (!TryReadQuotedToken(span, ref pos, out key))
            return false;

        SkipWhitespace(span, ref pos);
        int flagStart = pos;
        while (pos < span.Length && !char.IsWhiteSpace(span[pos]) && span[pos] != '"' && span[pos] != '#')
        {
            pos++;
        }

        if (pos == flagStart ||
            !int.TryParse(span[flagStart..pos], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        SkipWhitespace(span, ref pos);
        if (pos >= span.Length || span[pos] == '#')
            return false;

        if (span[pos] == '"')
            return TryReadQuotedToken(span, ref pos, out value);

        // Fallback for rare retail typos with unquoted values (e.g., ui/credits.uni line 235).
        int commentIdx = span[pos..].IndexOf('#');
        ReadOnlySpan<char> rawValue = commentIdx >= 0 ? span.Slice(pos, commentIdx) : span[pos..];
        value = rawValue.TrimEnd().ToString();
        return value.Length > 0;
    }

    private static bool TryReadQuotedToken(ReadOnlySpan<char> span, ref int pos, out string result)
    {
        if (pos >= span.Length || span[pos] != '"')
        {
            result = string.Empty;
            return false;
        }

        pos++; // Skip opening quote
        var sb = new StringBuilder();

        while (pos < span.Length)
        {
            char c = span[pos];
            if (c == '\\' && pos + 1 < span.Length)
            {
                char next = span[pos + 1];
                if (next == '"')
                {
                    // Retail ui/jkstrings.uni uses "\" for DIK_BACKSLASH rather than "\\".
                    if (IsClosingQuoteAfterBackslash(span[(pos + 2)..]))
                    {
                        sb.Append('\\');
                        pos += 2;
                        result = sb.ToString();
                        return true;
                    }

                    sb.Append('"');
                    pos += 2;
                    continue;
                }

                switch (next)
                {
                    case 'n':
                        sb.Append('\n');
                        pos += 2;
                        continue;
                    case 'r':
                        sb.Append('\r');
                        pos += 2;
                        continue;
                    case 't':
                        sb.Append('\t');
                        pos += 2;
                        continue;
                    case '\\':
                        sb.Append('\\');
                        pos += 2;
                        continue;
                    default:
                        sb.Append(c);
                        sb.Append(next);
                        pos += 2;
                        continue;
                }
            }

            if (c == '"')
            {
                pos++;
                result = sb.ToString();
                return true;
            }

            sb.Append(c);
            pos++;
        }

        result = string.Empty;
        return false;
    }

    private static bool IsClosingQuoteAfterBackslash(ReadOnlySpan<char> remainder)
    {
        ReadOnlySpan<char> trimmed = remainder.TrimStart();
        if (trimmed.IsEmpty)
            return true;

        if (trimmed[0] == '#')
        {
            int quoteCount = 0;
            foreach (char ch in trimmed)
            {
                if (ch == '"')
                    quoteCount++;
            }

            return quoteCount % 2 == 0;
        }

        return false;
    }

    private static void SkipWhitespace(ReadOnlySpan<char> span, ref int pos)
    {
        while (pos < span.Length && char.IsWhiteSpace(span[pos]))
        {
            pos++;
        }
    }

    private static string? ResolveVfsPath(IVirtualFileSystem vfs, string relativePath)
    {
        if (vfs.FileExists(relativePath))
            return relativePath;

        string lower = relativePath.ToLowerInvariant();
        if (vfs.FileExists(lower))
            return lower;

        string upper = relativePath.ToUpperInvariant();
        if (vfs.FileExists(upper))
            return upper;

        int slashIdx = relativePath.LastIndexOf('/');
        if (slashIdx <= 0)
            return null;

        string dir = relativePath[..slashIdx];
        string file = relativePath[(slashIdx + 1)..];
        string dirTitle = char.ToUpperInvariant(dir[0]) + dir[1..].ToLowerInvariant();
        string fileTitle = char.ToUpperInvariant(file[0]) + file[1..].ToLowerInvariant();
        string camelStrings = file.Replace("strings", "Strings", StringComparison.OrdinalIgnoreCase);

        string[] variants =
        [
            $"{dir.ToLowerInvariant()}/{fileTitle}",
            $"{dir.ToLowerInvariant()}/{camelStrings}",
            $"{dir.ToUpperInvariant()}/{file.ToLowerInvariant()}",
            $"{dirTitle}/{file.ToLowerInvariant()}",
            $"{dirTitle}/{fileTitle}"
        ];

        foreach (string variant in variants)
        {
            if (vfs.FileExists(variant))
                return variant;
        }

        return null;
    }
}
