using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Animation;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class GranHeadDiagnosticTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    public GranHeadDiagnosticTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestHeadFacingDirection()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var bytes3do = archive.ReadAllBytes("3do/rhstr.3do");
        Assert.NotNull(bytes3do);
        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(bytes3do));

        var bytesKey = archive.ReadAllBytes("3do/key/ryidleg.key");
        Assert.NotNull(bytesKey);
        var key = KeyframeReader.Parse(Encoding.ASCII.GetString(bytesKey));

        int nodeCount = model.Nodes.Count;

        // 1. Static node transforms
        Matrix4x4[] staticLocal = new Matrix4x4[nodeCount];
        Matrix4x4[] staticWorld = new Matrix4x4[nodeCount];
        bool[] compStatic = new bool[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            var n = model.Nodes[i];
            float px = n.PitchYawRoll.X * (MathF.PI / 180f);
            float py = n.PitchYawRoll.Y * (MathF.PI / 180f);
            float pz = n.PitchYawRoll.Z * (MathF.PI / 180f);
            Matrix4x4 rot = Matrix4x4.CreateRotationX(px) * Matrix4x4.CreateRotationY(py) * Matrix4x4.CreateRotationZ(pz);
            staticLocal[i] = Matrix4x4.CreateTranslation(-n.Pivot) * rot * Matrix4x4.CreateTranslation(n.Pivot) * Matrix4x4.CreateTranslation(n.Position);
        }
        for (int i = 0; i < nodeCount; i++) GetNodeTransform(i, model, staticLocal, staticWorld, compStatic);

        // 2. Animated node transforms currently used in ThingMeshRenderer
        Matrix4x4[] animLocal = new Matrix4x4[nodeCount];
        Matrix4x4[] animWorld = new Matrix4x4[nodeCount];
        bool[] compAnim = new bool[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            var n = model.Nodes[i];
            if (key.NodeEntries.TryGetValue(i, out var entries) && entries.Count > 0)
            {
                var entry = entries[0];
                float pRad = entry.PitchYawRoll.X * (MathF.PI / 180.0f);
                float yRad = entry.PitchYawRoll.Y * (MathF.PI / 180.0f);
                float rRad = entry.PitchYawRoll.Z * (MathF.PI / 180.0f);

                Matrix4x4 rotX = Matrix4x4.CreateRotationX(pRad);
                Matrix4x4 rotY = Matrix4x4.CreateRotationY(rRad);
                Matrix4x4 rotZ = Matrix4x4.CreateRotationZ(yRad);

                Matrix4x4 rot = rotZ * rotX * rotY;
                Matrix4x4 trans = Matrix4x4.CreateTranslation(entry.Position);

                animLocal[i] = Matrix4x4.CreateTranslation(-n.Pivot) * rot * Matrix4x4.CreateTranslation(n.Pivot) * trans;
            }
            else
            {
                animLocal[i] = staticLocal[i];
            }
        }
        for (int i = 0; i < nodeCount; i++) GetNodeTransform(i, model, animLocal, animWorld, compAnim);

        // Let's check the forward vector (Vector3.UnitY: in Sith Engine, +Y is FORWARD, +Z is UP)
        // For static head:
        Vector3 staticHeadForward = Vector3.TransformNormal(Vector3.UnitY, staticWorld[14]);
        Vector3 staticHeadUp = Vector3.TransformNormal(Vector3.UnitZ, staticWorld[14]);

        // For anim head:
        Vector3 animHeadForward = Vector3.TransformNormal(Vector3.UnitY, animWorld[14]);
        Vector3 animHeadUp = Vector3.TransformNormal(Vector3.UnitZ, animWorld[14]);

        _output.WriteLine($"Static Head: Forward={staticHeadForward}, Up={staticHeadUp}");
        _output.WriteLine($"Anim Head:   Forward={animHeadForward}, Up={animHeadUp}");

        // Also check the middle eye (Node 16) position relative to head position
        Vector3 sHeadPos = Vector3.Transform(Vector3.Zero, staticWorld[14]);
        Vector3 sEyePos = Vector3.Transform(Vector3.Zero, staticWorld[16]);
        Vector3 sEyeOffset = sEyePos - sHeadPos;

        Vector3 aHeadPos = Vector3.Transform(Vector3.Zero, animWorld[14]);
        Vector3 aEyePos = Vector3.Transform(Vector3.Zero, animWorld[16]);
        Vector3 aEyeOffset = aEyePos - aHeadPos;

        _output.WriteLine($"Static Eye offset from head: {sEyeOffset}");
        _output.WriteLine($"Anim Eye offset from head:   {aEyeOffset}");
    }

    private static Matrix4x4 GetNodeTransform(int nodeIdx, ThreeDoModel model, Matrix4x4[] localTransforms, Matrix4x4[] nodeTransforms, bool[] computed)
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
}
