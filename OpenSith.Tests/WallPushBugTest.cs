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

public class WallPushBugTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public WallPushBugTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SimulateUserWasdMovement()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);
        var player = new PlayerPhysics(spawn.Position, spawn.SectorIndex);

        // Yaw was -60 degrees
        float yaw = spawn.PitchYawRoll.Y; // -59.99
        float yawRad = MathF.PI / 180.0f * yaw;
        Vector3 forward = new Vector3(MathF.Cos(yawRad), MathF.Sin(yawRad), 0);

        _output.WriteLine($"Initial Pos: {player.Position}, Yaw={yaw}, Forward={forward}");

        _output.WriteLine($"\n--- Walking Towards Exit (+Y) ---");
        float dt = 1.0f / 60.0f;
        var exitPlayer = new PlayerPhysics(spawn.Position, spawn.SectorIndex);
        for (int i = 0; i < 60; i++)
        {
            exitPlayer.Update(dt, new Vector3(0, 1, 0), false, level);
            if (i % 10 == 0)
            {
                _output.WriteLine($"Tick {i}: Pos={exitPlayer.Position}, Sector={exitPlayer.CurrentSectorIndex}, Grounded={exitPlayer.IsGrounded}");
            }
        }
    }
}
