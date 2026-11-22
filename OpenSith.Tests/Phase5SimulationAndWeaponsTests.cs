using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Cog;
using OpenSith.Core.Materials;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Phase 5 integration tests for headless <see cref="WorldSimulation"/> in <c>OpenSith.Core</c>,
/// multi-player <see cref="PlayerState"/> &amp; dedicated-server isolation, retail COG-driven weapons,
/// items, Force powers, explosions/VFX, and <see cref="SprSprite"/> parsing.
/// </summary>
public sealed class Phase5SimulationAndWeaponsTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    private sealed class InMemoryVfs : IVirtualFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddText(string path, string content) =>
            _files[Normalize(path)] = Encoding.ASCII.GetBytes(content);

        public bool FileExists(string path) => _files.ContainsKey(Normalize(path));
        public byte[] ReadAllBytes(string path) => _files[Normalize(path)];
        public Stream OpenRead(string path) => new MemoryStream(_files[Normalize(path)], writable: false);
        public string ReadAllText(string path) => Encoding.ASCII.GetString(_files[Normalize(path)]);
        public void Dispose() { }

        private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    }

    private static JklLevel CreateBoxArenaLevel(float halfExtent = 4.0f, float height = 3.0f)
    {
        var level = new JklLevel { Name = "phase5_arena" };
        level.Materials.Add("wall0.mat");

        float h = halfExtent;
        level.Vertices.AddRange([
            new Vector3(-h, -h, 0f),     // 0: floor SW
            new Vector3( h, -h, 0f),     // 1: floor SE
            new Vector3( h,  h, 0f),     // 2: floor NE
            new Vector3(-h,  h, 0f),     // 3: floor NW
            new Vector3(-h, -h, height), // 4: ceil SW
            new Vector3( h, -h, height), // 5: ceil SE
            new Vector3( h,  h, height), // 6: ceil NE
            new Vector3(-h,  h, height), // 7: ceil NW
        ]);

        var surfaces = new[]
        {
            // Floor (+Z)
            new JklSurface { SurfaceIndex = 0, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [0, 1, 2, 3], Normal = Vector3.UnitZ, SurfaceFlags = 0x5 },
            // Ceiling (-Z)
            new JklSurface { SurfaceIndex = 1, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [7, 6, 5, 4], Normal = -Vector3.UnitZ, SurfaceFlags = 0x4 },
            // North wall (-Y)
            new JklSurface { SurfaceIndex = 2, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [3, 2, 6, 7], Normal = -Vector3.UnitY, SurfaceFlags = 0x4 },
            // South wall (+Y)
            new JklSurface { SurfaceIndex = 3, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [0, 4, 5, 1], Normal = Vector3.UnitY, SurfaceFlags = 0x4 },
            // East wall (-X)
            new JklSurface { SurfaceIndex = 4, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [1, 5, 6, 2], Normal = -Vector3.UnitX, SurfaceFlags = 0x4 },
            // West wall (+X)
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

        level.Things.Add(new JklThing
        {
            ThingIndex = 0,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, -1f, 0f),
            PitchYawRoll = Vector3.Zero
        });

        return level;
    }

    private static VirtualFileSystem? TryMountRetailVfs(out JklLevel? retailTemplatesLevel)
    {
        retailTemplatesLevel = null;
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(res2Path) || !File.Exists(jk1Path))
            return null;

        var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));
        vfs.Mount(GobArchive.FromFile(jk1Path));
        retailTemplatesLevel = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        return vfs;
    }

    [Fact]
    public void HeadlessSimulation_LivesInOpenSithCore_AndStepsWithoutAudioOrClient()
    {
        Assert.Equal(typeof(PlayerInventory).Assembly, typeof(WorldSimulation).Assembly);
        Assert.Equal("OpenSith.Core.Simulation", typeof(WorldSimulation).Namespace);

        var sim = new WorldSimulation(CreateBoxArenaLevel(), new InMemoryVfs(), audio: null);
        sim.UpdatePlayerInput(sim.GetLocalPlayerThing(), new Vector3(0f, 0f, 0f), 0, Vector3.UnitY);
        sim.StepWorld(0.1f);

        Assert.NotNull(sim.LocalPlayer);
        Assert.Equal(1, sim.GetNumPlayers());
        Assert.True(sim.GetLevelTime() > 0.05f);
    }

    [Fact]
    public void DedicatedServer_MultiPlayerIsolation_PvPCombat_AndScoring()
    {
        var level = CreateBoxArenaLevel();
        level.Templates["+testbolt"] = new JklTemplate
        {
            Name = "+testbolt",
            BasedOn = "none",
            Model3D = "bry0.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vel"] = "(0.0/10.0/0.0)",
                ["damage"] = "40.0",
                ["timer"] = "2.0",
                ["physflags"] = "0x200",
                ["typeflags"] = "0x40d",
                ["explode"] = "+testexp"
            }
        };
        level.Templates["+testexp"] = new JklTemplate
        {
            Name = "+testexp",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "explosion",
                ["timer"] = "0.5",
                ["blasttime"] = "0.5",
                ["sprite"] = "detx.spr",
                ["damage"] = "0",
                ["range"] = "0"
            }
        };

        var sim = new WorldSimulation(level, new InMemoryVfs(), isMultiplayer: true, isDedicatedServer: true);

        Assert.True(sim.IsDedicatedServer);
        Assert.True(sim.IsMultiplayer);
        Assert.Equal(-1, sim.GetLocalPlayerThing());
        Assert.Null(sim.LocalPlayer);
        Assert.Equal(1, sim.IsServer());
        Assert.Equal(1, sim.IsMulti());
        Assert.Equal(0, sim.GetNumPlayers());

        PlayerState p1 = sim.AddPlayer(0, "Kyle", team: 1);
        PlayerState p2 = sim.AddPlayer(1, "Jan", team: 2);

        Assert.Equal(2, sim.GetNumPlayers());
        Assert.Equal(p1.ThingIndex, sim.GetPlayerThing(0));
        Assert.Equal(p2.ThingIndex, sim.GetPlayerThing(1));
        Assert.Equal(1, sim.GetPlayerTeam(p1.ThingIndex));
        Assert.Equal(2, sim.GetPlayerTeam(p2.ThingIndex));

        // Verify per-player inventory and weapon state isolation
        p1.Inventory.EnergyAmmo = 90f;
        p2.Inventory.EnergyAmmo = 35f;
        p2.Inventory.Shields = 0f;
        p2.Inventory.Health = 30f;
        Assert.Equal(90f, sim.GetInv(p1.ThingIndex, PlayerInventory.BinEnergy));
        Assert.Equal(35f, sim.GetInv(p2.ThingIndex, PlayerInventory.BinEnergy));

        sim.SelectWeapon(p1.ThingIndex, PlayerInventory.BinBryar);
        sim.SelectWeapon(p2.ThingIndex, PlayerInventory.BinFists);
        Assert.Equal(PlayerInventory.BinBryar, sim.GetCurWeapon(p1.ThingIndex));
        Assert.Equal(PlayerInventory.BinFists, sim.GetCurWeapon(p2.ThingIndex));

        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, -1f, 0f), 0, Vector3.UnitY);
        sim.UpdatePlayerInput(p2.ThingIndex, new Vector3(0f,  1f, 0f), 0, -Vector3.UnitY);

        sim.FireProjectile(p1.ThingIndex, "+testbolt", "-1", 0, Vector3.Zero, Vector3.Zero, 1.0f, 0, 0f, 0f);
        bool sawImpactVfx = false;
        for (int i = 0; i < 20; i++)
        {
            sim.StepWorld(0.05f);
            if (sim.VisualEffects.Count > 0)
                sawImpactVfx = true;
        }

        Assert.True(p2.Inventory.IsDead, "Player 2 should be killed by Player 1's bolt.");
        Assert.Equal(100f, p1.Inventory.Health);
        Assert.Equal(1, sim.GetPlayerKills(p1.ThingIndex));
        Assert.Equal(1, sim.GetPlayerScore(p1.ThingIndex));
        Assert.Equal(1, sim.GetPlayerKilled(p2.ThingIndex));
        Assert.True(sawImpactVfx, "Expected impact explosion VisualEffect when bolt struck Player 2.");

        Assert.True(sim.RemovePlayer(p2.ThingIndex));
        Assert.Equal(1, sim.GetNumPlayers());
    }

    [Fact]
    public void RetailWeapons_Bryar_Saber_And_ThermalDetonator_ExecuteRealRetailCogs()
    {
        using var vfs = TryMountRetailVfs(out var retailTemplates);
        if (vfs == null || retailTemplates == null)
            return;

        var arena = CreateBoxArenaLevel(halfExtent: 4.0f);
        arena.Templates = retailTemplates.Templates;
        arena.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "reeyeesgun",
            SectorIndex = 0,
            Position = new Vector3(0f, 2.2f, 0.13f),
            PitchYawRoll = new Vector3(0f, 180f, 0f)
        });

        var sim = new WorldSimulation(arena, vfs, runStartupCogs: true);
        int playerId = sim.GetLocalPlayerThing();
        var player = sim.LocalPlayer!;

        // 1. Bryar Pistol (weap_bryar.cog) fires and damages Ree-Yees Thing 1 (50 HP -> <= 20 HP)
        float npc1HpBefore = sim.GetThingHealth(1);
        Assert.Equal(50f, npc1HpBefore);
        sim.SelectWeapon(playerId, PlayerInventory.BinBryar);
        Assert.Equal("bryv.3do", player.PovModel, ignoreCase: true);
        sim.StepWorld(1.0f); // Clear MountWait

        Vector3 npc1Pos = arena.Things[1].Position;
        sim.UpdatePlayerInput(playerId, new Vector3(npc1Pos.X, -1f, 0f), 0, Vector3.UnitY);

        float ammoBefore = player.Inventory.EnergyAmmo;
        Assert.True(sim.BeginWeaponFire(playerId, mode: 0));
        Assert.Equal(ammoBefore - 1f, player.Inventory.EnergyAmmo);
        Assert.NotEmpty(sim.Projectiles);
        sim.EndWeaponFire(playerId, mode: 0);

        for (int i = 0; i < 30; i++)
            sim.StepWorld(0.05f);
        Assert.True(sim.GetThingHealth(1) < npc1HpBefore, $"Expected Bryar bolt to damage Thing 1 (before={npc1HpBefore}, after={sim.GetThingHealth(1)}).");

        // 2. Lightsaber (weap_saber.cog)
        sim.SelectWeapon(playerId, PlayerInventory.BinLightsaber);
        Assert.Equal("sabv.3do", player.PovModel, ignoreCase: true);
        for (int i = 0; i < 20; i++)
            sim.StepWorld(0.05f); // Complete mount wait and 0.7s blade ignite timer

        Assert.True((sim.JkGetFlags(playerId) & 0x5) != 0, "Expected weap_saber.cog to ignite saber blade flags.");
        Assert.True(sim.BeginWeaponFire(playerId, mode: 0));
        Assert.True(player.SaberDamageActive);
        Assert.StartsWith("sabv", player.PovKey, StringComparison.OrdinalIgnoreCase);
        sim.EndWeaponFire(playerId, mode: 0);

        // 3. Thermal Detonator (weap_thermdet.cog) mode 0 bounce + mode 1 impact splash + VisualEffect
        sim.SetInv(playerId, PlayerInventory.BinThermDet, 4f);
        sim.SelectWeapon(playerId, PlayerInventory.BinThermDet);
        Assert.Equal("detv.3do", player.PovModel, ignoreCase: true);
        for (int i = 0; i < 20; i++)
            sim.StepWorld(0.05f);

        sim.UpdatePlayerInput(playerId, new Vector3(0f, -1f, 0f), 0, Vector3.UnitY);

        // Mode 1: hold & release throws bouncing +grenade2 (SITH_WF_FACEHITEXPLODE=0, SITH_PF_SURFACEBOUNCE=1)
        Assert.True(sim.BeginWeaponFire(playerId, mode: 1));
        for (int i = 0; i < 8; i++)
            sim.StepWorld(0.05f);
        Assert.True(sim.EndWeaponFire(playerId, mode: 1));
        Assert.Equal(3f, sim.GetInv(playerId, PlayerInventory.BinThermDet));
        Assert.Contains(sim.Projectiles, p => p.Bounce && p.UseGravity);

        // Wait past Mode 1 throwWait (0.8s) before throwing Mode 0 impact grenade (+grenade1)
        for (int i = 0; i < 18; i++)
            sim.StepWorld(0.05f);

        // Mode 0: spawn a fresh Ree-Yees ahead and detonate an impact grenade (+grenade1) beside it
        Vector3 npc2Pos = new(2.5f, 3.0f, 0.13f);
        int targetNpcId = sim.CreateThingAtPos("reeyeesgun", 0, npc2Pos, new Vector3(0f, 180f, 0f));
        sim.UpdatePlayerInput(playerId, new Vector3(npc2Pos.X, npc2Pos.Y - 1.15f, 0.2f), 0, Vector3.UnitY);
        float npc2HpBefore = sim.GetThingHealth(targetNpcId);
        Assert.Equal(50f, npc2HpBefore);
        Assert.True(sim.BeginWeaponFire(playerId, mode: 0));
        for (int i = 0; i < 6; i++)
            sim.StepWorld(0.05f);
        Assert.True(sim.EndWeaponFire(playerId, mode: 0));

        bool sawGrenadeVfx = false;
        for (int i = 0; i < 25; i++)
        {
            sim.StepWorld(0.05f);
            if (sim.VisualEffects.Count > 0)
                sawGrenadeVfx = true;
        }

        Assert.True(sawGrenadeVfx, "Expected thermal detonator explosion VisualEffect.");
        Assert.True(sim.GetThingHealth(targetNpcId) < npc2HpBefore, $"Expected splash damage on Stormtrooper Thing {targetNpcId} (before={npc2HpBefore}, after={sim.GetThingHealth(targetNpcId)}).");
    }

    [Fact]
    public void RetailItems_Bacta_FieldLight_And_IRGoggles_ExecuteRetailCogs()
    {
        using var vfs = TryMountRetailVfs(out var retailTemplates);
        if (vfs == null || retailTemplates == null)
            return;

        var arena = CreateBoxArenaLevel();
        arena.Templates = retailTemplates.Templates;
        var sim = new WorldSimulation(arena, vfs);
        int playerId = sim.GetLocalPlayerThing();
        var player = sim.LocalPlayer!;

        // 1. Bacta Tank (bin 40 -> +30 HP)
        player.Inventory.Health = 50f;
        sim.SetInv(playerId, PlayerInventory.BinBacta, 2f);
        Assert.True(sim.UseItem(playerId, PlayerInventory.BinBacta));
        Assert.Equal(80f, player.Inventory.Health);
        Assert.Equal(1f, sim.GetInv(playerId, PlayerInventory.BinBacta));

        // 2. Field Light (bin 42 -> actor flag 0x4 & battery drain)
        sim.SetInv(playerId, PlayerInventory.BinBattery, 100f);
        Assert.False(sim.IsFieldLightActive(playerId));
        Assert.True(sim.UseItem(playerId, PlayerInventory.BinFieldLight));
        Assert.True(sim.IsFieldLightActive(playerId));

        for (int i = 0; i < 20; i++)
            sim.StepWorld(0.1f); // 2s > 1.5s pulse -> drains battery

        Assert.True(player.Inventory.Battery < 100f);
        Assert.True(sim.UseItem(playerId, PlayerInventory.BinFieldLight));
        Assert.False(sim.IsFieldLightActive(playerId));

        // 3. IR Goggles (bin 41 -> IR mode tint)
        sim.SetInv(playerId, PlayerInventory.BinIrGoggles, 1f);
        Assert.True(sim.UseItem(playerId, PlayerInventory.BinIrGoggles));
        Assert.Equal(1, sim.IsInvActivated(playerId, PlayerInventory.BinIrGoggles));
        Assert.True(sim.CurrentScreenTint.Y >= 0.07f, $"Expected IR green tint >= 0.07, got {sim.CurrentScreenTint}");

        Assert.True(sim.UseItem(playerId, PlayerInventory.BinIrGoggles));
        Assert.Equal(0, sim.IsInvActivated(playerId, PlayerInventory.BinIrGoggles));
        Assert.True(sim.CurrentScreenTint.Y < 0.01f);
    }

    [Fact]
    public void RetailForceJump_HoldAndRelease_SpendsManaAndAppliesUpwardVelocity()
    {
        using var vfs = TryMountRetailVfs(out var retailTemplates);
        if (vfs == null || retailTemplates == null)
            return;

        var arena = CreateBoxArenaLevel();
        arena.Templates = retailTemplates.Templates;
        var sim = new WorldSimulation(arena, vfs);
        int playerId = sim.GetLocalPlayerThing();
        var player = sim.LocalPlayer!;

        sim.SetInv(playerId, PlayerInventory.BinForceRank, 4f);
        sim.SetInv(playerId, PlayerInventory.BinForceMana, 200f);
        sim.SetInv(playerId, PlayerInventory.BinForceJump, 4f);
        sim.UpdatePlayerInput(playerId, Vector3.Zero, 0, Vector3.UnitY, isGrounded: true);

        Assert.True(sim.BeginForcePower(playerId, PlayerInventory.BinForceJump, Vector3.UnitY));
        for (int i = 0; i < 6; i++)
            sim.StepWorld(0.1f);
        Assert.True(sim.EndForcePower(playerId, PlayerInventory.BinForceJump));

        Assert.True(player.Inventory.ForceMana < 200f);
        Assert.True(sim.TryConsumePlayerVelocityOverride(playerId, out Vector3 jumpVel));
        Assert.True(jumpVel.Z > 2.5f, $"Expected Force Jump velocity > 2.5, got {jumpVel}");
    }

    [Fact]
    public void SprSprite_Parse_DecodesRetailFormat_AndLoadsFromRes2Gob()
    {
        const string sample = """
            #material   type    width   height  geo     light   tex     extralight  xoff    yoff    zoff
            conx.mat    0       0.6     0.6     4       0       0       0.0         0.0    -0.1     0.22
            """;

        SprSprite parsed = SprSprite.Parse(sample);
        Assert.Equal("conx.mat", parsed.MaterialName);
        Assert.Equal(0, parsed.SpriteType);
        Assert.Equal(0.6f, parsed.Width, precision: 3);
        Assert.Equal(0.6f, parsed.Height, precision: 3);
        Assert.Equal(4, parsed.GeoMode);
        Assert.Equal(new Vector3(0f, -0.1f, 0.22f), parsed.Offset);

        using var vfs = TryMountRetailVfs(out _);
        if (vfs == null)
            return;

        Assert.True(SprSprite.TryLoad(vfs, "detx.spr", out SprSprite? detx));
        Assert.NotNull(detx);
        Assert.False(string.IsNullOrWhiteSpace(detx!.MaterialName));
    }

    [Fact]
    public void DedicatedServer_CogTimersScopeOwningPlayer_And_RemoveAddReusesSpawnCleanly()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/item_bacta.cog", """
            symbols
            message activated
            message timer
            end

            code
            activated:
                SetTimer(0.1);
                Return;
            timer:
                SetPlayerScore(GetLocalPlayerThing(), 77);
                Return;
            end
            """);

        var sim = new WorldSimulation(CreateBoxArenaLevel(), vfs, isMultiplayer: true, isDedicatedServer: true);
        Assert.Equal(-1, sim.GetLocalPlayerThing());

        PlayerState p1 = sim.AddPlayer(0, "Alpha", team: 1);
        PlayerState p2 = sim.AddPlayer(1, "Beta", team: 2);
        int p1Thing = p1.ThingIndex;

        Assert.True(sim.UseItem(p1Thing, PlayerInventory.BinBacta));
        sim.StepWorld(0.15f);

        Assert.Equal(-1, sim.GetLocalPlayerThing());
        Assert.Equal(77, p1.Score);
        Assert.Equal(0, p2.Score);

        int cogsBeforeRemove = sim.ActiveCogCount;
        Assert.True(cogsBeforeRemove >= 1);
        Assert.True(sim.RemovePlayer(p1Thing));
        Assert.True(sim.ActiveCogCount < cogsBeforeRemove);
        Assert.NotEqual(0, sim.GetThingFlags(p1Thing) & (int)(SithThingFlags.Disabled | SithThingFlags.Destroyed));

        PlayerState rejoined = sim.AddPlayer(0, "Alpha2", team: 1);
        Assert.Equal(p1Thing, rejoined.ThingIndex);
        Assert.Equal(0, sim.GetThingFlags(rejoined.ThingIndex) & (int)(SithThingFlags.Disabled | SithThingFlags.Destroyed));
    }

    [Fact]
    public void ForceAndItemActivation_EnforcesRank_PassesSenderRef_And_TicksActivateBinFire()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/item_bacta.cog", """
            symbols
            message activated
            end

            code
            activated:
                SetPlayerScore(GetSourceRef(), GetSenderRef());
                Return;
            end
            """);
        vfs.AddText("cog/force_lightning.cog", """
            symbols
            int player local
            message activated
            message fire
            message deactivated
            end

            code
            activated:
                player = GetSourceRef();
                SetInv(player, 14, GetSenderRef());
                ActivateBin(player, 0.1, GetSenderRef());
                Return;
            fire:
                player = GetSourceRef();
                SetPlayerScore(player, GetPlayerScore(player) + 1);
                Return;
            deactivated:
                player = GetSourceRef();
                DeactivateBin(player, GetSenderRef());
                Return;
            end
            """);

        var sim = new WorldSimulation(CreateBoxArenaLevel(), vfs);
        int p = sim.GetLocalPlayerThing();

        Assert.False(sim.BeginForcePower(p, PlayerInventory.BinForceLightning, Vector3.UnitY));
        Assert.False(sim.ActivateForcePower(PlayerInventory.BinForceLightning, Vector3.UnitY));

        Assert.True(sim.UseItem(p, PlayerInventory.BinBacta));
        Assert.Equal(PlayerInventory.BinBacta, sim.GetPlayerScore(p));

        sim.SetPlayerScore(p, 0);
        sim.SetInv(p, PlayerInventory.BinForceLightning, 2f);
        Assert.True(sim.BeginForcePower(p, PlayerInventory.BinForceLightning, Vector3.UnitY));
        Assert.Equal(PlayerInventory.BinForceLightning, (int)sim.GetInv(p, PlayerInventory.BinForceMana));
        Assert.Equal(1, sim.GetPlayerScore(p));

        sim.StepWorld(0.11f);
        Assert.Equal(2, sim.GetPlayerScore(p));
        sim.StepWorld(0.11f);
        Assert.Equal(3, sim.GetPlayerScore(p));

        Assert.True(sim.EndForcePower(p, PlayerInventory.BinForceLightning));
        sim.StepWorld(0.25f);
        Assert.Equal(3, sim.GetPlayerScore(p));
    }

    [Fact]
    public void WeaponReleaseGuard_IgnoresMismatchedMode_And_WeaponSwitchResetsSaberAndFireState()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/weap_bryar.cog", """
            symbols
            int player local
            message activated
            message deactivated
            end

            code
            activated:
                player = GetSourceRef();
                ActivateWeapon(player, 0.25, GetSenderRef());
                Return;
            deactivated:
                player = GetSourceRef();
                SetPlayerScore(player, GetPlayerScore(player) + 1);
                DeactivateWeapon(player, GetSenderRef());
                Return;
            end
            """);

        var sim = new WorldSimulation(CreateBoxArenaLevel(), vfs);
        int p = sim.GetLocalPlayerThing();
        PlayerState player = sim.LocalPlayer!;

        sim.SelectWeapon(p, PlayerInventory.BinBryar);
        Assert.True(sim.BeginWeaponFire(p, mode: 0));
        Assert.Equal(0, player.ActiveFireMode);

        Assert.False(sim.EndWeaponFire(p, mode: 1));
        Assert.Equal(0, sim.GetPlayerScore(p));
        Assert.Equal(0, player.ActiveFireMode);
        Assert.Equal(0f, sim.DeactivateWeapon(p, mode: 1));
        Assert.Equal(0, player.ActiveFireMode);

        Assert.True(sim.EndWeaponFire(p, mode: 0));
        Assert.Equal(1, sim.GetPlayerScore(p));
        Assert.Equal(-1, player.ActiveFireMode);

        // Select a weapon without a bin COG (weap_strifle.cog absent) and verify switching still clears saber/fire state.
        sim.SetInv(p, PlayerInventory.BinStRifle, 1f);
        sim.SelectWeapon(p, PlayerInventory.BinStRifle);
        sim.JkEnableSaber(p, 50f, 0.25f, 0.4f);
        player.ActiveFireMode = 0;
        player.FireRate = 0.2f;
        player.FireHoldElapsed = 0.5f;

        sim.SelectWeapon(p, PlayerInventory.BinBryar);
        Assert.False(player.SaberDamageActive);
        Assert.Equal(0f, player.SaberActiveTimer);
        Assert.Equal(-1, player.ActiveFireMode);
        Assert.Equal(0f, player.FireRate);
        Assert.Equal(0f, player.FireHoldElapsed);
    }

    [Fact]
    public void AddThingVel_And_ApplyForce_UpdatePlayerVelocityOverride()
    {
        var sim = new WorldSimulation(CreateBoxArenaLevel(), new InMemoryVfs());
        int p = sim.GetLocalPlayerThing();

        sim.SetThingVel(p, new Vector3(1f, 0f, 0f));
        sim.AddThingVel(p, new Vector3(0f, 2f, 0f));
        sim.ApplyForce(p, new Vector3(0f, 0f, 3f));

        Assert.True(sim.TryConsumePlayerVelocityOverride(p, out Vector3 vel));
        Assert.Equal(1f, vel.X, precision: 3);
        Assert.Equal(2f, vel.Y, precision: 3);
        Assert.Equal(3f, vel.Z, precision: 3);
    }

    [Fact]
    public void FireProjectile_ClampsObstructions_IgnoresPelvisZ_And_NonLoopingPovKeyClamps()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("3do/key/testmount.key", """
            SECTION: HEADER
            FRAMES 4
            FPS 10.0
            JOINTS 1

            SECTION: KEYFRAME NODES
            NODES 1
            NODE 0
            ENTRIES 1
            0: 0 0x0000 0.0 0.0 0.0 0.0 0.0 0.0
            0.0 0.0 0.0 0.0 0.0 0.0
            """);

        var arena = CreateBoxArenaLevel();
        var sim = new WorldSimulation(arena, vfs, isMultiplayer: true);
        PlayerState p1 = sim.LocalPlayer!;
        PlayerState p2 = sim.AddPlayer(1, "Target");

        // 1. Non-looping POV key clamps below key duration instead of wrapping back to 0.
        sim.JkPlayPovKey(p1.ThingIndex, "testmount.key", 0, flags: 0x14);
        Assert.False(p1.PovKeyLoop);
        float keyLen = sim.GetKeyLen("testmount.key");
        Assert.True(keyLen > 0.35f);
        sim.StepWorld(0.6f);
        Assert.True(p1.PovKeyTime > keyLen - 0.01f && p1.PovKeyTime < keyLen);

        // 2. Wall obstruction prevents tunneling past Y=4 wall; Pelvis Z mutation doesn't double-offset origin Z.
        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, 3.85f, 0f), 0, aimDir: Vector3.UnitY);
        arena.Things[p1.ThingIndex].Position = new Vector3(0f, 3.85f, 0.50f);
        sim.FireProjectile(p1.ThingIndex, "", "", 0, new Vector3(0f, 0.30f, 0f), Vector3.Zero, 1f, 0, 0f, 0f);

        Assert.Single(sim.Projectiles);
        Assert.True(sim.Projectiles[0].Position.Y < 4.0f);
        Assert.Equal(0.155f, sim.Projectiles[0].Position.Z, precision: 3);

        sim.StepWorld(0.05f);
        Assert.Empty(sim.Projectiles);

        // 3. Point-blank opponent obstruction pulls projectile spawn back so tick 1 hits instead of tunneling past.
        p2.Inventory.Shields = 0f;
        p2.Inventory.Health = 50f;
        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, 1.85f, 0f), 0, aimDir: Vector3.UnitY);
        sim.UpdatePlayerInput(p2.ThingIndex, new Vector3(0f, 2.00f, 0f), 0, aimDir: -Vector3.UnitY);
        sim.FireProjectile(p1.ThingIndex, "", "", 0, new Vector3(0f, 0.30f, 0f), Vector3.Zero, 1f, 0, 0f, 0f);

        Assert.Single(sim.Projectiles);
        Assert.True(sim.Projectiles[0].Position.Y <= 1.86f);
        sim.StepWorld(0.05f);
        Assert.True(p2.Inventory.Health < 50f);
    }
}
