using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Animation;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class AnimationEngineTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void ParseRetailPuppetAndKeyframe()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);

        // 1. Parse rystr.pup
        var pupEntry = archive.Entries.FirstOrDefault(e => e.Path.EndsWith("rystr.pup", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(pupEntry.Path));

        var pupBytes = archive.ReadAllBytes(pupEntry.Path);
        Assert.NotNull(pupBytes);
        var pup = PuppetReader.Parse(Encoding.ASCII.GetString(pupBytes), "rystr.pup");

        Assert.True(pup.Modes.Count >= 2);
        Assert.Equal("ryidleg.key", pup.GetKeyframe(0, "stand"));
        Assert.Equal("rystroll.key", pup.GetKeyframe(0, "walk"));
        Assert.Equal("rystandg.key", pup.GetKeyframe(1, "stand"));

        // 2. Parse ryidleg.key
        var keyEntry = archive.Entries.FirstOrDefault(e => e.Path.EndsWith("ryidleg.key", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(keyEntry.Path));

        var keyBytes = archive.ReadAllBytes(keyEntry.Path);
        Assert.NotNull(keyBytes);
        var key = KeyframeReader.Parse(Encoding.ASCII.GetString(keyBytes), "ryidleg.key");

        Assert.Equal(162, key.TotalFrames);
        Assert.Equal(15.0f, key.Fps);
        Assert.Equal(24, key.JointCount);
        Assert.True(key.NodeEntries.Count >= 20);

        // 3. Sample node transform at time t = 0 and t = 1.0s
        var m0 = key.SampleNodeTransform(1, 0.0f);
        var m1 = key.SampleNodeTransform(1, 1.0f);

        Assert.False(m0.IsIdentity);
        Assert.False(m1.IsIdentity);
    }
}
