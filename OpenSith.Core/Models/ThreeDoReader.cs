using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace OpenSith.Core.Models;

/// <summary>
/// Parser for LucasArts Sith Engine 3DO 3D mesh files.
/// </summary>
public sealed class ThreeDoReader
{
    public static ThreeDoModel Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var model = new ThreeDoModel();

        using var reader = new StringReader(text);
        string? line;
        string currentSection = string.Empty;
        ThreeDoMesh? currentMesh = null;

        while ((line = reader.ReadLine()) != null)
        {
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (line.StartsWith("SECTION:", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = line["SECTION:".Length..].Trim().ToUpperInvariant();
                continue;
            }

            switch (currentSection)
            {
                case "HEADER":
                    ParseHeader(line, model);
                    break;
                case "MODELRESOURCE":
                    ParseModelResource(line, model);
                    break;
                case "GEOMETRYDEF":
                    ParseGeometryDef(line, reader, model, ref currentMesh);
                    break;
                case "HIERARCHYDEF":
                    ParseHierarchyDef(line, model);
                    break;
            }
        }

        return model;
    }

    private static void ParseHeader(string line, ThreeDoModel model)
    {
        if (line.StartsWith("3DO", StringComparison.OrdinalIgnoreCase))
        {
            model.Version = line;
        }
    }

    private static void ParseModelResource(string line, ThreeDoModel model)
    {
        // Format: "0: 00crtesd.mat"
        int colonIdx = line.IndexOf(':');
        if (colonIdx >= 0)
        {
            string matName = line[(colonIdx + 1)..].Trim();
            if (!string.IsNullOrEmpty(matName))
            {
                model.Materials.Add(matName);
            }
        }
    }

