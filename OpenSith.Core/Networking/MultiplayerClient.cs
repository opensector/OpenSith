using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OpenSith.Core.Data;

namespace OpenSith.Core.Networking;

/// <summary>
/// Non-blocking managed UDP client communicating with <see cref="DedicatedGameServer"/>.
/// Designed for single-threaded game-loop polling (<see cref="PollSnapshots"/>) with
/// automatic sequence acknowledgement for reliable teleports, velocities, and events.
/// </summary>
public sealed class MultiplayerClient : IDisposable
{
    private readonly byte[] _sendBuffer = new byte[NetCodec.MaxPacketBytes];
    private UdpClient? _udp;
    private uint _nextInputSeq;
    private bool _disposed;

    public bool IsConnected { get; private set; }
    public ushort ClientId { get; private set; }
    public int PlayerThingId { get; private set; } = -1;
    public ServerWelcomePacket? Welcome { get; private set; }
    public ServerSnapshotPacket? LatestSnapshot { get; private set; }
    public uint LastTeleportSeq { get; private set; }
    public uint LastVelocitySeq { get; private set; }
    public uint LastEventSeq { get; private set; }
    public double SecondsSinceLastServerPacket { get; private set; }

    public async Task<ServerWelcomePacket> ConnectAsync(
        string host,
        int port,
        string playerName,
        int team = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        PlayerLoadoutConfig? initialLoadout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        Disconnect();

        var udp = new UdpClient();
        ConfigureWindowsUdpSocket(udp);
        udp.Connect(host, port);
        _udp = udp;

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        long nextRetryMs = 0;

        byte modelIdx = initialLoadout != null ? (byte)Math.Clamp(initialLoadout.ModelIndex, 0, 255) : (byte)0;
        byte saberIdx = initialLoadout != null ? (byte)Math.Clamp(initialLoadout.SaberColorIndex, 0, 255) : (byte)PlayerCustomizationCatalog.DefaultSaberColorIndex;
        byte jediRank = initialLoadout != null ? (byte)Math.Clamp(initialLoadout.JediRank, 0, PlayerLoadoutConfig.MaxJediRank) : (byte)0;
        NetLoadoutFlags loadoutFlags = initialLoadout?.GrantAllWeapons == true ? NetLoadoutFlags.GrantAllWeapons : NetLoadoutFlags.None;
        ulong packedRanks = initialLoadout != null ? NetCodec.PackForceRanks(initialLoadout.ForceRanks) : 0;

        var hello = new ClientHelloPacket(
            string.IsNullOrWhiteSpace(playerName) ? "Kyle" : playerName.Trim(),
            (byte)Math.Clamp(team, 0, 255),
            modelIdx,
            saberIdx,
            jediRank,
            loadoutFlags,
            packedRanks);

        try
        {
            while (sw.Elapsed < effectiveTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sw.ElapsedMilliseconds >= nextRetryMs)
                {
                    if (NetCodec.TryWriteClientHello(_sendBuffer, hello, out int len))
                        udp.Send(_sendBuffer, len);
                    nextRetryMs = sw.ElapsedMilliseconds + 250;
                }

                while (udp.Available > 0)
                {
                    byte[] data;
                    try
                    {
                        System.Net.IPEndPoint? ep = null;
                        data = udp.Receive(ref ep);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        continue;
                    }

                    if (!NetCodec.TryPeekMessageType(data, out NetMessageType msgType))
                        continue;

                    if (msgType == NetMessageType.ServerWelcome && NetCodec.TryReadServerWelcome(data, out var welcome))
                    {
                        Welcome = welcome;
                        ClientId = welcome.ClientId;
                        PlayerThingId = welcome.PlayerThingId;
                        LastTeleportSeq = 1;
                        LastVelocitySeq = 0;
                        LastEventSeq = 0;
                        SecondsSinceLastServerPacket = 0;
                        LatestSnapshot = null;
                        IsConnected = true;
                        return welcome;
                    }

                    if (msgType == NetMessageType.ServerReject && NetCodec.TryReadServerReject(data, out var reject))
                    {
                        throw new InvalidOperationException($"Server rejected connection: {reject.Reason}");
                    }
                }

                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out connecting to {host}:{port} after {effectiveTimeout.TotalSeconds:F1}s.");
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public void SendInput(ClientInputPacket input)
    {
        if (!IsConnected || _udp == null)
            return;

        input.ClientId = ClientId;
        input.InputSeq = ++_nextInputSeq;
        input.AckTeleportSeq = LastTeleportSeq;
        input.AckVelocitySeq = LastVelocitySeq;
        input.AckEventSeq = LastEventSeq;

        if (!NetCodec.TryWriteClientInput(_sendBuffer, input, out int len))
            return;

        try
        {
            _udp.Send(_sendBuffer, len);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void SendConfig(PlayerLoadoutConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsConnected || _udp == null)
            return;

        var pkt = new ClientConfigPacket(
            ClientId,
            (byte)Math.Clamp(config.ModelIndex, 0, 255),
            (byte)Math.Clamp(config.SaberColorIndex, 0, 255),
            (byte)Math.Clamp(config.JediRank, 0, PlayerLoadoutConfig.MaxJediRank),
            config.GrantAllWeapons ? NetLoadoutFlags.GrantAllWeapons : NetLoadoutFlags.None,
            NetCodec.PackForceRanks(config.ForceRanks));

        if (!NetCodec.TryWriteClientConfig(_sendBuffer, pkt, out int len))
            return;

        try
        {
            _udp.Send(_sendBuffer, len);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool PollSnapshots(List<NetWorldEvent>? newEvents = null, float elapsedSeconds = 0f) =>
        PollSnapshots(newEvents, elapsedSeconds, out _);

    public bool PollSnapshots(
        List<NetWorldEvent>? newEvents,
        float elapsedSeconds,
        out Vector3 accumulatedPlatformDelta)
    {
        accumulatedPlatformDelta = Vector3.Zero;
        if (!IsConnected || _udp == null)
            return false;

        SecondsSinceLastServerPacket += Math.Max(0f, elapsedSeconds);
        bool updated = false;

        while (true)
        {
            byte[] data;
            try
            {
                if (_udp.Available <= 0)
                    break;
                System.Net.IPEndPoint? ep = null;
                data = _udp.Receive(ref ep);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!NetCodec.TryPeekMessageType(data, out NetMessageType msgType))
                continue;

            if (msgType == NetMessageType.ServerReject && NetCodec.TryReadServerReject(data, out _))
            {
                Disconnect();
                break;
            }

            if (msgType != NetMessageType.ServerSnapshot || !NetCodec.TryReadServerSnapshot(data, out var snap))
                continue;

            SecondsSinceLastServerPacket = 0;

            foreach (var evt in snap.Events)
            {
                if (evt.EventSeq > LastEventSeq)
                {
                    LastEventSeq = evt.EventSeq;
                    newEvents?.Add(evt);
                }
            }

            if (LatestSnapshot == null || snap.ServerTick > LatestSnapshot.ServerTick)
            {
                LatestSnapshot = snap;
                updated = true;

                if ((snap.Local.Flags & LocalPlayerSnapFlags.HasTeleport) != 0 &&
                    snap.Local.TeleportSeq > LastTeleportSeq)
                {
                    LastTeleportSeq = snap.Local.TeleportSeq;
                    accumulatedPlatformDelta = Vector3.Zero;
                }

                if ((snap.Local.Flags & LocalPlayerSnapFlags.IsDead) == 0 &&
                    (snap.Local.Flags & LocalPlayerSnapFlags.HasPlatformDelta) != 0)
                {
                    accumulatedPlatformDelta += snap.Local.PlatformDelta;
                }

                if ((snap.Local.Flags & LocalPlayerSnapFlags.HasVelocityOverride) != 0 &&
                    snap.Local.VelocitySeq > LastVelocitySeq)
                {
                    LastVelocitySeq = snap.Local.VelocitySeq;
                }
            }
        }

        return updated;
    }

    public void Disconnect()
    {
        if (_udp != null)
        {
            if (IsConnected && ClientId != 0 &&
                NetCodec.TryWriteClientDisconnect(_sendBuffer, ClientId, out int len))
            {
                try
                {
                    _udp.Send(_sendBuffer, len);
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _udp.Dispose();
            _udp = null;
        }

        IsConnected = false;
        ClientId = 0;
        PlayerThingId = -1;
        Welcome = null;
        LatestSnapshot = null;
        LastTeleportSeq = 0;
        LastVelocitySeq = 0;
        LastEventSeq = 0;
        SecondsSinceLastServerPacket = 0;
        _nextInputSeq = 0;
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
        if (_disposed)
            return;
        _disposed = true;
        Disconnect();
    }
}
