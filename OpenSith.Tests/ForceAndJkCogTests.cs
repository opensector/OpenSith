using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// Mock <see cref="ICogWorldContext"/> recording Force power, saber, and boss/actor COG verb calls.
/// </summary>
public sealed class ForceCogWorldContext : ICogWorldContext
{
    private int _nextThingId = 500;
    private int _nextSoundChannel = 10;
    private int _viewScanIdx;

    public int LocalPlayerThing { get; set; } = 100;
    public int Difficulty { get; set; } = 1;
    public int MajorMode { get; set; }
    public int CurrentWeapon { get; set; } = 1;
    public int CurInvWeapon { get; set; } = 10;
    public int CurrentCamera { get; set; }
    public int SaberCam { get; set; }
    public float RandValue { get; set; } = 0.5f;
    public bool CutsceneActive { get; private set; }

    public Dictionary<int, int> ThingSectors { get; } = new() { [100] = 1 };
    public Dictionary<int, Vector3> ThingPositions { get; } = new() { [100] = Vector3.Zero };
    public Dictionary<int, Vector3> ThingVelocities { get; } = new();
    public Dictionary<int, Vector3> ThingRotVelocities { get; } = new();
    public Dictionary<int, Vector3> ThingLooks { get; } = new();
    public Dictionary<int, float> ThingHealth { get; } = new() { [100] = 50f };
    public Dictionary<int, float> ThingMaxHealth { get; } = new() { [100] = 100f };
    public Dictionary<int, int> ThingFlags { get; } = new();
    public Dictionary<int, int> ActorFlags { get; } = new();
    public Dictionary<int, int> PhysicsFlags { get; } = new();
    public Dictionary<int, int> JkFlags { get; } = new();
    public Dictionary<int, int> ThingTypes { get; } = new() { [100] = 10 }; // 10 = player
    public Dictionary<int, int> ThingClassCogs { get; } = new();
    public Dictionary<int, int> ThingGeoModes { get; } = new();
    public Dictionary<int, float> ThingLights { get; } = new();
    public Dictionary<int, float> ActorExtraSpeeds { get; } = new();
    public Dictionary<int, int> ArmedModes { get; } = new();
    public Dictionary<int, string> WeaponMeshes { get; } = new();
    public Dictionary<int, string> PovModels { get; } = new();

    public Dictionary<(int thing, int bin), float> Inventory { get; } = new();
    public Dictionary<(int thing, int bin), float> InventoryMax { get; } = new();
    public Dictionary<(int thing, int bin), int> InvActivated { get; } = new();
    public Dictionary<(int thing, int bin), int> InvAvailable { get; } = new();

    public List<int> ViewTargets { get; } = [];
    public HashSet<(int a, int b)> LineOfSight { get; } = [];
    public Dictionary<(int a, int b), float> ViewDots { get; } = new();

    public List<(int thingId, Vector3 force)> AppliedForces { get; } = [];
    public List<(int target, int source, float p0, float p1)> SkillTargetCalls { get; } = [];
    public List<(int thingId, float dmg, float len, float unk)> EnableSaberCalls { get; } = [];
    public List<int> DisableSaberCalls { get; } = [];
    public List<(int thingId, string sideMat, string tipMat, float baseRad, float tipRad, float len)> SaberInfoCalls { get; } = [];
    public List<(int thingId, int bin, float wait)> BinWaits { get; } = [];
    public List<(int weaponThing, float fireRate, int mode)> ActivateWeaponCalls { get; } = [];
    public List<(int thingId, string arg)> ParseArgCalls { get; } = [];
    public List<string> SoundsPlayed { get; } = [];