    private static void ParseGeometryDef(string line, StringReader reader, ThreeDoModel model, ref ThreeDoMesh? currentMesh)
    {
        string[] parts = SplitTokens(line);
        if (parts.Length == 0) return;

        string command = parts[0].ToUpperInvariant();

        if (command == "RADIUS" && parts.Length >= 2)
        {
            float r = ParseFloat(parts[1]);
            if (currentMesh != null)
                currentMesh.Radius = r;
            else
                model.Radius = r;
        }
        else if (command == "INSERT" && parts.Length >= 5 && parts[1].Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
        {
            model.InsertOffset = new Vector3(ParseFloat(parts[2]), ParseFloat(parts[3]), ParseFloat(parts[4]));
        }
        else if (command == "MESH" && parts.Length >= 2)
        {
            currentMesh = new ThreeDoMesh
            {
                MeshIndex = int.Parse(parts[1], CultureInfo.InvariantCulture)
            };
            model.Meshes.Add(currentMesh);
        }
        else if (command == "NAME" && parts.Length >= 2 && currentMesh != null)
        {
            currentMesh.Name = parts[1];
        }
        else if (command == "GEOMETRYMODE" && parts.Length >= 2 && currentMesh != null)
        {
            currentMesh.GeometryMode = ParseInt(parts[1]);
        }
        else if (command == "LIGHTINGMODE" && parts.Length >= 2 && currentMesh != null)
        {
            currentMesh.LightingMode = ParseInt(parts[1]);
        }
        else if (command == "TEXTUREMODE" && parts.Length >= 2 && currentMesh != null)
        {
            currentMesh.TextureMode = ParseInt(parts[1]);
        }
        else if (command == "VERTICES" && parts.Length >= 2 && currentMesh != null)
        {
            int count = ParseInt(parts[1]);
            ReadVertices(reader, currentMesh, count);
        }
        else if (command == "TEXTURE" && parts.Length >= 3 && parts[1].Equals("VERTICES", StringComparison.OrdinalIgnoreCase) && currentMesh != null)
        {
            int count = ParseInt(parts[2]);
            ReadTextureVertices(reader, currentMesh, count);
        }
        else if (command == "FACES" && parts.Length >= 2 && currentMesh != null)
        {
            int count = ParseInt(parts[1]);
            ReadFaces(reader, currentMesh, count);
        }
        else if (command == "FACE" && parts.Length >= 2 && parts[1].Equals("NORMALS", StringComparison.OrdinalIgnoreCase) && currentMesh != null)
        {
            ReadFaceNormals(reader, currentMesh);
        }
    }

    private static void ReadVertices(StringReader reader, ThreeDoMesh mesh, int count)
    {
        while (mesh.Vertices.Count < count)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] tokens = SplitTokens(line);
            // Format: "num: x y z i"
            // tokens[0] is "0:", tokens[1]=x, tokens[2]=y, tokens[3]=z, tokens[4]=i
            if (tokens.Length >= 4)
            {
                int start = tokens[0].EndsWith(':') ? 1 : 0;
                float x = ParseFloat(tokens[start + 0]);
                float y = ParseFloat(tokens[start + 1]);
                float z = ParseFloat(tokens[start + 2]);
                float intensity = tokens.Length > start + 3 ? ParseFloat(tokens[start + 3]) : 0.0f;

                mesh.Vertices.Add(new Vector3(x, y, z));
                mesh.VertexIntensities.Add(intensity);
            }
        }
    }

    private static void ReadTextureVertices(StringReader reader, ThreeDoMesh mesh, int count)
    {
        while (mesh.TextureVertices.Count < count)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] tokens = SplitTokens(line);
            // Format: "num: u v"
            if (tokens.Length >= 3)
            {
                int start = tokens[0].EndsWith(':') ? 1 : 0;
                float u = ParseFloat(tokens[start + 0]);
                float v = ParseFloat(tokens[start + 1]);
                mesh.TextureVertices.Add(new Vector2(u, v));
            }
        }
    }

    private static void ReadFaces(StringReader reader, ThreeDoMesh mesh, int count)
    {
        while (mesh.Faces.Count < count)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            line = StripComment(line).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Header line "num: material: type: geo: light: tex: extralight: verts:"
            if (line.Contains("material:", StringComparison.OrdinalIgnoreCase)) continue;

            string[] tokens = SplitTokens(line);
            // tokens: 0="0:", 1=material, 2=type, 3=geo, 4=light, 5=tex, 6=extralight, 7=numVerts, then pairs of "vIdx, uvIdx"
            if (tokens.Length >= 8)
            {
                int start = tokens[0].EndsWith(':') ? 1 : 0;
                int material = ParseInt(tokens[start + 0]);
                int type = ParseInt(tokens[start + 1]);
                int geo = ParseInt(tokens[start + 2]);
                int light = ParseInt(tokens[start + 3]);
                int tex = ParseInt(tokens[start + 4]);
                float extraLight = ParseFloat(tokens[start + 5]);
                int numVerts = ParseInt(tokens[start + 6]);

                var face = new ThreeDoFace
                {
                    MaterialIndex = material,
                    FaceType = type,
                    GeometryMode = geo,
                    LightingMode = light,
                    TextureMode = tex,
                    ExtraLight = extraLight,
                    VertexIndices = new int[numVerts],
                    TextureIndices = new int[numVerts]
                };

                // Read vertex/uv pairs: e.g. "0, 0", "1, 1"
                int tokenIdx = start + 7;
                int vertIdx = 0;
                while (tokenIdx < tokens.Length && vertIdx < numVerts)
                {
                    string pair = tokens[tokenIdx];
                    if (pair.EndsWith(','))
                    {
                        // Split across tokens: "0," "0"
                        int v = ParseInt(pair.TrimEnd(','));
                        int uv = tokenIdx + 1 < tokens.Length ? ParseInt(tokens[tokenIdx + 1]) : 0;
                        face.VertexIndices[vertIdx] = v;
                        face.TextureIndices[vertIdx] = uv;
                        tokenIdx += 2;
                    }
                    else if (pair.Contains(','))
                    {
                        string[] pairParts = pair.Split(',');
                        face.VertexIndices[vertIdx] = ParseInt(pairParts[0]);
                        face.TextureIndices[vertIdx] = ParseInt(pairParts[1]);
                        tokenIdx++;
                    }
                    else
                    {
                        face.VertexIndices[vertIdx] = ParseInt(pair);
                        face.TextureIndices[vertIdx] = 0;
                        tokenIdx++;
                    }
                    vertIdx++;
                }

                mesh.Faces.Add(face);
            }
        }
    }

    private static void ReadFaceNormals(StringReader reader, ThreeDoMesh mesh)
    {
        for (int i = 0; i < mesh.Faces.Count; i++)
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
                mesh.Faces[i].Normal = new Vector3(nx, ny, nz);
            }
        }
    }

    private static void ParseHierarchyDef(string line, ThreeDoModel model)
    {
        // Format: "num: flags: type: mesh: parent: child: sibling: numChildren: x: y: z: pitch: yaw: roll: pivotx: pivoty: pivotz: hnodename:"
        if (line.Contains("hnodename:", StringComparison.OrdinalIgnoreCase)) return;
        if (line.StartsWith("HIERARCHY", StringComparison.OrdinalIgnoreCase)) return;

        string[] tokens = SplitTokens(line);
        if (tokens.Length >= 18)
        {
            int start = tokens[0].EndsWith(':') ? 1 : 0;
            var node = new ThreeDoHierarchyNode
            {
                NodeIndex = ParseInt(tokens[0].TrimEnd(':')),
                Flags = ParseInt(tokens[start + 0]),
                NodeType = ParseInt(tokens[start + 1]),
                MeshIndex = ParseInt(tokens[start + 2]),
                ParentIndex = ParseInt(tokens[start + 3]),
                FirstChildIndex = ParseInt(tokens[start + 4]),
                NextSiblingIndex = ParseInt(tokens[start + 5]),
                Position = new Vector3(ParseFloat(tokens[start + 7]), ParseFloat(tokens[start + 8]), ParseFloat(tokens[start + 9])),
                PitchYawRoll = new Vector3(ParseFloat(tokens[start + 10]), ParseFloat(tokens[start + 11]), ParseFloat(tokens[start + 12])),
                Pivot = new Vector3(ParseFloat(tokens[start + 13]), ParseFloat(tokens[start + 14]), ParseFloat(tokens[start + 15])),
                Name = tokens.Length > start + 16 ? tokens[start + 16] : string.Empty
            };
            model.Nodes.Add(node);
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
}
