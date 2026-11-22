using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using OpenSith.Core.Audio;
using OpenSith.Core.Cog;
using OpenSith.Core.Data;
using OpenSith.Core.Materials;
using OpenSith.Core.Networking;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Phase 7 tests covering multi-cel MAT parsing, Concussion Rifle primary/secondary mechanics,
/// Force Lightning instant jagged beams, Bowcaster wall bounce, sticky Rail/Sequencer ordnance,
/// 28-model &amp; 7-saber-color &amp; 14-Force-power customization, and live UDP customization/EFX replication.
/// </summary>
public sealed class Phase7WeaponsCustomizationTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    private sealed class InMemoryVfs : IVirtualFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddText(string path, string content) =>
            _files[Normalize(path)] = Encoding.ASCII.GetBytes(content);

        public void AddBytes(string path, byte[] bytes) =>
            _files[Normalize(path)] = bytes;

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
        public List<(string SoundName, bool Looping)> PlayedLocal { get; } = [];
        public List<(string SoundName, Vector3 Position)> PlayedPos { get; } = [];
        public List<(int ChannelId, Vector3 Position)> PositionUpdates { get; } = [];
        public bool IsAvailable => true;
        public int PlaySoundLocal(string wavName, float volume = 1.0f, bool looping = false)
        {
            PlayedLocal.Add((wavName, looping));
            return _nextId++;
        }
        public int PlaySoundPos(string wavName, Vector3 position, float volume = 1.0f, float minRadius = 0.5f, float maxRadius = 8.0f, bool looping = false)
        {
            PlayedPos.Add((wavName, position));
            return _nextId++;
        }
        public void SetSoundPosition(int channelId, Vector3 position) => PositionUpdates.Add((channelId, position));
        public void StopSound(int channelId, float fadeOut = 0f) { }
        public void ChangeSoundPitch(int channelId, float pitch, float changeTime = 0f) { }
        public void ChangeSoundVol(int channelId, float volume, float fadeTime = 0f) { }
        public void SetMusicVolume(float volume) { }
        public void SetSectorAmbient(string? wavName, float volume = 0.5f) { }
        public float GetSoundDuration(string wavName) => 0.5f;
    }

    private static JklLevel CreateBoxArena(float halfExtent = 6.0f, float height = 3.0f)
    {
        var level = new JklLevel { Name = "phase7_arena" };
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
            Position = new Vector3(-2f, -2f, 0f),
            PitchYawRoll = new Vector3(0f, -90f, 0f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(2f, -2f, 0f),
            PitchYawRoll = new Vector3(0f, 90f, 0f)
        });

        return level;
    }

    private static byte[] BuildSyntheticMultiCelMat(int celCount, int width, int height, int mipCount, byte transIdx)
    {
        int totalTexBytes = celCount * 24;
        int mipSum = 0;
        for (int m = 0; m < mipCount; m++)
            mipSum += Math.Max(1, width >> m) * Math.Max(1, height >> m);
        totalTexBytes += celCount * mipSum;

        byte[] bytes = new byte[76 + (celCount * 40) + totalTexBytes];
        Encoding.ASCII.GetBytes("MAT ").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 0x32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), celCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), celCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), 8);

        int off = 76;
        for (int i = 0; i < celCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(off, 4), 8u);
            off += 24 + 16;
        }

        for (int c = 0; c < celCount; c++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(off + 0, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(off + 4, 4), height);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(off + 8, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(off + 16, 4), transIdx);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(off + 20, 4), mipCount);
            off += 24;

            for (int m = 0; m < mipCount; m++)
            {
                int mw = Math.Max(1, width >> m);
                int mh = Math.Max(1, height >> m);
                int len = mw * mh;
                byte fill = m == 0 ? (byte)(10 + c * 20) : (byte)255;
                Array.Fill(bytes, fill, off, len);
                if (m == 0)
                    bytes[off] = transIdx;
                off += len;
            }
        }

        return bytes;
    }

    private static CmpPalette BuildTestPalette()
    {
        byte[] cmp = new byte[64 + 768 + 64 * 256];
        Encoding.ASCII.GetBytes("CMP ").CopyTo(cmp, 0);
        BinaryPrimitives.WriteInt32LittleEndian(cmp.AsSpan(4, 4), 0x1E);
        for (int i = 0; i < 256; i++)
        {
            cmp[64 + i * 3 + 0] = (byte)i;
            cmp[64 + i * 3 + 1] = (byte)(255 - i);
            cmp[64 + i * 3 + 2] = (byte)((i * 3) & 0xFF);
        }
        int lmOff = 64 + 768;
        for (int l = 0; l < 64; l++)
            for (int i = 0; i < 256; i++)
                cmp[lmOff + l * 256 + i] = (byte)i;
        return CmpPalette.Parse(cmp);
    }

    [Fact]
    public void MatMaterial_ParsesAllCelsAndSkipsMipmaps_SyntheticAndRetail()
    {
        const byte transIdx = 7;
        byte[] synthetic = BuildSyntheticMultiCelMat(celCount: 4, width: 4, height: 4, mipCount: 2, transIdx: transIdx);
        MatMaterial mat = MatMaterial.Parse(synthetic);

        Assert.Equal(4, mat.CelCount);
        Assert.Equal(4, mat.Cels.Count);
        Assert.True(mat.AlphaEnabled);

        CmpPalette pal = BuildTestPalette();
        for (int c = 0; c < 4; c++)
        {
            Assert.Equal(4, mat.Cels[c].Width);
            Assert.Equal(4, mat.Cels[c].Height);
            Assert.Equal(transIdx, mat.Cels[c].IndexedPixels[0]);
            Assert.Equal((byte)(10 + c * 20), mat.Cels[c].IndexedPixels[1]);

            byte[] rgba = mat.ToRgbaByteBuffer(pal, lightLevel: 0, celIndex: c);
            Assert.Equal(0, rgba[3]); // transparent key pixel 0
            Assert.Equal((byte)(10 + c * 20), rgba[4]);
            Assert.Equal(255, rgba[7]);
        }

        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (!File.Exists(res2Path))
            return;

        using var gob = GobArchive.FromFile(res2Path);
        MatMaterial conx = MatMaterial.Parse(gob.ReadAllBytes("mat/conx.mat"));
        Assert.Equal(7, conx.CelCount);
        Assert.Equal(64, conx.Width);
        Assert.True(conx.AlphaEnabled);

        MatMaterial detx = MatMaterial.Parse(gob.ReadAllBytes("mat/detx.mat"));
        Assert.Equal(13, detx.CelCount);
    }

    [Fact]
    public void ConcussionRifle_PrimaryAndSecondary_ForceLightning_BowcasterBounce_AndStickyOrdnance()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("misc/snd/exp_conc.snd", "create expl_09.wav 0x0 1.0 5.0\n");

        var audio = new RecordingAudioSink();
        var level = CreateBoxArena(halfExtent: 6.0f);
        level.Templates["_actor"] = new JklTemplate
        {
            Name = "_actor",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mass"] = "150.0",
                ["health"] = "100"
            }
        };
        level.Templates["walkplayer"] = new JklTemplate { Name = "walkplayer", BasedOn = "_actor" };
        level.Templates["+conccloud"] = new JklTemplate
        {
            Name = "+conccloud",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "particle",
                ["timer"] = "0.12",
                ["material"] = "dusty.mat",
                ["count"] = "256",
                ["pitchrange"] = "5.0",
                ["elementsize"] = "0.012"
            }
        };
        level.Templates["+conc_exp"] = new JklTemplate
        {
            Name = "+conc_exp",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "explosion",
                ["sprite"] = "conx.spr",
                ["soundclass"] = "exp_conc.snd",
                ["creatething"] = "+conccloud",
                ["damage"] = "80.0",
                ["blasttime"] = "1.0",
                ["force"] = "200.0",
                ["range"] = "0.8"
            }
        };
        level.Templates["+concbullet"] = new JklTemplate
        {
            Name = "+concbullet",
            BasedOn = "none",
            Model3D = "con0.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vel"] = "(0/7/0)",
                ["explode"] = "+conc_exp",
                ["fleshhit"] = "+conc_exp",
                ["damage"] = "20.0",
                ["typeflags"] = "0x20000d",
                ["physflags"] = "0x200"
            }
        };
        level.Templates["+concblast3p"] = new JklTemplate
        {
            Name = "+concblast3p",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "particle",
                ["material"] = "00ramp4.mat",
                ["count"] = "64",
                ["pitchrange"] = "5.0"
            }
        };
        level.Templates["+concblast2"] = new JklTemplate
        {
            Name = "+concblast2",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["typeflags"] = "0xa00d",
                ["range"] = "5.0",
                ["damage"] = "80.0",
                ["mindamage"] = "20.0",
                ["rate"] = "1.0",
                ["elementsize"] = "0.30",
                ["fleshhit"] = "+concblast3p"
            }
        };
        level.Templates["+force_lightning"] = new JklTemplate
        {
            Name = "+force_lightning",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["typeflags"] = "0x1840d",
                ["range"] = "1.5",
                ["damage"] = "25.0",
                ["elementsize"] = "0.075",
                ["trailcylradius"] = "0.05"
            }
        };
        level.Templates["+crossbowbolt3"] = new JklTemplate
        {
            Name = "+crossbowbolt3",
            BasedOn = "none",
            Model3D = "bow0.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vel"] = "(0/5/0)",
                ["damage"] = "40.0",
                ["typeflags"] = "0x28440d",
                ["physflags"] = "0x200"
            }
        };
        level.Templates["+raildet2"] = new JklTemplate
        {
            Name = "+raildet2",
            BasedOn = "none",
            Model3D = "rld0.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vel"] = "(0/4/0)",
                ["timer"] = "0.4",
                ["damage"] = "5.0",
                ["explode"] = "+conc_exp",
                ["typeflags"] = "0x240b81",
                ["physflags"] = "0x200"
            }
        };

        var sim = new WorldSimulation(level, vfs, audio, isMultiplayer: true, isDedicatedServer: true);
        PlayerState p1 = sim.AddPlayer(0, "Kyle");
        PlayerState p2 = sim.AddPlayer(1, "Jan");
        p2.Inventory.Shields = 0f;
        p2.Inventory.Health = 100f;

        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, -1f, 0f), 0, Vector3.UnitY);
        sim.UpdatePlayerInput(p2.ThingIndex, new Vector3(0f, 1.0f, 0f), 0, -Vector3.UnitY);

        var spawnedVfx = new List<WorldSimulation.VisualEffect>();
        sim.OnVisualEffectSpawned += spawnedVfx.Add;

        // 1. Primary Concussion Rifle (+concbullet -> +conc_exp + +conccloud + exp_conc.snd) -> 20 + 80 = 100 lethal damage!
        sim.FireProjectile(p1.ThingIndex, "+concbullet", "concuss5.wav", 0, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        for (int i = 0; i < 15; i++)
            sim.StepWorld(0.05f);

        Assert.True(p2.Inventory.IsDead, "Direct +concbullet (20) + +conc_exp (80) should deal 100 lethal damage.");
        Assert.Contains(spawnedVfx, v => v.Kind == WorldSimulation.VisualEffectKind.Billboard && v.MaterialName == "conx.mat");
        Assert.Contains(spawnedVfx, v => v.Kind == WorldSimulation.VisualEffectKind.ParticleRing && v.SpriteName == "+conccloud");
        Assert.Contains(audio.PlayedPos, s => s.SoundName.Equals("expl_09.wav", StringComparison.OrdinalIgnoreCase));

        // 2. Secondary Concussion Rifle (+concblast2): instant hitscan BeamTrail + attenuated damage
        spawnedVfx.Clear();
        sim.RespawnPlayer(p2.ThingIndex);
        p2.Inventory.Shields = 0f;
        p2.Inventory.Health = 100f;
        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, -1f, 0f), 0, Vector3.UnitY);
        sim.UpdatePlayerInput(p2.ThingIndex, new Vector3(0f, 2.0f, 0f), 0, -Vector3.UnitY);

        sim.FireProjectile(p1.ThingIndex, "+concblast2", "concuss5.wav", 1, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        Assert.Empty(sim.Projectiles); // Hitscan: no flying projectile!
        Assert.Contains(spawnedVfx, v => v.Kind == WorldSimulation.VisualEffectKind.BeamTrail && v.JitterRadius <= 0.001f);
        Assert.Contains(spawnedVfx, v => v.Kind == WorldSimulation.VisualEffectKind.ParticleRing && v.SpriteName == "+concblast3p");
        Assert.InRange(p2.Inventory.Health, 20f, 26f); // ~80 - 1.0 * ~3m = ~77 damage

        // 3. Force Lightning (+force_lightning): instant jagged BeamTrail
        spawnedVfx.Clear();
        sim.UpdatePlayerInput(p2.ThingIndex, new Vector3(0f, 0f, 0f), 0, -Vector3.UnitY);
        float hpBeforeLit = p2.Inventory.Health;
        sim.FireProjectile(p1.ThingIndex, "+force_lightning", "-1", 0, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        Assert.Contains(spawnedVfx, v => v.Kind == WorldSimulation.VisualEffectKind.BeamTrail && v.JitterRadius > 0.001f);
        Assert.True(p2.Inventory.Health < hpBeforeLit);

        // 4. Bowcaster wall bounce (+crossbowbolt3)
        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, 5.8f, 0.5f), 0, Vector3.UnitY);
        sim.FireProjectile(p1.ThingIndex, "+crossbowbolt3", "-1", 0, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        Assert.Single(sim.Projectiles);
        sim.StepWorld(0.1f);
        Assert.Single(sim.Projectiles);
        Assert.True(sim.Projectiles[0].Velocity.Y < 0f, "Expected Bowcaster secondary bolt to bounce backward off wall.");

        // 5. Sticky Rail Detonator secondary (+raildet2) sticks to wall then detonates on timer
        spawnedVfx.Clear();
        sim.FireProjectile(p1.ThingIndex, "+raildet2", "-1", 1, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        sim.StepWorld(0.1f);
        var stickyProj = Assert.Single(sim.Projectiles, p => p.Sticky);
        Assert.True(stickyProj.IsStuck);
        Assert.Equal(Vector3.Zero, stickyProj.Velocity);
        for (int i = 0; i < 10; i++)
            sim.StepWorld(0.05f);
        Assert.DoesNotContain(sim.Projectiles, p => p.Sticky);
        Assert.Contains(spawnedVfx, v => v.Kind == WorldSimulation.VisualEffectKind.Billboard && v.MaterialName == "conx.mat");

        // 6. Sequencer Charge (+seqchrg) drops to floor, arms after 0.8s, and proximity-detonates when enemy approaches
        level.Templates["+seqchrg"] = new JklTemplate
        {
            Name = "+seqchrg",
            BasedOn = "none",
            Model3D = "seq0.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vel"] = "(0/0.1/0)",
                ["timer"] = "10.0",
                ["explode"] = "+conc_exp",
                ["typeflags"] = "0x40380",
                ["physflags"] = "0x29d"
            }
        };
        spawnedVfx.Clear();
        sim.RespawnPlayer(p2.ThingIndex);
        sim.UpdatePlayerInput(p2.ThingIndex, new Vector3(-4f, -4f, 0f), 0, Vector3.UnitY);
        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0f, 0f, 0.05f), 0, Vector3.UnitY);
        sim.FireProjectile(p1.ThingIndex, "+seqchrg", "-1", 0, Vector3.Zero, Vector3.Zero, 1f, 0, 0f, 0f);
        for (int i = 0; i < 25; i++)
            sim.StepWorld(0.05f);
        var planted = Assert.Single(sim.Projectiles, p => p.ProximityDetonate);
        Assert.True(planted.IsStuck);
        // Move p2 onto the armed sequencer charge -> triggers detonation
        sim.UpdatePlayerInput(p2.ThingIndex, planted.Position, 0, Vector3.UnitY);
        sim.StepWorld(0.05f);
        Assert.DoesNotContain(sim.Projectiles, p => p.ProximityDetonate);
        Assert.Contains(spawnedVfx, v => v.Kind == WorldSimulation.VisualEffectKind.ParticleRing && v.SpriteName == "+conccloud");
    }

    [Fact]
    public void CustomizationCatalog_ApplyAndRespawnPersistence_And_NetCodecV2()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("misc/models.dat", """
            numModels: 2
            0: ky.3do ky.snd # "Katarn"
            1: kyH4.3do ky.snd # "BobaFett"
            end
            """);
        vfs.AddText("misc/sabers.dat", """
            numSabers: 7
            0: sabB2.BM saberblue1.mat saberblue0.mat
            1: sabDR2.BM saberdred1.mat saberdred0.mat
            2: sabG2.BM sabergreen1.mat sabergreen0.mat
            3: sabO2.BM saberorange1.mat saberorange0.mat
            4: sabR2.BM saberred1.mat saberred0.mat
            5: sabV2.BM saberpurple1.mat saberpurple0.mat
            6: sabY2.BM saberyellow1.mat saberyellow0.mat
            end
            """);

        var models = PlayerCustomizationCatalog.LoadModels(vfs);
        var sabers = PlayerCustomizationCatalog.LoadSaberColors(vfs);
        Assert.Equal(2, models.Count);
        Assert.Equal(7, sabers.Count);
        Assert.True(PlayerCustomizationCatalog.TryResolveModelIndex(models, "boba", out int bobaIdx));
        Assert.Equal(1, bobaIdx);
        Assert.True(PlayerCustomizationCatalog.TryResolveSaberIndex(sabers, "purple", out int purpleIdx));
        Assert.Equal(5, purpleIdx);

        // Verify full 28 retail models if retail Steam assets exist
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (File.Exists(res2Path))
        {
            using var resVfs = new VirtualFileSystem();
            resVfs.Mount(GobArchive.FromFile(res2Path));
            var retailModels = PlayerCustomizationCatalog.LoadModels(resVfs);
            Assert.Equal(28, retailModels.Count);
        }

        // Verify NetCodec v2 packet round-trips
        Span<byte> buf = stackalloc byte[NetCodec.MaxPacketBytes];
        var cfg = new PlayerLoadoutConfig { ModelIndex = 1, SaberColorIndex = 5, JediRank = 7, GrantAllWeapons = true };
        cfg.ForceRanks[11] = 4; // Lightning rank 4
        ulong packed = NetCodec.PackForceRanks(cfg.ForceRanks);

        var hello = new ClientHelloPacket("Boba", 1, 1, 5, 7, NetLoadoutFlags.GrantAllWeapons, packed);
        Assert.True(NetCodec.TryWriteClientHello(buf, hello, out int hLen));
        Assert.True(NetCodec.TryReadClientHello(buf[..hLen], out var helloOut));
        Assert.Equal(hello, helloOut);

        var cc = new ClientConfigPacket(9, 1, 4, 8, NetLoadoutFlags.GrantAllWeapons, packed);
        Assert.True(NetCodec.TryWriteClientConfig(buf, cc, out int ccLen));
        Assert.True(NetCodec.TryReadClientConfig(buf[..ccLen], out var ccOut));
        Assert.Equal(cc, ccOut);

        var sim = new WorldSimulation(CreateBoxArena(), vfs, isMultiplayer: true, isDedicatedServer: true);
        PlayerState p = sim.AddPlayer(0, "Boba");
        sim.ApplyPlayerCustomization(p.ThingIndex, cfg);

        Assert.Equal("kyH4.3do", sim.GetPlayerByThing(p.ThingIndex)!.ModelIndex == 1 ? sim.ModelCatalog[1].Model3D : string.Empty);
        Assert.True(sim.TryGetThingSaberInfo(p.ThingIndex, out var saberInfo));
        Assert.Equal("saberpurple1.mat", saberInfo.SideMat);
        Assert.Equal(7, p.Inventory.ForceRank);
        Assert.Equal(350f, p.Inventory.ForceMana);
        Assert.Equal(4f, p.Inventory.GetInv(PlayerInventory.BinForceLightning));
        Assert.Equal(1f, p.Inventory.GetInv(PlayerInventory.BinConcRifle));

        sim.RespawnPlayer(p.ThingIndex);
        Assert.True(sim.TryGetThingSaberInfo(p.ThingIndex, out var saberAfterRespawn));
        Assert.Equal("saberpurple1.mat", saberAfterRespawn.SideMat);
        Assert.Equal(4f, p.Inventory.GetInv(PlayerInventory.BinForceLightning));
        Assert.Equal(1f, p.Inventory.GetInv(PlayerInventory.BinConcRifle));
    }

    [Fact]
    public async Task DedicatedServer_LiveCustomizationAndWeaponEfx_ReplicateBetweenTwoUdpClients()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("misc/models.dat", """
            numModels: 3
            0: ky.3do ky.snd # "Katarn"
            1: kyH4.3do ky.snd # "BobaFett"
            2: kyA10.3do ky.snd # "JanOrs"
            end
            """);

        var arena = CreateBoxArena(halfExtent: 6.0f);
        arena.Templates["+conccloud"] = new JklTemplate
        {
            Name = "+conccloud",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "particle",
                ["timer"] = "0.12",
                ["material"] = "dusty.mat",
                ["count"] = "64",
                ["pitchrange"] = "5.0"
            }
        };
        arena.Templates["+conc_exp"] = new JklTemplate
        {
            Name = "+conc_exp",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "explosion",
                ["sprite"] = "conx.spr",
                ["creatething"] = "+conccloud",
                ["damage"] = "60.0",
                ["blasttime"] = "0.5",
                ["range"] = "0.8"
            }
        };
        arena.Templates["+concblast2"] = new JklTemplate
        {
            Name = "+concblast2",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["typeflags"] = "0xa00d",
                ["range"] = "5.0",
                ["damage"] = "60.0",
                ["mindamage"] = "20.0",
                ["rate"] = "1.0",
                ["elementsize"] = "0.30",
                ["explode"] = "+conc_exp"
            }
        };
        vfs.AddText("cog/weap_concrifle.cog", """
            symbols
            thing player local
            int mode local
            message activated
            message fire
            message deactivated
            end
            code
            activated:
                player = GetSourceRef();
                ActivateWeapon(player, 0.2, GetSenderRef());
                Return;
            fire:
                player = GetSourceRef();
                FireProjectile(player, "+concblast2", "-1", GetSenderRef(), '0 0 0', '0 0 0', 1.0, 0, 0.0, 0.0);
                Return;
            deactivated:
                player = GetSourceRef();
                DeactivateWeapon(player, GetSenderRef());
                Return;
            end
            """);

        using var server = new DedicatedGameServer(arena, vfs, "JK1MP.GOB", "arena.jkl", port: 0, maxPlayers: 4, tickRateHz: 60);
        using var c1 = new MultiplayerClient();
        using var c2 = new MultiplayerClient();

        var loadout1 = new PlayerLoadoutConfig { ModelIndex = 1, SaberColorIndex = 4, JediRank = 6, GrantAllWeapons = true };
        loadout1.ApplyPreset(ForcePreset.DarkJedi);
        loadout1.ModelIndex = 1;
        loadout1.SaberColorIndex = 4; // Red
        loadout1.GrantAllWeapons = true;

        var loadout2 = new PlayerLoadoutConfig { ModelIndex = 2, SaberColorIndex = 5, JediRank = 6 };
        loadout2.ApplyPreset(ForcePreset.LightJedi);
        loadout2.ModelIndex = 2;
        loadout2.SaberColorIndex = 5; // Purple

        async Task<ServerWelcomePacket> ConnectAsync(MultiplayerClient c, string name, PlayerLoadoutConfig cfg)
        {
            var task = c.ConnectAsync("127.0.0.1", server.BoundPort, name, 0, TimeSpan.FromSeconds(5), initialLoadout: cfg);
            while (!task.IsCompleted)
            {
                server.Tick(0.01f);
                await Task.Delay(5);
            }
            return await task;
        }

        await ConnectAsync(c1, "Boba", loadout1);
        await ConnectAsync(c2, "Jan", loadout2);

        for (int i = 0; i < 6; i++)
        {
            server.Tick(1f / 60f);
            await Task.Delay(5);
            c1.PollSnapshots();
            c2.PollSnapshots();
        }

        Assert.NotNull(c2.LatestSnapshot);
        var p1FromC2 = Assert.Single(c2.LatestSnapshot!.Players, p => p.Name == "Boba");
        Assert.Equal((byte)1, p1FromC2.ModelIndex);
        Assert.Equal((byte)4, p1FromC2.SaberColorIndex);

        // Live skin & saber change via ClientConfig
        var updated1 = loadout1.Clone();
        updated1.ModelIndex = 2;
        updated1.SaberColorIndex = 6; // Yellow
        c1.SendConfig(updated1);

        // Fire Concussion Rifle secondary (+concblast2) and verify BeamTrail event replicates to Client 2
        c1.SendInput(new ClientInputPacket
        {
            Position = new Vector3(-2f, -2f, 0f),
            Sector = 0,
            AimDirection = Vector3.UnitX,
            Yaw = -90f,
            ActionSeq = 2,
            RequestedWeaponBin = (byte)PlayerInventory.BinConcRifle
        });

        var c2Events = new List<NetWorldEvent>();
        for (int i = 0; i < 6; i++)
        {
            c1.SendInput(new ClientInputPacket
            {
                Flags = ClientInputFlags.SecondaryFireHeld | ClientInputFlags.IsGrounded,
                Position = new Vector3(-2f, -2f, 0f),
                Sector = 0,
                AimDirection = Vector3.UnitX,
                Yaw = -90f,
                ActionSeq = 2
            });
            server.Tick(1f / 60f);
            await Task.Delay(5);
            c1.PollSnapshots();
            c2.PollSnapshots(c2Events);
        }

        var updatedP1 = Assert.Single(c2.LatestSnapshot!.Players, p => p.Name == "Boba");
        Assert.Equal((byte)2, updatedP1.ModelIndex);
        Assert.Equal((byte)6, updatedP1.SaberColorIndex);
        Assert.Contains(c2Events, e => e.Kind == NetEventKind.BeamTrail);
        Assert.Contains(c2Events, e => e.Kind == NetEventKind.ExplosionVfx && e.Text1 == "conx.mat");
        Assert.Contains(c2Events, e => e.Kind == NetEventKind.ParticleRing && e.Text0 == "+conccloud");
    }

    [Fact]
    public async Task LiveClientConfig_DoesNotRefillDepletedVitalsOrMana_AndGrantsAllWeaponsAmmoAtMostOncePerLife()
    {
        var vfs = new InMemoryVfs();
        var arena = CreateBoxArena(halfExtent: 6.0f);

        using var server = new DedicatedGameServer(arena, vfs, "JK1MP.GOB", "arena.jkl", port: 0, maxPlayers: 4, tickRateHz: 60);
        using var c1 = new MultiplayerClient();

        var initial = new PlayerLoadoutConfig
        {
            ModelIndex = 0,
            SaberColorIndex = 2,
            JediRank = 6,
            GrantAllWeapons = false
        };
        initial.ForceRanks[11] = 4; // Force Lightning (bin 32)

        var connectTask = c1.ConnectAsync("127.0.0.1", server.BoundPort, "Kyle", 0, TimeSpan.FromSeconds(5), initialLoadout: initial);
        while (!connectTask.IsCompleted)
        {
            server.Tick(0.01f);
            await Task.Delay(5);
        }
        await connectTask;

        var clientInfo = Assert.Single(server.GetConnectedClientsSnapshot());
        PlayerState p1 = server.Simulation.GetPlayerByThing(clientInfo.PlayerThingId)!;

        // Deplete mana, shields, battery, and energy ammo after initial spawn
        p1.Inventory.ForceMana = 25f;
        p1.Inventory.Shields = 10f;
        p1.Inventory.Battery = 5f;
        p1.Inventory.EnergyAmmo = 15f;

        // Reduce JediRank live without GrantAllWeapons -> must NOT refill mana, shields, battery, or energy ammo
        var reduced = initial.Clone();
        reduced.JediRank = 3;
        c1.SendConfig(reduced);
        for (int i = 0; i < 4; i++)
        {
            server.Tick(1f / 60f);
            await Task.Delay(5);
            c1.PollSnapshots();
        }

        Assert.Equal(3, p1.Inventory.ForceRank);
        Assert.InRange(p1.Inventory.ForceMana, 25f, 26f);
        Assert.Equal(10f, p1.Inventory.Shields);
        Assert.Equal(5f, p1.Inventory.Battery);
        Assert.Equal(15f, p1.Inventory.EnergyAmmo);

        // Enable All Weapons once mid-life -> grants weapons and starter ammo once, WITHOUT touching shields/battery/mana
        var grantOnce = reduced.Clone();
        grantOnce.GrantAllWeapons = true;
        c1.SendConfig(grantOnce);
        for (int i = 0; i < 4; i++)
        {
            server.Tick(1f / 60f);
            await Task.Delay(5);
            c1.PollSnapshots();
        }

        Assert.Equal(1f, p1.Inventory.GetInv(PlayerInventory.BinConcRifle));
        Assert.Equal(500f, p1.Inventory.EnergyAmmo);
        Assert.Equal(10f, p1.Inventory.Shields);
        Assert.Equal(5f, p1.Inventory.Battery);
        Assert.InRange(p1.Inventory.ForceMana, 25f, 26f);

        // Drain energy ammo and toggle GrantAllWeapons off and back on in the same life -> NO second ammo refill
        p1.Inventory.EnergyAmmo = 40f;
        var toggleOff = grantOnce.Clone();
        toggleOff.GrantAllWeapons = false;
        c1.SendConfig(toggleOff);
        for (int i = 0; i < 3; i++)
        {
            server.Tick(1f / 60f);
            await Task.Delay(5);
        }

        c1.SendConfig(grantOnce);
        for (int i = 0; i < 4; i++)
        {
            server.Tick(1f / 60f);
            await Task.Delay(5);
            c1.PollSnapshots();
        }

        Assert.Equal(40f, p1.Inventory.EnergyAmmo);

        // Policy clamp zeroes all ForceRanks when JediRank is clamped to 0
        var clamped = grantOnce.Clone();
        clamped.ClampToPolicy(maxRank: 0, allowAllWeapons: false, modelCount: 1, saberCount: 7);
        Assert.Equal(0, clamped.JediRank);
        Assert.False(clamped.GrantAllWeapons);
        Assert.All(clamped.ForceRanks, r => Assert.Equal((byte)0, r));
    }

    [Fact]
    public async Task InvalidInventoryAndWeaponBins_AreRejectedByWorldSimulationAndDedicatedServer()
    {
        var vfs = new InMemoryVfs();
        var arena = CreateBoxArena(halfExtent: 6.0f);

        // Direct WorldSimulation check
        var sim = new WorldSimulation(arena, vfs, isMultiplayer: true, isDedicatedServer: true);
        PlayerState p = sim.AddPlayer(0, "Kyle");
        p.Inventory.SetInv(PlayerInventory.BinForceLightning, 4f);
        p.Inventory.SetInvAvailable(PlayerInventory.BinForceLightning, true);
        int selectedBefore = p.CurWeaponBin;

        Assert.False(sim.UseItem(p.ThingIndex, PlayerInventory.BinForceLightning));
        Assert.Equal(4f, p.Inventory.GetInv(PlayerInventory.BinForceLightning));

        sim.SelectWeapon(p.ThingIndex, PlayerInventory.BinForceLightning);
        Assert.Equal(selectedBefore, p.CurWeaponBin);

        int[] invalidForceBins =
        [
            PlayerInventory.BinBryar,
            PlayerInventory.BinForceRank,
            PlayerInventory.BinBacta,
            PlayerInventory.BinShields
        ];
        p.Inventory.SetInv(PlayerInventory.BinBryar, 1f);
        p.Inventory.SetInv(PlayerInventory.BinForceRank, 6f);
        p.Inventory.SetInv(PlayerInventory.BinBacta, 1f);
        p.Inventory.SetInv(PlayerInventory.BinShields, 50f);
        int selectedForceBefore = p.SelectedForceBin;
        foreach (int invalidForceBin in invalidForceBins)
        {
            Assert.False(sim.BeginForcePower(p.ThingIndex, invalidForceBin, Vector3.UnitX));
            Assert.False(sim.EndForcePower(p.ThingIndex, invalidForceBin));
            Assert.Equal(selectedForceBefore, p.SelectedForceBin);
        }

        // DedicatedGameServer UDP ClientInputPacket check
        using var server = new DedicatedGameServer(arena, vfs, "JK1MP.GOB", "arena.jkl", port: 0, maxPlayers: 4, tickRateHz: 60);
        using var c1 = new MultiplayerClient();

        var loadout = new PlayerLoadoutConfig { JediRank = 6, GrantAllWeapons = false };
        loadout.ForceRanks[11] = 4; // bin 32

        var connectTask = c1.ConnectAsync("127.0.0.1", server.BoundPort, "Kyle", 0, TimeSpan.FromSeconds(5), initialLoadout: loadout);
        while (!connectTask.IsCompleted)
        {
            server.Tick(0.01f);
            await Task.Delay(5);
        }
        await connectTask;

        var clientInfo = Assert.Single(server.GetConnectedClientsSnapshot());
        PlayerState serverPlayer = server.Simulation.GetPlayerByThing(clientInfo.PlayerThingId)!;
        int serverWeaponBefore = serverPlayer.CurWeaponBin;
        int serverForceBefore = serverPlayer.SelectedForceBin;
        serverPlayer.Inventory.SetInv(PlayerInventory.BinBacta, 2f);
        serverPlayer.Inventory.SetInv(PlayerInventory.BinShields, 50f);

        c1.SendInput(new ClientInputPacket
        {
            Position = new Vector3(-2f, -2f, 0f),
            Sector = 0,
            AimDirection = Vector3.UnitX,
            Yaw = -90f,
            ActionSeq = 9,
            RequestedWeaponBin = (byte)PlayerInventory.BinForceLightning,
            RequestedItemBin = (byte)PlayerInventory.BinForceLightning
        });

        for (int i = 0; i < 4; i++)
        {
            server.Tick(1f / 60f);
            await Task.Delay(5);
            c1.PollSnapshots();
        }

        Assert.Equal(serverWeaponBefore, serverPlayer.CurWeaponBin);
        Assert.Equal(4f, serverPlayer.Inventory.GetInv(PlayerInventory.BinForceLightning));

        byte actionSeq = 10;
        foreach (int invalidForceBin in invalidForceBins)
        {
            c1.SendInput(new ClientInputPacket
            {
                Position = new Vector3(-2f, -2f, 0f),
                Sector = 0,
                AimDirection = Vector3.UnitX,
                Yaw = -90f,
                ActionSeq = actionSeq++,
                ActiveForceBin = (byte)invalidForceBin
            });

            for (int i = 0; i < 3; i++)
            {
                server.Tick(1f / 60f);
                await Task.Delay(5);
                c1.PollSnapshots();
            }

            Assert.Equal(serverForceBefore, serverPlayer.SelectedForceBin);
        }
    }

    [Fact]
    public void MatMaterialParse_RejectsExcessiveMipmapCount_AndHandlesTruncatedHugeTextureCountWithoutOom()
    {
        byte[] excessMips = BuildSyntheticMultiCelMat(celCount: 1, width: 1, height: 1, mipCount: 33, transIdx: 0);
        Assert.Throws<InvalidDataException>(() => MatMaterial.Parse(excessMips));

        byte[] hugeTexCount = BuildSyntheticMultiCelMat(celCount: 1, width: 4, height: 4, mipCount: 1, transIdx: 0);
        BinaryPrimitives.WriteInt32LittleEndian(hugeTexCount.AsSpan(16, 4), int.MaxValue);

        MatMaterial parsed = MatMaterial.Parse(hugeTexCount);
        Assert.Equal(1, parsed.CelCount);
    }

    [Fact]
    public async Task Repeater_LoopingAudioFireCadenceAndDamageDecay_WorkInMultiplayerAndSimulation()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/weap_repeater.cog", """
            symbols
            sound fireSound=rpeatrlp.wav local
            sound fireSound1=repeat-1.wav local
            template projectile=+repeaterball local
            thing player local
            flex fireWait=0.1 local
            flex powerBoost local
            int mode local
            int fireChannel=-1 local
            int dummy local
            message activated
            message fire
            message deactivated
            message killed
            end
            code
            activated:
                mode = GetSenderRef();
                if (mode == 0)
                {
                    if (fireChannel == -1)
                        fireChannel = PlaySoundThing(fireSound, player, 1.0, -1, -1, 0x181);
                    fireWait = 0.1;
                }
                else if (mode == 1)
                {
                    fireWait = 0.3;
                }
                powerBoost = GetInv(player, 63);
                ActivateWeapon(player, fireWait / powerBoost, mode);
                Return;
            fire:
                mode = GetSenderRef();
                if (mode == 0)
                {
                    dummy = FireProjectile(player, projectile, -1, 8, '0 0 0', '0 0 0', 1.0, 0, 0.0, 0.0);
                    ChangeInv(player, 12, -1.0);
                }
                else
                {
                    dummy = FireProjectile(player, projectile, fireSound1, 8, '0 0 0', '0 0 0', 1.0, 0, 0.0, 0.0);
                    ChangeInv(player, 12, -3.0);
                }
                powerBoost = GetInv(player, 63);
                ChangeFireRate(player, fireWait / powerBoost);
                Return;
            deactivated:
                mode = GetSenderRef();
                if (fireChannel != -1)
                {
                    StopSound(fireChannel, 0.1);
                    fireChannel = -1;
                    PlaySoundThing(fireSound1, player, 1.0, -1, -1, 0x180);
                }
                DeactivateWeapon(player, mode);
                Return;
            killed:
                if (fireChannel != -1)
                {
                    StopSound(fireChannel, 0.1);
                    fireChannel = -1;
                }
                Return;
            end
            """);

        var arena = CreateBoxArena(halfExtent: 12.0f);
        arena.Templates["+repeaterball"] = new JklTemplate
        {
            Name = "+repeaterball",
            BasedOn = "none",
            Model3D = "rpt0.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vel"] = "(0/6/0)",
                ["timer"] = "2.0",
                ["damage"] = "20.0",
                ["mindamage"] = "5.0",
                ["rate"] = "20.0",
                ["typeflags"] = "0x440d",
                ["physflags"] = "0x200"
            }
        };

        // 1. Verify projectile rate / mindamage decay and mode-switch loop cleanup in direct WorldSimulation
        var directSim = new WorldSimulation(arena, vfs, isMultiplayer: true, isDedicatedServer: true);
        int startedLoops = 0;
        int stoppedLoops = 0;
        directSim.OnLoopingSoundStarted += (_, _, _, _, _) => startedLoops++;
        directSim.OnLoopingSoundStopped += (_, _) => stoppedLoops++;

        PlayerState dp = directSim.AddPlayer(0, "Kyle");
        dp.Inventory.SetInv(PlayerInventory.BinRepeater, 1f);
        dp.Inventory.PowerAmmo = 500f;
        directSim.SelectWeapon(dp.ThingIndex, PlayerInventory.BinRepeater);

        Assert.True(directSim.BeginWeaponFire(dp.ThingIndex, 0));
        Assert.Equal(1, startedLoops);
        var bolt = Assert.Single(directSim.Projectiles);
        Assert.Equal(20f, bolt.Damage);

        // Switching primary -> secondary must cleanly stop primary's looping channel first
        Assert.True(directSim.BeginWeaponFire(dp.ThingIndex, 1));
        Assert.Equal(1, stoppedLoops);
        Assert.True(directSim.EndWeaponFire(dp.ThingIndex, 1));

        for (int i = 0; i < 10; i++)
            directSim.StepWorld(0.05f); // +0.5s at rate=20/s -> 20 - 10 = 10 dmg
        Assert.InRange(bolt.Damage, 9.9f, 10.1f);

        for (int i = 0; i < 10; i++)
            directSim.StepWorld(0.05f); // +0.5s more -> clamped at mindamage = 5
        Assert.Equal(5f, bolt.Damage);

        // 2. Verify 30 Hz DedicatedGameServer fire cadence (exact 10 Hz, no same-tick double-step) + loop replication
        using var server = new DedicatedGameServer(arena, vfs, "JK1MP.GOB", "arena.jkl", port: 0, maxPlayers: 4, tickRateHz: 30, allowClientAllWeapons: true, defaultAllWeapons: true);
        using var c1 = new MultiplayerClient();
        using var c2 = new MultiplayerClient();

        var loadout = new PlayerLoadoutConfig { GrantAllWeapons = true };
        async Task ConnectAsync(MultiplayerClient c, string name)
        {
            var task = c.ConnectAsync("127.0.0.1", server.BoundPort, name, 0, TimeSpan.FromSeconds(5), initialLoadout: loadout);
            while (!task.IsCompleted)
            {
                server.Tick(1f / 30f);
                await Task.Delay(5);
            }
            await task;
        }

        await ConnectAsync(c1, "Kyle");
        await ConnectAsync(c2, "Jan");

        var c1Info = Assert.Single(server.GetConnectedClientsSnapshot(), c => c.Name == "Kyle");
        PlayerState mpKyle = server.Simulation.GetPlayerByThing(c1Info.PlayerThingId)!;
        mpKyle.Inventory.PowerAmmo = 500f;

        c1.SendInput(new ClientInputPacket
        {
            Position = new Vector3(-2f, -2f, 0f),
            Sector = 0,
            AimDirection = Vector3.UnitY,
            Yaw = 0f,
            ActionSeq = 2,
            RequestedWeaponBin = (byte)PlayerInventory.BinRepeater
        });
        server.Tick(1f / 30f);
        await Task.Delay(5);

        var c2Events = new List<NetWorldEvent>();
        const float dt30 = 1f / 30f;
        for (int tick = 0; tick < 10; tick++)
        {
            c1.SendInput(new ClientInputPacket
            {
                Flags = ClientInputFlags.PrimaryFireHeld | ClientInputFlags.IsGrounded,
                Position = new Vector3(-2f, -2f, 0f),
                Sector = 0,
                AimDirection = Vector3.UnitY,
                Yaw = 0f,
                ActionSeq = 2
            });
            server.Tick(dt30);
            await Task.Delay(5);
            c1.PollSnapshots();
            c2.PollSnapshots(c2Events);
        }

        // Across 10 ticks at 30 Hz (0.333s total elapsed after start), shots fire at ticks 0, 3, 6, 9 => 4 shots total (not 5 at 15 Hz)
        Assert.Equal(496f, mpKyle.Inventory.PowerAmmo);

        var loopStart = Assert.Single(c2Events, e => e.Kind == NetEventKind.LoopSoundStart);
        Assert.Equal("rpeatrlp.wav", loopStart.Text0);
        Assert.True(loopStart.IntParam0 > 0);
        Assert.Equal(c1Info.PlayerThingId, (int)MathF.Round(loopStart.FloatParam1));

        // Release fire -> LoopSoundStop matching channel + release repeat-1.wav
        c2Events.Clear();
        for (int i = 0; i < 4; i++)
        {
            c1.SendInput(new ClientInputPacket
            {
                Flags = ClientInputFlags.IsGrounded,
                Position = new Vector3(-2f, -2f, 0f),
                Sector = 0,
                AimDirection = Vector3.UnitY,
                Yaw = 0f,
                ActionSeq = 2
            });
            server.Tick(dt30);
            await Task.Delay(5);
            c2.PollSnapshots(c2Events);
        }

        var loopStop = Assert.Single(c2Events, e => e.Kind == NetEventKind.LoopSoundStop);
        Assert.Equal(loopStart.IntParam0, loopStop.IntParam0);
        Assert.Contains(c2Events, e => e.Kind == NetEventKind.SoundPos && e.Text0 == "repeat-1.wav");
    }

    [Fact]
    public void ThirdPersonWeaponModels_MapAllArmedBinsAndExistInRetailGob()
    {
        Assert.Null(PlayerCustomizationCatalog.GetThirdPersonWeaponModel(PlayerInventory.BinFists));
        (int Bin, string ExpectedModel)[] expected =
        [
            (PlayerInventory.BinBryar, "bryg.3do"),
            (PlayerInventory.BinStRifle, "strg.3do"),
            (PlayerInventory.BinThermDet, "detg.3do"),
            (PlayerInventory.BinBowcaster, "bowg.3do"),
            (PlayerInventory.BinRepeater, "rptg.3do"),
            (PlayerInventory.BinRailDet, "rldg.3do"),
            (PlayerInventory.BinSeqCharge, "seqg.3do"),
            (PlayerInventory.BinConcRifle, "cong.3do"),
            (PlayerInventory.BinLightsaber, "sabg.3do"),
        ];

        foreach (var (bin, model) in expected)
            Assert.Equal(model, PlayerCustomizationCatalog.GetThirdPersonWeaponModel(bin));

        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        if (File.Exists(res2Path))
        {
            using var resVfs = new VirtualFileSystem();
            resVfs.Mount(GobArchive.FromFile(res2Path));
            foreach (var (_, model) in expected)
                Assert.True(resVfs.FileExists($"3do/{model}"), $"Expected retail mesh 3do/{model} in Res2.gob.");
        }
    }

    [Fact]
    public void NetCodec_RoundTrips_LoopSoundEvents()
    {
        var snap = new ServerSnapshotPacket
        {
            ServerTick = 42,
            LevelTime = 1.4f,
            LocalThingIndex = 0
        };
        snap.Events.Add(new NetWorldEvent(10, NetEventKind.LoopSoundStart, new Vector3(1f, 2f, 3f), 1.0f, 5f, 77, "rpeatrlp.wav", string.Empty));
        snap.Events.Add(new NetWorldEvent(11, NetEventKind.LoopSoundStop, Vector3.Zero, 0.1f, 0f, 77, string.Empty, string.Empty));

        Span<byte> buf = stackalloc byte[NetCodec.MaxPacketBytes];
        Assert.True(NetCodec.TryWriteServerSnapshot(buf, snap, out int written));
        Assert.True(NetCodec.TryReadServerSnapshot(buf[..written], out var decoded));
        Assert.Equal(2, decoded.Events.Count);
        Assert.Equal(NetEventKind.LoopSoundStart, decoded.Events[0].Kind);
        Assert.Equal("rpeatrlp.wav", decoded.Events[0].Text0);
        Assert.Equal(77, decoded.Events[0].IntParam0);
        Assert.Equal(5f, decoded.Events[0].FloatParam1);
        Assert.Equal(NetEventKind.LoopSoundStop, decoded.Events[1].Kind);
        Assert.Equal(77, decoded.Events[1].IntParam0);
    }

    [Fact]
    public void SinglePlayerLocalLoop_DoesNotMoveRelativeAudioSource_AndWeaponSwapClearsWeaponMesh()
    {
        var level = CreateBoxArena();
        var vfs = new InMemoryVfs();
        var audio = new RecordingAudioSink();
        var sim = new WorldSimulation(level, vfs, audio, isMultiplayer: false, isDedicatedServer: false);

        int localThing = sim.GetLocalPlayerThing();
        int otherThing = localThing == 0 ? 1 : 0;

        int localCh = sim.PlaySoundThing("rpeatrlp.wav", localThing, 1.0f, -1f, -1f, 0x181);
        int otherCh = sim.PlaySoundThing("rpeatrlp.wav", otherThing, 1.0f, -1f, -1f, 0x181);

        Assert.Contains(("rpeatrlp.wav", true), audio.PlayedLocal);

        sim.StepWorld(0.05f);

        Assert.DoesNotContain(audio.PositionUpdates, u => u.ChannelId == localCh);
        Assert.Contains(audio.PositionUpdates, u => u.ChannelId == otherCh);

        sim.JkSetWeaponMesh(localThing, "rptg.3do");
        Assert.Equal("rptg.3do", sim.JkGetWeaponMesh(localThing));

        sim.SetInv(localThing, PlayerInventory.BinFists, 1.0f);
        sim.SelectWeapon(localThing, PlayerInventory.BinFists);
        Assert.Equal(string.Empty, sim.JkGetWeaponMesh(localThing));
    }
}