    public int GetThingSector(int thingId) => ThingSectors.GetValueOrDefault(thingId, 1);
    public Vector3 GetThingPos(int thingId) => ThingPositions.GetValueOrDefault(thingId, Vector3.Zero);
    public void MoveToFrame(int thingId, int frame, float speed) { }
    public bool IsThingMoving(int thingId) => false;
    public int GetCurFrame(int thingId) => 0;
    public int GetLocalPlayerThing() => LocalPlayerThing;
    public void SetSectorAdjoins(int sectorId, bool visible) { }
    public void SetSectorLight(int sectorId, float light, float delay) { }
    public int PlaySoundThing(string sound, int thingId, float volume, float minRad, float maxRad, int flags)
    {
        SoundsPlayed.Add(sound);
        return ++_nextSoundChannel;
    }
    public int PlaySoundLocal(string sound, float volume, float pan, int flags)
    {
        SoundsPlayed.Add(sound);
        return ++_nextSoundChannel;
    }
    public int SendMessage(int cogId, string message) => 0;
    public void Print(string text) { }
    public float Rand() => RandValue;

    public int CreateThing(string template, int refThing) => ++_nextThingId;
    public int CreateThingAtPos(string template, int sector, Vector3 pos, Vector3 pyr) => ++_nextThingId;
    public void HealThing(int thingId, float amount)
    {
        float cur = ThingHealth.GetValueOrDefault(thingId, 0f);
        float max = ThingMaxHealth.GetValueOrDefault(thingId, 100f);
        ThingHealth[thingId] = MathF.Min(cur + amount, max);
    }
    public float GetThingHealth(int thingId) => ThingHealth.GetValueOrDefault(thingId, 0f);
    public float GetThingMaxHealth(int thingId) => ThingMaxHealth.GetValueOrDefault(thingId, 100f);
    public void SetThingVel(int thingId, Vector3 vel) => ThingVelocities[thingId] = vel;
    public void AddThingVel(int thingId, Vector3 vel) => ThingVelocities[thingId] = GetThingVel(thingId) + vel;
    public Vector3 GetThingVel(int thingId) => ThingVelocities.GetValueOrDefault(thingId, Vector3.Zero);
    public void SetThingRotVel(int thingId, Vector3 rotVel) => ThingRotVelocities[thingId] = rotVel;
    public Vector3 GetThingRotVel(int thingId) => ThingRotVelocities.GetValueOrDefault(thingId, Vector3.Zero);
    public void ApplyForce(int thingId, Vector3 force) => AppliedForces.Add((thingId, force));
    public void ThingLight(int thingId, float light, float fadeTime) => ThingLights[thingId] = light;
    public float GetThingLight(int thingId) => ThingLights.GetValueOrDefault(thingId, 0f);
    public int GetThingFlags(int thingId) => ThingFlags.GetValueOrDefault(thingId, 0);
    public void SetThingFlags(int thingId, int flags) => ThingFlags[thingId] = GetThingFlags(thingId) | flags;
    public void ClearThingFlags(int thingId, int flags) => ThingFlags[thingId] = GetThingFlags(thingId) & ~flags;
    public int GetActorFlags(int thingId) => ActorFlags.GetValueOrDefault(thingId, 0);
    public void SetActorFlags(int thingId, int flags) => ActorFlags[thingId] = GetActorFlags(thingId) | flags;
    public void ClearActorFlags(int thingId, int flags) => ActorFlags[thingId] = GetActorFlags(thingId) & ~flags;
    public int GetPhysicsFlags(int thingId) => PhysicsFlags.GetValueOrDefault(thingId, 0);
    public void SetPhysicsFlags(int thingId, int flags) => PhysicsFlags[thingId] = GetPhysicsFlags(thingId) | flags;
    public void ClearPhysicsFlags(int thingId, int flags) => PhysicsFlags[thingId] = GetPhysicsFlags(thingId) & ~flags;
    public int GetThingClassCog(int thingId) => ThingClassCogs.GetValueOrDefault(thingId, -1);
    public int GetThingGeoMode(int thingId) => ThingGeoModes.GetValueOrDefault(thingId, 0);
    public void SetThingCurGeoMode(int thingId, int geoMode) => ThingGeoModes[thingId] = geoMode;
    public int GetThingType(int thingId) => ThingTypes.GetValueOrDefault(thingId, 0);
    public void SetThingLook(int thingId, Vector3 lookVec) => ThingLooks[thingId] = lookVec;
    public void SetActorExtraSpeed(int thingId, float extraSpeed) => ActorExtraSpeeds[thingId] = extraSpeed;
    public void SetArmedMode(int thingId, int mode) => ArmedModes[thingId] = mode;
    public void ParseArg(int thingId, string argStr) => ParseArgCalls.Add((thingId, argStr));

