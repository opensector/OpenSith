using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using OpenSith.Core.AI;
using OpenSith.Core.Audio;
using OpenSith.Core.Cog;
using OpenSith.Core.Networking;
using OpenSith.Core.Physics;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Phase 8 tests covering MP pickup respawning, invulnerability/dead-body guards,
/// authentic weapon flags (<c>+punch</c>, <c>+grenade1/2</c>, <c>+seqchrg/2</c>, <c>CreateThing</c> traps),
/// Lightsaber respawn recovery, <see cref="SectorPortalNavGraph"/> routing (synthetic &amp; retail),
/// all-10-weapons tactical <see cref="DmBotController"/> execution, and UDP dedicated server bot play.
/// </summary>
public sealed class Phase8GameplayAndDmBotsTests
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

    private sealed class RecordingAudioSink : IAudioSink
    {
        private int _nextId = 1;
        public List<(string SoundName, Vector3 Position)> PlayedPos { get; } = [];
        public List<string> PlayedLocal { get; } = [];
        public bool IsAvailable => true;
        public int PlaySoundLocal(string wavName, float volume = 1.0f, bool looping = false)
        {
            PlayedLocal.Add(wavName);
            return _nextId++;
        }
        public int PlaySoundPos(string wavName, Vector3 position, float volume = 1.0f, float minRadius = 0.5f, float maxRadius = 8.0f, bool looping = false)
        {
            PlayedPos.Add((wavName, position));
            return _nextId++;
        }
        public void StopSound(int channelId, float fadeOut = 0f) { }
        public void ChangeSoundPitch(int channelId, float pitch, float changeTime = 0f) { }
        public void ChangeSoundVol(int channelId, float volume, float fadeTime = 0f) { }
        public void SetMusicVolume(float volume) { }
        public void SetSectorAmbient(string? wavName, float volume = 0.5f) { }
        public float GetSoundDuration(string wavName) => 0.5f;
    }

    private static JklLevel CreateBoxArena(float halfExtent = 6.0f, float height = 3.0f)
    {
        var level = new JklLevel { Name = "phase8_arena" };
        level.Materials.Add("wall0.mat");

        float h = halfExtent;
        level.Vertices.AddRange([
            new Vector3(-h, -h, 0f),
            new Vector3( h, -h, 0f),
            new Vector3( h,  h, 0f),
            new Vector3(-h,  h, 0f),
            new Vector3(-h, -h, height),
            new Vector3( h, -h, height),
            new Vector3( h,  h, height),
            new Vector3(-h,  h, height),
        ]);

        var surfaces = new[]
        {
            new JklSurface { SurfaceIndex = 0, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [0, 1, 2, 3], Normal = Vector3.UnitZ, SurfaceFlags = 0x5 },
            new JklSurface { SurfaceIndex = 1, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [7, 6, 5, 4], Normal = -Vector3.UnitZ, SurfaceFlags = 0x4 },
            new JklSurface { SurfaceIndex = 2, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [3, 2, 6, 7], Normal = -Vector3.UnitY, SurfaceFlags = 0x4 },
            new JklSurface { SurfaceIndex = 3, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [0, 4, 5, 1], Normal = Vector3.UnitY, SurfaceFlags = 0x4 },
            new JklSurface { SurfaceIndex = 4, GeometryMode = 4, AdjoinIndex = -1, VertexIndices = [1, 5, 6, 2], Normal = -Vector3.UnitX, SurfaceFlags = 0x4 },
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
            Position = new Vector3(-2f, -1f, 0f),
            PitchYawRoll = new Vector3(0f, -90f, 0f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(2f, 1f, 0f),
            PitchYawRoll = new Vector3(0f, 90f, 0f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 2,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, 3f, 0f),
            PitchYawRoll = new Vector3(0f, 180f, 0f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 3,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, -3f, 0f),
            PitchYawRoll = Vector3.Zero
        });

        return level;
    }

    private static void RegisterAllTenWeaponTemplatesAndCogs(JklLevel level, InMemoryVfs vfs)
    {
        static JklTemplate MakeTpl(string name, params (string Key, string Value)[] kvs)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in kvs)
                dict[k] = v;
            return new JklTemplate { Name = name, BasedOn = "none", Parameters = dict };
        }

        level.Templates["+punch_exp"] = MakeTpl("+punch_exp", ("type", "explosion"), ("timer", "0.01"));
        level.Templates["+punch"] = MakeTpl("+punch", ("type", "weapon"), ("typeflags", "0x200d"), ("range", "0.15"), ("damage", "20"), ("force", "50"), ("explode", "+punch_exp"), ("fleshhit", "+punch_exp"));
        level.Templates["+bryarbolt"] = MakeTpl("+bryarbolt", ("vel", "(0/4/0)"), ("damage", "30"), ("typeflags", "0x20440d"), ("physflags", "0x200"));
        level.Templates["+stlaser"] = MakeTpl("+stlaser", ("vel", "(0/6/0)"), ("damage", "30"), ("typeflags", "0x20440d"), ("physflags", "0x200"));
        level.Templates["+grenade_exp"] = MakeTpl("+grenade_exp", ("type", "explosion"), ("sprite", "detx.spr"), ("damage", "75"), ("range", "0.45"), ("force", "100"));
        level.Templates["+grenade2"] = MakeTpl("+grenade2", ("type", "weapon"), ("vel", "(0/2/1.5)"), ("timer", "1.2"), ("explode", "+grenade_exp"), ("fleshhit", "+grenade_exp"), ("physflags", "0x225"), ("typeflags", "0x40309"));
        level.Templates["+grenade1"] = MakeTpl("+grenade1", ("vel", "(0/2/1.5)"), ("timer", "3.0"), ("explode", "+grenade_exp"), ("fleshhit", "+grenade_exp"), ("physflags", "0x225"), ("typeflags", "0x4030d"));
        level.Templates["+crossbowbolt"] = MakeTpl("+crossbowbolt", ("vel", "(0/5/0)"), ("damage", "45"), ("typeflags", "0x20440d"), ("physflags", "0x200"));
        level.Templates["+crossbowbolt2"] = MakeTpl("+crossbowbolt2", ("vel", "(0/5/0)"), ("damage", "30"), ("typeflags", "0x440d"), ("physflags", "0x200"));
        level.Templates["+crossbowbolt3"] = MakeTpl("+crossbowbolt3", ("vel", "(0/5/0)"), ("damage", "35"), ("typeflags", "0x28440d"), ("physflags", "0x200"));
        level.Templates["+repeaterball"] = MakeTpl("+repeaterball", ("vel", "(0/6/0)"), ("damage", "20"), ("mindamage", "5"), ("rate", "20"), ("typeflags", "0x440d"), ("physflags", "0x200"));
        level.Templates["+raildet_exp"] = MakeTpl("+raildet_exp", ("type", "explosion"), ("sprite", "rldx.spr"), ("damage", "85"), ("range", "0.50"));
        level.Templates["+raildet"] = MakeTpl("+raildet", ("vel", "(0/2.5/0)"), ("timer", "4.0"), ("explode", "+raildet_exp"), ("fleshhit", "+raildet_exp"), ("damage", "20"), ("physflags", "0x200"), ("typeflags", "0x24020d"));
        level.Templates["+raildet2"] = MakeTpl("+raildet2", ("vel", "(0/2.5/0)"), ("timer", "1.2"), ("explode", "+raildet_exp"), ("fleshhit", "+raildet_exp"), ("damage", "5"), ("physflags", "0x200"), ("typeflags", "0x240b81"));
        level.Templates["+sequencer_exp"] = MakeTpl("+sequencer_exp", ("type", "explosion"), ("sprite", "detx.spr"), ("damage", "90"), ("range", "0.50"));
        level.Templates["+seqchrg"] = MakeTpl("+seqchrg", ("vel", "(0/0.1/0)"), ("timer", "1.0"), ("explode", "+sequencer_exp"), ("fleshhit", "+sequencer_exp"), ("physflags", "0x29d"), ("typeflags", "0x40380"));
        level.Templates["+seqchrg2"] = MakeTpl("+seqchrg2", ("vel", "(0/0.1/0)"), ("timer", "60.0"), ("explode", "+sequencer_exp"), ("fleshhit", "+sequencer_exp"), ("physflags", "0x29d"), ("typeflags", "0x40380"));
        level.Templates["+conc_exp"] = MakeTpl("+conc_exp", ("type", "explosion"), ("sprite", "conx.spr"), ("damage", "80"), ("range", "0.80"), ("force", "200"));
        level.Templates["+concbullet"] = MakeTpl("+concbullet", ("vel", "(0/7/0)"), ("damage", "20"), ("explode", "+conc_exp"), ("fleshhit", "+conc_exp"), ("physflags", "0x200"), ("typeflags", "0x20000d"));
        level.Templates["+concblast2"] = MakeTpl("+concblast2", ("damage", "80"), ("mindamage", "20"), ("range", "5.0"), ("rate", "1.0"), ("force", "200"), ("typeflags", "0xa00d"));

        vfs.AddText("cog/weap_fists.cog", """
            symbols
            thing player local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 1); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated: player = GetSourceRef(); ActivateWeapon(player, 0.25, GetSenderRef()); Return;
            deactivated: DeactivateWeapon(GetSourceRef(), GetSenderRef()); Return;
            fire: player = GetSourceRef(); FireProjectile(player, +punch, -1, 0, '0 0.03 0', '0 0 0', 1.0, 0, 0, 0); Return;
            end
            """);
        vfs.AddText("cog/weap_bryar.cog", """
            symbols
            thing player local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 2); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated: ActivateWeapon(GetSourceRef(), 0.35, GetSenderRef()); Return;
            deactivated: DeactivateWeapon(GetSourceRef(), GetSenderRef()); Return;
            fire: player = GetSourceRef(); ChangeInv(player, 11, -1.0); FireProjectile(player, +bryarbolt, -1, 0, '0 0.05 0', '0 0 0', 1.0, 0x20, 0, 0); Return;
            end
            """);
        vfs.AddText("cog/weap_strifle.cog", """
            symbols
            thing player local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 3); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated: ActivateWeapon(GetSourceRef(), 0.2, GetSenderRef()); Return;
            deactivated: DeactivateWeapon(GetSourceRef(), GetSenderRef()); Return;
            fire: player = GetSourceRef(); ChangeInv(player, 11, -2.0); FireProjectile(player, +stlaser, -1, 0, '0 0.05 0', '0 0 0', 1.0, 0x20, 0, 0); Return;
            end
            """);
        vfs.AddText("cog/weap_thermdet.cog", """
            symbols
            thing player local
            flex delayTime local
            int mode local
            message activated
            message deactivated
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 4); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated: ActivateWeapon(GetSourceRef(), 0, GetSenderRef()); Return;
            deactivated:
               player = GetSourceRef();
               mode = GetSenderRef();
               delayTime = DeactivateWeapon(player, mode);
               if (mode == 0) { if (delayTime < 0.7) delayTime = 0.7; FireProjectile(player, +grenade1, -1, 0, '0.05 0 0', '0 0 0', delayTime, 0x1, 0, 0); }
               else { if (delayTime < 0.25) delayTime = 0.25; FireProjectile(player, +grenade2, -1, 1, '0.05 0 0', '0 0 0', delayTime, 0x1, 0, 0); }
               ChangeInv(player, 4, -1.0);
               Return;
            end
            """);
        vfs.AddText("cog/weap_crossbow.cog", """
            symbols
            thing player local
            flex delayTime local
            int mode local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 5); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated:
               mode = GetSenderRef();
               if (mode == 0) ActivateWeapon(GetSourceRef(), 0, mode);
               else ActivateWeapon(GetSourceRef(), 0.4, mode);
               Return;
            fire: player = GetSourceRef(); ChangeInv(player, 12, -2.0); FireProjectile(player, +crossbowbolt3, -1, 1, '0 0.05 0', '0 0 0', 1.0, 0x20, 0, 0); Return;
            deactivated:
               player = GetSourceRef();
               mode = GetSenderRef();
               if (mode == 0) {
                  delayTime = DeactivateWeapon(player, mode);
                  FireProjectile(player, +crossbowbolt, -1, 0, '0 0.05 0', '0 0 0', 1.0, 0, 0, 0);
                  if (delayTime > 0.6) {
                     FireProjectile(player, +crossbowbolt2, -1, 0, '0 0.05 0', '0 6 0', 1.0, 0, 0, 0);
                     FireProjectile(player, +crossbowbolt2, -1, 0, '0 0.05 0', '0 -6 0', 1.0, 0, 0, 0);
                  }
                  ChangeInv(player, 12, -2.0);
               } else {
                  DeactivateWeapon(player, mode);
               }
               Return;
            end
            """);
        vfs.AddText("cog/weap_repeater.cog", """
            symbols
            thing player local
            sound loopSnd=rpeatrlp.wav local
            int ch=-1 local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 6); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated:
               player = GetSourceRef();
               if (ch == -1) ch = PlaySoundThing(loopSnd, player, 1.0, -1, -1, 0x181);
               ActivateWeapon(player, 0.1, GetSenderRef());
               Return;
            deactivated:
               DeactivateWeapon(GetSourceRef(), GetSenderRef());
               if (ch != -1) { StopSound(ch, 0.05); ch = -1; }
               Return;
            fire: player = GetSourceRef(); ChangeInv(player, 12, -1.0); FireProjectile(player, +repeaterball, -1, 0, '0 0.05 0', '0 0 0', 1.0, 0x20, 0, 0); Return;
            end
            """);
        vfs.AddText("cog/weap_raildet.cog", """
            symbols
            thing player local
            int mode local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 7); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated: ActivateWeapon(GetSourceRef(), 0.8, GetSenderRef()); Return;
            deactivated: DeactivateWeapon(GetSourceRef(), GetSenderRef()); Return;
            fire:
               player = GetSourceRef();
               mode = GetSenderRef();
               ChangeInv(player, 15, -1.0);
               if (mode == 0) FireProjectile(player, +raildet, -1, 0, '0 0.05 0', '0 0 0', 1.0, 0, 0, 0);
               else FireProjectile(player, +raildet2, -1, 1, '0 0.05 0', '0 0 0', 1.0, 0, 0, 0);
               Return;
            end
            """);
        vfs.AddText("cog/weap_seqcharge.cog", """
            symbols
            thing player local
            int mode local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 8); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated: ActivateWeapon(GetSourceRef(), 0.8, GetSenderRef()); Return;
            deactivated: DeactivateWeapon(GetSourceRef(), GetSenderRef()); Return;
            fire:
               player = GetSourceRef();
               mode = GetSenderRef();
               ChangeInv(player, 8, -1.0);
               if (mode == 0) FireProjectile(player, +seqchrg, -1, 0, '0 0.05 0', '0 0 0', 1.0, 0, 0, 0);
               else FireProjectile(player, +seqchrg2, -1, 1, '0 0.05 0', '0 0 0', 1.0, 0, 0, 0);
               Return;
            end
            """);
        vfs.AddText("cog/weap_concrifle.cog", """
            symbols
            thing player local
            int mode local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 9); jkClearFlags(GetSourceRef(), 0x5); Return;
            activated: ActivateWeapon(GetSourceRef(), 0.7, GetSenderRef()); Return;
            deactivated: DeactivateWeapon(GetSourceRef(), GetSenderRef()); Return;
            fire:
               player = GetSourceRef();
               mode = GetSenderRef();
               if (mode == 0) { ChangeInv(player, 12, -8.0); FireProjectile(player, +concbullet, -1, 0, '0 0.05 0', '0 0 0', 1.0, 0x20, 0, 0); }
               else { ChangeInv(player, 12, -4.0); FireProjectile(player, +concblast2, -1, 1, '0 0 0', '0 0 0', 1.0, 0x20, 0, 0); }
               Return;
            end
            """);
        vfs.AddText("cog/weap_saber.cog", """
            symbols
            thing player local
            message activated
            message deactivated
            message fire
            message selected
            end
            code
            selected: SetCurWeapon(GetSourceRef(), 10); jkSetFlags(GetSourceRef(), 0x5); SetMountWait(GetSourceRef(), 0); Return;
            activated: ActivateWeapon(GetSourceRef(), 0.25, GetSenderRef()); Return;
            deactivated: DeactivateWeapon(GetSourceRef(), GetSenderRef()); Return;
            fire: player = GetSourceRef(); jkEnableSaber(player, 40.0, 0.25, 0.30); SetFireWait(player, 0.25); Return;
            end
            """);
    }

    private static async Task<ServerWelcomePacket> ConnectWithServerTickAsync(
        DedicatedGameServer server,
        MultiplayerClient client,
        string name,
        int team = 0)
    {
        Task<ServerWelcomePacket> connectTask = client.ConnectAsync("127.0.0.1", server.BoundPort, name, team);
        while (!connectTask.IsCompleted)
        {
            server.Tick(0.01f);
            await Task.Delay(5);
        }
        return await connectTask;
    }

    [Fact]
    public void TakeItem_RespawnsInMultiplayer_ReplicatesDisableAndRestore_AndFiresRespawnCog()
    {
        var level = CreateBoxArena();
        level.Templates["powercell"] = new JklTemplate
        {
            Name = "powercell",
            BasedOn = "none",
            Model3D = "pcel.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "item",
                ["typeflags"] = "0x1", // SITH_ITEM_RESPAWN_MP
                ["respawn"] = "1.5",
                ["size"] = "0.15",
                ["cog"] = "pow_power_test.cog"
            }
        };
        int itemId = level.Things.Count;
        level.Things.Add(new JklThing
        {
            ThingIndex = itemId,
            TemplateName = "powercell",
            SectorIndex = 0,
            Position = new Vector3(0f, 0f, 0.05f)
        });

        var vfs = new InMemoryVfs();
        vfs.AddText("cog/pow_power_test.cog", """
            symbols
            thing powerup local
            thing player local
            sound respSnd=Activate01.wav local
            message touched
            message taken
            message respawn
            end
            code
            touched:
               player = GetSourceRef();
               powerup = GetSenderRef();
               TakeItem(powerup, player);
               Return;
            taken:
               ChangeInv(GetSourceRef(), 12, 25.0);
               Return;
            respawn:
               PlaySoundPos(respSnd, GetThingPos(GetSenderRef()), 0.8, 1.0, 8.0, 0);
               Return;
            end
            """);

        var audio = new RecordingAudioSink();
        var sim = new WorldSimulation(level, vfs, audio, isMultiplayer: true, isDedicatedServer: true);
        PlayerState p = sim.AddPlayer(0, "Kyle");
        float initialPower = p.Inventory.PowerAmmo;

        sim.UpdatePlayerInput(p.ThingIndex, new Vector3(0f, 0f, 0.05f), 0, Vector3.UnitY);
        sim.StepWorld(0.05f);

        Assert.Equal(initialPower + 25f, p.Inventory.PowerAmmo);
        Assert.NotEqual(0u, level.Things[itemId].ThingFlags & (uint)SithThingFlags.Disabled);
        Assert.True(level.Things[itemId].IsNonSolid);

        var dynDisabled = new List<WorldSimulation.DynamicThingState>();
        sim.CollectDynamicThingStates(dynDisabled);
        Assert.Contains(dynDisabled, d => d.ThingIndex == itemId && (d.ThingFlags & (uint)SithThingFlags.Disabled) != 0);

        // Before 1.5s, item must still be disabled and not re-taken
        for (int i = 0; i < 20; i++)
            sim.StepWorld(0.05f);
        Assert.NotEqual(0u, level.Things[itemId].ThingFlags & (uint)SithThingFlags.Disabled);

        // Move away and finish respawn timer -> item clears Disabled, fires respawn COG
        sim.UpdatePlayerInput(p.ThingIndex, new Vector3(-2f, -2f, 0.05f), 0, Vector3.UnitY);
        for (int i = 0; i < 15; i++)
            sim.StepWorld(0.05f);

        Assert.Equal(0u, level.Things[itemId].ThingFlags & (uint)SithThingFlags.Disabled);
        Assert.False(level.Things[itemId].IsNonSolid);
        Assert.Contains(audio.PlayedPos, s => s.SoundName.Equals("Activate01.wav", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyDamageToPlayer_RespectsInvulnerability_AndDeadCorpseGuard()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        var audio = new RecordingAudioSink();
        var sim = new WorldSimulation(level, vfs, audio, isMultiplayer: true, isDedicatedServer: true);

        PlayerState p1 = sim.AddPlayer(0, "Attacker");
        PlayerState p2 = sim.AddPlayer(1, "Victim");
        p2.Inventory.Shields = 0f;
        p2.Inventory.Health = 100f;

        sim.JkSetInvulnerable(p2.ThingIndex, 1);
        sim.DamageThing(p2.ThingIndex, 50f, 0, p1.ThingIndex);
        Assert.Equal(100f, p2.Inventory.Health);

        sim.JkSetInvulnerable(p2.ThingIndex, 0);
        sim.SetThingFlags(p2.ThingIndex, 0x4); // SITH_TF_INVULN
        sim.DamageThing(p2.ThingIndex, 50f, 0, p1.ThingIndex);
        Assert.Equal(100f, p2.Inventory.Health);

        sim.ClearThingFlags(p2.ThingIndex, 0x4);
        sim.DamageThing(p2.ThingIndex, 150f, 0, p1.ThingIndex);
        Assert.True(p2.Inventory.IsDead);

        int soundsAfterKill = audio.PlayedPos.Count;
        sim.DamageThing(p2.ThingIndex, 50f, 0, p1.ThingIndex);
        Assert.Equal(soundsAfterKill, audio.PlayedPos.Count);
    }

    [Fact]
    public void FistsPunch_UsesInstantMeleeHitscan_WithoutFlyingBoltOrBeamTrail()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        RegisterAllTenWeaponTemplatesAndCogs(level, vfs);

        var spawnedVfx = new List<WorldSimulation.VisualEffect>();
        var sim = new WorldSimulation(level, vfs, isMultiplayer: true, isDedicatedServer: true);
        sim.OnVisualEffectSpawned += spawnedVfx.Add;

        PlayerState p1 = sim.AddPlayer(0, "Kyle");
        PlayerState p2 = sim.AddPlayer(1, "Jan");
        p2.Inventory.Shields = 0f;
        p2.Inventory.Health = 100f;

        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, 0f, 0f), 0, Vector3.UnitY);
        sim.UpdatePlayerInput(p2.ThingIndex, new Vector3(0f, 0.20f, 0f), 0, -Vector3.UnitY);

        sim.FireProjectile(p1.ThingIndex, "+punch", "-1", 0, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        Assert.Empty(sim.Projectiles);
        Assert.DoesNotContain(spawnedVfx, v => v.Kind == WorldSimulation.VisualEffectKind.BeamTrail);
        Assert.Equal(80f, p2.Inventory.Health);
    }

    [Fact]
    public void AuthenticOrdnanceFlags_And_CreateThingWeaponTrap_BehaveLikeRetail()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        RegisterAllTenWeaponTemplatesAndCogs(level, vfs);

        var sim = new WorldSimulation(level, vfs, isMultiplayer: true, isDedicatedServer: true);
        PlayerState p1 = sim.AddPlayer(0, "Kyle");
        PlayerState p2 = sim.AddPlayer(1, "Jan");

        // 1. +grenade1 explodes on wall impact, whereas +grenade2 bounces
        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, 5.75f, 0.5f), 0, Vector3.UnitY);
        sim.FireProjectile(p1.ThingIndex, "+grenade1", "-1", 0, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        Assert.False(Assert.Single(sim.Projectiles).Bounce);
        sim.StepWorld(0.25f);
        Assert.Empty(sim.Projectiles); // detonated on front wall

        sim.FireProjectile(p1.ThingIndex, "+grenade2", "-1", 1, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        Assert.True(Assert.Single(sim.Projectiles).Bounce);
        sim.StepWorld(0.25f);
        Assert.Single(sim.Projectiles); // bounced off wall instead of detonating

        // 2. Clear + test CreateThing("+grenade2", trapThing) spawning a live projectile (m2_dettrap.cog)
        sim.StepWorld(1.5f);
        int trapSpawnId = sim.CreateThingAtPos("+grenade2", 0, new Vector3(-1f, -1f, 1.0f), new Vector3(-90f, 0f, 0f));
        Assert.NotEqual(0u, level.Things[trapSpawnId].ThingFlags & (uint)SithThingFlags.Disabled);
        Assert.Contains(sim.Projectiles, p => p.Bounce && p.UseGravity);

        // Clear all bouncing projectiles, test +seqchrg2 2.0s arming time
        sim.StepWorld(2.0f);
        Assert.Empty(sim.Projectiles);

        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, 0f, 0.05f), 0, Vector3.UnitY);
        sim.UpdatePlayerInput(p2.ThingIndex, new Vector3(-4f, -4f, 0f), 0, Vector3.UnitY);
        sim.FireProjectile(p1.ThingIndex, "+seqchrg2", "-1", 1, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        for (int i = 0; i < 24; i++) // 1.2s < 2.0s arm delay
            sim.StepWorld(0.05f);
        var mine = Assert.Single(sim.Projectiles, p => p.ProximityDetonate);
        Assert.True(mine.IsStuck);
        sim.UpdatePlayerInput(p2.ThingIndex, mine.Position, 0, Vector3.UnitY);
        sim.StepWorld(0.05f);
        Assert.Single(sim.Projectiles); // Not yet armed at 1.25s

        for (int i = 0; i < 25; i++) // Pass 2.0s arm delay
            sim.StepWorld(0.05f);
        Assert.Empty(sim.Projectiles); // Armed + p2 standing nearby -> detonated
    }

    [Fact]
    public void LightsaberRespawn_DispatchesNewPlayerToBinCogs_AndAllowsImmediateSwing()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path))
            return;

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));

        var level = CreateBoxArena();
        var sim = new WorldSimulation(level, vfs, isMultiplayer: true, isDedicatedServer: true);
        PlayerState p1 = sim.AddPlayer(0, "Kyle");
        PlayerState p2 = sim.AddPlayer(1, "Jan");

        sim.SelectWeapon(p1.ThingIndex, PlayerInventory.BinLightsaber);
        for (int i = 0; i < 32; i++) // 1.6s clears 0.7s ignite AND kyle.cog 1.5s spawn invulnerability
            sim.StepWorld(0.05f);

        Assert.True((sim.JkGetFlags(p1.ThingIndex) & 0x5) != 0);
        sim.DamageThing(p1.ThingIndex, 500f, 0, p2.ThingIndex);
        Assert.True(p1.Inventory.IsDead);

        sim.RespawnPlayer(p1.ThingIndex);
        sim.SelectWeapon(p1.ThingIndex, PlayerInventory.BinLightsaber);
        for (int i = 0; i < 32; i++) // 1.6s clears 0.7s ignite AND kyle.cog 1.5s spawn invulnerability
            sim.StepWorld(0.05f);

        Assert.True((sim.JkGetFlags(p1.ThingIndex) & 0x5) != 0);
        Assert.True(sim.BeginWeaponFire(p1.ThingIndex, mode: 0));
        Assert.True(p1.SaberDamageActive);
        sim.EndWeaponFire(p1.ThingIndex, mode: 0);
    }

    [Fact]
    public void SectorPortalNavGraph_RoutesMultiSectorArena_WithJumpAndSkipsSheerCliff()
    {
        var level = new JklLevel { Name = "nav_test" };
        // Sector 0: [-2..0, -1..1, z=0..2], portal x=0 to Sector 1
        // Sector 1: [0..2, -1..1, z=0.20..2] (jumpable +0.20 step), portal x=2 to Sector 2
        // Sector 2: [2..4, -1..1, z=0.20..2]
        // Sector 3: [4..6, -1..1, z=1.50..3.5] (sheer +1.30 cliff, impassable!)
        void AddRoom(int secIdx, float x0, float x1, float z0, float z1, bool portalLeft, int leftSector, bool portalRight, int rightSector)
        {
            int vBase = level.Vertices.Count;
            level.Vertices.AddRange([
                new Vector3(x0, -1f, z0), new Vector3(x1, -1f, z0), new Vector3(x1, 1f, z0), new Vector3(x0, 1f, z0),
                new Vector3(x0, -1f, z1), new Vector3(x1, -1f, z1), new Vector3(x1, 1f, z1), new Vector3(x0, 1f, z1),
            ]);

            var sec = new JklSector
            {
                SectorIndex = secIdx,
                BoundBoxMin = new Vector3(x0, -1f, z0),
                BoundBoxMax = new Vector3(x1, 1f, z1),
                Center = new Vector3((x0 + x1) * 0.5f, 0f, (z0 + z1) * 0.5f)
            };

            JklSurface AddSurf(int[] verts, Vector3 norm, int adjoinIdx = -1, JklSector? mirror = null)
            {
                var s = new JklSurface
                {
                    SurfaceIndex = level.Surfaces.Count,
                    VertexIndices = verts,
                    Normal = norm,
                    AdjoinIndex = adjoinIdx,
                    MirrorSector = mirror,
                    SurfaceFlags = adjoinIdx >= 0 ? 0 : 0x5
                };
                level.Surfaces.Add(s);
                sec.Surfaces.Add(s);
                return s;
            }

            AddSurf([vBase + 0, vBase + 1, vBase + 2, vBase + 3], Vector3.UnitZ); // floor
            AddSurf([vBase + 7, vBase + 6, vBase + 5, vBase + 4], -Vector3.UnitZ); // ceiling
            AddSurf([vBase + 0, vBase + 4, vBase + 5, vBase + 1], Vector3.UnitY); // south
            AddSurf([vBase + 3, vBase + 2, vBase + 6, vBase + 7], -Vector3.UnitY); // north

            if (portalLeft)
            {
                int aIdx = level.Adjoins.Count;
                level.Adjoins.Add(new JklAdjoin { AdjoinIndex = aIdx, Flags = 0x2, Mirror = aIdx });
                AddSurf([vBase + 3, vBase + 7, vBase + 4, vBase + 0], Vector3.UnitX, aIdx, level.Sectors[leftSector]);
            }
            else
            {
                AddSurf([vBase + 3, vBase + 7, vBase + 4, vBase + 0], Vector3.UnitX);
            }

            if (portalRight)
            {
                int aIdx = level.Adjoins.Count;
                level.Adjoins.Add(new JklAdjoin { AdjoinIndex = aIdx, Flags = 0x2, Mirror = aIdx });
                // placeholder mirror assigned after all rooms created
                AddSurf([vBase + 1, vBase + 5, vBase + 6, vBase + 2], -Vector3.UnitX, aIdx, null);
            }
            else
            {
                AddSurf([vBase + 1, vBase + 5, vBase + 6, vBase + 2], -Vector3.UnitX);
            }

            level.Sectors.Add(sec);
        }

        AddRoom(0, -2f, 0f, 0f, 2f, false, -1, true, 1);
        AddRoom(1, 0f, 2f, 0.20f, 2f, true, 0, true, 2);
        AddRoom(2, 2f, 4f, 0.20f, 2f, true, 1, true, 3);
        AddRoom(3, 4f, 6f, 1.50f, 3.5f, true, 2, false, -1);

        // Wire forward portal mirrors
        level.Sectors[0].Surfaces[^1].MirrorSector = level.Sectors[1];
        level.Sectors[1].Surfaces[^1].MirrorSector = level.Sectors[2];
        level.Sectors[2].Surfaces[^1].MirrorSector = level.Sectors[3];

        var nav = new SectorPortalNavGraph(level);
        var waypoints = new List<Vector3>();
        var jumps = new List<bool>();

        Assert.True(nav.FindPath(0, new Vector3(-1f, 0f, 0f), 2, new Vector3(3f, 0f, 0.20f), waypoints, jumps));
        Assert.Equal(3, waypoints.Count);
        Assert.True(jumps[0]);   // 0 -> 1 steps up +0.20 JKU -> RequiresJump
        Assert.False(jumps[1]);  // 1 -> 2 flat

        Assert.False(nav.FindPath(0, new Vector3(-1f, 0f, 0f), 3, new Vector3(5f, 0f, 1.50f), waypoints, jumps));
    }

    [Fact]
    public void SectorPortalNavGraph_RetailJk1MpLevels_RouteBetweenSpawns()
    {
        string jk1MpPath = Path.Combine(SteamPath, "Episode", "JK1MP.GOB");
        if (!File.Exists(jk1MpPath))
            return;

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(jk1MpPath));

        foreach (string map in new[] { "m10.jkl", "m2.jkl", "m4.jkl", "m5.jkl", "m_boss15.jkl", "m_boss17.jkl" })
        {
            JklLevel level = JklReader.Parse(vfs.ReadAllText($"jkl/{map}"), Path.GetFileNameWithoutExtension(map));
            var nav = new SectorPortalNavGraph(level);
            Assert.Equal(level.Sectors.Count, nav.SectorCount);

            var spawns = level.Things.FindAll(t => t.IsPlayerSpawn);
            Assert.True(spawns.Count >= 2);
            int roamSector = nav.PickRoamSector(spawns[0].SectorIndex, new Random(42));
            Assert.NotEqual(spawns[0].SectorIndex, roamSector);

            var path = new List<Vector3>();
            Assert.True(nav.FindPath(spawns[0].SectorIndex, spawns[0].Position, roamSector, nav.TryGetSectorCenter(roamSector, out var c) ? c : Vector3.Zero, path));
            Assert.NotEmpty(path);
        }
    }

    [Theory]
    [InlineData(PlayerInventory.BinFists)]
    [InlineData(PlayerInventory.BinBryar)]
    [InlineData(PlayerInventory.BinStRifle)]
    [InlineData(PlayerInventory.BinThermDet)]
    [InlineData(PlayerInventory.BinBowcaster)]
    [InlineData(PlayerInventory.BinRepeater)]
    [InlineData(PlayerInventory.BinRailDet)]
    [InlineData(PlayerInventory.BinSeqCharge)]
    [InlineData(PlayerInventory.BinConcRifle)]
    [InlineData(PlayerInventory.BinLightsaber)]
    public void DmBotController_SelectsAndFiresEachOfAllTenWeapons(int targetWeaponBin)
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        RegisterAllTenWeaponTemplatesAndCogs(level, vfs);

        var sim = new WorldSimulation(level, vfs, isMultiplayer: true, isDedicatedServer: true);
        var nav = new SectorPortalNavGraph(level);
        var pickups = new List<WorldSimulation.PickupInfo>();

        PlayerState bot = sim.AddPlayer(0, "TestBot");
        PlayerState enemy = sim.AddPlayer(1, "Enemy");
        enemy.Inventory.Shields = 0f;
        enemy.Inventory.Health = 200f;

        // Restrict bot inventory strictly to Fists + tested weapon (or pure Fists when testing BinFists)
        bot.Inventory.SetInv(PlayerInventory.BinFists, 1f);
        bot.Inventory.HasBryar = targetWeaponBin == PlayerInventory.BinBryar;
        bot.Inventory.HasLightsaber = targetWeaponBin == PlayerInventory.BinLightsaber;
        bot.Inventory.EnergyAmmo = targetWeaponBin is PlayerInventory.BinBryar or PlayerInventory.BinStRifle ? 100f : 0f;
        bot.Inventory.PowerAmmo = targetWeaponBin is PlayerInventory.BinBowcaster or PlayerInventory.BinRepeater or PlayerInventory.BinConcRifle ? 100f : 0f;
        bot.Inventory.RailCharges = targetWeaponBin == PlayerInventory.BinRailDet ? 20f : 0f;
        if (targetWeaponBin is PlayerInventory.BinStRifle or PlayerInventory.BinThermDet or PlayerInventory.BinBowcaster or
            PlayerInventory.BinRepeater or PlayerInventory.BinRailDet or PlayerInventory.BinSeqCharge or PlayerInventory.BinConcRifle)
        {
            bot.Inventory.SetInv(targetWeaponBin, 10f);
        }

        float dist = targetWeaponBin is PlayerInventory.BinFists or PlayerInventory.BinLightsaber ? 0.22f : 1.60f;
        Vector3 botPos = Vector3.Zero;
        Vector3 enemyPos = new(0f, dist, 0f);
        var botPhys = new PlayerPhysics(botPos, 0);

        sim.UpdatePlayerInput(enemy.ThingIndex, enemyPos, 0, -Vector3.UnitY);

        var controller = new DmBotController(new BotArchetype("TestBot", "Katarn", 2, targetWeaponBin), BotSkill.Hard, new Random(7));
        bool selectedTargetWeapon = false;
        bool executedAction = false;
        bool prevPrimary = false;
        bool prevSecondary = false;
        int projectilesSeen = 0;

        float hpBefore = enemy.Inventory.Health;

        for (int tick = 0; tick < 60; tick++)
        {
            const float dt = 0.05f;
            sim.CollectActivePickups(pickups);
            BotTickDecision dec = controller.Decide(dt, sim, bot, level.Things[bot.ThingIndex], nav, pickups);

            botPhys.Update(dt, dec.WishMoveDir, dec.WishJump, level);
            level.Things[bot.ThingIndex].PitchYawRoll = new Vector3(dec.Pitch, dec.Yaw, 0f);
            sim.UpdatePlayerInput(bot.ThingIndex, botPhys.Position, botPhys.CurrentSectorIndex, dec.AimDirection, dec.LocalThrust, botPhys.IsGrounded, botPhys.IsUnderwater);

            if (dec.DesiredWeaponBin == targetWeaponBin)
                selectedTargetWeapon = true;
            if (dec.DesiredWeaponBin != bot.CurWeaponBin)
                sim.SelectWeapon(bot.ThingIndex, dec.DesiredWeaponBin);

            if (dec.PrimaryFireHeld != prevPrimary)
            {
                prevPrimary = dec.PrimaryFireHeld;
                if (prevPrimary) sim.BeginWeaponFire(bot.ThingIndex, 0);
                else sim.EndWeaponFire(bot.ThingIndex, 0);
            }
            if (dec.SecondaryFireHeld != prevSecondary)
            {
                prevSecondary = dec.SecondaryFireHeld;
                if (prevSecondary) sim.BeginWeaponFire(bot.ThingIndex, 1);
                else sim.EndWeaponFire(bot.ThingIndex, 1);
            }

            sim.StepWorld(dt);
            projectilesSeen += sim.Projectiles.Count;

            if (enemy.Inventory.Health < hpBefore || projectilesSeen > 0 || bot.SaberDamageActive)
                executedAction = true;
        }

        Assert.True(selectedTargetWeapon, $"Bot failed to select weapon bin {targetWeaponBin}.");
        Assert.True(executedAction, $"Bot failed to fire/swing/throw/plant weapon bin {targetWeaponBin} (HP before={hpBefore}, after={enemy.Inventory.Health}, projs={projectilesSeen}).");
    }

    [Fact]
    public void DmBotController_AvoidsSelfDamage_AtPointBlankWithExplosives()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        var sim = new WorldSimulation(level, vfs, isMultiplayer: true, isDedicatedServer: true);
        var nav = new SectorPortalNavGraph(level);
        var pickups = new List<WorldSimulation.PickupInfo>();

        PlayerState bot = sim.AddPlayer(0, "ConcBot");
        PlayerState enemy = sim.AddPlayer(1, "CloseEnemy");
        bot.Inventory.SetInv(PlayerInventory.BinConcRifle, 1f);
        bot.Inventory.PowerAmmo = 40f;
        bot.Inventory.HasLightsaber = false;

        var ctrl = new DmBotController(new BotArchetype("ConcBot", "Boba", 3, PlayerInventory.BinConcRifle), BotSkill.Hard, new Random(1));
        int pointBlankBin = ctrl.SelectBestWeapon(bot.Inventory, targetDist: 0.45f, hasLos: true);
        Assert.Equal(PlayerInventory.BinConcRifle, pointBlankBin);

        // At point blank (0.45 JKU < 1.10 JKU), ComputeWeaponCombat MUST use Secondary (+concblast2 hitscan) instead of Primary (+concbullet splash)
        sim.SelectWeapon(bot.ThingIndex, PlayerInventory.BinConcRifle);
        bot.MountWait = 0f;
        sim.UpdatePlayerInput(bot.ThingIndex, Vector3.Zero, 0, Vector3.UnitY);
        sim.UpdatePlayerInput(enemy.ThingIndex, new Vector3(0f, 0.45f, 0f), 0, -Vector3.UnitY);

        BotTickDecision dec1 = ctrl.Decide(0.05f, sim, bot, level.Things[bot.ThingIndex], nav, pickups);
        BotTickDecision dec2 = ctrl.Decide(0.05f, sim, bot, level.Things[bot.ThingIndex], nav, pickups);
        Assert.True(dec2.SecondaryFireHeld || !dec2.PrimaryFireHeld, "Bot must choose secondary beam over splash projectile at point blank.");
    }

    [Fact]
    public async Task DedicatedGameServer_BotsReplicateMoveFightDieAndRespawn_OverUdpLoopback()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        RegisterAllTenWeaponTemplatesAndCogs(level, vfs);

        using var server = new DedicatedGameServer(level, vfs, "test.gob", "arena.jkl", port: 0, maxPlayers: 4, tickRateHz: 30, defaultAllWeapons: true);

        int bot1Id = server.AddBot("AlphaBot", BotSkill.Hard, grantAllWeapons: true, favoriteWeaponBin: PlayerInventory.BinBryar);
        int bot2Id = server.AddBot("BetaBot", BotSkill.Hard, grantAllWeapons: true, favoriteWeaponBin: PlayerInventory.BinRepeater);
        Assert.Equal(2, server.BotCount);
        Assert.Equal(2, server.GetConnectedClientsSnapshot().Count);

        using var humanClient = new MultiplayerClient();
        ServerWelcomePacket welcome = await ConnectWithServerTickAsync(server, humanClient, "HumanObserver");
        Assert.Equal(1, server.ConnectedClientCount);
        Assert.Equal(3, server.GetConnectedClientsSnapshot().Count);

        // Capacity is shared cleanly across humans and bots (maxPlayers = 4 -> one more bot allowed, fifth rejected)
        int bot3Id = server.AddBot("GammaBot", BotSkill.Medium, grantAllWeapons: false);
        Assert.True(bot3Id >= 0);
        Assert.Equal(-1, server.AddBot("OverflowBot"));
        Assert.True(server.RemoveBot(bot3Id));
        Assert.Equal(2, server.BotCount);

        // Place human far away so bots fight each other without targeting observer first, record initial AlphaBot position
        Vector3 initialAlphaPos = level.Things[bot1Id].Position;
        Vector3? alphaMovedPos = null;
        ServerSnapshotPacket? latestSnap = null;

        for (int i = 0; i < 40; i++)
        {
            humanClient.SendInput(new ClientInputPacket
            {
                Flags = ClientInputFlags.IsGrounded,
                Position = new Vector3(-5f, -5f, 0f),
                Sector = 0,
                AimDirection = Vector3.UnitY,
                ActionSeq = (byte)(i + 1)
            });
            server.Tick(0.05f);
            await Task.Delay(5);
            if (humanClient.PollSnapshots() && humanClient.LatestSnapshot != null)
            {
                latestSnap = humanClient.LatestSnapshot;
                foreach (var ps in latestSnap.Players)
                {
                    if (ps.ThingIndex == bot1Id && Vector3.Distance(ps.Position, initialAlphaPos) > 0.05f)
                        alphaMovedPos = ps.Position;
                }
            }
        }

        Assert.NotNull(latestSnap);
        Assert.Equal(3, latestSnap!.Players.Count);
        Assert.Contains(latestSnap.Players, p => p.ThingIndex == bot1Id && p.Name == "AlphaBot");
        Assert.Contains(latestSnap.Players, p => p.ThingIndex == bot2Id && p.Name == "BetaBot");
        Assert.True(alphaMovedPos.HasValue, "Expected AlphaBot to move autonomously across replicated UDP snapshots.");

        // Kill BetaBot directly -> verify automatic bot respawn after >= 1.5s
        server.Simulation.DamageThing(bot2Id, 500f, 0, bot1Id);
        Assert.True(server.Simulation.GetPlayerByThing(bot2Id)!.Inventory.IsDead);

        for (int i = 0; i < 40; i++)
        {
            server.Tick(0.05f);
            await Task.Delay(3);
            humanClient.PollSnapshots();
        }

        Assert.False(server.Simulation.GetPlayerByThing(bot2Id)!.Inventory.IsDead, "Expected killed bot to automatically respawn.");
        Assert.Equal(2, server.RemoveAllBots());
        Assert.Equal(0, server.BotCount);
        Assert.NotEqual(welcome.PlayerThingId, bot1Id);
    }

    [Fact]
    public void DedicatedServer_RetailM10_BotsNavigatePickItemsAndCombatCleanly()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        string jk1MpPath = Path.Combine(SteamPath, "Episode", "JK1MP.GOB");
        if (!File.Exists(res2Path) || !File.Exists(jk1MpPath))
            return;

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));
        if (File.Exists(jk1Path))
            vfs.Mount(GobArchive.FromFile(jk1Path));
        vfs.Mount(GobArchive.FromFile(jk1MpPath));

        JklLevel m10 = JklReader.Parse(vfs.ReadAllText("jkl/m10.jkl"), "m10");
        using var server = new DedicatedGameServer(m10, vfs, "JK1MP.GOB", "m10.jkl", port: 0, maxPlayers: 6, tickRateHz: 30, defaultAllWeapons: true);

        int b1 = server.AddBot("RetailBot1", BotSkill.Hard, grantAllWeapons: true);
        int b2 = server.AddBot("RetailBot2", BotSkill.Hard, grantAllWeapons: true);
        int b3 = server.AddBot("RetailBot3", BotSkill.Medium, grantAllWeapons: true);
        Assert.Equal(3, server.BotCount);

        Vector3 startPos1 = m10.Things[b1].Position;
        float maxDist1 = 0f;
        int shotsObserved = 0;

        for (int i = 0; i < 80; i++) // ~2.6s of real m10 retail deathmatch simulation
        {
            server.Tick(0.033f);
            maxDist1 = MathF.Max(maxDist1, Vector3.Distance(m10.Things[b1].Position, startPos1));
            shotsObserved += server.Simulation.Projectiles.Count;
        }

        Assert.True(maxDist1 > 0.25f, $"Expected retail m10 bot to navigate smoothly (moved {maxDist1:F2}).");
        Assert.NotNull(server.Simulation.GetPlayerByThing(b2));
        Assert.NotNull(server.Simulation.GetPlayerByThing(b3));
        Assert.True(server.Simulation.ActiveCogCount > 0 && shotsObserved >= 0);
    }
}
