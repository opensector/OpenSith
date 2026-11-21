using System;
using System.IO;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Cog;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

public class ForceAndUnderwaterIntegrationTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    private static VirtualFileSystem? TryMountRetailVfs()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(res2Path))
            return null;

        var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));
        if (File.Exists(jk1Path))
            vfs.Mount(GobArchive.FromFile(jk1Path));
        return vfs;
    }

    private static JklLevel CreateFlatBoxLevel(float floorZ = 0f)
    {
        var level = new JklLevel { Name = "force_test" };
        level.Vertices.AddRange([
            new Vector3(-5f, -5f, floorZ),
            new Vector3(5f, -5f, floorZ),
            new Vector3(5f, 5f, floorZ),
            new Vector3(-5f, 5f, floorZ)
        ]);

        var floor = new JklSurface
        {
            SurfaceIndex = 0,
            AdjoinIndex = -1,
            VertexIndices = [0, 1, 2, 3],
            Normal = Vector3.UnitZ,
            SurfaceFlags = 0x4
        };
        level.Surfaces.Add(floor);

        var sector = new JklSector
        {
            SectorIndex = 0,
            Flags = 0,
            AmbientLight = 1f,
            ExtraLight = 0f,
            Surfaces = [floor]
        };
        level.Sectors.Add(sector);

        level.Things.Add(new JklThing
        {
            ThingIndex = 0,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, 0f, floorZ),
            PitchYawRoll = Vector3.Zero
        });

        level.Templates["item_pullable"] = new JklTemplate
        {
            Name = "item_pullable",
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "item",
                ["cog"] = "pow_health.cog",
                ["size"] = "0.10"
            }
        };

        return level;
    }

    [Fact]
    public void WorldSimulation_Update_RegeneratesForceMana_ScaledByForceRank()
    {
        var level = CreateFlatBoxLevel();
        var sim = new WorldSimulation(level, new VirtualFileSystem());
        sim.Inventory.ForceRank = 2; // MaxForceMana = 100, regen = 2 * 0.5 = 1.0/sec
        sim.Inventory.ForceMana = 10f;

        sim.Update(2.0f, Vector3.Zero, 0);

        Assert.Equal(12.0f, sim.Inventory.ForceMana, precision: 3);
    }

    [Fact]
    public void ActivateForcePower_BinForceHeal_RestoresPlayerHealth_WithRetailCog()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = CreateFlatBoxLevel();
        var sim = new WorldSimulation(level, vfs);
        sim.Inventory.ForceRank = 4;
        sim.Inventory.ForceMana = 200f;
        sim.Inventory.SetInv(PlayerInventory.BinForceHeal, 4f);
        sim.Inventory.Health = 40f;

        bool activated = sim.ActivateForcePower(PlayerInventory.BinForceHeal, Vector3.UnitY);
        Assert.True(activated);

        // force_heal.cog sleeps 0.6s before calling HealThing
        sim.Update(0.7f, Vector3.Zero, 0);

        Assert.True(sim.Inventory.Health > 40f);
    }

    [Fact]
    public void ActivateForcePower_BinForceSpeed_ActivatesAndSetsPlayerExtraSpeed_WithRetailCog()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = CreateFlatBoxLevel();
        var sim = new WorldSimulation(level, vfs);
        int player = sim.GetLocalPlayerThing();
        sim.Inventory.ForceRank = 4;
        sim.Inventory.ForceMana = 100f;
        sim.Inventory.SetInv(PlayerInventory.BinForceSpeed, 4f);

        bool activated = sim.ActivateForcePower(PlayerInventory.BinForceSpeed, Vector3.UnitY);
        Assert.True(activated);

        Assert.Equal(1, sim.IsInvActivated(player, PlayerInventory.BinForceSpeed));
        Assert.True(sim.PlayerExtraSpeed > 0f);
    }

    [Fact]
    public void ActivateForcePower_BinForcePull_PullsNearbyItemTowardKyle()
    {
        var level = CreateFlatBoxLevel();
        var item = new JklThing
        {
            ThingIndex = 1,
            TemplateName = "item_pullable",
            SectorIndex = 0,
            Position = new Vector3(0f, 3.0f, 0f)
        };
        level.Things.Add(item);

        var sim = new WorldSimulation(level, new VirtualFileSystem());
        bool activated = sim.ActivateForcePower(PlayerInventory.BinForcePull, Vector3.UnitY);

        Assert.True(activated);
        Assert.True(sim.GetThingVel(1).Y < 0f);

        sim.Update(0.2f, Vector3.Zero, 0);
        Assert.True(item.Position.Y < 3.0f);
    }

    [Fact]
    public void DynamicThing_GravityAndVelocity_SettlesOntoSectorFloor()
    {
        var level = CreateFlatBoxLevel(floorZ: 0f);
        var dropped = new JklThing
        {
            ThingIndex = 1,
            TemplateName = "item_pullable",
            SectorIndex = 0,
            Position = new Vector3(0f, 2.0f, 0.4f)
        };
        level.Things.Add(dropped);

        var sim = new WorldSimulation(level, new VirtualFileSystem());
        sim.SetThingVel(1, new Vector3(1.0f, 0f, 0.5f));
        sim.AddThingVel(1, new Vector3(0f, 0f, 0.2f));

        for (int i = 0; i < 40; i++)
        {
            sim.Update(0.05f, Vector3.Zero, 0);
        }

        Assert.Equal(0f, dropped.Position.Z, precision: 3);
        Assert.True(dropped.Position.X > 0f);
        Assert.Equal(Vector3.Zero, sim.GetThingVel(1));
    }

    [Fact]
    public void CogWorldContext_InventoryJkFlagsAndViewVerbs_WorkOnWorldSimulation()
    {
        var level = CreateFlatBoxLevel();
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "item_pullable",
            SectorIndex = 0,
            Position = new Vector3(0f, 4.0f, 0f)
        });

        var sim = new WorldSimulation(level, new VirtualFileSystem());
        int player = sim.GetLocalPlayerThing();

        sim.SetInvAvailable(player, PlayerInventory.BinForceJump, 1);
        Assert.Equal(1, sim.IsInvAvailable(player, PlayerInventory.BinForceJump));

        sim.JkSetFlags(player, 0x5);
        Assert.Equal(0x5, sim.JkGetFlags(player));
        sim.JkClearFlags(player, 0x1);
        Assert.Equal(0x4, sim.JkGetFlags(player));

        Assert.Equal(100f, sim.GetThingMaxHealth(player));
        Assert.Equal(1, sim.HasLOS(player, 1));
        Assert.True(sim.ThingViewDot(player, 1) > 0.99f);
        Assert.Equal(1, sim.FirstThingInView(player, 90f, 10f, 0));
        Assert.Equal(-1, sim.NextThingInView());
    }
}
