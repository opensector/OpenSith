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

public class FaceCollisionTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void SyntheticThreeDoFace_BlocksWhenClosed_PassesWhenSlidOpen_AndRespectsFullPyr()
    {
        var level = CreateBoxLevel(-1.0f, 1.0f, -1.0f, 1.0f, 0.0f, 1.0f);
        level.Templates["test_door"] = new JklTemplate
        {
            Name = "test_door",
            BasedOn = "none",
            Model3D = "test_door.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "cog",
                ["collide"] = "3"
            }
        };

        // Thin vertical slab spanning X in [-0.20, 0.20], Y in [-0.02, 0.02], Z in [-0.15, 0.15]
        // When rotated by roll = -90, local X maps to -Z and local Z maps to +X,
        // still spanning a vertical wall across Y = 0!
        var doorModel = CreateBoxThreeDo(0.20f, 0.02f, 0.15f);

        var door = new JklThing
        {
            ThingIndex = 1,
            TemplateName = "test_door",
            Name = "test_door",
            Model3D = "test_door.3do",
            Position = new Vector3(0.0f, 0.0f, 0.15f),
            PitchYawRoll = new Vector3(0.0f, 0.0f, -90.0f),
            SectorIndex = 0
        };
        level.Things.Add(door);

        var player = new PlayerPhysics(new Vector3(0.0f, -0.5f, 0.0f), startSector: 0)
        {
            ModelResolver = name => name.Equals("test_door.3do", StringComparison.OrdinalIgnoreCase) ? doorModel : null
        };

        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 5; i++)
            player.Update(dt, Vector3.Zero, wishJump: false, level);

        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0.0f, 1.0f, 0.0f), wishJump: false, level);

        // Front face is at Y = -0.02, player sphere radius = 0.065 -> blocked around Y = -0.085
        Assert.True(player.Position.Y <= -0.080f && player.Position.Y >= -0.095f,
            $"Roll=-90 3DO door should block player at Y ~ -0.085, but Y={player.Position.Y}");

        // Slide door horizontally out of the doorway (along X) -> player passes freely
        door.Position = new Vector3(0.6f, 0.0f, 0.15f);
        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0.0f, 1.0f, 0.0f), wishJump: false, level);

        Assert.True(player.Position.Y > 0.3f,
            $"Player should pass through after 3DO door slides open, but Y={player.Position.Y}");
    }

    [Fact]
    public void CollideSphere_UsesActualMoveSize_AndRespectsDisabledDestroyedFlags()
    {
        var level = CreateBoxLevel(-1.0f, 1.0f, -1.0f, 1.0f, 0.0f, 1.0f);
        level.Templates["sphere_prop"] = new JklTemplate
        {
            Name = "sphere_prop",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "actor",
                ["collide"] = "1",
                ["size"] = "0.20",
                ["movesize"] = "0.10"
            }
        };

        var prop = new JklThing
        {
            ThingIndex = 1,
            TemplateName = "sphere_prop",
            Name = "sphere_prop",
            Position = new Vector3(0.0f, 0.0f, 0.12f),
            SectorIndex = 0
        };
        level.Things.Add(prop);

        var player = new PlayerPhysics(new Vector3(0.0f, -0.5f, 0.0f), startSector: 0);
        float dt = 1.0f / 60.0f;

        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0.0f, 1.0f, 0.0f), wishJump: false, level);

        // Combined radius = 0.065 + 0.10 = 0.165 (authentic movesize, no 0.577 fudge)
        Assert.True(player.Position.Y <= -0.160f && player.Position.Y >= -0.175f,
            $"SITH_COLLIDE_SPHERE should block at -(0.065 + 0.10) = -0.165, but Y={player.Position.Y}");

        // Setting SITH_TF_DISABLED (0x80000) makes the thing non-solid
        prop.ThingFlags |= (uint)SithThingFlags.Disabled;
        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0.0f, 1.0f, 0.0f), wishJump: false, level);

        Assert.True(player.Position.Y > 0.2f,
            $"Disabled thing should not block player, but Y={player.Position.Y}");
    }

    [Fact]
    public void RetailGob_NarShaddaa_2x3Door_FaceCollisionBlocksAndOpens()
    {
        if (!File.Exists(SteamJk1Path) || !File.Exists(SteamRes2Path)) return;

        using var jk1 = GobArchive.FromFile(SteamJk1Path);
        using var res2 = GobArchive.FromFile(SteamRes2Path);
        string jklText = Encoding.ASCII.GetString(jk1.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(jklText, "01narshadda");

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

        // Thing 4 is a 2x3door (Yaw=90) at (1.200002, 9.650073, 6.35)
        var door = level.Things[4];
        Assert.Contains("door", door.TemplateName, StringComparison.OrdinalIgnoreCase);
        string doorModelName = !string.IsNullOrEmpty(door.Model3D) ? door.Model3D : level.ResolveModel3D(door.TemplateName);
        Assert.False(string.IsNullOrEmpty(doorModelName));
        Assert.NotNull(ResolveFromRes2(doorModelName));

        // Verify ky.3do InsertOffset.Z (~0.12) configures PelvisOffset
        var kyModel = ResolveFromRes2("ky.3do");
        Assert.NotNull(kyModel);
        Assert.InRange(kyModel!.InsertOffset.Z, 0.10f, 0.14f);

        float floorZ = 6.20f;
        var startPos = new Vector3(door.Position.X - 0.35f, door.Position.Y, floorZ);
        int startSector = PlayerPhysics.FindSectorAtPosition(startPos, door.SectorIndex, level);
        var player = new PlayerPhysics(startPos, startSector)
        {
            ModelResolver = ResolveFromRes2
        };
        player.ConfigureFromModel(kyModel);

        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 30; i++)
            player.Update(dt, new Vector3(1.0f, 0.0f, 0.0f), wishJump: false, level);

        Assert.True(player.IsGrounded, $"Player should remain grounded at floorZ=6.20, pos={player.Position}");
        Assert.True(player.Position.X < door.Position.X - 0.06f,
            $"Closed retail 2x3door.3do should block player via SITH_COLLIDE_FACE, pos={player.Position} vs door={door.Position}");

        // Simulate door opening upward (Frame 1 in 01narshadda.jkl is Z = 6.60)
        door.Position = new Vector3(door.Position.X, door.Position.Y, 6.60f);
        for (int i = 0; i < 30; i++)
            player.Update(dt, new Vector3(1.0f, 0.0f, 0.0f), wishJump: false, level);

        Assert.True(player.Position.X > door.Position.X + 0.05f,
            $"Opened retail 2x3door.3do should allow passage, pos={player.Position} vs door={door.Position}");
    }

    [Fact]
    public void RetailGob_NarShaddaa_4x3DoorRollMinus90_FaceCollisionBlocks()
    {
        if (!File.Exists(SteamJk1Path) || !File.Exists(SteamRes2Path)) return;

        using var jk1 = GobArchive.FromFile(SteamJk1Path);
        using var res2 = GobArchive.FromFile(SteamRes2Path);
        string jklText = Encoding.ASCII.GetString(jk1.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(jklText, "01narshadda");

        // Things 56 & 57 in 01narshadda.jkl are 4x3door_1 with Roll = -90
        var door56 = level.Things[56];
        Assert.Contains("4x3door_1", door56.TemplateName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(-90.0f, door56.PitchYawRoll.Z, 1);

        string doorModelName = !string.IsNullOrEmpty(door56.Model3D) ? door56.Model3D : level.ResolveModel3D(door56.TemplateName);
        Assert.False(string.IsNullOrEmpty(doorModelName));
        ThreeDoModel? doorModel = res2.Contains($"3do/{doorModelName}")
            ? ThreeDoReader.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes($"3do/{doorModelName}")))
            : null;
        Assert.NotNull(doorModel);

        // Place player in a flat synthetic box with the retail door56 (preserving its Roll=-90 and model)
        var testLevel = CreateBoxLevel(-2.0f, 2.0f, -2.0f, 2.0f, 0.0f, 1.0f);
        testLevel.Templates["4x3door_1"] = level.Templates["4x3door_1"];
        if (level.Templates.TryGetValue("_structure", out var s)) testLevel.Templates["_structure"] = s;
        if (level.Templates.TryGetValue("_decor", out var d)) testLevel.Templates["_decor"] = d;

        var placedDoor = new JklThing
        {
            ThingIndex = 1,
            TemplateName = door56.TemplateName,
            Name = door56.Name,
            Model3D = doorModelName,
            Position = new Vector3(0.0f, 0.0f, 0.15f),
            PitchYawRoll = new Vector3(door56.PitchYawRoll.X, 0.0f, door56.PitchYawRoll.Z),
            SectorIndex = 0
        };
        testLevel.Things.Add(placedDoor);

        var player = new PlayerPhysics(new Vector3(0.0f, -0.5f, 0.0f), startSector: 0)
        {
            ModelResolver = name => name.Equals(doorModelName, StringComparison.OrdinalIgnoreCase) ? doorModel : null
        };

        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0.0f, 1.0f, 0.0f), wishJump: false, testLevel);

        Assert.True(player.Position.Y < -0.05f,
            $"Retail 4x3door_1.3do with Roll=-90 should block player via SITH_COLLIDE_FACE, pos={player.Position}");

        // Slide door aside -> player passes through
        placedDoor.Position = new Vector3(1.5f, 0.0f, 0.15f);
        for (int i = 0; i < 60; i++)
            player.Update(dt, new Vector3(0.0f, 1.0f, 0.0f), wishJump: false, testLevel);

        Assert.True(player.Position.Y > 0.3f,
            $"Player should pass freely after 4x3door_1 slides open, pos={player.Position}");
    }

    private static ThreeDoModel CreateBoxThreeDo(float hx, float hy, float hz)
    {
        var mesh = new ThreeDoMesh
        {
            MeshIndex = 0,
            Name = "box",
            Radius = MathF.Sqrt(hx * hx + hy * hy + hz * hz),
            Vertices =
            [
                new Vector3(-hx, -hy, -hz), // 0
                new Vector3( hx, -hy, -hz), // 1
                new Vector3( hx, -hy,  hz), // 2
                new Vector3(-hx, -hy,  hz), // 3
                new Vector3(-hx,  hy, -hz), // 4
                new Vector3( hx,  hy, -hz), // 5
                new Vector3( hx,  hy,  hz), // 6
                new Vector3(-hx,  hy,  hz), // 7
            ],
            Faces =
            [
                new ThreeDoFace { VertexIndices = [0, 1, 2, 3], Normal = new Vector3(0, -1, 0) },
                new ThreeDoFace { VertexIndices = [5, 4, 7, 6], Normal = new Vector3(0, 1, 0) },
                new ThreeDoFace { VertexIndices = [4, 0, 3, 7], Normal = new Vector3(-1, 0, 0) },
                new ThreeDoFace { VertexIndices = [1, 5, 6, 2], Normal = new Vector3(1, 0, 0) },
                new ThreeDoFace { VertexIndices = [3, 2, 6, 7], Normal = new Vector3(0, 0, 1) },
                new ThreeDoFace { VertexIndices = [4, 5, 1, 0], Normal = new Vector3(0, 0, -1) }
            ]
        };

        return new ThreeDoModel
        {
            Radius = mesh.Radius,
            Meshes = [mesh]
        };
    }

    private static JklLevel CreateBoxLevel(float minX, float maxX, float minY, float maxY, float floorZ, float ceilingZ)
    {
        var level = new JklLevel { Name = "synthetic_box" };
        var sector = new JklSector { SectorIndex = 0 };
        level.Sectors.Add(sector);

        AddQuad(level, sector,
            new Vector3(minX, minY, floorZ), new Vector3(maxX, minY, floorZ),
            new Vector3(maxX, maxY, floorZ), new Vector3(minX, maxY, floorZ),
            new Vector3(0, 0, 1));
        AddQuad(level, sector,
            new Vector3(minX, maxY, ceilingZ), new Vector3(maxX, maxY, ceilingZ),
            new Vector3(maxX, minY, ceilingZ), new Vector3(minX, minY, ceilingZ),
            new Vector3(0, 0, -1));
        AddQuad(level, sector,
            new Vector3(minX, maxY, floorZ), new Vector3(maxX, maxY, floorZ),
            new Vector3(maxX, maxY, ceilingZ), new Vector3(minX, maxY, ceilingZ),
            new Vector3(0, -1, 0));
        AddQuad(level, sector,
            new Vector3(maxX, minY, floorZ), new Vector3(minX, minY, floorZ),
            new Vector3(minX, minY, ceilingZ), new Vector3(maxX, minY, ceilingZ),
            new Vector3(0, 1, 0));
        AddQuad(level, sector,
            new Vector3(maxX, maxY, floorZ), new Vector3(maxX, minY, floorZ),
            new Vector3(maxX, minY, ceilingZ), new Vector3(maxX, maxY, ceilingZ),
            new Vector3(-1, 0, 0));
        AddQuad(level, sector,
            new Vector3(minX, minY, floorZ), new Vector3(minX, maxY, floorZ),
            new Vector3(minX, maxY, ceilingZ), new Vector3(minX, minY, ceilingZ),
            new Vector3(1, 0, 0));

        return level;
    }

    private static void AddQuad(JklLevel level, JklSector sector, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        int i0 = level.Vertices.Count;
        level.Vertices.Add(a);
        level.Vertices.Add(b);
        level.Vertices.Add(c);
        level.Vertices.Add(d);

        var surf = new JklSurface
        {
            SurfaceIndex = level.Surfaces.Count,
            VertexIndices = [i0, i0 + 1, i0 + 2, i0 + 3],
            Normal = normal
        };
        level.Surfaces.Add(surf);
        sector.Surfaces.Add(surf);
    }
}
