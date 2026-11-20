using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Full-featured mock world context backed by <see cref="PlayerInventory"/> for testing
/// Level 1 Jedi Knight COG scripts end-to-end.
/// </summary>
public sealed class Level1CogWorldContext : ICogWorldContext
{
    private int _nextThingId = 500;

    public PlayerInventory PlayerInventory { get; } = new();
    public int LocalPlayerThing { get; set; } = 100;
    public int CurrentSector { get; set; } = 1;
    public int Difficulty { get; set; } = 1;
    public int MajorMode { get; set; }
    public int AutoReloadFlags { get; set; }
    public int CurrentWeapon { get; set; } = 1;
    public int AutoSelectWeaponResult { get; set; } = 2;
    public float RandValue { get; set; } = 0.5f;
    public int SenderIdOverride { get; set; } = -1;
    public int SourceRefOverride { get; set; } = -1;

    public Dictionary<int, int> ThingSectors { get; } = new();
    public Dictionary<int, Vector3> ThingPositions { get; } = new();
    public Dictionary<int, Vector3> ThingVelocities { get; } = new();
    public Dictionary<int, int> ThingFrames { get; } = new();
    public Dictionary<int, float> ThingHealth { get; } = new();
    public Dictionary<int, string> ThingTemplates { get; } = new();
    public Dictionary<int, int> ThingFlags { get; } = new();
    public Dictionary<int, int> ActorFlags { get; } = new();
    public Dictionary<int, int> ActorWeapons { get; } = new();
    public Dictionary<int, float> ThingLifeleft { get; } = new();
    public HashSet<int> DestroyedThings { get; } = [];
    public HashSet<int> StoppedThings { get; } = [];

    public Dictionary<int, int> SurfaceCels { get; } = new();
    public Dictionary<int, Vector3> SurfaceCenters { get; } = new();
    public HashSet<int> MovingThings { get; } = [];
    public Dictionary<int, bool> SectorAdjoins { get; } = new();
    public Dictionary<int, (float light, float delay)> SectorLights { get; } = new();

    public List<(string template, int refThing, int sector, Vector3 pos, Vector3 pyr, int spawnedId)> CreatedThings { get; } = [];
    public List<(int thingId, int frame, float speed)> MoveToFrameCalls { get; } = [];
    public List<(int thingId, int jointIdx)> AmputatedJoints { get; } = [];
    public List<(int thingId, string aiClass)> AiClassesSet { get; } = [];
    public List<(int thingId, int targetThing)> AiFlees { get; } = [];
    public List<(int thingId, int frame)> AiLookFrames { get; } = [];
    public List<(int thingId, int frame)> AiMoveFrames { get; } = [];
    public List<(int surfaceId, float fps, int flags)> SurfaceAnims { get; } = [];
    public List<int> StoppedSurfaceAnims { get; } = [];
    public List<(int surfaceId, Vector3 dir, float speed)> SlideSurfaces { get; } = [];
    public List<(string sound, int thingId, float volume, float minRad, float maxRad, int flags)> SoundCalls { get; } = [];
    public List<(string sound, Vector3 pos, float volume, float minRad, float maxRad, int flags)> SoundPosCalls { get; } = [];
    public List<(string sound, float volume, float pan, int flags)> SoundLocalCalls { get; } = [];
    public List<(int channel, float fadeOut)> StoppedSounds { get; } = [];
    public List<(int itemId, int playerId)> TakenItems { get; } = [];
    public List<(int player, int strId)> UniStringsPrinted { get; } = [];
    public List<int> EndLevelCalls { get; } = [];
    public List<(int thingId, int weapon)> SelectedWeapons { get; } = [];
    public List<(int thingId, string keyframe, int priority, int flags)> PlayedKeys { get; } = [];
    public List<string> PrintedMessages { get; } = [];

    public int GetThingSector(int thingId) => ThingSectors.GetValueOrDefault(thingId, 0);
    public Vector3 GetThingPos(int thingId) => ThingPositions.GetValueOrDefault(thingId, Vector3.Zero);
    public void MoveToFrame(int thingId, int frame, float speed) => MoveToFrameCalls.Add((thingId, frame, speed));
    public bool IsThingMoving(int thingId) => MovingThings.Contains(thingId);
    public int GetCurFrame(int thingId) => ThingFrames.GetValueOrDefault(thingId, 0);
    public int GetLocalPlayerThing() => LocalPlayerThing;
    public void SetSectorAdjoins(int sectorId, bool visible) => SectorAdjoins[sectorId] = visible;
    public void SetSectorLight(int sectorId, float light, float delay) => SectorLights[sectorId] = (light, delay);
    public int PlaySoundThing(string sound, int thingId, float volume, float minRad, float maxRad, int flags)
    {
        SoundCalls.Add((sound, thingId, volume, minRad, maxRad, flags));
        return SoundCalls.Count;
    }
    public int SendMessage(int cogId, string message) => 0;
    public void Print(string text) => PrintedMessages.Add(text);
    public float Rand() => RandValue;

