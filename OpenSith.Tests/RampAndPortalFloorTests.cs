using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Models;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class RampAndPortalFloorTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    private static JklLevel? TryLoadNarShaddaa()
    {
        if (!File.Exists(SteamJk1Path)) return null;
        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        return JklReader.Parse(text, "01narshadda");
    }

    private static JklSurface AddQuadSurface(
        JklLevel level,
        JklSector sector,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
        Vector3 normal,
        int adjoinIndex = -1)
    {
        int baseIdx = level.Vertices.Count;
        level.Vertices.Add(v0);
        level.Vertices.Add(v1);
        level.Vertices.Add(v2);
        level.Vertices.Add(v3);

        var surface = new JklSurface
        {
            SurfaceIndex = level.Surfaces.Count,
            AdjoinIndex = adjoinIndex,
            Normal = Vector3.Normalize(normal),
            VertexIndices = [baseIdx, baseIdx + 1, baseIdx + 2, baseIdx + 3]
        };

        level.Surfaces.Add(surface);
        sector.Surfaces.Add(surface);
        return surface;
    }

    private static void AddBoxWalls(JklLevel level, JklSector sector, float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        AddQuadSurface(level, sector, new(minX, minY, minZ), new(maxX, minY, minZ), new(maxX, minY, maxZ), new(minX, minY, maxZ), new(0, 1, 0));
        AddQuadSurface(level, sector, new(maxX, maxY, minZ), new(minX, maxY, minZ), new(minX, maxY, maxZ), new(maxX, maxY, maxZ), new(0, -1, 0));
        AddQuadSurface(level, sector, new(minX, maxY, minZ), new(minX, minY, minZ), new(minX, minY, maxZ), new(minX, maxY, maxZ), new(1, 0, 0));
        AddQuadSurface(level, sector, new(maxX, minY, minZ), new(maxX, maxY, minZ), new(maxX, maxY, maxZ), new(maxX, minY, maxZ), new(-1, 0, 0));
    }

    [Fact]
    public void PassablePortalFloor_DoesNotCreateInvisibleFloor()
    {
        var level = new JklLevel { Name = "portal_floor_test" };
        var upper = new JklSector { SectorIndex = 0 };
        var lower = new JklSector { SectorIndex = 1 };
        level.Sectors.Add(upper);
        level.Sectors.Add(lower);

        // Adjoin 0: passable (Flags = 0x7 = visible + passable + block-light-off)
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 0, Flags = 0x7, Mirror = 1 });
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 1, Flags = 0x7, Mirror = 0 });

        // Upper sector [0.5, 1.5], passable sloped portal at bottom (Z=0.5..0.6)
        AddBoxWalls(level, upper, -1f, 1f, -1f, 1f, 0.5f, 1.5f);
        AddQuadSurface(level, upper, new(-1f, 1f, 1.5f), new(1f, 1f, 1.5f), new(1f, -1f, 1.5f), new(-1f, -1f, 1.5f), new(0, 0, -1));
        var portalDown = AddQuadSurface(level, upper,
            new(-1f, -1f, 0.5f), new(1f, -1f, 0.5f), new(1f, 1f, 0.6f), new(-1f, 1f, 0.6f),
            new(0, -0.0499f, 0.9988f), adjoinIndex: 0);

        // Lower sector [0.0, 0.6], solid floor at Z=0.0
        AddBoxWalls(level, lower, -1f, 1f, -1f, 1f, 0.0f, 0.6f);
        var portalUp = AddQuadSurface(level, lower,
            new(-1f, 1f, 0.6f), new(1f, 1f, 0.6f), new(1f, -1f, 0.5f), new(-1f, -1f, 0.5f),
            new(0, 0.0499f, -0.9988f), adjoinIndex: 1);
        AddQuadSurface(level, lower, new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(-1f, 1f, 0f), new(0, 0, 1));

        portalDown.MirrorSector = lower;
        portalDown.MirrorSurface = portalUp;
        portalUp.MirrorSector = upper;
        portalUp.MirrorSurface = portalDown;

        var player = new PlayerPhysics(new Vector3(0f, 0f, 0.9f), startSector: 0);
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 60; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
        }

        Assert.True(player.IsGrounded);
        Assert.Equal(1, player.CurrentSectorIndex);
        Assert.Equal(0.0f, player.Position.Z, precision: 3);
    }

    [Fact]
    public void NonPassablePortalFloor_BlocksFallAsSolidFloor()
    {
        var level = new JklLevel { Name = "solid_portal_floor_test" };
        var upper = new JklSector { SectorIndex = 0 };
        var lower = new JklSector { SectorIndex = 1 };
        level.Sectors.Add(upper);
        level.Sectors.Add(lower);

        // Adjoin without 0x2 (passable) flag -> solid invisible/grate floor
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 0, Flags = 0x1, Mirror = 1 });
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 1, Flags = 0x1, Mirror = 0 });

        AddBoxWalls(level, upper, -1f, 1f, -1f, 1f, 0.5f, 1.5f);
        AddQuadSurface(level, upper, new(-1f, 1f, 1.5f), new(1f, 1f, 1.5f), new(1f, -1f, 1.5f), new(-1f, -1f, 1.5f), new(0, 0, -1));
        var portalDown = AddQuadSurface(level, upper,
            new(-1f, -1f, 0.5f), new(1f, -1f, 0.5f), new(1f, 1f, 0.5f), new(-1f, 1f, 0.5f),
            new(0, 0, 1), adjoinIndex: 0);

        AddBoxWalls(level, lower, -1f, 1f, -1f, 1f, 0.0f, 0.5f);
        var portalUp = AddQuadSurface(level, lower,
            new(-1f, 1f, 0.5f), new(1f, 1f, 0.5f), new(1f, -1f, 0.5f), new(-1f, -1f, 0.5f),
            new(0, 0, -1), adjoinIndex: 1);
        AddQuadSurface(level, lower, new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(-1f, 1f, 0f), new(0, 0, 1));

        portalDown.MirrorSector = lower;
        portalDown.MirrorSurface = portalUp;
        portalUp.MirrorSector = upper;
        portalUp.MirrorSurface = portalDown;

        var player = new PlayerPhysics(new Vector3(0f, 0f, 0.8f), startSector: 0);
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 40; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
        }

        Assert.True(player.IsGrounded);
        Assert.Equal(0, player.CurrentSectorIndex);
        Assert.Equal(0.5f, player.Position.Z, precision: 3);
    }

    [Fact]
    public void FastMovementUpSteepRamp_RemainsGroundedAndDoesNotClip()
    {
        var level = new JklLevel { Name = "steep_ramp_test" };
        var sec = new JklSector { SectorIndex = 0 };
        level.Sectors.Add(sec);

        // 45-degree ramp: Y in [-1, 1], Z rises from 0.0 at Y=0 to 1.0 at Y=1
        AddQuadSurface(level, sec, new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 0f, 0f), new(-1f, 0f, 0f), new(0, 0, 1));
        AddQuadSurface(level, sec, new(-1f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 1f), new(-1f, 1f, 1f), new(0, -1f, 1f));
        AddQuadSurface(level, sec, new(-1f, 1f, 2f), new(1f, 1f, 2f), new(1f, -1f, 2f), new(-1f, -1f, 2f), new(0, 0, -1));
        AddBoxWalls(level, sec, -1f, 1f, -1f, 1f, 0f, 2f);

        var player = new PlayerPhysics(new Vector3(0f, -0.5f, 0f), startSector: 0);
        // Coarse 20 FPS timestep where vertical climb per tick (~0.10m) exceeds MaxStepHeight (0.06m)
        float dt = 1.0f / 20.0f;
        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsGrounded);

        for (int i = 0; i < 12; i++)
        {
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level);
            Assert.True(player.IsGrounded, $"Lost grounding on steep ramp at step {i}, pos={player.Position}");
            float expectedFloor = player.Position.Y > 0f ? MathF.Min(player.Position.Y, 1.0f) : 0f;
            Assert.True(player.Position.Z >= expectedFloor - 0.01f,
                $"Clipped below steep ramp at step {i}: Z={player.Position.Z}, expected>={expectedFloor}");
        }

        Assert.True(player.Position.Z > 0.35f, $"Expected player to climb up ramp, Z={player.Position.Z}");
    }

    [Fact]
    public void Retail01NarShaddaa_WalkFromSpawnIntoSector349_NoInvisibleFloorOrPhantomBump()
    {
        var level = TryLoadNarShaddaa();
        if (level == null) return;

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        var player = new PlayerPhysics(spawn!.Position, spawn.SectorIndex);
        float dt = 1.0f / 60.0f;

        for (int i = 0; i < 20; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsGrounded);
        Assert.Equal(-1.25f, player.Position.Z, precision: 3);

        bool reachedSec349Floor = false;
        for (int step = 0; step < 30; step++)
        {
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level);
            // Must never bump above Sector 339's -1.25m floor before reaching the far curb of Sector 349
            if (player.Position.Y < -4.75f)
            {
                Assert.True(player.Position.Z <= -1.249f,
                    $"Phantom upward bump at step {step}: pos={player.Position}");
            }
            if (player.CurrentSectorIndex == 349 && MathF.Abs(player.Position.Z - (-1.30f)) < 0.005f)
            {
                reachedSec349Floor = true;
            }
        }

        Assert.True(reachedSec349Floor, $"Player did not cleanly step onto Sector 349 floor (-1.30m), final pos={player.Position}");
    }

    [Fact]
    public void Retail01NarShaddaa_WalkUpAndDownRamp_Sec19ToSec22_SmoothAndGrounded()
    {
        var level = TryLoadNarShaddaa();
        if (level == null) return;

        // Bottom of the Sector 19 -> 20 -> 22 ramp
        var player = new PlayerPhysics(new Vector3(-8.0f, -5.45f, -1.30f), startSector: 19);
        float dt = 1.0f / 60.0f;

        for (int i = 0; i < 20; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsGrounded);
        Assert.Equal(-1.30f, player.Position.Z, precision: 3);

        // Walk up the ramp from Sector 19 (-1.30) -> Sector 20 (-1.15) -> Sector 22 (-0.95)
        float prevZ = player.Position.Z;
        bool visitedSec20 = false;
        for (int step = 0; step < 65; step++)
        {
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level);
            Assert.True(player.IsGrounded, $"Lost grounding walking up ramp at step {step}, pos={player.Position}");
            Assert.True(player.Position.Z >= prevZ - 0.0005f,
                $"Unexpected downward dip while walking up ramp at step {step}: prevZ={prevZ}, Z={player.Position.Z}");
            Assert.True(player.Position.Z <= -0.9495f,
                $"Phantom overshoot above top of ramp (-0.95) at step {step}: pos={player.Position}");

            if (player.CurrentSectorIndex == 20) visitedSec20 = true;
            prevZ = player.Position.Z;
        }

        Assert.True(visitedSec20, "Player should traverse Sector 20 along the ramp");
        Assert.Equal(22, player.CurrentSectorIndex);
        Assert.Equal(-0.95f, player.Position.Z, precision: 3);

        // Walk back down the ramp from Sector 22 (-0.95) -> Sector 20 (-1.15) -> Sector 19 (-1.30)
        prevZ = player.Position.Z;
        for (int step = 0; step < 65; step++)
        {
            player.Update(dt, new Vector3(0f, -1f, 0f), wishJump: false, level);
            Assert.True(player.IsGrounded, $"Lost grounding walking down ramp at step {step}, pos={player.Position}");
            Assert.True(player.Position.Z <= prevZ + 0.0005f,
                $"Unexpected upward bump while walking down ramp at step {step}: prevZ={prevZ}, Z={player.Position.Z}");
            Assert.True(player.Position.Z >= -1.3005f,
                $"Clipped below bottom floor (-1.30) at step {step}: pos={player.Position}");

            prevZ = player.Position.Z;
        }

        Assert.Equal(19, player.CurrentSectorIndex);
        Assert.Equal(-1.30f, player.Position.Z, precision: 3);
    }

    [Fact]
    public void Retail01NarShaddaa_200mClearance_Sec21RampTopAndSec77Portal_NotBlockedByCeiling()
    {
        var level = TryLoadNarShaddaa();
        if (level == null) return;

        float dt = 1.0f / 60.0f;

        // 1. Sec 21 ramp: floor rises from -0.950 (Y=-3.90) to -0.700 (Y=-4.40) under a -0.500 ceiling (0.200m clearance).
        var rampPlayer = new PlayerPhysics(new Vector3(-8.50f, -3.75f, -0.95f), startSector: 21);
        for (int i = 0; i < 10; i++)
            rampPlayer.Update(dt, Vector3.Zero, wishJump: false, level);

        Assert.True(rampPlayer.IsGrounded);
        Assert.Equal(-0.95f, rampPlayer.Position.Z, precision: 3);

        for (int step = 0; step < 45; step++)
        {
            rampPlayer.Update(dt, new Vector3(0f, -1f, 0f), wishJump: false, level);
            Assert.True(rampPlayer.IsGrounded, $"Lost grounding on Sec 21 ramp at step {step}, pos={rampPlayer.Position}");
        }

        // Player radius (0.065) stops center at Y ~= -4.335 against the Y=-4.400 wall,
        // where the sloped floor is Z ~= -0.7325 and ceiling is -0.500.
        // With old Height=0.22f, ceiling clamp would block the player at Z <= -0.720 (Y >= -4.36) and/or push them down.
        Assert.Equal(21, rampPlayer.CurrentSectorIndex);
        Assert.True(rampPlayer.Position.Y <= -4.33f,
            $"Expected player to reach Sec 21 ramp top near Y=-4.335, got Y={rampPlayer.Position.Y}");
        Assert.True(rampPlayer.Position.Z >= -0.74f,
            $"Expected player to climb Sec 21 ramp to Z>=-0.74, got Z={rampPlayer.Position.Z}");

        // 2. Sec 22 -> Sec 77 portal: Sec 77 has floor -0.850 and ceiling -0.650 (0.200m clearance).
        // Start in Sec 22 at Z=-0.850 and walk +X toward Sec 77 (X in [-7.900, -7.875], Y in [-4.200, -4.100]).
        var portalPlayer = new PlayerPhysics(new Vector3(-7.96f, -4.15f, -0.85f), startSector: 22);
        for (int step = 0; step < 15; step++)
        {
            portalPlayer.Update(dt, new Vector3(1f, 0f, 0f), wishJump: false, level);
            // Must not be pushed down by Sec 77's -0.650 ceiling while inside the 0.200m clearance zone
            if (portalPlayer.Position.X >= -7.95f)
            {
                Assert.True(portalPlayer.Position.Z >= -0.86f || portalPlayer.Velocity.Z <= 0f,
                    $"Unexpected ceiling push at step {step}: pos={portalPlayer.Position}");
            }
        }

        // Player should advance right up to Sec 77's back wall (X = -7.875 - 0.065 = -7.940) without ceiling rejection.
        Assert.True(portalPlayer.Position.X >= -7.945f,
            $"Expected player to enter Sec 22 -> Sec 77 clearance zone (X >= -7.945), got X={portalPlayer.Position.X}");
    }

    [Fact]
    public void Retail01NarShaddaa_Sec51_4x4Elev_GroundsFeetAndAllowsEnteringSec55Duct()
    {
        var level = TryLoadNarShaddaa();
        if (level == null || !File.Exists(SteamRes2Path)) return;

        using var res2 = GobArchive.FromFile(SteamRes2Path);
        var modelCache = new Dictionary<string, ThreeDoModel?>(StringComparer.OrdinalIgnoreCase);
        ThreeDoModel? ResolveFromRes2(string modelName)
        {
            if (modelCache.TryGetValue(modelName, out var cached))
                return cached;

            string[] paths = [$"3do/{modelName}", modelName];
            foreach (string path in paths)
            {
                if (res2.Contains(path))
                {
                    var parsed = ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(path)));
                    modelCache[modelName] = parsed;
                    return parsed;
                }
            }
            modelCache[modelName] = null;
            return null;
        }

        var elev = level.Things[2];
        Assert.Contains("elev", elev.TemplateName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(51, elev.SectorIndex);

        var player = new PlayerPhysics(new Vector3(elev.Position.X, elev.Position.Y, -0.58f), startSector: 51)
        {
            ModelResolver = ResolveFromRes2
        };

        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 30; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsGrounded, $"Expected Kyle to be grounded on e4x4.3do, pos={player.Position}");
        Assert.InRange(player.Position.Z, -0.615f, -0.609f);

        bool enteredSec55 = false;
        for (int step = 0; step < 60; step++)
        {
            player.Update(dt, new Vector3(1f, 0f, 0f), wishJump: false, level);
            if (player.CurrentSectorIndex == 55)
            {
                enteredSec55 = true;
                break;
            }
        }

        Assert.True(enteredSec55, $"Expected Kyle to step from 4x4elev through Surf 521 into Sec 55, final sector={player.CurrentSectorIndex}, pos={player.Position}");
        Assert.True(player.IsGrounded, $"Expected Kyle to remain grounded in Sec 55, pos={player.Position}");
        Assert.Equal(-0.600f, player.Position.Z, precision: 3);
    }

    [Fact]
    public void Retail01NarShaddaa_Sec50_SteepChuteSurf510_CannotBeClimbed()
    {
        var level = TryLoadNarShaddaa();
        if (level == null) return;

        var sec50 = level.Sectors[50];
        var surf510 = level.Surfaces[510];
        Assert.Contains(surf510, sec50.Surfaces);
        Assert.Equal(0xCu, surf510.SurfaceFlags & 0xCu);
        Assert.True((surf510.SurfaceFlags & 0x1u) == 0, "Surf 510 should not have SITH_SURFACE_FLOOR (0x1)");
        Assert.True(surf510.Normal.Z < 0.6f, $"Surf 510 should be steeper than 53.13 deg (normal.Z={surf510.Normal.Z})");

        // Surf 510 slopes upward toward -Y (Sec 56) from Sec 28 at +Y.
        // Spawn Kyle on Sec 28's floor just outside Surf 509 (the portal into Sec 50) and walk -Y toward Surf 510.
        float avgX = 0f, maxY = float.MinValue, minZ = float.MaxValue;
        foreach (int idx in surf510.VertexIndices)
        {
            Vector3 v = level.Vertices[idx];
            avgX += v.X;
            if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z;
        }
        avgX /= surf510.VertexIndices.Length;

        Vector3 startPos = new Vector3(avgX, maxY + 0.15f, minZ + 0.01f);
        int startSec = PlayerPhysics.FindSectorAtPosition(startPos, 28, level);
        Assert.Equal(28, startSec);

        var player = new PlayerPhysics(startPos, startSector: startSec);
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 60 && !player.IsGrounded; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsGrounded, $"Expected Kyle to be grounded in Sec 28, pos={player.Position}");
        float groundedZ = player.Position.Z;

        // Walk -Y directly into Sec 50's steep non-floor Surf 510; must block Kyle instead of letting him climb into Sec 56
        for (int step = 0; step < 60; step++)
        {
            player.Update(dt, new Vector3(0f, -1f, 0f), wishJump: false, level);
            Assert.NotEqual(56, player.CurrentSectorIndex);
            Assert.True(player.Position.Z <= groundedZ + 0.02f,
                $"Kyle climbed steep non-floor Surf 510 at step {step}: Z={player.Position.Z} vs groundedZ={groundedZ}");
        }
    }
}
