using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using OpenSith.Core.Data;

namespace OpenSith.Core.Networking;

public enum NetMessageType : byte
{
    ClientHello = 1,
    ServerWelcome = 2,
    ClientDisconnect = 3,
    ClientInput = 4,
    ServerSnapshot = 5,
    ServerReject = 6,
    ClientConfig = 7
}

[Flags]
public enum ClientInputFlags : ushort
{
    None = 0,
    IsGrounded = 1 << 0,
    IsUnderwater = 1 << 1,
    PrimaryFireHeld = 1 << 2,
    SecondaryFireHeld = 1 << 3,
    UsePressed = 1 << 4,
    RespawnPressed = 1 << 5
}

[Flags]
public enum LocalPlayerSnapFlags : byte
{
    None = 0,
    IsDead = 1 << 0,
    FieldLightOn = 1 << 1,
    IsFrozen = 1 << 2,
    HasTeleport = 1 << 3,
    HasVelocityOverride = 1 << 4,
    HasPlatformDelta = 1 << 5,
    HasScreenTint = 1 << 6,
    SaberDamageActive = 1 << 7
}

[Flags]
public enum RemotePlayerAnimFlags : byte
{
    None = 0,
    IsDead = 1 << 0,
    IsMoving = 1 << 1,
    IsFiring = 1 << 2,
    IsUnderwater = 1 << 3,
    SaberActive = 1 << 4,
    PovKeyLoop = 1 << 5,
    IsAirborne = 1 << 6
}

public enum NetEventKind : byte
{
    ExplosionVfx = 1,
    SoundPos = 2,
    KillFeed = 3,
    StatusMessage = 4,
    ParticleRing = 5,
    BeamTrail = 6,
    LoopSoundStart = 7,
    LoopSoundStop = 8,
    SurfaceScroll = 9,
    MaterialAnim = 10,
    ThingPlayKey = 11
}

[Flags]
public enum NetLoadoutFlags : byte
{
    None = 0,
    GrantAllWeapons = 1 << 0
}

public sealed record ClientHelloPacket(
    string PlayerName,
    byte Team,
    byte ModelIndex = 0,
    byte SaberColorIndex = PlayerCustomizationCatalog.DefaultSaberColorIndex,
    byte JediRank = 0,
    NetLoadoutFlags LoadoutFlags = NetLoadoutFlags.None,
    ulong PackedForceRanks = 0);

public sealed record ClientConfigPacket(
    ushort ClientId,
    byte ModelIndex,
    byte SaberColorIndex,
    byte JediRank,
    NetLoadoutFlags LoadoutFlags,
    ulong PackedForceRanks);

public sealed record ServerWelcomePacket(
    ushort ClientId,
    int PlayerThingId,
    byte TickRateHz,
    string EpisodeGob,
    string LevelFile,
    Vector3 SpawnPosition,
    float SpawnYaw,
    int SpawnSector);

public sealed record ServerRejectPacket(string Reason);

public sealed class ClientInputPacket
{
    public ushort ClientId { get; set; }
    public uint InputSeq { get; set; }
    public ClientInputFlags Flags { get; set; }
    public Vector3 Position { get; set; }
    public int Sector { get; set; }
    public Vector3 AimDirection { get; set; } = Vector3.UnitY;
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public Vector3 Thrust { get; set; }
    public byte ActionSeq { get; set; }
    public byte RequestedWeaponBin { get; set; }
    public byte RequestedItemBin { get; set; }
    public byte ActiveForceBin { get; set; }
    public uint AckTeleportSeq { get; set; }
    public uint AckVelocitySeq { get; set; }
    public uint AckEventSeq { get; set; }
}