    public int CreateThing(string template, int refThing)
    {
        int id = _nextThingId++;
        int sector = GetThingSector(refThing);
        Vector3 pos = GetThingPos(refThing);
        CreatedThings.Add((template, refThing, sector, pos, Vector3.Zero, id));
        ThingTemplates[id] = template;
        ThingSectors[id] = sector;
        ThingPositions[id] = pos;
        return id;
    }

    public int CreateThingAtPos(string template, int sector, Vector3 pos, Vector3 pyr)
    {
        int id = _nextThingId++;
        CreatedThings.Add((template, -1, sector, pos, pyr, id));
        ThingTemplates[id] = template;
        ThingSectors[id] = sector;
        ThingPositions[id] = pos;
        return id;
    }

    public void DestroyThing(int thingId) => DestroyedThings.Add(thingId);
    public void SetLifeleft(int thingId, float seconds) => ThingLifeleft[thingId] = seconds;

    public float DamageThing(int thingId, float damage, int flags, int damager)
    {
        if (thingId == LocalPlayerThing)
        {
            float before = PlayerInventory.Health;
            PlayerInventory.ApplyDamage(damage);
            return before - PlayerInventory.Health;
        }
        float hp = ThingHealth.GetValueOrDefault(thingId, 100f);
        float next = MathF.Max(0f, hp - damage);
        ThingHealth[thingId] = next;
        return hp - next;
    }

    public void HealThing(int thingId, float amount)
    {
        if (thingId == LocalPlayerThing)
            PlayerInventory.Heal(amount);
        else
            ThingHealth[thingId] = ThingHealth.GetValueOrDefault(thingId, 0f) + amount;
    }

    public float GetThingHealth(int thingId) =>
        thingId == LocalPlayerThing ? PlayerInventory.Health : ThingHealth.GetValueOrDefault(thingId, 0f);

    public string GetThingTemplate(int thingId) => ThingTemplates.GetValueOrDefault(thingId, string.Empty);
    public void SetThingVel(int thingId, Vector3 vel) => ThingVelocities[thingId] = vel;
    public Vector3 GetThingVel(int thingId) => ThingVelocities.GetValueOrDefault(thingId, Vector3.Zero);
    public void StopThing(int thingId) => StoppedThings.Add(thingId);
    public int PlayKey(int thingId, string keyframe, int priority, int flags)
    {
        PlayedKeys.Add((thingId, keyframe, priority, flags));
        return PlayedKeys.Count;
    }

    public int GetThingFlags(int thingId) => ThingFlags.GetValueOrDefault(thingId, 0);
    public void SetThingFlags(int thingId, int flags) => ThingFlags[thingId] = GetThingFlags(thingId) | flags;
    public void ClearThingFlags(int thingId, int flags) => ThingFlags[thingId] = GetThingFlags(thingId) & ~flags;
    public int GetActorFlags(int thingId) => ActorFlags.GetValueOrDefault(thingId, 0);
    public void SetActorFlags(int thingId, int flags) => ActorFlags[thingId] = GetActorFlags(thingId) | flags;
    public void ClearActorFlags(int thingId, int flags) => ActorFlags[thingId] = GetActorFlags(thingId) & ~flags;
    public int GetCurSector() => CurrentSector;

    public float ChangeInv(int thingId, int bin, float delta) => PlayerInventory.ChangeInv(bin, delta);
    public float GetInv(int thingId, int bin) => PlayerInventory.GetInv(bin);
    public void SetInv(int thingId, int bin, float val) => PlayerInventory.SetInv(bin, val);
    public float GetInvMax(int thingId, int bin) => PlayerInventory.GetInvMax(bin);
    public void SetGoalFlags(int thingId, int goalIdx, int flags) => PlayerInventory.SetGoalFlags(goalIdx, flags);
    public void ClearGoalFlags(int thingId, int goalIdx, int flags) => PlayerInventory.ClearGoalFlags(goalIdx, flags);
    public int GetGoalFlags(int thingId, int goalIdx) => PlayerInventory.GetGoalFlags(goalIdx);

