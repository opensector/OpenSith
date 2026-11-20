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
using Xunit.Abstractions;

namespace OpenSith.Tests;

/// <summary>
/// Tests for the 7 COG verbs used by 11gorc.jkl and verification of all COG scripts in 11gorc.jkl.
/// </summary>
public class GorcPicCogTests(ITestOutputHelper output)
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    private sealed class GorcPicWorldContext : ICogWorldContext
    {
        public List<(int camIdx, int thingId)> SetCameraFocusCalls { get; } = [];
        public List<int> SetCurrentCameraCalls { get; } = [];
        public List<float> SetMusicVolCalls { get; } = [];
        public int AutosaveGameCount { get; private set; }
        public List<(int thingId, Vector3 pos)> AISetLookPosCalls { get; } = [];
        public List<(int thingId, Vector3 pos)> AISetMovePosCalls { get; } = [];
        public List<(int thingId, int destThingId, float speed)> AIJumpCalls { get; } = [];

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

        public void SetCurrentCamera(int camIdx) => SetCurrentCameraCalls.Add(camIdx);
        public void SetCameraFocus(int camIdx, int thingId) => SetCameraFocusCalls.Add((camIdx, thingId));
        public void SetMusicVol(float vol) => SetMusicVolCalls.Add(vol);
        public void AutosaveGame() => AutosaveGameCount++;
        public void AISetLookPos(int thingId, Vector3 pos) => AISetLookPosCalls.Add((thingId, pos));
        public void AISetMovePos(int thingId, Vector3 pos) => AISetMovePosCalls.Add((thingId, pos));
        public void AIJump(int thingId, int destThingId, float speed) => AIJumpCalls.Add((thingId, destThingId, speed));
    }

    [Fact]
    public void GorcPic_Verbs_Are_Supported_And_Dispatched_In_CogVm()
    {
        string[] expectedVerbs =
        [
            "setcurrentcamera",
            "setcamerafocus",
            "setmusicvol",
            "autosavegame",
            "aisetlookpos",
            "aisetmovepos",
            "aijump"
        ];

        foreach (string verb in expectedVerbs)
        {
            Assert.True(CogVm.IsVerbSupported(verb), $"Verb '{verb}' should be supported.");
        }

        const string cogText = @"
symbols
message startup
int gorc=200
int dest=201
int cam=1
int focusThing=100
end
code
startup:
    SetCurrentCamera(cam);
    SetCameraFocus(cam, focusThing);
    SetMusicVol(0.75);
    AutosaveGame();
    AISetLookPos(gorc, '1.0 2.0 3.0');
    AISetMovePos(gorc, '4.0 5.0 6.0');
    AIJump(gorc, dest, 12.5);
    return;
end";

        var ctx = new GorcPicWorldContext();
        var vm = new CogVm(ctx);
        var inst = new CogInstance(CogParser.Parse(cogText, "test_gorc_verbs.cog"), cogId: 1);
        vm.AddInstance(inst);

        vm.SendMessage(inst, "startup", 100);

        Assert.Equal([1], ctx.SetCurrentCameraCalls);
        Assert.Equal([(1, 100)], ctx.SetCameraFocusCalls);
        Assert.Equal([0.75f], ctx.SetMusicVolCalls);
        Assert.Equal(1, ctx.AutosaveGameCount);
        Assert.Equal([(200, new Vector3(1f, 2f, 3f))], ctx.AISetLookPosCalls);
        Assert.Equal([(200, new Vector3(4f, 5f, 6f))], ctx.AISetMovePosCalls);
        Assert.Equal([(200, 201, 12.5f)], ctx.AIJumpCalls);
    }

    [Fact]
    public void Retail_11Gorc_All_Referenced_Cogs_Parse_Compile_With_Zero_Unknown_Verbs()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var jk1 = GobArchive.FromFile(SteamJk1Path);
        string jklText = Encoding.ASCII.GetString(jk1.ReadAllBytes("jkl/11gorc.jkl"));
        var level = JklReader.Parse(jklText, "11gorc.jkl");

        Assert.NotEmpty(level.CogScripts);

        string[] requiredScripts =
        [
            "11_endlevel.cog",
            "11_sound.cog",
            "00_sectorthrust.cog",
            "00_soundtrack.cog",
            "00_SecretAreas.cog",
            "darkjedi_gorc.cog",
            "darkjedi_pic.cog"
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

        output.WriteLine($"Compiled {compiledCount} COG scripts from jkl/11gorc.jkl.");
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
