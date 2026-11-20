using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Animation;
using OpenSith.Core.Materials;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class InspectNpcHeadsTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public InspectNpcHeadsTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ListAllNpcThingsInNarShaddaa()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;
        using var jk1 = GobArchive.FromFile(jk1Path);
        var entry = jk1.Entries.FirstOrDefault(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var level = JklReader.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(entry.Path)));

        _output.WriteLine("=== ALL ACTORS / NPCS IN 01NARSHADDA ===");
        foreach (var t in level.Things)
        {
            string m = t.Model3D;
            if (string.IsNullOrEmpty(m) && level.Templates.TryGetValue(t.TemplateName, out var tmpl))
                m = tmpl.Model3D;

            if (t.TemplateName.Contains("reeyees", StringComparison.OrdinalIgnoreCase) ||
                t.TemplateName.Contains("man", StringComparison.OrdinalIgnoreCase) ||
                t.TemplateName.Contains("woman", StringComparison.OrdinalIgnoreCase) ||
                t.TemplateName.Contains("bar", StringComparison.OrdinalIgnoreCase) ||
                t.TemplateName.Contains("tusken", StringComparison.OrdinalIgnoreCase))
            {
                _output.WriteLine($"Thing {t.ThingIndex,3}: Name='{t.TemplateName,-15}', Model='{m,-10}', Sec={t.SectorIndex,3}, Pos={t.Position}, PYR={t.PitchYawRoll}");
            }
        }
    }

    [Fact]
    public void InspectSpecificCantinaNpcs()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(jk1Path) || !File.Exists(res2Path)) return;
        using var jk1 = GobArchive.FromFile(jk1Path);
        using var res2 = GobArchive.FromFile(res2Path);

        var levelEntry = jk1.Entries.FirstOrDefault(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(levelEntry.Path);
        var level = JklReader.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(levelEntry.Path)));

        int[] cantinaThings = [207, 216, 95]; // Ree-Yees, Civilian, Bartender (if 95 is bartender)

        foreach (int tIdx in cantinaThings)
        {
            if (tIdx >= level.Things.Count) continue;
            var thing = level.Things[tIdx];

            string modelName = thing.Model3D;
            if (string.IsNullOrEmpty(modelName) && level.Templates.TryGetValue(thing.TemplateName, out var tmpl))
                modelName = tmpl.Model3D;

            _output.WriteLine($"\n==================== THING {thing.ThingIndex}: {thing.TemplateName} ('{modelName}') ====================");
            _output.WriteLine($"Thing World Pos: {thing.Position}, PYR: {thing.PitchYawRoll}");

            var modelEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(modelName, StringComparison.OrdinalIgnoreCase));
            if (modelEntry.Path == null) continue;
            var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(modelEntry.Path)));

            // Find puppet
            string current = thing.TemplateName;
            string? puppetName = null;
            while (!string.IsNullOrEmpty(current) && !current.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (level.Templates.TryGetValue(current, out var t))
                {
                    if (t.Parameters.TryGetValue("puppet", out var pup) && !string.IsNullOrEmpty(pup))
                    {
                        puppetName = pup;
                        break;
                    }
                    current = t.BasedOn;
                }
                else break;
            }

            KeyframeTrack? keyframe = null;
            if (!string.IsNullOrEmpty(puppetName))
            {
                var pupEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(puppetName, StringComparison.OrdinalIgnoreCase));
                if (pupEntry.Path != null)
                {
                    var pup = PuppetReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(pupEntry.Path)), puppetName);
                    string? idleKey = pup.GetKeyframe(0, "stand");
                    if (!string.IsNullOrEmpty(idleKey))
                    {
                        var keyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(idleKey, StringComparison.OrdinalIgnoreCase));
                        if (keyEntry.Path != null)
                        {
                            keyframe = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(keyEntry.Path)), idleKey);
                        }
                    }
                }
            }

            _output.WriteLine($"Puppet: '{puppetName}', Keyframe: '{keyframe?.Name}'");

            // Compute node transforms
            var nodeTransforms = ThreeDoHierarchy.ComputeTransforms(model, keyframe, 0f);
            var modelMatrix = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position).ToMatrix4x4();

            for (int i = 0; i < model.Nodes.Count; i++)
            {
                var n = model.Nodes[i];
                if (n.MeshIndex < 0 || n.MeshIndex >= model.Meshes.Count) continue;

                var mesh = model.Meshes[n.MeshIndex];
                Vector3 minModel = new(float.MaxValue), maxModel = new(float.MinValue);
                Vector3 minWorld = new(float.MaxValue), maxWorld = new(float.MinValue);

                foreach (var v in mesh.Vertices)
                {
                    var modelV = Vector3.Transform(v, nodeTransforms[i]);
                    var worldV = Vector3.Transform(modelV, modelMatrix);

                    minModel = Vector3.Min(minModel, modelV);
                    maxModel = Vector3.Max(maxModel, modelV);
                    minWorld = Vector3.Min(minWorld, worldV);
                    maxWorld = Vector3.Max(maxWorld, worldV);
                }

                _output.WriteLine($"  Node {i,2} ('{n.Name,-10}'): ModelZ=[{minModel.Z,7:F4}, {maxModel.Z,7:F4}], WorldZ=[{minWorld.Z,7:F4}, {maxWorld.Z,7:F4}]");
            }
        }
    }

    [Fact]
    public void InspectStatic3DoPropsUVs()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);

        foreach (string modelName in new[] { "00tabl.3do", "crt4-3.3do", "01colm.3do" })
        {
            var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(modelName, StringComparison.OrdinalIgnoreCase));
            if (entry.Path == null) continue;
            var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));
            _output.WriteLine($"=== {modelName}: {model.Meshes.Count} meshes, Materials: {string.Join(", ", model.Materials)} ===");
            var mesh = model.Meshes[0];
            for (int f = 0; f < Math.Min(3, mesh.Faces.Count); f++)
            {
                var face = mesh.Faces[f];
                _output.WriteLine($"  Face {f}: Normal={face.Normal}");
                for (int v = 0; v < face.VertexIndices.Length; v++)
                {
                    int vi = face.VertexIndices[v];
                    int ti = (v < face.TextureIndices.Length) ? face.TextureIndices[v] : -1;
                    Vector2 uv = (ti >= 0 && ti < mesh.TextureVertices.Count) ? mesh.TextureVertices[ti] : Vector2.Zero;
                    _output.WriteLine($"     v[{v}] vi={vi} pos={mesh.Vertices[vi]} uv={uv}");
                }
            }
        }
    }

    [Fact]
    public void InspectM2HeadMeshAllFaces()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("m2.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));

        var headMesh = model.Meshes[0];
        _output.WriteLine($"=== m2.3do Head Mesh 0: {headMesh.Faces.Count} faces ===");
        for (int f = 0; f < headMesh.Faces.Count; f++)
        {
            var face = headMesh.Faces[f];
            string mat = (face.MaterialIndex >= 0 && face.MaterialIndex < model.Materials.Count) ? model.Materials[face.MaterialIndex] : "";
            Vector3 center = Vector3.Zero;
            foreach (int vi in face.VertexIndices) center += headMesh.Vertices[vi];
            center /= face.VertexIndices.Length;

            _output.WriteLine($"Face {f,2}: Mat='{mat,-12}', Normal={face.Normal}, Center={center}");
        }
    }

    [Fact]
    public void InspectAllMeshBoundsM2()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("m2.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));

        var transforms = ThreeDoHierarchy.ComputeTransforms(model, null, 0f);
        _output.WriteLine($"=== m2.3do ACCUMULATED NODE TRANSFORMS (REST POSE) ===");
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var n = model.Nodes[i];
            var m = transforms[i];
            var up = Vector3.TransformNormal(Vector3.UnitZ, m);
            var look = Vector3.TransformNormal(new Vector3(0, 1, 0), m);
            _output.WriteLine($"Node {i,2} ('{n.Name,-10}'): Trans={m.Translation}, Up={up}, Look={look}");
        }
    }

    [Fact]
    public void InspectJklTextureVertices()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;
        using var jk1 = GobArchive.FromFile(jk1Path);
        var entry = jk1.Entries.FirstOrDefault(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var level = JklReader.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(entry.Path)));

        _output.WriteLine($"01narshadda: {level.TextureVertices.Count} tex verts, {level.Surfaces.Count} surfaces");
        // Check a wall in Sector 339
        var sec339 = level.Sectors[339];
        _output.WriteLine($"=== SECTOR 339 SURFACES ===");
        for (int s = 0; s < sec339.SurfaceCount; s++)
        {
            var surf = level.Surfaces[sec339.FirstSurfaceIndex + s];
            if (surf.MaterialIndex >= 0 && MathF.Abs(surf.Normal.Z) < 0.2f) // textured vertical wall
            {
                string matName = surf.MaterialIndex < level.Materials.Count ? level.Materials[surf.MaterialIndex] : "";
                _output.WriteLine($"Wall Surf {sec339.FirstSurfaceIndex + s}: Mat='{matName}', Normal={surf.Normal}");
                for (int v = 0; v < surf.VertexIndices.Length; v++)
                {
                    int vi = surf.VertexIndices[v];
                    int ti = surf.TextureIndices[v];
                    _output.WriteLine($"   v[{v}] pos={level.Vertices[vi]} uv={level.TextureVertices[ti]}");
                }
                break;
            }
        }
    }

    [Fact]
    public void InspectKyleHeadMeshAndTexture()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ky.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));

        var headNode = model.Nodes.FirstOrDefault(n => n.Name.Contains("head", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(headNode);
        _output.WriteLine($"Kyle Head Node: Name='{headNode.Name}', NodeIdx={headNode.NodeIndex}, Parent={headNode.ParentIndex}, MeshIdx={headNode.MeshIndex}, Pos={headNode.Position}, Pivot={headNode.Pivot}, PYR={headNode.PitchYawRoll}");

        if (headNode.MeshIndex >= 0 && headNode.MeshIndex < model.Meshes.Count)
        {
            var mesh = model.Meshes[headNode.MeshIndex];
            _output.WriteLine($"Kyle Head Mesh: {mesh.Vertices.Count} verts, {mesh.Faces.Count} faces");
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var face = mesh.Faces[f];
                string mat = (face.MaterialIndex >= 0 && face.MaterialIndex < model.Materials.Count) ? model.Materials[face.MaterialIndex] : "";
                _output.WriteLine($"  Face {f,2}: Mat='{mat}', Normal={face.Normal}");
                for (int v = 0; v < face.VertexIndices.Length; v++)
                {
                    int vi = face.VertexIndices[v];
                    int ti = (v < face.TextureIndices.Length) ? face.TextureIndices[v] : -1;
                    Vector2 uv = (ti >= 0 && ti < mesh.TextureVertices.Count) ? mesh.TextureVertices[ti] : Vector2.Zero;
                    _output.WriteLine($"     v[{v}] vi={vi} rawPos={mesh.Vertices[vi]} rawUv={uv}");
                }
            }
        }
    }

    [Fact]
    public void InspectGranAppendageMaterialsAndUVs()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));

        int[] appendageNodes = [2, 8, 14, 16, 18, 19];
        foreach (int nIdx in appendageNodes)
        {
            var node = model.Nodes[nIdx];
            if (node.MeshIndex < 0 || node.MeshIndex >= model.Meshes.Count) continue;
            var mesh = model.Meshes[node.MeshIndex];
            _output.WriteLine($"Node {nIdx,2} ('{node.Name}'): Mesh {node.MeshIndex}, {mesh.Vertices.Count} verts, {mesh.Faces.Count} faces");
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var face = mesh.Faces[f];
                string mat = (face.MaterialIndex >= 0 && face.MaterialIndex < model.Materials.Count) ? model.Materials[face.MaterialIndex] : "";
                _output.WriteLine($"   Face {f}: Mat='{mat}'");
                for (int v = 0; v < face.VertexIndices.Length; v++)
                {
                    int vi = face.VertexIndices[v];
                    int ti = (v < face.TextureIndices.Length) ? face.TextureIndices[v] : -1;
                    Vector2 uv = (ti >= 0 && ti < mesh.TextureVertices.Count) ? mesh.TextureVertices[ti] : Vector2.Zero;
                    _output.WriteLine($"      v[{v}] vi={vi} rawPos={mesh.Vertices[vi]} rawUv={uv}");
                }
            }
        }
    }

    [Fact]
    public void PrintRaw3DoHierarchyDef()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        string text = Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path));
        using var r = new StringReader(text);
        string? l;
        bool inHier = false;
        while ((l = r.ReadLine()) != null)
        {
            if (l.Contains("SECTION: HIERARCHYDEF", StringComparison.OrdinalIgnoreCase)) { inHier = true; }
            if (inHier)
            {
                _output.WriteLine(l);
                if (l.Contains("SECTION:", StringComparison.OrdinalIgnoreCase) && !l.Contains("HIERARCHYDEF")) break;
            }
        }
    }

    [Fact]
    public void InspectRyFaceTexturePixelsTopToBottom()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(res2Path) || !File.Exists(jk1Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        using var jk1 = GobArchive.FromFile(jk1Path);

        // Load CMP palette
        var cmpEntry = res2.Entries.FirstOrDefault(e => e.Path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(cmpEntry.Path);
        var pal = CmpPalette.Parse(res2.ReadAllBytes(cmpEntry.Path));

        // Load ryface.mat
        var matEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryface.mat", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(matEntry.Path);
        var mat = MatMaterial.Parse(res2.ReadAllBytes(matEntry.Path));

        _output.WriteLine($"ryface.mat: {mat.Width}x{mat.Height}");
        var rgba = mat.ToRgba32(pal);

        _output.WriteLine("=== ASCII ART OF ryface.mat (Row 0 at top) ===");
        for (int y = 0; y < mat.Height; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < mat.Width; x++)
            {
                var c = rgba[y * mat.Width + x];
                int b = (c.R + c.G + c.B) / 3;
                char ch = b < 50 ? '#' : b < 100 ? '*' : b < 150 ? '+' : b < 200 ? ':' : ' ';
                sb.Append(ch);
            }
            _output.WriteLine($"{y,2}: {sb}");
        }
    }

    [Fact]
    public void InspectRyFaceTextureAndUVs()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);

        // 1. Check ryface.mat
        var matEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryface.mat", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(matEntry.Path);
        var mat = OpenSith.Core.Materials.MatMaterial.Parse(res2.ReadAllBytes(matEntry.Path));
        _output.WriteLine($"ryface.mat: W={mat.Width}, H={mat.Height}");

        // 2. Check Mesh 11 in rhstr.3do
        var granEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(granEntry.Path);
        var gran = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(granEntry.Path)));
        var mesh = gran.Meshes[gran.Nodes[14].MeshIndex];

        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            string matName = (face.MaterialIndex >= 0 && face.MaterialIndex < gran.Materials.Count) ? gran.Materials[face.MaterialIndex] : "";
            if (!matName.Contains("face")) continue;

            _output.WriteLine($"Face {f}: Mat='{matName}', Normal={face.Normal}");
            for (int v = 0; v < face.VertexIndices.Length; v++)
            {
                int vi = face.VertexIndices[v];
                int ti = (v < face.TextureIndices.Length) ? face.TextureIndices[v] : -1;
                Vector3 rawPos = mesh.Vertices[vi];
                Vector2 rawUv = (ti >= 0 && ti < mesh.TextureVertices.Count) ? mesh.TextureVertices[ti] : Vector2.Zero;
                float normalizedU = rawUv.X / mat.Width;
                float normalizedV_Current = -rawUv.Y / mat.Height;
                float normalizedV_Direct = rawUv.Y / mat.Height;
                _output.WriteLine($"   v[{v}] vi={vi}: rawPos={rawPos}, rawUV={rawUv}, NormU={normalizedU:F3}, CurrentV={normalizedV_Current:F3}, DirectV={normalizedV_Direct:F3}");
            }
        }
    }

    [Fact]
    public void CheckReeYeesFaceNormalsInWorldSpace()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(jk1Path) || !File.Exists(res2Path)) return;
        using var jk1 = GobArchive.FromFile(jk1Path);
        using var res2 = GobArchive.FromFile(res2Path);

        var levelEntry = jk1.Entries.FirstOrDefault(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(levelEntry.Path);
        var level = JklReader.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(levelEntry.Path)));
        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);
        var t207 = level.Things[207];

        var granEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(granEntry.Path);
        var gran = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(granEntry.Path)));

        var keyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(keyEntry.Path);
        var key = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(keyEntry.Path)));

        var animTransforms = ThreeDoHierarchy.ComputeTransforms(gran, key, 0f);
        var modelMatrix = SithMatrix34.Build34(t207.PitchYawRoll, t207.Position).ToMatrix4x4();

        int headNodeIdx = 14;
        Matrix4x4 headWorldTransform = animTransforms[headNodeIdx] * modelMatrix;

        Vector3 kyleEye = spawn.Position + new Vector3(0, 0, 0.18f);
        Vector3 reeYeesHeadPos = Vector3.Transform(Vector3.Zero, headWorldTransform);
        Vector3 dirToKyle = Vector3.Normalize(kyleEye - reeYeesHeadPos);

        _output.WriteLine($"Ree-Yees Head Pos: {reeYeesHeadPos}, Kyle Eye Pos: {kyleEye}");
        _output.WriteLine($"Direction from Ree-Yees to Kyle: {dirToKyle}");

        var mesh = gran.Meshes[gran.Nodes[headNodeIdx].MeshIndex];
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            string mat = (face.MaterialIndex >= 0 && face.MaterialIndex < gran.Materials.Count) ? gran.Materials[face.MaterialIndex] : "";
            Vector3 worldNormal = Vector3.Normalize(Vector3.TransformNormal(face.Normal, headWorldTransform));
            float dotToKyle = Vector3.Dot(worldNormal, dirToKyle);
            _output.WriteLine($"Face {f,2}: Mat='{mat,-12}', LocalNorm={face.Normal}, WorldNorm={worldNormal}, DotWithKyle={dotToKyle:F4}");
        }
    }

    [Fact]
    public void CheckChinVsForeheadHeightInM2AndGran()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);

        // 1. Bartender m2.3do
        var m2Entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("m2.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(m2Entry.Path);
        var m2 = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(m2Entry.Path)));
        var m2Transforms = ThreeDoHierarchy.ComputeTransforms(m2, null, 0f);

        int m2HeadNode = 1;
        var m2HeadMesh = m2.Meshes[m2.Nodes[m2HeadNode].MeshIndex];
        Vector3 m2ChinLocal = m2HeadMesh.Vertices[34];
        Vector3 m2ForeheadLocal = m2HeadMesh.Vertices[14];

        Vector3 m2ChinTrans = Vector3.Transform(m2ChinLocal, m2Transforms[m2HeadNode]);
        Vector3 m2ForeheadTrans = Vector3.Transform(m2ForeheadLocal, m2Transforms[m2HeadNode]);

        _output.WriteLine($"=== BARTENDER (m2.3do) REST ===");
        _output.WriteLine($"  Chin Local: {m2ChinLocal} -> Transformed: {m2ChinTrans}");
        _output.WriteLine($"  Forehead Local: {m2ForeheadLocal} -> Transformed: {m2ForeheadTrans}");
        _output.WriteLine($"  Is Forehead Higher Than Chin? {m2ForeheadTrans.Z > m2ChinTrans.Z} (Forehead.Z={m2ForeheadTrans.Z}, Chin.Z={m2ChinTrans.Z})");

        var m2KeyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("m2idle.key", StringComparison.OrdinalIgnoreCase));
        if (m2KeyEntry.Path != null)
        {
            var m2Key = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(m2KeyEntry.Path)));
            var m2AnimTransforms = ThreeDoHierarchy.ComputeTransforms(m2, m2Key, 0f);
            var chinAnim = Vector3.Transform(m2ChinLocal, m2AnimTransforms[m2HeadNode]);
            var foreheadAnim = Vector3.Transform(m2ForeheadLocal, m2AnimTransforms[m2HeadNode]);
            _output.WriteLine($"=== BARTENDER (m2.3do) ANIM (m2idle.key) ===");
            _output.WriteLine($"  Chin Anim: {chinAnim}, Forehead Anim: {foreheadAnim}");
            _output.WriteLine($"  Is Forehead Higher Than Chin in Anim? {foreheadAnim.Z > chinAnim.Z}");
        }

        // 2. Gran rhstr.3do
        var granEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(granEntry.Path);
        var gran = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(granEntry.Path)));
        var granTransforms = ThreeDoHierarchy.ComputeTransforms(gran, null, 0f);

        int granHeadNode = 14;
        var granHeadMesh = gran.Meshes[gran.Nodes[granHeadNode].MeshIndex];
        Vector3 granChinLocal = granHeadMesh.Vertices[3];
        Vector3 granTopLocal = granHeadMesh.Vertices[1];

        Vector3 granChinTrans = Vector3.Transform(granChinLocal, granTransforms[granHeadNode]);
        Vector3 granTopTrans = Vector3.Transform(granTopLocal, granTransforms[granHeadNode]);

        _output.WriteLine($"=== GRAN (rhstr.3do) REST ===");
        _output.WriteLine($"  Chin Local: {granChinLocal} -> Transformed: {granChinTrans}");
        _output.WriteLine($"  Top of Head Local: {granTopLocal} -> Transformed: {granTopTrans}");
        _output.WriteLine($"  Is Top of Head Higher Than Chin? {granTopTrans.Z > granChinTrans.Z} (Top.Z={granTopTrans.Z}, Chin.Z={granChinTrans.Z})");

        var granKeyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        if (granKeyEntry.Path != null)
        {
            var granKey = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(granKeyEntry.Path)));
            var granAnimTransforms = ThreeDoHierarchy.ComputeTransforms(gran, granKey, 0f);
            var chinAnim = Vector3.Transform(granChinLocal, granAnimTransforms[granHeadNode]);
            var topAnim = Vector3.Transform(granTopLocal, granAnimTransforms[granHeadNode]);
            _output.WriteLine($"=== GRAN (rhstr.3do) ANIM (ryidleg.key) ===");
            _output.WriteLine($"  Chin Anim: {chinAnim}, Top Anim: {topAnim}");
            _output.WriteLine($"  Is Top of Head Higher Than Chin in Anim? {topAnim.Z > chinAnim.Z}");
        }
    }

    [Fact]
    public void PrintRawKeyframeText()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        string text = Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path));
        using var r = new StringReader(text);
        for (int i = 0; i < 40; i++)
        {
            var l = r.ReadLine();
            if (l == null) break;
            _output.WriteLine(l);
        }
    }

    [Fact]
    public void InspectRyIdleKeyNodes()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var key = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));

        _output.WriteLine($"=== ryidleg.key: {key.NodeEntries.Count} tracks ===");
        foreach (var (nodeIdx, entries) in key.NodeEntries)
        {
            _output.WriteLine($"  Track Node {nodeIdx}: {entries.Count} frames");
            foreach (var frame in entries)
            {
                _output.WriteLine($"     Frame {frame.Frame}: Pos={frame.Position}, PYR={frame.PitchYawRoll}");
            }
        }
    }

    [Fact]
    public void InspectRyStrPup()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rystr.pup", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        string content = Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path));
        _output.WriteLine("=== rystr.pup ===");
        _output.WriteLine(content);
    }

    [Fact]
    public void CheckReeYeesBhGunPuppet()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;
        using var jk1 = GobArchive.FromFile(jk1Path);
        var entry = jk1.Entries.FirstOrDefault(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var level = JklReader.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(entry.Path)));

        var t207 = level.Things[207];
        _output.WriteLine($"Thing 207: Name='{t207.TemplateName}', Model='{t207.Model3D}'");

        string current = t207.TemplateName;
        while (!string.IsNullOrEmpty(current) && !current.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (level.Templates.TryGetValue(current, out var tmpl))
            {
                _output.WriteLine($"  Template '{current}': BasedOn='{tmpl.BasedOn}', Model='{tmpl.Model3D}'");
                foreach (var (k, v) in tmpl.Parameters)
                {
                    _output.WriteLine($"     Param '{k}' = '{v}'");
                }
                current = tmpl.BasedOn;
            }
            else
            {
                _output.WriteLine($"  Template '{current}' NOT FOUND in level.Templates!");
                break;
            }
        }
    }

    [Fact]
    public void InspectM2HeadMaterial()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);

        var matEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("m2-headf.mat", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(matEntry.Path);
        var bytes = res2.ReadAllBytes(matEntry.Path);
        var mat = OpenSith.Core.Materials.MatMaterial.Parse(bytes);
        _output.WriteLine($"m2-headf.mat: Type={mat.MaterialType}, Records={mat.RecordCount}, W={mat.Width}, H={mat.Height}");
    }

    [Fact]
    public void InspectM2Mesh0Vertices()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("m2.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));

        var headMesh = model.Meshes[0];
        _output.WriteLine($"=== m2.3do Head Mesh 0: {headMesh.Vertices.Count} verts, {headMesh.Faces.Count} faces ===");
        for (int f = 0; f < headMesh.Faces.Count; f++)
        {
            var face = headMesh.Faces[f];
            string mat = (face.MaterialIndex >= 0 && face.MaterialIndex < model.Materials.Count) ? model.Materials[face.MaterialIndex] : "";
            _output.WriteLine($"Face {f,2}: Mat='{mat}', Normal={face.Normal}, VCount={face.VertexIndices.Length}");
            for (int v = 0; v < face.VertexIndices.Length; v++)
            {
                int vi = face.VertexIndices[v];
                int ti = (v < face.TextureIndices.Length) ? face.TextureIndices[v] : -1;
                Vector2 uv = (ti >= 0 && ti < headMesh.TextureVertices.Count) ? headMesh.TextureVertices[ti] : Vector2.Zero;
                _output.WriteLine($"   v[{v}] vi={vi} rawPos={headMesh.Vertices[vi]} uv={uv}");
            }
        }
    }

    [Fact]
    public void InspectM2AndMnNodesAndHierarchy()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);

        foreach (string modelName in new[] { "m2.3do", "mn.3do" })
        {
            var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(modelName, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(entry.Path);
            var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));

            _output.WriteLine($"==================== {modelName} ====================");
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                var n = model.Nodes[i];
                _output.WriteLine($"Node {i,2}: Name='{n.Name}', Parent={n.ParentIndex}, MeshIdx={n.MeshIndex}, Pos={n.Position}, Pivot={n.Pivot}, PYR={n.PitchYawRoll}");
            }
        }
    }

    [Fact]
    public void InspectHeadMeshVerticesTransform()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);

        var granEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(granEntry.Path);
        var gran = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(granEntry.Path)));

        var keyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(keyEntry.Path);
        var key = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(keyEntry.Path)));

        var animTransforms = ThreeDoHierarchy.ComputeTransforms(gran, key, 0f);
        var restTransforms = ThreeDoHierarchy.ComputeTransforms(gran, null, 0f);

        int headNodeIdx = 14;
        int meshIdx = gran.Nodes[headNodeIdx].MeshIndex;
        var mesh = gran.Meshes[meshIdx];

        _output.WriteLine("=== HEAD MESH 11 RAW VERTICES vs TRANSFORMED ===");
        for (int v = 0; v < mesh.Vertices.Count; v++)
        {
            var raw = mesh.Vertices[v];
            var animV = Vector3.Transform(raw, animTransforms[headNodeIdx]);
            var restV = Vector3.Transform(raw, restTransforms[headNodeIdx]);
            _output.WriteLine($"  Vert {v,2}: Raw={raw} -> Rest={restV} -> Anim={animV}");
        }

        // Check Torso Mesh 13 Vertices
        int torsoNodeIdx = 20;
        int torsoMeshIdx = gran.Nodes[torsoNodeIdx].MeshIndex;
        var torsoMesh = gran.Meshes[torsoMeshIdx];
        _output.WriteLine("=== TORSO MESH 13 RAW VERTICES vs TRANSFORMED (first 5) ===");
        for (int v = 0; v < 5; v++)
        {
            var raw = torsoMesh.Vertices[v];
            var animV = Vector3.Transform(raw, animTransforms[torsoNodeIdx]);
            var restV = Vector3.Transform(raw, restTransforms[torsoNodeIdx]);
            _output.WriteLine($"  Torso Vert {v,2}: Raw={raw} -> Rest={restV} -> Anim={animV}");
        }
    }

    [Fact]
    public void InspectRhstrNodesAndMeshes()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);

        // 1. rhstr.3do (Gran)
        var granEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(granEntry.Path);
        var gran = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(granEntry.Path)));

        _output.WriteLine("=== rhstr.3do NODES AND MESHES ===");
        for (int i = 0; i < gran.Nodes.Count; i++)
        {
            var n = gran.Nodes[i];
            if (n.Name.Contains("head", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("torso", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("eye", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("neck", StringComparison.OrdinalIgnoreCase))
            {
                _output.WriteLine($"Node {i,2}: Name='{n.Name}', MeshIdx={n.MeshIndex}, Parent={n.ParentIndex}, Pos={n.Position}, Pivot={n.Pivot}, PYR={n.PitchYawRoll}");
                if (n.MeshIndex >= 0 && n.MeshIndex < gran.Meshes.Count)
                {
                    var mesh = gran.Meshes[n.MeshIndex];
                    _output.WriteLine($"  Mesh {n.MeshIndex}: {mesh.Vertices.Count} verts, {mesh.Faces.Count} faces");
                    for (int f = 0; f < mesh.Faces.Count; f++)
                    {
                        var face = mesh.Faces[f];
                        string mat = (face.MaterialIndex >= 0 && face.MaterialIndex < gran.Materials.Count) ? gran.Materials[face.MaterialIndex] : "";
                        _output.WriteLine($"    Face {f,2}: Mat='{mat}', Normal={face.Normal}, V0={mesh.Vertices[face.VertexIndices[0]]}");
                    }
                }
            }
        }
    }
    
    [Fact]
    public void CheckFrontFacingDirection()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var m2Entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("m2.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(m2Entry.Path);
        var m2Model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(m2Entry.Path)));

        _output.WriteLine("=== m2.3do CHEST AND HEAD FRONT / BACK FACES ===");
        for (int m = 0; m < m2Model.Meshes.Count; m++)
        {
            var mesh = m2Model.Meshes[m];
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var face = mesh.Faces[f];
                string mat = (face.MaterialIndex >= 0 && face.MaterialIndex < m2Model.Materials.Count)
                    ? m2Model.Materials[face.MaterialIndex] : "";

                if (mat.Contains("chstf", StringComparison.OrdinalIgnoreCase) || mat.Contains("headf", StringComparison.OrdinalIgnoreCase) ||
                    mat.Contains("chstb", StringComparison.OrdinalIgnoreCase) || mat.Contains("headbk", StringComparison.OrdinalIgnoreCase))
                {
                    Vector3 faceCenter = Vector3.Zero;
                    foreach (var vi in face.VertexIndices) faceCenter += mesh.Vertices[vi];
                    faceCenter /= face.VertexIndices.Length;

                    _output.WriteLine($"  Mesh {m} Face {f} Mat='{mat}': Normal={face.Normal}, Center={faceCenter}");
                }
            }
        }
    }
    
    [Fact]
    public void InspectCantinaSpawnArea()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;
        using var jk1 = GobArchive.FromFile(jk1Path);
        var entry = jk1.Entries.FirstOrDefault(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry.Path);
        var level = JklReader.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(entry.Path)));

        _output.WriteLine("=== SECTOR 339 THINGS ===");
        foreach (var t in level.Things)
        {
            if (t.SectorIndex == 339 || t.SectorIndex == 349)
            {
                _output.WriteLine($"Thing {t.ThingIndex,3}: Name='{t.TemplateName}', Model='{t.Model3D}', Sec={t.SectorIndex}, Pos={t.Position}, PYR={t.PitchYawRoll}");
            }
        }
    }

    [Fact]
    public void CompareBuild34WithThingMeshRendererModelMatrix()
    {
        var pyr = new Vector3(0, 120.005f, 0);
        var pos = new Vector3(-9.70404f, -5.74837f, -1.07722f);

        var sithM = SithMatrix34.Build34(pyr, pos).ToMatrix4x4();

        float yawRad = -pyr.Y * (MathF.PI / 180.0f);
        float pitchRad = pyr.X * (MathF.PI / 180.0f);
        float rollRad = pyr.Z * (MathF.PI / 180.0f);

        Matrix4x4 currentModelMatrix = Matrix4x4.CreateRotationX(pitchRad) *
                               Matrix4x4.CreateRotationY(rollRad) *
                               Matrix4x4.CreateRotationZ(yawRad) *
                               Matrix4x4.CreateTranslation(pos);

        _output.WriteLine("=== SITHMATRIX34 (LUCASARTS AUTHENTIC) ===");
        _output.WriteLine($"Row 0 (R): {sithM.M11}, {sithM.M12}, {sithM.M13}");
        _output.WriteLine($"Row 1 (L): {sithM.M21}, {sithM.M22}, {sithM.M23}");
        _output.WriteLine($"Row 2 (U): {sithM.M31}, {sithM.M32}, {sithM.M33}");
        _output.WriteLine($"Row 3 (T): {sithM.M41}, {sithM.M42}, {sithM.M43}");

        _output.WriteLine("=== CURRENT THINGMESHRENDERER MODELMATRIX ===");
        _output.WriteLine($"Row 0 (R): {currentModelMatrix.M11}, {currentModelMatrix.M12}, {currentModelMatrix.M13}");
        _output.WriteLine($"Row 1 (L): {currentModelMatrix.M21}, {currentModelMatrix.M22}, {currentModelMatrix.M23}");
        _output.WriteLine($"Row 2 (U): {currentModelMatrix.M31}, {currentModelMatrix.M32}, {currentModelMatrix.M33}");
        _output.WriteLine($"Row 3 (T): {currentModelMatrix.M41}, {currentModelMatrix.M42}, {currentModelMatrix.M43}");
    }

    [Fact]
    public void VerifyKyleAndReeYeesFaceToFaceAlignment()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(jk1Path) || !File.Exists(res2Path)) return;

        using var jk1 = GobArchive.FromFile(jk1Path);
        using var res2 = GobArchive.FromFile(res2Path);

        var levelEntry = jk1.Entries.FirstOrDefault(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(levelEntry.Path);
        var level = JklReader.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(levelEntry.Path)));

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        var t207 = level.Things[207]; // Ree-Yees (sitting Gran)
        Assert.Equal("reeyeesbhgun", t207.TemplateName);

        // 1. Camera forward at spawn
        Vector3 kyleEye = spawn.Position + new Vector3(0, 0, 0.18f);
        float pitchRad = MathF.PI / 180.0f * spawn.PitchYawRoll.X;
        float yawRad = MathF.PI / 180.0f * spawn.PitchYawRoll.Y;
        Vector3 kyleLook = Vector3.Normalize(new Vector3(
            -MathF.Sin(yawRad) * MathF.Cos(pitchRad),
            MathF.Cos(yawRad) * MathF.Cos(pitchRad),
            MathF.Sin(pitchRad)
        ));

        // 2. Ree-Yees world placement
        var modelEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(modelEntry.Path);
        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(modelEntry.Path)));

        var keyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(keyEntry.Path);
        var key = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(keyEntry.Path)));

        var transforms = ThreeDoHierarchy.ComputeTransforms(model, key, 0f);
        var modelMatrix = SithMatrix34.Build34(t207.PitchYawRoll, t207.Position).ToMatrix4x4();

        int headNodeIdx = model.Nodes.FindIndex(n => n.Name.Equals("rehead", StringComparison.OrdinalIgnoreCase));
        int torsoNodeIdx = model.Nodes.FindIndex(n => n.Name.Equals("retorso", StringComparison.OrdinalIgnoreCase));
        int midEyeNodeIdx = model.Nodes.FindIndex(n => n.Name.Equals("remideye", StringComparison.OrdinalIgnoreCase));

        Assert.True(headNodeIdx >= 0);
        Assert.True(torsoNodeIdx >= 0);
        Assert.True(midEyeNodeIdx >= 0);

        Matrix4x4 headWorldTransform = transforms[headNodeIdx] * modelMatrix;
        Matrix4x4 torsoWorldTransform = transforms[torsoNodeIdx] * modelMatrix;
        Matrix4x4 midEyeWorldTransform = transforms[midEyeNodeIdx] * modelMatrix;

        Vector3 headWorldPos = Vector3.Transform(Vector3.Zero, headWorldTransform);
        Vector3 torsoWorldPos = Vector3.Transform(Vector3.Zero, torsoWorldTransform);
        Vector3 midEyeWorldPos = Vector3.Transform(Vector3.Zero, midEyeWorldTransform);

        Vector3 headWorldLook = Vector3.Normalize(Vector3.TransformNormal(new Vector3(0, 1, 0), headWorldTransform));
        Vector3 headWorldUp = Vector3.Normalize(Vector3.TransformNormal(new Vector3(0, 0, 1), headWorldTransform));

        _output.WriteLine($"Kyle Eye Pos: {kyleEye}, Camera Look: {kyleLook}");
        _output.WriteLine($"Ree-Yees Head Pos: {headWorldPos}, Look: {headWorldLook}, Up: {headWorldUp}");
        _output.WriteLine($"Ree-Yees Torso Pos: {torsoWorldPos}, MidEye Pos: {midEyeWorldPos}");

        // Kyle looks at Ree-Yees across the table
        Vector3 kyleToReeYees = Vector3.Normalize(headWorldPos - kyleEye);
        float kyleAimDot = Vector3.Dot(kyleLook, kyleToReeYees);
        _output.WriteLine($"Kyle Aim Alignment Dot: {kyleAimDot:F4}");
        Assert.True(kyleAimDot > 0.85f, $"Kyle should look directly at Ree-Yees across the booth table (dot={kyleAimDot})");

        // Ree-Yees looks towards Kyle across the table (3D dot ~0.58 due to seated pitch, 2D horizontal dot > 0.90)
        Vector3 reeYeesToKyle = Vector3.Normalize(kyleEye - headWorldPos);
        float reeYeesAimDot = Vector3.Dot(headWorldLook, reeYeesToKyle);
        _output.WriteLine($"Ree-Yees Look Alignment Dot (3D): {reeYeesAimDot:F4}");
        Assert.True(reeYeesAimDot > 0.50f, $"Ree-Yees should face Kyle across the booth table (dot={reeYeesAimDot})");

        Vector2 reeYeesLook2D = Vector2.Normalize(new Vector2(headWorldLook.X, headWorldLook.Y));
        Vector2 reeYeesToKyle2D = Vector2.Normalize(new Vector2(reeYeesToKyle.X, reeYeesToKyle.Y));
        float horizontalDot = Vector2.Dot(reeYeesLook2D, reeYeesToKyle2D);
        _output.WriteLine($"Ree-Yees Horizontal Alignment Dot (2D): {horizontalDot:F4}");
        Assert.True(horizontalDot > 0.90f, $"Ree-Yees horizontal heading should align directly with Kyle (2D dot={horizontalDot})");

        // Head must be strictly upright in world space (+Z is up)
        Assert.True(headWorldUp.Z > 0.8f, $"Ree-Yees head must be upright (+Z up), got Up.Z={headWorldUp.Z}");

        // Head must be above torso in world space
        Assert.True(headWorldPos.Z > torsoWorldPos.Z, $"Head (Z={headWorldPos.Z}) must be higher than Torso (Z={torsoWorldPos.Z})");

        // Third eye must be closer to Kyle than the back of the head
        float distEyeToKyle = Vector3.Distance(midEyeWorldPos, kyleEye);
        float distHeadToKyle = Vector3.Distance(headWorldPos, kyleEye);
        _output.WriteLine($"Dist Eye to Kyle: {distEyeToKyle:F4}, Dist Head to Kyle: {distHeadToKyle:F4}");
        Assert.True(distEyeToKyle < distHeadToKyle, "Ree-Yees eyes must be on front of face pointing towards Kyle");
    }

    [Fact]
    public void TestAuthenticAccumulateMatricesModelSpace()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var modelEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(modelEntry.Path);
        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(modelEntry.Path)));

        var keyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(keyEntry.Path);
        var key = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(keyEntry.Path)));

        // Test with keyframe
        var animTransforms = ThreeDoHierarchy.ComputeTransforms(model, key, 0f);
        // Test rest pose (no keyframe)
        var restTransforms = ThreeDoHierarchy.ComputeTransforms(model, null, 0f);

        int headIdx = -1, torsoIdx = -1, neckIdx = -1, lFootIdx = -1, rFootIdx = -1;
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            string name = model.Nodes[i].Name.ToLowerInvariant();
            if (name.Contains("head")) headIdx = i;
            if (name.Contains("torso")) torsoIdx = i;
            if (name.Contains("neck")) neckIdx = i;
            if (name.Contains("lfoot")) lFootIdx = i;
            if (name.Contains("rfoot")) rFootIdx = i;
        }

        Assert.True(headIdx >= 0);
        Assert.True(torsoIdx >= 0);
        Assert.True(neckIdx >= 0);
        Assert.True(lFootIdx >= 0);
        Assert.True(rFootIdx >= 0);

        _output.WriteLine($"Keyframe Anim - Head: {animTransforms[headIdx].Translation}");
        _output.WriteLine($"Keyframe Anim - Neck: {animTransforms[neckIdx].Translation}");
        _output.WriteLine($"Keyframe Anim - Torso: {animTransforms[torsoIdx].Translation}");
        _output.WriteLine($"Keyframe Anim - LFoot: {animTransforms[lFootIdx].Translation}");
        _output.WriteLine($"Keyframe Anim - RFoot: {animTransforms[rFootIdx].Translation}");

        // In model space, Z is Up: Head > Neck > Torso > Feet
        Assert.True(animTransforms[headIdx].Translation.Z > animTransforms[neckIdx].Translation.Z, "Head must be higher than Neck");
        Assert.True(animTransforms[neckIdx].Translation.Z > animTransforms[torsoIdx].Translation.Z, "Neck must be higher than Torso");
        Assert.True(animTransforms[torsoIdx].Translation.Z > animTransforms[lFootIdx].Translation.Z, "Torso must be higher than Feet");

        // Check head upright direction (Z component of Up vector must be > 0.8)
        var headUp = Vector3.TransformNormal(Vector3.UnitZ, animTransforms[headIdx]);
        _output.WriteLine($"Head Up Direction: {headUp}");
        Assert.True(headUp.Z > 0.7f, $"Head Up direction must point upward, got {headUp.Z}");

        // Check head forward direction (Lvec: Y component must be positive, facing forward)
        var headLook = Vector3.TransformNormal(new Vector3(0, 1, 0), animTransforms[headIdx]);
        _output.WriteLine($"Head Look Direction: {headLook}");
        Assert.True(headLook.Y > 0.5f, $"Head Look direction must face forward (+Y), got {headLook.Y}");
    }

    [Fact]
    public void InspectGranHeadAndSensoryOrgans()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);
        var granEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(granEntry.Path);
        var gran = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(granEntry.Path)));

        var keyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(keyEntry.Path);
        var key = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(keyEntry.Path)));

        var animTransforms = ThreeDoHierarchy.ComputeTransforms(gran, key, 0f);
        var restTransforms = ThreeDoHierarchy.ComputeTransforms(gran, null, 0f);

        _output.WriteLine("=== GRAN (rhstr.3do) HEAD & APPENDAGES ===");
        for (int i = 0; i < gran.Nodes.Count; i++)
        {
            var n = gran.Nodes[i];
            if (n.Name.Contains("head") || n.Name.Contains("eye") || n.Name.Contains("ear") || n.Name.Contains("neck") || n.Name.Contains("torso"))
            {
                var animPos = animTransforms[i].Translation;
                var restPos = restTransforms[i].Translation;
                var animUp = Vector3.TransformNormal(Vector3.UnitZ, animTransforms[i]);
                var restUp = Vector3.TransformNormal(Vector3.UnitZ, restTransforms[i]);
                _output.WriteLine($"Node {i,2} ('{n.Name}', parent {n.ParentIndex}):");
                _output.WriteLine($"   Pivot={n.Pivot}, Pos={n.Position}");
                _output.WriteLine($"   REST: Pos={restPos}, Up={restUp}");
                _output.WriteLine($"   ANIM: Pos={animPos}, Up={animUp}");

                if (n.MeshIndex >= 0 && n.MeshIndex < gran.Meshes.Count)
                {
                    var mesh = gran.Meshes[n.MeshIndex];
                    Vector3 minRaw = new(float.MaxValue), maxRaw = new(float.MinValue);
                    Vector3 minAnim = new(float.MaxValue), maxAnim = new(float.MinValue);
                    foreach (var v in mesh.Vertices)
                    {
                        minRaw = Vector3.Min(minRaw, v); maxRaw = Vector3.Max(maxRaw, v);
                        var av = Vector3.Transform(v, animTransforms[i]);
                        minAnim = Vector3.Min(minAnim, av); maxAnim = Vector3.Max(maxAnim, av);
                    }
                    _output.WriteLine($"   Mesh {n.MeshIndex} ({mesh.Vertices.Count} v): RawZ=[{minRaw.Z:F4}, {maxRaw.Z:F4}], AnimZ=[{minAnim.Z:F4}, {maxAnim.Z:F4}]");
                }
            }
        }
    }

    [Fact]
    public void InspectAllCantinaActorsPuppetsAndKeyframes()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(jk1Path) || !File.Exists(res2Path)) return;

        using var jk1 = GobArchive.FromFile(jk1Path);
        using var res2 = GobArchive.FromFile(res2Path);

        var levelEntry = jk1.Entries.FirstOrDefault(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(levelEntry.Path);
        var level = JklReader.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(levelEntry.Path)));

        _output.WriteLine("=== ALL THINGS WITH MODELS IN 01NARSHADDA ===");
        var checkedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var thing in level.Things)
        {
            string modelName = thing.Model3D;
            if (string.IsNullOrEmpty(modelName) && level.Templates.TryGetValue(thing.TemplateName, out var tmpl))
            {
                modelName = tmpl.Model3D;
            }

            if (string.IsNullOrEmpty(modelName)) continue;

            // Find puppet
            string current = thing.TemplateName;
            string? puppetName = null;
            while (!string.IsNullOrEmpty(current) && !current.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (level.Templates.TryGetValue(current, out var t))
                {
                    if (t.Parameters.TryGetValue("puppet", out var pup) && !string.IsNullOrEmpty(pup))
                    {
                        puppetName = pup;
                        break;
                    }
                    current = t.BasedOn;
                }
                else break;
            }

            if (!checkedModels.Add(modelName)) continue;

            _output.WriteLine($"Thing {thing.ThingIndex,3}: Name='{thing.TemplateName}', Model='{modelName}', Puppet='{puppetName}'");

            var modelEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(modelName, StringComparison.OrdinalIgnoreCase));
            if (modelEntry.Path == null) continue;

            var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(modelEntry.Path)));
            
            // Find head node
            int headIdx = model.Nodes.FindIndex(n => n.Name.Contains("head", StringComparison.OrdinalIgnoreCase));
            int torsoIdx = model.Nodes.FindIndex(n => n.Name.Contains("torso", StringComparison.OrdinalIgnoreCase));

            _output.WriteLine($"  Model '{modelName}': Nodes={model.Nodes.Count}, Meshes={model.Meshes.Count}, HeadNode={headIdx}, TorsoNode={torsoIdx}");
            if (headIdx >= 0)
            {
                var hn = model.Nodes[headIdx];
                _output.WriteLine($"    Head Node: Name='{hn.Name}', Parent={hn.ParentIndex}, Pos={hn.Position}, Pivot={hn.Pivot}, PYR={hn.PitchYawRoll}");
            }

            // Check Rest Pose Transforms
            var restTransforms = ThreeDoHierarchy.ComputeTransforms(model, null, 0f);
            if (headIdx >= 0)
            {
                var hWorld = restTransforms[headIdx];
                var hUp = Vector3.TransformNormal(Vector3.UnitZ, hWorld);
                var hLook = Vector3.TransformNormal(new Vector3(0, 1, 0), hWorld);
                _output.WriteLine($"    REST POSE Head Up: {hUp}, Look: {hLook}, Pos: {hWorld.Translation}");
            }

            // Check Puppet / Keyframe if available
            if (!string.IsNullOrEmpty(puppetName))
            {
                var pupEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(puppetName, StringComparison.OrdinalIgnoreCase));
                if (pupEntry.Path != null)
                {
                    var pup = PuppetReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(pupEntry.Path)), puppetName);
                    string? idleKey = pup.GetKeyframe(0, "stand");
                    _output.WriteLine($"    Puppet '{puppetName}' -> Stand Keyframe: '{idleKey}'");
                    if (!string.IsNullOrEmpty(idleKey))
                    {
                        var keyEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(idleKey, StringComparison.OrdinalIgnoreCase));
                        if (keyEntry.Path != null)
                        {
                            var key = KeyframeReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(keyEntry.Path)), idleKey);
                            var animTransforms = ThreeDoHierarchy.ComputeTransforms(model, key, 0f);
                            if (headIdx >= 0)
                            {
                                var hWorld = animTransforms[headIdx];
                                var hUp = Vector3.TransformNormal(Vector3.UnitZ, hWorld);
                                var hLook = Vector3.TransformNormal(new Vector3(0, 1, 0), hWorld);
                                _output.WriteLine($"    ANIM POSE Head Up: {hUp}, Look: {hLook}, Pos: {hWorld.Translation}");
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void AssertNpcFaceTextureMappingIsUpright()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;
        using var res2 = GobArchive.FromFile(res2Path);

        // Models to test: Ree-Yees, Bartender, Civilian, Kyle Katarn
        string[] testModels = ["rhstr.3do", "m2.3do", "mn.3do", "ky.3do"];

        foreach (string modelFile in testModels)
        {
            var entry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(modelFile, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(entry.Path);
            var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(entry.Path)));

            var headNode = model.Nodes.FirstOrDefault(n => n.Name.Contains("head", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(headNode);
            Assert.True(headNode.MeshIndex >= 0 && headNode.MeshIndex < model.Meshes.Count);

            var mesh = model.Meshes[headNode.MeshIndex];

            // Find faces with face/head material
            foreach (var face in mesh.Faces)
            {
                if (face.MaterialIndex < 0 || face.MaterialIndex >= model.Materials.Count) continue;
                string matName = model.Materials[face.MaterialIndex];
                if (!matName.Contains("face", StringComparison.OrdinalIgnoreCase) &&
                    !matName.Contains("head", StringComparison.OrdinalIgnoreCase)) continue;

                // Load material to get texture height
                var matEntry = res2.Entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(matName, StringComparison.OrdinalIgnoreCase));
                if (matEntry.Path == null) continue;
                var mat = MatMaterial.Parse(res2.ReadAllBytes(matEntry.Path));
                float texH = mat.Height;

                // Find highest vertex (max Z) and lowest vertex (min Z) in this face
                int highestV = -1;
                int lowestV = -1;
                float maxZ = float.MinValue;
                float minZ = float.MaxValue;

                for (int v = 0; v < face.VertexIndices.Length; v++)
                {
                    int vi = face.VertexIndices[v];
                    float z = mesh.Vertices[vi].Z;
                    if (z > maxZ) { maxZ = z; highestV = v; }
                    if (z < minZ) { minZ = z; lowestV = v; }
                }

                // If vertices have distinct vertical positions
                if (maxZ - minZ > 0.005f && highestV >= 0 && lowestV >= 0)
                {
                    int tiHigh = face.TextureIndices[highestV];
                    int tiLow = face.TextureIndices[lowestV];

                    Vector2 rawUvHigh = mesh.TextureVertices[tiHigh];
                    Vector2 rawUvLow = mesh.TextureVertices[tiLow];

                    // With modern OpenGL mapping: V = 1.0f + (rawUv.Y / texH)
                    float vHigh = 1.0f + (rawUvHigh.Y / texH);
                    float vLow = 1.0f + (rawUvLow.Y / texH);

                    // High vertex (forehead/eyes) must map to upper texture row (lower V)
                    // Low vertex (chin/mouth) must map to lower texture row (higher V)
                    _output.WriteLine($"{modelFile} Face with '{matName}': HighZ={maxZ:F4}->V={vHigh:F3}, LowZ={minZ:F4}->V={vLow:F3}");
                    Assert.True(vHigh < vLow, $"{modelFile} face '{matName}': High vertex V ({vHigh}) should be less than low vertex V ({vLow})");
                }
            }
        }
    }
}
