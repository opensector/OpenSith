using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Cog;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Regression tests for legacy COG verb aliases (SectorAdjoins, SectorLight, IsMoving,
/// jkGetLocalPlayer, WaitForStop), message aliases (activate/activated), symbol mask= parsing
/// and filtering, and retail Level 1 / generic door COGs.
/// </summary>
public sealed class CogAliasesAndDoorTests
{
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    private static CogScript? LoadRetailScript(string gobPath, string fileName)
    {
        if (!File.Exists(gobPath))
            return null;

        using var gob = GobArchive.FromFile(gobPath);
        var entry = gob.Entries.FirstOrDefault(e =>
            Path.GetFileName(e.Path.Replace('\\', '/')).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (entry.Path == null)
            return null;

        string text = Encoding.ASCII.GetString(gob.ReadAllBytes(entry.Path));
        return CogParser.Parse(text, fileName);
    }

    [Fact]
    public void IsMoving_ReturnsOneWhenMovingAndZeroWhenStopped()
    {
        const string source = """
            symbols
            message startup
            thing door
            int result=-1 local
            end
            code
            startup:
                result = IsMoving(door);
                return;
            end
            """;

        var script = CogParser.Parse(source, "test_ismoving.cog");
        var ctx = new Level1CogWorldContext();
        var vm = new CogVm(ctx);
        var instance = new CogInstance(script, cogId: 1);
        instance.SetSymbolValue("door", new CogValue(10));
        vm.AddInstance(instance);

        // Stopped -> 0
        vm.SendMessage(instance, "startup");
        Assert.Equal(0, instance.GetSymbolValue("result").AsInt);

        // Moving -> 1
        ctx.MovingThings.Add(10);
        vm.SendMessage(instance, "startup");
        Assert.Equal(1, instance.GetSymbolValue("result").AsInt);
    }

    [Fact]
    public void LegacySectorAndPlayerAliases_RouteToWorldContext()
    {
        const string source = """
            symbols
            message startup
            sector sec
            int player=-1 local
            end
            code
            startup:
                SectorAdjoins(sec, 1);
                SectorLight(sec, 0.75, 0.5);
                player = jkGetLocalPlayer();
                return;
            end
            """;

        var script = CogParser.Parse(source, "test_aliases.cog");
        var ctx = new Level1CogWorldContext { LocalPlayerThing = 42 };
        var vm = new CogVm(ctx);
        var instance = new CogInstance(script, cogId: 1);
        instance.SetSymbolValue("sec", new CogValue(7));
        vm.AddInstance(instance);

        vm.SendMessage(instance, "startup");

        Assert.True(ctx.SectorAdjoins[7]);
        Assert.Equal((0.75f, 0.5f), ctx.SectorLights[7]);
        Assert.Equal(42, instance.GetSymbolValue("player").AsInt);
    }

    [Fact]
    public void WaitForStop_SuspendsUntilThingStopsMoving()
    {
        const string source = """
            symbols
            message activated
            thing elev
            surface btn
            end
            code
            activated:
                SetWallCel(btn, 1);
                MoveToFrame(elev, 1, 5.0);
                WaitForStop(elev);
                SetWallCel(btn, 0);
                return;
            end
            """;

        var script = CogParser.Parse(source, "test_waitforstop.cog");
        var ctx = new Level1CogWorldContext();
        var vm = new CogVm(ctx);
        var instance = new CogInstance(script, cogId: 1);
        instance.SetSymbolValue("elev", new CogValue(20));
        instance.SetSymbolValue("btn", new CogValue(30));
        vm.AddInstance(instance);

        // Start moving before WaitForStop is evaluated
        ctx.MovingThings.Add(20);
        vm.SendMessage(instance, "activate");

        // Suspended on WaitForStop: button still at cel 1
        Assert.Equal(1, ctx.SurfaceCels[30]);
        Assert.Equal(20, instance.WaitingForStopThingId);

        // Still moving after tick -> stays suspended
        vm.Update(0.1f);
        Assert.Equal(1, ctx.SurfaceCels[30]);

        // Stops moving -> resumes and resets cel to 0
        ctx.MovingThings.Remove(20);
        vm.Update(0.1f);
        Assert.Equal(0, ctx.SurfaceCels[30]);
        Assert.Equal(-1, instance.WaitingForStopThingId);
    }

    [Fact]
    public void SymbolMaskParsingAndFiltering_MatchesOpenJkdf2HexRules()
    {
        const string source = """
            symbols
            thing defaultSym
            thing hexPrefixSym mask=0x405
            surface hexNoPrefixSym mask=400
            sector allSym mask=-1
            end
            code
            end
            """;

        var script = CogParser.Parse(source, "test_masks.cog");
        var instance = new CogInstance(script, cogId: 1);
        instance.SetSymbolValue("defaultSym", new CogValue(10));
        instance.SetSymbolValue("hexPrefixSym", new CogValue(11));
        instance.SetSymbolValue("hexNoPrefixSym", new CogValue(12));
        instance.SetSymbolValue("allSym", new CogValue(13));

        Assert.Equal(0x401, instance.GetSymbolMask(CogSymbolType.Thing, 10));
        Assert.Equal(0x405, instance.GetSymbolMask(CogSymbolType.Thing, 11));
        Assert.Equal(0x400, instance.GetSymbolMask(CogSymbolType.Surface, 12));
        Assert.Equal(-1, instance.GetSymbolMask(CogSymbolType.Sector, 13));

        // Player bit is 0x400 (1 << 10)
        Assert.True(instance.MatchesSymbolMask(CogSymbolType.Surface, 12, 0x400));
        Assert.False(instance.MatchesSymbolMask(CogSymbolType.Surface, 12, 0x004));
        Assert.True(instance.MatchesSymbolMask(CogSymbolType.Thing, 11, 0x004));
        Assert.True(instance.MatchesSymbolMask(CogSymbolType.Sector, 13, 0x004));
    }

    [Fact]
    public void Retail_00DoorCog_StartupAndActivateWorkEndToEnd()
    {
        var script = LoadRetailScript(SteamRes2Path, "00_door.cog");
        if (script == null) return;

        var ctx = new Level1CogWorldContext();
        ctx.ThingSectors[10] = 55;
        var vm = new CogVm(ctx);
        var instance = new CogInstance(script, cogId: 1);
        instance.BindPlacementArgs(new JklCogPlacement
        {
            ScriptName = "00_door.cog",
            SymbolArgs = ["10", "11", "-1", "-1"]
        });
        vm.AddInstance(instance);

        vm.SendMessage(instance, "startup");
        Assert.False(ctx.SectorAdjoins[55]);
        Assert.Equal((0.5f, 0f), ctx.SectorLights[55]);

        vm.SendMessage(instance, "activate", senderRef: 10, senderId: 0, sourceRef: -1);
        Assert.True(ctx.SectorAdjoins[55]);
        Assert.Equal(2, ctx.MoveToFrameCalls.Count);
        Assert.Equal((10, 1, 8.0f), ctx.MoveToFrameCalls[0]);
        Assert.Equal((11, 1, 8.0f), ctx.MoveToFrameCalls[1]);
    }

    [Fact]
    public void Retail_00DoorSwitchCog_StartupAndActivatedWorkEndToEnd()
    {
        var script = LoadRetailScript(SteamRes2Path, "00_doorswitch.cog");
        if (script == null) return;

        var ctx = new Level1CogWorldContext();
        ctx.ThingSectors[10] = 77;
        var vm = new CogVm(ctx);
        var instance = new CogInstance(script, cogId: 1);
        instance.BindPlacementArgs(new JklCogPlacement
        {
            ScriptName = "00_doorswitch.cog",
            SymbolArgs = ["10", "11", "-1", "-1", "20", "21"]
        });
        vm.AddInstance(instance);

        // startup: calls SectorAdjoins(doorsector, 0) and SectorLight(doorsector, lightvalue, 0.0)
        vm.SendMessage(instance, "startup");
        Assert.False(ctx.SectorAdjoins[77]);
        Assert.Equal((0.5f, 0f), ctx.SectorLights[77]);

        // If door is already moving, ismoving(door0) returns 1 -> checkstatus prevents MoveToFrame
        ctx.MovingThings.Add(10);
        vm.SendMessage(instance, "activate", senderRef: 20, senderId: 2, sourceRef: -1);
        Assert.Empty(ctx.MoveToFrameCalls);

        // When stopped, activate (dispatched to activated:) opens adjoins and moves both doors
        ctx.MovingThings.Remove(10);
        vm.SendMessage(instance, "activate", senderRef: 20, senderId: 2, sourceRef: -1);
        Assert.True(ctx.SectorAdjoins[77]);
        Assert.Equal(1, ctx.SurfaceCels[20]);
        Assert.Equal(1, ctx.SurfaceCels[21]);
        Assert.Equal(2, ctx.MoveToFrameCalls.Count);
        Assert.Equal((10, 1, 8.0f), ctx.MoveToFrameCalls[0]);
        Assert.Equal((11, 1, 8.0f), ctx.MoveToFrameCalls[1]);
    }

    [Fact]
    public void Retail_00OneWayDoorCog_StartupAndActivateWorkEndToEnd()
    {
        var script = LoadRetailScript(SteamRes2Path, "00_1waydoor.cog");
        if (script == null) return;

        var ctx = new Level1CogWorldContext { LocalPlayerThing = 100 };
        ctx.ThingSectors[10] = 88; // doorsector
        ctx.ThingSectors[100] = 99; // inside sector
        var vm = new CogVm(ctx);
        var instance = new CogInstance(script, cogId: 1);
        instance.BindPlacementArgs(new JklCogPlacement
        {
            ScriptName = "00_1waydoor.cog",
            SymbolArgs = ["10", "99"]
        });
        vm.AddInstance(instance);

        // startup: calls SectorAdjoins(doorsector, 0)
        vm.SendMessage(instance, "startup");
        Assert.False(ctx.SectorAdjoins[88]);

        // If door is moving, IsMoving(door) returns 1 -> early return
        ctx.MovingThings.Add(10);
        vm.SendMessage(instance, "activate", senderRef: 10);
        Assert.Empty(ctx.MoveToFrameCalls);

        // When stopped and player in 'inside' sector, opens adjoins & moves door
        ctx.MovingThings.Remove(10);
        vm.SendMessage(instance, "activate", senderRef: 10);
        Assert.True(ctx.SectorAdjoins[88]);
        Assert.Single(ctx.MoveToFrameCalls);
        Assert.Equal((10, 1, 8.0f), ctx.MoveToFrameCalls[0]);
    }

    [Fact]
    public void Retail_01CargoDoorsCog_ActivateWithSenderId1TriggersMoveToFrame()
    {
        var script = LoadRetailScript(SteamJk1Path, "01_cargodoors.cog");
        if (script == null) return;

        var ctx = new Level1CogWorldContext { Difficulty = 1 };
        ctx.SurfaceCenters[30] = new System.Numerics.Vector3(1f, 2f, 3f);
        var vm = new CogVm(ctx);
        var instance = new CogInstance(script, cogId: 1);
        instance.BindPlacementArgs(new JklCogPlacement
        {
            ScriptName = "01_cargodoors.cog",
            SymbolArgs = ["30", "40", "41"]
        });
        vm.AddInstance(instance);

        vm.SendMessage(instance, "startup");
        vm.SendMessage(instance, "activate", senderRef: 30, senderId: 1, sourceRef: -1);

        Assert.Equal(1, ctx.SurfaceCels[30]);
        Assert.Equal(2, ctx.MoveToFrameCalls.Count);
        Assert.Equal((40, 1, 6.0f), ctx.MoveToFrameCalls[0]);
        Assert.Equal((41, 1, 6.0f), ctx.MoveToFrameCalls[1]);
    }
}
