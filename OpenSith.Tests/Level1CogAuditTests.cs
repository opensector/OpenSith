using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// Full COG verb audit for Level 1 (<c>01narshadda.jkl</c>), its template COGs, and all <c>weap_*.cog</c> scripts,
/// plus unit tests for the 16 newly added COG verbs in <see cref="CogVm"/> and <see cref="ICogWorldContext"/>.
/// </summary>
public class Level1CogAuditTests(ITestOutputHelper output)
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    private sealed class AuditMockWorldContext : ICogWorldContext
    {
        public List<(int thingId, int cogId)> CaptureThingCalls { get; } = [];
        public List<(int thingId, float interval)> SetThingPulseCalls { get; } = [];
        public List<int> SetWeaponFlagsCalls { get; } = [];
        public List<(int thingId, float size)> SetThingCollideSizeCalls { get; } = [];
        public List<(int thingId, int collideType)> SetCollideTypeCalls { get; } = [];
        public List<(int thingId, float health)> SetThingHealthCalls { get; } = [];
        public List<(int playerThing, int backpackThing)> PickupBackpackCalls { get; } = [];
        public List<(int weaponThing, float fireRate)> ChangeFireRateCalls { get; } = [];
        public List<(string material, float fps, int flags)> MaterialAnimCalls { get; } = [];

        public int GetThingSector(int thingId) => 0;
        public Vector3 GetThingPos(int thingId) => Vector3.Zero;
        public void MoveToFrame(int thingId, int frame, float speed) { }
        public bool IsThingMoving(int thingId) => false;
        public int GetCurFrame(int thingId) => 0;
        public int GetLocalPlayerThing() => 100;
        public void SetSectorAdjoins(int sectorId, bool visible) { }
        public void SetSectorLight(int sectorId, float light, float delay) { }
        public int PlaySoundThing(string sound, int thingId, float volume, float minRad, float maxRad, int flags) => 0;
        public int SendMessage(int cogId, string message) => 0;
        public void Print(string text) { }
        public float Rand() => 0.5f;

        public int CreateThing(string template, int refThing) => 201;
        public int CreateThingNR(string template, int refThing) => 202;
        public void SetThingHealth(int thingId, float health) => SetThingHealthCalls.Add((thingId, health));
        public Vector3 GetThingLVec(int thingId) => new(0f, 1f, 0f);
        public void CaptureThing(int thingId, int cogId) => CaptureThingCalls.Add((thingId, cogId));
        public void SetThingPulse(int thingId, float interval) => SetThingPulseCalls.Add((thingId, interval));
        public void SetThingCollideSize(int thingId, float size) => SetThingCollideSizeCalls.Add((thingId, size));
        public void SetCollideType(int thingId, int collideType) => SetCollideTypeCalls.Add((thingId, collideType));
        public int GetAutoPickup() => 3;
        public int GetAutoSwitch() => 5;
        public int GetCurWeaponMode() => 1;
        public float GetWeaponPriority(int playerThing, int binIdx, int mode) => binIdx * 10f + mode;
        public void SetWeaponFlags(int flags) => SetWeaponFlagsCalls.Add(flags);
        public void ChangeFireRate(int weaponThing, float fireRate) => ChangeFireRateCalls.Add((weaponThing, fireRate));
        public int CreateBackpack(int playerThingId) => 303;
        public void PickupBackpack(int playerThingId, int backpackThingId) => PickupBackpackCalls.Add((playerThingId, backpackThingId));
        public int MaterialAnim(string material, float fps, int flags)
        {
            MaterialAnimCalls.Add((material, fps, flags));
            return 42;
        }
    }

    [Fact]
    public void Level1_NewlyAdded_Verbs_Are_Supported_And_Dispatched_In_CogVm()
    {
        string[] expectedVerbs =
        [
            "capturething", "setthingpulse", "setweaponflags", "setthingcollidesize",
            "createbackpack", "setcollidetype", "createthingnr", "setthinghealth",
            "getautopickup", "getweaponpriority", "pickupbackpack", "getautoswitch",
            "changefirerate", "getthinglvec", "getcurweaponmode", "materialanim"
        ];

        foreach (string verb in expectedVerbs)
        {
            Assert.True(CogVm.IsVerbSupported(verb), $"Verb '{verb}' should be supported.");
        }

        const string cogText = @"
symbols
message startup
int t1=0
int t2=0
int ap=0
int asw=0
int mode=0
int anim=0
float prio=0.0
vector lvec
end
code
startup:
    CaptureThing(10);
    SetThingPulse(10, 0.25);
    SetWeaponFlags(4);
    SetThingCollideSize(10, 0.75);
    t2 = CreateBackpack(100);
    SetCollideType(10, 3);
    t1 = CreateThingNR(""stormtrooper"", 100);
    SetThingHealth(10, 75.5);
    ap = GetAutoPickup();
    prio = GetWeaponPriority(100, 2, 1);
    PickupBackpack(100, t2);
    asw = GetAutoSwitch();
    ChangeFireRate(100, 0.15);
    lvec = GetThingLVec(10);
    mode = GetCurWeaponMode();
    anim = MaterialAnim(""01test.mat"", 12.0, 1);
    Return;
end";

        var script = CogParser.Parse(cogText, "level1_verbs_test.cog");
        var instance = new CogInstance(script, cogId: 77);
        var ctx = new AuditMockWorldContext();
        var vm = new CogVm(ctx);
        vm.AddInstance(instance);

        vm.SendMessage(instance, "startup");

        Assert.Single(ctx.CaptureThingCalls);
        Assert.Equal((10, 77), ctx.CaptureThingCalls[0]);
        Assert.Equal([(10, 0.25f)], ctx.SetThingPulseCalls);
        Assert.Equal([4], ctx.SetWeaponFlagsCalls);
        Assert.Equal([(10, 0.75f)], ctx.SetThingCollideSizeCalls);
        Assert.Equal(303, instance.GetSymbolValue("t2").AsInt);
        Assert.Equal([(10, 3)], ctx.SetCollideTypeCalls);
        Assert.Equal(202, instance.GetSymbolValue("t1").AsInt);
        Assert.Equal([(10, 75.5f)], ctx.SetThingHealthCalls);
        Assert.Equal(3, instance.GetSymbolValue("ap").AsInt);
        Assert.Equal(21f, instance.GetSymbolValue("prio").AsFloat);
        Assert.Equal([(100, 303)], ctx.PickupBackpackCalls);
        Assert.Equal(5, instance.GetSymbolValue("asw").AsInt);
        Assert.Equal([(100, 0.15f)], ctx.ChangeFireRateCalls);
        Assert.Equal(new Vector3(0f, 1f, 0f), instance.GetSymbolValue("lvec").AsVector3);
        Assert.Equal(1, instance.GetSymbolValue("mode").AsInt);
        Assert.Equal(42, instance.GetSymbolValue("anim").AsInt);
        Assert.Equal([("01test.mat", 12.0f, 1)], ctx.MaterialAnimCalls);
    }

    [Fact]
    public void Retail_01NarShaddaa_All_Placed_Template_And_Weapon_Cogs_Compile_With_Zero_Unknown_Verbs()
    {
        if (!File.Exists(SteamJk1Path) || !File.Exists(SteamRes2Path))
            return;

        using var jk1 = GobArchive.FromFile(SteamJk1Path);
        using var res2 = GobArchive.FromFile(SteamRes2Path);

        string jklText = Encoding.ASCII.GetString(jk1.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(jklText, "01narshadda");

        var scriptsToAudit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. All placed COG scripts in level.CogScripts
        foreach (string cogName in level.CogScripts)
        {
            if (!string.IsNullOrWhiteSpace(cogName))
                scriptsToAudit.Add(cogName);
        }

        // 2. All template cog= scripts (kyle.cog, xcrane.cog, class_sequencer.cog, 00_*trail.cog, actor_*.cog, pow_*.cog, etc.)
        foreach (var tpl in level.Templates.Values)
        {
            if (tpl.Parameters.TryGetValue("cog", out string? cogName) && !string.IsNullOrWhiteSpace(cogName))
                scriptsToAudit.Add(cogName);
        }

        // 3. All weap_*.cog scripts from Res2.gob
        foreach (var entry in res2.Entries)
        {
            string fileName = Path.GetFileName(entry.Path.Replace('\\', '/'));
            if (fileName.StartsWith("weap_", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".cog", StringComparison.OrdinalIgnoreCase))
            {
                scriptsToAudit.Add(fileName);
            }
        }

        Assert.NotEmpty(scriptsToAudit);

        var unknownByVerb = new SortedDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var missingScripts = new List<string>();
        int compiledCount = 0;

        foreach (string scriptName in scriptsToAudit)
        {
            byte[]? bytes = TryReadCogBytes(jk1, scriptName) ?? TryReadCogBytes(res2, scriptName);
            if (bytes == null)
            {
                missingScripts.Add(scriptName);
                continue;
            }

            string text = Encoding.ASCII.GetString(bytes);
            var script = CogParser.Parse(text, scriptName);
            var program = CogCompiler.Compile(script);
            compiledCount++;

            foreach (var instr in program.Instructions)
            {
                if (instr.Expr != null)
                    CollectUnsupportedVerbs(instr.Expr, scriptName, unknownByVerb);
            }
        }

        output.WriteLine($"Audited and compiled {compiledCount} COG scripts from 01narshadda.jkl (placed + templates + weap_*.cog).");
        foreach (var kvp in unknownByVerb)
        {
            output.WriteLine($"UNKNOWN VERB: {kvp.Key} (used in: {string.Join(", ", kvp.Value)})");
        }

        Assert.Empty(missingScripts);
        Assert.Empty(unknownByVerb);
    }

    private static byte[]? TryReadCogBytes(GobArchive archive, string fileName)
    {
        foreach (var entry in archive.Entries)
        {
            if (Path.GetFileName(entry.Path.Replace('\\', '/')).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return archive.ReadAllBytes(entry.Path);
        }
        return null;
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
}