    public void TakeItem(int itemThingId, int playerThingId) => TakenItems.Add((itemThingId, playerThingId));
    public int GetAutoReload() => AutoReloadFlags;
    public int AutoSelectWeapon(int thingId, int mode) => AutoSelectWeaponResult;
    public void SelectWeapon(int thingId, int weapon)
    {
        CurrentWeapon = weapon;
        SelectedWeapons.Add((thingId, weapon));
    }
    public int GetCurWeapon(int thingId) => CurrentWeapon;

    public void AmputateJoint(int thingId, int jointIdx) => AmputatedJoints.Add((thingId, jointIdx));
    public void AISetClass(int thingId, string aiClass) => AiClassesSet.Add((thingId, aiClass));
    public void AIFlee(int thingId, int targetThing) => AiFlees.Add((thingId, targetThing));
    public void AISetLookFrame(int thingId, int frame) => AiLookFrames.Add((thingId, frame));
    public void AISetMoveFrame(int thingId, int frame) => AiMoveFrames.Add((thingId, frame));
    public int GetActorWeapon(int thingId, int slot) => ActorWeapons.GetValueOrDefault(thingId, -1);

    public void SetSurfaceCel(int surfaceId, int cel) => SurfaceCels[surfaceId] = cel;
    public int GetSurfaceCel(int surfaceId) => SurfaceCels.GetValueOrDefault(surfaceId, 0);
    public void SlideSurface(int surfaceId, Vector3 dir, float speed) => SlideSurfaces.Add((surfaceId, dir, speed));
    public int SurfaceAnim(int surfaceId, float fps, int flags)
    {
        SurfaceAnims.Add((surfaceId, fps, flags));
        return SurfaceAnims.Count;
    }
    public void StopSurfaceAnim(int surfaceId) => StoppedSurfaceAnims.Add(surfaceId);
    public Vector3 SurfaceCenter(int surfaceId) => SurfaceCenters.GetValueOrDefault(surfaceId, Vector3.Zero);
    public int PlaySoundLocal(string sound, float volume, float pan, int flags)
    {
        SoundLocalCalls.Add((sound, volume, pan, flags));
        return SoundLocalCalls.Count;
    }
    public int PlaySoundPos(string sound, Vector3 pos, float volume, float minRad, float maxRad, int flags)
    {
        SoundPosCalls.Add((sound, pos, volume, minRad, maxRad, flags));
        return SoundPosCalls.Count;
    }
    public void StopSound(int channel, float fadeOut) => StoppedSounds.Add((channel, fadeOut));

    public void JkEndLevel(int success) => EndLevelCalls.Add(success);
    public void JkPrintUniString(int player, int strId) => UniStringsPrinted.Add((player, strId));
    public int GetMajorMode() => MajorMode;
    public int GetDifficulty() => Difficulty;
    public int GetSenderId() => SenderIdOverride;
    public int GetSourceRef() => SourceRefOverride;
}

