using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CantinaWalkSimulationTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public CantinaWalkSimulationTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SimulateWalkingFromBoothToCantina()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;

        using var archive = GobArchive.FromFile(jk1Path);
        var entry = archive.Entries.First(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        string jklText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
        var level = JklReader.Parse(jklText);

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        var physics = new PlayerPhysics(spawn.Position, spawn.SectorIndex);
        _output.WriteLine($"Initial Spawn: Sector={physics.CurrentSectorIndex}, Pos={physics.Position}");

        float dt = 1.0f / 60.0f;

        // Path waypoints from Booth 339 -> Hallway 349 -> Arch 337 -> Foyer 361 -> Arch 352 -> Cantina 370
        Vector3[] waypoints = [
            new Vector3(-10.35f, -4.95f, -1.25f),
            new Vector3(-9.75f, -4.875f, -1.25f),
            new Vector3(-9.55f, -4.875f, -1.30f),
            new Vector3(-9.50f, -5.375f, -1.30f),
            new Vector3(-9.00f, -5.375f, -1.30f)
        ];

        int currentWp = 0;
        int totalTicks = 0;
        int maxTicks = 800;

        int prevSector = physics.CurrentSectorIndex;
        Vector3 lastReportedPos = physics.Position;

        while (currentWp < waypoints.Length && totalTicks < maxTicks)
        {
            Vector3 target = waypoints[currentWp];
            Vector3 toTarget = target - physics.Position;
            toTarget.Z = 0; // horizontal only
            float distToWp = toTarget.Length();

            if (distToWp < 0.06f)
            {
                _output.WriteLine($"Reached Waypoint {currentWp} at tick {totalTicks}: Pos={physics.Position}, Sector={physics.CurrentSectorIndex}");
                currentWp++;
                continue;
            }

            Vector3 wishDir = Vector3.Normalize(toTarget);
            physics.Update(dt, wishDir, false, level);
            totalTicks++;

            if (physics.CurrentSectorIndex != prevSector)
            {
                _output.WriteLine($"[Tick {totalTicks,3}] TRANSITION: Sector {prevSector} -> Sector {physics.CurrentSectorIndex} at Pos={physics.Position}");
                prevSector = physics.CurrentSectorIndex;
            }

            if (totalTicks % 60 == 0)
            {
                float moved = (physics.Position - lastReportedPos).Length();
                _output.WriteLine($"[Tick {totalTicks,3}] Status: Pos={physics.Position}, Vel={physics.Velocity}, Sector={physics.CurrentSectorIndex}, Grounded={physics.IsGrounded}, DistToWp={distToWp:F3}, MovedLastSec={moved:F3}");
                lastReportedPos = physics.Position;
            }
        }

        _output.WriteLine($"Final state at tick {totalTicks}: Pos={physics.Position}, Sector={physics.CurrentSectorIndex}, WaypointsReached={currentWp}/{waypoints.Length}");

        // Assert all waypoints reached and Kyle entered the main cantina (Sector 370)
        Assert.Equal(waypoints.Length, currentWp);
        Assert.Equal(370, physics.CurrentSectorIndex);
    }

    [Fact]
    public void SimulateRoundTripCantinaWalk()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;

        using var archive = GobArchive.FromFile(jk1Path);
        var entry = archive.Entries.First(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        string jklText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
        var level = JklReader.Parse(jklText);

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        var physics = new PlayerPhysics(spawn.Position, spawn.SectorIndex);
        float dt = 1.0f / 60.0f;

        // Waypoints: outbound to Sector 370, then return to spawn in Sector 339
        Vector3[] waypoints = [
            new Vector3(-10.35f, -4.95f, -1.25f),
            new Vector3(-9.75f, -4.875f, -1.25f),
            new Vector3(-9.55f, -4.875f, -1.30f),
            new Vector3(-9.50f, -5.375f, -1.30f),
            new Vector3(-9.00f, -5.375f, -1.30f), // At cantina
            new Vector3(-9.50f, -5.375f, -1.30f), // Return turn
            new Vector3(-9.55f, -4.875f, -1.30f),
            new Vector3(-9.75f, -4.875f, -1.25f),
            new Vector3(-10.35f, -4.95f, -1.25f),
            spawn.Position                        // Back in booth
        ];

        int currentWp = 0;
        int totalTicks = 0;
        int maxTicks = 1600;

        while (currentWp < waypoints.Length && totalTicks < maxTicks)
        {
            Vector3 target = waypoints[currentWp];
            Vector3 toTarget = target - physics.Position;
            toTarget.Z = 0;
            float distToWp = toTarget.Length();

            if (distToWp < 0.08f)
            {
                currentWp++;
                continue;
            }

            Vector3 wishDir = Vector3.Normalize(toTarget);
            physics.Update(dt, wishDir, false, level);
            totalTicks++;
        }

        _output.WriteLine($"Round-trip finished: Waypoints={currentWp}/{waypoints.Length}, EndPos={physics.Position}, EndSector={physics.CurrentSectorIndex}");
        Assert.Equal(waypoints.Length, currentWp);
        Assert.Equal(339, physics.CurrentSectorIndex);
    }
}
