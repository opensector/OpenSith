using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.AI;
using OpenSith.Core.Animation;
using OpenSith.Core.Cog;
using OpenSith.Core.Models;
using OpenSith.Core.Networking;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Regression tests verifying two-track skeletal layered animations (running legs preserved while firing/swinging),
/// directional locomotion resolution, archetype weapon diversity, controlled repeater/concussion cadence,
/// and alignment-restricted Force Lightning gating.
/// </summary>
public sealed class BotAnimationAndWeaponBalanceTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    private sealed class InMemoryVfs : IVirtualFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddText(string path, string content) =>
            _files[Normalize(path)] = System.Text.Encoding.ASCII.GetBytes(content);

        public bool FileExists(string path) => _files.ContainsKey(Normalize(path));
        public byte[] ReadAllBytes(string path) => _files[Normalize(path)];
        public Stream OpenRead(string path) => new MemoryStream(_files[Normalize(path)], writable: false);
        public string ReadAllText(string path) => System.Text.Encoding.ASCII.GetString(_files[Normalize(path)]);
        public void Dispose() { }

        private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    }

    private static JklLevel CreateBoxArena(float halfExtent = 6.0f, float height = 3.0f)
    {
        var level = new JklLevel { Name = "anim_balance_arena" };
        level.Materials.Add("wall0.mat");

        float h = halfExtent;
        level.Vertices.AddRange([
            new Vector3(-h, -h, 0f),
            new Vector3( h, -h, 0f),
            new Vector3( h,  h, 0f),
            new Vector3(-h,  h, 0f),
            new Vector3(-h, -h, height),
            new Vector3( h, -h, height),
            new Vector3( h,  h, height),
            new Vector3(-h,  h, height),
        ]);

        var surfaces = new[]
        {
            new JklSurface { SurfaceIndex = 0, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [0, 1, 2, 3], Normal = Vector3.UnitZ, SurfaceFlags = 0x5 },
            new JklSurface { SurfaceIndex = 1, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [7, 6, 5, 4], Normal = -Vector3.UnitZ, SurfaceFlags = 0x4 },
            new JklSurface { SurfaceIndex = 2, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [3, 2, 6, 7], Normal = -Vector3.UnitY, SurfaceFlags = 0x4 },
            new JklSurface { SurfaceIndex = 3, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [0, 4, 5, 1], Normal = Vector3.UnitY, SurfaceFlags = 0x4 },
            new JklSurface { SurfaceIndex = 4, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [1, 5, 6, 2], Normal = -Vector3.UnitX, SurfaceFlags = 0x4 },
            new JklSurface { SurfaceIndex = 5, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [3, 7, 4, 0], Normal = Vector3.UnitX, SurfaceFlags = 0x4 },
        };
        level.Surfaces.AddRange(surfaces);
        level.Sectors.Add(new JklSector
        {
            SectorIndex = 0,
            BoundBoxMin = new Vector3(-h, -h, 0f),
            BoundBoxMax = new Vector3(h, h, height),
            Surfaces = [.. surfaces]
        });

        for (int i = 0; i < 8; i++)
        {
            float angle = (i / 8f) * MathF.Tau;
            level.Things.Add(new JklThing
            {
                ThingIndex = i,
                TemplateName = "walkplayer",
                SectorIndex = 0,
                Position = new Vector3(MathF.Cos(angle) * 3.0f, MathF.Sin(angle) * 3.0f, 0f),
                PitchYawRoll = new Vector3(0f, i * 45f, 0f)
            });
        }

        return level;
    }

    private static PlayerState CreateLoadedPlayer(WorldSimulation sim, int slot, string name)
    {
        PlayerState p = sim.AddPlayer(slot, name);
        p.Inventory.SetInv(PlayerInventory.BinFists, 1f);
        p.Inventory.SetInv(PlayerInventory.BinBryar, 1f);
        p.Inventory.SetInv(PlayerInventory.BinStRifle, 1f);
        p.Inventory.SetInv(PlayerInventory.BinThermDet, 10f);
        p.Inventory.SetInv(PlayerInventory.BinBowcaster, 1f);
        p.Inventory.SetInv(PlayerInventory.BinRepeater, 1f);
        p.Inventory.SetInv(PlayerInventory.BinRailDet, 1f);
        p.Inventory.SetInv(PlayerInventory.BinSeqCharge, 5f);
        p.Inventory.SetInv(PlayerInventory.BinConcRifle, 1f);
        p.Inventory.SetInv(PlayerInventory.BinLightsaber, 1f);
        p.Inventory.EnergyAmmo = 300f;
        p.Inventory.PowerAmmo = 300f;
        p.Inventory.RailCharges = 20f;
        return p;
    }

    [Fact]
    public void KeyframeReader_And_ThreeDoHierarchy_LayeredOverlay_KeepsRunningLegsWhileUpperBodyFires()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path))
            return;

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));

        ThreeDoModel ky = ThreeDoReader.Parse(vfs.ReadAllText("3do/ky.3do"));
        KeyframeTrack kyrun2 = KeyframeReader.Parse(vfs.ReadAllText("3do/key/kyrun2.key"), "kyrun2.key");
        KeyframeTrack kyfire = KeyframeReader.Parse(vfs.ReadAllText("3do/key/kyfire.key"), "kyfire.key");
        KeyframeTrack kysabrf1 = KeyframeReader.Parse(vfs.ReadAllText("3do/key/kysabrf1.key"), "kysabrf1.key");

        Assert.Equal(0xFFFF, kyrun2.NodeMask);
        Assert.Equal(0xF, kyfire.NodeMask);
        Assert.Equal(0xF, kysabrf1.NodeMask);

        const float runTimeA = 0.0f;
        const float runTimeB = 4.6f; // ~55 frames into 15fps run cycle -> opposite stride

        Matrix4x4[] pureRunA = ThreeDoHierarchy.ComputeTransforms(ky, kyrun2, runTimeA);
        Matrix4x4[] pureRunB = ThreeDoHierarchy.ComputeTransforms(ky, kyrun2, runTimeB);
        Matrix4x4[] layeredFireA = ThreeDoHierarchy.ComputeTransforms(ky, kyrun2, runTimeA, kyfire, 0.1f);
        Matrix4x4[] layeredFireB = ThreeDoHierarchy.ComputeTransforms(ky, kyrun2, runTimeB, kyfire, 0.1f);
        Matrix4x4[] layeredSaberB = ThreeDoHierarchy.ComputeTransforms(ky, kyrun2, runTimeB, kysabrf1, 0.25f);
        Matrix4x4[] pureFire = ThreeDoHierarchy.ComputeTransforms(ky, kyfire, 0.1f);
        Matrix4x4[] pureSaber = ThreeDoHierarchy.ComputeTransforms(ky, kysabrf1, 0.25f);

        int[] legNodes = [3, 4, 8, 9, 10, 14];
        bool legMovedBetweenStrides = false;
        foreach (int nodeIdx in legNodes)
        {
            Assert.True(Vector3.Distance(layeredFireA[nodeIdx].Translation, pureRunA[nodeIdx].Translation) < 1e-4f,
                $"Leg node {nodeIdx} at t={runTimeA} diverged from base run track.");
            Assert.True(Vector3.Distance(layeredFireB[nodeIdx].Translation, pureRunB[nodeIdx].Translation) < 1e-4f,
                $"Leg node {nodeIdx} at t={runTimeB} diverged from base run track.");
            Assert.True(Vector3.Distance(layeredSaberB[nodeIdx].Translation, pureRunB[nodeIdx].Translation) < 1e-4f,
                $"Leg node {nodeIdx} under saber overlay diverged from base run track.");

            if (Vector3.Distance(layeredFireB[nodeIdx].Translation, layeredFireA[nodeIdx].Translation) > 0.015f)
                legMovedBetweenStrides = true;
        }
        Assert.True(legMovedBetweenStrides, "Expected lower-body legs to animate through stride while firing overlay is active.");

        // Upper-body right hand (12) and torso (17) should adopt overlay rotations (matching upper 3x3 orientation)
        Assert.True(Vector3.Distance(layeredFireB[12].Translation, pureFire[12].Translation) < 0.005f,
            "Right hand (node 12) should follow kyfire overlay pose while running.");
        Assert.True(Vector3.Distance(layeredSaberB[12].Translation, pureSaber[12].Translation) < 0.005f,
            "Right hand (node 12) should follow kysabrf1 saber swing overlay pose while running.");
        Assert.True(Vector3.Distance(layeredSaberB[12].Translation, pureRunB[12].Translation) > 0.01f,
            "Saber swing overlay should meaningfully alter upper-body hand pose relative to plain run.");
    }

    [Fact]
    public void ThingMeshRenderer_ResolvePlayerAnimationState_LayersFireOverLocomotion_AndHandlesDirectionsAndHysteresis()
    {
        // Gun + moving + firing -> mode 1, run base + fire upper-body overlay
        var runGunFire = ThingMeshRenderer.ResolvePlayerAnimationState(
            isDead: false,
            isUnderwater: false,
            isAirborneSustained: false,
            isMoving: true,
            isFiring: true,
            weaponBin: PlayerInventory.BinRepeater,
            horizVelocity: new Vector2(0f, 1.5f),
            yawDeg: 0f);
        Assert.Equal(1, runGunFire.Mode);
        Assert.Equal("run", runGunFire.BaseAction);
        Assert.True(runGunFire.BaseLoop);
        Assert.Equal("fire", runGunFire.OverlayActionOrKey);

        // Saber + moving + firing -> mode 2, run base + kysabrf1.key overlay
        var runSaberFire = ThingMeshRenderer.ResolvePlayerAnimationState(
            isDead: false,
            isUnderwater: false,
            isAirborneSustained: false,
            isMoving: true,
            isFiring: true,
            weaponBin: PlayerInventory.BinLightsaber,
            horizVelocity: new Vector2(0f, 1.5f),
            yawDeg: 0f);
        Assert.Equal(2, runSaberFire.Mode);
        Assert.Equal("run", runSaberFire.BaseAction);
        Assert.Equal("kysabrf1.key", runSaberFire.OverlayActionOrKey);

        // Fists + moving + firing -> mode 0, run base + fire overlay
        var runFistsFire = ThingMeshRenderer.ResolvePlayerAnimationState(
            isDead: false,
            isUnderwater: false,
            isAirborneSustained: false,
            isMoving: true,
            isFiring: true,
            weaponBin: PlayerInventory.BinFists,
            horizVelocity: new Vector2(0f, 1.2f),
            yawDeg: 0f);
        Assert.Equal(0, runFistsFire.Mode);
        Assert.Equal("run", runFistsFire.BaseAction);
        Assert.Equal("fire", runFistsFire.OverlayActionOrKey);

        // Directional locomotion relative to yawDeg = 0 (+Y forward, +X right)
        var strafeRight = ThingMeshRenderer.ResolvePlayerAnimationState(false, false, false, true, false, PlayerInventory.BinBryar, new Vector2(1.4f, 0f), 0f);
        var strafeLeft = ThingMeshRenderer.ResolvePlayerAnimationState(false, false, false, true, false, PlayerInventory.BinBryar, new Vector2(-1.4f, 0f), 0f);
        var walkBack = ThingMeshRenderer.ResolvePlayerAnimationState(false, false, false, true, false, PlayerInventory.BinBryar, new Vector2(0f, -1.4f), 0f);
        Assert.Equal("straferight", strafeRight.BaseAction);
        Assert.Equal("strafeleft", strafeLeft.BaseAction);
        Assert.Equal("walkback", walkBack.BaseAction);

        // Sustained airborne -> fall + optional overlay if firing
        var fall = ThingMeshRenderer.ResolvePlayerAnimationState(false, false, true, true, false, PlayerInventory.BinBryar, new Vector2(0f, 1.4f), 0f);
        Assert.Equal("fall", fall.BaseAction);
        Assert.Null(fall.OverlayActionOrKey);

        // Dead -> death non-looping, no overlay
        var dead = ThingMeshRenderer.ResolvePlayerAnimationState(true, false, true, true, true, PlayerInventory.BinBryar, new Vector2(0f, 1.4f), 0f);
        Assert.Equal("death", dead.BaseAction);
        Assert.False(dead.BaseLoop);
        Assert.Null(dead.OverlayActionOrKey);
    }

    [Fact]
    public void DmBotController_AllWeaponsLoadout_RespectsArchetypeDiversity()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        var sim = new WorldSimulation(level, vfs, isMultiplayer: true, isDedicatedServer: true);
        PlayerState p = CreateLoadedPlayer(sim, 0, "DiversityTest");

        var distinctWeapons = new HashSet<int>();
        foreach (BotArchetype arch in DmBotController.DefaultArchetypes)
        {
            var ctrl = new DmBotController(arch, BotSkill.Medium, new Random(42));
            int selected = ctrl.SelectBestWeapon(p.Inventory, targetDist: 2.2f, hasLos: true);
            distinctWeapons.Add(selected);
            Assert.Equal(arch.FavoriteWeaponBin, selected);
        }

        Assert.True(distinctWeapons.Count >= 6,
            $"Expected >= 6 distinct weapons across the 8 archetypes with all weapons, got {distinctWeapons.Count}.");
    }

    [Fact]
    public void DmBotController_RepeaterAndConcussion_UseControlledBursts_AndAvoidModeFlicker()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        var sim = new WorldSimulation(level, vfs, isMultiplayer: true, isDedicatedServer: true);
        var nav = new SectorPortalNavGraph(level);
        var pickups = new List<WorldSimulation.PickupInfo>();

        const float dt = 1.0f / 30.0f;
        const int totalTicks = 180; // 6.0 seconds at 30 Hz

        // 1. Repeater bot burst discipline
        PlayerState repBot = sim.AddPlayer(0, "GreedoBot");
        PlayerState target = sim.AddPlayer(1, "Target");
        repBot.Inventory.SetInv(PlayerInventory.BinRepeater, 1f);
        repBot.Inventory.PowerAmmo = 500f;
        repBot.Inventory.HasBryar = false;
        repBot.Inventory.HasLightsaber = false;
        sim.SelectWeapon(repBot.ThingIndex, PlayerInventory.BinRepeater);
        sim.UpdatePlayerInput(repBot.ThingIndex, Vector3.Zero, 0, Vector3.UnitY);
        sim.UpdatePlayerInput(target.ThingIndex, new Vector3(0f, 2.0f, 0f), 0, -Vector3.UnitY);

        var repCtrl = new DmBotController(DmBotController.DefaultArchetypes[2], BotSkill.Medium, new Random(11));
        int repFiringTicks = 0;
        for (int t = 0; t < totalTicks; t++)
        {
            BotTickDecision dec = repCtrl.Decide(dt, sim, repBot, level.Things[repBot.ThingIndex], nav, pickups);
            if (dec.PrimaryFireHeld || dec.SecondaryFireHeld)
                repFiringTicks++;
        }
        double repDutyCycle = repFiringTicks / (double)totalTicks;
        Assert.InRange(repDutyCycle, 0.15, 0.55);

        // 2. Concussion rifle bot spacing and zero adjacent-tick mode flicker
        PlayerState concBot = sim.AddPlayer(2, "FettBot");
        concBot.Inventory.SetInv(PlayerInventory.BinConcRifle, 1f);
        concBot.Inventory.PowerAmmo = 500f;
        concBot.Inventory.HasBryar = false;
        concBot.Inventory.HasLightsaber = false;
        sim.SelectWeapon(concBot.ThingIndex, PlayerInventory.BinConcRifle);
        sim.UpdatePlayerInput(concBot.ThingIndex, new Vector3(0f, -0.5f, 0f), 0, Vector3.UnitY);

        var concCtrl = new DmBotController(DmBotController.DefaultArchetypes[1], BotSkill.Medium, new Random(22));
        int concFiringTicks = 0;
        bool prevPri = false;
        bool prevSec = false;
        bool modeAlternatedAdjacent = false;
        for (int t = 0; t < totalTicks; t++)
        {
            BotTickDecision dec = concCtrl.Decide(dt, sim, concBot, level.Things[concBot.ThingIndex], nav, pickups);
            if (dec.PrimaryFireHeld || dec.SecondaryFireHeld)
                concFiringTicks++;
            if ((prevPri && dec.SecondaryFireHeld) || (prevSec && dec.PrimaryFireHeld))
                modeAlternatedAdjacent = true;
            prevPri = dec.PrimaryFireHeld;
            prevSec = dec.SecondaryFireHeld;
        }
        double concDutyCycle = concFiringTicks / (double)totalTicks;
        Assert.False(modeAlternatedAdjacent, "Concussion Rifle bot alternated Primary/Secondary modes on adjacent ticks.");
        Assert.True(concDutyCycle > 0.0 && concDutyCycle <= 0.15,
            $"Expected spaced Concussion Rifle fire duty cycle in (0, 0.15], got {concDutyCycle:P1}.");
    }

    [Fact]
    public void DedicatedGameServer_BotForceLoadouts_RestrictLightningToDarkJedi_AndGateRangeAndCooldown()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        using var server = new DedicatedGameServer(level, vfs, "test.gob", "arena.jkl", port: 0, maxPlayers: 8, tickRateHz: 30);

        var lightOrNeutral = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bot_Kyle", "Bot_Fett", "Bot_Greedo", "Bot_Jan" };
        var darkJedi = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bot_Yun", "Bot_Sariss", "Bot_Jerec", "Bot_Boc" };

        var spawnedPlayers = new List<PlayerState>();
        for (int i = 0; i < 8; i++)
        {
            int thingId = server.AddBot(skill: BotSkill.Medium, grantAllWeapons: false, rng: new Random(300 + i));
            Assert.True(thingId >= 0);
            PlayerState? ps = server.Simulation.GetPlayerByThing(thingId);
            Assert.NotNull(ps);
            spawnedPlayers.Add(ps!);
        }

        foreach (PlayerState ps in spawnedPlayers)
        {
            float lightningRank = ps.Inventory.GetInv(PlayerInventory.BinForceLightning);
            if (lightOrNeutral.Contains(ps.Name))
                Assert.Equal(0f, lightningRank);
            else if (darkJedi.Contains(ps.Name))
                Assert.True(lightningRank > 0f, $"Dark Jedi bot '{ps.Name}' should have Force Lightning rank > 0.");
        }

        // Verify Force Lightning range gate (must NOT fire at dist = 2.5 JKU > 1.5 JKU max range) and >= 6s cooldown at close range
        var testLevel = CreateBoxArena();
        var sim = new WorldSimulation(testLevel, vfs, isMultiplayer: true, isDedicatedServer: true);
        var nav = new SectorPortalNavGraph(testLevel);
        var pickups = new List<WorldSimulation.PickupInfo>();

        PlayerState darkBot = sim.AddPlayer(0, "DarkTest");
        PlayerState target = sim.AddPlayer(1, "Victim");
        darkBot.Inventory.ForceRank = 3;
        darkBot.Inventory.SetInv(PlayerInventory.BinForceLightning, 2f);
        darkBot.Inventory.ForceMana = 200f;
        sim.UpdatePlayerInput(darkBot.ThingIndex, Vector3.Zero, 0, Vector3.UnitY);

        var ctrl = new DmBotController(DmBotController.DefaultArchetypes[6], BotSkill.Medium, new Random(77));
        const float dt = 1.0f / 30.0f;

        // 4 seconds out of range (2.5 JKU): zero Lightning casts
        sim.UpdatePlayerInput(target.ThingIndex, new Vector3(0f, 2.5f, 0f), 0, -Vector3.UnitY);
        int outOfRangeCasts = 0;
        for (int t = 0; t < 120; t++)
        {
            BotTickDecision dec = ctrl.Decide(dt, sim, darkBot, testLevel.Things[darkBot.ThingIndex], nav, pickups);
            if (dec.DesiredForceBin == PlayerInventory.BinForceLightning)
                outOfRangeCasts++;
        }
        Assert.Equal(0, outOfRangeCasts);

        // 8 seconds within 1.1 JKU range: at least 1 cast, at most 2 due to >= 6s cooldown
        sim.UpdatePlayerInput(target.ThingIndex, new Vector3(0f, 1.1f, 0f), 0, -Vector3.UnitY);
        int inRangeCasts = 0;
        for (int t = 0; t < 240; t++)
        {
            BotTickDecision dec = ctrl.Decide(dt, sim, darkBot, testLevel.Things[darkBot.ThingIndex], nav, pickups);
            if (dec.DesiredForceBin == PlayerInventory.BinForceLightning)
                inRangeCasts++;
        }
        Assert.InRange(inRangeCasts, 1, 2);
    }
}
