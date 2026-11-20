using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class PickupAndNpcDropTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";
    private readonly ITestOutputHelper _output;

    public PickupAndNpcDropTests(ITestOutputHelper output)
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

    private static JklThing FindRetailThing(JklLevel level, string templateName, int difficulty = 1)
    {
        return level.Things.First(t =>
            t.TemplateName.Equals(templateName, StringComparison.OrdinalIgnoreCase) &&
            (t.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) == 0 &&
            JklThing.ShouldSpawnForDifficulty(t.ThingFlags, difficulty));
    }

    [Fact]
    public void RetailPickups_01NarShaddaa_ArePickedUpWhenPlayerWalksOverThem()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs, difficulty: 1);

        // 1. Shield recharge (pow_shields.cog -> bin 60 +20; retail 01narshadda may place adjacent pairs)
        var shield = FindRetailThing(level, "shieldrecharge");
        float shieldsBefore = sim.Inventory.Shields;
        Vector3 shieldFeet = new(shield.Position.X, shield.Position.Y, shield.Position.Z - 0.14f);
        sim.CheckPickupTouches(shieldFeet);
        Assert.True(sim.Inventory.Shields >= shieldsBefore + 20f,
            $"Expected Shields >= {shieldsBefore + 20f}, got {sim.Inventory.Shields}");
        Assert.NotEqual(0u, shield.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed));

        // 2. Energy cell (pow_energy.cog -> bin 11 +20)
        var energy = FindRetailThing(level, "energycell");
        float energyBefore = sim.Inventory.EnergyAmmo;
        Vector3 energyFeet = new(energy.Position.X, energy.Position.Y, energy.Position.Z - 0.14f);
        sim.CheckPickupTouches(energyFeet);
        Assert.True(sim.Inventory.EnergyAmmo >= energyBefore + 20f,
            $"Expected EnergyAmmo >= {energyBefore + 20f}, got {sim.Inventory.EnergyAmmo}");
        Assert.NotEqual(0u, energy.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed));

        // 3. Health pack (pow_health.cog -> Health +20 when Health < 100)
        var healthPack = FindRetailThing(level, "healthpack");
        sim.Inventory.Health = 50f;
        Vector3 healthFeet = new(healthPack.Position.X, healthPack.Position.Y, healthPack.Position.Z - 0.14f);
        sim.CheckPickupTouches(healthFeet);
        Assert.Equal(70f, sim.Inventory.Health);
        Assert.NotEqual(0u, healthPack.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed));

        // 4. Stormtrooper rifle (pow_strifle.cog -> bin 3 = 1, bin 11 += 24)
        var strifle = FindRetailThing(level, "strifle");
        float energyPreRifle = sim.Inventory.EnergyAmmo;
        Vector3 rifleFeet = new(strifle.Position.X, strifle.Position.Y, strifle.Position.Z - 0.14f);
        sim.CheckPickupTouches(rifleFeet);
        Assert.Equal(1f, sim.Inventory.GetInv(PlayerInventory.BinStRifle));
        Assert.True(sim.Inventory.EnergyAmmo > energyPreRifle);
        Assert.NotEqual(0u, strifle.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed));

        // 5. Bryar pistol / Greedo pistol (pow_bryar.cog -> bin 2 = 1)
        var bryar = FindRetailThing(level, "greedopistol");
        sim.Inventory.HasBryar = false;
        Vector3 bryarFeet = new(bryar.Position.X, bryar.Position.Y, bryar.Position.Z - 0.14f);
        sim.CheckPickupTouches(bryarFeet);
        Assert.True(sim.Inventory.HasBryar);
        Assert.NotEqual(0u, bryar.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed));
    }

    [Fact]
    public void CheckPickupTouches_CapsuleOverlap_DetectsWaistHeightPickupAndIgnoresDistantPickup()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs, difficulty: 1);

        var shield = FindRetailThing(level, "shieldrecharge");
        float initialShields = sim.Inventory.Shields;

        // Far away horizontally -> should NOT pick up
        sim.CheckPickupTouches(shield.Position + new Vector3(0.5f, 0f, 0f));
        Assert.Equal(initialShields, sim.Inventory.Shields);

        // Player feet 0.18 units below pickup center (within 0.22f capsule height + radius) -> SHOULD pick up
        sim.CheckPickupTouches(shield.Position - new Vector3(0f, 0f, 0.18f));
        Assert.True(sim.Inventory.Shields >= initialShields + 20f,
            $"Expected Shields >= {initialShields + 20f}, got {sim.Inventory.Shields}");
    }

    [Theory]
    [InlineData("st_pistol", "+DEnergy")]
    [InlineData("st_rifle", "+DSTRifle")]
    [InlineData("reeyees", "+DSTRifle")]
    public void RetailNpc_Killed_DropsGroundedRenderableAndPickableItem(string npcTemplate, string expectedDropTemplate)
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs, difficulty: 1);

        var npcThing = level.Things.FirstOrDefault(t =>
            t.TemplateName.Equals(npcTemplate, StringComparison.OrdinalIgnoreCase) &&
            (t.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) == 0 &&
            JklThing.ShouldSpawnForDifficulty(t.ThingFlags, 1));

        if (npcThing == null)
        {
            _output.WriteLine($"Skipping {npcTemplate}: not spawned in 01narshadda.jkl at difficulty 1.");
            return;
        }

        int countBefore = level.Things.Count;

        // Aim downward from just above NPC torso center to guarantee unobstructed melee hit
        Vector3 npcCenter = npcThing.Position + new Vector3(0f, 0f, 0.10f);
        int hitId = sim.FireWeaponRay(
            npcCenter + new Vector3(0f, 0f, 0.05f),
            -Vector3.UnitZ,
            maxRange: 0.5f,
            damage: 500f,
            isMelee: true);

        Assert.Equal(npcThing.ThingIndex, hitId);
        Assert.True(level.Things.Count > countBefore, $"Expected {npcTemplate} to spawn a drop on killed.");

        var dropped = level.Things
            .Skip(countBefore)
            .FirstOrDefault(t => t.TemplateName.Equals(expectedDropTemplate, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(dropped);
        Assert.Equal(0u, dropped!.ThingFlags);
        Assert.False(string.IsNullOrWhiteSpace(dropped.Model3D));
        Assert.True(dropped.Position.Z <= npcThing.Position.Z + 0.01f,
            $"Dropped item Z ({dropped.Position.Z}) should be grounded at/below NPC position Z ({npcThing.Position.Z}).");

        // Verify the player can walk over and pick up the dropped item
        float energyBefore = sim.Inventory.EnergyAmmo;
        sim.CheckPickupTouches(new Vector3(dropped.Position.X, dropped.Position.Y, dropped.Position.Z - 0.05f));
        Assert.True(sim.Inventory.EnergyAmmo > energyBefore, "Expected EnergyAmmo to increase after picking up dropped item.");
        Assert.NotEqual(0u, dropped.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed));
    }

    [Fact]
    public void Retail01NarShaddaa_LoadNpcs_LiftsUndergroundNpcsInSec65AboveFloor()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sec65 = level.Sectors[65];

        float FindEffectiveFloorZ(int secIdx, Vector3 pos)
        {
            var sectors = new System.Collections.Generic.List<JklSector> { level.Sectors[secIdx] };
            foreach (var surf in level.Sectors[secIdx].Surfaces)
            {
                if (surf.IsPortal && surf.MirrorSector != null && !sectors.Contains(surf.MirrorSector))
                    sectors.Add(surf.MirrorSector);
            }

            float best = float.MinValue;
            foreach (var sec in sectors)
            {
                foreach (var s in sec.Surfaces)
                {
                    if (s.IsPortal || s.VertexIndices.Length < 3 || s.Normal.Z <= 0.4f)
                        continue;
                    var verts = s.VertexIndices.Select(i => level.Vertices[i]).ToArray();
                    bool inside = false;
                    for (int i = 0, j = verts.Length - 1; i < verts.Length; j = i++)
                    {
                        if (((verts[i].Y > pos.Y) != (verts[j].Y > pos.Y)) &&
                            (pos.X < (verts[j].X - verts[i].X) * (pos.Y - verts[i].Y) / ((verts[j].Y - verts[i].Y) == 0f ? 1e-6f : (verts[j].Y - verts[i].Y)) + verts[i].X))
                            inside = !inside;
                    }
                    if (!inside) continue;

                    float z = verts[0].Z - ((s.Normal.X * (pos.X - verts[0].X) + s.Normal.Y * (pos.Y - verts[0].Y)) / s.Normal.Z);
                    if (z <= pos.Z + 0.05f && z > best)
                        best = z;
                }
            }
            return best;
        }

        var thing159 = level.Things[159];
        var thing160 = level.Things[160];
        Assert.Equal(65, thing159.SectorIndex);
        Assert.Equal(65, thing160.SectorIndex);

        float floorZ159 = FindEffectiveFloorZ(65, thing159.Position);
        float floorZ160 = FindEffectiveFloorZ(65, thing160.Position);

        // Also test an NPC spawned 0.10m underground in Sec 65 to verify LoadNpcs lifts it
        var undergroundNpc = new JklThing
        {
            ThingIndex = level.Things.Count,
            TemplateName = "reeyeesbhgun",
            Position = new Vector3(-1.40f, 8.90f, 6.20f + 0.03f), // 0.10m below floorZ(6.20) + 0.13
            SectorIndex = 65
        };
        level.Things.Add(undergroundNpc);
        float floorZUnderground = FindEffectiveFloorZ(65, undergroundNpc.Position);

        var sim = new WorldSimulation(level, vfs, difficulty: 1);
        Assert.True(sim.ActiveNpcCount > 0);

        Assert.True(thing159.Position.Z >= floorZ159 + 0.12f,
            $"Thing 159 should be above floorZ ({floorZ159}) + hipHeight, got Z={thing159.Position.Z}");
        Assert.True(thing160.Position.Z >= floorZ160 + 0.12f,
            $"Thing 160 should be above floorZ ({floorZ160}) + hipHeight, got Z={thing160.Position.Z}");
        Assert.True(undergroundNpc.Position.Z >= floorZUnderground + 0.12f,
            $"Underground NPC should be lifted above floorZ ({floorZUnderground}) + hipHeight, got Z={undergroundNpc.Position.Z}");
    }

    [Fact]
    public void Npc_OnRetail2x2Elev_StaysOnPlatformWhenWalkingAndIsCarriedWhenMoving()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var retailLevel = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");

        // Build a synthetic pit sector with floor at Z = -1.0f (well below the elevator at Z = 0.0f)
        var level = new JklLevel { Name = "elev_npc_test" };
        foreach (var kv in retailLevel.Templates)
            level.Templates[kv.Key] = kv.Value;

        level.Vertices.AddRange(new[]
        {
            new Vector3(-4f, -4f, -1f),
            new Vector3( 4f, -4f, -1f),
            new Vector3( 4f,  4f, -1f),
            new Vector3(-4f,  4f, -1f),
            new Vector3(-4f, -4f,  3f),
            new Vector3( 4f, -4f,  3f),
            new Vector3( 4f,  4f,  3f),
            new Vector3(-4f,  4f,  3f),
        });

        var floorSurf = new JklSurface
        {
            SurfaceIndex = 0,
            AdjoinIndex = -1,
            Normal = Vector3.UnitZ,
            VertexIndices = [0, 1, 2, 3]
        };
        var ceilSurf = new JklSurface
        {
            SurfaceIndex = 1,
            AdjoinIndex = -1,
            Normal = -Vector3.UnitZ,
            VertexIndices = [7, 6, 5, 4]
        };
        level.Surfaces.Add(floorSurf);
        level.Surfaces.Add(ceilSurf);

        var sector = new JklSector
        {
            SectorIndex = 0,
            Center = new Vector3(0f, 0f, 1f),
            Surfaces = { floorSurf, ceilSurf }
        };
        level.Sectors.Add(sector);

        var playerSpawn = new JklThing
        {
            ThingIndex = 0,
            TemplateName = "walkplayer",
            Position = new Vector3(0.04f, 2.50f, 0.13f),
            SectorIndex = 0
        };
        var elevThing = new JklThing
        {
            ThingIndex = 1,
            TemplateName = "2x2elev",
            Position = new Vector3(0f, 0f, 0f),
            SectorIndex = 0,
            ExtraArguments = "numframes=2 frame=(0/0/0:0/0/0) frame=(0/0/1:0/0/0)"
        };
        var npcThing = new JklThing
        {
            ThingIndex = 2,
            TemplateName = "reeyeesbhgun",
            Position = new Vector3(0.04f, -0.04f, 0.13f),
            SectorIndex = 0
        };
        level.Things.Add(playerSpawn);
        level.Things.Add(elevThing);
        level.Things.Add(npcThing);

        var sim = new WorldSimulation(level, vfs, difficulty: 1);
        Assert.Equal(1, sim.ActiveNpcCount);

        // NPC should spawn on top of 2x2elev (Z >= 0), NOT dropped to pit floor (-1.0f)
        Assert.True(npcThing.Position.Z >= 0.05f,
            $"NPC should stay on 2x2elev top, got Z={npcThing.Position.Z}");

        // Alert the NPC with a 1-damage hit so it immediately acquires the player and follows
        sim.FireWeaponRay(npcThing.Position + new Vector3(0f, 0f, 0.25f), -Vector3.UnitZ, 0.5f, 1f, isMelee: true);

        // Tick simulation so NPC walks toward player on top of the 2x2elev
        Vector2 startXY = new(npcThing.Position.X, npcThing.Position.Y);
        for (int i = 0; i < 40; i++)
        {
            sim.Update(0.05f, playerSpawn.Position, 0);
        }

        float movedXY = Vector2.Distance(startXY, new Vector2(npcThing.Position.X, npcThing.Position.Y));
        Assert.True(movedXY > 0.01f, $"Expected NPC to walk on top of 2x2elev, movedXY={movedXY}");
        Assert.True(npcThing.Position.Z >= 0.05f,
            $"NPC fell off 2x2elev to pit floor while walking: Z={npcThing.Position.Z}");

        // Move elevator up to Z = 1.0f and verify NPC is carried up with it
        float zBeforeElev = npcThing.Position.Z;
        sim.MoveToFrame(elevThing.ThingIndex, 1, 5.0f);
        for (int i = 0; i < 60; i++)
        {
            sim.Update(0.05f, playerSpawn.Position, 0);
        }

        Assert.True(elevThing.Position.Z > 0.9f, $"Expected 2x2elev to reach Z~1.0, got {elevThing.Position.Z}");
        Assert.True(npcThing.Position.Z >= zBeforeElev + 0.9f,
            $"Expected NPC to be carried up by 2x2elev from {zBeforeElev} to >= {zBeforeElev + 0.9f}, got {npcThing.Position.Z}");
    }
}
