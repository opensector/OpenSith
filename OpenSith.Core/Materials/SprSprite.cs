using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using OpenSith.Core.Vfs;

namespace OpenSith.Core.Materials;

/// <summary>
/// Parsed retail Jedi Knight sprite definition (<c>.spr</c> from <c>misc/spr/</c>).
/// </summary>
public sealed record SprSprite(
    string MaterialName,
    int SpriteType,
    float Width,
    float Height,
    int GeoMode,
    int LightMode,
    int TexMode,
    float ExtraLight,
    Vector3 Offset)
{
    /// <summary>
    /// Parses a Jedi Knight <c>.spr</c> sprite definition payload.
    /// Format: <c>material type width height geo light tex extralight xoff yoff zoff</c>.
    /// </summary>
    public static SprSprite Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
                line = line[..commentIdx];

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4)
                throw new FormatException($"Invalid .spr sprite line (expected at least 4 fields, got {tokens.Length}): '{trimmed}'.");

            string materialName = tokens[0];
            int spriteType = ParseInt(tokens, 1, 0);
            float width = ParseFloat(tokens, 2, 0.2f);
            float height = ParseFloat(tokens, 3, width);
            int geoMode = ParseInt(tokens, 4, 4);
            int lightMode = ParseInt(tokens, 5, 0);
            int texMode = ParseInt(tokens, 6, 0);
            float extraLight = ParseFloat(tokens, 7, 0f);
            float xOff = ParseFloat(tokens, 8, 0f);
            float yOff = ParseFloat(tokens, 9, 0f);
            float zOff = ParseFloat(tokens, 10, 0f);

            return new SprSprite(
                materialName,
                spriteType,
                width,
                height,
                geoMode,
                lightMode,
                texMode,
                extraLight,
                new Vector3(xOff, yOff, zOff));
        }

        throw new FormatException("No valid sprite definition found in .spr payload.");
    }

    /// <summary>
    /// Attempts to load and parse a <c>.spr</c> asset from standard retail VFS paths.
    /// </summary>
    public static bool TryLoad(IVirtualFileSystem vfs, string spriteName, out SprSprite? sprite)
    {
        sprite = null;
        if (vfs == null || string.IsNullOrWhiteSpace(spriteName))
            return false;

        string cleanName = Path.GetFileName(spriteName.Trim());
        if (!cleanName.EndsWith(".spr", StringComparison.OrdinalIgnoreCase))
            cleanName += ".spr";

        foreach (string candidate in new[]
        {
            $"misc/spr/{cleanName}",
            $"spr/{cleanName}",
            spriteName.Trim(),
            cleanName
        })
        {
            if (!vfs.FileExists(candidate))
                continue;

            try
            {
                sprite = Parse(vfs.ReadAllText(candidate));
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static int ParseInt(string[] tokens, int index, int fallback) =>
        index < tokens.Length && int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int val)
            ? val
            : fallback;

    private static float ParseFloat(string[] tokens, int index, float fallback) =>
        index < tokens.Length && float.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float val)
            ? val
            : fallback;
}
