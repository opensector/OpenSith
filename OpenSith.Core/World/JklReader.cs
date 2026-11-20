using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace OpenSith.Core.World;

/// <summary>
/// High-performance parser for Jedi Knight level (.JKL) files.
/// </summary>
public sealed class JklReader
{
    public static JklLevel Parse(string text, string levelName = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        var level = new JklLevel { Name = levelName };

        using var reader = new StringReader(text);
        string? line;
        string currentSection = string.Empty;
        JklSector? currentSector = null;

        while ((line = reader.ReadLine()) != null)
        {
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("SECTION:", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = line["SECTION:".Length..].Trim().ToUpperInvariant();
                continue;
            }

            switch (currentSection)
            {
                case "SOUNDS":
                    ParseSounds(line, level);
                    break;
                case "MATERIALS":
                    ParseMaterials(line, level);
                    break;
                case "GEORESOURCE":
                    ParseGeoresource(line, reader, level);
                    break;
                case "SECTORS":
                    ParseSectors(line, reader, level, ref currentSector);
                    break;
                case "COGSCRIPTS":
                    ParseCogScripts(line, level);
                    break;
                case "COGS":
                    ParseCogs(line, level);
                    break;
                case "TEMPLATES":
                    ParseTemplates(line, level);
                    break;
                case "THINGS":
                    ParseThings(line, reader, level);
                    break;
            }
        }

        // Post-process: associate surfaces with their parent sectors
        var adjoinToSurface = new Dictionary<int, (JklSurface Surface, JklSector Sector)>();
        foreach (var sector in level.Sectors)
        {
            int start = sector.FirstSurfaceIndex;
            int count = sector.SurfaceCount;
            for (int i = 0; i < count; i++)
            {
                int surfaceIdx = start + i;
                if (surfaceIdx >= 0 && surfaceIdx < level.Surfaces.Count)
                {
                    var surf = level.Surfaces[surfaceIdx];
                    sector.Surfaces.Add(surf);
                    if (surf.AdjoinIndex >= 0)
                    {
                        adjoinToSurface[surf.AdjoinIndex] = (surf, sector);
                    }
                }
            }
        }

        // Post-process: link portal mirrors across sectors
        foreach (var sector in level.Sectors)
        {
            foreach (var surface in sector.Surfaces)
            {
                if (surface.AdjoinIndex >= 0 && surface.AdjoinIndex < level.Adjoins.Count)
                {
                    int mirrorIdx = level.Adjoins[surface.AdjoinIndex].Mirror;
                    if (adjoinToSurface.TryGetValue(mirrorIdx, out var target))
                    {
                        surface.MirrorSurface = target.Surface;
                        surface.MirrorSector = target.Sector;
                    }
                }
            }
        }

        // Post-process: resolve 3DO model filenames for all things
        foreach (var thing in level.Things)
        {
            thing.Model3D = level.ResolveModel3D(thing.TemplateName);
        }

        return level;
    }

