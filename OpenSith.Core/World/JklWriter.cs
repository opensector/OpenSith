using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OpenSith.Core.World;

/// <summary>
/// Serializer for Jedi Knight level (.JKL) files in Sith Engine format.
/// </summary>
public static class JklWriter
{
    /// <summary>
    /// Serializes a <see cref="JklLevel"/> to a text string in .JKL format.
    /// </summary>
    /// <param name="level">The level data to serialize.</param>
    /// <returns>The serialized .JKL string.</returns>
    public static string Write(JklLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        Write(level, stringWriter);
        return stringWriter.ToString();
    }

    /// <summary>
    /// Serializes a <see cref="JklLevel"/> to the specified <see cref="TextWriter"/>.
    /// </summary>
    /// <param name="level">The level data to serialize.</param>
    /// <param name="writer">The output text writer.</param>
    public static void Write(JklLevel level, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(writer);

        WriteJkSection(writer);
        WriteSoundsSection(level, writer);
        WriteMaterialsSection(level, writer);
        WriteGeoresourceSection(level, writer);
        WriteSectorsSection(level, writer);
        WriteCogScriptsSection(level, writer);
        WriteCogsSection(level, writer);
        WriteTemplatesSection(level, writer);
        WriteThingsSection(level, writer);
    }

    private static void WriteJkSection(TextWriter writer)
    {
        writer.WriteLine("######    JK  information ######");
        writer.WriteLine("SECTION: JK");
        writer.WriteLine("# Jedi Knight specific data");
        writer.WriteLine("################################");
        writer.WriteLine();
    }

    private static void WriteSoundsSection(JklLevel level, TextWriter writer)
    {
        writer.WriteLine("SECTION: SOUNDS");
        writer.WriteLine();
        writer.WriteLine($"World sounds {level.Sounds.Count}");
        writer.WriteLine();
        for (int i = 0; i < level.Sounds.Count; i++)
        {
            writer.WriteLine($"{i}:\t{level.Sounds[i]}");
        }
        writer.WriteLine("end");
        writer.WriteLine();
    }

    private static void WriteMaterialsSection(JklLevel level, TextWriter writer)
    {
        writer.WriteLine("SECTION: MATERIALS");
        writer.WriteLine();
        writer.WriteLine($"World materials {level.Materials.Count}");
        writer.WriteLine();
        writer.WriteLine("#num:\tmat:\t\txTile:\t\tyTile:");
        for (int i = 0; i < level.Materials.Count; i++)
        {
            writer.WriteLine($"{i}:\t{level.Materials[i]}\t1.000000\t1.000000");
        }
        writer.WriteLine("end");
        writer.WriteLine();
    }

