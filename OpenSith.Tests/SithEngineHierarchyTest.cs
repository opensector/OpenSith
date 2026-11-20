using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Core.Animation;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class SithEngineHierarchyTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public SithEngineHierarchyTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestGranAccumulateMatrices()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;

        using var archive = GobArchive.FromFile(res2Path);
        var entry3do = archive.Entries.First(e => e.Path.Equals("3do/rhstr.3do", StringComparison.OrdinalIgnoreCase));
        string text3do = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry3do.Path));
        var model = ThreeDoReader.Parse(text3do);

        var keyEntry = archive.Entries.First(e => e.Path.EndsWith("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        string keyText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(keyEntry.Path));
        var track = KeyframeReader.Parse(keyText, "ryidleg.key");

        // Test through ThreeDoHierarchy.ComputeTransforms
        var xforms = ThreeDoHierarchy.ComputeTransforms(model, track, 0f);

        int headIdx = model.Nodes.FindIndex(n => n.Name.Contains("head", StringComparison.OrdinalIgnoreCase));
        int torsoIdx = model.Nodes.FindIndex(n => n.Name.Contains("torso", StringComparison.OrdinalIgnoreCase));
        int neckIdx = model.Nodes.FindIndex(n => n.Name.Contains("neck", StringComparison.OrdinalIgnoreCase));

        Assert.True(headIdx >= 0);
        Assert.True(torsoIdx >= 0);
        Assert.True(neckIdx >= 0);

        Matrix4x4 headXform = xforms[headIdx];
        Matrix4x4 torsoXform = xforms[torsoIdx];

        // Transform head mesh vertices and check where the nose/eyes face
        var headMesh = model.Meshes[model.Nodes[headIdx].MeshIndex];
        Vector3 headCenter = Vector3.Zero;
        foreach (var v in headMesh.Vertices)
        {
            headCenter += Vector3.Transform(v, headXform);
        }
        headCenter /= headMesh.Vertices.Count;

        var torsoMesh = model.Meshes[model.Nodes[torsoIdx].MeshIndex];
        Vector3 torsoCenter = Vector3.Zero;
        foreach (var v in torsoMesh.Vertices)
        {
            torsoCenter += Vector3.Transform(v, torsoXform);
        }
        torsoCenter /= torsoMesh.Vertices.Count;

        _output.WriteLine($"World Head Center: {headCenter}");
        _output.WriteLine($"World Torso Center: {torsoCenter}");

        // In character coordinates, head must be ABOVE torso (headCenter.Z > torsoCenter.Z)
        Assert.True(headCenter.Z > torsoCenter.Z, $"Head Z ({headCenter.Z}) must be above Torso Z ({torsoCenter.Z})");

        // Head forward direction vector (row 1 in System.Numerics row-major matrix: M21, M22, M23):
        // In Sith coordinates: +Y is forward, so M22 > 0.5 means facing forward
        Vector3 headForward = new Vector3(headXform.M21, headXform.M22, headXform.M23);
        Vector3 torsoForward = new Vector3(torsoXform.M21, torsoXform.M22, torsoXform.M23);

        _output.WriteLine($"Head Forward Vector: {headForward}");
        _output.WriteLine($"Torso Forward Vector: {torsoForward}");

        Assert.True(headForward.Y > 0.5f, $"Head forward vector Y must be > 0.5, got {headForward.Y}");
        Assert.True(torsoForward.Y > 0.8f, $"Torso forward vector Y must be > 0.8, got {torsoForward.Y}");
    }
}