public sealed class NetLocalPlayerState
{
    public float Health { get; set; }
    public float MaxHealth { get; set; } = 100f;
    public float Shields { get; set; }
    public short EnergyAmmo { get; set; }
    public short PowerAmmo { get; set; }
    public short Battery { get; set; }
    public short ForceMana { get; set; }
    public short MaxForceMana { get; set; }
    public short ThermDetAmmo { get; set; }
    public short RailAmmo { get; set; }
    public short SeqAmmo { get; set; }
    public byte CurWeaponBin { get; set; }
    public byte SelectedItemBin { get; set; }
    public byte SelectedForceBin { get; set; }
    public float ExtraSpeed { get; set; }
    public LocalPlayerSnapFlags Flags { get; set; }
    public uint TeleportSeq { get; set; }
    public Vector3 TeleportPosition { get; set; }
    public float TeleportYaw { get; set; }
    public int TeleportSector { get; set; }
    public uint VelocitySeq { get; set; }
    public Vector3 VelocityOverride { get; set; }
    public Vector3 PlatformDelta { get; set; }
    public Vector3 ScreenTint { get; set; }
    public string PovModel { get; set; } = "bryv.3do";
    public string PovKey { get; set; } = "bryvpst1.key";
    public float PovKeyTime { get; set; }
    public Vector3 PovShakePos { get; set; }
    public byte ModelIndex { get; set; }
    public byte SaberColorIndex { get; set; } = PlayerCustomizationCatalog.DefaultSaberColorIndex;
    public byte JediRank { get; set; }
    public NetLoadoutFlags LoadoutFlags { get; set; }
    public ulong PackedForceRanks { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

public readonly record struct NetPlayerSnapshot(
    int ThingIndex,
    byte PlayerSlot,
    byte Team,
    string Name,
    Vector3 Position,
    float Pitch,
    float Yaw,
    byte Health,
    byte WeaponBin,
    RemotePlayerAnimFlags AnimFlags,
    float DistanceMoved,
    short Kills,
    short Deaths,
    short Score,
    byte ModelIndex = 0,
    byte SaberColorIndex = PlayerCustomizationCatalog.DefaultSaberColorIndex);

public readonly record struct NetProjectileSnapshot(
    Vector3 Position,
    Vector3 Velocity,
    uint BoltColorRgba,
    string ModelName);

public readonly record struct NetDynamicThingSnapshot(
    int ThingIndex,
    Vector3 Position,
    Vector3 PitchYawRoll,
    uint ThingFlags,
    int SectorIndex);

public readonly record struct NetWorldEvent(
    uint EventSeq,
    NetEventKind Kind,
    Vector3 Position,
    float FloatParam0,
    float FloatParam1,
    int IntParam0,
    string Text0,
    string Text1,
    Vector3 EndPosition = default);

public sealed class ServerSnapshotPacket
{
    public uint ServerTick { get; set; }
    public float LevelTime { get; set; }
    public int LocalThingIndex { get; set; } = -1;
    public NetLocalPlayerState Local { get; set; } = new();
    public List<NetPlayerSnapshot> Players { get; } = [];
    public List<NetProjectileSnapshot> Projectiles { get; } = [];
    public List<NetWorldEvent> Events { get; } = [];
    public List<NetDynamicThingSnapshot> DynamicThings { get; } = [];
}

/// <summary>
/// Pure managed binary UDP packet codec for OpenSith dedicated-server networking.
/// Uses explicit bounds checks and strict MTU truncation budgets so malformed or
/// oversized packets never throw or overflow.
/// </summary>
public static class NetCodec
{
    public const uint ProtocolMagic = 0x53495448u; // "SITH"
    public const ushort ProtocolVersion = 2;
    public const int DefaultPort = 27960;
    public const int MaxPacketBytes = 1350;

    public static ulong PackForceRanks(ReadOnlySpan<byte> ranks)
    {
        ulong packed = 0;
        int count = Math.Min(ranks.Length, PlayerLoadoutConfig.ForcePowerCount);
        for (int i = 0; i < count; i++)
        {
            ulong val = (ulong)Math.Clamp((int)ranks[i], 0, PlayerLoadoutConfig.MaxForcePowerRank);
            packed |= val << (i * 3);
        }
        return packed;
    }

    public static void UnpackForceRanks(ulong packed, Span<byte> dst)
    {
        int count = Math.Min(dst.Length, PlayerLoadoutConfig.ForcePowerCount);
        for (int i = 0; i < count; i++)
        {
            byte val = (byte)((packed >> (i * 3)) & 0x7UL);
            dst[i] = (byte)Math.Clamp((int)val, 0, PlayerLoadoutConfig.MaxForcePowerRank);
        }
    }

    public static PlayerLoadoutConfig DecodeLoadout(
        byte modelIndex,
        byte saberColorIndex,
        byte jediRank,
        NetLoadoutFlags flags,
        ulong packedForceRanks)
    {
        var cfg = new PlayerLoadoutConfig
        {
            ModelIndex = modelIndex,
            SaberColorIndex = saberColorIndex,
            JediRank = Math.Clamp((int)jediRank, 0, PlayerLoadoutConfig.MaxJediRank),
            GrantAllWeapons = (flags & NetLoadoutFlags.GrantAllWeapons) != 0
        };
        UnpackForceRanks(packedForceRanks, cfg.ForceRanks);
        return cfg;
    }

    public static int PackRingMeta(Vector3 rgb, float pitchSpreadDeg, int particleCount)
    {
        uint r = (uint)Math.Clamp((int)(rgb.X * 255f), 0, 255);
        uint g = (uint)Math.Clamp((int)(rgb.Y * 255f), 0, 255);
        uint b = (uint)Math.Clamp((int)(rgb.Z * 255f), 0, 255);
        uint cnt = (uint)Math.Clamp(particleCount, 1, 127);
        uint flat = pitchSpreadDeg <= 15f ? 0x80u : 0u;
        return unchecked((int)(r | (g << 8) | (b << 16) | ((flat | cnt) << 24)));
    }

    public static (Vector3 ColorTint, float PitchSpreadDeg, int ParticleCount) UnpackRingMeta(int packed)
    {
        uint u = unchecked((uint)packed);
        float r = (u & 0xFF) / 255f;
        float g = ((u >> 8) & 0xFF) / 255f;
        float b = ((u >> 16) & 0xFF) / 255f;
        uint top = (u >> 24) & 0xFF;
        bool flat = (top & 0x80) != 0;
        int count = Math.Clamp((int)(top & 0x7F), 16, 96);
        return (new Vector3(r, g, b), flat ? 5f : 360f, count);
    }

    public static int PackBeamMeta(Vector3 rgb, bool isLightning)
    {
        uint r = (uint)Math.Clamp((int)(rgb.X * 255f), 0, 255);
        uint g = (uint)Math.Clamp((int)(rgb.Y * 255f), 0, 255);
        uint b = (uint)Math.Clamp((int)(rgb.Z * 255f), 0, 255);
        uint lit = isLightning ? 1u : 0u;
        return unchecked((int)(r | (g << 8) | (b << 16) | (lit << 24)));
    }

    public static (Vector3 ColorTint, bool IsLightning) UnpackBeamMeta(int packed)
    {
        uint u = unchecked((uint)packed);
        float r = (u & 0xFF) / 255f;
        float g = ((u >> 8) & 0xFF) / 255f;
        float b = ((u >> 16) & 0xFF) / 255f;
        bool lit = ((u >> 24) & 1u) != 0;
        return (new Vector3(r, g, b), lit);
    }

    public const int MaxPlayersPerSnapshot = 16;
    public const int MaxProjectilesPerSnapshot = 48;
    public const int MaxEventsPerSnapshot = 16;
    public const int MaxDynamicThingsPerSnapshot = 48;

    private const int HeaderBytes = 7; // magic(4) + version(2) + type(1)

    public static bool TryPeekMessageType(ReadOnlySpan<byte> data, out NetMessageType messageType)
    {
        messageType = default;
        if (data.Length < HeaderBytes || data.Length > MaxPacketBytes)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != ProtocolMagic)
            return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2)) != ProtocolVersion)
            return false;

        messageType = (NetMessageType)data[6];
        return Enum.IsDefined(messageType);
    }

    public static bool TryWriteClientHello(Span<byte> dst, in ClientHelloPacket pkt, out int bytesWritten)
    {
        var w = new Writer(dst);
        if (!WriteHeader(ref w, NetMessageType.ClientHello) ||
            !w.TryWriteString(pkt.PlayerName, 32) ||
            !w.TryWriteByte(pkt.Team) ||
            !w.TryWriteByte(pkt.ModelIndex) ||
            !w.TryWriteByte(pkt.SaberColorIndex) ||
            !w.TryWriteByte(pkt.JediRank) ||
            !w.TryWriteByte((byte)pkt.LoadoutFlags) ||
            !w.TryWriteUInt64(pkt.PackedForceRanks))
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = w.Position;
        return true;
    }

    public static bool TryReadClientHello(ReadOnlySpan<byte> src, out ClientHelloPacket pkt)
    {
        pkt = new ClientHelloPacket(string.Empty, 0);
        var r = new Reader(src);
        if (!ReadHeader(ref r, NetMessageType.ClientHello) ||
            !r.TryReadString(out string name, 32) ||
            !r.TryReadByte(out byte team) ||
            !r.TryReadByte(out byte modelIdx) ||
            !r.TryReadByte(out byte saberIdx) ||
            !r.TryReadByte(out byte jediRank) ||
            !r.TryReadByte(out byte loadoutFlags) ||
            !r.TryReadUInt64(out ulong packedRanks))
        {
            return false;
        }
        pkt = new ClientHelloPacket(name, team, modelIdx, saberIdx, jediRank, (NetLoadoutFlags)loadoutFlags, packedRanks);
        return true;
    }

    public static bool TryWriteClientConfig(Span<byte> dst, in ClientConfigPacket pkt, out int bytesWritten)
    {
        var w = new Writer(dst);
        if (!WriteHeader(ref w, NetMessageType.ClientConfig) ||
            !w.TryWriteUInt16(pkt.ClientId) ||
            !w.TryWriteByte(pkt.ModelIndex) ||
            !w.TryWriteByte(pkt.SaberColorIndex) ||
            !w.TryWriteByte(pkt.JediRank) ||
            !w.TryWriteByte((byte)pkt.LoadoutFlags) ||
            !w.TryWriteUInt64(pkt.PackedForceRanks))
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = w.Position;
        return true;
    }

    public static bool TryReadClientConfig(ReadOnlySpan<byte> src, out ClientConfigPacket pkt)
    {
        pkt = new ClientConfigPacket(0, 0, PlayerCustomizationCatalog.DefaultSaberColorIndex, 0, NetLoadoutFlags.None, 0);
        var r = new Reader(src);
        if (!ReadHeader(ref r, NetMessageType.ClientConfig) ||
            !r.TryReadUInt16(out ushort clientId) ||
            !r.TryReadByte(out byte modelIdx) ||
            !r.TryReadByte(out byte saberIdx) ||
            !r.TryReadByte(out byte jediRank) ||
            !r.TryReadByte(out byte loadoutFlags) ||
            !r.TryReadUInt64(out ulong packedRanks))
        {
            return false;
        }
        pkt = new ClientConfigPacket(clientId, modelIdx, saberIdx, jediRank, (NetLoadoutFlags)loadoutFlags, packedRanks);
        return true;
    }

    public static bool TryWriteServerWelcome(Span<byte> dst, in ServerWelcomePacket pkt, out int bytesWritten)
    {
        var w = new Writer(dst);
        if (!WriteHeader(ref w, NetMessageType.ServerWelcome) ||
            !w.TryWriteUInt16(pkt.ClientId) ||
            !w.TryWriteInt32(pkt.PlayerThingId) ||
            !w.TryWriteByte(pkt.TickRateHz) ||
            !w.TryWriteString(pkt.EpisodeGob, 64) ||
            !w.TryWriteString(pkt.LevelFile, 64) ||
            !w.TryWriteVec3(pkt.SpawnPosition) ||
            !w.TryWriteSingle(pkt.SpawnYaw) ||
            !w.TryWriteInt32(pkt.SpawnSector))
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = w.Position;
        return true;
    }

    public static bool TryReadServerWelcome(ReadOnlySpan<byte> src, out ServerWelcomePacket pkt)
    {
        pkt = new ServerWelcomePacket(0, -1, 30, string.Empty, string.Empty, Vector3.Zero, 0f, 0);
        var r = new Reader(src);
        if (!ReadHeader(ref r, NetMessageType.ServerWelcome) ||
            !r.TryReadUInt16(out ushort clientId) ||
            !r.TryReadInt32(out int thingId) ||
            !r.TryReadByte(out byte tickHz) ||
            !r.TryReadString(out string episodeGob, 64) ||
            !r.TryReadString(out string levelFile, 64) ||
            !r.TryReadVec3(out Vector3 spawnPos) ||
            !r.TryReadFiniteSingle(out float spawnYaw) ||
            !r.TryReadInt32(out int spawnSector))
        {
            return false;
        }
        pkt = new ServerWelcomePacket(clientId, thingId, tickHz, episodeGob, levelFile, spawnPos, spawnYaw, spawnSector);
        return true;
    }

    public static bool TryWriteServerReject(Span<byte> dst, in ServerRejectPacket pkt, out int bytesWritten)
    {
        var w = new Writer(dst);
        if (!WriteHeader(ref w, NetMessageType.ServerReject) ||
            !w.TryWriteString(pkt.Reason, 96))
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = w.Position;
        return true;
    }

    public static bool TryReadServerReject(ReadOnlySpan<byte> src, out ServerRejectPacket pkt)
    {
        pkt = new ServerRejectPacket(string.Empty);
        var r = new Reader(src);
        if (!ReadHeader(ref r, NetMessageType.ServerReject) ||
            !r.TryReadString(out string reason, 96))
        {
            return false;
        }
        pkt = new ServerRejectPacket(reason);
        return true;
    }

    public static bool TryWriteClientDisconnect(Span<byte> dst, ushort clientId, out int bytesWritten)
    {
        var w = new Writer(dst);
        if (!WriteHeader(ref w, NetMessageType.ClientDisconnect) ||
            !w.TryWriteUInt16(clientId))
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = w.Position;
        return true;
    }

    public static bool TryReadClientDisconnect(ReadOnlySpan<byte> src, out ushort clientId)
    {
        clientId = 0;
        var r = new Reader(src);
        return ReadHeader(ref r, NetMessageType.ClientDisconnect) &&
               r.TryReadUInt16(out clientId);
    }

    public static bool TryWriteClientInput(Span<byte> dst, ClientInputPacket pkt, out int bytesWritten)
    {
        var w = new Writer(dst);
        if (!WriteHeader(ref w, NetMessageType.ClientInput) ||
            !w.TryWriteUInt16(pkt.ClientId) ||
            !w.TryWriteUInt32(pkt.InputSeq) ||
            !w.TryWriteUInt16((ushort)pkt.Flags) ||
            !w.TryWriteVec3(pkt.Position) ||
            !w.TryWriteInt32(pkt.Sector) ||
            !w.TryWriteVec3(pkt.AimDirection) ||
            !w.TryWriteSingle(pkt.Pitch) ||
            !w.TryWriteSingle(pkt.Yaw) ||
            !w.TryWriteVec3(pkt.Thrust) ||
            !w.TryWriteByte(pkt.ActionSeq) ||
            !w.TryWriteByte(pkt.RequestedWeaponBin) ||
            !w.TryWriteByte(pkt.RequestedItemBin) ||
            !w.TryWriteByte(pkt.ActiveForceBin) ||
            !w.TryWriteUInt32(pkt.AckTeleportSeq) ||
            !w.TryWriteUInt32(pkt.AckVelocitySeq) ||
            !w.TryWriteUInt32(pkt.AckEventSeq))
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = w.Position;
        return true;
    }

    public static bool TryReadClientInput(ReadOnlySpan<byte> src, out ClientInputPacket pkt)
    {
        pkt = new ClientInputPacket();
        var r = new Reader(src);
        if (!ReadHeader(ref r, NetMessageType.ClientInput) ||
            !r.TryReadUInt16(out ushort clientId) ||
            !r.TryReadUInt32(out uint inputSeq) ||
            !r.TryReadUInt16(out ushort flags) ||
            !r.TryReadVec3(out Vector3 pos) ||
            !r.TryReadInt32(out int sector) ||
            !r.TryReadVec3(out Vector3 aimDir) ||
            !r.TryReadFiniteSingle(out float pitch) ||
            !r.TryReadFiniteSingle(out float yaw) ||
            !r.TryReadVec3(out Vector3 thrust) ||
            !r.TryReadByte(out byte actionSeq) ||
            !r.TryReadByte(out byte reqWeapon) ||
            !r.TryReadByte(out byte reqItem) ||
            !r.TryReadByte(out byte activeForce) ||
            !r.TryReadUInt32(out uint ackTp) ||
            !r.TryReadUInt32(out uint ackVel) ||
            !r.TryReadUInt32(out uint ackEvt))
        {
            return false;
        }

        pkt.ClientId = clientId;
        pkt.InputSeq = inputSeq;
        pkt.Flags = (ClientInputFlags)flags;
        pkt.Position = pos;
        pkt.Sector = sector;
        pkt.AimDirection = aimDir;
        pkt.Pitch = pitch;
        pkt.Yaw = yaw;
        pkt.Thrust = thrust;
        pkt.ActionSeq = actionSeq;
        pkt.RequestedWeaponBin = reqWeapon;
        pkt.RequestedItemBin = reqItem;
        pkt.ActiveForceBin = activeForce;
        pkt.AckTeleportSeq = ackTp;
        pkt.AckVelocitySeq = ackVel;
        pkt.AckEventSeq = ackEvt;
        return true;
    }

    public static bool TryWriteServerSnapshot(Span<byte> dst, ServerSnapshotPacket pkt, out int bytesWritten)
    {
        var w = new Writer(dst);
        if (!WriteHeader(ref w, NetMessageType.ServerSnapshot) ||
            !w.TryWriteUInt32(pkt.ServerTick) ||
            !w.TryWriteSingle(pkt.LevelTime) ||
            !w.TryWriteInt32(pkt.LocalThingIndex) ||
            !TryWriteLocalState(ref w, pkt.Local))
        {
            bytesWritten = 0;
            return false;
        }

        // Write players up to cap + reserve 3 bytes for trailing section counts
        int playerCountPos = w.Position;
        if (!w.TryWriteByte(0))
        {
            bytesWritten = 0;
            return false;
        }
        byte writtenPlayers = 0;
        int maxPlayers = Math.Min(pkt.Players.Count, MaxPlayersPerSnapshot);
        for (int i = 0; i < maxPlayers; i++)
        {
            int cp = w.Position;
            if (!TryWritePlayer(ref w, pkt.Players[i]) || w.Remaining < 3)
            {
                w.Rewind(cp);
                break;
            }
            writtenPlayers++;
        }
        w.OverwriteByte(playerCountPos, writtenPlayers);

        // Write events next (reliability/important gameplay cues) while reserving 2 bytes for remaining counts
        int eventCountPos = w.Position;
        if (!w.TryWriteByte(0))
        {
            bytesWritten = 0;
            return false;
        }
        byte writtenEvents = 0;
        int maxEvents = Math.Min(pkt.Events.Count, MaxEventsPerSnapshot);
        for (int i = 0; i < maxEvents; i++)
        {
            int cp = w.Position;
            if (!TryWriteEvent(ref w, pkt.Events[i]) || w.Remaining < 2)
            {
                w.Rewind(cp);
                break;
            }
            writtenEvents++;
        }
        w.OverwriteByte(eventCountPos, writtenEvents);

        // Write projectiles while reserving 1 byte for DynamicThingCount
        int projCountPos = w.Position;
        if (!w.TryWriteByte(0))
        {
            bytesWritten = 0;
            return false;
        }
        byte writtenProjs = 0;
        int maxProjs = Math.Min(pkt.Projectiles.Count, MaxProjectilesPerSnapshot);
        for (int i = 0; i < maxProjs; i++)
        {
            int cp = w.Position;
            if (!TryWriteProjectile(ref w, pkt.Projectiles[i]) || w.Remaining < 1)
            {
                w.Rewind(cp);
                break;
            }
            writtenProjs++;
        }
        w.OverwriteByte(projCountPos, writtenProjs);

        // Write dynamic things with remaining MTU budget
        int dynCountPos = w.Position;
        if (!w.TryWriteByte(0))
        {
            bytesWritten = 0;
            return false;
        }
        byte writtenDyns = 0;
        int maxDyns = Math.Min(pkt.DynamicThings.Count, MaxDynamicThingsPerSnapshot);
        for (int i = 0; i < maxDyns; i++)
        {
            int cp = w.Position;
            if (!TryWriteDynamicThing(ref w, pkt.DynamicThings[i]))
            {
                w.Rewind(cp);
                break;
            }
            writtenDyns++;
        }
        w.OverwriteByte(dynCountPos, writtenDyns);

        bytesWritten = w.Position;
        return bytesWritten <= MaxPacketBytes;
    }

    public static bool TryReadServerSnapshot(ReadOnlySpan<byte> src, out ServerSnapshotPacket pkt)
    {
        pkt = new ServerSnapshotPacket();
        var r = new Reader(src);
        if (!ReadHeader(ref r, NetMessageType.ServerSnapshot) ||
            !r.TryReadUInt32(out uint tick) ||
            !r.TryReadFiniteSingle(out float levelTime) ||
            !r.TryReadInt32(out int localThingIdx) ||
            !TryReadLocalState(ref r, pkt.Local))
        {
            return false;
        }

        pkt.ServerTick = tick;
        pkt.LevelTime = levelTime;
        pkt.LocalThingIndex = localThingIdx;

        if (!r.TryReadByte(out byte playerCount) || playerCount > MaxPlayersPerSnapshot)
            return false;
        for (int i = 0; i < playerCount; i++)
        {
            if (!TryReadPlayer(ref r, out var p))
                return false;
            pkt.Players.Add(p);
        }

        if (!r.TryReadByte(out byte eventCount) || eventCount > MaxEventsPerSnapshot)
            return false;
        for (int i = 0; i < eventCount; i++)
        {
            if (!TryReadEvent(ref r, out var e))
                return false;
            pkt.Events.Add(e);
        }

        if (!r.TryReadByte(out byte projCount) || projCount > MaxProjectilesPerSnapshot)
            return false;
        for (int i = 0; i < projCount; i++)
        {
            if (!TryReadProjectile(ref r, out var pr))
                return false;
            pkt.Projectiles.Add(pr);
        }

        if (!r.TryReadByte(out byte dynCount) || dynCount > MaxDynamicThingsPerSnapshot)
            return false;
        for (int i = 0; i < dynCount; i++)
        {
            if (!TryReadDynamicThing(ref r, out var dt))
                return false;
            pkt.DynamicThings.Add(dt);
        }

        return true;
    }

    private static bool TryWriteLocalState(ref Writer w, NetLocalPlayerState s)
    {
        if (!w.TryWriteSingle(s.Health) ||
            !w.TryWriteSingle(s.MaxHealth) ||
            !w.TryWriteSingle(s.Shields) ||
            !w.TryWriteInt16(s.EnergyAmmo) ||
            !w.TryWriteInt16(s.PowerAmmo) ||
            !w.TryWriteInt16(s.Battery) ||
            !w.TryWriteInt16(s.ForceMana) ||
            !w.TryWriteInt16(s.MaxForceMana) ||
            !w.TryWriteInt16(s.ThermDetAmmo) ||
            !w.TryWriteInt16(s.RailAmmo) ||
            !w.TryWriteInt16(s.SeqAmmo) ||
            !w.TryWriteByte(s.CurWeaponBin) ||
            !w.TryWriteByte(s.SelectedItemBin) ||
            !w.TryWriteByte(s.SelectedForceBin) ||
            !w.TryWriteSingle(s.ExtraSpeed) ||
            !w.TryWriteByte((byte)s.Flags) ||
            !w.TryWriteString(s.PovModel, 32) ||
            !w.TryWriteString(s.PovKey, 32) ||
            !w.TryWriteSingle(s.PovKeyTime) ||
            !w.TryWriteVec3(s.PovShakePos) ||
            !w.TryWriteByte(s.ModelIndex) ||
            !w.TryWriteByte(s.SaberColorIndex) ||
            !w.TryWriteByte(s.JediRank) ||
            !w.TryWriteByte((byte)s.LoadoutFlags) ||
            !w.TryWriteUInt64(s.PackedForceRanks) ||
            !w.TryWriteString(s.StatusMessage, 64))
        {
            return false;
        }

        if ((s.Flags & LocalPlayerSnapFlags.HasTeleport) != 0)
        {
            if (!w.TryWriteUInt32(s.TeleportSeq) ||
                !w.TryWriteVec3(s.TeleportPosition) ||
                !w.TryWriteSingle(s.TeleportYaw) ||
                !w.TryWriteInt32(s.TeleportSector))
            {
                return false;
            }
        }

        if ((s.Flags & LocalPlayerSnapFlags.HasVelocityOverride) != 0)
        {
            if (!w.TryWriteUInt32(s.VelocitySeq) || !w.TryWriteVec3(s.VelocityOverride))
                return false;
        }

        if ((s.Flags & LocalPlayerSnapFlags.HasPlatformDelta) != 0)
        {
            if (!w.TryWriteVec3(s.PlatformDelta))
                return false;
        }

        if ((s.Flags & LocalPlayerSnapFlags.HasScreenTint) != 0)
        {
            if (!w.TryWriteVec3(s.ScreenTint))
                return false;
        }

        return true;
    }

    private static bool TryReadLocalState(ref Reader r, NetLocalPlayerState s)
    {
        if (!r.TryReadFiniteSingle(out float health) ||
            !r.TryReadFiniteSingle(out float maxHealth) ||
            !r.TryReadFiniteSingle(out float shields) ||
            !r.TryReadInt16(out short energy) ||
            !r.TryReadInt16(out short power) ||
            !r.TryReadInt16(out short battery) ||
            !r.TryReadInt16(out short mana) ||
            !r.TryReadInt16(out short maxMana) ||
            !r.TryReadInt16(out short therm) ||
            !r.TryReadInt16(out short rail) ||
            !r.TryReadInt16(out short seq) ||
            !r.TryReadByte(out byte curWeap) ||
            !r.TryReadByte(out byte selItem) ||
            !r.TryReadByte(out byte selForce) ||
            !r.TryReadFiniteSingle(out float extraSpeed) ||
            !r.TryReadByte(out byte flagsByte) ||
            !r.TryReadString(out string povModel, 32) ||
            !r.TryReadString(out string povKey, 32) ||
            !r.TryReadFiniteSingle(out float povTime) ||
            !r.TryReadVec3(out Vector3 shakePos) ||
            !r.TryReadByte(out byte modelIdx) ||
            !r.TryReadByte(out byte saberIdx) ||
            !r.TryReadByte(out byte jediRank) ||
            !r.TryReadByte(out byte loadoutFlags) ||
            !r.TryReadUInt64(out ulong packedRanks) ||
            !r.TryReadString(out string statusMsg, 64))
        {
            return false;
        }

        s.Health = health;
        s.MaxHealth = maxHealth;
        s.Shields = shields;
        s.EnergyAmmo = energy;
        s.PowerAmmo = power;
        s.Battery = battery;
        s.ForceMana = mana;
        s.MaxForceMana = maxMana;
        s.ThermDetAmmo = therm;
        s.RailAmmo = rail;
        s.SeqAmmo = seq;
        s.CurWeaponBin = curWeap;
        s.SelectedItemBin = selItem;
        s.SelectedForceBin = selForce;
        s.ExtraSpeed = extraSpeed;
        s.Flags = (LocalPlayerSnapFlags)flagsByte;
        s.PovModel = povModel;
        s.PovKey = povKey;
        s.PovKeyTime = povTime;
        s.PovShakePos = shakePos;
        s.ModelIndex = modelIdx;
        s.SaberColorIndex = saberIdx;
        s.JediRank = jediRank;
        s.LoadoutFlags = (NetLoadoutFlags)loadoutFlags;
        s.PackedForceRanks = packedRanks;
        s.StatusMessage = statusMsg;

        if ((s.Flags & LocalPlayerSnapFlags.HasTeleport) != 0)
        {
            if (!r.TryReadUInt32(out uint tpSeq) ||
                !r.TryReadVec3(out Vector3 tpPos) ||
                !r.TryReadFiniteSingle(out float tpYaw) ||
                !r.TryReadInt32(out int tpSec))
            {
                return false;
            }
            s.TeleportSeq = tpSeq;
            s.TeleportPosition = tpPos;
            s.TeleportYaw = tpYaw;
            s.TeleportSector = tpSec;
        }

        if ((s.Flags & LocalPlayerSnapFlags.HasVelocityOverride) != 0)
        {
            if (!r.TryReadUInt32(out uint velSeq) || !r.TryReadVec3(out Vector3 vel))
                return false;
            s.VelocitySeq = velSeq;
            s.VelocityOverride = vel;
        }

        if ((s.Flags & LocalPlayerSnapFlags.HasPlatformDelta) != 0)
        {
            if (!r.TryReadVec3(out Vector3 delta))
                return false;
            s.PlatformDelta = delta;
        }

        if ((s.Flags & LocalPlayerSnapFlags.HasScreenTint) != 0)
        {
            if (!r.TryReadVec3(out Vector3 tint))
                return false;
            s.ScreenTint = tint;
        }

        return true;
    }

    private static bool TryWritePlayer(ref Writer w, in NetPlayerSnapshot p) =>
        w.TryWriteInt32(p.ThingIndex) &&
        w.TryWriteByte(p.PlayerSlot) &&
        w.TryWriteByte(p.Team) &&
        w.TryWriteString(p.Name, 32) &&
        w.TryWriteVec3(p.Position) &&
        w.TryWriteSingle(p.Pitch) &&
        w.TryWriteSingle(p.Yaw) &&
        w.TryWriteByte(p.Health) &&
        w.TryWriteByte(p.WeaponBin) &&
        w.TryWriteByte((byte)p.AnimFlags) &&
        w.TryWriteSingle(p.DistanceMoved) &&
        w.TryWriteInt16(p.Kills) &&
        w.TryWriteInt16(p.Deaths) &&
        w.TryWriteInt16(p.Score) &&
        w.TryWriteByte(p.ModelIndex) &&
        w.TryWriteByte(p.SaberColorIndex);

    private static bool TryReadPlayer(ref Reader r, out NetPlayerSnapshot p)
    {
        p = default;
        if (!r.TryReadInt32(out int thingIdx) ||
            !r.TryReadByte(out byte slot) ||
            !r.TryReadByte(out byte team) ||
            !r.TryReadString(out string name, 32) ||
            !r.TryReadVec3(out Vector3 pos) ||
            !r.TryReadFiniteSingle(out float pitch) ||
            !r.TryReadFiniteSingle(out float yaw) ||
            !r.TryReadByte(out byte health) ||
            !r.TryReadByte(out byte weapBin) ||
            !r.TryReadByte(out byte animFlags) ||
            !r.TryReadFiniteSingle(out float distMoved) ||
            !r.TryReadInt16(out short kills) ||
            !r.TryReadInt16(out short deaths) ||
            !r.TryReadInt16(out short score) ||
            !r.TryReadByte(out byte modelIdx) ||
            !r.TryReadByte(out byte saberIdx))
        {
            return false;
        }

        p = new NetPlayerSnapshot(thingIdx, slot, team, name, pos, pitch, yaw, health, weapBin,
            (RemotePlayerAnimFlags)animFlags, distMoved, kills, deaths, score, modelIdx, saberIdx);
        return true;
    }

    private static bool TryWriteEvent(ref Writer w, in NetWorldEvent e) =>
        w.TryWriteUInt32(e.EventSeq) &&
        w.TryWriteByte((byte)e.Kind) &&
        w.TryWriteVec3(e.Position) &&
        (e.Kind != NetEventKind.BeamTrail || w.TryWriteVec3(e.EndPosition)) &&
        w.TryWriteSingle(e.FloatParam0) &&
        w.TryWriteSingle(e.FloatParam1) &&
        w.TryWriteInt32(e.IntParam0) &&
        w.TryWriteString(e.Text0, 48) &&
        w.TryWriteString(e.Text1, 48);

    private static bool TryReadEvent(ref Reader r, out NetWorldEvent e)
    {
        e = default;
        if (!r.TryReadUInt32(out uint seq) ||
            !r.TryReadByte(out byte kindByte) ||
            !Enum.IsDefined((NetEventKind)kindByte) ||
            !r.TryReadVec3(out Vector3 pos))
        {
            return false;
        }

        NetEventKind kind = (NetEventKind)kindByte;
        Vector3 endPos = Vector3.Zero;
        if (kind == NetEventKind.BeamTrail && !r.TryReadVec3(out endPos))
            return false;

        if (!r.TryReadFiniteSingle(out float f0) ||
            !r.TryReadFiniteSingle(out float f1) ||
            !r.TryReadInt32(out int i0) ||
            !r.TryReadString(out string t0, 48) ||
            !r.TryReadString(out string t1, 48))
        {
            return false;
        }

        e = new NetWorldEvent(seq, kind, pos, f0, f1, i0, t0, t1, endPos);
        return true;
    }

    private static bool TryWriteProjectile(ref Writer w, in NetProjectileSnapshot pr) =>
        w.TryWriteVec3(pr.Position) &&
        w.TryWriteVec3(pr.Velocity) &&
        w.TryWriteUInt32(pr.BoltColorRgba) &&
        w.TryWriteString(pr.ModelName, 24);

    private static bool TryReadProjectile(ref Reader r, out NetProjectileSnapshot pr)
    {
        pr = default;
        if (!r.TryReadVec3(out Vector3 pos) ||
            !r.TryReadVec3(out Vector3 vel) ||
            !r.TryReadUInt32(out uint rgba) ||
            !r.TryReadString(out string model, 24))
        {
            return false;
        }

        pr = new NetProjectileSnapshot(pos, vel, rgba, model);
        return true;
    }

    private static bool TryWriteDynamicThing(ref Writer w, in NetDynamicThingSnapshot dt) =>
        w.TryWriteInt32(dt.ThingIndex) &&
        w.TryWriteVec3(dt.Position) &&
        w.TryWriteVec3(dt.PitchYawRoll) &&
        w.TryWriteUInt32(dt.ThingFlags) &&
        w.TryWriteInt32(dt.SectorIndex);

    private static bool TryReadDynamicThing(ref Reader r, out NetDynamicThingSnapshot dt)
    {
        dt = default;
        if (!r.TryReadInt32(out int idx) ||
            !r.TryReadVec3(out Vector3 pos) ||
            !r.TryReadVec3(out Vector3 pyr) ||
            !r.TryReadUInt32(out uint flags) ||
            !r.TryReadInt32(out int sector))
        {
            return false;
        }

        dt = new NetDynamicThingSnapshot(idx, pos, pyr, flags, sector);
        return true;
    }

    private static bool WriteHeader(ref Writer w, NetMessageType type) =>
        w.TryWriteUInt32(ProtocolMagic) &&
        w.TryWriteUInt16(ProtocolVersion) &&
        w.TryWriteByte((byte)type);

    private static bool ReadHeader(ref Reader r, NetMessageType expectedType) =>
        r.Length <= MaxPacketBytes &&
        r.TryReadUInt32(out uint magic) && magic == ProtocolMagic &&
        r.TryReadUInt16(out ushort ver) && ver == ProtocolVersion &&
        r.TryReadByte(out byte msg) && msg == (byte)expectedType;

    private ref struct Writer
    {
        private readonly Span<byte> _buf;
        private readonly int _limit;
        public int Position { get; private set; }
        public int Remaining => _limit - Position;

        public Writer(Span<byte> buf)
        {
            _buf = buf;
            _limit = Math.Min(buf.Length, MaxPacketBytes);
            Position = 0;
        }

        public void Rewind(int position) => Position = position;
        public void OverwriteByte(int position, byte value) => _buf[position] = value;

        public bool TryWriteByte(byte v)
        {
            if (Remaining < 1) return false;
            _buf[Position++] = v;
            return true;
        }

        public bool TryWriteInt16(short v)
        {
            if (Remaining < 2) return false;
            BinaryPrimitives.WriteInt16LittleEndian(_buf.Slice(Position, 2), v);
            Position += 2;
            return true;
        }

        public bool TryWriteUInt16(ushort v)
        {
            if (Remaining < 2) return false;
            BinaryPrimitives.WriteUInt16LittleEndian(_buf.Slice(Position, 2), v);
            Position += 2;
            return true;
        }

        public bool TryWriteInt32(int v)
        {
            if (Remaining < 4) return false;
            BinaryPrimitives.WriteInt32LittleEndian(_buf.Slice(Position, 4), v);
            Position += 4;
            return true;
        }

        public bool TryWriteUInt32(uint v)
        {
            if (Remaining < 4) return false;
            BinaryPrimitives.WriteUInt32LittleEndian(_buf.Slice(Position, 4), v);
            Position += 4;
            return true;
        }

        public bool TryWriteUInt64(ulong v)
        {
            if (Remaining < 8) return false;
            BinaryPrimitives.WriteUInt64LittleEndian(_buf.Slice(Position, 8), v);
            Position += 8;
            return true;
        }

        public bool TryWriteSingle(float v)
        {
            if (Remaining < 4) return false;
            BinaryPrimitives.WriteSingleLittleEndian(_buf.Slice(Position, 4), float.IsFinite(v) ? v : 0f);
            Position += 4;
            return true;
        }

        public bool TryWriteVec3(Vector3 v) =>
            TryWriteSingle(v.X) && TryWriteSingle(v.Y) && TryWriteSingle(v.Z);

        public bool TryWriteString(string? text, int maxUtf8Bytes)
        {
            string value = text ?? string.Empty;
            int maxByteCount = Math.Min(maxUtf8Bytes, 255);
            if (string.IsNullOrEmpty(value))
                return TryWriteByte(0);

            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > maxByteCount)
            {
                // Safely truncate by runes/chars to fit maxByteCount
                Span<byte> tmp = stackalloc byte[255];
                int len = value.Length;
                while (len > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, len)) > maxByteCount)
                    len--;
                byteCount = Encoding.UTF8.GetBytes(value.AsSpan(0, len), tmp);
                if (Remaining < 1 + byteCount)
                    return false;
                _buf[Position++] = (byte)byteCount;
                tmp.Slice(0, byteCount).CopyTo(_buf.Slice(Position, byteCount));
                Position += byteCount;
                return true;
            }

            if (Remaining < 1 + byteCount)
                return false;
            _buf[Position++] = (byte)byteCount;
            Encoding.UTF8.GetBytes(value, _buf.Slice(Position, byteCount));
            Position += byteCount;
            return true;
        }
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _buf;
        public int Position { get; private set; }
        public int Length => _buf.Length;
        public int Remaining => _buf.Length - Position;

        public Reader(ReadOnlySpan<byte> buf)
        {
            _buf = buf;
            Position = 0;
        }

        public bool TryReadByte(out byte v)
        {
            if (Remaining < 1) { v = 0; return false; }
            v = _buf[Position++];
            return true;
        }

        public bool TryReadInt16(out short v)
        {
            if (Remaining < 2) { v = 0; return false; }
            v = BinaryPrimitives.ReadInt16LittleEndian(_buf.Slice(Position, 2));
            Position += 2;
            return true;
        }

        public bool TryReadUInt16(out ushort v)
        {
            if (Remaining < 2) { v = 0; return false; }
            v = BinaryPrimitives.ReadUInt16LittleEndian(_buf.Slice(Position, 2));
            Position += 2;
            return true;
        }

        public bool TryReadInt32(out int v)
        {
            if (Remaining < 4) { v = 0; return false; }
            v = BinaryPrimitives.ReadInt32LittleEndian(_buf.Slice(Position, 4));
            Position += 4;
            return true;
        }

        public bool TryReadUInt32(out uint v)
        {
            if (Remaining < 4) { v = 0; return false; }
            v = BinaryPrimitives.ReadUInt32LittleEndian(_buf.Slice(Position, 4));
            Position += 4;
            return true;
        }

        public bool TryReadUInt64(out ulong v)
        {
            if (Remaining < 8) { v = 0; return false; }
            v = BinaryPrimitives.ReadUInt64LittleEndian(_buf.Slice(Position, 8));
            Position += 8;
            return true;
        }

        public bool TryReadFiniteSingle(out float v)
        {
            if (Remaining < 4) { v = 0f; return false; }
            v = BinaryPrimitives.ReadSingleLittleEndian(_buf.Slice(Position, 4));
            Position += 4;
            return float.IsFinite(v);
        }

        public bool TryReadVec3(out Vector3 v)
        {
            v = Vector3.Zero;
            if (!TryReadFiniteSingle(out float x) ||
                !TryReadFiniteSingle(out float y) ||
                !TryReadFiniteSingle(out float z))
            {
                return false;
            }
            v = new Vector3(x, y, z);
            return true;
        }

        public bool TryReadString(out string value, int maxUtf8Bytes)
        {
            value = string.Empty;
            if (!TryReadByte(out byte len))
                return false;
            if (len > maxUtf8Bytes || Remaining < len)
                return false;
            if (len == 0)
                return true;
            value = Encoding.UTF8.GetString(_buf.Slice(Position, len));
            Position += len;
            return true;
        }
    }
}