    private static void ParseSounds(string line, JklLevel level)
    {
        if (line.StartsWith("World", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int colonIdx = line.IndexOf(':');
        string remainder = colonIdx >= 0 ? line[(colonIdx + 1)..].Trim() : line;
        string[] tokens = SplitTokens(remainder);
        if (tokens.Length > 0 && !string.IsNullOrEmpty(tokens[0]))
        {
            level.Sounds.Add(tokens[0]);
        }
    }

    private static void ParseMaterials(string line, JklLevel level)
    {
        int colonIdx = line.IndexOf(':');
        if (colonIdx >= 0)
        {
            string remainder = line[(colonIdx + 1)..].Trim();
            string[] tokens = SplitTokens(remainder);
            if (tokens.Length > 0 && tokens[0].EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                level.Materials.Add(tokens[0]);
            }
        }
    }

    private static void ParseGeoresource(string line, StringReader reader, JklLevel level)
    {
        string[] parts = SplitTokens(line);
        if (parts.Length < 2) return;

        if (parts[0].Equals("World", StringComparison.OrdinalIgnoreCase))
        {
            string subType = parts[1].ToUpperInvariant();
            if (subType == "COLORMAPS" && parts.Length >= 3)
            {
                int count = ParseInt(parts[2]);
                ReadColormaps(reader, level, count);
            }
            else if (subType == "VERTICES" && parts.Length >= 3)
            {
                int count = ParseInt(parts[2]);
                ReadWorldVertices(reader, level, count);
            }
            else if (subType == "TEXTURE" && parts.Length >= 4 && parts[2].Equals("vertices", StringComparison.OrdinalIgnoreCase))
            {
                int count = ParseInt(parts[3]);
                ReadTextureVertices(reader, level, count);
            }
            else if (subType == "SURFACES" && parts.Length >= 3)
            {
                int count = ParseInt(parts[2]);
                ReadWorldSurfacesAndNormals(reader, level, count);
            }
            else if (subType == "ADJOINS" && parts.Length >= 3)
            {
                int count = ParseInt(parts[2]);
                ReadWorldAdjoins(reader, level, count);
            }
        }
        else if (parts[0].Equals("Texture", StringComparison.OrdinalIgnoreCase) &&
                 parts[1].Equals("vertices", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
        {
            int count = ParseInt(parts[2]);
            ReadTextureVertices(reader, level, count);
        }
    }

    private static void ReadColormaps(StringReader reader, JklLevel level, int count)
    {
        while (level.ColorMaps.Count < count)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            int colonIdx = line.IndexOf(':');
            if (colonIdx >= 0)
            {
                string cmpName = line[(colonIdx + 1)..].Trim();
                level.ColorMaps.Add(cmpName);
            }
        }
    }

    private static void ReadWorldVertices(StringReader reader, JklLevel level, int count)
    {
        while (level.Vertices.Count < count)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] tokens = SplitTokens(line);
            if (tokens.Length >= 4)
            {
                int start = tokens[0].EndsWith(':') ? 1 : 0;
                float x = ParseFloat(tokens[start + 0]);
                float y = ParseFloat(tokens[start + 1]);
                float z = ParseFloat(tokens[start + 2]);
                level.Vertices.Add(new Vector3(x, y, z));
            }
        }
    }

    private static void ReadTextureVertices(StringReader reader, JklLevel level, int count)
    {
        while (level.TextureVertices.Count < count)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] tokens = SplitTokens(line);
            if (tokens.Length >= 3)
            {
                int start = tokens[0].EndsWith(':') ? 1 : 0;
                float u = ParseFloat(tokens[start + 0]);
                float v = ParseFloat(tokens[start + 1]);
                level.TextureVertices.Add(new Vector2(u, v));
            }
        }
    }

    private static void ReadWorldSurfacesAndNormals(StringReader reader, JklLevel level, int count)
    {
        while (level.Surfaces.Count < count)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.Contains("surfflags:", StringComparison.OrdinalIgnoreCase)) continue;

            string[] tokens = SplitTokens(line);
            if (tokens.Length >= 10)
            {
                int start = tokens[0].EndsWith(':') ? 1 : 0;
                int surfaceIdx = ParseInt(tokens[0].TrimEnd(':'));
                int mat = ParseInt(tokens[start + 0]);
                int surfflags = ParseInt(tokens[start + 1]);
                int faceflags = ParseInt(tokens[start + 2]);
                int geo = ParseInt(tokens[start + 3]);
                int light = ParseInt(tokens[start + 4]);
                int tex = ParseInt(tokens[start + 5]);
                int adjoin = ParseInt(tokens[start + 6]);
                float extraLight = ParseFloat(tokens[start + 7]);
                int nverts = ParseInt(tokens[start + 8]);

                var surface = new JklSurface
                {
                    SurfaceIndex = surfaceIdx,
                    MaterialIndex = mat,
                    SurfaceFlags = surfflags,
                    FaceFlags = faceflags,
                    GeometryMode = geo,
                    LightingMode = light,
                    TextureMode = tex,
                    AdjoinIndex = adjoin,
                    ExtraLight = extraLight,
                    VertexIndices = new int[nverts],
                    TextureIndices = new int[nverts],
                    VertexIntensities = new float[nverts]
                };

                int tokenIdx = start + 9;
                for (int v = 0; v < nverts && tokenIdx < tokens.Length; v++, tokenIdx++)
                {
                    string pair = tokens[tokenIdx];
                    if (pair.Contains(','))
                    {
                        string[] parts = pair.Split(',');
                        surface.VertexIndices[v] = ParseInt(parts[0]);
                        surface.TextureIndices[v] = ParseInt(parts[1]);
                    }
                    else
                    {
                        surface.VertexIndices[v] = ParseInt(pair);
                        surface.TextureIndices[v] = -1;
                    }
                }

                for (int v = 0; v < nverts && tokenIdx < tokens.Length; v++, tokenIdx++)
                {
                    surface.VertexIntensities[v] = ParseFloat(tokens[tokenIdx]);
                }

                level.Surfaces.Add(surface);
            }
        }

        for (int i = 0; i < count; i++)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line))
            {
                i--;
                continue;
            }

            string[] tokens = SplitTokens(line);
            if (tokens.Length >= 4)
            {
                int start = tokens[0].EndsWith(':') ? 1 : 0;
                float nx = ParseFloat(tokens[start + 0]);
                float ny = ParseFloat(tokens[start + 1]);
                float nz = ParseFloat(tokens[start + 2]);
                level.Surfaces[i].Normal = new Vector3(nx, ny, nz);
            }
        }
    }

    private static void ReadWorldAdjoins(StringReader reader, JklLevel level, int count)
    {
        while (level.Adjoins.Count < count)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.Contains("flags:", StringComparison.OrdinalIgnoreCase)) continue;

            string[] tokens = SplitTokens(line);
            if (tokens.Length >= 4)
            {
                int start = tokens[0].EndsWith(':') ? 1 : 0;
                int idx = ParseInt(tokens[0].TrimEnd(':'));
                int flags = ParseInt(tokens[start + 0]);
                int mirror = ParseInt(tokens[start + 1]);
                float dist = ParseFloat(tokens[start + 2]);

                level.Adjoins.Add(new JklAdjoin
                {
                    AdjoinIndex = idx,
                    Flags = flags,
                    Mirror = mirror,
                    Dist = dist
                });
            }
        }
    }

    private static void ParseSectors(string line, StringReader reader, JklLevel level, ref JklSector? currentSector)
    {
        string[] parts = SplitTokens(line);
        if (parts.Length == 0) return;

        string command = parts[0].ToUpperInvariant();

        if (command == "SECTOR" && parts.Length >= 2)
        {
            currentSector = new JklSector
            {
                SectorIndex = ParseInt(parts[1])
            };
            level.Sectors.Add(currentSector);
        }
        else if (command == "FLAGS" && parts.Length >= 2 && currentSector != null)
        {
            currentSector.Flags = ParseInt(parts[1]);
        }
        else if (command == "AMBIENT" && parts.Length >= 3 && currentSector != null)
        {
            currentSector.AmbientLight = ParseFloat(parts[2]);
        }
        else if (command == "EXTRA" && parts.Length >= 3 && currentSector != null)
        {
            currentSector.ExtraLight = ParseFloat(parts[2]);
        }
        else if (command == "COLORMAP" && parts.Length >= 2 && currentSector != null)
        {
            currentSector.ColorMapIndex = ParseInt(parts[1]);
        }
        else if (command == "TINT" && parts.Length >= 4 && currentSector != null)
        {
            currentSector.Tint = new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]));
        }
        else if (command == "BOUNDBOX" && parts.Length >= 7 && currentSector != null)
        {
            currentSector.BoundBoxMin = new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]));
            currentSector.BoundBoxMax = new Vector3(ParseFloat(parts[4]), ParseFloat(parts[5]), ParseFloat(parts[6]));
        }
        else if (command == "CENTER" && parts.Length >= 4 && currentSector != null)
        {
            currentSector.Center = new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]));
        }
        else if (command == "RADIUS" && parts.Length >= 2 && currentSector != null)
        {
            currentSector.Radius = ParseFloat(parts[1]);
        }
        else if (command == "VERTICES" && parts.Length >= 2 && currentSector != null)
        {
            int count = ParseInt(parts[1]);
            currentSector.VertexIndices = new int[count];
            for (int i = 0; i < count; i++)
            {
                string? vLine = reader.ReadLine();
                if (vLine == null) break;
                vLine = StripComment(vLine).Trim();
                if (string.IsNullOrEmpty(vLine))
                {
                    i--;
                    continue;
                }
                string[] vTokens = SplitTokens(vLine);
                int vStart = vTokens[0].EndsWith(':') ? 1 : 0;
                currentSector.VertexIndices[i] = ParseInt(vTokens[vStart]);
            }
        }
        else if (command == "SURFACES" && parts.Length >= 3 && currentSector != null)
        {
            currentSector.FirstSurfaceIndex = ParseInt(parts[1]);
            currentSector.SurfaceCount = ParseInt(parts[2]);
        }
    }

    private static void ParseCogScripts(string line, JklLevel level)
    {
        if (line.StartsWith("World", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int colonIdx = line.IndexOf(':');
        string remainder = colonIdx >= 0 ? line[(colonIdx + 1)..].Trim() : line;
        string[] tokens = SplitTokens(remainder);
        if (tokens.Length > 0 && !string.IsNullOrEmpty(tokens[0]))
        {
            level.CogScripts.Add(tokens[0]);
        }
    }

    private static void ParseCogs(string line, JklLevel level)
    {
        if (line.StartsWith("World", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int colonIdx = line.IndexOf(':');
        int index = level.Cogs.Count;
        string remainder = line;

        if (colonIdx >= 0)
        {
            string indexPart = line[..colonIdx].Trim();
            if (int.TryParse(indexPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedIndex))
            {
                index = parsedIndex;
            }
            remainder = line[(colonIdx + 1)..].Trim();
        }

        string[] tokens = SplitTokens(remainder);
        if (tokens.Length == 0) return;

        string scriptName = tokens[0];
        var symbolArgs = new List<string>(tokens.Length - 1);
        for (int i = 1; i < tokens.Length; i++)
        {
            symbolArgs.Add(tokens[i]);
        }

        level.Cogs.Add(new JklCogPlacement
        {
            Index = index,
            ScriptName = scriptName,
            SymbolArgs = symbolArgs
        });
    }

    private static void ParseTemplates(string line, JklLevel level)
    {
        if (line.StartsWith("World templates", StringComparison.OrdinalIgnoreCase)) return;

        string[] tokens = SplitTokens(line);
        if (tokens.Length >= 2)
        {
            string name = tokens[0];
            string basedOn = tokens[1];
            string model = string.Empty;
            var paramDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 2; i < tokens.Length; i++)
            {
                string token = tokens[i];
                int eqIdx = token.IndexOf('=');
                if (eqIdx > 0)
                {
                    string key = token[..eqIdx];
                    string val = token[(eqIdx + 1)..];
                    paramDict[key] = val;
                    if (key.Equals("model3d", StringComparison.OrdinalIgnoreCase))
                    {
                        model = val;
                    }
                }
            }

            level.Templates[name] = new JklTemplate
            {
                Name = name,
                BasedOn = basedOn,
                Model3D = model,
                Parameters = paramDict
            };
        }
    }

    private static void ParseThings(string line, StringReader reader, JklLevel level)
    {
        string[] parts = SplitTokens(line);
        if (parts.Length >= 3 && parts[0].Equals("World", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("things", StringComparison.OrdinalIgnoreCase))
        {
            int count = ParseInt(parts[2]);
            while (level.Things.Count < count)
            {
                string? tLine = reader.ReadLine();
                if (tLine == null) break;
                tLine = StripComment(tLine).Trim();
                if (string.IsNullOrEmpty(tLine)) continue;
                if (tLine.Equals("end", StringComparison.OrdinalIgnoreCase)) break;
                if (tLine.StartsWith("section:", StringComparison.OrdinalIgnoreCase)) break;
                if (tLine.Contains("template:", StringComparison.OrdinalIgnoreCase)) continue;

                string[] tokens = SplitTokens(tLine);
                if (tokens.Length >= 10)
                {
                    int start = tokens[0].EndsWith(':') ? 1 : 0;
                    int thingIdx = ParseInt(tokens[0].TrimEnd(':'));
                    string template = tokens[start + 0];
                    string name = tokens[start + 1];
                    float x = ParseFloat(tokens[start + 2]);
                    float y = ParseFloat(tokens[start + 3]);
                    float z = ParseFloat(tokens[start + 4]);
                    float pitch = ParseFloat(tokens[start + 5]);
                    float yaw = ParseFloat(tokens[start + 6]);
                    float roll = ParseFloat(tokens[start + 7]);
                    int sector = ParseInt(tokens[start + 8]);

                    string extra = string.Empty;
                    uint thingFlags = 0;
                    int numFrames = 0;
                    List<string> frames = [];
                    float? light = null;
                    float? size = null;
                    float? moveSize = null;

                    if (tokens.Length > start + 9)
                    {
                        extra = string.Join(' ', tokens, start + 9, tokens.Length - (start + 9));
                        ParseThingExtraArgs(tokens, start + 9, ref thingFlags, ref numFrames, frames, ref light, ref size, ref moveSize);
                    }

                    level.Things.Add(new JklThing
                    {
                        ThingIndex = thingIdx,
                        TemplateName = template,
                        Name = name,
                        Position = new Vector3(x, y, z),
                        PitchYawRoll = new Vector3(pitch, yaw, roll),
                        SectorIndex = sector,
                        ExtraArguments = extra,
                        ThingFlags = thingFlags,
                        NumFrames = numFrames,
                        Frames = frames,
                        Light = light,
                        Size = size,
                        MoveSize = moveSize
                    });
                }
            }
        }
    }

    private static void ParseThingExtraArgs(
        string[] tokens,
        int startIndex,
        ref uint thingFlags,
        ref int numFrames,
        List<string> frames,
        ref float? light,
        ref float? size,
        ref float? moveSize)
    {
        for (int i = startIndex; i < tokens.Length; i++)
        {
            string token = tokens[i];
            int eqIdx = token.IndexOf('=');
            if (eqIdx <= 0) continue;

            string key = token[..eqIdx];
            string val = token[(eqIdx + 1)..];

            if (key.Equals("thingflags", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseUInt(val, out uint flags)) thingFlags = flags;
            }
            else if (key.Equals("numframes", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nf)) numFrames = nf;
            }
            else if (key.Equals("frame", StringComparison.OrdinalIgnoreCase))
            {
                frames.Add(val);
            }
            else if (key.Equals("light", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float l)) light = l;
            }
            else if (key.Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float sz)) size = sz;
            }
            else if (key.Equals("movesize", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float msz)) moveSize = msz;
            }
        }
    }

    private static string StripComment(string line)
    {
        int hashIdx = line.IndexOf('#');
        return hashIdx >= 0 ? line[..hashIdx] : line;
    }

    private static string[] SplitTokens(string line)
    {
        return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private static float ParseFloat(string s) =>
        float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int ParseInt(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static bool TryParseUInt(string s, out uint value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
