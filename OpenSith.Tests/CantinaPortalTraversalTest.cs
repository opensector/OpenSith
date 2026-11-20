using System;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class CantinaPortalTraversalTest
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void TestWalkOutOfCantinaBoothAndExplore()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var kyleSpawn = level.Things[0];
        var player = new PlayerPhysics(kyleSpawn.Position, kyleSpawn.SectorIndex);
        float dt = 1.0f / 60.0f;

        // 1. Land on booth floor
        for (int i = 0; i < 20; i++) player.Update(dt, Vector3.Zero, false, level);
        Assert.True(player.IsGrounded);
        Assert.Equal(339, player.CurrentSectorIndex);

        // 2. Step forward out of booth into hallway (Sector 349)
        Vector3 stepOut = new Vector3(0, 1, 0);
        while (player.Position.Y < -4.88f)
        {
            player.Update(dt, stepOut, false, level);
        }
        Assert.Equal(349, player.CurrentSectorIndex);

        // 3. Walk east towards door 3376 into Sector 361
        Vector3 target = new Vector3(-9.50f, -4.88f, player.Position.Z);
        for (int i = 0; i < 60; i++)
        {
            Vector3 dir = Vector3.Normalize(target - player.Position);
            player.Update(dt, dir, false, level);
        }

        // Successfully crossed door into Sector 361
        Assert.Equal(361, player.CurrentSectorIndex);
        Assert.True(player.Position.X > -9.70f);
        Assert.True(player.IsGrounded);
    }
}
