using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OpenSith.Core.AI;
using OpenSith.Core.Cog;
using OpenSith.Core.Data;
using OpenSith.Core.Physics;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;

namespace OpenSith.Core.Networking;

/// <summary>
/// Authoritative headless UDP dedicated game server driving a <see cref="WorldSimulation"/>
/// with <c>isDedicatedServer: true</c> and streaming snapshots to <c>0..N</c> connected clients.
/// </summary>
public sealed class DedicatedGameServer : IDisposable
{
    public readonly record struct ConnectedClientInfo(
        ushort ClientId,
        int PlayerThingId,
        byte PlayerSlot,
        string Name,
        int Team,
        int Kills,
        int Deaths,
        int Score,
        Vector3 Position,
        bool IsBot = false);

    private sealed class BotSession
    {
        public int PlayerThingId;
        public byte PlayerSlot;
        public required PlayerPhysics Physics;
        public required DmBotController Controller;
        public bool PrevPrimaryHeld;
        public bool PrevSecondaryHeld;
        public int PrevForceBin;
        public float DeadRespawnTimer;
        public Vector3 PrevSnapshotPos;
        public float LastDistanceMoved;
    }

    private sealed class ClientSession
    {
        public ushort ClientId;
        public required IPEndPoint RemoteEndPoint;
        public int PlayerThingId;
        public byte PlayerSlot;
        public float IdleSeconds;
        public uint LastInputSeq;
        public bool HasInput;
        public int LastActionSeq = -1;

        public uint PendingTeleportSeq = 1;
        public uint AckedTeleportSeq = 1;
        public Vector3 PendingTeleportPos;
        public float PendingTeleportYaw;
        public int PendingTeleportSector;

        public uint PendingVelocitySeq;
        public uint AckedVelocitySeq;
        public Vector3 PendingVelocityOverride;

        public uint AckedEventSeq;

        public bool PrevPrimaryHeld;
        public bool PrevSecondaryHeld;
        public int PrevForceBin;
        public Vector3 PrevSnapshotPos;
        public float LastDistanceMoved;
    }

    private const float MaxClientStepJumpDistance = 3.5f;
    private const int MaxBufferedEvents = 128;

    private readonly UdpClient _udp;
    private readonly JklLevel _level;
    private readonly ServerAudioSink _audioSink;
    private readonly WorldSimulation _sim;
    private readonly string _episodeGob;
    private readonly string _levelFile;
    private readonly int _maxPlayers;
    private readonly byte _tickRateHz;
    private readonly float _clientTimeoutSeconds;
    private readonly int _maxJediRank;
    private readonly bool _defaultAllWeapons;
    private readonly bool _allowClientAllWeapons;

    private readonly List<ClientSession> _clients = [];
    private readonly Dictionary<ushort, ClientSession> _clientsById = new();
    private readonly Dictionary<int, ClientSession> _clientsByThing = new();
    private readonly List<BotSession> _bots = [];
    private readonly Dictionary<int, BotSession> _botsByThing = new();
    private readonly SectorPortalNavGraph _navGraph;
    private readonly List<WorldSimulation.PickupInfo> _pickupScratch = [];
    private readonly List<SolidThingCollider> _botColliderScratch = [];
    private readonly Dictionary<int, (Vector2 Dir, float Speed)> _activeSurfaceScrolls = new();
    private readonly Dictionary<string, float> _activeMaterialAnims = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NetWorldEvent> _recentEvents = [];
    private readonly HashSet<int> _activeLoopChannels = [];
    private readonly List<WorldSimulation.DynamicThingState> _dynamicScratch = [];
    private readonly byte[] _sendBuffer = new byte[NetCodec.MaxPacketBytes];
    private readonly object _sync = new();

    private ushort _nextClientId = 1;
    private int _botSequence;
    private uint _nextEventSeq = 1;
    private bool _disposed;

    public int BoundPort { get; }
    public uint ServerTick { get; private set; }
    public byte TickRateHz => _tickRateHz;
    public int MaxPlayers => _maxPlayers;
    public int MaxJediRank => _maxJediRank;
    public bool DefaultAllWeapons => _defaultAllWeapons;
    public bool AllowClientAllWeapons => _allowClientAllWeapons;
    public int ConnectedClientCount { get { lock (_sync) return _clients.Count; } }
    public int BotCount { get { lock (_sync) return _bots.Count; } }
    public string EpisodeGob => _episodeGob;
    public string LevelFile => _levelFile;
    public WorldSimulation Simulation => _sim;

    public DedicatedGameServer(
        JklLevel level,
        IVirtualFileSystem vfs,
        string episodeGob,
        string levelFile,
        int port = NetCodec.DefaultPort,
        int maxPlayers = 8,
        int tickRateHz = 30,
        float clientTimeoutSeconds = 10f,
        int maxJediRank = PlayerLoadoutConfig.MaxJediRank,
        bool defaultAllWeapons = false,
        bool allowClientAllWeapons = true)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(vfs);

        _level = level;
        _episodeGob = episodeGob ?? string.Empty;
        _levelFile = levelFile ?? string.Empty;
        _maxPlayers = Math.Clamp(maxPlayers, 1, NetCodec.MaxPlayersPerSnapshot);
        _tickRateHz = (byte)Math.Clamp(tickRateHz, 10, 120);
        _clientTimeoutSeconds = MathF.Max(1f, clientTimeoutSeconds);
        _maxJediRank = Math.Clamp(maxJediRank, 0, PlayerLoadoutConfig.MaxJediRank);
        _defaultAllWeapons = defaultAllWeapons;
        _allowClientAllWeapons = allowClientAllWeapons;

        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        ConfigureWindowsUdpSocket(_udp);
        BoundPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        _audioSink = new ServerAudioSink(HandleSimulationSound);
        _navGraph = new SectorPortalNavGraph(level);
        _sim = new WorldSimulation(
            level,
            vfs,
            _audioSink,
            difficulty: 1,
            runStartupCogs: false,
            isMultiplayer: true,
            isDedicatedServer: true);

