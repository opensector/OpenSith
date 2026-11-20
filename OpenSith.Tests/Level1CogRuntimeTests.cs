using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Live <see cref="WorldSimulation"/> regression tests against retail <c>01narshadda.jkl</c>
/// covering <c>CaptureThing</c>, <c>CreateThing</c> NPC spawning, surface/prop <c>damaged:</c>,
/// <c>01_goal0.cog</c> <c>sighted:</c>, <c>JkEndLevel</c> <c>shutdown:</c>, and P1/P2 COG verbs.
/// </summary>
public class Level1CogRuntimeTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

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

    private static (WorldSimulation Sim, JklLevel Level)? TryLoadLevel1()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null)
            return null;

        if (!vfs.FileExists("jkl/01narshadda.jkl"))
            return null;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs);
        return (sim, level);
    }

    [Fact]
    public void NeonSign_SurfaceDamaged_DispatchesCogDamaged()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null) return;
        var (sim, level) = loaded.Value;

        var neonCog = level.Cogs.FirstOrDefault(c =>
            c.ScriptName.Equals("01_neonsign.cog", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(neonCog);
        int surfaceId = int.Parse(neonCog!.SymbolArgs[0]);

        Assert.True((level.Surfaces[surfaceId].SurfaceFlags & 0x2) != 0);
        sim.SetSurfaceCel(surfaceId, 1);

        Vector3 center = sim.SurfaceCenter(surfaceId);
        Vector3 normal = Vector3.Normalize(level.Surfaces[surfaceId].Normal);
        Vector3 rayOrigin = center + normal * 0.25f;
        Vector3 rayDir = -normal;

        int thingsBefore = level.Things.Count;
        sim.FireWeaponRay(rayOrigin, rayDir, maxRange: 1.0f, damage: 10f, isMelee: true);

        // 01_neonsign.cog damaged: sets cel to 0 and spawns +sparks
        Assert.Equal(0, sim.GetSurfaceCel(surfaceId));
        Assert.True(level.Things.Count > thingsBefore);
    }

    [Fact]
    public void NeonSign_And_Crate_ProjectileDamaged_DispatchesCogDamaged()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null) return;
        var (sim, level) = loaded.Value;

        var neonCog = level.Cogs.FirstOrDefault(c =>
            c.ScriptName.Equals("01_neonsign.cog", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(neonCog);
        int surfaceId = int.Parse(neonCog!.SymbolArgs[0]);
        sim.SetSurfaceCel(surfaceId, 1);

        Vector3 center = sim.SurfaceCenter(surfaceId);
        Vector3 normal = Vector3.Normalize(level.Surfaces[surfaceId].Normal);
        Vector3 rayOrigin = center + normal * 0.25f;
        Vector3 rayDir = -normal;

        sim.FireWeaponRay(rayOrigin, rayDir, maxRange: 5.0f, damage: 15f, isMelee: false, boltSpeed: 10.0f);
        var spawn = level.GetPlayerSpawn()!;
        sim.Update(0.05f, spawn.Position, spawn.SectorIndex);
        Assert.Equal(0, sim.GetSurfaceCel(surfaceId));

        var crate = level.Things.FirstOrDefault(t =>
            t.TemplateName.Equals("crate6_1", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(crate);
        int thingsBefore = level.Things.Count;
        Vector3 crateOrigin = crate!.Position - Vector3.UnitY * 0.25f;
        sim.FireWeaponRay(crateOrigin, Vector3.UnitY, maxRange: 5.0f, damage: 100f, isMelee: false, boltSpeed: 10.0f);
        sim.Update(0.05f, spawn.Position, spawn.SectorIndex);

        bool destroyedOrSpawned = (sim.GetThingFlags(crate.ThingIndex) & (int)SithThingFlags.Destroyed) != 0
            || level.Things.Count > thingsBefore;
        Assert.True(destroyedOrSpawned);
    }

    [Fact]
    public void DestructibleCrate_Damaged_DispatchesActorCsDamaged()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null) return;
        var (sim, level) = loaded.Value;

        var crate = level.Things.FirstOrDefault(t =>
            t.TemplateName.Equals("crate6_1", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(crate);

        Vector3 rayOrigin = crate!.Position - Vector3.UnitY * 0.35f;
        Vector3 rayDir = Vector3.UnitY;

        int thingsBefore = level.Things.Count;
        int hitId = sim.FireWeaponRay(rayOrigin, rayDir, maxRange: 1.0f, damage: 100f, isMelee: true);

        Assert.Equal(crate.ThingIndex, hitId);
        // actor_cs.cog damaged/killed destroys or spawns debris/chunk things
        bool destroyedOrSpawned = (sim.GetThingFlags(crate.ThingIndex) & (int)SithThingFlags.Destroyed) != 0
            || level.Things.Count > thingsBefore;
        Assert.True(destroyedOrSpawned);
    }

    [Fact]
    public void Goal0_Sighting8t88_DispatchesSightedAndCompletesGoal()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null) return;
        var (sim, level) = loaded.Value;

        const int thing8t88Id = 204;
        Assert.InRange(thing8t88Id, 0, level.Things.Count - 1);
        var thing8t88 = level.Things[thing8t88Id];

        Assert.Equal(1, sim.Inventory.GetGoalFlags(0));

        // Place player directly in front of 8t88 with clear line of sight
        Vector3 playerPos = thing8t88.Position - Vector3.UnitY * 0.5f;
        Vector3 aimDir = Vector3.Normalize(thing8t88.Position - playerPos);
        int sector = thing8t88.SectorIndex;

        sim.Update(0.016f, playerPos, sector, aimDir);

        Assert.Equal(3, sim.Inventory.GetGoalFlags(0));
    }

    [Fact]
    public void CreateThing_WithAiClass_AndCaptureThing_SpawnsNpcRuntimeAndCaptures()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null) return;
        var (sim, level) = loaded.Value;

        // Find any template in 01narshadda.jkl that defines an aiclass
        string? npcTemplate = null;
        foreach (var t in level.Things)
        {
            if (sim.ActiveNpcCount > 0 && !string.IsNullOrWhiteSpace(t.TemplateName) &&
                !string.IsNullOrEmpty(sim.GetNpcAiClassName(t.ThingIndex)))
            {
                npcTemplate = t.TemplateName;
                break;
            }
        }
        Assert.NotNull(npcTemplate);

        int npcCountBefore = sim.ActiveNpcCount;
        var spawn = level.GetPlayerSpawn();
        Assert.NotNull(spawn);

        int newId = sim.CreateThing(npcTemplate!, spawn!.ThingIndex);
        Assert.True(newId >= 0);
        Assert.Equal(npcCountBefore + 1, sim.ActiveNpcCount);
        Assert.False(string.IsNullOrEmpty(sim.GetNpcAiClassName(newId)));

        sim.CaptureThing(newId, 0);
        Assert.True((sim.GetThingFlags(newId) & (int)SithThingFlags.Captured) != 0);
    }

    [Fact]
    public void JkEndLevel_DispatchesShutdownTo01EndLevelCog()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null) return;
        var (sim, _) = loaded.Value;

        sim.Inventory.TotalSecrets = 3;
        sim.Inventory.SecretsFound = 3;
        float starsBefore = sim.Inventory.GetInv(PlayerInventory.BinNewStars);

        sim.JkEndLevel(1);

        Assert.True(sim.LevelCompleted);
        // 01_endlevel.cog shutdown: awards a bonus Force star when all secrets are found
        Assert.Equal(starsBefore + 1f, sim.Inventory.GetInv(PlayerInventory.BinNewStars));
    }

    [Fact]
    public void CogWorldContextOverrides_TrackStateAccurately()
    {
        var loaded = TryLoadLevel1();
        if (loaded == null) return;
        var (sim, level) = loaded.Value;

        int playerId = sim.GetLocalPlayerThing();

        sim.SetThingUserData(playerId, 42.5f);
        Assert.Equal(42.5f, sim.GetThingUserData(playerId));

        sim.ThingLight(playerId, 0.75f, 0f);
        Assert.Equal(0.75f, sim.GetThingLight(playerId));

        sim.SelectWeapon(playerId, PlayerInventory.BinBryar);
        Assert.Equal(PlayerInventory.BinBryar, sim.GetCurWeapon(playerId));
        Assert.Equal(PlayerInventory.BinBryar, sim.GetCurInvWeapon(playerId));
        sim.SetCurWeapon(playerId, PlayerInventory.BinLightsaber);
        Assert.Equal(PlayerInventory.BinLightsaber, sim.GetCurWeapon(playerId));
        sim.SetCurInvWeapon(playerId, PlayerInventory.BinStRifle);
        Assert.Equal(PlayerInventory.BinStRifle, sim.GetCurInvWeapon(playerId));

        sim.AmputateJoint(playerId, 3);
        Assert.True(sim.IsJointAmputated(playerId, 3));

        if (level.Things.Count > 1)
        {
            int otherId = (playerId == 0) ? 1 : 0;
            sim.AttachThingToThingEx(otherId, playerId, 0);
            Assert.Equal(playerId, sim.GetThingParent(otherId));
            sim.DetachThing(otherId);
            Assert.Equal(-1, sim.GetThingParent(otherId));
        }

        sim.SetCollideType(playerId, 0);
        Assert.Equal(0, sim.GetCollideType(playerId));
        Assert.True(level.Things[playerId].IsNonSolid);
        sim.SetCollideType(playerId, 1);
        Assert.False(level.Things[playerId].IsNonSolid);

        // Find any live NPC to test SetThingHealth and AISetMoveSpeed
        var npcThing = level.Things.FirstOrDefault(t => !string.IsNullOrEmpty(sim.GetNpcAiClassName(t.ThingIndex)));
        if (npcThing != null)
        {
            sim.SetThingHealth(npcThing.ThingIndex, 12.5f);
            Assert.Equal(12.5f, sim.GetThingHealth(npcThing.ThingIndex));
            sim.AISetMoveSpeed(npcThing.ThingIndex, 1.25f);
        }
    }
}
