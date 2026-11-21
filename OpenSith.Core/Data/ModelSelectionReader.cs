using System.Globalization;

namespace OpenSith.Core.Data;

/// <summary>
/// Represents a selectable player character model entry parsed from <c>misc/models.dat</c>.
/// </summary>
public sealed record ModelSelectionEntry(
    int Index,
    string Model3D,
    string SoundClass,
    string DisplayName);

/// <summary>
/// Parser for LucasArts Jedi Knight <c>misc/models.dat</c> multiplayer character selection tables.
/// </summary>
public static class ModelSelectionReader
{
    /// <summary>
    /// Parses all character model selection entries from a <c>models.dat</c> text payload.
    /// </summary>
    public static IReadOnlyList<ModelSelectionEntry> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entries = new List<ModelSelectionEntry>();
        using var reader = new StringReader(content);

        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            int hashIdx = trimmed.IndexOf('#');
            string dataPart = (hashIdx >= 0 ? trimmed[..hashIdx] : trimmed).Trim();
            string? commentPart = hashIdx >= 0 ? trimmed[(hashIdx + 1)..].Trim() : null;

            if (dataPart.Length == 0)
                continue;

            if (dataPart.Equals("end", StringComparison.OrdinalIgnoreCase))
                break;

            if (TrySkipCountHeader(dataPart, "numModels", lineNumber))
                continue;

            string[] tokens = dataPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                throw new FormatException(
                    $"Expected 3 fields ('index: model.3do sound.snd') in models.dat line {lineNumber}, got {tokens.Length}: '{dataPart}'.");
            }

            string indexToken = tokens[0].TrimEnd(':');
            if (!int.TryParse(indexToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                throw new FormatException($"Invalid model index '{tokens[0]}' in models.dat line {lineNumber}.");
            }

            string model3D = tokens[1];
            string soundClass = tokens[2];
            string displayName = ExtractDisplayName(commentPart, model3D);

            entries.Add(new ModelSelectionEntry(index, model3D, soundClass, displayName));
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

    private static string ExtractDisplayName(string? commentPart, string model3D)
    {
        if (!string.IsNullOrEmpty(commentPart))
        {
            int firstQuote = commentPart.IndexOf('"');
            if (firstQuote >= 0)
            {
                int secondQuote = commentPart.IndexOf('"', firstQuote + 1);
                if (secondQuote > firstQuote + 1)
                {
                    return commentPart[(firstQuote + 1)..secondQuote];
                }
            }
        }

        return Path.GetFileNameWithoutExtension(model3D);
    }
}
