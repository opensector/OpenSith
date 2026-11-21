using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Client;
using OpenSith.Core.AI;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// End-to-end regression tests for Boss Battle #2 (Gorc &amp; Pic, <c>11gorc.jkl</c>) and synthetic
/// unit tests for <c>AISetClass</c>, <c>AISetMovePos</c>/<c>AISetLookPos</c>/<c>AIJump</c>,
/// <c>SectorThrust</c>, <c>SITH_TF_CAPTURED</c> <c>touched</c> triggers, <c>damaged</c> <c>ReturnEx</c>
/// chaining, and Force Grip <c>0x40000</c> actor freeze.
/// </summary>
public class GorcPicBossBattleTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    private sealed class InMemoryVfs : IVirtualFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddText(string path, string content) =>
            _files[path.Replace('\\', '/')] = Encoding.ASCII.GetBytes(content);

        public bool FileExists(string path) => _files.ContainsKey(path.Replace('\\', '/'));

        public byte[] ReadAllBytes(string path) =>
            _files.TryGetValue(path.Replace('\\', '/'), out var data)
                ? data
                : throw new FileNotFoundException(path);

        public string ReadAllText(string path) => Encoding.ASCII.GetString(ReadAllBytes(path));
        public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path));
        public IEnumerable<string> ListFiles() => _files.Keys;
        public void Dispose() { }
    }

    private static VirtualFileSystem? TryMountRetailVfs()
    {
        string res1Path = Path.Combine(SteamPath, "Resource", "Res1hi.gob");
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");

        if (!File.Exists(res2Path) || !File.Exists(jk1Path))
            return null;

        var vfs = new VirtualFileSystem();
        if (File.Exists(res1Path))
            vfs.Mount(GobArchive.FromFile(res1Path));
        vfs.Mount(GobArchive.FromFile(res2Path));
        vfs.Mount(GobArchive.FromFile(jk1Path));
        return vfs;
    }

    private static WorldSimulation? TryCreateGorcPicSimulation(out JklLevel level)
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null)
        {
            level = null!;
            return null;
        }

        level = JklReader.Parse(vfs.ReadAllText("jkl/11gorc.jkl"), "11gorc");
        return new WorldSimulation(level, vfs, difficulty: 1);
    }

    private static JklLevel BuildMinimalFloorLevel()
    {
        const string jkl = """
            SECTION: GEORESOURCE
            World vertices 4
            0: -5.0 -5.0 0.0
            1:  5.0 -5.0 0.0
            2:  5.0  5.0 0.0
            3: -5.0  5.0 0.0
            World surfaces 1
            0: 0 0x4 0 4 0 0 -1 0.0 4 0,0 1,0 2,0 3,0 0 0 0 0
            0: 0.0 0.0 1.0
            SECTION: SECTORS
            World sectors 1
            SECTOR 0
            FLAGS 0x0
            AMBIENT LIGHT 1.0
            EXTRA LIGHT 0.0
            BOUNDING BOX -5.0 -5.0 -1.0 5.0 5.0 5.0
            SURFACES 0 1
            SECTION: TEMPLATES
            World templates 0
            SECTION: THINGS
            World things 0
            """;
        return JklReader.Parse(jkl, "synthetic_gorc");
    }

    [Fact]
    public void Retail11Gorc_StartupInitializesGorcAndPic_AndSkipCutsceneUnlocksBosses()
    {
        var sim = TryCreateGorcPicSimulation(out var level);
        if (sim == null) return;

        // Gorc (Thing 2): go.3do, health=800, saberorange1/0.mat, length=0.185, JkFlags & 0x5
        var gorc = level.Things[2];
        Assert.Equal("gorc", gorc.TemplateName, ignoreCase: true);
        Assert.Equal("go.3do", level.ResolveModel3D(gorc.TemplateName), ignoreCase: true);
        Assert.Equal(800f, sim.GetThingHealth(2), precision: 1);
        Assert.True(sim.TryGetThingSaberInfo(2, out var gorcSaber));
        Assert.Equal("saberorange1.mat", gorcSaber.SideMat, ignoreCase: true);
        Assert.Equal("saberorange0.mat", gorcSaber.TipMat, ignoreCase: true);
        Assert.Equal(0.185f, gorcSaber.Length, precision: 3);
        Assert.Equal(0x5, sim.JkGetFlags(2) & 0x5);

        // Pic (Thing 3): pi.3do, health=600, saberorange1/0.mat, length=0.09, JkFlags & 0x5
        var pic = level.Things[3];
        Assert.Equal("pic", pic.TemplateName, ignoreCase: true);
        Assert.Equal("pi.3do", level.ResolveModel3D(pic.TemplateName), ignoreCase: true);
        Assert.Equal(600f, sim.GetThingHealth(3), precision: 1);
        Assert.True(sim.TryGetThingSaberInfo(3, out var picSaber));
        Assert.Equal("saberorange1.mat", picSaber.SideMat, ignoreCase: true);
        Assert.Equal("saberorange0.mat", picSaber.TipMat, ignoreCase: true);
        Assert.Equal(0.09f, picSaber.Length, precision: 3);
        Assert.Equal(0x5, sim.JkGetFlags(3) & 0x5);

        // Intro cutscene pauses both bosses; SkipActiveCutscene() clears 26s intro and unlocks both
        Assert.True(sim.IsInCutscene);
        Assert.NotEqual(0, sim.AiGetMode(2) & 0x2000);
        Assert.NotEqual(0, sim.AiGetMode(3) & 0x2000);

        sim.SkipActiveCutscene();
        Assert.False(sim.IsInCutscene);
        Assert.Equal(0, sim.AiGetMode(2) & 0x2000);
        Assert.Equal(0, sim.AiGetMode(3) & 0x2000);
    }

    [Fact]
    public void Retail11Gorc_KillingGorcSwapsPicToAngryAi_AndTouching8t88HeadCompletesLevel()
    {
        var sim = TryCreateGorcPicSimulation(out var level);
        if (sim == null) return;

        sim.SkipActiveCutscene();

        // Keep Kyle below altitude -0.34 so 11_endlevel.cog's pulse swaps Pic to picangry.ai
        Vector3 lowPlayerPos = new(-0.168f, 12.3775f, -0.50f);
        int playerSector = level.Things[1].SectorIndex;

        // Kill Gorc (Thing 2)
        sim.DamageThing(2, 1000f, 0x10, 1);
        Assert.Equal(0f, sim.GetThingHealth(2), precision: 1);

        // Advance past 1.5s scream timer & 3.0s pulse in 11_endlevel.cog
        for (int i = 0; i < 70; i++)
            sim.Update(0.05f, lowPlayerPos, playerSector);

        Assert.Contains("picangry.ai", sim.GetNpcAiClassName(3), StringComparison.OrdinalIgnoreCase);

        // Kill Pic (Thing 3): LevelCompleted must remain false until Kyle touches 8t88_head (Thing 5)
        sim.DamageThing(3, 1000f, 0x10, 1);
        Assert.Equal(0f, sim.GetThingHealth(3), precision: 1);
        Assert.False(sim.LevelCompleted);

        // Move Kyle onto 8t88_head (Thing 5)
        var head = level.Things[5];
        sim.Update(0.05f, head.Position, head.SectorIndex);

        // 11_endlevel.cog sleeps 3.0s before calling JkEndLevel(1)
        for (int i = 0; i < 40; i++)
            sim.Update(0.1f, head.Position, head.SectorIndex);

        Assert.True(sim.LevelCompleted);
    }

    [Fact]
    public void Synthetic_AiSetClass_PreservesPausedAndCombatState()
    {
        var level = BuildMinimalFloorLevel();
        level.Templates["boss"] = new JklTemplate
        {
            Name = "boss",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["aiclass"] = "base.ai",
                ["health"] = "100",
                ["maxvel"] = "0.5",
                ["size"] = "0.06"
            }
        };

        level.Things.Add(new JklThing { ThingIndex = 0, TemplateName = "walkplayer", Position = new Vector3(0, 0, 0.1f), SectorIndex = 0 });
        level.Things.Add(new JklThing { ThingIndex = 1, TemplateName = "boss", Position = new Vector3(1, 0, 0.1f), SectorIndex = 0 });

        var vfs = new InMemoryVfs();
        vfs.AddText("misc/ai/base.ai", "alignment=-1.0, rank=0.5, fov=180, maxstep=0.2, sightdist=5.0, heardist=2.0\n");
        vfs.AddText("misc/ai/angry.ai", "alignment=-1.0, rank=1.0, fov=360, maxstep=0.2, sightdist=8.0, heardist=4.0\n");

        var sim = new WorldSimulation(level, vfs);
        sim.AiSetMode(1, 0x2000 | 0x4); // Disabled + Attacking

        sim.AISetClass(1, "angry.ai");

        Assert.Contains("angry.ai", sim.GetNpcAiClassName(1), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(0, sim.AiGetMode(1) & 0x2000);
        Assert.NotEqual(0, sim.AiGetMode(1) & 0x4);
    }

    [Fact]
    public void Synthetic_AiSetMovePos_AiJump_And_SectorThrust_MoveEntities()
    {
        var level = BuildMinimalFloorLevel();
        level.Templates["npc"] = new JklTemplate
        {
            Name = "npc",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["aiclass"] = "base.ai",
                ["health"] = "100",
                ["maxvel"] = "0.8",
                ["size"] = "0.06"
            }
        };
        level.Templates["crate"] = new JklTemplate
        {
            Name = "crate",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["move"] = "physics"
            }
        };

        level.Things.Add(new JklThing { ThingIndex = 0, TemplateName = "walkplayer", Position = new Vector3(-2, 0, 0.1f), SectorIndex = 0 });
        level.Things.Add(new JklThing { ThingIndex = 1, TemplateName = "npc", Position = new Vector3(0, 0, 0.13f), SectorIndex = 0 });
        level.Things.Add(new JklThing { ThingIndex = 2, TemplateName = "ghost", Position = new Vector3(2, 0, 0.13f), SectorIndex = 0 });
        level.Things.Add(new JklThing { ThingIndex = 3, TemplateName = "crate", Position = new Vector3(0, 1, 0.1f), SectorIndex = 0 });

        var vfs = new InMemoryVfs();
        vfs.AddText("misc/ai/base.ai", "alignment=-1.0, rank=0.5, fov=180, maxstep=0.2, sightdist=5.0, heardist=2.0\n");

        var sim = new WorldSimulation(level, vfs);

        // 1. AISetMovePos & AISetLookPos steer NPC toward target
        sim.AISetLookPos(1, new Vector3(2, 0, 0.13f));
        sim.AISetMovePos(1, new Vector3(2, 0, 0.13f));
        for (int i = 0; i < 10; i++)
            sim.Update(0.05f, level.Things[0].Position, 0);
        Assert.True(level.Things[1].Position.X > 0.1f);

        // 2. AIJump launches NPC upward/forward toward Thing 2
        float zBeforeJump = level.Things[1].Position.Z;
        sim.AIJump(1, 2, 6.0f);
        sim.Update(0.05f, level.Things[0].Position, 0);
        Assert.True(level.Things[1].Position.Z > zBeforeJump);

        // 3. SectorThrust lifts dynamic things in that sector
        sim.SectorThrust(0, Vector3.UnitZ, 8.0f);
        Assert.Equal(new Vector3(0, 0, 8.0f), sim.GetSectorThrust(0));
        float crateZBefore = level.Things[3].Position.Z;
        for (int i = 0; i < 10; i++)
            sim.Update(0.05f, level.Things[0].Position, 0);
        Assert.True(level.Things[3].Position.Z > crateZBefore);
    }

    [Fact]
    public void Synthetic_DamagedReturnExChaining_CapturedTouchDebounce_And_ActorFreeze()
    {
        var level = BuildMinimalFloorLevel();
        level.Templates["boss"] = new JklTemplate
        {
            Name = "boss",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["aiclass"] = "base.ai",
                ["health"] = "100",
                ["maxvel"] = "0.5",
                ["size"] = "0.06"
            }
        };
        level.Templates["head_item"] = new JklTemplate
        {
            Name = "head_item",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["collide"] = "1",
                ["size"] = "0.1"
            }
        };

        level.Things.Add(new JklThing { ThingIndex = 0, TemplateName = "walkplayer", Position = new Vector3(-2, 0, 0.1f), SectorIndex = 0 });
        level.Things.Add(new JklThing { ThingIndex = 1, TemplateName = "boss", Position = new Vector3(1, 0, 0.13f), SectorIndex = 0 });
        level.Things.Add(new JklThing { ThingIndex = 2, TemplateName = "head_item", Position = new Vector3(3, 0, 0.1f), SectorIndex = 0, ThingFlags = 0x400 });

        level.CogScripts.Add("half_dmg.cog");
        level.CogScripts.Add("head_touch.cog");
        level.Cogs.Add(new JklCogPlacement { Index = 0, ScriptName = "half_dmg.cog", SymbolArgs = ["1"] });
        level.Cogs.Add(new JklCogPlacement { Index = 1, ScriptName = "half_dmg.cog", SymbolArgs = ["1"] });
        level.Cogs.Add(new JklCogPlacement { Index = 2, ScriptName = "head_touch.cog", SymbolArgs = ["2"] });

        var vfs = new InMemoryVfs();
        vfs.AddText("misc/ai/base.ai", "alignment=-1.0, rank=0.5, fov=180, maxstep=0.2, sightdist=5.0, heardist=2.0\n");
        vfs.AddText("cog/half_dmg.cog", """
            symbols
            message damaged
            thing boss mask=0xfff
            end
            code
            damaged:
                ReturnEx(GetParam(0) / 2);
                Return;
            end
            """);
        vfs.AddText("cog/head_touch.cog", """
            symbols
            message touched
            thing head linkid=3
            int touches=0 local
            end
            code
            touched:
                if (GetSenderId() != 3) Return;
                touches = touches + 1;
                if (touches == 2) jkEndLevel(1);
                Return;
            end
            """);

        var sim = new WorldSimulation(level, vfs);

        // Two linked half_dmg.cog instances chain 40 -> 20 -> 10
        sim.DamageThing(1, 40f, 0x2, 0);
        Assert.Equal(90f, sim.GetThingHealth(1), precision: 1);

        // SetActorFlags(0x40000) freezes player and NPC
        sim.SetActorFlags(0, 0x40000);
        sim.SetActorFlags(1, 0x40000);
        Assert.True(sim.IsPlayerFrozen);
        Assert.True(sim.IsActorFrozen(1));
        Vector3 npcPosBefore = level.Things[1].Position;
        sim.AISetMovePos(1, new Vector3(4, 0, 0.13f));
        sim.Update(0.05f, level.Things[0].Position, 0);
        Assert.Equal(npcPosBefore, level.Things[1].Position);
        sim.ClearActorFlags(0, 0x40000);
        sim.ClearActorFlags(1, 0x40000);

        // Non-item SITH_TF_CAPTURED touch triggers once while overlapping (debounced), then again after leaving
        sim.Update(0.05f, new Vector3(3, 0, 0.1f), 0);
        sim.Update(0.05f, new Vector3(3, 0, 0.1f), 0);
        Assert.False(sim.LevelCompleted);

        sim.Update(0.05f, new Vector3(-2, 0, 0.1f), 0);
        sim.Update(0.05f, new Vector3(3, 0, 0.1f), 0);
        Assert.True(sim.LevelCompleted);
    }
}
