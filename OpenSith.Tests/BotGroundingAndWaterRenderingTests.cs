using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using OpenSith.Client;
using OpenSith.Core.AI;
using OpenSith.Core.Networking;
using OpenSith.Core.Physics;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// Regression tests verifying Deathmatch AI bot grounding, jump cooldowns, realistic Force Speed caps,
/// downward ramp adhesion, paired water portal lighting preservation, multi-cel water animation,
/// and multiplayer SlideWall/underwater screen-tint replication.
/// </summary>
public sealed class BotGroundingAndWaterRenderingTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";
    private readonly ITestOutputHelper _output;

    public BotGroundingAndWaterRenderingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static VirtualFileSystem? TryMountRetailMpVfs()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1MpPath = Path.Combine(SteamPath, "Episode", "JK1MP.GOB");
        if (!File.Exists(res2Path) || !File.Exists(jk1MpPath))
            return null;

        var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (File.Exists(jk1Path))
            vfs.Mount(GobArchive.FromFile(jk1Path));
        vfs.Mount(GobArchive.FromFile(jk1MpPath));
        return vfs;
    }

    private static JklSurface AddQuad(
        JklLevel level,
        JklSector sector,
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        Vector3 v3,
        Vector3 normal,
        int surfaceFlags = 0x4)
    {
        int b = level.Vertices.Count;
        level.Vertices.Add(v0);
        level.Vertices.Add(v1);
        level.Vertices.Add(v2);
        level.Vertices.Add(v3);

        var s = new JklSurface
        {
            SurfaceIndex = level.Surfaces.Count,
            AdjoinIndex = -1,
            GeometryMode = 4,
            SurfaceFlags = surfaceFlags,
            Normal = Vector3.Normalize(normal),
            VertexIndices = [b, b + 1, b + 2, b + 3]
        };
        level.Surfaces.Add(s);
        sector.Surfaces.Add(s);
        return s;
    }

    private static async Task<ServerWelcomePacket> ConnectWithServerTickAsync(
        DedicatedGameServer server,
        MultiplayerClient client,
        string name)
    {
        Task<ServerWelcomePacket> connectTask = client.ConnectAsync("127.0.0.1", server.BoundPort, name, team: 0);
        while (!connectTask.IsCompleted)
        {
            server.Tick(0.01f);
            await Task.Delay(5);
        }
        return await connectTask;
    }

    [Fact]
    public void DedicatedGameServer_ComputeBotMoveSpeed_CapsForceSpeedAndRemovesFlyBoost()
    {
        float easyFwd = DedicatedGameServer.ComputeBotMoveSpeed(BotSkill.Easy, new Vector3(0f, 1f, 0f), 0f);
        float medFwd = DedicatedGameServer.ComputeBotMoveSpeed(BotSkill.Medium, new Vector3(0f, 1f, 0f), 0f);
        float hardFwd = DedicatedGameServer.ComputeBotMoveSpeed(BotSkill.Hard, new Vector3(0f, 1f, 0f), 0f);
        float hardStrafe = DedicatedGameServer.ComputeBotMoveSpeed(BotSkill.Hard, new Vector3(1f, 0f, 0f), 0f);
        float hardMaxForceSpeed = DedicatedGameServer.ComputeBotMoveSpeed(BotSkill.Hard, new Vector3(0f, 1f, 0f), 2.0f);

        Assert.InRange(easyFwd, 1.0f, 1.4f);
        Assert.InRange(medFwd, 1.3f, 1.6f);
        Assert.InRange(hardFwd, 1.6f, 1.8f);
        Assert.True(hardStrafe < hardFwd);
        Assert.InRange(hardMaxForceSpeed, 2.2f, 2.75f);
    }

    [Fact]
    public void PlayerPhysics_RunningDownSlopedRampAt30Hz_RemainsGroundedUntilDropOff()
    {
        var level = new JklLevel { Name = "downward_ramp_adhesion" };
        var sec = new JklSector { SectorIndex = 0 };
        level.Sectors.Add(sec);

        // Box bounds: X in [-2, 2], Y in [-2, 4], Z in [-1, 3]
        AddQuad(level, sec, new(-2f, -2f, -1f), new(2f, -2f, -1f), new(2f, -2f, 3f), new(-2f, -2f, 3f), Vector3.UnitY);
        AddQuad(level, sec, new(2f, 4f, -1f), new(-2f, 4f, -1f), new(-2f, 4f, 3f), new(2f, 4f, 3f), -Vector3.UnitY);
        AddQuad(level, sec, new(-2f, 4f, -1f), new(-2f, -2f, -1f), new(-2f, -2f, 3f), new(-2f, 4f, 3f), Vector3.UnitX);
        AddQuad(level, sec, new(2f, -2f, -1f), new(2f, 4f, -1f), new(2f, 4f, 3f), new(2f, -2f, 3f), -Vector3.UnitX);
        AddQuad(level, sec, new(-2f, 4f, 3f), new(2f, 4f, 3f), new(2f, -2f, 3f), new(-2f, -2f, 3f), -Vector3.UnitZ);

        // Top horizontal ledge Y in [-2, 0] @ Z = 1.0
        AddQuad(level, sec, new(-2f, -2f, 1f), new(2f, -2f, 1f), new(2f, 0f, 1f), new(-2f, 0f, 1f), Vector3.UnitZ, surfaceFlags: 0x5);
        // 26.6-degree downward ramp Y in [0, 2] from Z = 1.0 down to Z = 0.0 (normal = (0, 1, 2) normalized; Z = 0.894 >= 0.6)
        AddQuad(level, sec, new(-2f, 0f, 1f), new(2f, 0f, 1f), new(2f, 2f, 0f), new(-2f, 2f, 0f), new Vector3(0f, 1f, 2f), surfaceFlags: 0x5);
        // Bottom catch floor Y in [2, 4] @ Z = -1.0 (1.0 JKU cliff drop past ramp edge!)
        AddQuad(level, sec, new(-2f, 2f, -1f), new(2f, 2f, -1f), new(2f, 4f, -1f), new(-2f, 4f, -1f), Vector3.UnitZ, surfaceFlags: 0x5);

        var phys = new PlayerPhysics(new Vector3(0f, -0.5f, 1f), startSector: 0)
        {
            MoveSpeed = 2.0f
        };

        const float dt = 1.0f / 30.0f;
        for (int i = 0; i < 6; i++)
            phys.Update(dt, Vector3.Zero, wishJump: false, level);
        Assert.True(phys.IsGrounded);

        // Walk forward (+Y) down the entire ramp at 30 Hz: vertical step-down per tick is ~0.033 JKU (> 0.03 old snap, <= 0.06 MaxStepHeight)
        bool detachedOnRamp = false;
        while (phys.Position.Y < 1.85f)
        {
            phys.Update(dt, Vector3.UnitY, wishJump: false, level);
            if (phys.Position.Y is >= 0.1f and <= 1.85f && !phys.IsGrounded)
                detachedOnRamp = true;
        }
        Assert.False(detachedOnRamp, $"Player launched off downward ramp instead of staying grounded (pos={phys.Position}).");

        // Keep walking off the vertical cliff at Y = 2.0 -> must detach and fall naturally
        bool detachedOverDrop = false;
        for (int i = 0; i < 15; i++)
        {
            phys.Update(dt, Vector3.UnitY, wishJump: false, level);
            if (!phys.IsGrounded && phys.Position.Y > 2.08f)
            {
                detachedOverDrop = true;
                break;
            }
        }
        Assert.True(detachedOverDrop, $"Player failed to fall off sheer cliff past Y=2.0 (pos={phys.Position}, grounded={phys.IsGrounded}).");
    }

    [Theory]
    [InlineData("m10.jkl")]
    [InlineData("m2.jkl")]
    public void RetailMpLevels_BotsStayGroundedWithBoundedSpeedOver5Seconds(string mapFile)
    {
        using var vfs = TryMountRetailMpVfs();
        if (vfs == null)
            return;

        JklLevel level = JklReader.Parse(vfs.ReadAllText($"jkl/{mapFile}"), Path.GetFileNameWithoutExtension(mapFile));
        using var server = new DedicatedGameServer(level, vfs, "JK1MP.GOB", mapFile, port: 0, maxPlayers: 6, tickRateHz: 30, defaultAllWeapons: true);

        int[] bots =
        [
            server.AddBot("GroundBot1", BotSkill.Hard, grantAllWeapons: true, rng: new Random(101)),
            server.AddBot("GroundBot2", BotSkill.Hard, grantAllWeapons: true, rng: new Random(102)),
            server.AddBot("GroundBot3", BotSkill.Medium, grantAllWeapons: true, rng: new Random(103)),
        ];

        const float dt = 1.0f / 30.0f;
        var prevPositions = bots.Select(id => level.Things[id].Position).ToArray();
        var prevAlive = bots.Select(_ => true).ToArray();
        var prevGrounded = bots.Select(_ => false).ToArray();
        var airTicks = new int[bots.Length];
        var groundedAirTicks = new int[bots.Length];
        var jumpLaunches = new int[bots.Length];
        var upwardAirborneTicks = new int[bots.Length];
        var maxHorizSpeed = new float[bots.Length];
        var maxDispFromStart = new float[bots.Length];
        Vector3[] startPositions = [.. prevPositions];

        for (int tick = 0; tick < 150; tick++) // 5.0 seconds at 30 Hz
        {
            server.Tick(dt);

            for (int b = 0; b < bots.Length; b++)
            {
                PlayerState? ps = server.Simulation.GetPlayerByThing(bots[b]);
                Assert.NotNull(ps);
                Vector3 curPos = level.Things[bots[b]].Position;

                if (!ps!.Inventory.IsDead)
                {
                    if (!ps.IsUnderwater)
                    {
                        airTicks[b]++;
                        if (ps.IsGrounded)
                        {
                            groundedAirTicks[b]++;
                        }
                        else
                        {
                            if (prevAlive[b] && prevGrounded[b] && curPos.Z > prevPositions[b].Z + 0.015f)
                                jumpLaunches[b]++;
                            if (prevAlive[b] && curPos.Z > prevPositions[b].Z + 0.002f)
                                upwardAirborneTicks[b]++;
                        }
                    }

                    if (prevAlive[b] && Vector3.DistanceSquared(curPos, prevPositions[b]) < 1.5f)
                    {
                        float speed = Vector2.Distance(
                            new Vector2(prevPositions[b].X, prevPositions[b].Y),
                            new Vector2(curPos.X, curPos.Y)) / dt;
                        maxHorizSpeed[b] = MathF.Max(maxHorizSpeed[b], speed);
                    }

                    maxDispFromStart[b] = MathF.Max(maxDispFromStart[b], Vector3.Distance(curPos, startPositions[b]));
                }

                prevPositions[b] = curPos;
                prevAlive[b] = !ps.Inventory.IsDead;
                prevGrounded[b] = ps.IsGrounded;
            }
        }

        int totalAirTicks = 0;
        int totalGroundedAirTicks = 0;
        for (int b = 0; b < bots.Length; b++)
        {
            totalAirTicks += airTicks[b];
            totalGroundedAirTicks += groundedAirTicks[b];
            double groundedRatio = airTicks[b] > 0 ? groundedAirTicks[b] / (double)airTicks[b] : 1.0;
            _output.WriteLine($"{mapFile} bot[{b}]: grounded={groundedAirTicks[b]}/{airTicks[b]} ({groundedRatio:P1}), jumps={jumpLaunches[b]}, upAir={upwardAirborneTicks[b]}, maxHorizSpeed={maxHorizSpeed[b]:F2} JKU/s, maxDisp={maxDispFromStart[b]:F2}");

            Assert.True(maxDispFromStart[b] > 0.25f, $"{mapFile} bot[{b}] failed to navigate.");
            Assert.True(maxHorizSpeed[b] < 3.5f, $"{mapFile} bot[{b}] exceeded realistic max speed: {maxHorizSpeed[b]:F2} JKU/s");
            Assert.True(jumpLaunches[b] <= 4, $"{mapFile} bot[{b}] jumped too frequently: {jumpLaunches[b]} jumps in 5s.");
            Assert.True(upwardAirborneTicks[b] <= 48, $"{mapFile} bot[{b}] spent too many ticks moving upward airborne: {upwardAirborneTicks[b]} ticks.");
        }

        double aggregateGroundedRatio = totalAirTicks > 0 ? totalGroundedAirTicks / (double)totalAirTicks : 1.0;
        Assert.True(aggregateGroundedRatio >= 0.60, $"{mapFile} aggregate bot grounded ratio too low: {aggregateGroundedRatio:P1}");
    }

    [Fact]
    public void DmBotController_SkipsUnreachableMultiStoryPickups_ViaCanReach()
    {
        // Two-sector level where Sector 1 is on a sheer 2.0 JKU cliff above Sector 0 (no walkable/jumpable path 0 -> 1)
        var level = new JklLevel { Name = "multi_story_reachability" };
        var lower = new JklSector { SectorIndex = 0, BoundBoxMin = new Vector3(-4f, -4f, 0f), BoundBoxMax = new Vector3(4f, 4f, 2f) };
        var upper = new JklSector { SectorIndex = 1, BoundBoxMin = new Vector3(-1f, -1f, 2f), BoundBoxMax = new Vector3(1f, 1f, 4f) };
        level.Sectors.Add(lower);
        level.Sectors.Add(upper);
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 0, Flags = 0x7, Mirror = 1 });
        level.Adjoins.Add(new JklAdjoin { AdjoinIndex = 1, Flags = 0x7, Mirror = 0 });

        // Lower floor [-4..4, -4..4] @ Z = 0, ceiling portal @ Z = 2
        AddQuad(level, lower, new(-4f, -4f, 0f), new(4f, -4f, 0f), new(4f, 4f, 0f), new(-4f, 4f, 0f), Vector3.UnitZ, 0x5);
        JklSurface lowerToUpperPortal = AddQuad(level, lower, new(-1f, 1f, 2f), new(1f, 1f, 2f), new(1f, -1f, 2f), new(-1f, -1f, 2f), -Vector3.UnitZ);
        AddQuad(level, lower, new(-4f, -4f, 0f), new(4f, -4f, 0f), new(4f, -4f, 2f), new(-4f, -4f, 2f), Vector3.UnitY);
        AddQuad(level, lower, new(4f, 4f, 0f), new(-4f, 4f, 0f), new(-4f, 4f, 2f), new(4f, 4f, 2f), -Vector3.UnitY);
        AddQuad(level, lower, new(-4f, 4f, 0f), new(-4f, -4f, 0f), new(-4f, -4f, 2f), new(-4f, 4f, 2f), Vector3.UnitX);
        AddQuad(level, lower, new(4f, -4f, 0f), new(4f, 4f, 0f), new(4f, 4f, 2f), new(4f, -4f, 2f), -Vector3.UnitX);

        // Upper room [-1..1, -1..1] @ Z = 2.0..4.0 with sheer portal at Z = 2.0 (unreachable upward from Sector 0 floor)
        JklSurface upperPortal = AddQuad(level, upper, new(-1f, -1f, 2f), new(1f, -1f, 2f), new(1f, 1f, 2f), new(-1f, 1f, 2f), Vector3.UnitZ, 0x5);
        AddQuad(level, upper, new(-1f, 1f, 4f), new(1f, 1f, 4f), new(1f, -1f, 4f), new(-1f, -1f, 4f), -Vector3.UnitZ);
        lowerToUpperPortal.AdjoinIndex = 0;
        lowerToUpperPortal.MirrorSector = upper;
        lowerToUpperPortal.MirrorSurface = upperPortal;
        upperPortal.AdjoinIndex = 1;
        upperPortal.MirrorSector = lower;
        upperPortal.MirrorSurface = lowerToUpperPortal;

        var nav = new SectorPortalNavGraph(level);
        Assert.True(nav.CanReach(0, 0));
        Assert.False(nav.CanReach(0, 1));

        // Put a closer pickup in unreachable Sector 1 directly above Y=+0.5, and a further reachable pickup at X=+3.0 in Sector 0
        level.Things.Add(new JklThing { ThingIndex = 0, TemplateName = "walkplayer", SectorIndex = 0, Position = Vector3.Zero });
        using var vfs = new VirtualFileSystem();
        var sim = new WorldSimulation(level, vfs, isMultiplayer: true, isDedicatedServer: true);
        PlayerState bot = sim.AddPlayer(0, "ReachBot");
        bot.Inventory.Health = 30f; // urgent health seek
        sim.UpdatePlayerInput(bot.ThingIndex, new Vector3(0f, 0f, 0f), 0, Vector3.UnitY, isGrounded: true);

        var ctrl = new DmBotController(DmBotController.DefaultArchetypes[0], BotSkill.Medium, new Random(3));
        var pickups = new List<WorldSimulation.PickupInfo>
        {
            new(ThingIndex: 10, Position: new Vector3(0f, 0.5f, 2.05f), SectorIndex: 1, TemplateName: "healthpack", WeaponBin: -1, IsHealth: true, IsShield: false, IsAmmo: false, IsPowerup: false),
            new(ThingIndex: 11, Position: new Vector3(3.0f, 0.0f, 0.05f), SectorIndex: 0, TemplateName: "healthpack", WeaponBin: -1, IsHealth: true, IsShield: false, IsAmmo: false, IsPowerup: false),
        };

        // First tick settles weapon-swap trigger release (CurWeaponBin -> default rifle);
        // second tick steers along the reachable ground pickup path.
        _ = ctrl.Decide(0.033f, sim, bot, level.Things[bot.ThingIndex], nav, pickups);
        BotTickDecision dec = ctrl.Decide(0.033f, sim, bot, level.Things[bot.ThingIndex], nav, pickups);
        Assert.True(dec.WishMoveDir.X > 0.7f && MathF.Abs(dec.WishMoveDir.Y) < 0.3f,
            $"Expected bot to steer +X toward reachable ground pickup in Sec 0, got move={dec.WishMoveDir}");
    }

    [Fact]
    public void LevelMeshRenderer_WaterPortalSymmetry_CelAnimation_And_SlideScaling()
    {
        // Pure helper contracts for multi-cel water (8-cel 00Twater2.mat), switches, and SlideWall scale
        Assert.Equal(0, LevelMeshRenderer.ComputeAnimatedCelIndex(8, 0.0f, 6.0f));
        Assert.Equal(3, LevelMeshRenderer.ComputeAnimatedCelIndex(8, 0.55f, 6.0f));
        Assert.Equal(0, LevelMeshRenderer.ComputeAnimatedCelIndex(8, 8.0f / 6.0f + 0.01f, 6.0f));
        Assert.Equal(0, LevelMeshRenderer.ComputeAnimatedCelIndex(8, 2.0f, 0.0f));

        Assert.True(LevelMeshRenderer.ShouldAutoAnimateTranslucentMaterial("00twater2.mat", isTranslucent: true, celCount: 8));
        Assert.False(LevelMeshRenderer.ShouldAutoAnimateTranslucentMaterial("00aswit1.mat", isTranslucent: false, celCount: 2));
        Assert.False(LevelMeshRenderer.ShouldAutoAnimateTranslucentMaterial("00aswit1.mat", isTranslucent: true, celCount: 2));
        Assert.False(LevelMeshRenderer.ShouldAutoAnimateTranslucentMaterial("numbers.mat", isTranslucent: false, celCount: 10));

        Vector2 scaledScroll = LevelMeshRenderer.ComputeSlideWallUvVelocity(new Vector2(-1f, 0.5f), 2.0f);
        Assert.Equal(-0.5f, scaledScroll.X, precision: 4);
        Assert.Equal(0.25f, scaledScroll.Y, precision: 4);

        using var vfs = TryMountRetailMpVfs();
        if (vfs == null)
            return;

        // m10.jkl paired water portal: underwater ceiling face (470) vs above-water top face (484)
        JklLevel m10 = JklReader.Parse(vfs.ReadAllText("jkl/m10.jkl"), "m10");
        JklSurface surf470 = m10.Surfaces[470];
        JklSurface surf484 = m10.Surfaces[484];

        Assert.Same(surf484, surf470.MirrorSurface);
        Assert.Same(surf470, surf484.MirrorSurface);
        Assert.True(LevelMeshRenderer.HasDrawnMirror(surf470));
        Assert.True(LevelMeshRenderer.HasDrawnMirror(surf484));
        Assert.False(LevelMeshRenderer.IsDoubleSidedSurface(surf470));
        Assert.False(LevelMeshRenderer.IsDoubleSidedSurface(surf484));
        Assert.True(LevelMeshRenderer.IsTranslucentSurface(surf484));

        // Preserving Surf 484 keeps bright above-water ExtraLight (1.0f) instead of dark underwater Sec 42 light (< 0.5f)
        float topIntensity = LevelMeshRenderer.ComputeVertexIntensity(surf484, 0, m10.Sectors[43].ExtraLight);
        float undersideIntensity = LevelMeshRenderer.ComputeVertexIntensity(surf470, 0, m10.Sectors[42].ExtraLight);
        Assert.Equal(1.0f, topIntensity, precision: 4);
        Assert.True(undersideIntensity < 0.5f, $"Expected dark underwater surface light < 0.5, got {undersideIntensity}");

        // m_boss17.jkl one-sided 13fwater.mat portal has undrawn mirror -> remains double-sided
        JklLevel mboss17 = JklReader.Parse(vfs.ReadAllText("jkl/m_boss17.jkl"), "m_boss17");
        JklSurface s422 = mboss17.Surfaces[422];
        Assert.False(LevelMeshRenderer.HasDrawnMirror(s422));
        Assert.True(LevelMeshRenderer.IsDoubleSidedSurface(s422));
        Assert.True(LevelMeshRenderer.IsTranslucentSurface(s422));
    }

    [Fact]
    public async Task DedicatedServer_RetailM10_ReplicatesSurfaceScrolls_And_UnderwaterSectorTint()
    {
        using var vfs = TryMountRetailMpVfs();
        if (vfs == null)
            return;

        JklLevel m10 = JklReader.Parse(vfs.ReadAllText("jkl/m10.jkl"), "m10");
        using var server = new DedicatedGameServer(m10, vfs, "JK1MP.GOB", "m10.jkl", port: 0, maxPlayers: 4, tickRateHz: 30);

        using var client = new MultiplayerClient();
        ServerWelcomePacket welcome = await ConnectWithServerTickAsync(server, client, "WaterTester");

        var events = new List<NetWorldEvent>();
        Vector3 curPos = welcome.SpawnPosition;
        int curSector = welcome.SpawnSector;
        uint actionSeq = 1;

        // Step several ticks so all >16 startup SurfaceScroll events flush across UDP snapshots with EventSeq acks
        for (int i = 0; i < 8; i++)
        {
            client.SendInput(new ClientInputPacket
            {
                Flags = ClientInputFlags.IsGrounded,
                Position = curPos,
                Sector = curSector,
                AimDirection = Vector3.UnitY,
                ActionSeq = unchecked((byte)actionSeq++)
            });
            server.Tick(0.033f);
            await Task.Delay(5);
            client.PollSnapshots(events, 0.033f, out _);
        }

        var scrollEvents = events.Where(e => e.Kind == NetEventKind.SurfaceScroll).ToList();
        Assert.Equal(17, scrollEvents.Count);
        Assert.Contains(scrollEvents, e => e.IntParam0 == 484 && MathF.Abs(e.Position.Z - 1.0f) < 0.01f);
        Assert.Contains(scrollEvents, e => e.IntParam0 == 34);
        Assert.Contains(scrollEvents, e => e.IntParam0 == 539);

        // Move client smoothly into underwater Sector 42 (m10 pool with blue Tint 0.40, 0.80, 1.00) respecting MaxClientStepJumpDistance
        JklSector sec42 = m10.Sectors[42];
        Vector3 underwaterPos = (sec42.BoundBoxMin + sec42.BoundBoxMax) * 0.5f;
        Assert.True(PlayerPhysics.IsPointInsideSector(underwaterPos, sec42, m10));

        const int moveSteps = 6;
        for (int step = 1; step <= moveSteps; step++)
        {
            Vector3 p = Vector3.Lerp(welcome.SpawnPosition, underwaterPos, step / (float)moveSteps);
            int sec = PlayerPhysics.FindSectorAtPosition(p, curSector, m10);
            curSector = sec;

            client.SendInput(new ClientInputPacket
            {
                Flags = ClientInputFlags.IsUnderwater,
                Position = p,
                Sector = sec,
                AimDirection = Vector3.UnitY,
                ActionSeq = unchecked((byte)actionSeq++)
            });
            server.Tick(0.033f);
            await Task.Delay(5);
            client.PollSnapshots(events, 0.033f, out _);
        }

        // One more tick ensures snapshot reflects Sector 42
        client.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsUnderwater,
            Position = underwaterPos,
            Sector = 42,
            AimDirection = Vector3.UnitY,
            ActionSeq = unchecked((byte)actionSeq++)
        });
        server.Tick(0.033f);
        await Task.Delay(5);
        client.PollSnapshots(events, 0.033f, out _);

        ServerSnapshotPacket? snap = client.LatestSnapshot;
        Assert.NotNull(snap);
        Assert.NotEqual(0, (int)(snap!.Local.Flags & LocalPlayerSnapFlags.HasScreenTint));
        Assert.True(snap.Local.ScreenTint.Z > snap.Local.ScreenTint.X && snap.Local.ScreenTint.Z > 0.1f,
            $"Expected aqua blue underwater tint from m10 Sec 42, got {snap.Local.ScreenTint}");
    }

    [Fact]
    public void NetCodec_RoundTripsAirborneFlagAndScrollMaterialAnimEvents()
    {
        var pkt = new ServerSnapshotPacket
        {
            ServerTick = 42,
            LevelTime = 1.5f,
            LocalThingIndex = 0
        };
        pkt.Players.Add(new NetPlayerSnapshot(
            ThingIndex: 1,
            PlayerSlot: 1,
            Team: 0,
            Name: "Bot_Kyle",
            Position: new Vector3(1f, 2f, 3f),
            Pitch: 0f,
            Yaw: 45f,
            Health: 100,
            WeaponBin: 2,
            AnimFlags: RemotePlayerAnimFlags.IsMoving | RemotePlayerAnimFlags.IsAirborne,
            DistanceMoved: 0.04f,
            Kills: 0,
            Deaths: 0,
            Score: 0));
        pkt.Events.Add(new NetWorldEvent(1, NetEventKind.SurfaceScroll, new Vector3(-1f, 0f, 1.5f), 0f, 0f, 484, string.Empty, string.Empty));
        pkt.Events.Add(new NetWorldEvent(2, NetEventKind.MaterialAnim, Vector3.Zero, 6.0f, 0f, 0, "00Twater2.mat", string.Empty));

        byte[] buf = new byte[NetCodec.MaxPacketBytes];
        Assert.True(NetCodec.TryWriteServerSnapshot(buf, pkt, out int written));
        Assert.True(NetCodec.TryReadServerSnapshot(buf.AsSpan(0, written), out ServerSnapshotPacket? decoded));
        Assert.NotNull(decoded);

        Assert.True((decoded!.Players[0].AnimFlags & RemotePlayerAnimFlags.IsAirborne) != 0);
        Assert.Equal(2, decoded.Events.Count);
        Assert.Equal(NetEventKind.SurfaceScroll, decoded.Events[0].Kind);
        Assert.Equal(484, decoded.Events[0].IntParam0);
        Assert.Equal(new Vector3(-1f, 0f, 1.5f), decoded.Events[0].Position);
        Assert.Equal(NetEventKind.MaterialAnim, decoded.Events[1].Kind);
        Assert.Equal("00Twater2.mat", decoded.Events[1].Text0);
        Assert.Equal(6.0f, decoded.Events[1].FloatParam0, precision: 3);
    }
}
