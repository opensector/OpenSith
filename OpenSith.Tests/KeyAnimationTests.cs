using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Animation;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Tests for direct keyframe (.KEY) animation evaluation on non-puppet 3DO models.
/// </summary>
public class KeyAnimationTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void TieBomberTakeoffKeyframe_MovesRootNodeAlongTrajectory()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);

        var modelEntry = archive.Entries.FirstOrDefault(
            e => e.Path.Equals("3do/tieb.3do", StringComparison.OrdinalIgnoreCase) ||
                 e.Path.EndsWith("/tieb.3do", StringComparison.OrdinalIgnoreCase) ||
                 e.Path.EndsWith("\\tieb.3do", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(modelEntry.Path), "3do/tieb.3do not found in Res2.gob");

        var keyEntry = archive.Entries.FirstOrDefault(
            e => e.Path.Equals("3do/key/01vtieb.key", StringComparison.OrdinalIgnoreCase) ||
                 e.Path.EndsWith("/01vtieb.key", StringComparison.OrdinalIgnoreCase) ||
                 e.Path.EndsWith("\\01vtieb.key", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(keyEntry.Path), "3do/key/01vtieb.key not found in Res2.gob");

        byte[]? modelBytes = archive.ReadAllBytes(modelEntry.Path);
        byte[]? keyBytes = archive.ReadAllBytes(keyEntry.Path);
        Assert.NotNull(modelBytes);
        Assert.NotNull(keyBytes);

        var model = ThreeDoReader.Parse(Encoding.ASCII.GetString(modelBytes));
        var keyframe = KeyframeReader.Parse(Encoding.ASCII.GetString(keyBytes), "01vtieb.key");

        Assert.NotEmpty(model.Nodes);
        Assert.Contains("tie~bmb", model.Nodes[0].Name, StringComparison.OrdinalIgnoreCase);

        Matrix4x4[] transformsAtStart = ThreeDoHierarchy.ComputeTransforms(model, keyframe, 0.0f);
        Matrix4x4[] transformsAtTakeoff = ThreeDoHierarchy.ComputeTransforms(model, keyframe, 7.2f);

        Vector3 posStart = transformsAtStart[0].Translation;
        Vector3 posTakeoff = transformsAtTakeoff[0].Translation;

        Vector3 delta = posTakeoff - posStart;
        Assert.True(delta.Length() > 10.0f, $"Expected significant takeoff displacement, got {delta}");

        Assert.InRange(posTakeoff.X, -9.6f, -9.2f);
        Assert.InRange(posTakeoff.Y, 17.0f, 17.4f);
        Assert.InRange(posTakeoff.Z, 3.7f, 4.1f);
    }

    [Fact]
    public void SyntheticKeyframe_MovesRootNodeWithoutPuppet()
    {
        var model = new ThreeDoModel();
        model.Nodes.Add(new ThreeDoHierarchyNode
        {
            NodeIndex = 0,
            Name = "root",
            ParentIndex = -1,
            Position = Vector3.Zero,
            PitchYawRoll = Vector3.Zero,
            Pivot = Vector3.Zero
        });

        var keyframe = new KeyframeTrack
        {
            Name = "test.key",
            TotalFrames = 10,
            Fps = 10.0f
        };
        keyframe.NodeEntries[0] =
        [
            new KeyframeEntry { Frame = 0, Position = Vector3.Zero },
            new KeyframeEntry { Frame = 10, Position = new Vector3(1.0f, 2.0f, 3.0f) }
        ];

        Matrix4x4[] t0 = ThreeDoHierarchy.ComputeTransforms(model, keyframe, 0.0f);
        Matrix4x4[] tMid = ThreeDoHierarchy.ComputeTransforms(model, keyframe, 0.5f);

        Assert.Equal(Vector3.Zero, t0[0].Translation);
        Assert.InRange(tMid[0].Translation.X, 0.49f, 0.51f);
        Assert.InRange(tMid[0].Translation.Y, 0.99f, 1.01f);
        Assert.InRange(tMid[0].Translation.Z, 1.49f, 1.51f);
    }
}