    public float ChangeInv(int thingId, int bin, float delta)
    {
        float cur = Inventory.GetValueOrDefault((thingId, bin), 0f);
        float next = MathF.Max(0f, cur + delta);
        Inventory[(thingId, bin)] = next;
        return next;
    }
    public float GetInv(int thingId, int bin) => Inventory.GetValueOrDefault((thingId, bin), 0f);
    public void SetInv(int thingId, int bin, float val) => Inventory[(thingId, bin)] = val;
    public float GetInvMax(int thingId, int bin) => InventoryMax.GetValueOrDefault((thingId, bin), 100f);
    public int IsInvActivated(int thingId, int bin) => InvActivated.GetValueOrDefault((thingId, bin), 0);
    public void SetInvActivated(int thingId, int bin, int activated) => InvActivated[(thingId, bin)] = activated;
    public void SetInvAvailable(int thingId, int bin, int available) => InvAvailable[(thingId, bin)] = available;
    public int IsInvAvailable(int thingId, int bin) => InvAvailable.GetValueOrDefault((thingId, bin), 0);
    public void SetBinWait(int thingId, int bin, float wait) => BinWaits.Add((thingId, bin, wait));
    public int GetCurWeapon(int thingId) => CurrentWeapon;
    public void SetCurWeapon(int thingId, int binIdx) => CurrentWeapon = binIdx;
    public void SelectWeapon(int thingId, int weapon) => CurrentWeapon = weapon;
    public int GetCurInvWeapon(int thingId) => CurInvWeapon;
    public void ActivateWeapon(int weaponThing, float fireRate, int mode) => ActivateWeaponCalls.Add((weaponThing, fireRate, mode));

    public float SkillTarget(int targetThing, int sourceThing, float param0, float param1)
    {
        SkillTargetCalls.Add((targetThing, sourceThing, param0, param1));
        return 1f;
    }
    public int FirstThingInView(int viewerThing, float fov, float maxDist, int typeMask)
    {
        _viewScanIdx = 0;
        return ViewTargets.Count > 0 ? ViewTargets[_viewScanIdx++] : -1;
    }
    public int NextThingInView() => _viewScanIdx < ViewTargets.Count ? ViewTargets[_viewScanIdx++] : -1;
    public int HasLOS(int thingA, int thingB) => LineOfSight.Contains((thingA, thingB)) ? 1 : 0;
    public float ThingViewDot(int viewerThing, int targetThing) => ViewDots.GetValueOrDefault((viewerThing, targetThing), 0.95f);

    public int GetCurrentCamera() => CurrentCamera;
    public void JkSetFlags(int thingId, int flags) => JkFlags[thingId] = JkGetFlags(thingId) | flags;
    public int JkGetFlags(int thingId) => JkFlags.GetValueOrDefault(thingId, 0);
    public void JkClearFlags(int thingId, int flags) => JkFlags[thingId] = JkGetFlags(thingId) & ~flags;
    public void JkSetWeaponMesh(int thingId, string model) => WeaponMeshes[thingId] = model;
    public string JkGetWeaponMesh(int thingId) => WeaponMeshes.GetValueOrDefault(thingId, string.Empty);
    public void JkEnableSaber(int thingId, float damage, float collideLength, float unknown) =>
        EnableSaberCalls.Add((thingId, damage, collideLength, unknown));
    public void JkDisableSaber(int thingId) => DisableSaberCalls.Add(thingId);
    public void JkSetSaberInfo(int thingId, string sideMat, string tipMat, float baseRad, float tipRad, float length, string wallSparks, string bloodSparks, string saberSparks) =>
        SaberInfoCalls.Add((thingId, sideMat, tipMat, baseRad, tipRad, length));
    public void JkSetPovModel(int thingId, string model) => PovModels[thingId] = model;
    public int JkGetSaberCam() => SaberCam;
    public void JkBeginCutscene() => CutsceneActive = true;
    public void JkEndCutscene() => CutsceneActive = false;
    public int GetMajorMode() => MajorMode;
    public int GetDifficulty() => Difficulty;
}

