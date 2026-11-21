using System;
using System.IO;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Cog;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// End-to-end regression tests for Boss Battle #4: Sariss
/// Light Side (<c>17asarris.jkl</c>) and Dark Side (<c>17bsarris.jkl</c>).
/// </summary>
public class SarissBossBattleTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";
    private readonly ITestOutputHelper _output;

    public SarissBossBattleTests(ITestOutputHelper output)
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

    private static WorldSimulation? TryCreateSimulation(string jklPath, string levelName, out JklLevel level)
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null)
        {
            level = null!;
            return null;
        }

        level = JklReader.Parse(vfs.ReadAllText(jklPath), levelName);
        return new WorldSimulation(level, vfs, difficulty: 1);
    }

    private static Vector3 GetThingForward(JklThing thing)
    {
        float yawRad = thing.PitchYawRoll.Y * (MathF.PI / 180f);
        return new Vector3(-MathF.Sin(yawRad), MathF.Cos(yawRad), 0f);
    }

    [Fact]
    public void Retail17aSariss_LightSide_StartupPickUpSaberAndDefeatEndsLevel()
    {
        var sim = TryCreateSimulation("jkl/17asarris.jkl", "17asarris", out var level);
        if (sim == null) return;

        // Verify Sariss (Thing 1), Yun dead body (Thing 3), and Lightsaber pickup (Thing 4)
        var sariss = level.Things[1];
        Assert.Equal("sa.3do", level.ResolveModel3D(sariss.TemplateName), ignoreCase: true);
        Assert.Equal(1900f, sim.GetThingHealth(1), precision: 1);

        Assert.True(sim.TryGetThingSaberInfo(1, out var saberInfo));
        Assert.Equal("saberblue1.mat", saberInfo.SideMat, ignoreCase: true);
        Assert.Equal("saberblue0.mat", saberInfo.TipMat, ignoreCase: true);
        Assert.Equal(0.118f, saberInfo.Length, precision: 3);
        Assert.Equal(0x5, sim.JkGetFlags(1) & 0x5);

        Assert.Equal("yundead", level.Things[3].TemplateName, ignoreCase: true);
        Assert.Equal("lightsaber", level.Things[4].TemplateName, ignoreCase: true);

        // 17a_endlevel.cog removes Kyle's lightsaber on startup (SetInv(player, 10, 0))
        Assert.Equal(0f, sim.Inventory.GetInv(PlayerInventory.BinLightsaber));

        // Walking over Thing 4 (lightsaber) triggers pow_saber.cog touched: and restores saber
        sim.CheckPickupTouches(level.Things[4].Position);
        Assert.Equal(1f, sim.Inventory.GetInv(PlayerInventory.BinLightsaber));

        // SkipActiveCutscene() wakes Sariss (clears 0x2000 AI pause)
        sim.SkipActiveCutscene();
        Assert.False(sim.IsInCutscene);
        Assert.Equal(0, sim.AiGetMode(1) & 0x2000);

        // Verify GetLevelTime() tracks elapsed Update(dt) time
        Assert.Equal(0f, sim.GetLevelTime());

        // Kill Sariss from behind (bypassing frontal saber parry)
        Vector3 sarissCenter = sariss.Position + new Vector3(0f, 0f, 0.10f);
        Vector3 sarissForward = GetThingForward(sariss);
        Vector3 backOrigin = sarissCenter - sarissForward * 0.25f;
        sim.FireWeaponRay(backOrigin, sarissForward, maxRange: 0.6f, damage: 2000f, isMelee: true);
        Assert.True(sim.GetThingHealth(1) <= 0f);

        int playerIdx = sim.GetLocalPlayerThing();
        Vector3 playerPos = level.Things[playerIdx].Position;
        int playerSector = level.Things[playerIdx].SectorIndex;
        for (int i = 0; i < 120; i++)
        {
            sim.Update(0.1f, playerPos, playerSector);
            if (sim.LevelCompleted)
                break;
        }

        _output.WriteLine($"17aSariss LevelCompleted={sim.LevelCompleted}, ForceRank={sim.Inventory.GetInv(PlayerInventory.BinForceRank)}, LevelTime={sim.GetLevelTime():F2}s");
        Assert.True(sim.GetLevelTime() > 0f);
        Assert.True(sim.LevelCompleted, "Expected 17a_endlevel.cog killed: to call JkEndLevel(1).");
        Assert.Equal(7f, sim.Inventory.GetInv(PlayerInventory.BinForceRank));
    }

    [Fact]
    public void Retail17bSariss_DarkSide_Walkplayer19AndYun2EndsLevel()
    {
        var sim = TryCreateSimulation("jkl/17bsarris.jkl", "17bsarris", out var level);
        if (sim == null) return;

        // 17bsarris.jkl walkplayer is Thing 19, Yun is Thing 20
        Assert.Equal(19, sim.GetLocalPlayerThing());

        Assert.Equal(1000f, sim.GetThingHealth(20), precision: 1);
        Assert.True(sim.TryGetThingSaberInfo(20, out var saberInfo));
        Assert.Equal("saberyellow1.mat", saberInfo.SideMat, ignoreCase: true);
        Assert.Equal("saberyellow0.mat", saberInfo.TipMat, ignoreCase: true);
        Assert.Equal(0.100f, saberInfo.Length, precision: 3);
        Assert.Equal(0x5, sim.JkGetFlags(20) & 0x5);

        // SkipActiveCutscene() clears 17b_Camera.cog intro
        Assert.True(sim.IsInCutscene);
        sim.SkipActiveCutscene();
        Assert.False(sim.IsInCutscene);

        // Kill Yun (Thing 20) from behind (bypassing frontal saber parry)
        var yun = level.Things[20];
        Vector3 yunCenter = yun.Position + new Vector3(0f, 0f, 0.10f);
        Vector3 yunForward = GetThingForward(yun);
        Vector3 backOrigin = yunCenter - yunForward * 0.25f;
        sim.FireWeaponRay(backOrigin, yunForward, maxRange: 0.6f, damage: 1100f, isMelee: true);
        Assert.True(sim.GetThingHealth(20) <= 0f);

        Vector3 playerPos = level.Things[19].Position;
        int playerSector = level.Things[19].SectorIndex;
        for (int i = 0; i < 120; i++)
        {
            sim.Update(0.1f, playerPos, playerSector);
            if (sim.LevelCompleted)
                break;
        }

        _output.WriteLine($"17bSariss LevelCompleted={sim.LevelCompleted}, LevelTime={sim.GetLevelTime():F2}s");
        Assert.True(sim.LevelCompleted, "Expected 17b_endlevel.cog killed: to call JkEndLevel(1).");
    }
}
