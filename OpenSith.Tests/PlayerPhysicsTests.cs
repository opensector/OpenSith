using System;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class PlayerPhysicsTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void PlayerGravity_DropsAndLandsOnFloor()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        // Start player slightly in the air above spawn point
        var player = new PlayerPhysics(spawn.Position + new Vector3(0, 0, 0.5f), spawn.SectorIndex);
        Assert.False(player.IsGrounded);

        // Step simulation for 60 ticks (1 second)
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 60; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
        }

        // Verify player landed on the solid ground
        Assert.True(player.IsGrounded);
        Assert.True(player.Position.Z <= spawn.Position.Z + 0.1f);
        Assert.Equal(0.0f, player.Velocity.Z);

        // Test Jump
        player.Update(dt, Vector3.Zero, wishJump: true, level);
        Assert.False(player.IsGrounded);
        Assert.True(player.Velocity.Z > 1.0f);
    }
}
