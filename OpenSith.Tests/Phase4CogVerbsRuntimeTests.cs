using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Client;
using OpenSith.Core.Cog;
using OpenSith.Core.Models;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Runtime end-to-end tests for all Phase 4 COG verbs wired into <see cref="WorldSimulation"/>.
/// </summary>
public class Phase4CogVerbsRuntimeTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    private sealed class InMemoryVfs : IVirtualFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddText(string path, string content) =>
            _files[Normalize(path)] = Encoding.ASCII.GetBytes(content);

        public void AddBytes(string path, byte[] bytes) =>
            _files[Normalize(path)] = bytes;

        public bool FileExists(string path) => _files.ContainsKey(Normalize(path));
        public byte[] ReadAllBytes(string path) => _files[Normalize(path)];
        public Stream OpenRead(string path) => new MemoryStream(_files[Normalize(path)], writable: false);
        public string ReadAllText(string path) => Encoding.ASCII.GetString(_files[Normalize(path)]);
        public void Dispose() { }

        private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    }

    private static JklLevel CreateTwoRoomPortalLevel()
    {
        var level = new JklLevel { Name = "phase4_two_room" };
        level.Materials.Add("wall0.mat");

        // Room 0: [-2, 2] x [-2, 0], z = [0, 2]
        // Room 1: [-2, 2] x [ 0, 2], z = [0, 2]
        // Portal plane at Y = 0
        level.Vertices.AddRange([
            new Vector3(-2f, -2f, 0f), // 0
            new Vector3( 2f, -2f, 0f), // 1
            new Vector3( 2f,  0f, 0f), // 2
            new Vector3(-2f,  0f, 0f), // 3
            new Vector3( 2f,  2f, 0f), // 4
            new Vector3(-2f,  2f, 0f), // 5
            new Vector3(-2f,  0f, 2f), // 6
            new Vector3( 2f,  0f, 2f), // 7
        ]);

        var floor0 = new JklSurface
        {
            SurfaceIndex = 0,
            MaterialIndex = 0,
            GeometryMode = 4,
            AdjoinIndex = -1,
            VertexIndices = [0, 1, 2, 3],
            Normal = Vector3.UnitZ,
            SurfaceFlags = 0x5
        };
        var portal0To1 = new JklSurface
        {
            SurfaceIndex = 1,
            MaterialIndex = 0,
            GeometryMode = 0,
            AdjoinIndex = 0,
            VertexIndices = [3, 2, 7, 6],
            Normal = -Vector3.UnitY,
            SurfaceFlags = 0x2 // SITH_SURFACE_COG_LINKED
        };
        var floor1 = new JklSurface
        {
            SurfaceIndex = 2,
            MaterialIndex = 0,
            GeometryMode = 4,
            AdjoinIndex = -1,
            VertexIndices = [3, 2, 4, 5],
            Normal = Vector3.UnitZ,
            SurfaceFlags = 0x5
        };
        var portal1To0 = new JklSurface
        {
            SurfaceIndex = 3,
            MaterialIndex = 0,
            GeometryMode = 0,
            AdjoinIndex = 1,
            VertexIndices = [2, 3, 6, 7],
            Normal = Vector3.UnitY,
            SurfaceFlags = 0x2
        };

        var sec0 = new JklSector
        {
            SectorIndex = 0,
            Surfaces = [floor0, portal0To1],
            BoundBoxMin = new Vector3(-2f, -2f, 0f),
            BoundBoxMax = new Vector3(2f, 0f, 2f),
            VertexIndices = [0, 1, 2, 3, 6, 7]
        };
        var sec1 = new JklSector
        {
            SectorIndex = 1,
            Surfaces = [floor1, portal1To0],
            BoundBoxMin = new Vector3(-2f, 0f, 0f),
            BoundBoxMax = new Vector3(2f, 2f, 2f),
            VertexIndices = [2, 3, 4, 5, 6, 7]
        };

        portal0To1.MirrorSurface = portal1To0;
        portal0To1.MirrorSector = sec1;
        portal1To0.MirrorSurface = portal0To1;
        portal1To0.MirrorSector = sec0;

        level.Adjoins.AddRange([
            new JklAdjoin { AdjoinIndex = 0, Flags = 0x3, Mirror = 1, Dist = 0f },
            new JklAdjoin { AdjoinIndex = 1, Flags = 0x3, Mirror = 0, Dist = 0f }
        ]);
        level.Sectors.AddRange([sec0, sec1]);
        level.Surfaces.AddRange([floor0, portal0To1, floor1, portal1To0]);

        level.Things.Add(new JklThing
        {
            ThingIndex = 0,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, -0.5f, 0f),
            PitchYawRoll = Vector3.Zero
        });

        return level;
    }

    [Fact]
    public void SurfaceGeoMode_Material_FaceType_And_SenderType_Are_Wired()
    {
        var level = CreateTwoRoomPortalLevel();
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/surf_test.cog", """
            symbols
            message activate
            surface gate=1 linkid=7
            int outGeo=-1
            int outSenderType=-1
            int outSector=-1
            int outVerts=0
            vector outNormal
            vector outVertPos
            end
            code
            activate:
                outSenderType = GetSenderType();
                SetFaceGeoMode(gate, 4);
                outGeo = GetFaceGeoMode(gate);
                SetSurfaceMat(gate, "00water1.mat");
                SetFaceType(gate, 2);
                outSector = GetSurfaceSector(gate);
                outVerts = GetNumSurfaceVertices(gate);
                outNormal = GetSurfaceNormal(gate);
                outVertPos = GetSurfaceVertexPos(gate, 1);
                return;
            end
            """);

        level.Cogs.Add(new JklCogPlacement { Index = 0, ScriptName = "surf_test.cog", SymbolArgs = [] });
        var sim = new WorldSimulation(level, vfs);

        var geoChanges = new List<(int surfaceId, int geoMode)>();
        var matChanges = new List<(int surfaceId, string mat)>();
        sim.OnSurfaceGeoModeChanged += (s, g) => geoChanges.Add((s, g));
        sim.OnSurfaceMaterialChanged += (s, m) => matChanges.Add((s, m));

        // Player at Y=-0.16 looking directly at linked portal surface 1 (at Y=0, normal=-Y)
        bool activated = sim.ActivateNearestDoor(new Vector3(0f, -0.16f, 0f), Vector3.UnitY);
        Assert.True(activated);

        Assert.Equal(4, level.Surfaces[1].GeometryMode);
        Assert.Contains((1, 4), geoChanges);
        Assert.Contains((1, "00water1.mat"), matChanges);
        Assert.True((level.Surfaces[1].FaceFlags & 0x2) != 0);
        Assert.Equal("00water1.mat", level.Materials[level.Surfaces[1].MaterialIndex]);

        Assert.True(sim.TryGetCogSymbolValue("surf_test.cog", "outSenderType", out var vSenderType));
        Assert.Equal(6, vSenderType.AsInt);
        Assert.True(sim.TryGetCogSymbolValue("surf_test.cog", "outGeo", out var vGeo));
        Assert.Equal(4, vGeo.AsInt);
        Assert.True(sim.TryGetCogSymbolValue("surf_test.cog", "outSector", out var vSector));
        Assert.Equal(0, vSector.AsInt);
        Assert.True(sim.TryGetCogSymbolValue("surf_test.cog", "outVerts", out var vVerts));
        Assert.Equal(4, vVerts.AsInt);
        Assert.True(sim.TryGetCogSymbolValue("surf_test.cog", "outNormal", out var vNormal));
        Assert.Equal(-Vector3.UnitY, vNormal.AsVector3);
        Assert.True(sim.TryGetCogSymbolValue("surf_test.cog", "outVertPos", out var vVertPos));
        Assert.Equal(new Vector3(2f, 0f, 0f), vVertPos.AsVector3);
    }

    [Fact]
    public void AdjoinFlags_Dynamically_Block_And_Unblock_PlayerPhysics_Traversal()
    {
        var level = CreateTwoRoomPortalLevel();
        var sim = new WorldSimulation(level, new InMemoryVfs());

        // Block portal surface 1 by clearing PASSABLE flag 0x2
        sim.ClearAdjoinFlags(1, 0x2);
        Assert.Equal(0x1, sim.GetAdjoinFlags(1));

        var player = new PlayerPhysics(new Vector3(0f, -0.25f, 0f), 0);
        for (int i = 0; i < 40; i++)
            player.Update(0.02f, Vector3.UnitY, false, level);

        // Player must remain blocked in Sector 0 (cannot cross Y=0 plane into Sector 1)
        Assert.Equal(0, player.CurrentSectorIndex);
        Assert.True(player.Position.Y <= 0.001f, $"Expected player blocked at Y<=0.001f, actual pos={player.Position}, adjoin0={level.Adjoins[0].Flags}, adjoin1={level.Adjoins[1].Flags}");

        // Now restore PASSABLE flag 0x2 and continue walking +Y
        sim.SetAdjoinFlags(1, 0x2);
        Assert.Equal(0x3, sim.GetAdjoinFlags(1));

        for (int i = 0; i < 40; i++)
            player.Update(0.02f, Vector3.UnitY, false, level);

        Assert.Equal(1, player.CurrentSectorIndex);
        Assert.True(player.Position.Y > 0.1f);
    }

    [Fact]
    public void SectorFlags_And_Tint_Toggle_Underwater_And_ScreenTint()
    {
        var level = CreateTwoRoomPortalLevel();
        var sim = new WorldSimulation(level, new InMemoryVfs());
        var player = new PlayerPhysics(new Vector3(0f, -0.5f, 0.2f), 0);

        player.Update(0.02f, Vector3.Zero, false, level);
        Assert.False(player.IsUnderwater);

        // Enable underwater sector flag 0x2 + sector tint
        sim.SetSectorFlags(0, 0x2);
        sim.SetSectorTint(0, new Vector3(0f, 0f, 1f));
        player.Update(0.02f, Vector3.Zero, false, level);
        Assert.True(player.IsUnderwater);
        Assert.True(sim.CurrentScreenTint.Z > 0.2f);

        // AddTint + clear water flag (03_paddlepool.cog pattern)
        sim.AddDynamicTint(0, 0.2f, 0f, 0f);
        Assert.True(sim.CurrentScreenTint.X > 0.15f);

        sim.ClearSectorFlags(0, 0x2);
        sim.SetSectorTint(0, Vector3.Zero);
        player.Update(0.02f, Vector3.Zero, false, level);
        Assert.False(player.IsUnderwater);
    }

    [Fact]
    public void RotatePivot_Orbits_And_Rotates_Door_And_Attached_Child_And_Fires_Arrived()
    {
        var level = CreateTwoRoomPortalLevel();
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/pivot_test.cog", """
            symbols
            message startup
            message arrived
            thing fulcrum=1 linkid=1
            thing rider=2 nolink
            int arrivedCount=0 local
            end
            code
            startup:
                AttachThingToThing(rider, fulcrum);
                SetThingAttachFlags(rider, 8);
                RotatePivot(fulcrum, 1, 1.0);
                return;
            arrived:
                arrivedCount = arrivedCount + 1;
                return;
            end
            """);

        // Fulcrum at (0, 0, 0), Frame 1 defines pivot at (0, 0, 0) and delta rotation (0, 90, 0)
        // like retail 07_camerakey.cog / 09fuelstation.jkl halfcircledoor
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "halfcircledoor",
            SectorIndex = 0,
            Position = Vector3.Zero,
            PitchYawRoll = new Vector3(0f, 0f, 180f),
            NumFrames = 2,
            ExtraArguments = "frame=(0/0/0:0/0/180) frame=(0/0/0:0/90/0)"
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 2,
            TemplateName = "camera_obj",
            SectorIndex = 0,
            Position = new Vector3(0f, 1f, 0f),
            PitchYawRoll = Vector3.Zero
        });
        level.Cogs.Add(new JklCogPlacement { Index = 0, ScriptName = "pivot_test.cog", SymbolArgs = [] });

        var sim = new WorldSimulation(level, vfs);
        Assert.Equal(1, sim.GetThingParent(2));
        Assert.Equal(8, sim.GetAttachFlags(2));
        Assert.True(sim.IsThingMoving(1));

        for (int i = 0; i < 10; i++)
            sim.Update(0.1f, Vector3.Zero, 0);

        Assert.False(sim.IsThingMoving(1));
        Assert.Equal(90f, level.Things[1].PitchYawRoll.Y, precision: 2);
        // Rider originally at (0, 1, 0) rotated +90 yaw in Sith (-sin(90), cos(90)) -> (-1, 0, 0)
        Assert.Equal(-1f, level.Things[2].Position.X, precision: 2);
        Assert.Equal(0f, level.Things[2].Position.Y, precision: 2);
        Assert.Equal(90f, level.Things[2].PitchYawRoll.Y, precision: 2);
        Assert.True(sim.TryGetCogSymbolValue("pivot_test.cog", "arrivedCount", out var arrived1));
        Assert.Equal(1, arrived1.AsInt);

        // Verify SetThingAttachFlags ORs additional flags without clobbering existing flags (0x8 | 0x4 = 0xC)
        sim.SetThingAttachFlags(2, 0x4);
        Assert.Equal(0xC, sim.GetAttachFlags(2));

        // Reverse rotation with negative time (-0.5) rotates back to initial yaw and fires arrived again
        float beforeYaw = level.Things[1].PitchYawRoll.Y;
        sim.RotatePivot(1, 1, -0.5f);
        for (int i = 0; i < 6; i++)
            sim.Update(0.1f, Vector3.Zero, 0);

        Assert.False(sim.IsThingMoving(1));
        Assert.Equal(0f, level.Things[1].PitchYawRoll.Y, precision: 2);
        Assert.NotEqual(beforeYaw, level.Things[1].PitchYawRoll.Y);
        Assert.True(sim.TryGetCogSymbolValue("pivot_test.cog", "arrivedCount", out var arrived2));
        Assert.Equal(2, arrived2.AsInt);
    }

    [Fact]
    public void RotatePivot_MultiAxis_OffCenter_Composes3DMatrix_LikeRetailDoorsAndHatches()
    {
        var level = CreateTwoRoomPortalLevel();
        // Retail 06abarons.jkl Thing 221 (06door3x2) off-center pivot + non-zero StartPyr
        var doorStartPyr = new Vector3(-15.0f, -45.0f, 90.0f);
        var doorDeltaPyr = new Vector3(43.079517f, 59.510811f, -110.753555f);
        var doorStartPos = new Vector3(21.509611f, 41.200092f, 1.049890f);
        var doorPivotPos = new Vector3(21.494581f, 41.155125f, 0.916974f);
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "06door3x2",
            SectorIndex = 0,
            Position = doorStartPos,
            PitchYawRoll = doorStartPyr,
            NumFrames = 2,
            ExtraArguments = $"frame=({doorStartPos.X}/{doorStartPos.Y}/{doorStartPos.Z}:{doorStartPyr.X}/{doorStartPyr.Y}/{doorStartPyr.Z}) frame=({doorPivotPos.X}/{doorPivotPos.Y}/{doorPivotPos.Z}:{doorDeltaPyr.X}/{doorDeltaPyr.Y}/{doorDeltaPyr.Z})"
        });

        // Retail 10cargo.jkl Thing 12 (mndr) gimbal-lock StartPyr (90,0,0) + off-center pivot + Roll delta -143
        var hatchStartPos = new Vector3(1.5f, 0f, 2f);
        var hatchPivotPos = new Vector3(1.0f, 0f, 2f);
        level.Things.Add(new JklThing
        {
            ThingIndex = 2,
            TemplateName = "mndr",
            SectorIndex = 0,
            Position = hatchStartPos,
            PitchYawRoll = new Vector3(90f, 0f, 0f),
            NumFrames = 2,
            ExtraArguments = "frame=(1.5/0/2:90/0/0) frame=(1.0/0/2:0/0/-143)"
        });

        var sim = new WorldSimulation(level, new InMemoryVfs());
        sim.RotatePivot(1, 1, 0.8f);
        sim.RotatePivot(2, 1, 1.0f);

        for (int i = 0; i < 12; i++)
            sim.Update(0.1f, Vector3.Zero, 0);

        Assert.False(sim.IsThingMoving(1));
        Assert.False(sim.IsThingMoving(2));

        // 06door3x2 must match SithMatrix34.Multiply(Build34(delta), Build34(start)) so Lvec points vertically up (0, 0, 1)
        var expectedDoorRot = SithMatrix34.Multiply(SithMatrix34.Build34(doorDeltaPyr, Vector3.Zero), SithMatrix34.Build34(doorStartPyr, Vector3.Zero));
        var actualDoorRot = SithMatrix34.Build34(level.Things[1].PitchYawRoll, Vector3.Zero);
        Assert.Equal(expectedDoorRot.Lvec.X, actualDoorRot.Lvec.X, precision: 3);
        Assert.Equal(expectedDoorRot.Lvec.Y, actualDoorRot.Lvec.Y, precision: 3);
        Assert.Equal(expectedDoorRot.Lvec.Z, actualDoorRot.Lvec.Z, precision: 3);
        Vector3 expectedDoorPos = doorPivotPos + SithMatrix34.Build34(doorDeltaPyr, Vector3.Zero).TransformNormal(doorStartPos - doorPivotPos);
        Assert.Equal(expectedDoorPos.X, level.Things[1].Position.X, precision: 3);
        Assert.Equal(expectedDoorPos.Y, level.Things[1].Position.Y, precision: 3);
        Assert.Equal(expectedDoorPos.Z, level.Things[1].Position.Z, precision: 3);

        // mndr hatch must orbit (0.5, 0, 0) around (1, 0, 2) by -143 degrees in XY and compose rotation identically
        Vector3 expectedHatchPos = hatchPivotPos + SithMatrix34.Build34(new Vector3(0f, 0f, -143f), Vector3.Zero).TransformNormal(hatchStartPos - hatchPivotPos);
        Assert.Equal(expectedHatchPos.X, level.Things[2].Position.X, precision: 3);
        Assert.Equal(expectedHatchPos.Y, level.Things[2].Position.Y, precision: 3);
        Assert.Equal(expectedHatchPos.Z, level.Things[2].Position.Z, precision: 3);
        var expectedHatchRot = SithMatrix34.Multiply(SithMatrix34.Build34(new Vector3(0f, 0f, -143f), Vector3.Zero), SithMatrix34.Build34(new Vector3(90f, 0f, 0f), Vector3.Zero));
        var actualHatchRot = SithMatrix34.Build34(level.Things[2].PitchYawRoll, Vector3.Zero);
        Assert.Equal(expectedHatchRot.Rvec.X, actualHatchRot.Rvec.X, precision: 3);
        Assert.Equal(expectedHatchRot.Rvec.Y, actualHatchRot.Rvec.Y, precision: 3);
        Assert.Equal(expectedHatchRot.Lvec.Z, actualHatchRot.Lvec.Z, precision: 3);
    }

    [Fact]
    public void JumpToFrame_SkipToFrame_PathMovePause_Resume_And_GetGoalFrame_Work()
    {
        var level = CreateTwoRoomPortalLevel();
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "01door",
            SectorIndex = 0,
            Position = new Vector3(0f, 0f, 0f),
            NumFrames = 3,
            ExtraArguments = "frame=(0/0/0:0/0/0) frame=(0/0/1:0/0/0) frame=(0/0/2:0/0/0)"
        });
        var sim = new WorldSimulation(level, new InMemoryVfs());

        sim.JumpToFrame(1, 2, sectorId: 1);
        Assert.Equal(new Vector3(0f, 0f, 2f), level.Things[1].Position);
        Assert.Equal(1, level.Things[1].SectorIndex);
        Assert.Equal(2, sim.GetCurFrame(1));
        Assert.False(sim.IsThingMoving(1));

        sim.SkipToFrame(1, 0, speed: 10.0f); // 10 * 0.1 = 1.0 m/s over 2.0m = 2.0s
        Assert.Equal(0, sim.GetGoalFrame(1));
        Assert.True(sim.IsThingMoving(1));

        sim.Update(0.5f, Vector3.Zero, 0);
        float zAfterHalfSec = level.Things[1].Position.Z;
        Assert.InRange(zAfterHalfSec, 1.4f, 1.6f);

        sim.PathMovePause(1);
        sim.Update(0.5f, Vector3.Zero, 0);
        Assert.Equal(zAfterHalfSec, level.Things[1].Position.Z, precision: 4);

        sim.PathMoveResume(1);
        for (int i = 0; i < 20; i++)
            sim.Update(0.1f, Vector3.Zero, 0);

        Assert.False(sim.IsThingMoving(1));
        Assert.Equal(0f, level.Things[1].Position.Z, precision: 3);
    }

    [Fact]
    public void TeleportThing_FirstThingInSector_NextThingInSector_And_ThingTimers_Work()
    {
        var level = CreateTwoRoomPortalLevel();
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/timer_test.cog", """
            symbols
            message startup
            message timer
            message pulse
            thing target=1 linkid=1
            int timerFired=0 local
            int pulseCount=0 local
            end
            code
            startup:
                SetThingTimer(target, 0.25);
                SetThingPulse(target, 0.20);
                return;
            timer:
                timerFired = timerFired + 1;
                return;
            pulse:
                pulseCount = pulseCount + 1;
                return;
            end
            """);

        level.Things.Add(new JklThing { ThingIndex = 1, TemplateName = "prop_a", SectorIndex = 1, Position = new Vector3(0.5f, 1f, 0f) });
        level.Things.Add(new JklThing { ThingIndex = 2, TemplateName = "dest_b", SectorIndex = 1, Position = new Vector3(1.5f, 1.5f, 0.5f), PitchYawRoll = new Vector3(0f, 45f, 0f) });
        level.Cogs.Add(new JklCogPlacement { Index = 0, ScriptName = "timer_test.cog", SymbolArgs = [] });

        var sim = new WorldSimulation(level, vfs);
        (Vector3 Pos, Vector3 Pyr, int Sector)? teleported = null;
        sim.OnPlayerTeleported += (p, pyr, s) => teleported = (p, pyr, s);

        // Enumerate things in sector 1: 1 -> 2 -> -1, plus invalid/-1 guard, CreateThing, and DestroyThing
        Assert.Equal(-1, sim.NextThingInSector(-1));
        int first = sim.FirstThingInSector(1);
        int second = sim.NextThingInSector(first);
        int third = sim.NextThingInSector(second);
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(-1, third);

        // Teleport local player (thing 0) to thing 2
        sim.TeleportThing(0, 2);
        Assert.NotNull(teleported);
        Assert.Equal(new Vector3(1.5f, 1.5f, 0.5f), level.Things[0].Position);
        Assert.Equal(1, level.Things[0].SectorIndex);

        // Advance simulation to fire timer (0.25s) and two pulses (0.20s, 0.40s) with overshoot preserved
        for (int i = 0; i < 5; i++)
            sim.Update(0.09f, level.Things[0].Position, 1);

        Assert.True(sim.TryGetCogSymbolValue("timer_test.cog", "timerFired", out var timerVal));
        Assert.Equal(1, timerVal.AsInt);
        Assert.True(sim.TryGetCogSymbolValue("timer_test.cog", "pulseCount", out var pulseVal));
        Assert.Equal(2, pulseVal.AsInt);

        // Dynamic CreateThing + DestroyThing update FirstThingInSector/NextThingInSector cleanly
        int spawnedId = sim.CreateThingAtPos("prop_c", 1, new Vector3(1f, 1f, 0f), Vector3.Zero);
        sim.DestroyThing(1);
        var sec1Ids = new List<int>();
        for (int id = sim.FirstThingInSector(1); id >= 0; id = sim.NextThingInSector(id))
            sec1Ids.Add(id);
        Assert.DoesNotContain(1, sec1Ids);
        Assert.Contains(2, sec1Ids);
        Assert.Contains(spawnedId, sec1Ids);

        // LightAnim + StopAnim
        int surfAnim = sim.SurfaceLightAnim(0, 0.0f, 1.0f, 0.2f);
        int thingAnim = sim.ThingLightAnim(1, 0.2f, 0.8f, 0.2f);
        Assert.True(surfAnim > 0);
        Assert.True(thingAnim > surfAnim);
        Assert.Equal(surfAnim, sim.GetSurfaceAnim(0));

        float latestSurfLight = -1f;
        sim.OnSurfaceLightChanged += (s, l) => { if (s == 0) latestSurfLight = l; };
        sim.Update(0.1f, level.Things[0].Position, 1);
        Assert.InRange(latestSurfLight, 0.4f, 0.6f);
        Assert.InRange(sim.GetThingLight(1), 0.4f, 0.6f);

        sim.StopAnim(surfAnim);
        Assert.Equal(-1, sim.GetSurfaceAnim(0));
    }

    [Fact]
    public void DeferredStartup_Allows_Event_Subscriptions_Before_StartupCogs_Run()
    {
        var level = CreateTwoRoomPortalLevel();
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/startup_events.cog", """
            symbols
            message startup
            surface gate=1 nolink
            end
            code
            startup:
                SetFaceGeoMode(gate, 4);
                SetSectorLight(0, 0.65, 0.0);
                AssignWeapon(GetLocalPlayerThing(), 10);
                return;
            end
            """);
        level.Cogs.Add(new JklCogPlacement { Index = 0, ScriptName = "startup_events.cog", SymbolArgs = [] });

        var sim = new WorldSimulation(level, vfs, runStartupCogs: false);

        var geoEvents = new List<(int s, int g)>();
        var lightEvents = new List<(int sec, float l)>();
        var weaponEvents = new List<int>();
        sim.OnSurfaceGeoModeChanged += (s, g) => geoEvents.Add((s, g));
        sim.OnSectorLightChanged += (sec, l) => lightEvents.Add((sec, l));
        sim.OnPlayerWeaponAssigned += bin => weaponEvents.Add(bin);

        sim.StartLevel();

        Assert.Contains((1, 4), geoEvents);
        Assert.Contains((0, 0.65f), lightEvents);
        Assert.Contains(10, weaponEvents);
    }

    [Fact]
    public void SuperFlags_JkGetChoice_JkPrintUniString_And_0Arg_JkEndLevel_Work()
    {
        var level = CreateTwoRoomPortalLevel();
        var vfs = new InMemoryVfs();
        vfs.AddText("misc/cogstrings.uni", """
            MSGS 2
            "COG_01001" 0 "Locked!"
            "COG_00064" 0 "Dark Surge"
            """);

        var sim = new WorldSimulation(level, vfs);

        sim.JkSetSuperFlags(4);
        Assert.Equal(4, sim.JkGetSuperFlags());
        sim.JkClearSuperFlags(4);
        Assert.Equal(0, sim.JkGetSuperFlags());

        Assert.Equal(1, sim.JkGetChoice());
        sim.SetInv(0, 75, -25f);
        Assert.Equal(2, sim.JkGetChoice());

        sim.JkPrintUniString(0, 1001);
        Assert.Equal("Locked!", sim.StatusMessage);
        sim.JkPrintUniString(0, 64);
        Assert.Equal("Dark Surge", sim.StatusMessage);

        // 0-argument jkEndLevel() must mark level as completed (levels 09, 18, 20)
        var endCog = new CogInstance(CogParser.Parse("""
            symbols
            message startup
            end
            code
            startup:
                jkEndLevel();
                return;
            end
            """, "test_endlevel.cog"), cogId: 99);
        var vm = new CogVm(sim);
        vm.AddInstance(endCog);
        vm.SendMessage(endCog, "startup");
        Assert.True(sim.LevelCompleted);
    }

    [Fact]
    public void GetSoundLen_And_GetKeyLen_Read_Retail_Durations()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string res1Path = Path.Combine(SteamPath, "Resource", "Res1hi.gob");
        if (!File.Exists(res2Path))
            return;

        using var vfs = new VirtualFileSystem();
        if (File.Exists(res1Path))
            vfs.Mount(GobArchive.FromFile(res1Path));
        vfs.Mount(GobArchive.FromFile(res2Path));

        var sim = new WorldSimulation(CreateTwoRoomPortalLevel(), vfs);

        float keyLen = sim.GetKeyLen("sabvhold.key");
        float sndLen = sim.GetSoundLen("ltsaberon01.wav");

        Assert.True(keyLen > 0f, $"Expected positive keyframe duration for sabvhold.key, got {keyLen}");
        Assert.True(sndLen > 0f, $"Expected positive WAV duration for ltsaberon01.wav, got {sndLen}");
    }

    [Fact]
    public void StopSound_Clears_Looping_Sound_Entry_So_It_Can_Replay()
    {
        var sim = new WorldSimulation(CreateTwoRoomPortalLevel(), new InMemoryVfs());

        int ch1 = sim.PlaySoundThing("loop.wav", 1, 1.0f, 0.5f, 8.0f, flags: 1);
        Assert.True(ch1 > 0);

        int duplicate = sim.PlaySoundThing("loop.wav", 1, 1.0f, 0.5f, 8.0f, flags: 1);
        Assert.Equal(ch1, duplicate);

        sim.StopSound(ch1, 0f);

        int ch2 = sim.PlaySoundThing("loop.wav", 1, 1.0f, 0.5f, 8.0f, flags: 1);
        Assert.True(ch2 > ch1, $"Expected a fresh channel after StopSound, got ch1={ch1}, ch2={ch2}");
    }
}
