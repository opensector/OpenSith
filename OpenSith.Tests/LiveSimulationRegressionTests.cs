using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Animation;
using OpenSith.Core.Models;
using OpenSith.Core.Physics;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// End-to-end live simulation regression tests covering NPC floor height containment,
/// multiplayer walkplayer spawn filtering, and closed-door line-of-sight / projectile blocking.
/// </summary>
public class LiveSimulationRegressionTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";
    private readonly ITestOutputHelper _output;

    public LiveSimulationRegressionTests(ITestOutputHelper output)
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

    private static bool TryGetSectorFloorZ(JklLevel level, int sectorIndex, float x, float y, float refZ, out float floorZ)
    {
        floorZ = float.MinValue;
        bool found = false;

        if (sectorIndex < 0 || sectorIndex >= level.Sectors.Count)
            return false;

        var sector = level.Sectors[sectorIndex];
        foreach (var surface in sector.Surfaces)
        {
            if (surface.IsPortal || surface.VertexIndices.Length < 3 || surface.Normal.Z <= 0.4f)
                continue;

            var verts = new Vector3[surface.VertexIndices.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                int idx = surface.VertexIndices[i];
                verts[i] = (idx >= 0 && idx < level.Vertices.Count) ? level.Vertices[idx] : Vector3.Zero;
            }

            if (!IsPointInPolygon2D(x, y, verts))
                continue;

            Vector3 v0 = verts[0];
            float planeZ = v0.Z - ((surface.Normal.X * (x - v0.X) + surface.Normal.Y * (y - v0.Y)) / surface.Normal.Z);
            if (planeZ <= refZ + 0.05f && planeZ > floorZ)
            {
                floorZ = planeZ;
                found = true;
            }
        }

        return found;
    }

    private static bool IsPointInPolygon2D(float px, float py, Vector3[] verts)
    {
        bool inside = false;
        int n = verts.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = verts[i].X, yi = verts[i].Y;
            float xj = verts[j].X, yj = verts[j].Y;
            bool intersect = ((yi > py) != (yj > py)) &&
                             (px < (xj - xi) * (py - yi) / ((yj - yi) == 0f ? 1e-6f : (yj - yi)) + xi);
            if (intersect)
                inside = !inside;
        }
        return inside;
    }

    private static bool HasAiClass(JklLevel level, string templateName)
    {
        string current = templateName;
        int guard = 0;
        while (!string.IsNullOrEmpty(current) && !current.Equals("none", StringComparison.OrdinalIgnoreCase) && guard++ < 32)
        {
            if (!level.Templates.TryGetValue(current, out var tmpl))
                break;

            if (tmpl.Parameters.TryGetValue("aiclass", out var val) && !string.IsNullOrWhiteSpace(val))
                return true;

            current = tmpl.BasedOn;
        }
        return false;
    }

    [Fact]
    public void LiveSim_AllNpcsStayAboveFloor_After180Ticks()
    {
        using var vfs = TryMountRetailVfs();
        if (vfs == null)
            return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs);

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        // Identify all NPC things loaded by WorldSimulation
        var npcThings = level.Things
            .Where(t => t.ThingIndex != spawn.ThingIndex &&
                        !t.IsPlayerSpawn &&
                        t.SectorIndex >= 0 &&
                        t.SectorIndex < level.Sectors.Count &&
                        HasAiClass(level, t.TemplateName))
            .ToList();

        Assert.NotEmpty(npcThings);

        // Record initial heights above floor before simulation
        var initialHeights = new Dictionary<int, float>();
        foreach (var npc in npcThings)
        {
            if (TryGetSectorFloorZ(level, npc.SectorIndex, npc.Position.X, npc.Position.Y, npc.Position.Z, out float initFloorZ))
                initialHeights[npc.ThingIndex] = npc.Position.Z - initFloorZ;
        }

        // Place player in the main cantina (Sector 370 if valid, else spawn) so nearby NPCs actively move
        Vector3 playerPos = level.Sectors.Count > 370
            ? new Vector3(-9.00f, -5.375f, -1.30f)
            : spawn.Position;
        int playerSector = level.Sectors.Count > 370 ? 370 : spawn.SectorIndex;

        const float dt = 1.0f / 60.0f;
        for (int tick = 0; tick < 180; tick++)
        {
            sim.Update(dt, playerPos, playerSector);
        }

        int checkedCount = 0;
        foreach (var npc in npcThings)
        {
            if (!TryGetSectorFloorZ(level, npc.SectorIndex, npc.Position.X, npc.Position.Y, npc.Position.Z, out float floorZ))
                continue;

            checkedCount++;
            float heightAboveFloor = npc.Position.Z - floorZ;
            float minAllowed = initialHeights.TryGetValue(npc.ThingIndex, out float initH) && initH < 0.08f
                ? MathF.Max(0.01f, initH - 0.005f) // Short droids (e.g. mb at 0.04, r5 at 0.0645) start < 0.08 and must not sink
                : 0.08f;

            Assert.True(
                npc.Position.Z >= floorZ + minAllowed,
                $"NPC Thing {npc.ThingIndex} ({npc.TemplateName}) in Sector {npc.SectorIndex} sank near/below floor: " +
                $"Pos.Z={npc.Position.Z:F4}, floorZ={floorZ:F4}, heightAboveFloor={heightAboveFloor:F4}");
        }

        _output.WriteLine($"Verified {checkedCount}/{npcThings.Count} NPCs stayed above sector floor after 180 ticks.");
        Assert.True(checkedCount > 0, "Expected at least one NPC with a valid sector floor to verify.");
    }

    [Fact]
    public void LiveSim_NoExtraWalkplayerSpawnsRendered()
    {
        using var vfs = TryMountRetailVfs();
        if (vfs == null)
            return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var extraSpawns = level.Things.Where(t => t.IsPlayerSpawn && t.ThingIndex != 0).ToList();

        Assert.NotEmpty(extraSpawns);
        _output.WriteLine($"Found {extraSpawns.Count} extra walkplayer spawn things in 01narshadda.jkl.");

        foreach (var extra in extraSpawns)
        {
            // Never rendered in 1st-person (playerThingIndex = 0) or 3rd-person (playerThingIndex = -1)
            Assert.False(ThingMeshRenderer.ShouldRenderThing(extra, playerThingIndex: 0));
            Assert.False(ThingMeshRenderer.ShouldRenderThing(extra, playerThingIndex: -1));
        }

        var playerZero = level.Things[0];
        Assert.True(playerZero.IsPlayerSpawn);
        Assert.False(ThingMeshRenderer.ShouldRenderThing(playerZero, playerThingIndex: 0));
        Assert.True(ThingMeshRenderer.ShouldRenderThing(playerZero, playerThingIndex: -1));
    }

    [Fact]
    public void LiveSim_ClosedDoorBlocksLineOfSightAndProjectiles()
    {
        using var vfs = TryMountRetailVfs();
        if (vfs == null)
            return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs);

        // Find a framed door in 01narshadda.jkl that starts closed and has clear space across its face
        JklThing? testDoor = null;
        Vector3 sideA = Vector3.Zero;
        Vector3 sideB = Vector3.Zero;

        foreach (var thing in level.Things)
        {
            if (!thing.TemplateName.Contains("door", StringComparison.OrdinalIgnoreCase) || !sim.IsDoorClosed(thing.ThingIndex))
                continue;

            var mat = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position);
            Vector3 center = thing.Position + new Vector3(0f, 0f, 0.12f);
            Vector3[] probeDirs = [Vector3.Normalize(mat.Lvec), Vector3.Normalize(mat.Rvec)];
            foreach (var dir in probeDirs)
            {
                Vector3 a = center - dir * 0.22f;
                Vector3 b = center + dir * 0.22f;
                if (!sim.HasLineOfSight(a, b) &&
                    sim.TryRaycastWorld(a, b, out Vector3 hit) &&
                    Vector3.Distance(hit, center) < 0.20f)
                {
                    testDoor = thing;
                    sideA = a;
                    sideB = b;
                    break;
                }
            }

            if (testDoor != null)
                break;
        }

        Assert.NotNull(testDoor);
        _output.WriteLine($"Testing closed door Thing {testDoor.ThingIndex} ({testDoor.TemplateName}) at {testDoor.Position}.");

        // 1. Line of sight across the closed door is blocked (at both mid-height and eye-height)
        Assert.False(sim.HasLineOfSight(sideA, sideB));
        Assert.False(sim.HasLineOfSight(sideA + new Vector3(0f, 0f, 0.10f), sideB + new Vector3(0f, 0f, 0.10f)));

        // 2. An enemy positioned on sideA cannot see or shoot through the closed door at player on sideB
        var rangedEnemy = level.Things.FirstOrDefault(t =>
            t.TemplateName.Contains("gun", StringComparison.OrdinalIgnoreCase) &&
            t.ThingIndex != spawnThingId(level));
        if (rangedEnemy != null)
        {
            rangedEnemy.Position = sideA;
            rangedEnemy.SectorIndex = testDoor.SectorIndex;
        }

        // 3. Projectile fired from sideA toward sideB must be blocked by the closed door before reaching sideB
        Vector3 fireDir = Vector3.Normalize(sideB - sideA);
        sim.FireWeaponRay(sideA, fireDir, maxRange: 5.0f, damage: 25f, isMelee: false);
        var playerBolt = Assert.Single(sim.Projectiles, p => !p.IsEnemy);

        const float dt = 1.0f / 60.0f;
        float maxProgressTowardB = 0f;
        float totalDist = Vector3.Distance(sideA, sideB);
        for (int i = 0; i < 10; i++)
        {
            if (sim.Projectiles.Contains(playerBolt))
            {
                float progress = Vector3.Dot(playerBolt.Position - sideA, fireDir);
                if (progress > maxProgressTowardB)
                    maxProgressTowardB = progress;
            }
            sim.Update(dt, sideB, testDoor.SectorIndex);
        }

        // Bolt should have impacted the closed door and been destroyed before reaching sideB
        Assert.DoesNotContain(playerBolt, sim.Projectiles);
        Assert.True(maxProgressTowardB < totalDist, $"Projectile passed through closed door! maxProgress={maxProgressTowardB:F3}, totalDist={totalDist:F3}");

        // Ranged enemy on sideA must not have fired at sideB through the closed door
        if (rangedEnemy != null)
        {
            Assert.DoesNotContain(sim.Projectiles, p => p.OwnerThingIndex == rangedEnemy.ThingIndex);
        }

        // 4. Activating/opening the door unblocks line of sight across it
        bool activated = sim.ActivateNearestDoor(sideA, fireDir);
        if (!activated)
            sim.MoveToFrame(testDoor.ThingIndex, 1, speed: 20.0f);

        for (int i = 0; i < 120 && (sim.IsThingMoving(testDoor.ThingIndex) || sim.IsDoorClosed(testDoor.ThingIndex)); i++)
        {
            sim.Update(dt, sideB, testDoor.SectorIndex);
        }

        Assert.False(sim.IsDoorClosed(testDoor.ThingIndex));
        Assert.True(sim.HasLineOfSight(sideA, sideB), "Opening the door should unblock line of sight.");

        static int spawnThingId(JklLevel lvl) => lvl.GetPlayerSpawn()?.ThingIndex ?? 0;
    }

    [Fact]
    public void Synthetic_ShouldRenderThing_FiltersExtraWalkplayers()
    {
        var mainSpawn = new JklThing { ThingIndex = 0, TemplateName = "walkplayer" };
        var mpSpawn1 = new JklThing { ThingIndex = 12, TemplateName = "walkplayer", ThingFlags = (uint)SithThingFlags.Disabled };
        var mpSpawn2 = new JklThing { ThingIndex = 45, TemplateName = "kyle", ThingFlags = (uint)SithThingFlags.Disabled };
        var stormtrooper = new JklThing { ThingIndex = 7, TemplateName = "st_trooper" };

        Assert.False(ThingMeshRenderer.ShouldRenderThing(mainSpawn, playerThingIndex: 0));
        Assert.True(ThingMeshRenderer.ShouldRenderThing(mainSpawn, playerThingIndex: -1));

        Assert.False(ThingMeshRenderer.ShouldRenderThing(mpSpawn1, playerThingIndex: 0));
        Assert.False(ThingMeshRenderer.ShouldRenderThing(mpSpawn1, playerThingIndex: -1));
        Assert.False(ThingMeshRenderer.ShouldRenderThing(mpSpawn2, playerThingIndex: 0));
        Assert.False(ThingMeshRenderer.ShouldRenderThing(mpSpawn2, playerThingIndex: -1));

        Assert.True(ThingMeshRenderer.ShouldRenderThing(stormtrooper, playerThingIndex: 0));
        Assert.True(ThingMeshRenderer.ShouldRenderThing(stormtrooper, playerThingIndex: -1));
    }

    [Fact]
    public void Synthetic_NpcMaintainsHipHeightAndClosedDoorBlocksLos()
    {
        // Build a synthetic hallway with a closed framed door in the middle
        var level = new JklLevel { Name = "synthetic_sim" };
        var sec = new JklSector
        {
            SectorIndex = 0,
            BoundBoxMin = new Vector3(-2f, -2f, 0f),
            BoundBoxMax = new Vector3(2f, 2f, 1f)
        };
        level.Sectors.Add(sec);

        // Floor at Z = 0, ceiling at Z = 1
        AddQuad(level, sec,
            new Vector3(-2f, -2f, 0f), new Vector3(2f, -2f, 0f),
            new Vector3(2f, 2f, 0f), new Vector3(-2f, 2f, 0f),
            new Vector3(0f, 0f, 1f));
        AddQuad(level, sec,
            new Vector3(-2f, 2f, 1f), new Vector3(2f, 2f, 1f),
            new Vector3(2f, -2f, 1f), new Vector3(-2f, -2f, 1f),
            new Vector3(0f, 0f, -1f));

        // Templates: player spawn, enemy NPC, and door
        var npcTmpl = new JklTemplate { Name = "test_npc", BasedOn = "none" };
        npcTmpl.Parameters["aiclass"] = "test.ai";
        npcTmpl.Parameters["maxvel"] = "0.6";
        npcTmpl.Parameters["size"] = "0.065";
        level.Templates["test_npc"] = npcTmpl;

        var doorTmpl = new JklTemplate { Name = "test_door", BasedOn = "none" };
        doorTmpl.Parameters["size"] = "0.25";
        level.Templates["test_door"] = doorTmpl;

        level.Things.Add(new JklThing
        {
            ThingIndex = 0,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, -1.0f, 0.13f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "test_npc",
            SectorIndex = 0,
            Position = new Vector3(0f, 1.0f, 0.13f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 2,
            TemplateName = "test_door",
            SectorIndex = 0,
            Position = new Vector3(0f, 0f, 0.2f),
            ExtraArguments = "numframes=2 frame=(0/0/0.2:0/0/0) frame=(0/0/1.2:0/0/0)"
        });

        using var vfs = new VirtualFileSystem();
        var sim = new WorldSimulation(level, vfs);

        // Closed door blocks LOS between NPC (y=1) and player (y=-1)
        Assert.True(sim.IsDoorClosed(2));
        Assert.False(sim.HasLineOfSight(new Vector3(0f, -0.8f, 0.25f), new Vector3(0f, 0.8f, 0.25f)));

        // Step simulation: NPC must stay at hip height (~0.13 above floor Z=0)
        for (int i = 0; i < 60; i++)
        {
            sim.Update(1.0f / 60.0f, new Vector3(0f, -1.0f, 0.13f), 0);
        }

        Assert.True(level.Things[1].Position.Z >= 0.08f, $"NPC sank to Z={level.Things[1].Position.Z}");

        // Open door to frame 1 -> LOS becomes clear
        sim.MoveToFrame(2, 1, 20.0f);
        for (int i = 0; i < 60 && sim.IsThingMoving(2); i++)
        {
            sim.Update(1.0f / 60.0f, new Vector3(0f, -1.0f, 0.13f), 0);
        }

        Assert.False(sim.IsDoorClosed(2));
        Assert.True(sim.HasLineOfSight(new Vector3(0f, -0.8f, 0.25f), new Vector3(0f, 0.8f, 0.25f)));
    }

    [Fact]
    public void LiveSim_BlasterProjectile_CollidesWithCantinaWall()
    {
        using var vfs = TryMountRetailVfs();
        if (vfs == null)
            return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs);

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);
        Assert.Equal(339, spawn.SectorIndex);

        // Player eye height in Sector 339 booth (floor is Z=-1.25, standing eye Z=-1.07)
        Vector3 eyePos = new Vector3(spawn.Position.X, spawn.Position.Y, -1.07f);
        float yawRad = spawn.PitchYawRoll.Y * (MathF.PI / 180.0f);
        Vector3 forward = Vector3.Normalize(new Vector3(-MathF.Sin(yawRad), MathF.Cos(yawRad), 0f));

        // 1. Fire directly from Sector 339 spawn toward the nearby booth wall along spawn heading
        sim.FireWeaponRay(eyePos, forward, maxRange: 10.0f, damage: 25.0f, isMelee: false);
        Assert.Single(sim.Projectiles);

        const float dt = 1.0f / 60.0f;
        int ticks = 0;
        for (; ticks < 60 && sim.Projectiles.Count > 0; ticks++)
        {
            sim.Update(dt, spawn.Position, spawn.SectorIndex);
        }

        Assert.Empty(sim.Projectiles);
        Assert.InRange(ticks, 1, 10);
        _output.WriteLine($"Booth wall collision from spawn heading resolved in {ticks} ticks.");

        // 2. Fire point-blank (0.045 units) toward the south booth wall (Y=-5.55) where 0.08f offset used to tunnel
        Vector3 nearBoothWall = new Vector3(spawn.Position.X, -5.505f, -1.07f);
        sim.FireWeaponRay(nearBoothWall, -Vector3.UnitY, maxRange: 10.0f, damage: 25.0f, isMelee: false);
        Assert.Single(sim.Projectiles);

        int closeTicks = 0;
        for (; closeTicks < 60 && sim.Projectiles.Count > 0; closeTicks++)
        {
            sim.Update(dt, spawn.Position, spawn.SectorIndex);
        }

        Assert.Empty(sim.Projectiles);
        Assert.InRange(closeTicks, 1, 5);
        _output.WriteLine($"Point-blank south booth wall collision resolved in {closeTicks} ticks.");
    }

    [Fact]
    public void Synthetic_BlasterProjectile_BlockedByImpassablePortalAndShortStep()
    {
        var level = new JklLevel { Name = "synthetic_portal_wall" };
        var sec = new JklSector
        {
            SectorIndex = 0,
            BoundBoxMin = new Vector3(-2f, -2f, 0f),
            BoundBoxMax = new Vector3(2f, 2f, 1f)
        };
        level.Sectors.Add(sec);

        // Impassable window portal at Y = 0.5 (Flags = 0x5 -> bit 0x2 not set)
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 0, Flags = 0x5, Mirror = -1, Dist = 0f });

        AddQuad(level, sec,
            new Vector3(-1f, 0.5f, 0f), new Vector3(1f, 0.5f, 0f),
            new Vector3(1f, 0.5f, 1f), new Vector3(-1f, 0.5f, 1f),
            new Vector3(0f, -1f, 0f));
        level.Surfaces[0].AdjoinIndex = 0;

        level.Things.Add(new JklThing
        {
            ThingIndex = 0,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, 0f, 0.13f)
        });

        using var vfs = new VirtualFileSystem();
        var sim = new WorldSimulation(level, vfs);

        Vector3 origin = new Vector3(0f, 0.2f, 0.5f);
        sim.FireWeaponRay(origin, Vector3.UnitY, maxRange: 10.0f, damage: 25.0f, isMelee: false);
        Assert.Single(sim.Projectiles);

        // Verify tiny high-FPS dt steps (0.0001s) still detect the impassable window rather than tunneling
        for (int i = 0; i < 600 && sim.Projectiles.Count > 0; i++)
        {
            sim.Update(0.0001f, new Vector3(0f, 0f, 0.13f), 0);
        }

        Assert.Empty(sim.Projectiles);
    }

    [Fact]
    public void LiveSim_SaberNode5AndBryarViewmodelProjectCleanly()
    {
        using var vfs = TryMountRetailVfs();
        if (vfs == null)
            return;

        var sabv = ThreeDoReader.Parse(vfs.ReadAllText("3do/sabv.3do"));
        var sabvReady = KeyframeReader.Parse(vfs.ReadAllText("3do/key/sabvrdy.key"), "sabvrdy.key");
        var sabvSwing = KeyframeReader.Parse(vfs.ReadAllText("3do/key/sabvf1.key"), "sabvf1.key");
        var bryv = ThreeDoReader.Parse(vfs.ReadAllText("3do/bryv.3do"));
        var bryvReady = KeyframeReader.Parse(vfs.ReadAllText("3do/key/bryvpst1.key"), "bryvpst1.key");

        var camera = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        const float aspect = 16.0f / 9.0f;
        float fovRad = MathF.PI / 180.0f * ThingMeshRenderer.ViewmodelVerticalFovDegrees;
        Matrix4x4 view = camera.GetViewMatrix();
        Matrix4x4 vmProj = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspect, 0.005f, 10.0f);

        // 1. Verify sabv.3do Node 5 (K_Rhand) transforms cleanly across sabvf1.key swing
        var swingPositions = new List<Vector3>();
        var swingDirs = new List<Vector3>();
        for (int step = 0; step <= 6; step++)
        {
            float t = (step / 6.0f) * sabvSwing.Duration;
            Assert.True(ThingMeshRenderer.TryComputeNodeTransform(sabv, sabvSwing, t, nodeIndex: 5, out Matrix4x4 node5));

            Vector3 localPos = Vector3.Transform(Vector3.Zero, node5);
            Vector3 localDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, node5));
            Assert.True(float.IsFinite(localPos.X) && float.IsFinite(localPos.Y) && float.IsFinite(localPos.Z));
            Assert.InRange(localDir.Length(), 0.999f, 1.001f);

            swingPositions.Add(localPos);
            swingDirs.Add(localDir);
        }

        // Confirm swing actually animates Node 5 across the arc
        float maxPosDelta = swingPositions.Max(p => Vector3.Distance(p, swingPositions[0]));
        float minDot = swingDirs.Min(d => Vector3.Dot(d, swingDirs[0]));
        Assert.True(maxPosDelta > 0.03f, $"Expected sabvf1.key Node 5 position to move across swing, maxDelta={maxPosDelta:F4}");
        Assert.True(minDot < 0.5f, $"Expected sabvf1.key Node 5 +Y direction to rotate across swing, minDot={minDot:F4}");

        // 2. Verify sabv.3do (sabvrdy.key) hilt & blade project inside visible bounds [-0.8, 0.9]
        Assert.True(ThingMeshRenderer.TryComputeNodeTransform(sabv, sabvReady, 0f, nodeIndex: 5, out Matrix4x4 readyNode5));
        Matrix4x4 saberWorld = ThingMeshRenderer.BuildViewmodelWorldMatrix(camera, Program.GetViewmodelBaseOffset(isSaber: true));
        Matrix4x4 readyJoint5World = readyNode5 * saberWorld;
        Vector3 bladeBase = Vector3.Transform(Vector3.Zero, readyJoint5World);
        Vector3 bladeDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, readyJoint5World));
        Vector3 bladeTip = bladeBase + bladeDir * 0.115f;

        Assert.True(TryProjectToNdc(bladeBase, view, vmProj, out Vector2 baseNdc));
        Assert.True(TryProjectToNdc(bladeTip, view, vmProj, out Vector2 tipNdc));
        Assert.InRange(baseNdc.X, -0.8f, 0.9f);
        Assert.InRange(baseNdc.Y, -0.8f, 0.9f);
        Assert.InRange(tipNdc.X, -0.8f, 0.9f);
        Assert.InRange(tipNdc.Y, -0.8f, 0.9f);
        Assert.True(tipNdc.Y > baseNdc.Y, "Lightsaber blade tip should project above the hilt base.");

        // 3. Verify bryv.3do (bryvpst1.key) frames gun barrel in bottom-right [-1.0, -0.5] while hand stays below -1.0
        Vector3 bryarOffset = Program.GetViewmodelBaseOffset(isSaber: false);
        Matrix4x4 bryarWorld = ThingMeshRenderer.BuildViewmodelWorldMatrix(camera, bryarOffset);
        foreach (float sampleTime in new[] { 0.0f, 0.25f })
        {
            Matrix4x4[] bryvNodes = ThreeDoHierarchy.ComputeTransforms(bryv, bryvReady, sampleTime);
            int gunVisibleCount = 0;
            int handVisibleCount = 0;
            for (int n = 0; n < bryv.Nodes.Count; n++)
            {
                int meshIdx = bryv.Nodes[n].MeshIndex;
                if (meshIdx < 0 || meshIdx >= bryv.Meshes.Count)
                    continue;

                var mesh = bryv.Meshes[meshIdx];
                Matrix4x4 nodeWorld = bryvNodes[n] * bryarWorld;
                foreach (var face in mesh.Faces)
                {
                    string matName = (face.MaterialIndex >= 0 && face.MaterialIndex < bryv.Materials.Count)
                        ? bryv.Materials[face.MaterialIndex]
                        : string.Empty;
                    bool isHandMat = matName.StartsWith("hd", StringComparison.OrdinalIgnoreCase);
                    foreach (int vIdx in face.VertexIndices)
                    {
                        Vector3 worldPos = Vector3.Transform(mesh.Vertices[vIdx], nodeWorld);
                        if (!TryProjectToNdc(worldPos, view, vmProj, out Vector2 ndc))
                            continue;

                        if (ndc.X is >= 0.0f and <= 0.95f && ndc.Y is >= -1.0f and <= 0.0f)
                        {
                            if (isHandMat) handVisibleCount++;
                            else gunVisibleCount++;
                        }
                    }
                }
            }

            Assert.True(gunVisibleCount >= 20, $"Expected bryv.3do gun at t={sampleTime:F2}s to project in bottom-right, got {gunVisibleCount} face-verts.");
            if (sampleTime == 0.0f)
                Assert.Equal(0, handVisibleCount);
            _output.WriteLine($"bryv.3do @ t={sampleTime:F2}s: gunVerts={gunVisibleCount}, handVertsAboveBottom={handVisibleCount}.");
        }
    }

    [Fact]
    public void Synthetic_DoorOpensCrisplyWithMoveToFrame()
    {
        var level = new JklLevel { Name = "synthetic_door_speed" };
        level.Sectors.Add(new JklSector { SectorIndex = 0 });
        level.Things.Add(new JklThing { ThingIndex = 0, TemplateName = "walkplayer", SectorIndex = 0 });
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "test_door",
            SectorIndex = 0,
            Position = new Vector3(0f, 0f, 0f),
            ExtraArguments = "numframes=2 frame=(0/0/0:0/0/0) frame=(0/0/0.22:0/0/0)"
        });

        using var vfs = new VirtualFileSystem();
        var sim = new WorldSimulation(level, vfs);

        sim.MoveToFrame(1, 1, speed: 8.0f);
        const float dt = 1.0f / 60.0f;
        int ticks = 0;
        for (; ticks < 30 && sim.IsThingMoving(1); ticks++)
        {
            sim.Update(dt, Vector3.Zero, 0);
        }

        Assert.False(sim.IsThingMoving(1));
        Assert.Equal(1, sim.GetCurFrame(1));
        Assert.InRange(ticks * dt, 0.25f, 0.35f);
    }

    [Fact]
    public void Synthetic_ActivateNearestDoor_GazeRaycast_ActivatesCogLinkedSurface()
    {
        var level = new JklLevel { Name = "synthetic_gaze_activate" };
        // Sector 0: 2x2x1 box; surface 0 is front wall at y=0.5 with normal (0, -1, 0)
        level.Vertices.AddRange([
            new Vector3(-1f, 0.5f, 0f),
            new Vector3( 1f, 0.5f, 0f),
            new Vector3( 1f, 0.5f, 1f),
            new Vector3(-1f, 0.5f, 1f)
        ]);
        var switchSurf = new JklSurface
        {
            SurfaceIndex = 0,
            SurfaceFlags = 0x2, // SITH_SURFACE_COG_LINKED
            Normal = new Vector3(0f, -1f, 0f),
            VertexIndices = [0, 1, 2, 3]
        };
        level.Surfaces.Add(switchSurf);
        level.Sectors.Add(new JklSector
        {
            SectorIndex = 0,
            BoundBoxMin = new Vector3(-1f, -1f, 0f),
            BoundBoxMax = new Vector3(1f, 0.5f, 1f),
            Surfaces = [switchSurf]
        });

        level.Things.Add(new JklThing { ThingIndex = 0, TemplateName = "walkplayer", SectorIndex = 0 });
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "test_door",
            SectorIndex = 0,
            Position = new Vector3(0f, 0.8f, 0f),
            ExtraArguments = "numframes=2 frame=(0/0.8/0:0/0/0) frame=(0/0.8/0.5:0/0/0)"
        });

        const string doorSwitchCog = """
            symbols
            message activate
            surface switch linkid=1
            thing door0
            end
            code
            activate:
                if (GetSenderId() == 1)
                    MoveToFrame(door0, 1, 8.0);
                Return;
            end
            """;

        using var vfs = new TestMemoryVfs();
        vfs.Add("cog/test_switch.cog", doorSwitchCog);
        level.Cogs.Add(new JklCogPlacement
        {
            Index = 0,
            ScriptName = "test_switch.cog",
            SymbolArgs = ["0", "1"]
        });

        var sim = new WorldSimulation(level, vfs);

        // Too far away (y=0.25 -> dist to y=0.5 is 0.25 > 0.165 + 0.025)
        Assert.False(sim.ActivateNearestDoor(new Vector3(0f, 0.25f, 0f), Vector3.UnitY));
        Assert.False(sim.IsThingMoving(1));

        // Facing away
        Assert.False(sim.ActivateNearestDoor(new Vector3(0f, 0.36f, 0f), -Vector3.UnitY));
        Assert.False(sim.IsThingMoving(1));

        // Within gaze range (y=0.36 -> dist 0.14 <= 0.165) looking at +Y
        Assert.True(sim.ActivateNearestDoor(new Vector3(0f, 0.36f, 0f), Vector3.UnitY));
        Assert.True(sim.IsThingMoving(1));
    }

    [Fact]
    public void Synthetic_DifficultyFiltering_And_DestroyThing_UsesThingFlags()
    {
        var level = new JklLevel { Name = "synthetic_difficulty" };
        level.Sectors.Add(new JklSector { SectorIndex = 0 });

        var npcTmpl = new JklTemplate { Name = "test_npc", BasedOn = "none" };
        npcTmpl.Parameters["aiclass"] = "test.ai";
        level.Templates["test_npc"] = npcTmpl;

        level.Things.Add(new JklThing { ThingIndex = 0, TemplateName = "walkplayer", SectorIndex = 0 });
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "test_npc",
            Model3D = "st.3do",
            SectorIndex = 0,
            ThingFlags = (uint)SithThingFlags.NoEasy
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 2,
            TemplateName = "test_npc",
            Model3D = "st.3do",
            SectorIndex = 0,
            ThingFlags = (uint)SithThingFlags.Disabled
        });

        using var vfs = new VirtualFileSystem();

        // Easy (0): Thing 1 (NoEasy) and Thing 2 (Disabled) are both filtered out
        var easySim = new WorldSimulation(level, vfs, difficulty: 0);
        Assert.Equal(0, easySim.GetDifficulty());
        Assert.Equal(0, easySim.ActiveNpcCount);

        // Medium (1): Thing 1 spawns, Thing 2 (Disabled) does not
        var medSim = new WorldSimulation(level, vfs, difficulty: 1);
        Assert.Equal(1, medSim.GetDifficulty());
        Assert.Equal(1, medSim.ActiveNpcCount);

        // DestroyThing sets Disabled|Destroyed and IsNonSolid without blanking Model3D/TemplateName
        medSim.DestroyThing(1);
        Assert.True(level.Things[1].IsNonSolid);
        Assert.True((level.Things[1].ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0);
        Assert.Equal("st.3do", level.Things[1].Model3D);
        Assert.Equal("test_npc", level.Things[1].TemplateName);
    }

    private sealed class TestMemoryVfs : IVirtualFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, string content) =>
            _files[GobArchive.NormalizePath(path)] = System.Text.Encoding.ASCII.GetBytes(content);

        public bool FileExists(string path) => _files.ContainsKey(GobArchive.NormalizePath(path));

        public byte[] ReadAllBytes(string path) =>
            _files.TryGetValue(GobArchive.NormalizePath(path), out var data)
                ? data
                : throw new FileNotFoundException(path);

        public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

        public string ReadAllText(string path) => System.Text.Encoding.ASCII.GetString(ReadAllBytes(path));

        public void Dispose() { }
    }

    [Fact]
    public void LiveSim_KilledNpcCorpse_BecomesNonSolid_AndPlayerWalksThrough()
    {
        using var vfs = TryMountRetailVfs();
        if (vfs == null)
            return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs);

        // Find a reeyeesgun NPC that sits in a clear walkable corridor so Kyle can walk toward and past it
        JklThing? reeYees = null;
        Vector3 walkDir = Vector3.Zero;
        float floorZ = 0f;

        Vector3[] candidateDirs = [Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX];
        foreach (var thing in level.Things)
        {
            if (!thing.TemplateName.Equals("reeyeesgun", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryGetSectorFloorZ(level, thing.SectorIndex, thing.Position.X, thing.Position.Y, thing.Position.Z + 0.15f, out float npcFloor))
                continue;

            Vector3 npcTorso = new Vector3(thing.Position.X, thing.Position.Y, npcFloor + 0.10f);
            foreach (var dir in candidateDirs)
            {
                Vector3 before = npcTorso - dir * 0.24f;
                Vector3 after = npcTorso + dir * 0.20f;
                if (!sim.HasLineOfSight(before, after))
                    continue;

                if (TryGetSectorFloorZ(level, thing.SectorIndex, before.X, before.Y, npcFloor + 0.05f, out float beforeFloor) &&
                    TryGetSectorFloorZ(level, thing.SectorIndex, after.X, after.Y, npcFloor + 0.05f, out float afterFloor) &&
                    MathF.Abs(beforeFloor - npcFloor) < 0.03f &&
                    MathF.Abs(afterFloor - npcFloor) < 0.03f)
                {
                    reeYees = thing;
                    walkDir = dir;
                    floorZ = npcFloor;
                    break;
                }
            }

            if (reeYees != null)
                break;
        }

        Assert.NotNull(reeYees);
        Assert.False(reeYees.IsNonSolid);

        Vector2 walkDir2D = new Vector2(walkDir.X, walkDir.Y);
        Vector3 startPos = new Vector3(
            reeYees.Position.X - walkDir.X * 0.24f,
            reeYees.Position.Y - walkDir.Y * 0.24f,
            floorZ);

        var player = new PlayerPhysics(startPos, reeYees.SectorIndex);
        const float dt = 1.0f / 60.0f;

        // 1. Alive Ree-Yees blocks Kyle from walking through it
        for (int i = 0; i < 30; i++)
        {
            player.Update(dt, walkDir, wishJump: false, level);
        }

        float signedProgressAlive = Vector2.Dot(
            new Vector2(player.Position.X - reeYees.Position.X, player.Position.Y - reeYees.Position.Y),
            walkDir2D);
        Assert.True(signedProgressAlive < -0.10f,
            $"Alive Ree-Yees should block Kyle before reaching its center (signedProgress={signedProgressAlive:F4}).");

        // 2. Verify player bolt colors (Bryar = red, Repeater = gold) and kill Ree-Yees via FireWeaponRay
        Vector3 fireOrigin = player.EyePosition;
        Vector3 targetTorso = reeYees.Position + new Vector3(0f, 0f, 0.10f);
        Vector3 fireDir = Vector3.Normalize(targetTorso - fireOrigin);
        sim.FireWeaponRay(fireOrigin, fireDir, maxRange: 5.0f, damage: 200.0f, isMelee: false, projectileModel: "bry0.3do");
        var bryarBolt = Assert.Single(sim.Projectiles, p => !p.IsEnemy && p.ModelName == "bry0.3do");
        Assert.True(bryarBolt.BoltColor.X > 0.8f && bryarBolt.BoltColor.Y < 0.3f && bryarBolt.BoltColor.Z < 0.3f,
            $"Expected Bryar bolt to be red, got {bryarBolt.BoltColor}");

        for (int i = 0; i < 20 && !reeYees.IsNonSolid; i++)
        {
            sim.Update(dt, player.Position, player.CurrentSectorIndex);
        }

        Assert.True(reeYees.IsNonSolid, "Killed Ree-Yees corpse must become non-solid.");

        // 3. Kyle now walks cleanly through/over the corpse
        for (int i = 0; i < 45; i++)
        {
            player.Update(dt, walkDir, wishJump: false, level);
        }

        float signedProgressDead = Vector2.Dot(
            new Vector2(player.Position.X - reeYees.Position.X, player.Position.Y - reeYees.Position.Y),
            walkDir2D);
        Assert.True(signedProgressDead > 0.05f,
            $"Kyle should walk cleanly through dead Ree-Yees corpse (signedProgress={signedProgressDead:F4}).");

        // 4. Verify Repeater fires gold bolt (rpt0.3do), Rodian (greedo) fires green bolt, and alive Ree-Yees fires red bolt
        sim.FireWeaponRay(fireOrigin, -walkDir, maxRange: 5.0f, damage: 18.0f, isMelee: false, projectileModel: "rpt0.3do");
        var repBolt = sim.Projectiles.FirstOrDefault(p => !p.IsEnemy && p.ModelName == "rpt0.3do");
        Assert.NotNull(repBolt);
        Assert.True(repBolt.BoltColor.X > 0.8f && repBolt.BoltColor.Y > 0.6f && repBolt.BoltColor.Z < 0.3f,
            $"Expected Repeater bolt to be gold, got {repBolt.BoltColor}");

        var greedo = level.Things.FirstOrDefault(t => t.TemplateName.Equals("greedo", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(greedo);
        Assert.False(greedo.IsNonSolid);

        // Place Kyle directly in front of Greedo inside Greedo's sector so Greedo fires at Kyle
        Vector3 greedoToPlayer = new Vector3(0.45f, 0f, 0f);
        if (!sim.HasLineOfSight(greedo.Position + new Vector3(0f, 0f, 0.10f), greedo.Position + greedoToPlayer + new Vector3(0f, 0f, 0.10f)))
            greedoToPlayer = new Vector3(-0.45f, 0f, 0f);
        if (!sim.HasLineOfSight(greedo.Position + new Vector3(0f, 0f, 0.10f), greedo.Position + greedoToPlayer + new Vector3(0f, 0f, 0.10f)))
            greedoToPlayer = new Vector3(0f, 0.45f, 0f);

        Vector3 kyleNearGreedo = greedo.Position + greedoToPlayer;
        greedo.PitchYawRoll = new Vector3(
            0f,
            MathF.Atan2(-greedoToPlayer.X, greedoToPlayer.Y) * (180f / MathF.PI),
            0f);

        WorldSimulation.Projectile? greedoBolt = null;
        for (int i = 0; i < 180 && greedoBolt == null; i++)
        {
            sim.Update(dt, kyleNearGreedo, greedo.SectorIndex);
            greedoBolt = sim.Projectiles.FirstOrDefault(p => p.OwnerThingIndex == greedo.ThingIndex);
        }

        Assert.NotNull(greedoBolt);
        Assert.Equal("bry1.3do", greedoBolt.ModelName);
        Assert.True(greedoBolt.BoltColor.Y > 0.8f && greedoBolt.BoltColor.X < 0.3f && greedoBolt.BoltColor.Z < 0.4f,
            $"Expected Rodian (greedo) bolt to be green, got {greedoBolt.BoltColor}");

        var secondReeYees = level.Things.FirstOrDefault(t =>
            t.TemplateName.Equals("reeyeesgun", StringComparison.OrdinalIgnoreCase) &&
            t.ThingIndex != reeYees.ThingIndex);
        Assert.NotNull(secondReeYees);

        Vector3 ryToPlayer = new Vector3(0.45f, 0f, 0f);
        if (!sim.HasLineOfSight(secondReeYees.Position + new Vector3(0f, 0f, 0.10f), secondReeYees.Position + ryToPlayer + new Vector3(0f, 0f, 0.10f)))
            ryToPlayer = new Vector3(0f, 0.45f, 0f);
        if (!sim.HasLineOfSight(secondReeYees.Position + new Vector3(0f, 0f, 0.10f), secondReeYees.Position + ryToPlayer + new Vector3(0f, 0f, 0.10f)))
            ryToPlayer = new Vector3(0f, -0.45f, 0f);

        Vector3 kyleNearRy = secondReeYees.Position + ryToPlayer;
        secondReeYees.PitchYawRoll = new Vector3(
            0f,
            MathF.Atan2(-ryToPlayer.X, ryToPlayer.Y) * (180f / MathF.PI),
            0f);

        WorldSimulation.Projectile? ryBolt = null;
        for (int i = 0; i < 180 && ryBolt == null; i++)
        {
            sim.Update(dt, kyleNearRy, secondReeYees.SectorIndex);
            ryBolt = sim.Projectiles.FirstOrDefault(p => p.OwnerThingIndex == secondReeYees.ThingIndex);
        }

        Assert.NotNull(ryBolt);
        Assert.Equal("str0.3do", ryBolt.ModelName);
        Assert.True(ryBolt.BoltColor.X > 0.8f && ryBolt.BoltColor.Y < 0.3f && ryBolt.BoltColor.Z < 0.3f,
            $"Expected Ree-Yees bolt to be red, got {ryBolt.BoltColor}");

        // 5. Verify taking/destroying a pickup item sets IsNonSolid = true
        var pickup = level.Things.FirstOrDefault(t => t.TemplateName.Equals("greedopistol", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(pickup);
        Assert.False(pickup.IsNonSolid);
        sim.TakeItem(pickup.ThingIndex, level.GetPlayerSpawn()?.ThingIndex ?? 0);
        Assert.True(pickup.IsNonSolid);
    }

    [Fact]
    public void Synthetic_PuppetSubmodeFlagsAndVelocityDrivenDelta()
    {
        const string pupText = """
            Mode=0
            stand kywait0.key 0x00 0 0
            walk kywalkF.key 0x01 1 1
            run kyrun0.key 0x01 2 2
            death kydie0.key 0x14 3 3

            Mode=1, basedon=0
            stand kystand1.key 0x28 0 0
            fire kyfire1.key 0x00 2 2
            """;

        var pup = PuppetReader.Parse(pupText, "synthetic.pup");

        Assert.True(pup.GetSubmode(0, "run", out string? runKey, out int runFlags));
        Assert.Equal("kyrun0.key", runKey);
        Assert.True((runFlags & 0x01) != 0);

        Assert.True(pup.GetSubmode(1, "run", out string? mode1RunKey, out int mode1RunFlags));
        Assert.Equal("kyrun0.key", mode1RunKey);
        Assert.True((mode1RunFlags & 0x01) != 0);

        Assert.True(pup.GetSubmode(1, "stand", out string? mode1StandKey, out int mode1StandFlags));
        Assert.Equal("kystand1.key", mode1StandKey);
        Assert.Equal(0x28, mode1StandFlags);
        Assert.True((mode1StandFlags & 0x01) == 0);

        Assert.True(pup.GetSubmode(0, "death", out _, out int deathFlags));
        Assert.Equal(0x14, deathFlags);

        // Velocity-driven delta: 2.0 units/s at 60Hz, 15fps -> 12.8s track completes in ~0.343s
        float stepAnimDt = ThingMeshRenderer.ComputeVelocityDrivenTimeDelta(2.0f * (1.0f / 60.0f), 15.0f);
        Assert.InRange(stepAnimDt, 0.62f, 0.63f);

        float animTime = 0f;
        int ticks = 0;
        while (animTime < 12.8f && ticks < 200)
        {
            animTime += stepAnimDt;
            ticks++;
        }

        float wallTime = ticks * (1.0f / 60.0f);
        Assert.InRange(wallTime, 0.30f, 0.38f);
    }

    [Fact]
    public void LiveSim_KylePuppetVelocityFlagsAndFullbrightProjectiles()
    {
        using var vfs = TryMountRetailVfs();
        if (vfs == null)
            return;

        // 1. Verify ky.pup parses run/walk with 0x01 velocity flag and stand without 0x01
        var kyPup = PuppetReader.Parse(vfs.ReadAllText("misc/pup/ky.pup"), "ky.pup");

        Assert.True(kyPup.GetSubmode(0, "run", out string? runKey, out int runFlags));
        Assert.Equal("kyrun0.key", runKey, ignoreCase: true);
        Assert.True((runFlags & 0x01) != 0, $"Expected ky.pup run flags to have 0x01 set, got 0x{runFlags:X}");

        Assert.True(kyPup.GetSubmode(0, "walk", out string? walkKey, out int walkFlags));
        Assert.Equal("kywalkF.key", walkKey, ignoreCase: true);
        Assert.True((walkFlags & 0x01) != 0, $"Expected ky.pup walk flags to have 0x01 set, got 0x{walkFlags:X}");

        Assert.True(kyPup.GetSubmode(0, "stand", out _, out int standFlags));
        Assert.True((standFlags & 0x01) == 0, $"Expected ky.pup stand flags not to have 0x01 set, got 0x{standFlags:X}");

        // 2. Verify kyrun0.key advances 1 full cycle (12.8s) in ~0.34s at 2.0 JK units/s
        var kyRunKey = KeyframeReader.Parse(vfs.ReadAllText("3do/key/kyrun0.key"), "kyrun0.key");
        Assert.InRange(kyRunKey.Duration, 12.7f, 12.9f);
        Assert.Equal(15.0f, kyRunKey.Fps);

        const float dt = 1.0f / 60.0f;
        float animDeltaPerTick = ThingMeshRenderer.ComputeVelocityDrivenTimeDelta(2.0f * dt, kyRunKey.Fps);
        float animTime = 0f;
        int ticks = 0;
        while (animTime < kyRunKey.Duration && ticks < 200)
        {
            animTime += animDeltaPerTick;
            ticks++;
        }

        float cycleWallSeconds = ticks * dt;
        _output.WriteLine($"kyrun0.key (Duration={kyRunKey.Duration:F2}s, Fps={kyRunKey.Fps:F1}) completed 1 cycle in {cycleWallSeconds:F3}s ({ticks} ticks).");
        Assert.InRange(cycleWallSeconds, 0.30f, 0.38f);

        // 3. Verify retail projectile 3DOs load from Res2.gob and emit fullbright (1.0f) intensity
        //    (bry0, bry1, str0, and rpt0 (+repeaterball) all have LightingMode == 0 in retail)
        (string ModelName, int ExpectedLightingMode)[] projectileModels =
        [
            ("bry0.3do", 0),
            ("bry1.3do", 0),
            ("str0.3do", 0),
            ("rpt0.3do", 0)
        ];
        foreach (var (modelName, expectedLightingMode) in projectileModels)
        {
            string path = $"3do/{modelName}";
            Assert.True(vfs.FileExists(path), $"Expected retail projectile model '{path}' in VFS.");
            var model = ThreeDoReader.Parse(vfs.ReadAllText(path));
            Assert.NotEmpty(model.Meshes);
            foreach (var mesh in model.Meshes)
            {
                Assert.Equal(expectedLightingMode, mesh.LightingMode);
                Assert.Equal(1.0f, ThingMeshRenderer.ResolveVertexIntensity(mesh, 0, forceFullbright: mesh.LightingMode != 0));
            }
        }
    }

    private static bool TryProjectToNdc(Vector3 worldPos, Matrix4x4 view, Matrix4x4 projection, out Vector2 ndc)
    {
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1.0f), view * projection);
        if (clip.W <= 0.005f)
        {
            ndc = Vector2.Zero;
            return false;
        }

        ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        return true;
    }

    private static void AddQuad(JklLevel level, JklSector sec, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 normal)
    {
        int start = level.Vertices.Count;
        level.Vertices.Add(v0);
        level.Vertices.Add(v1);
        level.Vertices.Add(v2);
        level.Vertices.Add(v3);

        var surf = new JklSurface
        {
            SurfaceIndex = level.Surfaces.Count,
            VertexIndices = [start, start + 1, start + 2, start + 3],
            Normal = normal,
            AdjoinIndex = -1
        };
        level.Surfaces.Add(surf);
        sec.Surfaces.Add(surf);
    }
}
