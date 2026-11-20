using System;
using System.IO;
using System.Numerics;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class RecursiveNodeTransformTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public RecursiveNodeTransformTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private Matrix4x4 GetNodeTransform(int nodeIdx, ThreeDoModel model, Matrix4x4[] localTransforms, Matrix4x4[] nodeTransforms, bool[] computed)
    {
        if (nodeIdx < 0 || nodeIdx >= model.Nodes.Count) return Matrix4x4.Identity;
        if (computed[nodeIdx]) return nodeTransforms[nodeIdx];

        var node = model.Nodes[nodeIdx];
        Matrix4x4 local = localTransforms[nodeIdx];

        if (node.ParentIndex >= 0 && node.ParentIndex != nodeIdx && node.ParentIndex < model.Nodes.Count)
        {
            Matrix4x4 parentMatrix = GetNodeTransform(node.ParentIndex, model, localTransforms, nodeTransforms, computed);
            nodeTransforms[nodeIdx] = local * parentMatrix;
        }
        else
        {
            nodeTransforms[nodeIdx] = local;
        }

        computed[nodeIdx] = true;
        return nodeTransforms[nodeIdx];
    }

    [Fact]
    public void TestGranAndTuskenTransformVertices()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));

        string[] models = ["thout.3do", "rhstr.3do"];
        foreach (var m in models)
        {
            string text = vfs.ReadAllText($"3do/{m}");
            var model = ThreeDoReader.Parse(text);

            Matrix4x4[] localTransforms = new Matrix4x4[model.Nodes.Count];
            Matrix4x4[] nodeTransforms = new Matrix4x4[model.Nodes.Count];
            bool[] computed = new bool[model.Nodes.Count];

            for (int i = 0; i < model.Nodes.Count; i++)
            {
                var node = model.Nodes[i];
                float px = node.PitchYawRoll.X * (MathF.PI / 180f);
                float py = node.PitchYawRoll.Y * (MathF.PI / 180f);
                float pz = node.PitchYawRoll.Z * (MathF.PI / 180f);

                Matrix4x4 rot = Matrix4x4.CreateRotationX(px) *
                                Matrix4x4.CreateRotationY(py) *
                                Matrix4x4.CreateRotationZ(pz);

                localTransforms[i] = Matrix4x4.CreateTranslation(-node.Pivot) *
                                    rot *
                                    Matrix4x4.CreateTranslation(node.Pivot) *
                                    Matrix4x4.CreateTranslation(node.Position);
            }

            for (int i = 0; i < model.Nodes.Count; i++)
            {
                GetNodeTransform(i, model, localTransforms, nodeTransforms, computed);
            }

            int validVerts = 0;
            Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int meshIdx = model.Nodes[i].MeshIndex;
                if (meshIdx >= 0 && meshIdx < model.Meshes.Count)
                {
                    foreach (var v in model.Meshes[meshIdx].Vertices)
                    {
                        Vector3 worldV = Vector3.Transform(v, nodeTransforms[i]);
                        min = Vector3.Min(min, worldV);
                        max = Vector3.Max(max, worldV);
                        if (worldV.LengthSquared() > 0.0001f) validVerts++;
                    }
                }
            }

            _output.WriteLine($"[TRANSFORM SUCCESS] {m}: {validVerts} non-zero vertices! Bounds Min={min}, Max={max}");
            Assert.True(validVerts > 100);
            Assert.True((max - min).Length() > 0.05f);
        }
    }
}