    private static void WriteGeoresourceSection(JklLevel level, TextWriter writer)
    {
        writer.WriteLine("SECTION: GEORESOURCE");
        writer.WriteLine();

        if (level.ColorMaps.Count > 0)
        {
            writer.WriteLine("#------ Palette Subsection -----");
            writer.WriteLine($"World Colormaps\t{level.ColorMaps.Count}");
            for (int i = 0; i < level.ColorMaps.Count; i++)
            {
                writer.WriteLine($"{i}:\t{level.ColorMaps[i]}");
            }
            writer.WriteLine();
        }

        writer.WriteLine("#----- Vertices Subsection -----");
        writer.WriteLine($"World vertices {level.Vertices.Count}");
        writer.WriteLine("#num:\tvertex:");
        for (int i = 0; i < level.Vertices.Count; i++)
        {
            var v = level.Vertices[i];
            writer.WriteLine($"{i}:\t{FormatFloat(v.X)}\t{FormatFloat(v.Y)}\t{FormatFloat(v.Z)}");
        }
        writer.WriteLine();

        writer.WriteLine("#----- Texture Vertices Subsection -----");
        writer.WriteLine($"World texture vertices {level.TextureVertices.Count}");
        writer.WriteLine("#num:\tu:\t\tv:");
        for (int i = 0; i < level.TextureVertices.Count; i++)
        {
            var uv = level.TextureVertices[i];
            writer.WriteLine($"{i}:\t{FormatFloat(uv.X)}\t{FormatFloat(uv.Y)}");
        }
        writer.WriteLine();

        writer.WriteLine("#----- Surfaces Subsection -----");
        writer.WriteLine($"World surfaces {level.Surfaces.Count}");
        writer.WriteLine("#num:\tmat:\tsurfflags:\tfaceflags:\tgeo:\tlight:\ttex:\tadjoin:\textralight:\tnverts:\tvertices:\tintensities:");
        for (int i = 0; i < level.Surfaces.Count; i++)
        {
            var s = level.Surfaces[i];
            int nverts = s.VertexIndices?.Length ?? 0;
            writer.Write($"{s.SurfaceIndex}:\t{s.MaterialIndex}\t0x{s.SurfaceFlags:X}\t0x{s.FaceFlags:X}\t{s.GeometryMode}\t{s.LightingMode}\t{s.TextureMode}\t{s.AdjoinIndex}\t{FormatFloat(s.ExtraLight)}\t{nverts}");

            for (int v = 0; v < nverts; v++)
            {
                int vIdx = s.VertexIndices![v];
                int tIdx = (s.TextureIndices != null && v < s.TextureIndices.Length) ? s.TextureIndices[v] : -1;
                writer.Write($"\t{vIdx},{tIdx}");
            }

            for (int v = 0; v < nverts; v++)
            {
                float intensity = (s.VertexIntensities != null && v < s.VertexIntensities.Length) ? s.VertexIntensities[v] : 0f;
                writer.Write($"\t{FormatFloat(intensity)}");
            }

            writer.WriteLine();
        }
        writer.WriteLine();

        writer.WriteLine("#--- Surface normals ---");
        for (int i = 0; i < level.Surfaces.Count; i++)
        {
            var n = level.Surfaces[i].Normal;
            writer.WriteLine($"{i}:\t{FormatFloat(n.X)}\t{FormatFloat(n.Y)}\t{FormatFloat(n.Z)}");
        }
        writer.WriteLine();

        writer.WriteLine("#----- Adjoins Subsection -----");
        writer.WriteLine($"World adjoins {level.Adjoins.Count}");
        writer.WriteLine("#num:\tflags:\t\tmirror:\tdist:");
        for (int i = 0; i < level.Adjoins.Count; i++)
        {
            var a = level.Adjoins[i];
            writer.WriteLine($"{a.AdjoinIndex}:\t0x{a.Flags:X}\t{a.Mirror}\t{FormatFloat(a.Dist)}");
        }
        writer.WriteLine();
    }

    private static void WriteSectorsSection(JklLevel level, TextWriter writer)
    {
        writer.WriteLine("Section: SECTORS");
        writer.WriteLine();
        writer.WriteLine($"World sectors {level.Sectors.Count}");
        writer.WriteLine();

        for (int sIdx = 0; sIdx < level.Sectors.Count; sIdx++)
        {
            var sec = level.Sectors[sIdx];
            int secNum = sec.SectorIndex >= 0 ? sec.SectorIndex : sIdx;
            writer.WriteLine($"SECTOR\t{secNum}");
            writer.WriteLine($"FLAGS\t0x{sec.Flags:X}");
            writer.WriteLine($"AMBIENT LIGHT\t{FormatFloat(sec.AmbientLight)}");
            writer.WriteLine($"EXTRA LIGHT\t{FormatFloat(sec.ExtraLight)}");
            writer.WriteLine($"COLORMAP\t{sec.ColorMapIndex}");
            writer.WriteLine($"TINT\t{FormatFloat(sec.Tint.X)}\t{FormatFloat(sec.Tint.Y)}\t{FormatFloat(sec.Tint.Z)}");
            writer.WriteLine($"BOUNDBOX\t{FormatFloat(sec.BoundBoxMin.X)}\t{FormatFloat(sec.BoundBoxMin.Y)}\t{FormatFloat(sec.BoundBoxMin.Z)}\t{FormatFloat(sec.BoundBoxMax.X)}\t{FormatFloat(sec.BoundBoxMax.Y)}\t{FormatFloat(sec.BoundBoxMax.Z)}");
            writer.WriteLine($"CENTER\t{FormatFloat(sec.Center.X)}\t{FormatFloat(sec.Center.Y)}\t{FormatFloat(sec.Center.Z)}");
            writer.WriteLine($"RADIUS\t{FormatFloat(sec.Radius)}");

            var vIndices = sec.VertexIndices ?? [];
            writer.WriteLine($"VERTICES\t{vIndices.Length}");
            for (int v = 0; v < vIndices.Length; v++)
            {
                writer.WriteLine($"{v}:\t{vIndices[v]}");
            }

            int firstSurf = sec.FirstSurfaceIndex;
            int surfCount = sec.SurfaceCount;
            if (surfCount == 0 && sec.Surfaces.Count > 0)
            {
                firstSurf = sec.Surfaces[0].SurfaceIndex;
                surfCount = sec.Surfaces.Count;
            }
            writer.WriteLine($"SURFACES\t{firstSurf}\t{surfCount}");
            writer.WriteLine();
        }
    }

