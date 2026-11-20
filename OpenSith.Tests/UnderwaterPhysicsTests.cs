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

public class UnderwaterPhysicsTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";
    private const int SectorFlagUnderwater = 0x2;

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

    private static void AddBoxWalls(
        JklLevel level,
        JklSector sector,
        float minX, float maxX,
        float minY, float maxY,
        float minZ, float maxZ)
    {
        AddQuadSurface(level, sector, new(minX, minY, minZ), new(maxX, minY, minZ), new(maxX, minY, maxZ), new(minX, minY, maxZ), new(0, 1, 0));
        AddQuadSurface(level, sector, new(maxX, maxY, minZ), new(minX, maxY, minZ), new(minX, maxY, maxZ), new(maxX, maxY, maxZ), new(0, -1, 0));
        AddQuadSurface(level, sector, new(minX, maxY, minZ), new(minX, minY, minZ), new(minX, minY, maxZ), new(minX, maxY, maxZ), new(1, 0, 0));
        AddQuadSurface(level, sector, new(maxX, minY, minZ), new(maxX, maxY, minZ), new(maxX, maxY, maxZ), new(maxX, minY, maxZ), new(-1, 0, 0));
    }

    private static JklLevel CreateClosedUnderwaterBox(
        float minX = -2f, float maxX = 2f,
        float minY = -2f, float maxY = 2f,
        float minZ = 0f, float maxZ = 2f,
        int sectorFlags = SectorFlagUnderwater)
    {
        var level = new JklLevel { Name = "underwater_box" };
        var sector = new JklSector { SectorIndex = 0, Flags = sectorFlags };
        level.Sectors.Add(sector);

        AddBoxWalls(level, sector, minX, maxX, minY, maxY, minZ, maxZ);
        // Floor
        AddQuadSurface(level, sector, new(minX, minY, minZ), new(maxX, minY, minZ), new(maxX, maxY, minZ), new(minX, maxY, minZ), new(0, 0, 1));
        // Ceiling
        AddQuadSurface(level, sector, new(minX, maxY, maxZ), new(maxX, maxY, maxZ), new(maxX, minY, maxZ), new(minX, minY, maxZ), new(0, 0, -1));

        return level;
    }

    [Fact]
    public void UnderwaterSector_SetsIsUnderwater_AndFloatsInsteadOfSinking()
    {
        var level = CreateClosedUnderwaterBox(minZ: 0.0f, maxZ: 2.0f, sectorFlags: SectorFlagUnderwater);
        var player = new PlayerPhysics(new Vector3(0f, 0f, 1.0f), startSector: 0);

        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 60; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
        }

        Assert.True(player.IsUnderwater);
        Assert.False(player.IsGrounded);
        // Gentle underwater buoyancy/drag prevents sinking to the floor (would hit Z=0 in <0.8s in air)
        Assert.True(player.Position.Z > 0.90f, $"Expected player to float near Z=1.0, got Z={player.Position.Z}");
    }

    [Fact]
    public void UnderwaterSector_SwimUpDownAndSpeedScaled()
    {
        var level = CreateClosedUnderwaterBox(minX: -5f, maxX: 5f, minY: -5f, maxY: 5f, minZ: 0.0f, maxZ: 3.0f);
        var player = new PlayerPhysics(new Vector3(0f, 0f, 1.0f), startSector: 0);

        float dt = 1.0f / 60.0f;

        // Swim upward with wishJump = true
        for (int i = 0; i < 30; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: true, level);
        }
        Assert.True(player.IsUnderwater);
        Assert.True(player.Position.Z > 1.2f, $"Expected player to swim upward, got Z={player.Position.Z}");

        // Swim downward with negative wishDirection.Z into floor without clipping
        for (int i = 0; i < 150; i++)
        {
            player.Update(dt, new Vector3(0f, 0f, -1f), wishJump: false, level);
        }
        Assert.True(player.Position.Z >= 0.0f, $"Player clipped below floor: Z={player.Position.Z}");
        Assert.True(player.Position.Z < 0.05f, $"Expected player to swim down to floor, got Z={player.Position.Z}");

        // Horizontal swim speed should be scaled by ~0.6x MoveSpeed (1.2 m/s instead of 2.0 m/s)
        player.Position = new Vector3(0f, 0f, 1.0f);
        player.Velocity = Vector3.Zero;
        for (int i = 0; i < 60; i++)
        {
            player.Update(dt, new Vector3(1f, 0f, 0f), wishJump: false, level);
        }
        float expectedSwimSpeed = player.MoveSpeed * 0.6f;
        Assert.InRange(player.Velocity.X, expectedSwimSpeed * 0.4f, expectedSwimSpeed + 0.05f);
        Assert.True(player.Velocity.X < player.MoveSpeed * 0.85f, $"Expected 0.6x swim speed cap, got Vx={player.Velocity.X}");
    }

    [Fact]
    public void UnderwaterSector_DoesNotSnapToFloorWithinStepTolerance()
    {
        var level = CreateClosedUnderwaterBox(minZ: 0.0f, maxZ: 2.0f);
        var player = new PlayerPhysics(new Vector3(0f, 0f, 0.02f), startSector: 0);

        player.Update(1.0f / 60.0f, Vector3.Zero, wishJump: false, level);

        Assert.True(player.IsUnderwater);
        Assert.False(player.IsGrounded);
        Assert.True(player.Position.Z > 0.015f, $"Player snapped to floor underwater: Z={player.Position.Z}");
    }

    [Fact]
    public void UnderwaterToAirTransition_RestoresAirGravityAndGrounding()
    {
        var level = new JklLevel { Name = "water_to_air_test" };
        var water = new JklSector { SectorIndex = 0, Flags = SectorFlagUnderwater };
        var air = new JklSector { SectorIndex = 1, Flags = 0x0 };
        level.Sectors.Add(water);
        level.Sectors.Add(air);

        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 0, Flags = 0x7, Mirror = 1 });
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 1, Flags = 0x7, Mirror = 0 });

        // Water sector [0.0, 1.0]
        AddBoxWalls(level, water, -2f, 2f, -2f, 2f, 0.0f, 1.0f);
        AddQuadSurface(level, water, new(-2f, -2f, 0.0f), new(2f, -2f, 0.0f), new(2f, 2f, 0.0f), new(-2f, 2f, 0.0f), new(0, 0, 1));
        var waterTop = AddQuadSurface(level, water, new(-2f, 2f, 1.0f), new(2f, 2f, 1.0f), new(2f, -2f, 1.0f), new(-2f, -2f, 1.0f), new(0, 0, -1), adjoinIndex: 0);

        // Air sector [1.0, 3.0]
        AddBoxWalls(level, air, -2f, 2f, -2f, 2f, 1.0f, 3.0f);
        var airBottom = AddQuadSurface(level, air, new(-2f, -2f, 1.0f), new(2f, -2f, 1.0f), new(2f, 2f, 1.0f), new(-2f, 2f, 1.0f), new(0, 0, 1), adjoinIndex: 1);
        AddQuadSurface(level, air, new(-2f, 2f, 3.0f), new(2f, 2f, 3.0f), new(2f, -2f, 3.0f), new(-2f, -2f, 3.0f), new(0, 0, -1));

        waterTop.MirrorSector = air;
        waterTop.MirrorSurface = airBottom;
        airBottom.MirrorSector = water;
        airBottom.MirrorSurface = waterTop;

        var player = new PlayerPhysics(new Vector3(0f, 0f, 0.6f), startSector: 0);
        float dt = 1.0f / 60.0f;

        // Swim upward out of the water surface
        for (int i = 0; i < 45; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: true, level);
            if (!player.IsUnderwater)
                break;
        }

        Assert.Equal(1, player.CurrentSectorIndex);
        Assert.False(player.IsUnderwater);
        Assert.True(player.Position.Z > 1.0f);

        // Release jump in air sector: normal air gravity (3.5) should pull player back down into water
        float vzBefore = player.Velocity.Z;
        player.Update(dt, Vector3.Zero, wishJump: false, level);
        Assert.True(player.Velocity.Z < vzBefore - (player.Gravity * dt * 0.9f),
            $"Expected normal air gravity to apply in air sector: before={vzBefore}, after={player.Velocity.Z}");

        for (int i = 0; i < 60; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: false, level);
            if (player.IsUnderwater)
                break;
        }
        Assert.True(player.IsUnderwater, "Expected falling back into water sector to re-enable IsUnderwater");
    }

    [Fact]
    public void UnderwaterSector_WallCeilingAndThreeDoCollisionsRemainActive()
    {
        var level = CreateClosedUnderwaterBox(minX: -1f, maxX: 1f, minY: -1f, maxY: 1f, minZ: 0f, maxZ: 1.5f);

        // Add a solid Collide==3 3DO obstacle inside the water
        level.Templates["underwater_pillar"] = new JklTemplate
        {
            Name = "underwater_pillar",
            BasedOn = "none",
            Model3D = "pillar.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "cog",
                ["collide"] = "3"
            }
        };
        level.Things.Add(new JklThing
        {
            ThingIndex = 0,
            TemplateName = "underwater_pillar",
            Name = "underwater_pillar",
            Model3D = "pillar.3do",
            Position = new Vector3(0.5f, 0f, 0.5f),
            SectorIndex = 0
        });

        var pillarModel = new ThreeDoModel
        {
            Meshes =
            [
                new ThreeDoMesh
                {
                    Name = "box",
                    Vertices =
                    [
                        new(-0.2f, -0.2f, -0.5f), new(0.2f, -0.2f, -0.5f), new(0.2f, 0.2f, -0.5f), new(-0.2f, 0.2f, -0.5f),
                        new(-0.2f, -0.2f, 0.5f),  new(0.2f, -0.2f, 0.5f),  new(0.2f, 0.2f, 0.5f),  new(-0.2f, 0.2f, 0.5f)
                    ],
                    Faces =
                    [
                        new ThreeDoFace { VertexIndices = [0, 4, 7, 3], Normal = new(-1, 0, 0) }
                    ]
                }
            ]
        };

        var player = new PlayerPhysics(new Vector3(0f, 0f, 0.5f), startSector: 0)
        {
            ModelResolver = name => name == "pillar.3do" ? pillarModel : null
        };

        float dt = 1.0f / 60.0f;

        // Swim into the 3DO face at X = 0.3
        for (int i = 0; i < 60; i++)
        {
            player.Update(dt, new Vector3(1f, 0f, 0f), wishJump: false, level);
        }
        Assert.True(player.Position.X <= 0.3f - player.Radius + 0.01f,
            $"Expected 3DO face collision underwater to block at X<=0.245, got X={player.Position.X}");

        // Swim into the sector wall at X = -1.0
        for (int i = 0; i < 120; i++)
        {
            player.Update(dt, new Vector3(-1f, 0f, 0f), wishJump: false, level);
        }
        Assert.True(player.Position.X >= -1.0f + player.Radius - 0.01f,
            $"Expected sector wall collision underwater, got X={player.Position.X}");

        // Swim up into ceiling at Z = 1.5
        for (int i = 0; i < 120; i++)
        {
            player.Update(dt, Vector3.Zero, wishJump: true, level);
        }
        Assert.True(player.Position.Z + player.Height <= 1.5f + 0.01f,
            $"Expected ceiling collision underwater, got Z+Height={player.Position.Z + player.Height}");
    }

    [Fact]
    public void Retail_UnderwaterSectors_EnableSwimmingWithoutClipping()
    {
        if (!File.Exists(SteamJk1Path))
            return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string[] candidateLevels = ["jkl/06bbarons.jkl", "jkl/16aescapeship.jkl"];

        JklLevel? level = null;
        JklSector? underwaterSector = null;

        foreach (string entry in candidateLevels)
        {
            if (!archive.Contains(entry))
                continue;

            string text = Encoding.ASCII.GetString(archive.ReadAllBytes(entry));
            var parsed = JklReader.Parse(text, Path.GetFileNameWithoutExtension(entry));
            foreach (var sec in parsed.Sectors)
            {
                if ((sec.Flags & SectorFlagUnderwater) != 0 && sec.Surfaces.Count >= 4)
                {
                    level = parsed;
                    underwaterSector = sec;
                    break;
                }
            }

            if (underwaterSector != null)
                break;
        }

        Assert.NotNull(level);
        Assert.NotNull(underwaterSector);

        // Compute interior centroid of the underwater sector
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (var surf in underwaterSector!.Surfaces)
        {
            foreach (int vIdx in surf.VertexIndices)
            {
                if (vIdx >= 0 && vIdx < level!.Vertices.Count)
                {
                    sum += level.Vertices[vIdx];
                    count++;
                }
            }
        }
        Assert.True(count > 0);
        Vector3 centroid = sum / count;

        var player = new PlayerPhysics(centroid, underwaterSector.SectorIndex);
        float dt = 1.0f / 60.0f;

        // Initial tick verifies IsUnderwater is set from retail Flags & 0x2
        player.Update(dt, Vector3.Zero, wishJump: false, level!);
        Assert.True(player.IsUnderwater);
        Assert.False(player.IsGrounded);

        float startZ = player.Position.Z;

        // Swim upward & forward in 3D
        for (int i = 0; i < 20; i++)
        {
            player.Update(dt, new Vector3(1f, 0f, 0f), wishJump: true, level!);
        }

        Assert.True(player.Position.Z > startZ, $"Expected upward swimming in retail underwater sector, startZ={startZ}, endZ={player.Position.Z}");
        Assert.True(player.CurrentSectorIndex >= 0 && player.CurrentSectorIndex < level!.Sectors.Count);
        Assert.True(player.Position.Z > -20.0f);
    }
}
