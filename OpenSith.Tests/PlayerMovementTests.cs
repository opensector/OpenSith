using System;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class PlayerMovementTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public PlayerMovementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestPlayerWalkAcrossSector0()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var spawn = level.Things[172];
        Assert.NotNull(spawn);
        Assert.Equal(0, spawn.SectorIndex);

        var player = new PlayerPhysics(spawn.Position, spawn.SectorIndex);
        _output.WriteLine($"Spawn at: {player.Position} in Sector {player.CurrentSectorIndex}");

        // Land on floor
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 30; i++)
        {
            player.Update(dt, Vector3.Zero, false, level);
        }

        _output.WriteLine($"Landed at: {player.Position}, Grounded: {player.IsGrounded}");
        Assert.True(player.IsGrounded);
        Assert.Equal(5.299998f, player.Position.Z, 0.01f);
    }

    [Fact]
    public void TestPortalMirrorLinking()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        Assert.True(level.Adjoins.Count > 1000);

        var sec0 = level.Sectors[0];
        int portalsWithMirrorSector = 0;
        foreach (var s in sec0.Surfaces)
        {
            if (s.IsPortal)
            {
                Assert.NotNull(s.MirrorSector);
                Assert.NotNull(s.MirrorSurface);
                portalsWithMirrorSector++;
            }
        }

        _output.WriteLine($"Sector 0 has {portalsWithMirrorSector} portals correctly linked to mirror sectors.");
        Assert.True(portalsWithMirrorSector >= 10);
    }
}
