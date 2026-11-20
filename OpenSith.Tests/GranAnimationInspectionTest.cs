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

public class GranAnimationInspectionTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public GranAnimationInspectionTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectGranModelAndKeyframe()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path)) return;

        using var archive = GobArchive.FromFile(res2Path);

        // 1. Load rhstr.3do
        var entry3do = archive.Entries.FirstOrDefault(e => e.Path.Equals("3do/rhstr.3do", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry3do.Path);
        string text3do = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry3do.Path));
        var model = ThreeDoReader.Parse(text3do);

        _output.WriteLine("=== rhstr.3do NODES ===");
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var n = model.Nodes[i];
            _output.WriteLine($"Node {i,2}: name='{n.Name,-12}' parent={n.ParentIndex,2} mesh={n.MeshIndex,2} pos=({n.Position.X:F4}, {n.Position.Y:F4}, {n.Position.Z:F4}) pivot=({n.Pivot.X:F4}, {n.Pivot.Y:F4}, {n.Pivot.Z:F4}) pyr=({n.PitchYawRoll.X:F2}, {n.PitchYawRoll.Y:F2}, {n.PitchYawRoll.Z:F2})");
        }

        // 2. Check mesh for node 14 (head) and parent node 17 (neck)
        int headNodeIdx = model.Nodes.FindIndex(n => n.Name.Contains("head", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"\nHead node index: {headNodeIdx}");
        if (headNodeIdx >= 0)
        {
            var headNode = model.Nodes[headNodeIdx];
            if (headNode.MeshIndex >= 0 && headNode.MeshIndex < model.Meshes.Count)
            {
                var mesh = model.Meshes[headNode.MeshIndex];
                _output.WriteLine($"Head mesh '{mesh.Name}' has {mesh.Vertices.Count} vertices, {mesh.Faces.Count} faces.");
                // Compute bounding box / average of head vertices in local mesh space
                Vector3 min = new(float.MaxValue), max = new(float.MinValue), avg = Vector3.Zero;
                foreach (var v in mesh.Vertices)
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                    avg += v;
                }
                avg /= mesh.Vertices.Count;
                _output.WriteLine($"Head vertices bounds: min=({min.X:F4}, {min.Y:F4}, {min.Z:F4}) max=({max.X:F4}, {max.Y:F4}, {max.Z:F4}) avg=({avg.X:F4}, {avg.Y:F4}, {avg.Z:F4})");
            }
        }

        // 3. Find keyframe for ryidleg.key or gran idle
        var keyEntry = archive.Entries.FirstOrDefault(e => e.Path.EndsWith("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        if (keyEntry.Path == null)
        {
            _output.WriteLine("ryidleg.key not found in res2.gob, searching all .key files...");
            foreach (var e in archive.Entries.Where(x => x.Path.EndsWith(".key", StringComparison.OrdinalIgnoreCase)).Take(10))
            {
                _output.WriteLine($"  {e.Path}");
            }
        }
        else
        {
            _output.WriteLine($"Found {keyEntry.Path}");
            string keyText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(keyEntry.Path));
            var track = KeyframeReader.Parse(keyText, "ryidleg.key");
            _output.WriteLine($"Track: frames={track.TotalFrames}, fps={track.Fps}, joints={track.JointCount}, nodesWithEntries={track.NodeEntries.Count}");
            
            foreach (var kv in track.NodeEntries.OrderBy(k => k.Key))
            {
                int nIdx = kv.Key;
                string nodeName = (nIdx >= 0 && nIdx < model.Nodes.Count) ? model.Nodes[nIdx].Name : "unknown";
                var e0 = kv.Value[0];
                _output.WriteLine($"  Key Node {nIdx,2} ('{nodeName,-10}'): {kv.Value.Count} entries. Entry0: pos=({e0.Position.X:F4}, {e0.Position.Y:F4}, {e0.Position.Z:F4}) pyr=({e0.PitchYawRoll.X:F2}, {e0.PitchYawRoll.Y:F2}, {e0.PitchYawRoll.Z:F2})");
            }
        }
    }
}
