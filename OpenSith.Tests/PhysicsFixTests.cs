using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class PhysicsFixTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    [Fact]
    public void CeilingCollision_PreventsJumpingThroughRoof()
    {
        // Room from [-1, 1] x [-1, 1] x [0, 0.30] (player Height = 0.22, so headroom is 0.08)
        var level = CreateBoxLevel(minX: -1f, maxX: 1f, minY: -1f, maxY: 1f, floorZ: 0.0f, ceilingZ: 0.30f);
        var player = new PlayerPhysics(new Vector3(0f, 0f, 0.0f), startSector: 0);

        float dt = 1.0f / 60.0f;
        // Ground the player first
        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsGrounded);

        // Jump and step several ticks upward
        player.Update(dt, Vector3.Zero, wishJump: true, level);
        float maxHeadZ = player.Position.Z + player.Height;

        for (int i = 0; i < 30; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
            float headZ = player.Position.Z + player.Height;
            if (headZ > maxHeadZ) maxHeadZ = headZ;

            // Player must never penetrate the ceiling at Z = 0.30
            Assert.True(headZ <= 0.3001f, $"Player head Z ({headZ}) penetrated ceiling (0.30)!");
        }

        Assert.True(maxHeadZ <= 0.3001f);
        Assert.Equal(0, player.CurrentSectorIndex);
    }

    [Fact]
    public void CeilingCollision_PortalCeilingAllowsJumpingIntoUpperSector()
    {
        var level = new JklLevel { Name = "synthetic_shaft" };
        var lower = new JklSector { SectorIndex = 0 };
        var upper = new JklSector { SectorIndex = 1 };
        level.Sectors.Add(lower);
        level.Sectors.Add(upper);

        // Lower sector [0, 0.30], open top at Z = 0.30
        AddQuadSurface(level, lower,
            new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f),
            new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f),
            new Vector3(0, 0, 1));
        var portalUp = AddQuadSurface(level, lower,
            new Vector3(-1f, 1f, 0.30f), new Vector3(1f, 1f, 0.30f),
            new Vector3(1f, -1f, 0.30f), new Vector3(-1f, -1f, 0.30f),
            new Vector3(0, 0, -1), isPortal: true);

        // Upper sector [0.30, 1.00], open bottom at Z = 0.30
        var portalDown = AddQuadSurface(level, upper,
            new Vector3(-1f, -1f, 0.30f), new Vector3(1f, -1f, 0.30f),
            new Vector3(1f, 1f, 0.30f), new Vector3(-1f, 1f, 0.30f),
            new Vector3(0, 0, 1), isPortal: true);
        AddQuadSurface(level, upper,
            new Vector3(-1f, 1f, 1.0f), new Vector3(1f, 1f, 1.0f),
            new Vector3(1f, -1f, 1.0f), new Vector3(-1f, -1f, 1.0f),
            new Vector3(0, 0, -1));

        portalUp.MirrorSurface = portalDown;
        portalUp.MirrorSector = upper;
        portalDown.MirrorSurface = portalUp;
        portalDown.MirrorSector = lower;

        var player = new PlayerPhysics(new Vector3(0f, 0f, 0f), startSector: 0);
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        player.Update(dt, Vector3.Zero, wishJump: true, level);
        float peakHeadZ = 0f;
        for (int i = 0; i < 20; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
            peakHeadZ = MathF.Max(peakHeadZ, player.Position.Z + player.Height);
        }

        // Player's head should pass freely above the portal plane at Z = 0.30
        Assert.True(peakHeadZ > 0.35f, $"Portal ceiling should not block jump, peakHeadZ={peakHeadZ}");
    }

    [Fact]
    public void CeilingCollision_RetailCantinaBoothPreventsNoclip()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        var player = new PlayerPhysics(spawn.Position, spawn.SectorIndex);
        float dt = 1.0f / 60.0f;

        for (int i = 0; i < 20; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsGrounded);

        // Sector 339 booth ceiling is at Z = -0.95
        player.Update(dt, Vector3.Zero, wishJump: true, level);
        for (int i = 0; i < 30; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
            Assert.True(player.Position.Z + player.Height <= -0.949f,
                $"Player clipped through booth ceiling: head={player.Position.Z + player.Height}");
        }
    }

    [Fact]
    public void StepUpHeight_WalksOntoLowLedge_AndBlockedByTallWall()
    {
        // 0.04f ledge is <= default MaxStepHeight (0.06f) -> player walks up without jumping
        var stepLevel = CreateTwoSectorStepLevel(stepHeight: 0.04f);
        var stepPlayer = new PlayerPhysics(new Vector3(0f, -0.5f, 0.0f), startSector: 0);

        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 5; i++)
            stepPlayer.Update(dt, Vector3.Zero, wishJump: false, stepLevel);

        Assert.True(stepPlayer.IsGrounded);

        for (int i = 0; i < 60; i++)
            stepPlayer.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, stepLevel);

        Assert.True(stepPlayer.Position.Y > 0.3f, $"Expected player to step over 0.04f ledge, but Y={stepPlayer.Position.Y}");
        Assert.Equal(0.04f, stepPlayer.Position.Z, precision: 3);
        Assert.Equal(1, stepPlayer.CurrentSectorIndex);

        // 0.15f wall is > default MaxStepHeight (0.06f) -> player is blocked horizontally
        var wallLevel = CreateTwoSectorStepLevel(stepHeight: 0.15f);
        var wallPlayer = new PlayerPhysics(new Vector3(0f, -0.5f, 0.0f), startSector: 0);

        for (int i = 0; i < 5; i++)
            wallPlayer.Update(dt, Vector3.Zero, wishJump: false, wallLevel);

        for (int i = 0; i < 60; i++)
            wallPlayer.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, wallLevel);

        Assert.True(wallPlayer.Position.Y <= 0.001f - wallPlayer.Radius + 0.005f,
            $"Expected 0.15f wall at Y=0 to block player, but Y={wallPlayer.Position.Y}");
        Assert.Equal(0.0f, wallPlayer.Position.Z, precision: 3);
        Assert.Equal(0, wallPlayer.CurrentSectorIndex);
    }

    [Fact]
    public void SolidDoorThing_BlocksWhenClosed_AllowsPassageWhenMovedAside()
    {
        var level = CreateBoxLevel(minX: -1f, maxX: 1f, minY: -1f, maxY: 1f, floorZ: 0.0f, ceilingZ: 1.0f);
        level.Templates["_decor"] = new JklTemplate
        {
            Name = "_decor",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "cog",
                ["collide"] = "1"
            }
        };
        level.Templates["_structure"] = new JklTemplate
        {
            Name = "_structure",
            BasedOn = "_decor",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["collide"] = "3"
            }
        };
        level.Templates["2x3door"] = new JklTemplate
        {
            Name = "2x3door",
            BasedOn = "_structure",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["size"] = "0.180710"
            }
        };

        var door = new JklThing
        {
            ThingIndex = 1,
            TemplateName = "2x3door",
            Name = "2x3door",
            Position = new Vector3(0f, 0.0f, 0.15f),
            SectorIndex = 0
        };
        level.Things.Add(door);

        var player = new PlayerPhysics(new Vector3(0f, -0.6f, 0.0f), startSector: 0);
        float dt = 1.0f / 60.0f;

        // Ground player
        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        // Walk directly into closed door at Y = 0
        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level);

        Assert.True(player.Position.Y <= -0.109f && player.Position.Y >= -0.13f,
            $"Closed door should block player right at door face (~ -0.11), but Y={player.Position.Y}");

        // Move door aside horizontally -> player walks right through Y = 0
        door.Position = new Vector3(0.8f, 0.0f, 0.15f);
        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level);

        Assert.True(player.Position.Y > 0.3f,
            $"Player should pass through after door moved aside, but Y={player.Position.Y}");
    }

    [Fact]
    public void SolidDoorThing_OrientedBox_BlocksAtCenterAndOffCenter_WhileStayingThin()
    {
        var level = CreateBoxLevel(minX: -1f, maxX: 1f, minY: -1f, maxY: 1f, floorZ: 0.0f, ceilingZ: 1.0f);
        level.Templates["2x3door"] = new JklTemplate
        {
            Name = "2x3door",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "cog",
                ["collide"] = "3",
                ["size"] = "0.180710",
                ["movesize"] = "0.055"
            }
        };

        var door = new JklThing
        {
            ThingIndex = 1,
            TemplateName = "2x3door",
            Name = "2x3door",
            Position = new Vector3(0f, 0.0f, 0.15f),
            PitchYawRoll = new Vector3(0f, 0.0f, 0.0f),
            SectorIndex = 0
        };
        level.Things.Add(door);

        float dt = 1.0f / 60.0f;

        // Yaw = 0: doorway spans along X (right = (1, 0)), door face normal along Y (forward = (0, 1))
        foreach (float lateralX in new[] { 0.0f, 0.10f, -0.10f })
        {
            var player = new PlayerPhysics(new Vector3(lateralX, -0.5f, 0.0f), startSector: 0);
            for (int i = 0; i < 5; i++)
                player.Update(dt, Vector3.Zero, wishJump: false, level);

            for (int i = 0; i < 60; i++)
                player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level);

            float distToDoorCenter = door.Position.Y - player.Position.Y;
            Assert.True(distToDoorCenter >= 0.105f && distToDoorCenter <= 0.121f,
                $"Door (yaw=0) at lateralX={lateralX} should block within ~0.12f of door face, dist={distToDoorCenter}, pos={player.Position}");
            Assert.True(MathF.Abs(player.Position.X - lateralX) < 0.005f,
                $"Player at lateralX={lateralX} should not slide sideways past OBB door, X={player.Position.X}");
        }

        // Yaw = 90: doorway spans along Y (right = (0, 1)), door face normal along -X (forward = (-1, 0))
        door.PitchYawRoll = new Vector3(0f, 90.0f, 0.0f);
        foreach (float lateralY in new[] { 0.0f, 0.10f, -0.10f })
        {
            var player = new PlayerPhysics(new Vector3(-0.5f, lateralY, 0.0f), startSector: 0);
            for (int i = 0; i < 5; i++)
                player.Update(dt, Vector3.Zero, wishJump: false, level);

            for (int i = 0; i < 60; i++)
                player.Update(dt, new Vector3(1f, 0f, 0f), wishJump: false, level);

            float distToDoorCenter = door.Position.X - player.Position.X;
            Assert.True(distToDoorCenter >= 0.105f && distToDoorCenter <= 0.121f,
                $"Door (yaw=90) at lateralY={lateralY} should block within ~0.12f of door face, dist={distToDoorCenter}, pos={player.Position}");
            Assert.True(MathF.Abs(player.Position.Y - lateralY) < 0.005f,
                $"Player at lateralY={lateralY} should not slide sideways past OBB door, Y={player.Position.Y}");
        }
    }

    [Fact]
    public void SolidThingColliderList_BlocksWhenClosed_AllowsPassageWhenOpenedUpward()
    {
        var level = CreateBoxLevel(minX: -1f, maxX: 1f, minY: -1f, maxY: 1f, floorZ: 0.0f, ceilingZ: 1.0f);
        var player = new PlayerPhysics(new Vector3(0f, -0.6f, 0.0f), startSector: 0);
        float dt = 1.0f / 60.0f;

        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        var closedDoor = new List<SolidThingCollider>
        {
            new(new Vector3(0f, 0.0f, 0.0f), Radius: 0.18f, Height: 0.30f, BottomOffset: 0.0f)
        };

        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level, closedDoor);

        Assert.True(player.Position.Y < -0.20f,
            $"Closed SolidThingCollider should block player, but Y={player.Position.Y}");

        // Door lifted above player head height (Bottom at Z = 0.25 > player.Height = 0.22)
        var openDoor = new List<SolidThingCollider>
        {
            new(new Vector3(0f, 0.0f, 0.25f), Radius: 0.18f, Height: 0.30f, BottomOffset: 0.0f)
        };

        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level, openDoor);

        Assert.True(player.Position.Y > 0.3f,
            $"Lifted door should let player walk underneath, but Y={player.Position.Y}");
    }

    [Fact]
    public void SolidDoorThing_RetailNarShaddaaDoorBlocksAndOpens()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        // Thing 4 is a 2x3door (Yaw=90, doorway runs along X through Sector 74) at (1.200002, 9.650073, 6.35)
        var door = level.Things[4];
        Assert.Contains("door", door.TemplateName, StringComparison.OrdinalIgnoreCase);

        float floorZ = 6.20f;
        var startPos = new Vector3(door.Position.X - 0.35f, door.Position.Y, floorZ);
        int startSector = PlayerPhysics.FindSectorAtPosition(startPos, door.SectorIndex, level);
        var player = new PlayerPhysics(startPos, startSector);
        float dt = 1.0f / 60.0f;

        for (int i = 0; i < 30; i++)
            player.Update(dt, new Vector3(1f, 0f, 0f), wishJump: false, level);

        Assert.True(player.IsGrounded, $"Player should remain grounded at floorZ=6.20, pos={player.Position}");
        Assert.True(player.Position.X <= door.Position.X - 0.119f && player.Position.X >= door.Position.X - 0.13f,
            $"Closed retail door should block player right at door face (~ doorX - 0.12), but pos={player.Position} sector={player.CurrentSectorIndex} vs door={door.Position}");

        // Simulate door opening upward (Frame 1 in 01narshadda.jkl is Z = 6.60)
        door.Position = new Vector3(door.Position.X, door.Position.Y, 6.60f);
        for (int i = 0; i < 30; i++)
            player.Update(dt, new Vector3(1f, 0f, 0f), wishJump: false, level);

        Assert.True(player.Position.X > door.Position.X + 0.05f,
            $"Opened retail door should allow player to pass, but pos={player.Position} sector={player.CurrentSectorIndex} vs door={door.Position}");
    }

    [Fact]
    public void NarShaddaaSpawn_WithLargeFirstFrameDt_LandsOnCantinaFloor()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        var player = new PlayerPhysics(spawn.Position, spawn.SectorIndex);
        player.Update(0.5f, Vector3.Zero, wishJump: false, level);
        for (int i = 0; i < 10; i++)
            player.Update(1.0f / 60.0f, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsGrounded, $"Kyle should be grounded in Sector 339, Z={player.Position.Z}");
        Assert.Equal(339, player.CurrentSectorIndex);
        Assert.True(player.Position.Z > -1.4f && player.Position.Z < -1.1f,
            $"Kyle Z={player.Position.Z} should rest on Cantina floor (~ -1.25), not fall into void");
    }

    [Fact]
    public void PortalCollision_ImpassableWindowBlocksPlayer_PassablePortalAllowsPassage()
    {
        var level = new JklLevel { Name = "synthetic_window_portal" };
        var sec0 = new JklSector { SectorIndex = 0 };
        var sec1 = new JklSector { SectorIndex = 1 };
        level.Sectors.Add(sec0);
        level.Sectors.Add(sec1);

        // Sector 0: Y in [-1, 0]; Sector 1: Y in [0, 1]
        AddBoxSector(level, sec0, -1f, 1f, -1f, 0f, 0.0f, 1.0f, skipNorthWall: true, skipSouthWall: false);
        AddBoxSector(level, sec1, -1f, 1f, 0f, 1f, 0.0f, 1.0f, skipNorthWall: false, skipSouthWall: true);

        // Window portal at Y = 0 spanning full height [0, 1]
        var portal0 = AddQuadSurface(level, sec0,
            new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f),
            new Vector3(1f, 0f, 1f), new Vector3(-1f, 0f, 1f),
            new Vector3(0f, -1f, 0f), isPortal: true);
        var portal1 = AddQuadSurface(level, sec1,
            new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f),
            new Vector3(-1f, 0f, 1f), new Vector3(1f, 0f, 1f),
            new Vector3(0f, 1f, 0f), isPortal: true);

        portal0.AdjoinIndex = 0;
        portal0.MirrorSurface = portal1;
        portal0.MirrorSector = sec1;
        portal1.AdjoinIndex = 1;
        portal1.MirrorSurface = portal0;
        portal1.MirrorSector = sec0;

        // Flags = 0x1 (visible, impassable: SITH_ADJOIN_MOVE 0x2 is NOT set)
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 0, Flags = 0x1, Mirror = 1 });
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 1, Flags = 0x1, Mirror = 0 });

        Assert.False(PlayerPhysics.IsPassablePortal(portal0, level));
        Assert.False(PlayerPhysics.IsPassablePortal(portal1, level));

        var player = new PlayerPhysics(new Vector3(0f, -0.5f, 0f), startSector: 0);
        float dt = 1.0f / 60.0f;

        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        // Walk directly into impassable window portal at Y = 0
        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level);

        Assert.True(player.Position.Y <= 0.001f - player.Radius + 0.005f,
            $"Impassable portal (Flags=0x1) should block player at Y=0, but Y={player.Position.Y}");
        Assert.Equal(0, player.CurrentSectorIndex);

        // Enable PASSABLE bit (0x2 -> Flags = 0x3) -> player walks right through into Sector 1
        level.Adjoins[0].Flags = 0x3;
        level.Adjoins[1].Flags = 0x3;

        Assert.True(PlayerPhysics.IsPassablePortal(portal0, level));
        Assert.True(PlayerPhysics.IsPassablePortal(portal1, level));

        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0f, 1f, 0f), wishJump: false, level);

        Assert.True(player.Position.Y > 0.3f,
            $"Passable portal (Flags=0x3) should let player walk through, but Y={player.Position.Y}");
        Assert.Equal(1, player.CurrentSectorIndex);
    }

    [Fact]
    public void PortalCollision_ImpassableCeilingBlocksJump_PassableCeilingAllowsJump()
    {
        var level = new JklLevel { Name = "synthetic_forcefield_ceiling" };
        var lower = new JklSector { SectorIndex = 0 };
        var upper = new JklSector { SectorIndex = 1 };
        level.Sectors.Add(lower);
        level.Sectors.Add(upper);

        AddQuadSurface(level, lower,
            new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f),
            new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f),
            new Vector3(0, 0, 1));
        var portalUp = AddQuadSurface(level, lower,
            new Vector3(-1f, 1f, 0.30f), new Vector3(1f, 1f, 0.30f),
            new Vector3(1f, -1f, 0.30f), new Vector3(-1f, -1f, 0.30f),
            new Vector3(0, 0, -1), isPortal: true);
        var portalDown = AddQuadSurface(level, upper,
            new Vector3(-1f, -1f, 0.30f), new Vector3(1f, -1f, 0.30f),
            new Vector3(1f, 1f, 0.30f), new Vector3(-1f, 1f, 0.30f),
            new Vector3(0, 0, 1), isPortal: true);
        AddQuadSurface(level, upper,
            new Vector3(-1f, 1f, 1.0f), new Vector3(1f, 1f, 1.0f),
            new Vector3(1f, -1f, 1.0f), new Vector3(-1f, -1f, 1.0f),
            new Vector3(0, 0, -1));

        portalUp.AdjoinIndex = 0;
        portalUp.MirrorSurface = portalDown;
        portalUp.MirrorSector = upper;
        portalDown.AdjoinIndex = 1;
        portalDown.MirrorSurface = portalUp;
        portalDown.MirrorSector = lower;

        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 0, Flags = 0x1, Mirror = 1 });
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 1, Flags = 0x1, Mirror = 0 });

        var player = new PlayerPhysics(new Vector3(0f, 0f, 0f), startSector: 0);
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        player.Update(dt, Vector3.Zero, wishJump: true, level);
        for (int i = 0; i < 20; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
            Assert.True(player.Position.Z + player.Height <= 0.3001f,
                $"Impassable ceiling portal (Flags=0x1) should block jump, headZ={player.Position.Z + player.Height}");
        }

        // Now mark portal passable (Flags=0x3) -> player jumps cleanly into upper sector
        level.Adjoins[0].Flags = 0x3;
        level.Adjoins[1].Flags = 0x3;

        for (int i = 0; i < 20; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        player.Update(dt, Vector3.Zero, wishJump: true, level);
        float peakHeadZ = 0f;
        for (int i = 0; i < 20; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
            peakHeadZ = MathF.Max(peakHeadZ, player.Position.Z + player.Height);
        }

        Assert.True(peakHeadZ > 0.35f,
            $"Passable ceiling portal (Flags=0x3) should allow jumping above 0.30, peakHeadZ={peakHeadZ}");
    }

    [Fact]
    public void PortalCollision_RetailNarShaddaaCantinaWindowBlocksPlayer()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        // Cantina window glass surface 3539 in Sector 362 (Flags=0x5, missing PASSABLE bit 0x2)
        var windowSurface = level.Surfaces[3539];
        Assert.True(windowSurface.IsPortal);
        Assert.False(PlayerPhysics.IsPassablePortal(windowSurface, level));

        // Start inside Cantina window booth Sector 357 and walk -Y toward window Sector 362 / exterior Sector 351
        var player = new PlayerPhysics(new Vector3(-8.35f, -5.94f, -1.2f), startSector: 357);
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0f, -1f, 0f), wishJump: false, level);

        Assert.NotEqual(351, player.CurrentSectorIndex);
        Assert.True(player.Position.Y >= -6.02f,
            $"Cantina glass window should block Kyle before exterior Sector 351, pos={player.Position} sec={player.CurrentSectorIndex}");
    }

    private static JklLevel CreateBoxLevel(float minX, float maxX, float minY, float maxY, float floorZ, float ceilingZ)
    {
        var level = new JklLevel { Name = "synthetic_box" };
        var sector = new JklSector { SectorIndex = 0 };
        level.Sectors.Add(sector);

        AddBoxSector(level, sector, minX, maxX, minY, maxY, floorZ, ceilingZ, skipNorthWall: false, skipSouthWall: false);
        return level;
    }

    private static JklLevel CreateTwoSectorStepLevel(float stepHeight)
    {
        var level = new JklLevel { Name = "synthetic_step" };
        var sec0 = new JklSector { SectorIndex = 0 };
        var sec1 = new JklSector { SectorIndex = 1 };
        level.Sectors.Add(sec0);
        level.Sectors.Add(sec1);

        // Sector 0: X in [-1, 1], Y in [-1, 0], Z in [0, 1]
        AddBoxSector(level, sec0, -1f, 1f, -1f, 0f, 0.0f, 1.0f, skipNorthWall: true, skipSouthWall: false);
        // Step riser wall at Y = 0 from Z = 0 to Z = stepHeight (normal pointing -Y into sec0)
        AddQuadSurface(level, sec0,
            new Vector3(-1f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 0f, stepHeight),
            new Vector3(-1f, 0f, stepHeight),
            new Vector3(0f, -1f, 0f),
            isPortal: false);

        // Sector 1: X in [-1, 1], Y in [0, 1], Z in [stepHeight, 1]
        AddBoxSector(level, sec1, -1f, 1f, 0f, 1f, stepHeight, 1.0f, skipNorthWall: false, skipSouthWall: true);

        // Portal at Y = 0 from Z = stepHeight to Z = 1.0 connecting sec0 <-> sec1
        var portal0 = AddQuadSurface(level, sec0,
            new Vector3(-1f, 0f, stepHeight),
            new Vector3(1f, 0f, stepHeight),
            new Vector3(1f, 0f, 1.0f),
            new Vector3(-1f, 0f, 1.0f),
            new Vector3(0f, -1f, 0f),
            isPortal: true);
        var portal1 = AddQuadSurface(level, sec1,
            new Vector3(1f, 0f, stepHeight),
            new Vector3(-1f, 0f, stepHeight),
            new Vector3(-1f, 0f, 1.0f),
            new Vector3(1f, 0f, 1.0f),
            new Vector3(0f, 1f, 0f),
            isPortal: true);

        portal0.MirrorSurface = portal1;
        portal0.MirrorSector = sec1;
        portal1.MirrorSurface = portal0;
        portal1.MirrorSector = sec0;

        return level;
    }

    private static void AddBoxSector(
        JklLevel level,
        JklSector sector,
        float minX,
        float maxX,
        float minY,
        float maxY,
        float floorZ,
        float ceilingZ,
        bool skipNorthWall,
        bool skipSouthWall)
    {
        // Floor (+Z normal)
        AddQuadSurface(level, sector,
            new Vector3(minX, minY, floorZ),
            new Vector3(maxX, minY, floorZ),
            new Vector3(maxX, maxY, floorZ),
            new Vector3(minX, maxY, floorZ),
            new Vector3(0, 0, 1));

        // Ceiling (-Z normal)
        AddQuadSurface(level, sector,
            new Vector3(minX, maxY, ceilingZ),
            new Vector3(maxX, maxY, ceilingZ),
            new Vector3(maxX, minY, ceilingZ),
            new Vector3(minX, minY, ceilingZ),
            new Vector3(0, 0, -1));

        // West wall (+X normal)
        AddQuadSurface(level, sector,
            new Vector3(minX, minY, floorZ),
            new Vector3(minX, maxY, floorZ),
            new Vector3(minX, maxY, ceilingZ),
            new Vector3(minX, minY, ceilingZ),
            new Vector3(1, 0, 0));

        // East wall (-X normal)
        AddQuadSurface(level, sector,
            new Vector3(maxX, maxY, floorZ),
            new Vector3(maxX, minY, floorZ),
            new Vector3(maxX, minY, ceilingZ),
            new Vector3(maxX, maxY, ceilingZ),
            new Vector3(-1, 0, 0));

        if (!skipSouthWall)
        {
            // South wall (+Y normal)
            AddQuadSurface(level, sector,
                new Vector3(maxX, minY, floorZ),
                new Vector3(minX, minY, floorZ),
                new Vector3(minX, minY, ceilingZ),
                new Vector3(maxX, minY, ceilingZ),
                new Vector3(0, 1, 0));
        }

        if (!skipNorthWall)
        {
            // North wall (-Y normal)
            AddQuadSurface(level, sector,
                new Vector3(minX, maxY, floorZ),
                new Vector3(maxX, maxY, floorZ),
                new Vector3(maxX, maxY, ceilingZ),
                new Vector3(minX, maxY, ceilingZ),
                new Vector3(0, -1, 0));
        }
    }

    private static JklSurface AddQuadSurface(
        JklLevel level,
        JklSector sector,
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        Vector3 v3,
        Vector3 normal,
        bool isPortal = false)
    {
        int baseIdx = level.Vertices.Count;
        level.Vertices.Add(v0);
        level.Vertices.Add(v1);
        level.Vertices.Add(v2);
        level.Vertices.Add(v3);

        var surface = new JklSurface
        {
            SurfaceIndex = level.Surfaces.Count,
            MaterialIndex = isPortal ? -1 : 0,
            GeometryMode = isPortal ? 0 : 4,
            AdjoinIndex = isPortal ? 0 : -1,
            VertexIndices = [baseIdx, baseIdx + 1, baseIdx + 2, baseIdx + 3],
            TextureIndices = [-1, -1, -1, -1],
            VertexIntensities = [1f, 1f, 1f, 1f],
            Normal = normal
        };

        level.Surfaces.Add(surface);
        sector.Surfaces.Add(surface);
        return surface;
    }
}
