using System;
using System.IO;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Models;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// Retail Level 1 (<c>01narshadda.jkl</c>) regression tests for <see cref="WorldSimulation.ActivateNearestDoor"/>
/// covering all four Level 1 door mechanisms: <c>00_door.cog</c>, <c>00_1waydoor.cog</c>,
/// <c>00_doorswitch.cog</c>, and <c>01_cargodoors.cog</c>.
/// </summary>
public class DoorRegressionTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";
    private readonly ITestOutputHelper _output;

    public DoorRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static VirtualFileSystem? TryMountRetailVfs()
    {
        string res1Path = Path.Combine(SteamPath, "Resource", "Res1hi.gob");
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");

        if (!File.Exists(res2Path) || !File.Exists(jk1Path))
            return null;

        var vfs = new VirtualFileSystem();
        if (File.Exists(res1Path))
            vfs.Mount(GobArchive.FromFile(res1Path));
        vfs.Mount(GobArchive.FromFile(res2Path));
        vfs.Mount(GobArchive.FromFile(jk1Path));
        return vfs;
    }

    private static (JklLevel level, WorldSimulation sim)? TryLoadLevel1()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null)
            return null;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs);
        return (level, sim);
    }

    private static Vector3 GetSurfaceCenter(JklLevel level, JklSurface surface)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (int idx in surface.VertexIndices)
        {
            if (idx >= 0 && idx < level.Vertices.Count)
            {
                sum += level.Vertices[idx];
                count++;
            }
        }
        return count > 0 ? sum / count : Vector3.Zero;
    }

    private static float GetSurfaceMinZ(JklLevel level, JklSurface surface)
    {
        float minZ = float.MaxValue;
        foreach (int idx in surface.VertexIndices)
        {
            if (idx >= 0 && idx < level.Vertices.Count)
                minZ = MathF.Min(minZ, level.Vertices[idx].Z);
        }
        return minZ == float.MaxValue ? 0f : minZ;
    }

    private static Vector3 GetDoorApproachNormal(JklThing door)
    {
        Matrix4x4 world = SithMatrix34.Build34(door.PitchYawRoll, Vector3.Zero).ToMatrix4x4();
        // Door local Y is forward/through the door; normalize in XY plane.
        Vector3 forward = Vector3.Normalize(new Vector3(world.M21, world.M22, 0f));
        return forward;
    }

    private static Vector3 GetDoorSideAxis(JklThing door)
    {
        Matrix4x4 world = SithMatrix34.Build34(door.PitchYawRoll, Vector3.Zero).ToMatrix4x4();
        return Vector3.Normalize(new Vector3(world.M11, world.M12, 0f));
    }

    private static void StepSimulation(WorldSimulation sim, Vector3 playerPos, int playerSector, int ticks = 15)
    {
        const float dt = 1.0f / 60.0f;
        for (int i = 0; i < ticks; i++)
            sim.Update(dt, playerPos, playerSector);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(23)]
    [InlineData(65)]
    [InlineData(70)]
    [InlineData(307)]
    public void Level1_StandardDoors_ActivateFromFront(int doorThingId)
    {
        var loaded = TryLoadLevel1();
        if (loaded == null)
            return;

        var (level, sim) = loaded.Value;
        var door = level.Things[doorThingId];
        Vector3 initialPos = sim.GetThingPos(doorThingId);

        Vector3 normal = GetDoorApproachNormal(door);
        Vector3 playerPos = door.Position + normal * 0.14f;
        Vector3 aimDir = -normal;

        bool activated = sim.ActivateNearestDoor(playerPos, aimDir);
        _output.WriteLine($"Door {doorThingId} ({door.TemplateName}) at {door.Position}: activated={activated}, moving={sim.IsThingMoving(doorThingId)}");

        Assert.True(activated, $"Expected 00_door.cog thing {doorThingId} to activate.");
        Assert.True(sim.IsThingMoving(doorThingId), $"Expected door {doorThingId} to start moving.");

        StepSimulation(sim, playerPos, door.SectorIndex);
        Assert.NotEqual(initialPos, sim.GetThingPos(doorThingId));
    }

    [Fact]
    public void Level1_Door70_AngledAndDoorframeApproach_ActivatesDoor()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null)
            return;

        var (level, sim) = loaded.Value;
        const int doorThingId = 70;
        var door = level.Things[doorThingId];
        Vector3 initialPos = sim.GetThingPos(doorThingId);

        Vector3 normal = GetDoorApproachNormal(door);
        Vector3 side = GetDoorSideAxis(door);

        // Stand slightly off to the doorframe side and aim at an angle toward the door
        Vector3 playerPos = door.Position + normal * 0.13f + side * 0.10f;
        Vector3 aimDir = Vector3.Normalize(-normal - side * 0.45f);

        bool activated = sim.ActivateNearestDoor(playerPos, aimDir);
        _output.WriteLine($"Door {doorThingId} angled/doorframe approach: activated={activated}, moving={sim.IsThingMoving(doorThingId)}");

        Assert.True(activated, "Expected angled/doorframe approach on door 70 to activate rather than being shadowed by doorframe.");
        Assert.True(sim.IsThingMoving(doorThingId));

        StepSimulation(sim, playerPos, door.SectorIndex);
        Assert.NotEqual(initialPos, sim.GetThingPos(doorThingId));
    }

    [Fact]
    public void Level1_OneWayDoor47_FrontAndDoorframeApproach_Activates()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null)
            return;

        var (level, sim) = loaded.Value;
        const int doorThingId = 47;
        var door = level.Things[doorThingId];

        Vector3 normal = GetDoorApproachNormal(door);
        Vector3 side = GetDoorSideAxis(door);

        Vector3 playerPos = door.Position + normal * 0.13f + side * 0.08f;
        Vector3 aimDir = Vector3.Normalize(-normal - side * 0.25f);
        sim.Update(1.0f / 60.0f, playerPos, door.SectorIndex);

        bool activated = sim.ActivateNearestDoor(playerPos, aimDir);
        _output.WriteLine($"One-way door {doorThingId} doorframe-adjacent approach: activated={activated}, moving={sim.IsThingMoving(doorThingId)}");

        Assert.True(activated, "Expected 00_1waydoor.cog thing 47 to activate from doorframe-adjacent approach.");
    }

    [Fact]
    public void Level1_OneWayDoor47_DirectFrontApproach_Activates()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null)
            return;

        var (level, sim) = loaded.Value;
        const int doorThingId = 47;
        var door = level.Things[doorThingId];

        Vector3 unlockedNormal = -GetDoorApproachNormal(door);
        Vector3 playerPos = door.Position + unlockedNormal * 0.14f;
        sim.Update(1.0f / 60.0f, playerPos, door.SectorIndex);

        bool activated = sim.ActivateNearestDoor(playerPos, -unlockedNormal);
        Assert.True(activated, "Expected 00_1waydoor.cog thing 47 to activate from direct front approach.");
    }

    [Theory]
    [InlineData(2409, 56, 57)]
    [InlineData(2403, 56, 57)]
    public void Level1_DoorSwitchCog_HorizontalAndDirectGaze_OpensDoors(int switchSurfaceId, int doorA, int doorB)
    {
        // 1. Horizontal gaze at normal eye height
        {
            var loaded = TryLoadLevel1();
            if (loaded == null)
                return;

            var (level, sim) = loaded.Value;
            var surf = level.Surfaces[switchSurfaceId];
            Vector3 center = GetSurfaceCenter(level, surf);
            float floorZ = GetSurfaceMinZ(level, surf);

            Vector3 flatNormal = Vector3.Normalize(new Vector3(surf.Normal.X, surf.Normal.Y, 0f));
            Vector3 playerPos = new(center.X + flatNormal.X * 0.14f, center.Y + flatNormal.Y * 0.14f, floorZ);
            Vector3 horizontalAim = -flatNormal;

            bool activated = sim.ActivateNearestDoor(playerPos, horizontalAim);
            _output.WriteLine($"Switch {switchSurfaceId} horizontal gaze: activated={activated}, door{doorA}Moving={sim.IsThingMoving(doorA)}, door{doorB}Moving={sim.IsThingMoving(doorB)}");

            Assert.True(activated, $"Expected switch {switchSurfaceId} to activate with horizontal gaze.");
            Assert.True(sim.IsThingMoving(doorA) || sim.IsThingMoving(doorB),
                $"Expected doors {doorA}/{doorB} to move when switch {switchSurfaceId} is activated with horizontal gaze.");
        }

        // 2. Direct gaze aimed down at the switch surface center
        {
            var loaded = TryLoadLevel1();
            if (loaded == null)
                return;

            var (level, sim) = loaded.Value;
            var surf = level.Surfaces[switchSurfaceId];
            Vector3 center = GetSurfaceCenter(level, surf);
            float floorZ = GetSurfaceMinZ(level, surf);

            Vector3 flatNormal = Vector3.Normalize(new Vector3(surf.Normal.X, surf.Normal.Y, 0f));
            Vector3 playerPos = new(center.X + flatNormal.X * 0.14f, center.Y + flatNormal.Y * 0.14f, floorZ);
            Vector3 eyePos = playerPos + new Vector3(0f, 0f, 0.157f);
            Vector3 directAim = Vector3.Normalize(center - eyePos);

            bool activated = sim.ActivateNearestDoor(playerPos, directAim);
            _output.WriteLine($"Switch {switchSurfaceId} direct gaze: activated={activated}, door{doorA}Moving={sim.IsThingMoving(doorA)}, door{doorB}Moving={sim.IsThingMoving(doorB)}");

            Assert.True(activated, $"Expected switch {switchSurfaceId} to activate with direct gaze.");
            Assert.True(sim.IsThingMoving(doorA) || sim.IsThingMoving(doorB),
                $"Expected doors {doorA}/{doorB} to move when switch {switchSurfaceId} is activated with direct gaze.");
        }
    }

    [Fact]
    public void Level1_CargoDoorsSwitch4325_HorizontalAndDirectGaze_OpensCargoDoors()
    {
        const int switchSurfaceId = 4325;
        const int cargoDoorA = 273;
        const int cargoDoorB = 274;

        // 1. Horizontal gaze at normal eye height
        {
            var loaded = TryLoadLevel1();
            if (loaded == null)
                return;

            var (level, sim) = loaded.Value;
            var surf = level.Surfaces[switchSurfaceId];
            Vector3 center = GetSurfaceCenter(level, surf);
            float floorZ = GetSurfaceMinZ(level, surf);

            Vector3 flatNormal = Vector3.Normalize(new Vector3(surf.Normal.X, surf.Normal.Y, 0f));
            Vector3 playerPos = new(center.X + flatNormal.X * 0.14f, center.Y + flatNormal.Y * 0.14f, floorZ);
            Vector3 horizontalAim = -flatNormal;

            bool activated = sim.ActivateNearestDoor(playerPos, horizontalAim);
            _output.WriteLine($"Cargo switch {switchSurfaceId} horizontal gaze: activated={activated}, door{cargoDoorA}Moving={sim.IsThingMoving(cargoDoorA)}, door{cargoDoorB}Moving={sim.IsThingMoving(cargoDoorB)}");

            Assert.True(activated, "Expected 01_cargodoors.cog switch 4325 to activate with horizontal gaze.");
            Assert.True(sim.IsThingMoving(cargoDoorA) && sim.IsThingMoving(cargoDoorB),
                "Expected cargo doors 273 and 274 to both move when switch 4325 is activated.");
        }

        // 2. Direct gaze aimed down at the switch surface center
        {
            var loaded = TryLoadLevel1();
            if (loaded == null)
                return;

            var (level, sim) = loaded.Value;
            var surf = level.Surfaces[switchSurfaceId];
            Vector3 center = GetSurfaceCenter(level, surf);
            float floorZ = GetSurfaceMinZ(level, surf);

            Vector3 flatNormal = Vector3.Normalize(new Vector3(surf.Normal.X, surf.Normal.Y, 0f));
            Vector3 playerPos = new(center.X + flatNormal.X * 0.14f, center.Y + flatNormal.Y * 0.14f, floorZ);
            Vector3 eyePos = playerPos + new Vector3(0f, 0f, 0.157f);
            Vector3 directAim = Vector3.Normalize(center - eyePos);

            bool activated = sim.ActivateNearestDoor(playerPos, directAim);
            _output.WriteLine($"Cargo switch {switchSurfaceId} direct gaze: activated={activated}, door{cargoDoorA}Moving={sim.IsThingMoving(cargoDoorA)}, door{cargoDoorB}Moving={sim.IsThingMoving(cargoDoorB)}");

            Assert.True(activated, "Expected 01_cargodoors.cog switch 4325 to activate with direct gaze.");
            Assert.True(sim.IsThingMoving(cargoDoorA) && sim.IsThingMoving(cargoDoorB),
                "Expected cargo doors 273 and 274 to both move when switch 4325 is activated.");
        }
    }
}