public class ForceAndJkCogTests(ITestOutputHelper output)
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
    public void Retail_Force_Saber_And_Actor_Cogs_Have_Zero_Unknown_Verbs()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var unknownByVerb = new SortedDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var parsedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var gobPath in new[] { SteamRes2Path, SteamJk1Path })
        {
            if (!File.Exists(gobPath)) continue;
            using var gob = GobArchive.FromFile(gobPath);
            foreach (var entry in gob.Entries)
            {
                string fileName = Path.GetFileName(entry.Path.Replace('\\', '/'));
                if (!fileName.EndsWith(".cog", StringComparison.OrdinalIgnoreCase)) continue;
                bool isTarget = fileName.StartsWith("force_", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("weap_saber.cog", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("actor_", StringComparison.OrdinalIgnoreCase);
                if (!isTarget) continue;

                string text = Encoding.ASCII.GetString(gob.ReadAllBytes(entry.Path));
                var script = CogParser.Parse(text, fileName);
                var program = CogCompiler.Compile(script);
                parsedFiles.Add(fileName);

                foreach (var instr in program.Instructions)
                {
                    if (instr.Expr != null)
                        CollectUnsupportedVerbs(instr.Expr, fileName, unknownByVerb);
                }
            }
        }

        output.WriteLine($"Audited {parsedFiles.Count} retail Force/Saber/Actor COG scripts.");
        Assert.True(parsedFiles.Count >= 50, $"Expected at least 50 scripts, got {parsedFiles.Count}");
        Assert.Contains("force_jump.cog", parsedFiles);
        Assert.Contains("force_speed.cog", parsedFiles);
        Assert.Contains("force_pull.cog", parsedFiles);
        Assert.Contains("force_heal.cog", parsedFiles);
        Assert.Contains("weap_saber.cog", parsedFiles);
        Assert.Empty(unknownByVerb);
    }

    private static void CollectUnsupportedVerbs(CogExpr expr, string fileName, SortedDictionary<string, HashSet<string>> unknown)
    {
        switch (expr)
        {
            case CallVerbExpr call:
                if (!CogVm.IsVerbSupported(call.VerbName))
                {
                    if (!unknown.TryGetValue(call.VerbName, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        unknown[call.VerbName] = set;
                    }
                    set.Add(fileName);
                }
                foreach (var arg in call.Arguments)
                    CollectUnsupportedVerbs(arg, fileName, unknown);
                break;
            case BinaryExpr bin:
                CollectUnsupportedVerbs(bin.Left, fileName, unknown);
                CollectUnsupportedVerbs(bin.Right, fileName, unknown);
                break;
            case UnaryExpr un:
                CollectUnsupportedVerbs(un.Operand, fileName, unknown);
                break;
            case IndexExpr idx:
                CollectUnsupportedVerbs(idx.Target, fileName, unknown);
                CollectUnsupportedVerbs(idx.Index, fileName, unknown);
                break;
            case AssignExpr assign:
                CollectUnsupportedVerbs(assign.Target, fileName, unknown);
                CollectUnsupportedVerbs(assign.Value, fileName, unknown);
                break;
        }
    }

    [Fact]
    public void Retail_ForceJump_Activates_And_Sets_InvActivated()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var script = LoadRetailScript(SteamRes2Path, "force_jump.cog");
        var ctx = new ForceCogWorldContext { LocalPlayerThing = 100 };
        // Force Jump uses bin 21 (jump) and bin 14 (mana)
        ctx.Inventory[(100, 14)] = 100f;
        ctx.Inventory[(100, 21)] = 4f;

        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 1);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);
        vm.SendMessage(inst, "activated", 100);

        Assert.Equal(1, ctx.IsInvActivated(100, 21));
    }

    [Fact]
    public void Retail_ForceSpeed_Activates_And_Sets_ActorExtraSpeed()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var script = LoadRetailScript(SteamRes2Path, "force_speed.cog");
        var ctx = new ForceCogWorldContext { LocalPlayerThing = 100 };
        // Force Speed uses bin 22 and mana bin 14
        ctx.Inventory[(100, 14)] = 100f;
        ctx.Inventory[(100, 22)] = 4f;

        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 2);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);
        vm.SendMessage(inst, "activated", 100);

        Assert.Equal(1, ctx.IsInvActivated(100, 22));
        Assert.True(ctx.ActorExtraSpeeds.GetValueOrDefault(100, 0f) > 0f);
    }

    [Fact]
    public void Retail_ForceHeal_Restores_Player_Health()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var script = LoadRetailScript(SteamRes2Path, "force_heal.cog");
        var ctx = new ForceCogWorldContext { LocalPlayerThing = 100 };
        ctx.ThingHealth[100] = 40f;
        ctx.ThingMaxHealth[100] = 100f;
        // Force Heal uses bin 25 and mana bin 14 (cost = 200)
        ctx.Inventory[(100, 14)] = 200f;
        ctx.Inventory[(100, 25)] = 4f;

        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 3);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);
        vm.SendMessage(inst, "activated", 100);
        // force_heal sleeps 0.6s before calling HealThing(player, 20 * rank)
        vm.Update(0.7f);

        Assert.True(ctx.ThingHealth[100] > 40f);
    }

    [Fact]
    public void Retail_ForcePull_Scans_View_And_Dispatches_SkillTarget()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var script = LoadRetailScript(SteamRes2Path, "force_pull.cog");
        var ctx = new ForceCogWorldContext { LocalPlayerThing = 100 };
        ctx.Inventory[(100, 14)] = 100f;
        ctx.Inventory[(100, 24)] = 4f; // Force Pull bin 24
        ctx.ThingPositions[100] = Vector3.Zero;
        ctx.ThingPositions[200] = new Vector3(0f, 5f, 0f);
        ctx.ThingTypes[200] = 2; // actor
        ctx.ViewTargets.Add(200);
        ctx.LineOfSight.Add((100, 200));
        ctx.ViewDots[(100, 200)] = 0.99f;

        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 4);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);
        vm.SendMessage(inst, "activated", 100);
        // Force Pull runs targeting in pulse (0.33s)
        vm.Update(0.4f);

        Assert.Equal(1, ctx.IsInvActivated(100, 24));
    }

    [Fact]
    public void Retail_WeapSaber_Selects_And_Fires_Saber()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var script = LoadRetailScript(SteamRes2Path, "weap_saber.cog");
        var ctx = new ForceCogWorldContext { LocalPlayerThing = 100 };

        var vm = new CogVm(ctx);
        var inst = new CogInstance(script, cogId: 5);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);
        vm.SendMessage(inst, "selected", 100);

        Assert.False(string.IsNullOrEmpty(ctx.PovModels.GetValueOrDefault(100)));
        Assert.False(string.IsNullOrEmpty(ctx.WeaponMeshes.GetValueOrDefault(100)));
        Assert.Equal(2, ctx.ArmedModes.GetValueOrDefault(100));
        Assert.NotEmpty(ctx.SoundsPlayed);

        vm.SendMessage(inst, "fire", senderRef: 0, senderId: 0, sourceRef: 100);
        Assert.NotEmpty(ctx.EnableSaberCalls);
    }

    [Fact]
    public void SkillTarget_Dispatches_To_ClassCog_And_Falls_Back_To_Context()
    {
        const string casterCog = @"
symbols
message activated
int targetThing
int player
float res
end
code
activated:
    res = SkillTarget(targetThing, player, 26, 4.0);
    return;
end";

        const string targetClassCog = @"
symbols
message skill
float receivedSkill
float receivedPower
end
code
skill:
    receivedSkill = GetParam(0);
    receivedPower = GetParam(1);
    ReturnEx(42.0);
    return;
end";

        var ctx = new ForceCogWorldContext();
        ctx.ThingClassCogs[200] = 10; // has class cog
        ctx.ThingClassCogs[201] = -1; // no class cog -> fallback

        var vm = new CogVm(ctx);
        var caster = new CogInstance(CogParser.Parse(casterCog, "caster.cog"), cogId: 1);
        var targetCls = new CogInstance(CogParser.Parse(targetClassCog, "target_cls.cog"), cogId: 10);
        vm.AddInstance(caster);
        vm.AddInstance(targetCls);

        caster.SetSymbolValue("targetThing", new CogValue(200));
        caster.SetSymbolValue("player", new CogValue(100));
        vm.SendMessage(caster, "activated", 100);

        Assert.Equal(26f, targetCls.GetSymbolValue("receivedSkill").AsFloat);
        Assert.Equal(4f, targetCls.GetSymbolValue("receivedPower").AsFloat);
        Assert.Equal(42f, caster.GetSymbolValue("res").AsFloat);

        caster.SetSymbolValue("targetThing", new CogValue(201));
        vm.SendMessage(caster, "activated", 100);
        Assert.Single(ctx.SkillTargetCalls);
        Assert.Equal((201, 100, 26f, 4f), ctx.SkillTargetCalls[0]);
        Assert.Equal(1f, caster.GetSymbolValue("res").AsFloat);
    }

    [Fact]
    public void Jk_Saber_Flags_Cutscene_And_Thing_Verbs_Work_EndToEnd()
    {
        const string cogText = @"
symbols
message activated
int player
int flagsAfterClear
string meshOut
float maxHp
vector rotVelOut
vector velOut
int availOut
int curInvWep
vector rvec
end
code
activated:
    jkSetFlags(player, 0x5);
    jkClearFlags(player, 0x1);
    flagsAfterClear = jkGetFlags(player);
    jkSetWeaponMesh(player, ""sabv.3do"");
    meshOut = jkGetWeaponMesh(player);
    jkSetSaberInfo(player, ""sabblue0.mat"", ""sabblue1.mat"", 0.003, 0.001, 0.12, ""sparks"", ""sparks"", ""sparks"");
    jkEnableSaber(player, 10.0, 0.12, 0.0);
    jkDisableSaber(player);
    jkBeginCutscene();
    jkEndCutscene();
    SetThingCurGeoMode(player, 4);
    SetThingLight(player, 0.75, 0.0);
    SetThingLook(player, '0 1 0');
    SetThingRotVel(player, '1 2 3');
    rotVelOut = GetThingRotVel(player);
    SetThingVel(player, '1 0 0');
    AddThingVel(player, '0 2 0');
    velOut = GetThingVel(player);
    maxHp = GetThingMaxHealth(player);
    SetInvAvailable(player, 21, 1);
    availOut = IsInvAvailable(player, 21);
    curInvWep = GetCurInvWeapon(player);
    ParseArg(player, ""health=100"");
    rvec = RandVec();
    return;
end";

        var ctx = new ForceCogWorldContext
        {
            CurInvWeapon = 10,
            RandValue = 0.75f
        };
        ctx.ThingMaxHealth[100] = 150f;

        var vm = new CogVm(ctx);
        var inst = new CogInstance(CogParser.Parse(cogText, "test_jk.cog"), cogId: 1);
        inst.SetSymbolValue("player", new CogValue(100));
        vm.AddInstance(inst);

        vm.SendMessage(inst, "activated", 100);

        Assert.Equal(4, inst.GetSymbolValue("flagsAfterClear").AsInt);
        Assert.Equal("sabv.3do", inst.GetSymbolValue("meshOut").AsString);
        Assert.Single(ctx.SaberInfoCalls);
        Assert.Single(ctx.EnableSaberCalls);
        Assert.Single(ctx.DisableSaberCalls);
        Assert.False(ctx.CutsceneActive);
        Assert.Equal(4, ctx.GetThingGeoMode(100));
        Assert.Equal(0.75f, ctx.GetThingLight(100));
        Assert.Equal(new Vector3(0f, 1f, 0f), ctx.ThingLooks[100]);
        Assert.Equal(new Vector3(1f, 2f, 3f), inst.GetSymbolValue("rotVelOut").AsVector3);
        Assert.Equal(new Vector3(1f, 2f, 0f), inst.GetSymbolValue("velOut").AsVector3);
        Assert.Equal(150f, inst.GetSymbolValue("maxHp").AsFloat);
        Assert.Equal(1, inst.GetSymbolValue("availOut").AsInt);
        Assert.Equal(10, inst.GetSymbolValue("curInvWep").AsInt);
        Assert.Single(ctx.ParseArgCalls);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), inst.GetSymbolValue("rvec").AsVector3);
    }
}
