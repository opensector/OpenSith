using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using OpenSith.Core.Cog;
using OpenSith.Core.Networking;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Phase 6 integration tests covering the pure managed UDP binary protocol (<see cref="NetCodec"/>),
/// <see cref="DedicatedGameServer"/>, <see cref="MultiplayerClient"/>, multi-client loopback combat/doors/respawn,
/// stale pre-respawn packet rejection, timeouts, and retail <c>JK1MP.GOB</c> (<c>m2.jkl</c>) hosting.
/// </summary>
public sealed class Phase6DedicatedServerTests
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

    private static JklLevel CreateMultiSpawnBoxArena(float halfExtent = 4.0f, float height = 3.0f)
    {
        var level = new JklLevel { Name = "phase6_mp_arena" };
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
            Position = new Vector3(-2f, 0f, 0f),
            PitchYawRoll = new Vector3(0f, -90f, 0f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 1,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(2f, 0f, 0f),
            PitchYawRoll = new Vector3(0f, 90f, 0f)
        });
        level.Things.Add(new JklThing
        {
            ThingIndex = 2,
            TemplateName = "walkplayer",
            SectorIndex = 0,
            Position = new Vector3(0f, 2.5f, 0f),
            PitchYawRoll = new Vector3(0f, 180f, 0f)
        });

        level.Things.Add(new JklThing
        {
            ThingIndex = 3,
            TemplateName = "testdoor",
            SectorIndex = 0,
            Position = new Vector3(0f, 2.0f, 0f),
            PitchYawRoll = Vector3.Zero,
            Size = 0.25f,
            ThingFlags = (uint)SithThingFlags.Captured,
            ExtraArguments = "frame=(0/2/0:0/0/0) frame=(0/2/1.2:0/0/0)"
        });
        level.Cogs.Add(new JklCogPlacement
        {
            Index = 0,
            ScriptName = "test_door.cog",
            SymbolArgs = ["3"]
        });

        level.Templates["+testbolt"] = new JklTemplate
        {
            Name = "+testbolt",
            BasedOn = "none",
            Model3D = "bry0.3do",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vel"] = "(0.0/16.0/0.0)",
                ["damage"] = "250.0",
                ["timer"] = "2.0",
                ["physflags"] = "0x200",
                ["typeflags"] = "0x40d",
                ["explode"] = "+testexp"
            }
        };
        level.Templates["+testexp"] = new JklTemplate
        {
            Name = "+testexp",
            BasedOn = "none",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "explosion",
                ["timer"] = "0.5",
                ["blasttime"] = "0.5",
                ["sprite"] = "detx.spr",
                ["damage"] = "0",
                ["range"] = "0"
            }
        };

        return level;
    }

    private static async Task PumpServerAndClientsAsync(
        DedicatedGameServer server,
        float dt,
        int ticks,
        params MultiplayerClient[] clients)
    {
        for (int i = 0; i < ticks; i++)
        {
            server.Tick(dt);
            await Task.Delay(5);
            foreach (var c in clients)
                c.PollSnapshots();
        }
    }

    private static async Task<ServerWelcomePacket> ConnectWithServerTickAsync(
        DedicatedGameServer server,
        MultiplayerClient client,
        string name,
        int team = 0,
        TimeSpan? timeout = null)
    {
        Task<ServerWelcomePacket> connectTask = client.ConnectAsync("127.0.0.1", server.BoundPort, name, team, timeout);
        while (!connectTask.IsCompleted)
        {
            server.Tick(0.01f);
            await Task.Delay(5);
        }
        return await connectTask;
    }

    [Fact]
    public void NetCodec_RoundTripsAllPackets_AndRejectsMalformedOrOversizePayloads()
    {
        Span<byte> buf = stackalloc byte[NetCodec.MaxPacketBytes];

        var hello = new ClientHelloPacket("Kyle Katarn", 1);
        Assert.True(NetCodec.TryWriteClientHello(buf, hello, out int helloLen));
        Assert.True(NetCodec.TryPeekMessageType(buf[..helloLen], out var helloType));
        Assert.Equal(NetMessageType.ClientHello, helloType);
        Assert.True(NetCodec.TryReadClientHello(buf[..helloLen], out var helloOut));
        Assert.Equal(hello, helloOut);

        var welcome = new ServerWelcomePacket(42, 7, 30, "JK1MP.GOB", "m2.jkl", new Vector3(1.5f, -2.5f, 0.25f), 135f, 12);
        Assert.True(NetCodec.TryWriteServerWelcome(buf, welcome, out int welcomeLen));
        Assert.True(NetCodec.TryReadServerWelcome(buf[..welcomeLen], out var welcomeOut));
        Assert.Equal(welcome, welcomeOut);

        var reject = new ServerRejectPacket("Server is full.");
        Assert.True(NetCodec.TryWriteServerReject(buf, reject, out int rejectLen));
        Assert.True(NetCodec.TryReadServerReject(buf[..rejectLen], out var rejectOut));
        Assert.Equal(reject, rejectOut);

        Assert.True(NetCodec.TryWriteClientDisconnect(buf, 42, out int discLen));
        Assert.True(NetCodec.TryReadClientDisconnect(buf[..discLen], out ushort discId));
        Assert.Equal((ushort)42, discId);

        var input = new ClientInputPacket
        {
            ClientId = 42,
            InputSeq = 999,
            Flags = ClientInputFlags.IsGrounded | ClientInputFlags.PrimaryFireHeld | ClientInputFlags.UsePressed,
            Position = new Vector3(1f, 2f, 3f),
            Sector = 5,
            AimDirection = Vector3.UnitY,
            Pitch = -12.5f,
            Yaw = 45f,
            Thrust = new Vector3(0.5f, 1f, 0f),
            ActionSeq = 9,
            RequestedWeaponBin = 10,
            RequestedItemBin = 40,
            ActiveForceBin = 21,
            AckTeleportSeq = 3,
            AckVelocitySeq = 4,
            AckEventSeq = 18
        };
        Assert.True(NetCodec.TryWriteClientInput(buf, input, out int inputLen));
        Assert.True(NetCodec.TryReadClientInput(buf[..inputLen], out var inputOut));
        Assert.Equal(input.ClientId, inputOut.ClientId);
        Assert.Equal(input.InputSeq, inputOut.InputSeq);
        Assert.Equal(input.Flags, inputOut.Flags);
        Assert.Equal(input.Position, inputOut.Position);
        Assert.Equal(input.RequestedWeaponBin, inputOut.RequestedWeaponBin);
        Assert.Equal(input.AckEventSeq, inputOut.AckEventSeq);

        // Non-finite floats are sanitized on write AND rejected if raw NaN/Infinity bytes appear on read
        var badInput = new ClientInputPacket { Position = new Vector3(float.NaN, 0f, 0f) };
        Assert.True(NetCodec.TryWriteClientInput(buf, badInput, out _));

        byte[] nanInput = buf[..inputLen].ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(nanInput.AsSpan(15, 4), float.NaN);
        Assert.False(NetCodec.TryReadClientInput(nanInput, out _));

        byte[] infInput = buf[..inputLen].ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(infInput.AsSpan(15, 4), float.PositiveInfinity);
        Assert.False(NetCodec.TryReadClientInput(infInput, out _));

        var snap = new ServerSnapshotPacket
        {
            ServerTick = 123,
            LevelTime = 4.1f,
            LocalThingIndex = 2
        };
        snap.Local.Health = 85f;
        snap.Local.MaxHealth = 100f;
        snap.Local.Shields = 60f;
        snap.Local.Flags = LocalPlayerSnapFlags.FieldLightOn |
                           LocalPlayerSnapFlags.HasTeleport |
                           LocalPlayerSnapFlags.HasVelocityOverride |
                           LocalPlayerSnapFlags.HasPlatformDelta |
                           LocalPlayerSnapFlags.HasScreenTint;
        snap.Local.TeleportSeq = 3;
        snap.Local.TeleportPosition = new Vector3(4f, 5f, 6f);
        snap.Local.VelocitySeq = 7;
        snap.Local.VelocityOverride = new Vector3(0f, 0f, 4f);
        snap.Local.PlatformDelta = new Vector3(0.1f, 0f, 0f);
        snap.Local.ScreenTint = new Vector3(0f, 0.2f, 0f);
        snap.Local.PovModel = "sabv.3do";
        snap.Local.PovKey = "sabvhold.key";
        snap.Local.StatusMessage = "TEST STATUS";
        snap.Players.Add(new NetPlayerSnapshot(0, 0, 1, "Kyle", Vector3.Zero, 0f, 90f, 100, 10, RemotePlayerAnimFlags.SaberActive, 0.05f, 3, 1, 3));
        snap.Events.Add(new NetWorldEvent(11, NetEventKind.KillFeed, Vector3.Zero, 0f, 0f, 0, "Kyle", "Jan"));
        snap.Projectiles.Add(new NetProjectileSnapshot(Vector3.One, Vector3.UnitY * 10f, 0xFF1122FFu, "bry0.3do"));
        snap.DynamicThings.Add(new NetDynamicThingSnapshot(3, new Vector3(0f, 2f, 1.2f), Vector3.Zero, 0x400u, 0));

        Assert.True(NetCodec.TryWriteServerSnapshot(buf, snap, out int snapLen));
        Assert.InRange(snapLen, 1, NetCodec.MaxPacketBytes);
        Assert.True(NetCodec.TryReadServerSnapshot(buf[..snapLen], out var snapOut));
        Assert.Equal(snap.ServerTick, snapOut.ServerTick);
        Assert.Equal(snap.Local.TeleportPosition, snapOut.Local.TeleportPosition);
        Assert.Equal(snap.Local.VelocityOverride, snapOut.Local.VelocityOverride);
        Assert.Equal("sabv.3do", snapOut.Local.PovModel);
        Assert.Single(snapOut.Players);
        Assert.Single(snapOut.Events);
        Assert.Single(snapOut.Projectiles);
        Assert.Single(snapOut.DynamicThings);

        byte[] nanSnap = buf[..snapLen].ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(nanSnap.AsSpan(11, 4), float.NaN);
        Assert.False(NetCodec.TryReadServerSnapshot(nanSnap, out _));

        // Oversize snapshot gracefully truncates within MaxPacketBytes and still round-trips
        for (int i = 0; i < 120; i++)
            snap.DynamicThings.Add(new NetDynamicThingSnapshot(100 + i, new Vector3(i, 0f, 0f), Vector3.Zero, 0u, 0));
        Assert.True(NetCodec.TryWriteServerSnapshot(buf, snap, out int hugeLen));
        Assert.InRange(hugeLen, 1, NetCodec.MaxPacketBytes);
        Assert.True(NetCodec.TryReadServerSnapshot(buf[..hugeLen], out var hugeOut));
        Assert.True(hugeOut.DynamicThings.Count < snap.DynamicThings.Count);

        // Wrong magic / version / truncated / oversize datagrams are rejected safely
        byte[] corrupted = buf[..helloLen].ToArray();
        corrupted[0] ^= 0xFF;
        Assert.False(NetCodec.TryPeekMessageType(corrupted, out _));
        Assert.False(NetCodec.TryReadClientHello(corrupted, out _));

        for (int trunc = 0; trunc < helloLen; trunc++)
            Assert.False(NetCodec.TryReadClientHello(buf[..trunc], out _));

        byte[] oversize = new byte[NetCodec.MaxPacketBytes + 1];
        Assert.False(NetCodec.TryPeekMessageType(oversize, out _));
        Assert.False(NetCodec.TryReadServerSnapshot(oversize, out _));
    }

    [Fact]
    public async Task DedicatedServer_TwoUdpClients_JoinMoveActivateDoorCombatScoreRespawnAndDisconnect()
    {
        var vfs = new InMemoryVfs();
        vfs.AddText("cog/test_door.cog", """
            symbols
            thing door0
            message activate
            end

            code
            activate:
                MoveToFrame(door0, 1, 6.0);
                Return;
            end
            """);
        vfs.AddText("cog/weap_bryar.cog", """
            symbols
            int player local
            message activated
            message fire
            message deactivated
            end

            code
            activated:
                player = GetSourceRef();
                ActivateWeapon(player, 0.25, GetSenderRef());
                Return;
            fire:
                player = GetSourceRef();
                FireProjectile(player, "+testbolt", "pistol01.wav", GetSenderRef(), '0.0 0.08 0.0', '0 0 0', 1.0, 0, 0.0, 0.0);
                Return;
            deactivated:
                player = GetSourceRef();
                DeactivateWeapon(player, GetSenderRef());
                Return;
            end
            """);

        var level = CreateMultiSpawnBoxArena();
        using var server = new DedicatedGameServer(
            level,
            vfs,
            "JK1MP.GOB",
            "arena.jkl",
            port: 0,
            maxPlayers: 2,
            tickRateHz: 30,
            clientTimeoutSeconds: 2.0f);

        // All unused walkplayer markers start hidden in dedicated server mode
        for (int i = 0; i < 3; i++)
            Assert.NotEqual(0u, level.Things[i].ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Invisible));

        using var client1 = new MultiplayerClient();
        using var client2 = new MultiplayerClient();
        using var client3 = new MultiplayerClient();

        ServerWelcomePacket w1 = await ConnectWithServerTickAsync(server, client1, "Kyle", team: 1);
        ServerWelcomePacket w2 = await ConnectWithServerTickAsync(server, client2, "Jan", team: 2);

        Assert.NotEqual(w1.ClientId, w2.ClientId);
        Assert.NotEqual(w1.PlayerThingId, w2.PlayerThingId);
        Assert.True(Vector3.Distance(w1.SpawnPosition, w2.SpawnPosition) > 1.0f);
        Assert.Equal(2, server.ConnectedClientCount);

        // Server rejects 3rd client when full (maxPlayers: 2)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectWithServerTickAsync(server, client3, "Mara", timeout: TimeSpan.FromSeconds(2)));

        // Move Kyle in front of door and activate it via UsePressed
        var kylePosNearDoor = new Vector3(0f, 1.85f, 0f);
        client1.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded | ClientInputFlags.UsePressed,
            Position = new Vector3(0f, 0f, 0f),
            Sector = 0,
            AimDirection = Vector3.UnitY,
            Yaw = 0f,
            ActionSeq = 2
        });
        await PumpServerAndClientsAsync(server, 0.033f, 2, client1, client2);

        client1.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded | ClientInputFlags.UsePressed,
            Position = kylePosNearDoor,
            Sector = 0,
            AimDirection = Vector3.UnitY,
            Yaw = 0f,
            ActionSeq = 3
        });
        await PumpServerAndClientsAsync(server, 0.05f, 8, client1, client2);

        Assert.NotNull(client2.LatestSnapshot);
        Assert.Contains(client2.LatestSnapshot!.Players, p => p.ThingIndex == w1.PlayerThingId && Vector3.Distance(p.Position, kylePosNearDoor) < 0.05f);
        Assert.Contains(client2.LatestSnapshot.DynamicThings, dt => dt.ThingIndex == 3 && dt.Position.Z > 0.1f);

        // Move Kyle facing Jan at (1.5, 0, 0) and fire lethal Bryar bolt
        var events1 = new List<NetWorldEvent>();
        var events2 = new List<NetWorldEvent>();

        client2.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded,
            Position = new Vector3(1.5f, 0f, 0f),
            Sector = 0,
            AimDirection = -Vector3.UnitX,
            Yaw = 90f,
            ActionSeq = 1
        });
        client1.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded,
            Position = new Vector3(0f, 0f, 0f),
            Sector = 0,
            AimDirection = Vector3.UnitX,
            Yaw = -90f,
            ActionSeq = 3
        });
        await PumpServerAndClientsAsync(server, 0.033f, 2, client1, client2);

        client1.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded | ClientInputFlags.PrimaryFireHeld,
            Position = new Vector3(0f, 0f, 0f),
            Sector = 0,
            AimDirection = Vector3.UnitX,
            Yaw = -90f,
            ActionSeq = 3
        });

        bool sawProjectile = false;
        for (int i = 0; i < 12; i++)
        {
            server.Tick(0.033f);
            await Task.Delay(5);
            client1.PollSnapshots(events1);
            client2.PollSnapshots(events2);
            if (client2.LatestSnapshot?.Projectiles.Count > 0)
                sawProjectile = true;
            if ((client2.LatestSnapshot?.Local.Flags & LocalPlayerSnapFlags.IsDead) != 0)
                break;
        }

        // Release fire hold
        client1.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded,
            Position = new Vector3(0f, 0f, 0f),
            Sector = 0,
            AimDirection = Vector3.UnitX,
            Yaw = -90f,
            ActionSeq = 3
        });
        await PumpServerAndClientsAsync(server, 0.033f, 2, client1, client2);

        Assert.True(sawProjectile, "Expected Bryar projectile to replicate in snapshot before impact.");
        Assert.NotEqual(0, (int)(client2.LatestSnapshot!.Local.Flags & LocalPlayerSnapFlags.IsDead));
        Assert.Equal(0f, client2.LatestSnapshot.Local.Health);

        var kyleRow = Assert.Single(client1.LatestSnapshot!.Players, p => p.ThingIndex == w1.PlayerThingId);
        var janRow = Assert.Single(client1.LatestSnapshot.Players, p => p.ThingIndex == w2.PlayerThingId);
        Assert.Equal(1, kyleRow.Kills);
        Assert.Equal(1, kyleRow.Score);
        Assert.Equal(1, janRow.Deaths);
        Assert.Contains(events1, e => e.Kind == NetEventKind.KillFeed && e.Text0 == "Kyle" && e.Text1 == "Jan");
        Assert.Contains(events2, e => e.Kind == NetEventKind.ExplosionVfx);

        // Respawn Jan and verify stale pre-respawn position packet with old TeleportSeq is rejected!
        uint preRespawnTpSeq = client2.LastTeleportSeq;
        client2.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.RespawnPressed,
            Position = new Vector3(1.5f, 0f, 0f),
            Sector = 0,
            ActionSeq = 10
        });
        server.Tick(0.033f);
        await Task.Delay(5);

        PlayerState janServerState = server.Simulation.GetPlayerByThing(w2.PlayerThingId)!;
        Vector3 respawnPos = janServerState.WorldPosition;
        Assert.False(janServerState.Inventory.IsDead);
        Assert.Equal(100f, janServerState.Inventory.Health);
        Assert.Equal(100f, janServerState.Inventory.Shields);
        Assert.True(Vector3.Distance(respawnPos, new Vector3(1.5f, 0f, 0f)) > 0.5f);

        // Inject stale packet BEFORE client2 polls the new TeleportSeq -> ack still carries preRespawnTpSeq
        client2.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded,
            Position = new Vector3(1.5f, 0f, 0f),
            Sector = 0,
            ActionSeq = 10
        });
        Assert.Equal(preRespawnTpSeq, client2.LastTeleportSeq);
        server.Tick(0.033f);
        await Task.Delay(5);
        Assert.Equal(respawnPos, janServerState.WorldPosition);

        // Poll snapshot to learn new TeleportSeq, then verify movement resumes cleanly
        Assert.True(client2.PollSnapshots());
        Assert.True(client2.LastTeleportSeq > preRespawnTpSeq);
        Assert.False((client2.LatestSnapshot!.Local.Flags & LocalPlayerSnapFlags.IsDead) != 0);

        Vector3 movedAfterRespawn = respawnPos + new Vector3(0.25f, 0f, 0f);
        client2.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded,
            Position = movedAfterRespawn,
            Sector = 0,
            ActionSeq = 10
        });
        server.Tick(0.033f);
        await Task.Delay(5);
        Assert.Equal(movedAfterRespawn, janServerState.WorldPosition);

        // Explicit disconnect clears client snapshot state and removes Jan immediately; timeout expires silent Kyle
        client2.Disconnect();
        Assert.Null(client2.LatestSnapshot);
        Assert.Null(client2.Welcome);
        await PumpServerAndClientsAsync(server, 0.033f, 2, client1);
        Assert.Equal(1, server.ConnectedClientCount);

        for (int i = 0; i < 5; i++)
            server.Tick(0.5f);
        Assert.Equal(0, server.ConnectedClientCount);
    }

    [Fact]
    public async Task DedicatedServer_RetailM2CanyonOasis_TwoUdpClientsJoinFightAndRespawn()
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

        JklLevel m2 = JklReader.Parse(vfs.ReadAllText("jkl/m2.jkl"), "m2");
        using var server = new DedicatedGameServer(m2, vfs, "JK1MP.GOB", "m2.jkl", port: 0, maxPlayers: 4, tickRateHz: 30);

        var spawns = server.Simulation.GetAllPlayerSpawns();
        Assert.True(spawns.Count >= 2);
        foreach (var sp in spawns)
            Assert.NotEqual(0u, sp.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Invisible));

        using var kyle = new MultiplayerClient();
        using var jan = new MultiplayerClient();

        ServerWelcomePacket w1 = await ConnectWithServerTickAsync(server, kyle, "Kyle");
        ServerWelcomePacket w2 = await ConnectWithServerTickAsync(server, jan, "Jan");

        Assert.NotEqual(w1.PlayerThingId, w2.PlayerThingId);
        Assert.True(Vector3.Distance(w1.SpawnPosition, w2.SpawnPosition) > 0.25f);

        // Place Kyle directly in front of Jan's facing direction via valid step deltas to verify live retail weap_bryar combat in m2.jkl
        float janYawRad = w2.SpawnYaw * (MathF.PI / 180f);
        Vector3 janForward = Vector3.Normalize(new Vector3(-MathF.Sin(janYawRad), MathF.Cos(janYawRad), 0f));
        Vector3 desiredDuelPos = w2.SpawnPosition + janForward * 0.45f;
        Vector3 losStart = w2.SpawnPosition + new Vector3(0f, 0f, 0.12f);
        Vector3 targetDuelPos = server.Simulation.TryRaycastWorld(losStart, desiredDuelPos + new Vector3(0f, 0f, 0.12f), out Vector3 wallHit)
            ? Vector3.Lerp(w2.SpawnPosition, new Vector3(wallHit.X, wallHit.Y, w2.SpawnPosition.Z), 0.5f)
            : desiredDuelPos;

        Vector3 stepPos = w1.SpawnPosition;
        while (Vector3.Distance(stepPos, targetDuelPos) > 1.5f)
        {
            Vector3 delta = Vector3.Normalize(targetDuelPos - stepPos) * 1.5f;
            stepPos += delta;
            kyle.SendInput(new ClientInputPacket
            {
                Flags = ClientInputFlags.IsGrounded,
                Position = stepPos,
                Sector = w2.SpawnSector,
                AimDirection = Vector3.UnitY,
                ActionSeq = 1
            });
            server.Tick(0.033f);
            await Task.Delay(3);
        }

        Vector3 aimAtJan = Vector3.Normalize((w2.SpawnPosition + new Vector3(0f, 0f, 0.10f)) - (targetDuelPos + new Vector3(0f, 0f, 0.155f)));
        kyle.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded,
            Position = targetDuelPos,
            Sector = w2.SpawnSector,
            AimDirection = aimAtJan,
            ActionSeq = 1
        });
        await PumpServerAndClientsAsync(server, 0.033f, 2, kyle, jan);

        float initialJanCombinedHp = jan.LatestSnapshot!.Local.Health + jan.LatestSnapshot.Local.Shields;
        bool sawDamage = false;

        // Fire retail weap_bryar.cog until Jan takes damage and eventually dies or HP clearly drops
        kyle.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.IsGrounded | ClientInputFlags.PrimaryFireHeld,
            Position = targetDuelPos,
            Sector = w2.SpawnSector,
            AimDirection = aimAtJan,
            ActionSeq = 1
        });

        for (int i = 0; i < 180; i++)
        {
            server.Tick(0.05f);
            await Task.Delay(3);
            kyle.PollSnapshots();
            jan.PollSnapshots();
            if (jan.LatestSnapshot != null)
            {
                float hp = jan.LatestSnapshot.Local.Health + jan.LatestSnapshot.Local.Shields;
                if (hp < initialJanCombinedHp)
                    sawDamage = true;
                if ((jan.LatestSnapshot.Local.Flags & LocalPlayerSnapFlags.IsDead) != 0)
                    break;
            }
        }

        Assert.True(sawDamage, "Expected retail weap_bryar.cog projectiles to hit and damage Jan in m2.jkl.");
        Assert.NotEqual(0, (int)(jan.LatestSnapshot!.Local.Flags & LocalPlayerSnapFlags.IsDead));

        jan.SendInput(new ClientInputPacket
        {
            Flags = ClientInputFlags.RespawnPressed,
            Position = w2.SpawnPosition,
            Sector = w2.SpawnSector,
            ActionSeq = 5
        });
        await PumpServerAndClientsAsync(server, 0.05f, 4, kyle, jan);
        Assert.False((jan.LatestSnapshot.Local.Flags & LocalPlayerSnapFlags.IsDead) != 0);
        Assert.Equal(100f, jan.LatestSnapshot.Local.Health);

        kyle.Disconnect();
        jan.Disconnect();
        server.Tick(0.033f);
        Assert.Equal(0, server.ConnectedClientCount);
    }

    [Fact]
    public void WorldSimulation_RemoveAddPlayerAndFullSpawnRespawn_UseImmutableSpawnsAndResetTransientState()
    {
        var level = CreateMultiSpawnBoxArena();
        // Keep exactly 2 initial walkplayer spawns so full-slot respawn pressure is exercised directly.
        level.Things.RemoveAt(2);

        var sim = new WorldSimulation(level, new InMemoryVfs(), isMultiplayer: true, isDedicatedServer: true);
        PlayerState p1 = sim.AddPlayer(0, "Kyle", team: 1);
        PlayerState p2 = sim.AddPlayer(1, "Jan", team: 2);

        Vector3 spawn0 = new(-2f, 0f, 0f);
        Vector3 spawn1 = new(2f, 0f, 0f);

        // Move p1 away and set transient actor/saber/speed flags, then remove p1 -> p3 must spawn at immutable spawn0 cleanly.
        sim.UpdatePlayerInput(p1.ThingIndex, new Vector3(0.5f, 1.5f, 0f), 0, Vector3.UnitY);
        sim.SetActorExtraSpeed(p1.ThingIndex, 0.75f);
        sim.SetActorFlags(p1.ThingIndex, 0x80 | 0x4);
        sim.JkSetFlags(p1.ThingIndex, 0x5);
        int reusedId = p1.ThingIndex;
        Assert.True(sim.RemovePlayer(reusedId));

        PlayerState p3 = sim.AddPlayer(0, "ObiWan", team: 1);
        Assert.Equal(reusedId, p3.ThingIndex);
        Assert.Equal(spawn0, p3.WorldPosition);
        Assert.Equal(spawn0, level.Things[reusedId].Position);
        Assert.Equal(0f, sim.GetActorExtraSpeed(reusedId));
        Assert.Equal(0, sim.GetActorFlags(reusedId));
        Assert.Equal(0, sim.JkGetFlags(reusedId));
        Assert.Equal(0, sim.GetThingFlags(reusedId) & (int)(SithThingFlags.Disabled | SithThingFlags.Destroyed | SithThingFlags.Invisible));

        // Move p2 to death location (0.25, 0.25, 0), apply transient flags & saber weapon, kill, and respawn when all spawns are occupied.
        Vector3 deathSpot = new(0.25f, 0.25f, 0f);
        sim.UpdatePlayerInput(p2.ThingIndex, deathSpot, 0, Vector3.UnitX);
        sim.SelectWeapon(p2.ThingIndex, PlayerInventory.BinLightsaber);
        sim.SetActorExtraSpeed(p2.ThingIndex, 0.6f);
        sim.SetActorFlags(p2.ThingIndex, 0x4 | 0x80 | 0x800 | 0x40000);
        sim.JkSetFlags(p2.ThingIndex, 0x5);
        sim.DamageThing(p2.ThingIndex, 250f, 0, p3.ThingIndex);
        Assert.True(p2.Inventory.IsDead);

        sim.RespawnPlayer(p2.ThingIndex);
        Assert.False(p2.Inventory.IsDead);
        // Must respawn at an initial spawn marker (spawn1 is farthest from p3 at spawn0), NEVER at deathSpot.
        Assert.Equal(spawn1, p2.WorldPosition);
        Assert.Equal(spawn1, level.Things[p2.ThingIndex].Position);
        Assert.Equal(0f, sim.GetActorExtraSpeed(p2.ThingIndex));
        Assert.Equal(0, sim.GetActorFlags(p2.ThingIndex) & (0x4 | 0x80 | 0x800 | 0x40000));
        Assert.Equal(0, sim.JkGetFlags(p2.ThingIndex));
        Assert.Equal(PlayerInventory.BinBryar, p2.CurWeaponBin);
    }
}
