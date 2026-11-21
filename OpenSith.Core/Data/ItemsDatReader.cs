using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenSith.Core.Data;

/// <summary>
/// Represents a single inventory bin definition parsed from <c>misc/items.dat</c>.
/// </summary>
public sealed record ItemDefinition(
    string Name,
    int BinId,
    float MinValue,
    float MaxValue,
    uint Flags,
    string? CogName,
    IReadOnlyDictionary<string, string> Args);

/// <summary>
/// Parser for LucasArts Jedi Knight <c>misc/items.dat</c> inventory bin definition files.
/// </summary>
public static class ItemsDatReader
{
    /// <summary>
    /// Parses all item definitions from an <c>items.dat</c> text payload in file order.
    /// </summary>
    public static IReadOnlyList<ItemDefinition> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var items = new List<ItemDefinition>();
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

            string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 1 && tokens[0].Equals("end", StringComparison.OrdinalIgnoreCase))
                break;

            if (tokens.Length < 5)
            {
                throw new FormatException(
                    $"Expected at least 5 fields in items.dat line {lineNumber}, got {tokens.Length}: '{trimmed}'.");
            }

            string name = tokens[0];
            if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int binId))
            {
                throw new FormatException($"Invalid bin id '{tokens[1]}' in items.dat line {lineNumber}.");
            }

            if (!float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float minValue))
            {
                throw new FormatException($"Invalid min value '{tokens[2]}' in items.dat line {lineNumber}.");
            }

            if (!float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float maxValue))
            {
                throw new FormatException($"Invalid max value '{tokens[3]}' in items.dat line {lineNumber}.");
            }

            if (!TryParseFlags(tokens[4], out uint flags))
            {
                throw new FormatException($"Invalid flags '{tokens[4]}' in items.dat line {lineNumber}.");
            }

            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? cogName = null;

            for (int i = 5; i < tokens.Length; i++)
            {
                string argToken = tokens[i];
                int eqIdx = argToken.IndexOf('=');
                if (eqIdx > 0)
                {
                    string key = argToken[..eqIdx].Trim();
                    string val = argToken[(eqIdx + 1)..].Trim();
                    args[key] = val;

                    if (key.Equals("cog", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                    {
                        cogName = val;
                    }
                }
                else
                {
                    args[argToken] = string.Empty;
                }
            }

            items.Add(new ItemDefinition(
                name,
                binId,
                minValue,
                maxValue,
                flags,
                cogName,
                new ReadOnlyDictionary<string, string>(args)));
        }

        return items;
    }

    /// <summary>
    /// Parses all item definitions from an <c>items.dat</c> text payload indexed by <see cref="ItemDefinition.BinId"/>.
    /// </summary>
    public static Dictionary<int, ItemDefinition> ParseById(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        IReadOnlyList<ItemDefinition> items = Parse(content);
        var byId = new Dictionary<int, ItemDefinition>(items.Count);
        foreach (ItemDefinition item in items)
        {
            byId[item.BinId] = item;
        }

        return byId;
    }

    private static bool TryParseFlags(string token, out uint flags)
    {
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flags);
        }

        return uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out flags);
    }
}