        _sim.OnPlayerKilled += HandlePlayerKilled;
        _sim.OnPlayerRespawned += HandlePlayerTeleportOrRespawn;
        _sim.OnAnyPlayerTeleported += HandlePlayerTeleportOrRespawn;
        _sim.OnVisualEffectSpawned += HandleVisualEffectSpawned;
        _sim.OnLoopingSoundStarted += HandleLoopingSoundStarted;
        _sim.OnLoopingSoundStopped += HandleLoopingSoundStopped;
        _sim.OnSurfaceScroll += HandleSurfaceScroll;
        _sim.OnMaterialAnimChanged += HandleMaterialAnimChanged;
        _sim.StartLevel();
    }

    public IReadOnlyList<ConnectedClientInfo> GetConnectedClientsSnapshot()
    {
        lock (_sync)
        {
            var result = new List<ConnectedClientInfo>(_clients.Count + _bots.Count);
            foreach (var c in _clients)
            {
                var p = _sim.GetPlayerByThing(c.PlayerThingId);
                if (p == null) continue;
                result.Add(new ConnectedClientInfo(
                    c.ClientId,
                    c.PlayerThingId,
                    c.PlayerSlot,
                    p.Name,
                    p.Team,
                    p.Kills,
                    p.Killed,
                    p.Score,
                    GetPlayerPosition(p),
                    IsBot: false));
            }
            foreach (var b in _bots)
            {
                var p = _sim.GetPlayerByThing(b.PlayerThingId);
                if (p == null) continue;
                result.Add(new ConnectedClientInfo(
                    0,
                    b.PlayerThingId,
                    b.PlayerSlot,
                    p.Name,
                    p.Team,
                    p.Kills,
                    p.Killed,
                    p.Score,
                    GetPlayerPosition(p),
                    IsBot: true));
            }
            return result;
        }
    }

    /// <summary>
    /// Spawns a server-authoritative Deathmatch AI bot that participates as a native
    /// <see cref="PlayerState"/> + <see cref="PlayerPhysics"/> player across all weapons and UDP snapshots.
    /// Returns the bot's player Thing index, or <c>-1</c> if the server is at capacity.
    /// </summary>
    public int AddBot(
        string? name = null,
        BotSkill skill = BotSkill.Medium,
        bool grantAllWeapons = false,
        int team = 0,
        int? favoriteWeaponBin = null,
        PlayerLoadoutConfig? customLoadout = null,
        Random? rng = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.Count + _bots.Count >= _maxPlayers)
                return -1;

            int archIdx = _botSequence++;
            BotArchetype archetype = DmBotController.DefaultArchetypes[archIdx % DmBotController.DefaultArchetypes.Count];
            if (!string.IsNullOrWhiteSpace(name))
                archetype = archetype with { Name = name.Trim() };
            else if (archIdx >= DmBotController.DefaultArchetypes.Count)
                archetype = archetype with { Name = $"{archetype.Name}_{(archIdx / DmBotController.DefaultArchetypes.Count) + 1}" };

            if (favoriteWeaponBin.HasValue && WorldSimulation.IsValidWeaponBin(favoriteWeaponBin.Value))
                archetype = archetype with { FavoriteWeaponBin = favoriteWeaponBin.Value };

            byte freeSlot = AllocateFreeSlot();
            PlayerState player = _sim.AddPlayer(freeSlot, archetype.Name, team);

            PlayerLoadoutConfig loadout;
            if (customLoadout != null)
            {
                loadout = customLoadout.Clone();
            }
            else
            {
                loadout = new PlayerLoadoutConfig
                {
                    ModelIndex = ResolveModelIndexHint(archetype.PreferredModelHint, archIdx),
                    SaberColorIndex = _sim.SaberCatalog.Count > 0 ? archetype.SaberColorIndex % _sim.SaberCatalog.Count : 0,
                    JediRank = skill switch
                    {
                        BotSkill.Easy => 0,
                        BotSkill.Medium => 3,
                        _ => 6
                    },
                    GrantAllWeapons = grantAllWeapons || _defaultAllWeapons
                };
                if (loadout.JediRank > 0)
                {
                    for (int i = 0; i < PlayerCustomizationCatalog.ForcePowers.Count; i++)
                    {
                        int bin = PlayerCustomizationCatalog.ForcePowers[i].Bin;
                        if (bin is PlayerInventory.BinForceJump or PlayerInventory.BinForceSpeed or PlayerInventory.BinForceHeal or PlayerInventory.BinForceLightning)
                            loadout.ForceRanks[i] = (byte)(skill == BotSkill.Hard ? 3 : 2);
                    }
                }
            }

            if (_defaultAllWeapons)
                loadout.GrantAllWeapons = true;
            loadout.ClampToPolicy(
                _maxJediRank,
                grantAllWeapons || _allowClientAllWeapons || _defaultAllWeapons,
                _sim.ModelCatalog.Count,
                _sim.SaberCatalog.Count);

            _sim.ApplyPlayerCustomization(player.ThingIndex, loadout);

            JklThing? spawnThing = player.ThingIndex >= 0 && player.ThingIndex < _level.Things.Count
                ? _level.Things[player.ThingIndex]
                : null;
            Vector3 spawnPos = spawnThing?.Position ?? player.WorldPosition;
            int spawnSector = spawnThing?.SectorIndex ?? 0;

            var physics = new PlayerPhysics(spawnPos, spawnSector);
            physics.ConfigureFromModel(_sim.ResolveThreeDoModel(spawnThing?.Model3D));

            var bot = new BotSession
            {
                PlayerThingId = player.ThingIndex,
                PlayerSlot = freeSlot,
                Physics = physics,
                Controller = new DmBotController(archetype, skill, rng),
                PrevSnapshotPos = spawnPos
            };

            _bots.Add(bot);
            _botsByThing[player.ThingIndex] = bot;
            EnqueueEvent(NetEventKind.StatusMessage, Vector3.Zero, 0f, 0f, 0, $"{player.Name} [BOT] joined the game", string.Empty);
            return player.ThingIndex;
        }
    }

    public bool RemoveBot(int thingId)
    {
        lock (_sync)
        {
            if (!_botsByThing.Remove(thingId, out var bot))
                return false;

            string name = _sim.GetPlayerByThing(thingId)?.Name ?? $"Bot {bot.PlayerSlot + 1}";
            _bots.Remove(bot);
            _sim.RemovePlayer(thingId);
            EnqueueEvent(NetEventKind.StatusMessage, Vector3.Zero, 0f, 0f, 0, $"{name} left the game", string.Empty);
            return true;
        }
    }

    public int RemoveAllBots()
    {
        lock (_sync)
        {
            int count = _bots.Count;
            if (count == 0)
                return 0;

            foreach (var bot in _bots)
            {
                _botsByThing.Remove(bot.PlayerThingId);
                _sim.RemovePlayer(bot.PlayerThingId);
            }
            _bots.Clear();
            EnqueueEvent(NetEventKind.StatusMessage, Vector3.Zero, 0f, 0f, 0, $"Removed {count} bot(s)", string.Empty);
            return count;
        }
    }

    private int ResolveModelIndexHint(string preferredHint, int fallbackSeed)
    {
        if (_sim.ModelCatalog.Count == 0)
            return 0;

        if (!string.IsNullOrWhiteSpace(preferredHint))
        {
            for (int i = 0; i < _sim.ModelCatalog.Count; i++)
            {
                if (_sim.ModelCatalog[i].DisplayName.Contains(preferredHint, StringComparison.OrdinalIgnoreCase) ||
                    _sim.ModelCatalog[i].Model3D.Contains(preferredHint, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return Math.Abs(fallbackSeed) % _sim.ModelCatalog.Count;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        float fixedDt = 1.0f / _tickRateHz;
        double fixedDtSec = 1.0 / _tickRateHz;
        long prevTicks = Stopwatch.GetTimestamp();
        double accumulator = 0.0;

        while (!cancellationToken.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - prevTicks) / (double)Stopwatch.Frequency;
            prevTicks = now;
            if (elapsed > 0.25)
                elapsed = 0.25;
            accumulator += elapsed;

            while (accumulator >= fixedDtSec && !cancellationToken.IsCancellationRequested)
            {
                Tick(fixedDt);
                accumulator -= fixedDtSec;
            }

            double remainingMs = (fixedDtSec - accumulator) * 1000.0;
            if (remainingMs > 2.0)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remainingMs - 1.0, 15.0)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drains incoming UDP packets, steps the authoritative simulation by <paramref name="dt"/>,
    /// expires timed-out clients, and transmits per-client state snapshots.
    /// </summary>
    public void Tick(float dt)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (dt <= 0f)
                return;

            DrainIncomingPackets();

            // Advance idle timers and expire silent clients
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                var client = _clients[i];
                client.IdleSeconds += dt;
                if (client.IdleSeconds >= _clientTimeoutSeconds)
                {
                    DisconnectClient(client, "timed out");
                }
            }

            StepBotsPreSim(dt);

            _sim.StepWorld(dt);
            ServerTick++;

            StepBotsPostSim();

            foreach (var client in _clients)
            {
                if (_sim.TryConsumePlayerVelocityOverride(client.PlayerThingId, out Vector3 velOverride))
                {
                    client.PendingVelocitySeq++;
                    client.PendingVelocityOverride = velOverride;
                }
            }

            BroadcastSnapshots();
        }
    }

    private void DrainIncomingPackets()
    {
        int maxPacketsPerTick = Math.Max(64, _maxPlayers * 32);
        int drained = 0;
        while (!_disposed && drained < maxPacketsPerTick)
        {
            IPEndPoint? senderEp = null;
            byte[] data;
            try
            {
                if (_udp.Available <= 0)
                    break;
                data = _udp.Receive(ref senderEp);
                drained++;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (senderEp == null || !NetCodec.TryPeekMessageType(data, out NetMessageType msgType))
                continue;

            switch (msgType)
            {
                case NetMessageType.ClientHello:
                    if (NetCodec.TryReadClientHello(data, out var hello))
                        HandleClientHello(senderEp, hello);
                    break;

                case NetMessageType.ClientInput:
                    if (NetCodec.TryReadClientInput(data, out var input))
                        HandleClientInput(senderEp, input);
                    break;

                case NetMessageType.ClientConfig:
                    if (NetCodec.TryReadClientConfig(data, out var config))
                        HandleClientConfig(senderEp, config);
                    break;

                case NetMessageType.ClientDisconnect:
                    if (NetCodec.TryReadClientDisconnect(data, out ushort discId))
                        HandleClientDisconnect(senderEp, discId);
                    break;
            }
        }
    }

    private void HandleClientHello(IPEndPoint senderEp, ClientHelloPacket hello)
    {
        // Idempotent reconnect from identical endpoint in case ServerWelcome dropped
        foreach (var existing in _clients)
        {
            if (existing.RemoteEndPoint.Equals(senderEp))
            {
                existing.IdleSeconds = 0f;
                var existingPlayer = _sim.GetPlayerByThing(existing.PlayerThingId);
                Vector3 pos = existingPlayer != null ? GetPlayerPosition(existingPlayer) : Vector3.Zero;
                int sector = existingPlayer != null && existing.PlayerThingId < _level.Things.Count
                    ? _level.Things[existing.PlayerThingId].SectorIndex
                    : 0;
                float yaw = existingPlayer != null && existing.PlayerThingId < _level.Things.Count
                    ? _level.Things[existing.PlayerThingId].PitchYawRoll.Y
                    : 0f;

                SendWelcome(senderEp, existing, pos, yaw, sector);
                return;
            }
        }

        if (_clients.Count + _bots.Count >= _maxPlayers)
        {
            if (NetCodec.TryWriteServerReject(_sendBuffer, new ServerRejectPacket("Server is full."), out int rejLen))
                SafeSendTo(_sendBuffer.AsSpan(0, rejLen), senderEp);
            return;
        }

        byte freeSlot = AllocateFreeSlot();
        string safeName = string.IsNullOrWhiteSpace(hello.PlayerName)
            ? $"Player {freeSlot + 1}"
            : hello.PlayerName.Trim();

        PlayerState player = _sim.AddPlayer(freeSlot, safeName, hello.Team);
        PlayerLoadoutConfig initialLoadout = NetCodec.DecodeLoadout(
            hello.ModelIndex,
            hello.SaberColorIndex,
            hello.JediRank,
            hello.LoadoutFlags,
            hello.PackedForceRanks);
        if (_defaultAllWeapons)
            initialLoadout.GrantAllWeapons = true;
        initialLoadout.ClampToPolicy(
            _maxJediRank,
            _allowClientAllWeapons || _defaultAllWeapons,
            _sim.ModelCatalog.Count,
            _sim.SaberCatalog.Count);
        _sim.ApplyPlayerCustomization(player.ThingIndex, initialLoadout);
        ushort clientId = _nextClientId++;
        if (_nextClientId == 0)
            _nextClientId = 1;

        var thing = player.ThingIndex >= 0 && player.ThingIndex < _level.Things.Count
            ? _level.Things[player.ThingIndex]
            : null;
        Vector3 spawnPos = thing?.Position ?? player.WorldPosition;
        float spawnYaw = thing?.PitchYawRoll.Y ?? 0f;
        int spawnSector = thing?.SectorIndex ?? 0;

        var session = new ClientSession
        {
            ClientId = clientId,
            RemoteEndPoint = new IPEndPoint(senderEp.Address, senderEp.Port),
            PlayerThingId = player.ThingIndex,
            PlayerSlot = freeSlot,
            PrevSnapshotPos = spawnPos,
            PendingTeleportPos = spawnPos,
            PendingTeleportYaw = spawnYaw,
            PendingTeleportSector = spawnSector,
            AckedEventSeq = _nextEventSeq > 0 ? _nextEventSeq - 1 : 0
        };

        _clients.Add(session);
        _clientsById[clientId] = session;
        _clientsByThing[player.ThingIndex] = session;

        EnqueueEvent(NetEventKind.StatusMessage, Vector3.Zero, 0f, 0f, 0, $"{player.Name} joined the game", string.Empty);
        ReplaySurfaceAndMaterialAnimsToEvents();
        SendWelcome(senderEp, session, spawnPos, spawnYaw, spawnSector);
    }

    private void HandleClientInput(IPEndPoint senderEp, ClientInputPacket pkt)
    {
        if (!_clientsById.TryGetValue(pkt.ClientId, out var client) || !client.RemoteEndPoint.Equals(senderEp))
            return;

        client.IdleSeconds = 0f;

        if (client.HasInput && pkt.InputSeq < client.LastInputSeq)
            return;
        client.HasInput = true;
        client.LastInputSeq = pkt.InputSeq;

        if (pkt.AckTeleportSeq > client.AckedTeleportSeq)
            client.AckedTeleportSeq = Math.Min(pkt.AckTeleportSeq, client.PendingTeleportSeq);
        if (pkt.AckVelocitySeq > client.AckedVelocitySeq)
            client.AckedVelocitySeq = Math.Min(pkt.AckVelocitySeq, client.PendingVelocitySeq);
        if (pkt.AckEventSeq > client.AckedEventSeq)
            client.AckedEventSeq = pkt.AckEventSeq;

        PlayerState? player = _sim.GetPlayerByThing(client.PlayerThingId);
        if (player == null)
            return;

        if (player.Inventory.IsDead)
        {
            ReleaseActivePlayerHolds(client, player);
            bool deadActionEdge = pkt.ActionSeq != client.LastActionSeq;
            client.LastActionSeq = pkt.ActionSeq;
            if (deadActionEdge && (pkt.Flags & ClientInputFlags.RespawnPressed) != 0)
                _sim.RespawnPlayer(client.PlayerThingId);
            return;
        }

        // Ignore stale pre-teleport/pre-respawn packets (position, aim, actions, fire) until TeleportSeq is acked
        if (client.AckedTeleportSeq < client.PendingTeleportSeq)
            return;

        bool actionEdge = pkt.ActionSeq != client.LastActionSeq;
        client.LastActionSeq = pkt.ActionSeq;

        Vector3 aimDir = pkt.AimDirection.LengthSquared() > 1e-6f
            ? Vector3.Normalize(pkt.AimDirection)
            : player.AimDirection;

        Vector3 acceptedPos = GetPlayerPosition(player);
        int acceptedSector = player.ThingIndex < _level.Things.Count
            ? _level.Things[player.ThingIndex].SectorIndex
            : Math.Max(0, pkt.Sector);

        float stepDist = Vector3.Distance(pkt.Position, acceptedPos);
        if (stepDist <= MaxClientStepJumpDistance)
        {
            acceptedPos = pkt.Position;
            if (pkt.Sector >= 0 && pkt.Sector < _level.Sectors.Count)
                acceptedSector = pkt.Sector;
        }
        else
        {
            // Excessive discrepancy without a server teleport -> pull client back authoritatively
            client.PendingTeleportSeq++;
            client.PendingTeleportPos = acceptedPos;
            client.PendingTeleportYaw = pkt.Yaw;
            client.PendingTeleportSector = acceptedSector;
        }

        _sim.UpdatePlayerInput(
            client.PlayerThingId,
            acceptedPos,
            acceptedSector,
            aimDir,
            pkt.Thrust,
            (pkt.Flags & ClientInputFlags.IsGrounded) != 0,
            (pkt.Flags & ClientInputFlags.IsUnderwater) != 0);

        if (client.PlayerThingId < _level.Things.Count)
        {
            var cur = _level.Things[client.PlayerThingId].PitchYawRoll;
            _level.Things[client.PlayerThingId].PitchYawRoll = new Vector3(pkt.Pitch, pkt.Yaw, cur.Z);
        }

        if (actionEdge)
        {
            if (WorldSimulation.IsValidWeaponBin(pkt.RequestedWeaponBin))
                _sim.SelectWeapon(client.PlayerThingId, pkt.RequestedWeaponBin);
            if (WorldSimulation.IsValidItemBin(pkt.RequestedItemBin))
                _sim.UseItem(client.PlayerThingId, pkt.RequestedItemBin);
            if ((pkt.Flags & ClientInputFlags.UsePressed) != 0)
                _sim.ActivateNearestDoor(client.PlayerThingId, acceptedPos, aimDir);
        }

        bool primaryHeld = (pkt.Flags & ClientInputFlags.PrimaryFireHeld) != 0;
        bool secondaryHeld = (pkt.Flags & ClientInputFlags.SecondaryFireHeld) != 0;
        int desiredForceBin = WorldSimulation.IsValidForceBin(pkt.ActiveForceBin)
            ? pkt.ActiveForceBin
            : 0;
        SyncWeaponAndForceHolds(
            client.PlayerThingId,
            ref client.PrevPrimaryHeld,
            ref client.PrevSecondaryHeld,
            ref client.PrevForceBin,
            primaryHeld,
            secondaryHeld,
            desiredForceBin,
            aimDir);
    }

    private void ReleaseActivePlayerHolds(ClientSession client, PlayerState player) =>
        ReleaseWeaponAndForceHolds(
            player.ThingIndex,
            ref client.PrevPrimaryHeld,
            ref client.PrevSecondaryHeld,
            ref client.PrevForceBin);

    private void SyncWeaponAndForceHolds(
        int playerThingId,
        ref bool prevPrimaryHeld,
        ref bool prevSecondaryHeld,
        ref int prevForceBin,
        bool primaryHeld,
        bool secondaryHeld,
        int desiredForceBin,
        Vector3 aimDir)
    {
        if (primaryHeld != prevPrimaryHeld)
        {
            prevPrimaryHeld = primaryHeld;
            if (primaryHeld)
                _sim.BeginWeaponFire(playerThingId, 0);
            else
                _sim.EndWeaponFire(playerThingId, 0);
        }

        if (secondaryHeld != prevSecondaryHeld)
        {
            prevSecondaryHeld = secondaryHeld;
            if (secondaryHeld)
                _sim.BeginWeaponFire(playerThingId, 1);
            else
                _sim.EndWeaponFire(playerThingId, 1);
        }

        if (desiredForceBin != prevForceBin)
        {
            if (prevForceBin > 0)
                _sim.EndForcePower(playerThingId, prevForceBin);
            prevForceBin = desiredForceBin;
            if (desiredForceBin > 0)
                _sim.BeginForcePower(playerThingId, desiredForceBin, aimDir);
        }
    }

    private void ReleaseWeaponAndForceHolds(
        int playerThingId,
        ref bool prevPrimaryHeld,
        ref bool prevSecondaryHeld,
        ref int prevForceBin)
    {
        if (prevPrimaryHeld)
        {
            prevPrimaryHeld = false;
            _sim.EndWeaponFire(playerThingId, 0);
        }
        if (prevSecondaryHeld)
        {
            prevSecondaryHeld = false;
            _sim.EndWeaponFire(playerThingId, 1);
        }
        if (prevForceBin > 0)
        {
            _sim.EndForcePower(playerThingId, prevForceBin);
            prevForceBin = 0;
        }
    }

    /// <summary>
    /// Computes the horizontal movement speed for a Deathmatch AI bot according to skill tier,
    /// directional thrust, and Force Speed extra speed, avoiding runaway speed stacking.
    /// </summary>
    public static float ComputeBotMoveSpeed(BotSkill skill, Vector3 localThrust, float extraSpeed)
    {
        bool isForward = localThrust.Y >= 0.1f;
        float baseSpeed = (skill, isForward) switch
        {
            (BotSkill.Easy, true) => 1.25f,
            (BotSkill.Easy, false) => 1.00f,
            (BotSkill.Medium, true) => 1.50f,
            (BotSkill.Medium, false) => 1.25f,
            (_, true) => 1.75f,
            (_, false) => 1.45f
        };
        float boostMul = 1.0f + Math.Clamp(extraSpeed, -0.5f, 1.5f) * 0.35f;
        return baseSpeed * boostMul;
    }

    private void StepBotsPreSim(float dt)
    {
        if (_bots.Count == 0)
            return;

        _sim.CollectActivePickups(_pickupScratch);

        foreach (var bot in _bots)
        {
            var player = _sim.GetPlayerByThing(bot.PlayerThingId);
            if (player == null || bot.PlayerThingId < 0 || bot.PlayerThingId >= _level.Things.Count)
                continue;

            var thing = _level.Things[bot.PlayerThingId];
            if (player.Inventory.IsDead)
            {
                ReleaseWeaponAndForceHolds(
                    bot.PlayerThingId,
                    ref bot.PrevPrimaryHeld,
                    ref bot.PrevSecondaryHeld,
                    ref bot.PrevForceBin);
                bot.DeadRespawnTimer += dt;
                if (bot.DeadRespawnTimer >= 1.5f)
                {
                    bot.DeadRespawnTimer = 0f;
                    _sim.RespawnPlayer(bot.PlayerThingId);
                }
                continue;
            }

            bot.DeadRespawnTimer = 0f;
            BotTickDecision decision = bot.Controller.Decide(dt, _sim, player, thing, _navGraph, _pickupScratch);

            _botColliderScratch.Clear();
            foreach (var other in _sim.Players)
            {
                if (other.ThingIndex == bot.PlayerThingId || other.Inventory.IsDead)
                    continue;
                _botColliderScratch.Add(new SolidThingCollider(GetPlayerPosition(other), 0.065f, 0.18f));
            }

            bot.Physics.MoveSpeed = ComputeBotMoveSpeed(
                bot.Controller.Skill,
                decision.LocalThrust,
                _sim.GetActorExtraSpeed(bot.PlayerThingId));
            bot.Physics.Update(
                dt,
                decision.WishMoveDir,
                decision.WishJump,
                _level,
                solidThings: _botColliderScratch,
                modelResolver: _sim.ResolveThreeDoModel);

            thing.PitchYawRoll = new Vector3(decision.Pitch, decision.Yaw, thing.PitchYawRoll.Z);
            _sim.UpdatePlayerInput(
                bot.PlayerThingId,
                bot.Physics.Position,
                bot.Physics.CurrentSectorIndex,
                decision.AimDirection,
                decision.LocalThrust,
                bot.Physics.IsGrounded,
                bot.Physics.IsUnderwater);

            if (decision.DesiredWeaponBin != player.CurWeaponBin &&
                WorldSimulation.IsValidWeaponBin(decision.DesiredWeaponBin))
            {
                _sim.SelectWeapon(bot.PlayerThingId, decision.DesiredWeaponBin);
            }

            if (decision.UsePressed)
                _sim.ActivateNearestDoor(bot.PlayerThingId, bot.Physics.Position, decision.AimDirection);

            int desiredForceBin = WorldSimulation.IsValidForceBin(decision.DesiredForceBin)
                ? decision.DesiredForceBin
                : 0;
            SyncWeaponAndForceHolds(
                bot.PlayerThingId,
                ref bot.PrevPrimaryHeld,
                ref bot.PrevSecondaryHeld,
                ref bot.PrevForceBin,
                decision.PrimaryFireHeld,
                decision.SecondaryFireHeld,
                desiredForceBin,
                decision.AimDirection);
        }
    }

    private void StepBotsPostSim()
    {
        if (_bots.Count == 0)
            return;

        foreach (var bot in _bots)
        {
            var player = _sim.GetPlayerByThing(bot.PlayerThingId);
            if (player == null || player.Inventory.IsDead)
                continue;

            Vector3 platDelta = _sim.ConsumePlayerPlatformDelta(bot.PlayerThingId);
            if (platDelta.LengthSquared() > 1e-8f)
                bot.Physics.Position += platDelta;

            if (_sim.TryConsumePlayerVelocityOverride(bot.PlayerThingId, out Vector3 velOverride))
                bot.Physics.ApplyExternalVelocity(velOverride);
        }
    }

    private void HandleClientConfig(IPEndPoint senderEp, ClientConfigPacket pkt)
    {
        if (!_clientsById.TryGetValue(pkt.ClientId, out var client) || !client.RemoteEndPoint.Equals(senderEp))
            return;

        client.IdleSeconds = 0f;
        PlayerLoadoutConfig loadout = NetCodec.DecodeLoadout(
            pkt.ModelIndex,
            pkt.SaberColorIndex,
            pkt.JediRank,
            pkt.LoadoutFlags,
            pkt.PackedForceRanks);
        if (_defaultAllWeapons)
            loadout.GrantAllWeapons = true;
        loadout.ClampToPolicy(
            _maxJediRank,
            _allowClientAllWeapons || _defaultAllWeapons,
            _sim.ModelCatalog.Count,
            _sim.SaberCatalog.Count);
        _sim.ApplyPlayerCustomization(client.PlayerThingId, loadout, refillVitalsAndAmmo: false);
    }

    private void HandleClientDisconnect(IPEndPoint senderEp, ushort clientId)
    {
        if (_clientsById.TryGetValue(clientId, out var client) && client.RemoteEndPoint.Equals(senderEp))
            DisconnectClient(client, "disconnected");
    }

    private void DisconnectClient(ClientSession client, string reason)
    {
        string name = _sim.GetPlayerByThing(client.PlayerThingId)?.Name ?? $"Player {client.PlayerSlot + 1}";
        _clients.Remove(client);
        _clientsById.Remove(client.ClientId);
        _clientsByThing.Remove(client.PlayerThingId);
        _sim.RemovePlayer(client.PlayerThingId);
        EnqueueEvent(NetEventKind.StatusMessage, Vector3.Zero, 0f, 0f, 0, $"{name} {reason}", string.Empty);
    }

    private void BroadcastSnapshots()
    {
        if (_clients.Count == 0)
            return;

        _sim.CollectDynamicThingStates(_dynamicScratch);

        // Compute per-player distance delta cleanly once per tick before building client packets
        var playerSnaps = new List<NetPlayerSnapshot>(_clients.Count + _bots.Count);
        foreach (var c in _clients)
        {
            var p = _sim.GetPlayerByThing(c.PlayerThingId);
            if (p == null) continue;
            playerSnaps.Add(BuildPlayerSnapshot(p, ref c.PrevSnapshotPos, out c.LastDistanceMoved));
        }
        foreach (var b in _bots)
        {
            var p = _sim.GetPlayerByThing(b.PlayerThingId);
            if (p == null) continue;
            playerSnaps.Add(BuildPlayerSnapshot(p, ref b.PrevSnapshotPos, out b.LastDistanceMoved));
        }

        var projectiles = new List<NetProjectileSnapshot>(_sim.Projectiles.Count);
        foreach (var pr in _sim.Projectiles)
        {
            uint rgba =
                ((uint)Math.Clamp((int)(pr.BoltColor.X * 255f), 0, 255)) |
                ((uint)Math.Clamp((int)(pr.BoltColor.Y * 255f), 0, 255) << 8) |
                ((uint)Math.Clamp((int)(pr.BoltColor.Z * 255f), 0, 255) << 16) |
                0xFF000000u;
            projectiles.Add(new NetProjectileSnapshot(pr.Position, pr.Velocity, rgba, pr.ModelName));
        }

        var dynamicThings = new List<NetDynamicThingSnapshot>(_dynamicScratch.Count);
        foreach (var dt in _dynamicScratch)
            dynamicThings.Add(new NetDynamicThingSnapshot(dt.ThingIndex, dt.Position, dt.PitchYawRoll, dt.ThingFlags, dt.SectorIndex));

        Span<byte> liveForceRanks = stackalloc byte[PlayerLoadoutConfig.ForcePowerCount];
        foreach (var client in _clients)
        {
            var lp = _sim.GetPlayerByThing(client.PlayerThingId);
            if (lp == null) continue;

            var snap = new ServerSnapshotPacket
            {
                ServerTick = ServerTick,
                LevelTime = _sim.GetLevelTime(),
                LocalThingIndex = lp.ThingIndex
            };

            var inv = lp.Inventory;
            LocalPlayerSnapFlags lpFlags = LocalPlayerSnapFlags.None;
            if (inv.IsDead) lpFlags |= LocalPlayerSnapFlags.IsDead;
            if (_sim.IsFieldLightActive(lp.ThingIndex)) lpFlags |= LocalPlayerSnapFlags.FieldLightOn;
            if (_sim.IsActorFrozen(lp.ThingIndex)) lpFlags |= LocalPlayerSnapFlags.IsFrozen;
            if (lp.SaberDamageActive) lpFlags |= LocalPlayerSnapFlags.SaberDamageActive;
            if (client.AckedTeleportSeq < client.PendingTeleportSeq) lpFlags |= LocalPlayerSnapFlags.HasTeleport;
            if (client.AckedVelocitySeq < client.PendingVelocitySeq) lpFlags |= LocalPlayerSnapFlags.HasVelocityOverride;

            Vector3 platDelta = _sim.ConsumePlayerPlatformDelta(lp.ThingIndex);
            if (platDelta.LengthSquared() > 1e-8f)
                lpFlags |= LocalPlayerSnapFlags.HasPlatformDelta;

            Vector3 tint = _sim.ComputeScreenTintForPlayer(lp.ThingIndex);
            if (tint.LengthSquared() > 1e-6f)
                lpFlags |= LocalPlayerSnapFlags.HasScreenTint;

            snap.Local.Health = inv.Health;
            snap.Local.MaxHealth = inv.MaxHealth;
            snap.Local.Shields = inv.Shields;
            snap.Local.EnergyAmmo = (short)Math.Clamp((int)inv.EnergyAmmo, 0, short.MaxValue);
            snap.Local.PowerAmmo = (short)Math.Clamp((int)inv.PowerAmmo, 0, short.MaxValue);
            snap.Local.Battery = (short)Math.Clamp((int)inv.Battery, 0, short.MaxValue);
            snap.Local.ForceMana = (short)Math.Clamp((int)inv.ForceMana, 0, short.MaxValue);
            snap.Local.MaxForceMana = (short)Math.Clamp((int)inv.MaxForceMana, 0, short.MaxValue);
            snap.Local.ThermDetAmmo = (short)Math.Clamp((int)inv.GetInv(PlayerInventory.BinThermDet), 0, short.MaxValue);
            snap.Local.RailAmmo = (short)Math.Clamp((int)inv.RailCharges, 0, short.MaxValue);
            snap.Local.SeqAmmo = (short)Math.Clamp((int)inv.GetInv(PlayerInventory.BinSeqCharge), 0, short.MaxValue);
            snap.Local.CurWeaponBin = (byte)Math.Clamp(lp.CurWeaponBin, 0, 255);
            snap.Local.SelectedItemBin = (byte)Math.Clamp(lp.SelectedItemBin, 0, 255);
            snap.Local.SelectedForceBin = (byte)Math.Clamp(lp.SelectedForceBin, 0, 255);
            snap.Local.ExtraSpeed = _sim.GetActorExtraSpeed(lp.ThingIndex);
            snap.Local.Flags = lpFlags;
            snap.Local.TeleportSeq = client.PendingTeleportSeq;
            snap.Local.TeleportPosition = client.PendingTeleportPos;
            snap.Local.TeleportYaw = client.PendingTeleportYaw;
            snap.Local.TeleportSector = client.PendingTeleportSector;
            snap.Local.VelocitySeq = client.PendingVelocitySeq;
            snap.Local.VelocityOverride = client.PendingVelocityOverride;
            snap.Local.PlatformDelta = platDelta;
            snap.Local.ScreenTint = tint;
            snap.Local.PovModel = lp.PovModel;
            snap.Local.PovKey = lp.PovKey;
            snap.Local.PovKeyTime = lp.PovKeyTime;
            snap.Local.PovShakePos = lp.PovShakePos;
            snap.Local.ModelIndex = (byte)Math.Clamp(lp.ModelIndex, 0, 255);
            snap.Local.SaberColorIndex = (byte)Math.Clamp(lp.SaberColorIndex, 0, 255);
            snap.Local.JediRank = (byte)Math.Clamp(inv.ForceRank, 0, PlayerLoadoutConfig.MaxJediRank);
            snap.Local.LoadoutFlags = lp.Loadout?.GrantAllWeapons == true ? NetLoadoutFlags.GrantAllWeapons : NetLoadoutFlags.None;
            liveForceRanks.Clear();
            for (int fi = 0; fi < PlayerCustomizationCatalog.ForcePowers.Count; fi++)
                liveForceRanks[fi] = (byte)Math.Clamp((int)inv.GetInv(PlayerCustomizationCatalog.ForcePowers[fi].Bin), 0, PlayerLoadoutConfig.MaxForcePowerRank);
            snap.Local.PackedForceRanks = NetCodec.PackForceRanks(liveForceRanks);
            snap.Local.StatusMessage = lp.StatusMessage ?? string.Empty;

            snap.Players.AddRange(playerSnaps);
            snap.Projectiles.AddRange(projectiles);
            snap.DynamicThings.AddRange(dynamicThings);

            foreach (var evt in _recentEvents)
            {
                if (evt.EventSeq > client.AckedEventSeq)
                    snap.Events.Add(evt);
            }

            if (NetCodec.TryWriteServerSnapshot(_sendBuffer, snap, out int bytesWritten))
                SafeSendTo(_sendBuffer.AsSpan(0, bytesWritten), client.RemoteEndPoint);
        }
    }

    private void SendWelcome(IPEndPoint target, ClientSession session, Vector3 pos, float yaw, int sector)
    {
        var welcome = new ServerWelcomePacket(
            session.ClientId,
            session.PlayerThingId,
            _tickRateHz,
            _episodeGob,
            _levelFile,
            pos,
            yaw,
            sector);
        if (NetCodec.TryWriteServerWelcome(_sendBuffer, welcome, out int written))
            SafeSendTo(_sendBuffer.AsSpan(0, written), target);
    }

    private void SafeSendTo(ReadOnlySpan<byte> payload, IPEndPoint destination)
    {
        try
        {
            _udp.Send(payload, destination);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private NetPlayerSnapshot BuildPlayerSnapshot(PlayerState p, ref Vector3 prevSnapshotPos, out float distMoved)
    {
        Vector3 pos = GetPlayerPosition(p);
        distMoved = p.Inventory.IsDead
            ? 0f
            : Vector2.Distance(new Vector2(prevSnapshotPos.X, prevSnapshotPos.Y), new Vector2(pos.X, pos.Y));
        prevSnapshotPos = pos;

        Vector3 pyr = p.ThingIndex >= 0 && p.ThingIndex < _level.Things.Count
            ? _level.Things[p.ThingIndex].PitchYawRoll
            : Vector3.Zero;

        RemotePlayerAnimFlags animFlags = RemotePlayerAnimFlags.None;
        if (p.Inventory.IsDead) animFlags |= RemotePlayerAnimFlags.IsDead;
        if (distMoved > 0.002f) animFlags |= RemotePlayerAnimFlags.IsMoving;
        if (p.ActiveFireMode >= 0 || p.FireWait > 0f) animFlags |= RemotePlayerAnimFlags.IsFiring;
        if (p.IsUnderwater) animFlags |= RemotePlayerAnimFlags.IsUnderwater;
        if (!p.IsGrounded && !p.IsUnderwater) animFlags |= RemotePlayerAnimFlags.IsAirborne;
        bool isSaber = p.CurWeaponBin == PlayerInventory.BinLightsaber;
        if (isSaber && ((_sim.JkGetFlags(p.ThingIndex) & 0x85) != 0 || p.SaberDamageActive))
            animFlags |= RemotePlayerAnimFlags.SaberActive;
        if (p.PovKeyLoop) animFlags |= RemotePlayerAnimFlags.PovKeyLoop;

        return new NetPlayerSnapshot(
            p.ThingIndex,
            (byte)p.PlayerSlot,
            (byte)p.Team,
            p.Name,
            pos,
            pyr.X,
            pyr.Y,
            (byte)Math.Clamp((int)MathF.Ceiling(p.Inventory.Health), 0, 255),
            (byte)Math.Clamp(p.CurWeaponBin, 0, 255),
            animFlags,
            distMoved,
            (short)Math.Clamp(p.Kills, short.MinValue, short.MaxValue),
            (short)Math.Clamp(p.Killed, short.MinValue, short.MaxValue),
            (short)Math.Clamp(p.Score, short.MinValue, short.MaxValue),
            (byte)Math.Clamp(p.ModelIndex, 0, 255),
            (byte)Math.Clamp(p.SaberColorIndex, 0, 255));
    }

    private byte AllocateFreeSlot()
    {
        for (byte slot = 0; slot < _maxPlayers; slot++)
        {
            bool used = false;
            foreach (var c in _clients)
            {
                if (c.PlayerSlot == slot)
                {
                    used = true;
                    break;
                }
            }
            if (!used)
            {
                foreach (var b in _bots)
                {
                    if (b.PlayerSlot == slot)
                    {
                        used = true;
                        break;
                    }
                }
            }
            if (!used)
                return slot;
        }
        return (byte)(_clients.Count + _bots.Count);
    }

    private Vector3 GetPlayerPosition(PlayerState player) =>
        player.HasInputPosition
            ? player.WorldPosition
            : (player.ThingIndex >= 0 && player.ThingIndex < _level.Things.Count ? _level.Things[player.ThingIndex].Position : Vector3.Zero);

    private void HandleSimulationSound(string soundName, Vector3 position, float volume, bool isPositional)
    {
        if (!isPositional && _clients.Count == 0)
            return;

        EnqueueEvent(NetEventKind.SoundPos, position, volume, isPositional ? 1f : 0f, 0, soundName, string.Empty);
    }

    private void HandleLoopingSoundStarted(int channelId, int ownerThingId, string wavName, Vector3 position, float volume)
    {
        if (channelId <= 0 || string.IsNullOrWhiteSpace(wavName))
            return;

        _activeLoopChannels.Add(channelId);
        EnqueueEvent(NetEventKind.LoopSoundStart, position, volume, ownerThingId, channelId, wavName, string.Empty);
    }

    private void HandleLoopingSoundStopped(int channelId, float fadeOut)
    {
        if (channelId <= 0 || !_activeLoopChannels.Remove(channelId))
            return;

        EnqueueEvent(NetEventKind.LoopSoundStop, Vector3.Zero, fadeOut, 0f, channelId, string.Empty, string.Empty);
    }

    private void HandleSurfaceScroll(int surfaceId, Vector2 dir, float speed)
    {
        _activeSurfaceScrolls[surfaceId] = (dir, speed);
        if (_clients.Count > 0)
            EnqueueEvent(NetEventKind.SurfaceScroll, new Vector3(dir.X, dir.Y, speed), 0f, 0f, surfaceId, string.Empty, string.Empty);
    }

    private void HandleMaterialAnimChanged(string materialName, float fps)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            return;

        if (fps > 0f)
            _activeMaterialAnims[materialName] = fps;
        else
            _activeMaterialAnims.Remove(materialName);

        if (_clients.Count > 0)
            EnqueueEvent(NetEventKind.MaterialAnim, Vector3.Zero, fps, 0f, 0, materialName, string.Empty);
    }

    private void ReplaySurfaceAndMaterialAnimsToEvents()
    {
        foreach (var (surfaceId, scroll) in _activeSurfaceScrolls)
            EnqueueEvent(NetEventKind.SurfaceScroll, new Vector3(scroll.Dir.X, scroll.Dir.Y, scroll.Speed), 0f, 0f, surfaceId, string.Empty, string.Empty);

        foreach (var (matName, fps) in _activeMaterialAnims)
            EnqueueEvent(NetEventKind.MaterialAnim, Vector3.Zero, fps, 0f, 0, matName, string.Empty);
    }

    private void HandleVisualEffectSpawned(WorldSimulation.VisualEffect vfx)
    {
        switch (vfx.Kind)
        {
            case WorldSimulation.VisualEffectKind.ParticleRing:
                EnqueueEvent(
                    NetEventKind.ParticleRing,
                    vfx.Position,
                    vfx.ExpandRadius,
                    vfx.Duration,
                    NetCodec.PackRingMeta(vfx.ColorTint, vfx.PitchSpreadDeg, vfx.ParticleCount),
                    vfx.SpriteName,
                    vfx.MaterialName);
                break;

            case WorldSimulation.VisualEffectKind.BeamTrail:
                EnqueueEvent(
                    NetEventKind.BeamTrail,
                    vfx.Position,
                    vfx.TrailSegmentSize,
                    vfx.Duration,
                    NetCodec.PackBeamMeta(vfx.ColorTint, vfx.JitterRadius > 0.001f),
                    vfx.SpriteName,
                    vfx.MaterialName,
                    vfx.EndPosition);
                break;

            default:
                EnqueueEvent(
                    NetEventKind.ExplosionVfx,
                    vfx.Position,
                    vfx.Width,
                    vfx.Duration,
                    vfx.MaxLight > 0f ? 1 : 0,
                    vfx.SpriteName,
                    vfx.MaterialName);
                break;
        }
    }

    private void HandlePlayerKilled(int victimThingId, int attackerThingId, string victimName, string attackerName)
    {
        bool suicideOrWorld = attackerThingId < 0 || attackerThingId == victimThingId;
        EnqueueEvent(NetEventKind.KillFeed, Vector3.Zero, 0f, 0f, suicideOrWorld ? 1 : 0, attackerName, victimName);
    }

    private void HandlePlayerTeleportOrRespawn(int thingId, Vector3 pos, Vector3 pyr, int sector)
    {
        if (_botsByThing.TryGetValue(thingId, out var bot))
        {
            bot.Physics.Position = pos;
            bot.Physics.CurrentSectorIndex = sector;
            bot.Physics.Velocity = Vector3.Zero;
            bot.PrevSnapshotPos = pos;
            bot.PrevPrimaryHeld = false;
            bot.PrevSecondaryHeld = false;
            bot.PrevForceBin = 0;
            bot.DeadRespawnTimer = 0f;
            bot.Controller.ResetForRespawn(pos);
            return;
        }

        if (!_clientsByThing.TryGetValue(thingId, out var client))
            return;

        client.PendingTeleportSeq++;
        client.PendingTeleportPos = pos;
        client.PendingTeleportYaw = pyr.Y;
        client.PendingTeleportSector = sector;
        client.PrevSnapshotPos = pos;
        client.AckedVelocitySeq = client.PendingVelocitySeq;
        client.PendingVelocityOverride = Vector3.Zero;
        client.PrevPrimaryHeld = false;
        client.PrevSecondaryHeld = false;
        client.PrevForceBin = 0;
    }

    private void EnqueueEvent(
        NetEventKind kind,
        Vector3 position,
        float f0,
        float f1,
        int i0,
        string text0,
        string text1,
        Vector3 endPosition = default)
    {
        if (_recentEvents.Count >= MaxBufferedEvents)
            _recentEvents.RemoveAt(0);

        _recentEvents.Add(new NetWorldEvent(
            _nextEventSeq++,
            kind,
            position,
            f0,
            f1,
            i0,
            text0 ?? string.Empty,
            text1 ?? string.Empty,
            endPosition));
    }

    private static void ConfigureWindowsUdpSocket(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            const int SioUdpConnReset = -1744830452;
            udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;

            _sim.OnPlayerKilled -= HandlePlayerKilled;
            _sim.OnPlayerRespawned -= HandlePlayerTeleportOrRespawn;
            _sim.OnAnyPlayerTeleported -= HandlePlayerTeleportOrRespawn;
            _sim.OnVisualEffectSpawned -= HandleVisualEffectSpawned;
            _sim.OnLoopingSoundStarted -= HandleLoopingSoundStarted;
            _sim.OnLoopingSoundStopped -= HandleLoopingSoundStopped;
            _sim.OnSurfaceScroll -= HandleSurfaceScroll;
            _sim.OnMaterialAnimChanged -= HandleMaterialAnimChanged;
            _udp.Dispose();
        }
    }
}
