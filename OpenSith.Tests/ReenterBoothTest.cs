using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class ReenterBoothTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public ReenterBoothTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestWalkOutAndWalkBackIn()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        var player = new PlayerPhysics(spawn.Position, spawn.SectorIndex);
        float dt = 1.0f / 60.0f;

        // 1. Land
        for (int i = 0; i < 20; i++) player.Update(dt, Vector3.Zero, false, level);
        _output.WriteLine($"Landed: Pos={player.Position}, Sector={player.CurrentSectorIndex}");
        Assert.Equal(339, player.CurrentSectorIndex);

        // 2. Walk forward out of booth into hallway (Sector 349)
        Vector3 forwardY = new Vector3(0, 1, 0);
        for (int i = 0; i < 40; i++)
        {
            player.Update(dt, forwardY, false, level);
        }
        _output.WriteLine($"In hallway: Pos={player.Position}, Sector={player.CurrentSectorIndex}");
        Assert.Equal(349, player.CurrentSectorIndex);

        // 3. Reverse direction and walk back into booth 339 (-Y direction)
        Vector3 backY = new Vector3(0, -1, 0);
        for (int i = 0; i < 60; i++)
        {
            int prevSec = player.CurrentSectorIndex;
            player.Update(dt, backY, false, level);
            if (i % 10 == 0 || player.CurrentSectorIndex != prevSec)
            {
                _output.WriteLine($"Step {i}: Pos={player.Position}, Sector={player.CurrentSectorIndex}, Grounded={player.IsGrounded}, Vel={player.Velocity}");
            }
        }

        _output.WriteLine($"Final: Pos={player.Position}, Sector={player.CurrentSectorIndex}");
        Assert.Equal(339, player.CurrentSectorIndex);
    }
}
