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
/// Tests for the COG verbs used by 17asarris.jkl &amp; 17bsarris.jkl and verification of all placed COG scripts.
/// </summary>
public class SarissCogTests(ITestOutputHelper output)
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    private sealed class SarissWorldContext : ICogWorldContext
    {
        public float LevelTime { get; set; } = 42.5f;
        public List<string> PrintedMessages { get; } = [];
        public List<(int surfaceId, Vector3 dir, float speed)> SlideSurfaceCalls { get; } = [];
        public List<(int thingId, float r, float g, float b)> AddDynamicAddCalls { get; } = [];

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
        public void Print(string text) => PrintedMessages.Add(text);
        public float Rand() => 0.5f;

        public float GetLevelTime() => LevelTime;
        public void SlideSurface(int surfaceId, Vector3 dir, float speed) => SlideSurfaceCalls.Add((surfaceId, dir, speed));
        public void AddDynamicAdd(int thingId, float r, float g, float b) => AddDynamicAddCalls.Add((thingId, r, g, b));
    }

    [Fact]
    public void Sariss_Verbs_Are_Supported_And_Dispatched_In_CogVm()
    {
        string[] expectedVerbs = ["slidewall", "getleveltime", "printint", "printflex", "adddynamicadd"];
        foreach (string verb in expectedVerbs)
        {
            Assert.True(CogVm.IsVerbSupported(verb), $"Verb '{verb}' should be supported.");
        }

        const string cogText = @"
symbols
message startup
surface wall0=12
thing target=300
vector slideDir='0.0/1.0/0.0'
float elapsed=0.0
int count=7
flex ratio=2.75
end
code
startup:
    elapsed = GetLevelTime();
    SlideWall(wall0, slideDir, 3.5);
    printint(count);
    printflex(elapsed);
    printflex(ratio);
    AddDynamicAdd(target, 0.2, 0.4, 0.6);
    return;
end";

        var ctx = new SarissWorldContext { LevelTime = 123.25f };
        var vm = new CogVm(ctx);
        var inst = new CogInstance(CogParser.Parse(cogText, "test_sariss_verbs.cog"), cogId: 1);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);

        Assert.Equal(123.25f, inst.GetSymbolValue("elapsed").AsFloat, precision: 3);
        Assert.Equal([(12, new Vector3(0f, 1f, 0f), 3.5f)], ctx.SlideSurfaceCalls);
        Assert.Equal(["7", "123.25", "2.75"], ctx.PrintedMessages);
        Assert.Equal([(300, 0.2f, 0.4f, 0.6f)], ctx.AddDynamicAddCalls);
    }

    [Fact]
    public void Vector_CurrNode_Used_As_Integer_Array_Index_In_CogVm()
    {
        const string cogText = @"
symbols
message startup
int searchNode0=10
int searchNode1=20
int searchNode2=30
vector currNode
int firstResult=-1
int secondResult=-1
end
code
startup:
    firstResult = searchNode0[currNode];
    currNode = 1;
    currNode = currNode + 1;
    secondResult = searchNode0[currNode];
    return;
end";

        var ctx = new SarissWorldContext();
        var vm = new CogVm(ctx);
        var inst = new CogInstance(CogParser.Parse(cogText, "test_currnode_index.cog"), cogId: 1);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);

        Assert.Equal(10, inst.GetSymbolValue("firstResult").AsInt);
        Assert.Equal(30, inst.GetSymbolValue("secondResult").AsInt);
    }

    [Fact]
    public void Retail_17aSariss_And_17bSariss_All_Placed_Cogs_Parse_Compile_With_Zero_Unknown_Verbs()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var jk1 = GobArchive.FromFile(SteamJk1Path);
        using var res2 = File.Exists(SteamRes2Path) ? GobArchive.FromFile(SteamRes2Path) : null;

        string[] level17aScripts =
        [
            "17a_endlevel.cog",
            "17a_RahnLines.cog",
            "17a_SarrissSearch.cog",
            "17a_Stream.cog",
            "darkjedi_sariss.cog",
            "00_slideceiling.cog",
            "00_soundtrack.cog",
            "00_PlayAmbient.cog",
            "00_SecretAreas.cog"
        ];

        string[] level17bScripts =
        [
            "17b_endlevel.cog",
            "17b_Camera.cog",
            "darkjedi_yun2.cog",
            "00_slideceiling.cog",
            "00_soundtrack.cog",
            "00_PlayAmbient.cog",
            "00_SecretAreas.cog"
        ];

        VerifyLevelCogs(jk1, res2, "jkl/17asarris.jkl", "17asarris.jkl", level17aScripts);
        VerifyLevelCogs(jk1, res2, "jkl/17bsarris.jkl", "17bsarris.jkl", level17bScripts);
    }

    private void VerifyLevelCogs(
        GobArchive jk1,
        GobArchive? res2,
        string jklGobPath,
        string jklName,
        string[] requiredScripts)
    {
        string jklText = Encoding.ASCII.GetString(jk1.ReadAllBytes(jklGobPath));
        var level = JklReader.Parse(jklText, jklName);

        Assert.NotEmpty(level.CogScripts);

        foreach (string required in requiredScripts)
        {
            Assert.Contains(level.CogScripts, s => s.Equals(required, StringComparison.OrdinalIgnoreCase));
        }

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

        output.WriteLine($"Compiled {compiledCount} COG scripts from {jklGobPath}.");
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
