using System;
using System.IO;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// End-to-end regression tests for Boss Battle #3: Maw (<c>15maw.jkl</c>),
/// <c>_flyactor</c> 3D flying physics (<c>physflags=0x7606</c>), <c>15_Rahnlines.cog</c> intro skip,
/// frontal saber parry vs. flanking/Force Persuasion bypass, and <c>15_endlevel.cog</c> level completion.
/// </summary>
public class MawBossBattleTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";
    private const int MawThingIndex = 31;
    private readonly ITestOutputHelper _output;

    public MawBossBattleTests(ITestOutputHelper output)
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

    private static WorldSimulation? TryCreateMawSimulation(out JklLevel level)
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null)
        {
            level = null!;
            return null;
        }

        level = JklReader.Parse(vfs.ReadAllText("jkl/15maw.jkl"), "15maw");
        return new WorldSimulation(level, vfs, difficulty: 1);
    }

    private static Vector3 GetThingForward(JklThing thing)
    {
        float yawRad = thing.PitchYawRoll.Y * (MathF.PI / 180f);
        return new Vector3(-MathF.Sin(yawRad), MathF.Cos(yawRad), 0f);
    }

    [Fact]
    public void Retail15Maw_TemplateAndStartup_SetsFlyActorAndSaberInfo()
    {
        var sim = TryCreateMawSimulation(out var level);
        if (sim == null) return;

        var maw = level.Things[MawThingIndex];
        Assert.Equal("maw", maw.TemplateName, ignoreCase: true);
        Assert.Equal("ma.3do", level.ResolveModel3D(maw.TemplateName), ignoreCase: true);
        Assert.Equal(900f, sim.GetThingHealth(MawThingIndex), precision: 1);
        Assert.False(sim.GetNpcUseGravity(MawThingIndex));

        Assert.True(sim.TryGetThingSaberInfo(MawThingIndex, out var saberInfo));
        Assert.Equal("saberdred1.mat", saberInfo.SideMat, ignoreCase: true);
        Assert.Equal("saberdred0.mat", saberInfo.TipMat, ignoreCase: true);
        Assert.Equal(0.125f, saberInfo.Length, precision: 3);
        Assert.Equal(0x5, sim.JkGetFlags(MawThingIndex) & 0x5);
    }

    [Fact]
    public void Retail15Maw_SkipActiveCutscene_ClearsRahnIntroAndMawAdvancesIn3D()
    {
        var sim = TryCreateMawSimulation(out var level);
        if (sim == null) return;

        // 15_Rahnlines.cog locks player during 29s intro
        Assert.True(sim.IsInCutscene);
        sim.SkipActiveCutscene();
        Assert.False(sim.IsInCutscene);

        var maw = level.Things[MawThingIndex];
        Vector3 mawStart = maw.Position;
        Vector3 playerPos = level.Things[0].Position;
        int playerSector = level.Things[0].SectorIndex;
        float distStart = Vector3.Distance(mawStart, playerPos);

        // Elevate player slightly to verify Maw adjusts Z while flying toward Kyle
        Vector3 elevatedPlayerPos = playerPos + new Vector3(0f, 0f, 0.35f);

        for (int i = 0; i < 150; i++)
        {
            sim.Update(1.0f / 30.0f, elevatedPlayerPos, playerSector);
        }

        float movedDist = Vector3.Distance(mawStart, maw.Position);
        float distEnd = Vector3.Distance(maw.Position, elevatedPlayerPos);
        _output.WriteLine($"Maw moved {movedDist:F3}m (Z {mawStart.Z:F3} -> {maw.Position.Z:F3}), dist to Kyle {distStart:F3} -> {distEnd:F3}");

        Assert.True(movedDist > 0.10f, "Expected Maw to fly/advance toward Kyle after skipping 15_Rahnlines.cog intro.");
        Assert.True(distEnd < distStart, "Expected Maw to close distance toward Kyle.");
    }

    [Fact]
    public void Retail15Maw_SaberParryFromFront_BypassedByFlankAndForcePersuasion_AndKillingEndsLevel()
    {
        var sim = TryCreateMawSimulation(out var level);
        if (sim == null) return;

        var maw = level.Things[MawThingIndex];
        Vector3 mawCenter = maw.Position + new Vector3(0f, 0f, 0.10f);
        Vector3 mawForward = GetThingForward(maw);
        float startHp = sim.GetThingHealth(MawThingIndex);
        Assert.Equal(900f, startHp, precision: 1);

        // 1. Frontal saber swing -> Maw parries (fov=0.40, chance=1.00 -> 0 damage, block anim, saber sparks)
        Vector3 frontOrigin = mawCenter + mawForward * 0.25f;
        int hitId = sim.FireWeaponRay(frontOrigin, -mawForward, maxRange: 0.6f, damage: 100f, isMelee: true);
        Assert.Equal(MawThingIndex, hitId);
        Assert.Equal(900f, sim.GetThingHealth(MawThingIndex), precision: 1);
        Assert.Equal("block", sim.GetNpcAnimAction(MawThingIndex));
        Assert.True(sim.Sparks.Count > 0);

        // 2. Flanking / backstab swing -> bypasses parry and deals full damage
        Vector3 backOrigin = mawCenter - mawForward * 0.25f;
        sim.FireWeaponRay(backOrigin, mawForward, maxRange: 0.6f, damage: 100f, isMelee: true);
        Assert.Equal(800f, sim.GetThingHealth(MawThingIndex), precision: 1);

        // 3. Force Persuasion invisibility on Kyle (ActorFlags 0x80) -> bypasses parry even from front
        sim.SetActorFlags(0, 0x80);
        sim.FireWeaponRay(frontOrigin, -mawForward, maxRange: 0.6f, damage: 100f, isMelee: true);
        Assert.Equal(700f, sim.GetThingHealth(MawThingIndex), precision: 1);

        // 4. Kill Maw -> 15_endlevel.cog killed: triggers JkEndLevel(1) (LevelCompleted == true)
        sim.FireWeaponRay(frontOrigin, -mawForward, maxRange: 0.6f, damage: 800f, isMelee: true);
        Assert.True(sim.GetThingHealth(MawThingIndex) <= 0f);

        Vector3 playerPos = level.Things[0].Position;
        int playerSector = level.Things[0].SectorIndex;
        for (int i = 0; i < 120; i++)
        {
            sim.Update(0.1f, playerPos, playerSector);
            if (sim.LevelCompleted)
                break;
        }

        _output.WriteLine($"LevelCompleted={sim.LevelCompleted}");
        Assert.True(sim.LevelCompleted, "Expected 15_endlevel.cog killed: to call JkEndLevel(1).");
    }

    [Fact]
    public void Synthetic_FlyActor_ParsesPhysFlagsAndHoversTowardTargetZClampedToRoom()
    {
        // Room 4x4m, floor Z=0, ceiling Z=3
        var level = new JklLevel();
        level.Vertices.AddRange([
            new Vector3(-2f, -2f, 0f), // 0
            new Vector3( 2f, -2f, 0f), // 1
            new Vector3( 2f,  2f, 0f), // 2
            new Vector3(-2f,  2f, 0f), // 3
            new Vector3(-2f, -2f, 3f), // 4
            new Vector3( 2f, -2f, 3f), // 5
            new Vector3( 2f,  2f, 3f), // 6
            new Vector3(-2f,  2f, 3f), // 7
        ]);

        var sector = new JklSector { SectorIndex = 0 };
        sector.Surfaces.Add(new JklSurface { VertexIndices = [0, 1, 2, 3], Normal = Vector3.UnitZ, AdjoinIndex = -1 });
        sector.Surfaces.Add(new JklSurface { VertexIndices = [4, 7, 6, 5], Normal = -Vector3.UnitZ, AdjoinIndex = -1 });
        sector.Surfaces.Add(new JklSurface { VertexIndices = [0, 4, 5, 1], Normal = Vector3.UnitY, AdjoinIndex = -1 });
        sector.Surfaces.Add(new JklSurface { VertexIndices = [1, 5, 6, 2], Normal = -Vector3.UnitX, AdjoinIndex = -1 });
        sector.Surfaces.Add(new JklSurface { VertexIndices = [2, 6, 7, 3], Normal = -Vector3.UnitY, AdjoinIndex = -1 });
        sector.Surfaces.Add(new JklSurface { VertexIndices = [3, 7, 4, 0], Normal = Vector3.UnitX, AdjoinIndex = -1 });
        level.Sectors.Add(sector);

        level.Templates["flyer"] = new JklTemplate
        {
            Name = "flyer",
            Parameters =
            {
                ["aiclass"] = "none",
                ["physflags"] = "0x7606",
                ["maxvel"] = "1.0",
                ["size"] = "0.10",
                ["health"] = "100"
            }
        };
        level.Templates["walker"] = new JklTemplate
        {
            Name = "walker",
            Parameters =
            {
                ["aiclass"] = "none",
                ["physflags"] = "0x4a4f",
                ["maxvel"] = "1.0",
                ["size"] = "0.10",
                ["health"] = "100"
            }
        };

        level.Things.Add(new JklThing
        {
            ThingIndex = 0,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, -1.5f, 0.13f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "flyer",
            SectorIndex = 0,
            Position = new Vector3(0f, 1.5f, 0.13f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 2,
            TemplateName = "walker",
            SectorIndex = 0,
            Position = new Vector3(1.0f, 1.5f, 0.13f)
        });

        var vfs = new VirtualFileSystem();
        var sim = new WorldSimulation(level, vfs, difficulty: 1);

        Assert.False(sim.GetNpcUseGravity(1));
        Assert.True(sim.GetNpcUseGravity(2));

        // Direct flyer toward Z=5.0 (above ceiling Z=3.0) -> should rise and clamp below ceiling
        sim.AISetMovePos(1, new Vector3(0f, 0f, 5.0f));
        sim.AISetMovePos(2, new Vector3(1.0f, 0f, 5.0f));

        for (int i = 0; i < 60; i++)
        {
            sim.Update(0.05f, level.Things[0].Position, 0);
        }

        Assert.True(level.Things[1].Position.Z > 1.0f, $"Expected _flyactor to ascend, got Z={level.Things[1].Position.Z:F3}");
        Assert.True(level.Things[1].Position.Z < 3.0f, $"Expected _flyactor to clamp below ceiling Z=3.0, got Z={level.Things[1].Position.Z:F3}");
        Assert.Equal(0.13f, level.Things[2].Position.Z, precision: 2);
    }
}
