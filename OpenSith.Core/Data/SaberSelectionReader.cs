using System.Globalization;

namespace OpenSith.Core.Data;

/// <summary>
/// Represents a selectable lightsaber color definition parsed from <c>misc/sabers.dat</c>.
/// </summary>
public sealed record SaberSelectionEntry(
    int Index,
    string PreviewBitmap,
    string SideMaterial,
    string TipMaterial);

/// <summary>
/// Parser for LucasArts Jedi Knight <c>misc/sabers.dat</c> lightsaber color selection tables.
/// </summary>
public static class SaberSelectionReader
{
    /// <summary>
    /// Parses all lightsaber entries from a <c>sabers.dat</c> text payload.
    /// </summary>
    public static IReadOnlyList<SaberSelectionEntry> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entries = new List<SaberSelectionEntry>();
        using var reader = new StringReader(content);

        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            int commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
            {
                line = line[..commentIdx];
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
                break;

            if (TrySkipCountHeader(trimmed, "numSabers", lineNumber))
                continue;

            string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4)
            {
                throw new FormatException(
                    $"Expected 4 fields ('index: preview.bm side.mat tip.mat') in sabers.dat line {lineNumber}, got {tokens.Length}: '{trimmed}'.");
            }

            string indexToken = tokens[0].TrimEnd(':');
            if (!int.TryParse(indexToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                throw new FormatException($"Invalid saber index '{tokens[0]}' in sabers.dat line {lineNumber}.");
            }

            entries.Add(new SaberSelectionEntry(index, tokens[1], tokens[2], tokens[3]));
        }

        return entries;
    }

    private static bool TrySkipCountHeader(string dataPart, string headerName, int lineNumber)
    {
        if (!dataPart.StartsWith(headerName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (dataPart.Length > headerName.Length &&
            dataPart[headerName.Length] != ':' &&
            !char.IsWhiteSpace(dataPart[headerName.Length]))
        {
            return false;
        }

        string countPart = dataPart[headerName.Length..].TrimStart(':', ' ', '\t').Trim();
        if (countPart.Length > 0 &&
            !int.TryParse(countPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new FormatException($"Invalid {headerName} count '{countPart}' in line {lineNumber}.");
        }

        return true;
    }
}
