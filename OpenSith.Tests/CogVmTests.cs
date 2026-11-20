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

public sealed class TestCogWorldContext : ICogWorldContext
{
    public Dictionary<int, int> ThingSectors { get; } = new();
    public Dictionary<int, Vector3> ThingPositions { get; } = new();
    public Dictionary<int, int> ThingFrames { get; } = new();
    public HashSet<int> MovingThings { get; } = [];
    public Dictionary<int, bool> SectorAdjoins { get; } = new();
    public Dictionary<int, (float light, float delay)> SectorLights { get; } = new();
    public List<(int thingId, int frame, float speed)> MoveToFrameCalls { get; } = [];
    public List<(string sound, int thingId, float volume, float minRad, float maxRad, int flags)> SoundCalls { get; } = [];
    public List<(int cogId, string message)> SentMessages { get; } = [];
    public List<string> PrintedMessages { get; } = [];
    public int LocalPlayerThing { get; set; } = 100;
    public float RandValue { get; set; } = 0.5f;

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
        return 1;
    }
    public int SendMessage(int cogId, string message)
    {
        SentMessages.Add((cogId, message));
        return 0;
    }
    public void Print(string text) => PrintedMessages.Add(text);
    public float Rand() => RandValue;
}

/// <summary>
/// Comprehensive unit tests for the OpenSith COG Virtual Machine, value type semantics,
/// argument binding, coroutines, and retail LucasArts Jedi Knight scripts.
/// </summary>
public class CogVmTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    private const string SyntheticDoorCog = """
        symbols
        message startup
        message activate
        message arrived
        message timer
        message blocked

        thing   door0   linkid=0 mask=0x405
        thing   door1   linkid=1 mask=0x405
        thing   door2   linkid=2 mask=0x405
        thing   door3   linkid=3 mask=0x405

        float   moveSpeed=8.0
        float   sleepTime=2.0
        float   lightValue=0.5

        sector  doorSector  local
        int     numDoors=0  local
        int     doorStatus  local
        int     moveStatus  local
        int     i           local
        end

        code
        startup:
            for (i=0; i<=3; i=i+1)
                if (door0[i] >= 0) numDoors = numDoors + 1;

            doorSector = GetThingSector(door0);
            SetSectorAdjoins(doorSector, 0);
            SetSectorLight(doorSector, lightValue, 0.0);
            return;

        activate:
            call CheckStatus;
            if (moveStatus) return;
            if (doorStatus == 0) {
                SetSectorAdjoins(doorSector, 1);
                call OpenDoors;
            }
            return;

        arrived:
            call CheckStatus;
            if (moveStatus) return;
            if (doorStatus == numDoors) {
                SetTimer(sleepTime);
            } else if (doorStatus == 0) {
                SetSectorAdjoins(doorSector, 0);
            }
            return;

        blocked:
            call OpenDoors;
            return;

        timer:
            call CloseDoors;
            return;

        OpenDoors:
            for (i=0; i<=3; i=i+1)
                if (door0[i] >= 0) MoveToFrame(door0[i], 1, moveSpeed);
            return;

        CloseDoors:
            for (i=0; i<=3; i=i+1)
                if (door0[i] >= 0) MoveToFrame(door0[i], 0, moveSpeed);
            return;

        CheckStatus:
            moveStatus = 0;
            doorStatus = 0;
            for (i=0; i<=3; i=i+1) {
                if (door0[i] >= 0) {
                    moveStatus = moveStatus + IsThingMoving(door0[i]);
                    doorStatus = doorStatus + GetCurFrame(door0[i]);
                }
            }
            return;
        end
        """;

    private const string SyntheticElevCog = """
        symbols
        message crossed
        message arrived
        message timer

        surface lower_adjoin0
        surface lower_adjoin1

        thing   elevator

        float   start_wait=0.25
        float   sleeptime=2.0
        float   speed=4.0
        end

        code
        crossed:
            if (IsThingMoving(elevator)) return;
            if (GetCurFrame(elevator) != 0) return;

            Sleep(start_wait);

            MoveToFrame(elevator, 1, speed);
            return;

        arrived:
            if (GetCurFrame(elevator) == 0) return;
            SetTimer(sleeptime);
            return;

        timer:
            MoveToFrame(elevator, 0, speed);
            return;
        end
        """;

    private const string SyntheticBartenderCog = """
        symbols
        message killed
        message entered

        thing   normry   mask=-1
        thing   sambt    mask=-1
        thing   cliffry  mask=-1
        sector  bar2
        sector  bar1
        sector  bar3
        int     deadsam  local
        int     deadry   local
        int     needsl=0 local
        sound   trouble=i00mn19z.wav local
        end

        code
        entered:
            if (needsl == 0) return;
            if (deadsam == 1) return;

            Sleep (0.5);

            PlaySoundThing(trouble, sambt, 1.5, 10.0, -1, 0x10080);

            needsl = 0;
            return;

        killed:
            if (deadsam == 1) return;

            if (GetSenderRef() == sambt)
            {
               deadsam = 1;
               return;
            }

            deadry = deadry + 1;

            if (deadry == 2)
            {
               if((GetThingSector(GetLocalPlayerThing()) == bar1) || (GetThingSector(GetLocalPlayerThing()) == bar2) || (GetThingSector(GetLocalPlayerThing()) == bar3))
               {
                   Sleep (1.5);
                   PlaySoundThing(trouble, sambt, 1.5, 10.0, -1, 0x10080);
               }
               else
               {
                   needsl = 1;
               }
            }
            return;
        end
        """;

    private static CogScript LoadScript(string cogName, string syntheticFallback, string? preferredGob = null)
    {
        string[] searchPaths = preferredGob != null
            ? [preferredGob, SteamRes2Path, SteamJk1Path]
            : [SteamRes2Path, SteamJk1Path];

        foreach (var archPath in searchPaths)
        {
            if (File.Exists(archPath))
            {
                using var archive = GobArchive.FromFile(archPath);
                var entry = archive.Entries.FirstOrDefault(e => e.Path.EndsWith(cogName, StringComparison.OrdinalIgnoreCase));
                if (entry.Length > 0)
                {
                    string text = Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
                    return CogParser.Parse(text, cogName);
                }
            }
        }

        return CogParser.Parse(syntheticFallback, cogName);
    }

    [Fact]
    public void CogValue_ConversionsAndTruthiness_MatchSemantics()
    {
        // Int
        CogValue vInt = 42;
        Assert.Equal(42, vInt.AsInt);
        Assert.Equal(42f, vInt.AsFloat);
        Assert.True(vInt.AsBool);
        Assert.Equal("42", vInt.AsString);

        CogValue vZero = 0;
        Assert.False(vZero.AsBool);

        // Float
        CogValue vFloat = 3.5f;
        Assert.Equal(3, vFloat.AsInt);
        Assert.Equal(3.5f, vFloat.AsFloat);
        Assert.True(vFloat.AsBool);
        Assert.Equal("3.5", vFloat.AsString);

        // Vector3
        var vec = new Vector3(1f, 2f, 3f);
        CogValue vVec = vec;
        Assert.Equal(vec, vVec.AsVector3);
        Assert.True(vVec.AsBool);
        Assert.False(new CogValue(Vector3.Zero).AsBool);

        // String
        CogValue vStr = "123.45";
        Assert.Equal(123, vStr.AsInt);
        Assert.Equal(123.45f, vStr.AsFloat);
        Assert.True(vStr.AsBool);

        CogValue vEmpty = "";
        Assert.False(vEmpty.AsBool);
        CogValue vStrZero = "0";
        Assert.False(vStrZero.AsBool);

        // Implicit conversions
        CogValue fromBoolTrue = true;
        Assert.Equal(1, fromBoolTrue.AsInt);
        Assert.True(fromBoolTrue.AsBool);

        // Explicit casts
        int unboxedInt = (int)vInt;
        Assert.Equal(42, unboxedInt);
        float unboxedFloat = (float)vFloat;
        Assert.Equal(3.5f, unboxedFloat);
        bool unboxedBool = (bool)vInt;
        Assert.True(unboxedBool);
        Vector3 unboxedVec = (Vector3)vVec;
        Assert.Equal(vec, unboxedVec);
    }

    [Fact]
    public void CogValue_ArithmeticAndComparisons_OperateCorrectly()
    {
        CogValue a = 10;
        CogValue b = 2.5f;

        Assert.Equal(12.5f, (a + b).AsFloat);
        Assert.Equal(7.5f, (a - b).AsFloat);
        Assert.Equal(25f, (a * b).AsFloat);
        Assert.Equal(4f, (a / b).AsFloat);

        // Vector arithmetic
        CogValue v1 = new Vector3(1, 2, 3);
        CogValue v2 = new Vector3(4, 5, 6);
        Assert.Equal(new Vector3(5, 7, 9), (v1 + v2).AsVector3);
        Assert.Equal(new Vector3(2, 4, 6), (v1 * 2f).AsVector3);

        // Comparisons
        Assert.True(a > b);
        Assert.True(b < a);
        Assert.Equal(a, new CogValue(10));
        Assert.Equal(new CogValue("10"), a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CogInstance_SymbolTableAndArrayIndexing_ResolvesBasePlusIndex()
    {
        var script = LoadScript("00_door.cog", SyntheticDoorCog);
        var instance = new CogInstance(script, cogId: 1);

        // Bind positional placement args: door0=10, door1=11, door2=-1, door3=-1, moveSpeed=6.5, sleepTime=3.0, lightValue=0.8
        var placement = new JklCogPlacement
        {
            Index = 1,
            ScriptName = "00_door.cog",
            SymbolArgs = ["10", "11", "-1", "-1", "6.5", "3.0", "0.8"]
        };
        instance.BindPlacementArgs(placement);

        // Check direct symbol values
        Assert.Equal(10, instance.GetSymbolValue("door0").AsInt);
        Assert.Equal(11, instance.GetSymbolValue("door1").AsInt);
        Assert.Equal(-1, instance.GetSymbolValue("door2").AsInt);
        Assert.Equal(-1, instance.GetSymbolValue("door3").AsInt);
        Assert.Equal(6.5f, instance.GetSymbolValue("moveSpeed").AsFloat);
        Assert.Equal(3.0f, instance.GetSymbolValue("sleepTime").AsFloat);
        Assert.Equal(0.8f, instance.GetSymbolValue("lightValue").AsFloat);

        // Consecutive array indexing: door0[0]=10, door0[1]=11, door0[2]=-1, door0[3]=-1
        Assert.Equal(10, instance.GetArrayElement("door0", 0).AsInt);
        Assert.Equal(11, instance.GetArrayElement("door0", 1).AsInt);
        Assert.Equal(-1, instance.GetArrayElement("door0", 2).AsInt);
        Assert.Equal(-1, instance.GetArrayElement("door0", 3).AsInt);

        // Mutate array via door0[1]
        instance.SetArrayElement("door0", 1, 99);
        Assert.Equal(99, instance.GetSymbolValue("door1").AsInt);
    }

    [Fact]
    public void CogVm_ControlFlow_WhileForDoWhileGotoCallReturn()
    {
        const string scriptText = """
            symbols
            message startup
            int a local
            int sum=0 local
            int i local
            end

            code
            startup:
                # Test for loop and break
                for (i = 0; i < 10; i = i + 1)
                {
                    if (i == 5) break;
                    sum = sum + i;
                }
                # sum is 0 + 1 + 2 + 3 + 4 = 10

                # Test while loop and continue
                a = 0;
                while (a < 5)
                {
                    a = a + 1;
                    if (a == 2) continue;
                    sum = sum + 1;
                }
                # increments 4 times -> sum is 14

                # Test subroutine call and return
                call MySub;

                # Test goto
                goto Finish;
                sum = 9999;

            MySub:
                sum = sum + 10;
                return;

            Finish:
                return;
            end
            """;

        var script = CogParser.Parse(scriptText, "flow_test.cog");
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script);
        vm.AddInstance(instance);

        vm.SendMessage(instance, "startup");

        Assert.Equal(24, instance.GetSymbolValue("sum").AsInt);
    }

    [Fact]
    public void CogVm_ReturnEx_AndParams_OperateCorrectly()
    {
        const string scriptText = """
            symbols
            message user0
            end

            code
            user0:
                if (GetSenderRef() == 42)
                {
                    ReturnEx(GetParam(0) + GetParam(1));
                }
                ReturnEx(0);
            end
            """;

        var script = CogParser.Parse(scriptText, "msg_test.cog");
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script);
        vm.AddInstance(instance);

        var result = vm.SendMessage(instance, "user0", senderRef: 42, [15, 27]);
        Assert.Equal(42, result.AsInt);

        var failResult = vm.SendMessage(instance, "user0", senderRef: 99, [15, 27]);
        Assert.Equal(0, failResult.AsInt);
    }

    [Fact]
    public void DoorCog_Lifecycle_StartupActivateArrivedTimer()
    {
        var script = LoadScript("00_door.cog", SyntheticDoorCog, SteamRes2Path);
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script, cogId: 0);
        vm.AddInstance(instance);

        // Bind placement: door0=4, door1=-1, door2=-1, door3=-1, moveSpeed=8.0, sleepTime=2.0, lightValue=0.5
        var placement = new JklCogPlacement
        {
            Index = 0,
            ScriptName = "00_door.cog",
            SymbolArgs = ["4", "-1", "-1", "-1", "8.000000", "2.000000", "0.500000"]
        };
        instance.BindPlacementArgs(placement);

        // Door sector is 10
        context.ThingSectors[4] = 10;
        context.ThingFrames[4] = 0; // Door initially closed at frame 0

        // 1. Send startup: verifies adjoins closed (0), light set, numDoors counted
        vm.SendMessage(instance, "startup");

        Assert.Equal(1, instance.GetSymbolValue("numDoors").AsInt);
        Assert.Equal(10, instance.GetSymbolValue("doorSector").AsInt);
        Assert.True(context.SectorAdjoins.ContainsKey(10));
        Assert.False(context.SectorAdjoins[10]); // Closed (0)
        Assert.Equal(0.5f, context.SectorLights[10].light);

        // 2. Send activate: verifies adjoins opened & MoveToFrame(4, 1, 8.0) called
        vm.SendMessage(instance, "activate");

        Assert.True(context.SectorAdjoins[10]); // Opened (1)
        Assert.Single(context.MoveToFrameCalls);
        Assert.Equal((4, 1, 8.0f), context.MoveToFrameCalls[0]);

        // 3. Door arrives at frame 1: arms timer for sleepTime (2.0s)
        context.ThingFrames[4] = 1;
        vm.SendMessage(instance, "arrived");

        Assert.True(instance.TimerActive);
        Assert.Equal(2.0f, instance.TimerTimeLeft);

        // 4. Update(2.1f): timer triggers and closes door via MoveToFrame(4, 0, 8.0)
        vm.Update(2.1f);

        Assert.False(instance.TimerActive);
        Assert.Equal(2, context.MoveToFrameCalls.Count);
        Assert.Equal((4, 0, 8.0f), context.MoveToFrameCalls[1]);

        // 5. Door arrives back at frame 0: adjoins are closed again
        context.ThingFrames[4] = 0;
        vm.SendMessage(instance, "arrived");

        Assert.False(context.SectorAdjoins[10]); // Closed (0)
    }

    [Fact]
    public void StdElevCog_Lifecycle_SleepsBeforeMovingAndResumesOnUpdate()
    {
        var script = LoadScript("00_std_elev.cog", SyntheticElevCog, SteamRes2Path);
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script, cogId: 1);
        vm.AddInstance(instance);

        // Placement: elevator=6, start_wait=0.25, sleeptime=2.0, speed=4.0
        var placement = new JklCogPlacement
        {
            Index = 1,
            ScriptName = "00_std_elev.cog",
            SymbolArgs = ["839", "854", "6", "0.250000", "2.000000", "4.000000"]
        };
        instance.BindPlacementArgs(placement);

        context.ThingFrames[6] = 0;

        // 1. Send crossed: elevator should pause via Sleep(0.25f) before moving
        vm.SendMessage(instance, "crossed");

        Assert.True(instance.IsSleeping);
        Assert.InRange(instance.SleepTimeLeft, 0.2f, 0.25f);
        Assert.Empty(context.MoveToFrameCalls); // Not moved yet!

        // 2. Partial tick (0.1s): still sleeping
        vm.Update(0.1f);
        Assert.True(instance.IsSleeping);
        Assert.Empty(context.MoveToFrameCalls);

        // 3. Finish sleep wait (another 0.2s, total 0.3s >= 0.25s): resumes and calls MoveToFrame
        vm.Update(0.2f);
        Assert.False(instance.IsSleeping);
        Assert.Single(context.MoveToFrameCalls);
        Assert.Equal((6, 1, 4.0f), context.MoveToFrameCalls[0]);

        // 4. Elevator arrives at frame 1: arms timer for sleeptime (2.0s)
        context.ThingFrames[6] = 1;
        vm.SendMessage(instance, "arrived");
        Assert.True(instance.TimerActive);
        Assert.Equal(2.0f, instance.TimerTimeLeft);

        // 5. Timer elapses: moves back to frame 0
        vm.Update(2.1f);
        Assert.False(instance.TimerActive);
        Assert.Equal(2, context.MoveToFrameCalls.Count);
        Assert.Equal((6, 0, 4.0f), context.MoveToFrameCalls[1]);
    }

    [Fact]
    public void BartenderCog_KilledAndEntered_CountsKillsAndPlaysSounds()
    {
        var script = LoadScript("01_bartender.cog", SyntheticBartenderCog, SteamJk1Path);
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script, cogId: 29);
        vm.AddInstance(instance);

        // Placement: normry=208, sambt=214, cliffry=207, bar2=350, bar1=370, bar3=353
        var placement = new JklCogPlacement
        {
            Index = 29,
            ScriptName = "01_bartender.cog",
            SymbolArgs = ["208", "214", "207", "350", "370", "353"]
        };
        instance.BindPlacementArgs(placement);

        // Case A: Player is inside bar1 (sector 370). When 2nd Ree Yees dies, bartender sleeps then plays sound!
        context.LocalPlayerThing = 100;
        context.ThingSectors[100] = 370; // Inside bar1

        // Kill normry (1st Ree Yees)
        vm.SendMessage(instance, "killed", senderRef: 208);
        Assert.Equal(1, instance.GetSymbolValue("deadry").AsInt);
        Assert.Empty(context.SoundCalls);

        // Kill cliffry (2nd Ree Yees)
        vm.SendMessage(instance, "killed", senderRef: 207);
        Assert.Equal(2, instance.GetSymbolValue("deadry").AsInt);
        Assert.True(instance.IsSleeping); // Pauses for Sleep(1.5)
        Assert.Empty(context.SoundCalls);

        // Advance past 1.5s sleep
        vm.Update(1.6f);
        Assert.False(instance.IsSleeping);
        Assert.Single(context.SoundCalls);
        var soundCall = context.SoundCalls[0];
        Assert.Equal("i00mn19z.wav", soundCall.sound);
        Assert.Equal(214, soundCall.thingId);
        Assert.Equal(1.5f, soundCall.volume);
        Assert.Equal(10.0f, soundCall.minRad);
        Assert.Equal(-1f, soundCall.maxRad);
        Assert.Equal(0x10080, soundCall.flags);
    }

    [Fact]
    public void BartenderCog_PlayerOutsideBar_SetsNeedslAndPlaysOnEntered()
    {
        var script = LoadScript("01_bartender.cog", SyntheticBartenderCog, SteamJk1Path);
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script, cogId: 29);
        vm.AddInstance(instance);

        var placement = new JklCogPlacement
        {
            Index = 29,
            ScriptName = "01_bartender.cog",
            SymbolArgs = ["208", "214", "207", "350", "370", "353"]
        };
        instance.BindPlacementArgs(placement);

        // Player is outside bar (sector 999)
        context.LocalPlayerThing = 100;
        context.ThingSectors[100] = 999;

        // Kill both Ree Yees
        vm.SendMessage(instance, "killed", senderRef: 208);
        vm.SendMessage(instance, "killed", senderRef: 207);

        Assert.Equal(2, instance.GetSymbolValue("deadry").AsInt);
        Assert.Equal(1, instance.GetSymbolValue("needsl").AsInt);
        Assert.False(instance.IsSleeping);
        Assert.Empty(context.SoundCalls);

        // Player enters bar sector
        vm.SendMessage(instance, "entered");
        Assert.True(instance.IsSleeping); // Sleep(0.5)

        vm.Update(0.6f);
        Assert.False(instance.IsSleeping);
        Assert.Equal(0, instance.GetSymbolValue("needsl").AsInt);
        Assert.Single(context.SoundCalls);
        Assert.Equal("i00mn19z.wav", context.SoundCalls[0].sound);
        Assert.Equal(214, context.SoundCalls[0].thingId);
    }

    [Fact]
    public void BartenderCog_BartenderKilled_SetsDeadsamAndMutes()
    {
        var script = LoadScript("01_bartender.cog", SyntheticBartenderCog, SteamJk1Path);
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script, cogId: 29);
        vm.AddInstance(instance);

        var placement = new JklCogPlacement
        {
            Index = 29,
            ScriptName = "01_bartender.cog",
            SymbolArgs = ["208", "214", "207", "350", "370", "353"]
        };
        instance.BindPlacementArgs(placement);

        // Kill bartender (sambt = 214)
        vm.SendMessage(instance, "killed", senderRef: 214);
        Assert.Equal(1, instance.GetSymbolValue("deadsam").AsInt);

        // Subsequent kills should do nothing
        vm.SendMessage(instance, "killed", senderRef: 208);
        vm.SendMessage(instance, "killed", senderRef: 207);
        Assert.Equal(0, instance.GetSymbolValue("deadry").AsInt);
        Assert.Empty(context.SoundCalls);

        // Entered should also do nothing
        vm.SendMessage(instance, "entered");
        Assert.Empty(context.SoundCalls);
    }

    [Fact]
    public void Pulse_TriggersPeriodicallyOnUpdate()
    {
        const string scriptText = """
            symbols
            message startup
            message pulse
            int count=0 local
            end

            code
            startup:
                SetPulse(1.0);
                return;

            pulse:
                count = count + 1;
                return;
            end
            """;

        var script = CogParser.Parse(scriptText, "pulse_test.cog");
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script);
        vm.AddInstance(instance);

        vm.SendMessage(instance, "startup");
        Assert.True(instance.PulseActive);
        Assert.Equal(0, instance.GetSymbolValue("count").AsInt);

        // Update 0.5s -> not yet
        vm.Update(0.5f);
        Assert.Equal(0, instance.GetSymbolValue("count").AsInt);

        // Update 0.6s (total 1.1s) -> pulse fires
        vm.Update(0.6f);
        Assert.Equal(1, instance.GetSymbolValue("count").AsInt);

        // Update another 1.0s -> pulse fires again
        vm.Update(1.0f);
        Assert.Equal(2, instance.GetSymbolValue("count").AsInt);
    }

    [Fact]
    public void StepGuard_InfiniteLoop_ThrowsInvalidOperationException()
    {
        const string scriptText = """
            symbols
            message startup
            end

            code
            startup:
                while (1)
                {
                }
                return;
            end
            """;

        var script = CogParser.Parse(scriptText, "infinite_loop.cog");
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script);
        vm.AddInstance(instance);

        var ex = Assert.Throws<InvalidOperationException>(() => vm.SendMessage(instance, "startup"));
        Assert.Contains("maximum instruction step limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecursionGuard_InfiniteMessagePingPong_ThrowsInvalidOperationException()
    {
        const string scriptText = """
            symbols
            message user0
            end

            code
            user0:
                SendMessage(0, "user0");
                return;
            end
            """;

        var script = CogParser.Parse(scriptText, "pingpong.cog");
        var context = new TestCogWorldContext();
        var vm = new CogVm(context);
        var instance = new CogInstance(script, cogId: 0);
        vm.AddInstance(instance);

        var ex = Assert.Throws<InvalidOperationException>(() => vm.SendMessage(instance, "user0"));
        Assert.Contains("maximum message recursion depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MathVerbs_VectorAndRand_OperateCorrectly()
    {
        const string scriptText = """
            symbols
            message startup
            vector v1 local
            vector v2 local
            vector cross local
            vector norm local
            float dot local
            float dist local
            float len local
            float r local
            end

            code
            startup:
                v1 = VectorSet(1.0, 0.0, 0.0);
                v2 = VectorSet(0.0, 1.0, 0.0);
                cross = VectorCross(v1, v2);
                norm = VectorNorm(VectorSet(0.0, 3.0, 4.0));
                dot = VectorDot(v1, v2);
                dist = VectorDist(v1, v2);
                len = VectorLen(v1);
                r = Rand();
                return;
            end
            """;

        var script = CogParser.Parse(scriptText, "math_test.cog");
        var context = new TestCogWorldContext { RandValue = 0.77f };
        var vm = new CogVm(context);
        var instance = new CogInstance(script);
        vm.AddInstance(instance);

        vm.SendMessage(instance, "startup");

        Assert.Equal(new Vector3(0, 0, 1), instance.GetSymbolValue("cross").AsVector3);
        Assert.Equal(new Vector3(0, 0.6f, 0.8f), instance.GetSymbolValue("norm").AsVector3);
        Assert.Equal(0f, instance.GetSymbolValue("dot").AsFloat);
        Assert.Equal(MathF.Sqrt(2f), instance.GetSymbolValue("dist").AsFloat, tolerance: 0.001f);
        Assert.Equal(1f, instance.GetSymbolValue("len").AsFloat);
        Assert.Equal(0.77f, instance.GetSymbolValue("r").AsFloat);
    }

    [Fact]
    public void RetailGobAssets_AllThreeTargetScripts_RunLifecycleSuccessfully()
    {
        if (!File.Exists(SteamRes2Path) || !File.Exists(SteamJk1Path)) return;

        // 1. Res2.gob 00_door.cog
        using (var res2 = GobArchive.FromFile(SteamRes2Path))
        {
            var doorEntry = res2.Entries.First(e => e.Path.EndsWith("00_door.cog", StringComparison.OrdinalIgnoreCase));
            var doorScript = CogParser.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(doorEntry.Path)), "00_door.cog");
            var ctx = new TestCogWorldContext();
            var vm = new CogVm(ctx);
            var inst = new CogInstance(doorScript, 0);
            vm.AddInstance(inst);
            inst.BindPlacementArgs(new JklCogPlacement { ScriptName = "00_door.cog", SymbolArgs = ["4", "-1", "-1", "-1", "8.0", "2.0", "0.5"] });
            ctx.ThingSectors[4] = 12;

            vm.SendMessage(inst, "startup");
            Assert.False(ctx.SectorAdjoins[12]);
            vm.SendMessage(inst, "activate");
            Assert.True(ctx.SectorAdjoins[12]);
            Assert.Single(ctx.MoveToFrameCalls);

            ctx.ThingFrames[4] = 1;
            vm.SendMessage(inst, "arrived");
            Assert.True(inst.TimerActive);

            vm.Update(2.1f);
            Assert.Equal(2, ctx.MoveToFrameCalls.Count);
        }

        // 2. Res2.gob 00_std_elev.cog
        using (var res2 = GobArchive.FromFile(SteamRes2Path))
        {
            var elevEntry = res2.Entries.First(e => e.Path.EndsWith("00_std_elev.cog", StringComparison.OrdinalIgnoreCase));
            var elevScript = CogParser.Parse(Encoding.ASCII.GetString(res2.ReadAllBytes(elevEntry.Path)), "00_std_elev.cog");
            var ctx = new TestCogWorldContext();
            var vm = new CogVm(ctx);
            var inst = new CogInstance(elevScript, 1);
            vm.AddInstance(inst);
            inst.BindPlacementArgs(new JklCogPlacement { ScriptName = "00_std_elev.cog", SymbolArgs = ["100", "101", "5", "0.25", "2.0", "4.0"] });

            vm.SendMessage(inst, "crossed");
            Assert.True(inst.IsSleeping);
            Assert.Empty(ctx.MoveToFrameCalls);

            vm.Update(0.3f);
            Assert.False(inst.IsSleeping);
            Assert.Single(ctx.MoveToFrameCalls);
            Assert.Equal((5, 1, 4.0f), ctx.MoveToFrameCalls[0]);
        }

        // 3. JK1.GOB 01_bartender.cog
        using (var jk1 = GobArchive.FromFile(SteamJk1Path))
        {
            var barEntry = jk1.Entries.First(e => e.Path.EndsWith("01_bartender.cog", StringComparison.OrdinalIgnoreCase));
            var barScript = CogParser.Parse(Encoding.ASCII.GetString(jk1.ReadAllBytes(barEntry.Path)), "01_bartender.cog");
            var ctx = new TestCogWorldContext();
            var vm = new CogVm(ctx);
            var inst = new CogInstance(barScript, 29);
            vm.AddInstance(inst);
            inst.BindPlacementArgs(new JklCogPlacement { ScriptName = "01_bartender.cog", SymbolArgs = ["208", "214", "207", "350", "370", "353"] });
            ctx.LocalPlayerThing = 100;
            ctx.ThingSectors[100] = 370;

            vm.SendMessage(inst, "killed", senderRef: 208);
            vm.SendMessage(inst, "killed", senderRef: 207);
            Assert.True(inst.IsSleeping);
            vm.Update(1.6f);
            Assert.Single(ctx.SoundCalls);
            Assert.Equal("i00mn19z.wav", ctx.SoundCalls[0].sound);
        }
    }
}