/// <summary>
/// Unit tests for <see cref="PlayerInventory"/>, extended COG verbs, and retail Level 1 COG scripts
/// from JK1.GOB and Res2.gob.
/// </summary>
public class Level1CogTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    private static CogScript LoadRetailScript(string gobPath, string fileName)
    {
        using var gob = GobArchive.FromFile(gobPath);
        var entry = gob.Entries.First(e =>
            Path.GetFileName(e.Path.Replace('\\', '/')).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        string text = Encoding.ASCII.GetString(gob.ReadAllBytes(entry.Path));
        return CogParser.Parse(text, fileName);
    }

    [Fact]
    public void PlayerInventory_HealthShieldsAmmoSecretsGoalsRespawn_WorkCorrectly()
    {
        var inv = new PlayerInventory();
        Assert.Equal(100f, inv.Health);
        Assert.Equal(100f, inv.MaxHealth);
        Assert.Equal(0f, inv.Shields);
        Assert.False(inv.IsDead);
        Assert.False(inv.HasBryar);
        Assert.False(inv.HasLightsaber);

        // Shields absorb damage before health
        inv.AddShields(50f);
        Assert.Equal(50f, inv.Shields);
        inv.AddShields(300f); // clamped to 200
        Assert.Equal(200f, inv.Shields);

        inv.ApplyDamage(150f);
        Assert.Equal(50f, inv.Shields);
        Assert.Equal(100f, inv.Health);

        inv.ApplyDamage(80f);
        Assert.Equal(0f, inv.Shields);
        Assert.Equal(70f, inv.Health);

        inv.Heal(20f);
        Assert.Equal(90f, inv.Health);
        inv.Heal(50f); // clamped to MaxHealth (100)
        Assert.Equal(100f, inv.Health);

        // Ammo clamping
        inv.EnergyAmmo = 600f;
        inv.PowerAmmo = -10f;
        Assert.Equal(500f, inv.EnergyAmmo);
        Assert.Equal(0f, inv.PowerAmmo);

        // Weapons, secrets, goals
        inv.HasBryar = true;
        inv.HasLightsaber = true;
        inv.TotalSecrets = 3;
        inv.SecretsFound = 2;
        inv.SetGoalFlags(0, 1);
        inv.SetGoalFlags(0, 2);
        Assert.True(inv.HasBryar);
        Assert.True(inv.HasLightsaber);
        Assert.Equal(3, inv.GetGoalFlags(0));
        inv.ClearGoalFlags(0, 1);
        Assert.Equal(2, inv.GetGoalFlags(0));

        // Fatal damage and respawn
        inv.ApplyDamage(1000f);
        Assert.Equal(0f, inv.Health);
        Assert.True(inv.IsDead);

        inv.ResetForRespawn();
        Assert.False(inv.IsDead);
        Assert.Equal(100f, inv.Health);
        Assert.Equal(0f, inv.Shields);
        Assert.True(inv.HasBryar);
        Assert.Equal(2, inv.SecretsFound);
    }

    [Fact]
    public void CogVm_VectorBitAndWorldVerbs_OperateCorrectly()
    {
        const string scriptText = """
            symbols
            message startup
            vector sum local
            vector diff local
            vector scaled local
            int bits local
            int hasBit2 local
            int mode local
            int sec local
            int tflags local
            vector vel local
            end

            code
            startup:
                sum = VectorAdd(VectorSet(1.0, 2.0, 3.0), VectorSet(4.0, 5.0, 6.0));
                diff = VectorSub(VectorSet(5.0, 5.0, 5.0), VectorSet(1.0, 2.0, 3.0));
                scaled = VectorScale(VectorSet(1.0, 2.0, 3.0), 2.5);
                bits = BitSet(0x1, 0x4);
                hasBit2 = BitTest(bits, 0x4);
                bits = BitClear(bits, 0x1);
                mode = GetMajorMode();
                sec = GetCurSector();
                SetThingFlags(100, 0x10);
                tflags = GetThingFlags(100);
                ClearThingFlags(100, 0x10);
                SetThingVel(100, VectorSet(0.0, 3.0, 4.0));
                vel = GetThingVel(100);
                SlideSurface(77, VectorSet(1.0, 0.0, 0.0), 2.5);
                return;
            end
            """;

        var ctx = new Level1CogWorldContext { MajorMode = 2, CurrentSector = 42 };
        var vm = new CogVm(ctx);
        var inst = new CogInstance(CogParser.Parse(scriptText, "verb_test.cog"), cogId: 0);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup");

        Assert.Equal(new Vector3(5f, 7f, 9f), inst.GetSymbolValue("sum").AsVector3);
        Assert.Equal(new Vector3(4f, 3f, 2f), inst.GetSymbolValue("diff").AsVector3);
        Assert.Equal(new Vector3(2.5f, 5f, 7.5f), inst.GetSymbolValue("scaled").AsVector3);
        Assert.Equal(0x4, inst.GetSymbolValue("bits").AsInt);
        Assert.Equal(1, inst.GetSymbolValue("hasBit2").AsInt);
        Assert.Equal(2, inst.GetSymbolValue("mode").AsInt);
        Assert.Equal(42, inst.GetSymbolValue("sec").AsInt);
        Assert.Equal(0x10, inst.GetSymbolValue("tflags").AsInt);
        Assert.Equal(0, ctx.GetThingFlags(100));
        Assert.Equal(new Vector3(0f, 3f, 4f), inst.GetSymbolValue("vel").AsVector3);
        Assert.Single(ctx.SlideSurfaces);
        Assert.Equal((77, new Vector3(1f, 0f, 0f), 2.5f), ctx.SlideSurfaces[0]);
    }

    [Fact]
    public void Retail_00_DeathFloor_KillsPlayerOnTouch()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var script = LoadRetailScript(SteamRes2Path, "00_deathfloor.cog");
        var ctx = new Level1CogWorldContext { LocalPlayerThing = 100 };
        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 1);
        vm.AddInstance(inst);

        Assert.Equal(100f, ctx.PlayerInventory.Health);
        Assert.False(ctx.PlayerInventory.IsDead);

        vm.SendMessage(inst, "touched", senderRef: 10, senderId: 0, sourceRef: 100);

        Assert.Equal(0f, ctx.PlayerInventory.Health);
        Assert.True(ctx.PlayerInventory.IsDead);
    }

    [Fact]
    public void Retail_00_SecretAreas_CountsSecretsOncePerSector()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var script = LoadRetailScript(SteamRes2Path, "00_SecretAreas.cog");
        var ctx = new Level1CogWorldContext { LocalPlayerThing = 100 };
        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 2);
        vm.AddInstance(inst);

        // Bind secret1 -> sector 50, secret2 -> sector 51, remaining -> -1
        var args = new List<string> { "50", "51" };
        while (args.Count < 20) args.Add("-1");
        inst.BindPlacementArgs(new JklCogPlacement { ScriptName = "00_SecretAreas.cog", SymbolArgs = args });

        vm.SendMessage(inst, "startup");
        Assert.Equal(0, ctx.PlayerInventory.SecretsFound);
        Assert.Equal(2, ctx.PlayerInventory.TotalSecrets);

        // Enter secret1 (linkid=1 resolved automatically from senderRef=50)
        vm.SendMessage(inst, "entered", senderRef: 50);
        Assert.Equal(1, ctx.PlayerInventory.SecretsFound);
        Assert.Single(ctx.UniStringsPrinted);
        Assert.Equal((100, 360), ctx.UniStringsPrinted[0]);
        Assert.Single(ctx.SoundLocalCalls);
        Assert.Equal("Activate01.wav", ctx.SoundLocalCalls[0].sound);

        // Re-entering secret1 must not increment again
        vm.SendMessage(inst, "entered", senderRef: 50);
        Assert.Equal(1, ctx.PlayerInventory.SecretsFound);

        // Enter secret2 (linkid=2 resolved from senderRef=51)
        vm.SendMessage(inst, "entered", senderRef: 51);
        Assert.Equal(2, ctx.PlayerInventory.SecretsFound);
    }

    [Fact]
    public void Retail_01_Goal0_And_01_EndLevel_CompleteLevelFlow()
    {
        if (!File.Exists(SteamJk1Path)) return;

        var endLevelScript = LoadRetailScript(SteamJk1Path, "01_endlevel.cog");
        var goal0Script = LoadRetailScript(SteamJk1Path, "01_goal0.cog");

        var ctx = new Level1CogWorldContext { LocalPlayerThing = 100 };
        var vm = new CogVm(ctx);

        var endInst = new CogInstance(endLevelScript, cogId: 0);
        var goalInst = new CogInstance(goal0Script, cogId: 1);
        goalInst.BindPlacementArgs(new JklCogPlacement { ScriptName = "01_goal0.cog", SymbolArgs = ["250"] });

        vm.AddInstance(endInst);
        vm.AddInstance(goalInst);

        // Startup initializes master COG, goal 0 displayed, fists + bryar + 50 energy ammo
        vm.SendMessage(endInst, "startup");
        Assert.Equal(0, vm.MasterCogId);
        Assert.Equal(1000f, ctx.PlayerInventory.GetInv(99));
        Assert.Equal(1, ctx.PlayerInventory.GetGoalFlags(0));
        Assert.True(ctx.PlayerInventory.HasBryar);
        Assert.Equal(50f, ctx.PlayerInventory.EnergyAmmo);

        // Sighting 8t88 triggers goal0 -> SendMessage(GetMasterCOG(), user0) -> 01_endlevel user0
        vm.SendMessage(goalInst, "sighted");
        Assert.Single(ctx.AiLookFrames);
        Assert.Equal((250, 1), ctx.AiLookFrames[0]);
        Assert.Single(ctx.AiMoveFrames);
        Assert.Equal((250, 1), ctx.AiMoveFrames[0]);

        // Goal 0 now has DISPLAYED (1) | COMPLETED (2) = 3, Kyle frozen/invuln, sleeping 3.0s
        Assert.Equal(3, ctx.PlayerInventory.GetGoalFlags(0));
        Assert.Contains(ctx.SoundCalls, s => s.sound.Equals("Accomplish1.wav", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ctx.UniStringsPrinted, u => u == (100, 350));
        Assert.Contains(100, ctx.StoppedThings);
        Assert.Equal(0xa00008, ctx.GetActorFlags(100));
        Assert.True(endInst.IsSleeping);
        Assert.Empty(ctx.EndLevelCalls);

        // Advance past 3.0s sleep -> clears invuln (0x8) and calls JkEndLevel(1)
        vm.Update(3.1f);
        Assert.False(endInst.IsSleeping);
        Assert.Equal(0xa00000, ctx.GetActorFlags(100));
        Assert.Equal([1], ctx.EndLevelCalls);

        // Shutdown awards bonus force star when all secrets found
        ctx.PlayerInventory.TotalSecrets = 3;
        ctx.PlayerInventory.SecretsFound = 3;
        vm.SendMessage(endInst, "shutdown");
        Assert.Equal(1f, ctx.PlayerInventory.GetInv(PlayerInventory.BinNewStars));
    }

    [Fact]
    public void Retail_01_NeonSign_AnimatesAndBreaksOnDamage()
    {
        if (!File.Exists(SteamJk1Path)) return;

        var script = LoadRetailScript(SteamJk1Path, "01_neonsign.cog");
        var ctx = new Level1CogWorldContext();
        ctx.SurfaceCels[30] = 1; // currently animated/lit cel
        ctx.SurfaceCenters[30] = new Vector3(1f, 2f, 3f);

        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 3);
        inst.BindPlacementArgs(new JklCogPlacement { ScriptName = "01_neonsign.cog", SymbolArgs = ["30", "2.0"] });
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup");
        Assert.Single(ctx.SurfaceAnims);
        Assert.Equal((30, 2.0f, 0x5), ctx.SurfaceAnims[0]);

        vm.SendMessage(inst, "damaged", senderRef: 30, senderId: 0, sourceRef: 777);
        Assert.Single(ctx.StoppedSurfaceAnims);
        Assert.Equal(30, ctx.StoppedSurfaceAnims[0]);
        Assert.Equal(0, ctx.GetSurfaceCel(30));
        Assert.Single(ctx.SoundPosCalls);
        Assert.Equal("00ElectrExplo01.wav", ctx.SoundPosCalls[0].sound);
        Assert.Single(ctx.CreatedThings);
        Assert.Equal("+sparks", ctx.CreatedThings[0].template);
        Assert.Equal(777, ctx.CreatedThings[0].refThing);

        // Subsequent damage when cel is already 0 does nothing
        vm.SendMessage(inst, "damaged", senderRef: 30, senderId: 0, sourceRef: 777);
        Assert.Single(ctx.CreatedThings);
    }

    [Fact]
    public void Retail_01_FunicularA_MovesBetweenFramesOnSwitchAndTimer()
    {
        if (!File.Exists(SteamJk1Path)) return;

        var script = LoadRetailScript(SteamJk1Path, "01_funicularA.cog");
        var ctx = new Level1CogWorldContext { Difficulty = 2 }; // Hard: speed=8.0, sleeptime=2.0
        ctx.SurfaceCenters[21] = new Vector3(5f, 6f, 7f);

        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 4);
        inst.BindPlacementArgs(new JklCogPlacement
        {
            ScriptName = "01_funicularA.cog",
            SymbolArgs = ["40", "21", "22", "8.0", "4.0", "2.0", "4.0"]
        });
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup");
        Assert.Equal(8.0f, inst.GetSymbolValue("speed").AsFloat);
        Assert.Equal(2.0f, inst.GetSymbolValue("sleeptime").AsFloat);

        // Activate switch1 (surface 21, linkid=1)
        vm.SendMessage(inst, "activate", senderRef: 21);
        Assert.Equal(1, ctx.GetSurfaceCel(21));
        Assert.Equal(1, ctx.GetSurfaceCel(22));
        Assert.Single(ctx.SoundPosCalls);
        Assert.Single(ctx.MoveToFrameCalls);
        Assert.Equal((40, 1, 8.0f), ctx.MoveToFrameCalls[0]);

        // Arrived at goal frame 1 -> starts 2.0s return timer
        ctx.ThingFrames[40] = 1;
        vm.SendMessage(inst, "arrived");
        Assert.True(inst.TimerActive);

        // Timer elapses -> moves back to frame 0
        vm.Update(2.1f);
        Assert.Equal(2, ctx.MoveToFrameCalls.Count);
        Assert.Equal((40, 0, 8.0f), ctx.MoveToFrameCalls[1]);

        // Arrived at frame 0 -> resets switch cels
        ctx.ThingFrames[40] = 0;
        vm.SendMessage(inst, "arrived");
        Assert.Equal(0, ctx.GetSurfaceCel(21));
        Assert.Equal(0, ctx.GetSurfaceCel(22));
    }

    [Fact]
    public void Retail_01_TieBomber_ExecutesSleepAndMultipleExtendedTimers()
    {
        if (!File.Exists(SteamJk1Path)) return;

        var script = LoadRetailScript(SteamJk1Path, "01_TieBomber.cog");
        var ctx = new Level1CogWorldContext();
        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 5);
        inst.BindPlacementArgs(new JklCogPlacement { ScriptName = "01_TieBomber.cog", SymbolArgs = ["60"] });
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup");
        vm.SendMessage(inst, "sighted");
        Assert.True(inst.IsSleeping);

        // Wake after 2.0s sleep -> PlayKey, startup sound, arms SetTimerEx(4.0, 1) & SetTimerEx(7.2, 2)
        vm.Update(2.1f);
        Assert.False(inst.IsSleeping);
        Assert.Single(ctx.PlayedKeys);
        Assert.Equal((60, "01VTieB.key", 1, 0x14), ctx.PlayedKeys[0]);
        Assert.Single(ctx.SoundCalls);
        Assert.Equal("TieBomberStartup.wav", ctx.SoundCalls[0].sound);

        // At +4.1s, timer 1 fires -> engine whine sound
        vm.Update(4.1f);
        Assert.Equal(2, ctx.SoundCalls.Count);
        Assert.Equal("TieEngineWhine01.wav", ctx.SoundCalls[1].sound);
        Assert.Empty(ctx.DestroyedThings);

        // At +3.2s (total 7.3s), timer 2 fires -> StopSound + DestroyThing(60)
        vm.Update(3.2f);
        Assert.Single(ctx.StoppedSounds);
        Assert.Equal((2, 3.0f), ctx.StoppedSounds[0]);
        Assert.Contains(60, ctx.DestroyedThings);
    }

    [Fact]
    public void Retail_Powerups_HealthShieldsEnergy_WorkWithPlayerInventory()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var healthScript = LoadRetailScript(SteamRes2Path, "pow_health.cog");
        var shieldsScript = LoadRetailScript(SteamRes2Path, "pow_shields.cog");
        var energyScript = LoadRetailScript(SteamRes2Path, "pow_energy.cog");

        var ctx = new Level1CogWorldContext { LocalPlayerThing = 100, AutoReloadFlags = 1, AutoSelectWeaponResult = 2 };
        var vm = new CogVm(ctx);

        var healthInst = new CogInstance(healthScript, cogId: 10);
        var shieldsInst = new CogInstance(shieldsScript, cogId: 11);
        var energyInst = new CogInstance(energyScript, cogId: 12);
        vm.AddInstance(healthInst);
        vm.AddInstance(shieldsInst);
        vm.AddInstance(energyInst);

        // Health: full health -> not taken; damaged (60) -> taken & healed to 80
        vm.SendMessage(healthInst, "touched", senderRef: 301, senderId: 0, sourceRef: 100);
        Assert.Empty(ctx.TakenItems);

        ctx.PlayerInventory.Health = 60f;
        vm.SendMessage(healthInst, "touched", senderRef: 301, senderId: 0, sourceRef: 100);
        Assert.Single(ctx.TakenItems);
        vm.SendMessage(healthInst, "taken", senderRef: 301, senderId: 0, sourceRef: 100);
        Assert.Equal(80f, ctx.PlayerInventory.Health);

        // Shields: adds 20 shields
        vm.SendMessage(shieldsInst, "touched", senderRef: 302, senderId: 0, sourceRef: 100);
        Assert.Equal(2, ctx.TakenItems.Count);
        vm.SendMessage(shieldsInst, "taken", senderRef: 302, senderId: 0, sourceRef: 100);
        Assert.Equal(20f, ctx.PlayerInventory.Shields);

        // Energy: adds 20 energy & auto-selects energy weapon when bin was previously 0
        vm.SendMessage(energyInst, "touched", senderRef: 303, senderId: 0, sourceRef: 100);
        Assert.Equal(3, ctx.TakenItems.Count);
        vm.SendMessage(energyInst, "taken", senderRef: 303, senderId: 0, sourceRef: 100);
        Assert.Equal(20f, ctx.PlayerInventory.EnergyAmmo);
        Assert.Single(ctx.SelectedWeapons);
        Assert.Equal((100, 2), ctx.SelectedWeapons[0]);
    }

    [Fact]
    public void Retail_ActorRb_DamagedKilledAndSkillForcePull_WorkCorrectly()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var script = LoadRetailScript(SteamRes2Path, "actor_rb.cog");
        var ctx = new Level1CogWorldContext { LocalPlayerThing = 100, RandValue = 0.01f };
        ctx.ThingHealth[200] = 30f;
        ctx.ThingTemplates[200] = "reeyeesGUN";
        ctx.ThingSectors[200] = 15;
        ctx.ThingPositions[200] = new Vector3(1f, 2f, 3f);
        ctx.ActorWeapons[200] = 1;

        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 20);
        vm.AddInstance(inst);

        // 1. Damaged by saber (param1 == 16): doubles damage (20 -> 40 >= 30 hp) and dismembers joint 5
        var dmgResult = vm.SendMessage(inst, "damaged", senderRef: 200, [20, 16]);
        Assert.Equal(40f, dmgResult.AsFloat);
        Assert.Contains((200, 5), ctx.AmputatedJoints);
        Assert.Contains(ctx.CreatedThings, t => t.template == "+RYlimb" && t.sector == 15);
        int limbId = ctx.CreatedThings.First(t => t.template == "+RYlimb").spawnedId;
        Assert.Equal(new Vector3(0f, 0.7f, 1.4f), ctx.GetThingVel(limbId));

        // 2. Killed: amputates joint 3 and drops +DSTRifle with 200s lifeleft
        vm.SendMessage(inst, "killed", senderRef: 200);
        Assert.Contains((200, 3), ctx.AmputatedJoints);
        var dropped = ctx.CreatedThings.First(t => t.template == "+DSTRifle" && t.refThing == 200);
        Assert.Equal(200f, ctx.ThingLifeleft[dropped.spawnedId]);

        // 3. Skill: Force Pull (bin 24) disarms and makes Ree Yees flee with noweapon.ai
        var pulled = vm.SendMessage(inst, "skill", senderRef: 200, [24]);
        Assert.True(pulled.AsInt >= 500);
        Assert.Equal(30f, ctx.ThingLifeleft[pulled.AsInt]);
        Assert.Single(ctx.AiClassesSet);
        Assert.Equal((200, "noweapon.ai"), ctx.AiClassesSet[0]);
        Assert.Single(ctx.AiFlees);
        Assert.Equal((200, 100), ctx.AiFlees[0]);

        // Force Grip (31) -> 10, Deadly Sight (34) -> 5
        Assert.Equal(10, vm.SendMessage(inst, "skill", senderRef: 200, [31]).AsInt);
        Assert.Equal(5, vm.SendMessage(inst, "skill", senderRef: 200, [34]).AsInt);
    }

    [Fact]
    public void Retail_01_CargoElev1_And_00_SurfAnim_RunCleanly()
    {
        if (!File.Exists(SteamJk1Path) || !File.Exists(SteamRes2Path)) return;

        var cargoScript = LoadRetailScript(SteamJk1Path, "01_cargoelev1.cog");
        var surfAnimScript = LoadRetailScript(SteamRes2Path, "00_surfanim.cog");

        var ctx = new Level1CogWorldContext();
        ctx.SurfaceCenters[80] = new Vector3(10f, 0f, 2f);
        var vm = new CogVm(ctx);

        var cargoInst = new CogInstance(cargoScript, cogId: 30);
        cargoInst.BindPlacementArgs(new JklCogPlacement { ScriptName = "01_cargoelev1.cog", SymbolArgs = ["80", "90", "8.0"] });

        var surfInst = new CogInstance(surfAnimScript, cogId: 31);
        surfInst.BindPlacementArgs(new JklCogPlacement { ScriptName = "00_surfanim.cog", SymbolArgs = ["81", "82", "-1", "-1", "5.0"] });

        vm.AddInstance(cargoInst);
        vm.AddInstance(surfInst);

        vm.SendMessage(surfInst, "startup");
        Assert.Equal(2, ctx.SurfaceAnims.Count);
        Assert.Equal((81, 5.0f, 0x1), ctx.SurfaceAnims[0]);
        Assert.Equal((82, 5.0f, 0x1), ctx.SurfaceAnims[1]);

        vm.SendMessage(cargoInst, "activate", senderRef: 80);
        Assert.Equal(1, ctx.GetSurfaceCel(80));
        Assert.Single(ctx.MoveToFrameCalls);
        Assert.Equal((90, 1, 8.0f), ctx.MoveToFrameCalls[0]);

        vm.SendMessage(cargoInst, "arrived");
        Assert.Equal(0, ctx.GetSurfaceCel(80));
    }
}
