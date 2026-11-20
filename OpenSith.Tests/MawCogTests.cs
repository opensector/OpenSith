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
/// Tests for the COG verbs used by 15maw.jkl and verification of all 9 COG scripts referenced by jkl/15maw.jkl.
/// </summary>
public class MawCogTests(ITestOutputHelper output)
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    private sealed class MawWorldContext : ICogWorldContext
    {
        public List<(int thingId, float speed)> AISetMoveSpeedCalls { get; } = [];
        public List<(float uSpeed, float vSpeed)> SlideCeilingSkyCalls { get; } = [];

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

        public void AISetMoveSpeed(int thingId, float speed) => AISetMoveSpeedCalls.Add((thingId, speed));
        public void SlideCeilingSky(float uSpeed, float vSpeed) => SlideCeilingSkyCalls.Add((uSpeed, vSpeed));
    }

    [Fact]
    public void Maw_Verbs_Are_Supported_And_Dispatched_In_CogVm()
    {
        string[] expectedVerbs = ["aisetmovespeed", "slideceilingsky"];
        foreach (string verb in expectedVerbs)
        {
            Assert.True(CogVm.IsVerbSupported(verb), $"Verb '{verb}' should be supported.");
        }

        const string cogText = @"
symbols
message startup
int maw=250
float u=10.0
float v=-5.5
end
code
startup:
    AISetMoveSpeed(maw, 4.0);
    SlideCeilingSky(u, v);
    return;
end";

        var ctx = new MawWorldContext();
        var vm = new CogVm(ctx);
        var inst = new CogInstance(CogParser.Parse(cogText, "test_maw_verbs.cog"), cogId: 1);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);

        Assert.Equal([(250, 4.0f)], ctx.AISetMoveSpeedCalls);
        Assert.Equal([(10.0f, -5.5f)], ctx.SlideCeilingSkyCalls);
    }

    [Fact]
    public void Retail_15Maw_All_Referenced_Cogs_Parse_Compile_With_Zero_Unknown_Verbs()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var jk1 = GobArchive.FromFile(SteamJk1Path);
        string jklText = Encoding.ASCII.GetString(jk1.ReadAllBytes("jkl/15maw.jkl"));
        var level = JklReader.Parse(jklText, "15maw.jkl");

        Assert.NotEmpty(level.CogScripts);

        string[] requiredScripts =
        [
            "15_sectordoor.cog",
            "00_elev_switch2.cog",
            "15_endlevel.cog",
            "00_slideceiling.cog",
            "00_soundtrack.cog",
            "darkjedi_maw.cog",
            "15_Rahnlines.cog",
            "15_mawdoor.cog",
            "00_SecretAreas.cog"
        ];

        foreach (string required in requiredScripts)
        {
            Assert.Contains(level.CogScripts, s => s.Equals(required, StringComparison.OrdinalIgnoreCase));
        }

        using var res2 = File.Exists(SteamRes2Path) ? GobArchive.FromFile(SteamRes2Path) : null;

        var unknownByVerb = new SortedDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var missingScripts = new List<string>();
        var compiledCount = 0;

        foreach (string scriptName in requiredScripts)
        {
            byte[]? bytes = TryReadCogBytes(jk1, scriptName) ?? (res2 != null ? TryReadCogBytes(res2, scriptName) : null);
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

        output.WriteLine($"Compiled {compiledCount} COG scripts from jkl/15maw.jkl.");
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