    private static void WriteCogScriptsSection(JklLevel level, TextWriter writer)
    {
        writer.WriteLine("Section: cogscripts");
        writer.WriteLine();
        writer.WriteLine($"World scripts {level.CogScripts.Count}");
        writer.WriteLine();
        for (int i = 0; i < level.CogScripts.Count; i++)
        {
            writer.WriteLine($"{i}:\t{level.CogScripts[i]}");
        }
        writer.WriteLine("end");
        writer.WriteLine();
    }

    private static void WriteCogsSection(JklLevel level, TextWriter writer)
    {
        writer.WriteLine("Section: cogs");
        writer.WriteLine();
        writer.WriteLine($"World cogs {level.Cogs.Count}");
        writer.WriteLine();
        writer.WriteLine("#Num Script Symbol values");
        for (int i = 0; i < level.Cogs.Count; i++)
        {
            var cog = level.Cogs[i];
            int idx = cog.Index >= 0 ? cog.Index : i;
            string args = cog.SymbolArgs.Count > 0 ? "\t" + string.Join(' ', cog.SymbolArgs) : "";
            writer.WriteLine($"{idx}:\t{cog.ScriptName}{args}");
        }
        writer.WriteLine("end");
        writer.WriteLine();
    }

    private static void WriteTemplatesSection(JklLevel level, TextWriter writer)
    {
        writer.WriteLine("Section: TEMPLATES");
        writer.WriteLine();
        writer.WriteLine($"World templates {level.Templates.Count}");
        writer.WriteLine();
        writer.WriteLine("#Name:           Based On:        Params:");
        foreach (var tpl in level.Templates.Values)
        {
            var parts = new List<string>();
            foreach (var (k, v) in tpl.Parameters)
            {
                parts.Add($"{k}={v}");
            }
            if (!string.IsNullOrEmpty(tpl.Model3D) && !tpl.Parameters.ContainsKey("model3d"))
            {
                parts.Add($"model3d={tpl.Model3D}");
            }

            string basedOn = string.IsNullOrWhiteSpace(tpl.BasedOn) ? "none" : tpl.BasedOn;
            string paramStr = parts.Count > 0 ? "\t" + string.Join(' ', parts) : "";
            writer.WriteLine($"{tpl.Name}\t{basedOn}{paramStr}");
        }
        writer.WriteLine("end");
        writer.WriteLine();
    }

    private static void WriteThingsSection(JklLevel level, TextWriter writer)
    {
        writer.WriteLine("Section: Things");
        writer.WriteLine();
        writer.WriteLine($"World things {level.Things.Count}");
        writer.WriteLine();
        writer.WriteLine("#num template:       name:         \tX:\t\tY:\t\tZ:\t\tPitch:\t\tYaw:\t\tRoll:\t\tSector:");
        for (int i = 0; i < level.Things.Count; i++)
        {
            var t = level.Things[i];
            int thingIdx = t.ThingIndex >= 0 ? t.ThingIndex : i;
            string tplName = string.IsNullOrEmpty(t.TemplateName) ? "none" : t.TemplateName;
            string name = string.IsNullOrEmpty(t.Name) ? tplName : t.Name;
            string extra = string.IsNullOrEmpty(t.ExtraArguments) ? "" : $"\t{t.ExtraArguments}";
            if (t.ThingFlags != 0 && !t.ExtraArguments.Contains("thingflags=", StringComparison.OrdinalIgnoreCase))
            {
                extra += string.IsNullOrEmpty(extra) ? $"\tthingflags=0x{t.ThingFlags:x}" : $" thingflags=0x{t.ThingFlags:x}";
            }
            writer.WriteLine($"{thingIdx}:\t{tplName}\t{name}\t{FormatFloat(t.Position.X)}\t{FormatFloat(t.Position.Y)}\t{FormatFloat(t.Position.Z)}\t{FormatFloat(t.PitchYawRoll.X)}\t{FormatFloat(t.PitchYawRoll.Y)}\t{FormatFloat(t.PitchYawRoll.Z)}\t{t.SectorIndex}{extra}");
        }
        writer.WriteLine("end");
        writer.WriteLine();
    }

    private static string FormatFloat(float value)
    {
        if (value == 0f) return "0.000000";
        string str = value.ToString("F6", CultureInfo.InvariantCulture);
        return str == "-0.000000" ? "0.000000" : str;
    }
}
