using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// End-to-end regression tests for the retail <c>07yun.jkl</c> Yun Dark Jedi boss battle:
/// saber info/flags, AI pause/unpause, Force Persuasion invisibility, frontal blaster deflection,
/// frontal saber parry vs. flanking/backstab &amp; invisibility bypass, and <c>07_yun.cog</c> level completion.
/// </summary>
public class YunBossBattleTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";
    private readonly ITestOutputHelper _output;

    public YunBossBattleTests(ITestOutputHelper output)
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

    private static WorldSimulation? TryCreateYunSimulation(out JklLevel level)
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null)
        {
            level = null!;
            return null;
        }

        level = JklReader.Parse(vfs.ReadAllText("jkl/07yun.jkl"), "07yun");
        return new WorldSimulation(level, vfs, difficulty: 1);
    }

    private static Vector3 GetThingForward(JklThing thing)
    {
        float yawRad = thing.PitchYawRoll.Y * (MathF.PI / 180f);
        return new Vector3(-MathF.Sin(yawRad), MathF.Cos(yawRad), 0f);
    }

    [Fact]
    public void Retail07Yun_StartupSetsSaberInfoAndFlags_AndAiPauseWorks()
    {
        var sim = TryCreateYunSimulation(out var level);
        if (sim == null) return;

        // darkjedi_yun.cog startup sets Yun (Thing 1) saber info and JkFlags 0x5 (SABERON | SABEREXTEND)
        Assert.True(sim.TryGetThingSaberInfo(1, out var saberInfo));
        Assert.Equal("saberyellow1.mat", saberInfo.SideMat, ignoreCase: true);
        Assert.Equal("saberyellow0.mat", saberInfo.TipMat, ignoreCase: true);
        Assert.Equal(0.100f, saberInfo.Length, precision: 3);
        Assert.Equal(0x5, sim.JkGetFlags(1) & 0x5);

        // AiSetMode(0x2000) pauses Yun in UpdateNpcs; AiClearMode(0x2000) unpauses
        var yun = level.Things[1];
        Vector3 initialPos = yun.Position;
        sim.AiSetMode(1, 0x2000);
        Assert.NotEqual(0, sim.AiGetMode(1) & 0x2000);

        Vector3 playerPos = level.Things[0].Position;
        int playerSector = level.Things[0].SectorIndex;
        for (int i = 0; i < 10; i++)
            sim.Update(0.05f, playerPos, playerSector);

        Assert.Equal(initialPos, yun.Position);
        Assert.Equal("stand", sim.GetNpcAnimAction(1));

        sim.AiClearMode(1, 0x2000);
        Assert.Equal(0, sim.AiGetMode(1) & 0x2000);

        // ActorFlags 0x80 & CurGeoMode 0 hide thing mesh via SithThingFlags.Invisible
        Assert.False(sim.IsThingInvisible(1));
        Assert.True(ThingMeshRenderer.ShouldRenderThing(yun, playerThingIndex: 0));

        sim.SetActorFlags(1, 0x80);
        sim.SetThingCurGeoMode(1, 0);
        Assert.True(sim.IsThingInvisible(1));
        Assert.False(ThingMeshRenderer.ShouldRenderThing(yun, playerThingIndex: 0));

        sim.ClearActorFlags(1, 0x80);
        sim.SetThingCurGeoMode(1, sim.GetThingGeoMode(1));
        Assert.False(sim.IsThingInvisible(1));
        Assert.True(ThingMeshRenderer.ShouldRenderThing(yun, playerThingIndex: 0));
    }

    [Fact]
    public void Retail07Yun_SaberParryFromFront_BypassedByFlankAndForcePersuasion()
    {
        var sim = TryCreateYunSimulation(out var level);
        if (sim == null) return;

        var yun = level.Things[1];
        Vector3 yunCenter = yun.Position + new Vector3(0f, 0f, 0.10f);
        Vector3 yunForward = GetThingForward(yun);
        float startHp = sim.GetThingHealth(1);
        Assert.Equal(500f, startHp, precision: 1);

        // 1. Frontal saber swing -> Yun parries (0 damage, block anim, saber sparks)
        Vector3 frontOrigin = yunCenter + yunForward * 0.25f;
        int hitId = sim.FireWeaponRay(frontOrigin, -yunForward, maxRange: 0.6f, damage: 50f, isMelee: true);
        Assert.Equal(1, hitId);
        Assert.Equal(500f, sim.GetThingHealth(1), precision: 1);
        Assert.Equal("block", sim.GetNpcAnimAction(1));
        Assert.True(sim.Sparks.Count > 0);

        // 2. Flank / backstab swing (dot < fov) -> bypasses parry and deals full damage
        Vector3 backOrigin = yunCenter - yunForward * 0.25f;
        sim.FireWeaponRay(backOrigin, yunForward, maxRange: 0.6f, damage: 50f, isMelee: true);
        Assert.Equal(450f, sim.GetThingHealth(1), precision: 1);

        // 3. Force Persuasion invisibility on Kyle (ActorFlags 0x80) -> bypasses parry even from front!
        sim.SetActorFlags(0, 0x80);
        sim.FireWeaponRay(frontOrigin, -yunForward, maxRange: 0.6f, damage: 50f, isMelee: true);
        Assert.Equal(400f, sim.GetThingHealth(1), precision: 1);
    }

    [Fact]
    public void Retail07Yun_FrontalBlasterBolt_IsDeflectedBack()
    {
        var sim = TryCreateYunSimulation(out var level);
        if (sim == null) return;

        var yun = level.Things[1];
        Vector3 yunCenter = yun.Position + new Vector3(0f, 0f, 0.10f);
        Vector3 yunForward = GetThingForward(yun);
        Vector3 shotOrigin = yunCenter + yunForward * 0.35f;
        Vector3 shotDir = -yunForward;

        // Place Kyle at shotOrigin so deflected bolt heads back toward Kyle
        level.Things[0].Position = shotOrigin - new Vector3(0f, 0f, 0.10f);
        sim.FireWeaponRay(shotOrigin, shotDir, maxRange: 5.0f, damage: 20f, isMelee: false, projectileModel: "bry0.3do", boltSpeed: 8.0f);
        Assert.Single(sim.Projectiles);

        bool deflected = false;
        for (int i = 0; i < 20; i++)
        {
            sim.Update(0.016f, level.Things[0].Position, level.Things[0].SectorIndex);
            if (sim.Projectiles.Count > 0 && sim.Projectiles[0].IsEnemy)
            {
                deflected = true;
                var bolt = sim.Projectiles[0];
                Assert.Equal(1, bolt.OwnerThingIndex);
                Assert.True(Vector3.Dot(bolt.Direction, yunForward) > 0.5f,
                    $"Deflected bolt should travel back toward shooter (dot={Vector3.Dot(bolt.Direction, yunForward):F2})");
                break;
            }
        }

        Assert.True(deflected, "Expected frontal Bryar bolt to be deflected by Yun.");
        Assert.Equal(500f, sim.GetThingHealth(1), precision: 1);
        Assert.Equal("block", sim.GetNpcAnimAction(1));
        Assert.True(sim.Sparks.Count > 0);
    }

    [Fact]
    public void Retail07Yun_KillingYun_OpensOverheadDoorsAndEndsLevel()
    {
        var sim = TryCreateYunSimulation(out var level);
        if (sim == null) return;

        var yun = level.Things[1];
        Vector3 yunCenter = yun.Position + new Vector3(0f, 0f, 0.10f);
        Vector3 yunForward = GetThingForward(yun);
        Vector3 backOrigin = yunCenter - yunForward * 0.25f;

        Vector3[] initialDoorPositions = Enumerable.Range(8, 8)
            .Select(id => level.Things[id].Position)
            .ToArray();

        // Kill Yun with backstab saber swings (bypasses frontal parry)
        sim.FireWeaponRay(backOrigin, yunForward, maxRange: 0.6f, damage: 300f, isMelee: true);
        sim.FireWeaponRay(backOrigin, yunForward, maxRange: 0.6f, damage: 300f, isMelee: true);
        Assert.True(sim.GetThingHealth(1) <= 0f);

        // Step simulation past 07_yun.cog Sleep(3.0) + tridoor MoveToFrame + WaitForStop + Sleep(1.0)
        Vector3 playerPos = level.Things[0].Position;
        int playerSector = level.Things[0].SectorIndex;
        for (int i = 0; i < 120; i++)
        {
            sim.Update(0.1f, playerPos, playerSector);
            if (sim.LevelCompleted)
                break;
        }

        bool anyDoorMoved = Enumerable.Range(8, 8)
            .Any(id => Vector3.Distance(level.Things[id].Position, initialDoorPositions[id - 8]) > 0.01f);

        _output.WriteLine($"LevelCompleted={sim.LevelCompleted}, anyDoorMoved={anyDoorMoved}");
        Assert.True(anyDoorMoved, "Expected ovhdoor0..7 (Things 8..15) to open after Yun is killed.");
        Assert.True(sim.LevelCompleted, "Expected 07_yun.cog killed: to call JkEndLevel(1).");
    }

    [Fact]
    public void Retail_07Yun_CameraThingIsInvisible_AndSkippingIntroMakesYunAttackFirst()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/07yun.jkl"), "07yun");
        var sim = new WorldSimulation(level, vfs, difficulty: 1);

        // Thing 29 (droidcam, rs.3do) is a camera entity and must never render as a floating 3DO
        Assert.True((level.Things[29].ThingFlags & (uint)SithThingFlags.Invisible) != 0);
        Assert.False(ThingMeshRenderer.ShouldRenderThing(level.Things[29], playerThingIndex: 0));

        // 07_rahnlines.cog locks player (0xa00000) and pauses Yun (0x2000) during intro
        Assert.True(sim.IsInCutscene);
        sim.SkipActiveCutscene();
        Assert.False(sim.IsInCutscene);

        Vector3 yunStart = level.Things[1].Position;
        Vector3 playerPos = level.Things[0].Position;
        int playerSector = level.Things[0].SectorIndex;
        float healthBefore = sim.Inventory.Health;

        // Yun approaches and attacks Kyle without Kyle attacking first
        for (int i = 0; i < 180; i++)
        {
            sim.Update(1.0f / 30.0f, playerPos, playerSector);
        }

        float movedDist = Vector3.Distance(yunStart, level.Things[1].Position);
        _output.WriteLine($"Yun moved {movedDist:F3}m, Kyle HP {healthBefore} -> {sim.Inventory.Health}");
        Assert.True(movedDist > 0.10f || sim.Inventory.Health < healthBefore,
            "Expected Yun to advance and attack Kyle after 07_rahnlines.cog intro finishes.");
    }
}
