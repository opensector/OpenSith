using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using OpenSith.Core.AI;
using OpenSith.Core.Animation;
using OpenSith.Core.Audio;
using OpenSith.Core.Cog;
using OpenSith.Core.Data;
using OpenSith.Core.Models;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;

namespace OpenSith.Core.Simulation;

/// <summary>
/// Live world simulation controller bridging COG script execution (<see cref="CogVm"/>),
/// frame-based doors/elevators, sector adjoin visibility/collision, and NPC <see cref="AiBrain"/> updates.
/// </summary>
public sealed partial class WorldSimulation : ICogWorldContext
{
    private readonly JklLevel _level;
    private readonly IVirtualFileSystem _vfs;
    private readonly CogVm _cogVm;
    private readonly Random _random = new();
    private readonly UniStringTable _strings;
    private readonly IReadOnlyDictionary<int, ItemDefinition> _itemDefinitions;
    private readonly Dictionary<int, int> _surfaceToSector;

    private readonly Dictionary<string, CogScript?> _scriptCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<CogInstance>> _thingToCogs = new();
    private readonly Dictionary<int, List<CogInstance>> _surfaceToCogs = new();
    private readonly Dictionary<int, List<CogInstance>> _sectorToCogs = new();

    private readonly Dictionary<int, int> _origSurfaceAdjoins = new();
    private readonly Dictionary<int, int> _origAdjoinFlags = new();
    private readonly Dictionary<int, bool> _sectorAdjoinsVisible = new();
    private readonly Dictionary<int, float> _sectorLights = new();

    private readonly Dictionary<int, List<ThingFrame>> _thingFrames = new();
    private readonly Dictionary<int, ThingMotionState> _thingMotions = new();
    private readonly List<int> _doorThingIds = [];
    private readonly Dictionary<string, (Vector3 Min, Vector3 Max)> _doorBoundsCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, AiClass> _aiClassCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, NpcRuntime> _npcs = new();
    private readonly List<AiSoundEvent> _soundEvents = [];
    private readonly List<Projectile> _projectiles = [];
    private readonly Dictionary<string, int> _activeLoopingSounds = new(StringComparer.OrdinalIgnoreCase);
    private int _nextHeadlessSoundChannel = 1;
    private readonly IAudioSink? _audio;
    private bool _startupRun;

    private int _localPlayerThingIndex;
    private int? _activeCogPlayerContext;
    private readonly Dictionary<int, PlayerState> _playersByThing = new();
    private readonly List<PlayerState> _players = [];
    private readonly PlayerInventory _fallbackInventory = new();
    private string? _globalStatusMessage;
    private float _globalStatusTimer;
    private float _elapsedLevelTime;
    private int _nextDynamicCogId = 10000;

    private readonly Dictionary<string, CogInstance?> _templateCogInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThreeDoModel?> _threeDoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, CogInstance> _pickupCogs = new();
    private readonly HashSet<int> _destroyedThings = [];
    private readonly Dictionary<int, float> _thingLifeleft = new();
    private readonly Dictionary<int, Vector3> _thingVelocities = new();
    private readonly Dictionary<int, int> _thingFlags = new();
    private readonly Dictionary<int, int> _actorFlags = new();
    private readonly Dictionary<int, float> _actorExtraSpeeds = new();
    private readonly Dictionary<int, int> _jkFlags = new();
    private readonly Dictionary<int, string> _weaponMeshes = new();
    private readonly Dictionary<int, SaberInfo> _saberInfos = new();
    private readonly Dictionary<int, int> _origGeoModes = new();
    private readonly Dictionary<int, int> _curGeoModes = new();
    private readonly HashSet<int> _hiddenBySim = [];
    private readonly List<int> _viewSearchResults = [];
    private int _viewSearchIndex;
    private readonly Dictionary<int, int> _surfaceCels = new();
    private readonly Dictionary<int, Vector3> _sectorThrusts = new();
    private readonly HashSet<int> _sightedThings = [];
    private readonly Dictionary<int, float> _thingUserData = new();
    private readonly Dictionary<int, HashSet<int>> _amputatedJoints = new();
    private readonly Dictionary<int, int> _thingParents = new();
    private readonly Dictionary<int, float> _thingLights = new();
    private readonly Dictionary<int, int> _thingCollideTypes = new();
    private int _nextKeyTrackId = 1;

    /// <summary>
    /// Public lightsaber blade appearance parameters configured via <c>JkSetSaberInfo</c>.
    /// </summary>
    public readonly record struct ThingSaberInfo(
        string SideMat,
        string TipMat,
        float BaseRadius,
        float TipRadius,
        float Length);

    private sealed record SaberInfo(
        string SideMat, string TipMat, float BaseRad, float tipRad, float Length,
        string WallSparks, string BloodSparks, string SaberSparks);

    public int Difficulty { get; set; } = 1;
    public bool IsMultiplayer { get; }
    public bool IsDedicatedServer { get; }
    public IReadOnlyList<PlayerState> Players => _players;
    public PlayerState? LocalPlayer =>
        _localPlayerThingIndex >= 0 && _playersByThing.TryGetValue(_localPlayerThingIndex, out var p)
            ? p
            : null;
    public PlayerState? GetPlayerByThing(int thingId) => _playersByThing.GetValueOrDefault(thingId);
    public PlayerInventory Inventory => LocalPlayer?.Inventory ?? _fallbackInventory;
    public string? StatusMessage
    {
        get => LocalPlayer?.StatusMessage ?? _globalStatusMessage;
        private set
        {
            if (LocalPlayer != null)
                LocalPlayer.StatusMessage = value;
            else
                _globalStatusMessage = value;
        }
    }
    public bool LevelCompleted { get; private set; }
    public float PlayerExtraSpeed => _localPlayerThingIndex >= 0 ? _actorExtraSpeeds.GetValueOrDefault(_localPlayerThingIndex, 0f) : 0f;
    public bool IsPlayerFrozen => _localPlayerThingIndex >= 0 && (GetActorFlags(_localPlayerThingIndex) & 0x40000) != 0;
    public bool IsActorFrozen(int thingId) => (GetActorFlags(thingId) & 0x40000) != 0;
    public string GetNpcAiClassName(int thingId) => _npcs.TryGetValue(thingId, out var npc) ? npc.Brain.Class.Name : string.Empty;
    public Vector3 GetSectorThrust(int sector) => _sectorThrusts.GetValueOrDefault(sector, Vector3.Zero);

    public Action<int, float>? OnSectorLightChanged;
    public Action<int, Vector2, float>? OnSurfaceScroll;
    public event Action<int, string, bool>? OnPlayThingKeyframe;

    public static readonly Vector3 BoltColorRed = new(1.0f, 0.12f, 0.06f);
    public static readonly Vector3 BoltColorGreen = new(0.15f, 1.0f, 0.25f);
    public static readonly Vector3 BoltColorGold = new(1.0f, 0.78f, 0.12f);

    /// <summary>
    /// Active 3D blaster projectile travelling through the level.
    /// </summary>
    public sealed class Projectile
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Direction;
        public float Damage;
        public float Lifetime;
        public int OwnerThingIndex;
        public bool IsEnemy;
        public string ModelName = "bry0.3do";
        public Vector3 BoltColor = BoltColorRed;
        public bool UseGravity;
        public bool Bounce;
        public bool ExplodeOnImpact = true;
        public string? ExplodeTemplate;
        public string? FleshHitTemplate;
        public int DamageClass = 2;
        public float Radius = 0.02f;
    }

    public sealed class ImpactSpark
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
        public float MaxLife;
    }

    private readonly struct ThingFrame
    {
        public readonly Vector3 Position;
        public readonly Vector3 PitchYawRoll;

        public ThingFrame(Vector3 position, Vector3 pitchYawRoll)
        {
            Position = position;
            PitchYawRoll = pitchYawRoll;
        }
    }

    private sealed class ThingMotionState
    {
        public int CurrentFrame;
        public int TargetFrame;
        public bool IsMoving;
        public bool IsPaused;
        public bool IsPivotRotation;
        public Vector3 StartPos;
        public Vector3 TargetPos;
        public Vector3 StartPyr;
        public Vector3 TargetPyr;
        public Vector3 PivotWorldPos;
        public Vector3 StartOffsetFromPivot;
        public Vector3 TargetDeltaPyr;
        public float Elapsed;
        public float Duration;
        public float LastDeltaZ;
    }

    private sealed class NpcRuntime
    {
        public required JklThing Thing;
        public required AiBrain Brain;
        public float Health;
        public float MaxHealth;
        public float MoveSpeed;
        public float Radius;
        public float HipHeightOffset { get; init; } = 0.13f;
        public uint TypeFlags { get; init; }
        public bool UseGravity { get; init; } = true;
        public float BlockFov { get; init; }
        public float BlockChance { get; init; }
        public float BlockError { get; init; }
        public bool IsAlive = true;
        public bool WasDamagedThisTick;
        public Vector3? LastDamageSourcePos;
        public float FireAnimTimer;
        public string FireAnimAction = "fire";
        public float BlockAnimTimer;
        public float HitAnimTimer;
        public string AnimAction = "stand";
        public int AnimMode;
        public bool LoopAnim = true;
        public float LastDistanceMoved;
        public int OnElevatorThingId = -1;
        public Vector3 Velocity;
        public bool IsAirborne;
        public Vector3? OverrideMovePos;
        public int? OverrideMoveThingId;
        public int? OverrideFireTargetId;
        public Vector3? OverrideLookPos;
        public float OverrideLookTimer;
        public float BlindTimer;
        public int LastDamagerThingId = -1;
    }

    public int ActiveCogCount => _cogVm.Instances.Count;
    public int ActiveNpcCount => _npcs.Count;
    public int FramedThingCount => _thingFrames.Count;
    public IReadOnlyList<Projectile> Projectiles => _projectiles;
    private readonly List<ImpactSpark> _sparks = [];
    public IReadOnlyList<ImpactSpark> Sparks => _sparks;

    public WorldSimulation(
        JklLevel level,
        IVirtualFileSystem vfs,
        IAudioSink? audio = null,
        int difficulty = 1,
        bool runStartupCogs = true,
        bool isMultiplayer = false,
        bool isDedicatedServer = false)
    {
        _level = level ?? throw new ArgumentNullException(nameof(level));
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
        _audio = audio;
        Difficulty = Math.Clamp(difficulty, 0, 2);
        IsMultiplayer = isMultiplayer;
        IsDedicatedServer = isDedicatedServer;
        _strings = UniStringTable.LoadFromVfs(_vfs);
        _itemDefinitions = LoadItemDefinitions(_vfs);
        _surfaceToSector = BuildSurfaceToSectorMap(_level);
        _cogVm = new CogVm(this)
        {
            InstanceScopeRunner = RunInInstancePlayerScope
        };

        if (isDedicatedServer)
        {
            _localPlayerThingIndex = -1;
        }
        else
        {
            var spawn = _level.GetPlayerSpawn();
            _localPlayerThingIndex = spawn?.ThingIndex ?? 0;
            var localPlayer = new PlayerState
            {
                ThingIndex = _localPlayerThingIndex,
                PlayerSlot = 0,
                Name = "Player 1",
                PrevSectorIndex = spawn?.SectorIndex ?? -1,
                WorldPosition = spawn?.Position ?? Vector3.Zero,
                HasInputPosition = spawn != null
            };
            localPlayer.Inventory.Health = 100f;
            localPlayer.Inventory.Shields = isMultiplayer ? 100f : 50f;
            localPlayer.Inventory.EnergyAmmo = isMultiplayer ? 50f : 100f;
            localPlayer.Inventory.PowerAmmo = 100f;
            localPlayer.Inventory.Battery = 100f;
            localPlayer.Inventory.SetInv(PlayerInventory.BinFists, 1f);
            localPlayer.Inventory.HasBryar = true;
            localPlayer.Inventory.HasLightsaber = true;
            localPlayer.Inventory.SetInv(PlayerInventory.BinFieldLight, 1f);
            localPlayer.Inventory.SetInvAvailable(PlayerInventory.BinFieldLight, true);
            localPlayer.Inventory.SetInv(PlayerInventory.BinPowerBoost, 1f);
            if (!isMultiplayer)
            {
                localPlayer.Inventory.SetInv(PlayerInventory.BinStRifle, 1f);
                localPlayer.Inventory.SetInv(PlayerInventory.BinRepeater, 1f);
            }
            _playersByThing[_localPlayerThingIndex] = localPlayer;
            _players.Add(localPlayer);
        }

        InitializeAdjoinSnapshots();
        InitializeGeoModes();
        ParseThingFrames();
        LoadLevelCogs();
        LoadNpcs();
        if (runStartupCogs)
            StartLevel();
    }

    /// <summary>
    /// Spawns and registers a player slot for multi-player or dedicated-server sessions.
    /// </summary>
    public PlayerState AddPlayer(int playerSlot, string name = "", int team = 0)
    {
        JklThing? candidateSpawn = null;
        foreach (var t in _level.Things)
        {
            if (t.IsPlayerSpawn && !_playersByThing.ContainsKey(t.ThingIndex))
            {
                candidateSpawn = t;
                break;
            }
        }

        int thingIndex;
        if (candidateSpawn != null)
        {
            thingIndex = candidateSpawn.ThingIndex;
            candidateSpawn.ThingFlags &= ~(uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed | SithThingFlags.Invisible);
            candidateSpawn.IsNonSolid = false;
            _thingFlags[thingIndex] = (int)candidateSpawn.ThingFlags;
        }
        else
        {
            var templateSpawn = _level.GetPlayerSpawn();
            thingIndex = _level.Things.Count;
            _level.Things.Add(new JklThing
            {
                ThingIndex = thingIndex,
                TemplateName = templateSpawn?.TemplateName ?? "walkplayer",
                SectorIndex = templateSpawn?.SectorIndex ?? 0,
                Position = templateSpawn?.Position ?? Vector3.Zero,
                PitchYawRoll = templateSpawn?.PitchYawRoll ?? Vector3.Zero,
                Model3D = "ky.3do",
                ThingFlags = (uint)(SithThingFlags.EmitLight | SithThingFlags.Captured | SithThingFlags.Splashes)
            });
        }

        var spawnThing = _level.Things[thingIndex];
        var player = new PlayerState
        {
            ThingIndex = thingIndex,
            PlayerSlot = playerSlot,
            Name = string.IsNullOrWhiteSpace(name) ? $"Player {playerSlot + 1}" : name.Trim(),
            Team = team,
            PrevSectorIndex = spawnThing.SectorIndex,
            WorldPosition = spawnThing.Position,
            HasInputPosition = true,
            CurWeaponBin = PlayerInventory.BinBryar,
            CurInvWeaponBin = PlayerInventory.BinBryar
        };

        player.Inventory.Health = 100f;
        player.Inventory.Shields = 100f;
        player.Inventory.EnergyAmmo = 50f;
        player.Inventory.Battery = 100f;
        player.Inventory.SetInv(PlayerInventory.BinFists, 1f);
        player.Inventory.HasBryar = true;
        player.Inventory.HasLightsaber = true;
        player.Inventory.SetInv(PlayerInventory.BinFieldLight, 1f);
        player.Inventory.SetInvAvailable(PlayerInventory.BinFieldLight, true);
        player.Inventory.SetInv(PlayerInventory.BinPowerBoost, 1f);

        _destroyedThings.Remove(thingIndex);
        _playersByThing[thingIndex] = player;
        _players.Add(player);

        if (IsMultiplayer)
        {
            var classCog = GetOrCreatePlayerClassCog(player);
            if (classCog != null && classCog.Program.Labels.ContainsKey("newplayer"))
            {
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(classCog, "newplayer", senderRef: player.ThingIndex, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
            }
        }

        return player;
    }

    /// <summary>
    /// Removes a player from the active session and clears its thing state.
    /// </summary>
    public bool RemovePlayer(int thingIndex)
    {
        if (!_playersByThing.Remove(thingIndex, out var player))
            return false;

        if (player.ClassCog != null)
        {
            _playerCogOwners.Remove(player.ClassCog);
            _cogVm.RemoveInstance(player.ClassCog);
            player.ClassCog = null;
        }

        foreach (var binCog in player.BinCogs.Values)
        {
            _playerCogOwners.Remove(binCog);
            _cogVm.RemoveInstance(binCog);
        }
        player.BinCogs.Clear();
        player.ActiveBinFireTimers.Clear();

        _players.Remove(player);
        _activeCogPlayerContext = _activeCogPlayerContext == thingIndex ? null : _activeCogPlayerContext;
        if (_localPlayerThingIndex == thingIndex)
            _localPlayerThingIndex = -1;

        if (thingIndex >= 0 && thingIndex < _level.Things.Count)
            DestroyThing(thingIndex);

        return true;
    }

    /// <summary>
    /// Updates authoritative per-player spatial and input state prior to <see cref="StepWorld"/>.
    /// </summary>
    public void UpdatePlayerInput(
        int playerThingId,
        Vector3 pos,
        int sector,
        Vector3? aimDir = null,
        Vector3? thrust = null,
        bool isGrounded = true,
        bool isUnderwater = false)
    {
        if (playerThingId < 0 || !_playersByThing.TryGetValue(playerThingId, out var player))
            return;

        player.WorldPosition = pos;
        player.HasInputPosition = true;
        if (playerThingId < _level.Things.Count)
        {
            var thing = _level.Things[playerThingId];
            thing.Position = pos;
            thing.SectorIndex = sector;
        }

        if (aimDir is Vector3 dir && dir.LengthSquared() > 1e-6f)
        {
            player.AimDirection = Vector3.Normalize(dir);
            player.HasAimDirection = true;
            if (playerThingId < _level.Things.Count)
            {
                float yaw = MathF.Atan2(-player.AimDirection.X, player.AimDirection.Y) * (180f / MathF.PI);
                var cur = _level.Things[playerThingId].PitchYawRoll;
                _level.Things[playerThingId].PitchYawRoll = new Vector3(cur.X, yaw, cur.Z);
            }
        }

        if (thrust is Vector3 t)
            player.Thrust = t;
        player.IsGrounded = isGrounded;
        player.IsUnderwater = isUnderwater;
    }

    public Vector3 ConsumePlayerPlatformDelta(int playerThingId)
    {
        if (!_playersByThing.TryGetValue(playerThingId, out var player))
            return Vector3.Zero;

        Vector3 delta = player.PendingPlatformDelta;
        player.PendingPlatformDelta = Vector3.Zero;
        return delta;
    }

    public bool TryConsumePlayerVelocityOverride(int playerThingId, out Vector3 velocity)
    {
        if (_playersByThing.TryGetValue(playerThingId, out var player) && player.HasVelocityOverride)
        {
            velocity = player.VelocityOverride;
            player.HasVelocityOverride = false;
            return true;
        }

        velocity = Vector3.Zero;
        return false;
    }

    /// <summary>
    /// Dispatches the <c>startup</c> message to all loaded level COGs once (idempotent).
    /// Useful when constructing <see cref="WorldSimulation"/> with <c>runStartupCogs: false</c>
    /// so event listeners and player state can be wired before <c>startup:</c> executes.
    /// </summary>
    public void StartLevel()
    {
        if (_startupRun)
            return;

        _startupRun = true;
        RunStartupCogs();
    }

    /// <summary>
    /// Read-only inspection helper that retrieves a symbol's runtime <see cref="CogValue"/>
    /// from the first loaded COG instance matching <paramref name="scriptName"/>.
    /// </summary>
    public bool TryGetCogSymbolValue(string scriptName, string symbolName, out CogValue value)
    {
        foreach (var inst in _cogVm.Instances)
        {
            if (string.Equals(inst.Script.Name, scriptName, StringComparison.OrdinalIgnoreCase))
            {
                value = inst.GetSymbolValue(symbolName);
                return value.Type != CogValueType.Null || inst.SymbolSlots.ContainsKey(symbolName);
            }
        }

        value = CogValue.Null;
        return false;
    }

    private void InitializeGeoModes()
    {
        foreach (var thing in _level.Things)
        {
            string? type = ResolveTemplateParam(thing.TemplateName, "type");
            if (string.Equals(type, "camera", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thing.TemplateName, "droidcam", StringComparison.OrdinalIgnoreCase))
            {
                _origGeoModes[thing.ThingIndex] = 0;
                SetThingCurGeoMode(thing.ThingIndex, 0);
                continue;
            }

            string modelName = !string.IsNullOrEmpty(thing.Model3D)
                ? thing.Model3D
                : _level.ResolveModel3D(thing.TemplateName);
            int geoMode = !string.IsNullOrEmpty(modelName) ? 4 : 0;
            _origGeoModes[thing.ThingIndex] = geoMode;
            _curGeoModes[thing.ThingIndex] = geoMode;
        }
    }

    private void InitializeAdjoinSnapshots()
    {
        for (int i = 0; i < _level.Adjoins.Count; i++)
        {
            _origAdjoinFlags[i] = _level.Adjoins[i].Flags;
        }

        foreach (var surface in _level.Surfaces)
        {
            _origSurfaceAdjoins[surface.SurfaceIndex] = surface.AdjoinIndex;
        }
    }

    private void ParseThingFrames()
    {
        foreach (var thing in _level.Things)
        {
            if (string.IsNullOrWhiteSpace(thing.ExtraArguments))
                continue;

            var frames = ParseFramesFromExtraArgs(thing.ExtraArguments, thing.PitchYawRoll);
            if (frames.Count > 0)
            {
                _thingFrames[thing.ThingIndex] = frames;
                _thingMotions[thing.ThingIndex] = new ThingMotionState
                {
                    CurrentFrame = 0,
                    TargetFrame = 0,
                    IsMoving = false
                };

                if (IsDoorThing(thing))
                    _doorThingIds.Add(thing.ThingIndex);
            }
        }
    }

    private static List<ThingFrame> ParseFramesFromExtraArgs(string extraArgs, Vector3 defaultPyr)
    {
        var frames = new List<ThingFrame>();
        int searchFrom = 0;

        while (searchFrom < extraArgs.Length)
        {
            int idx = extraArgs.IndexOf("frame=(", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                break;

            int contentStart = idx + "frame=(".Length;
            int closeIdx = extraArgs.IndexOf(')', contentStart);
            if (closeIdx < 0)
                break;

            string inner = extraArgs[contentStart..closeIdx];
            searchFrom = closeIdx + 1;

            int colonIdx = inner.IndexOf(':');
            string posStr = colonIdx >= 0 ? inner[..colonIdx] : inner;
            string? pyrStr = colonIdx >= 0 ? inner[(colonIdx + 1)..] : null;

            if (!TryParseSlashVec3(posStr, out var pos))
                continue;

            Vector3 pyr = defaultPyr;
            if (!string.IsNullOrWhiteSpace(pyrStr) && TryParseSlashVec3(pyrStr, out var parsedPyr))
                pyr = parsedPyr;

            frames.Add(new ThingFrame(pos, pyr));
        }

        return frames;
    }

    private static bool TryParseSlashVec3(string text, out Vector3 vec)
    {
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            vec = new Vector3(x, y, z);
            return true;
        }

        vec = Vector3.Zero;
        return false;
    }

    private void LoadLevelCogs()
    {
        foreach (var placement in _level.Cogs)
        {
            var script = LoadCogScript(placement.ScriptName);
            if (script == null)
                continue;

            var instance = new CogInstance(script, placement.Index);
            instance.BindPlacementArgs(placement);
            _cogVm.AddInstance(instance);

            IndexInstanceBindings(instance);
        }
    }

    private CogScript? LoadCogScript(string scriptName)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
            return null;

        if (_scriptCache.TryGetValue(scriptName, out var cached))
            return cached;

        string[] candidatePaths = [
            $"cog/{scriptName}",
            $"misc/cog/{scriptName}",
            scriptName
        ];

        foreach (var path in candidatePaths)
        {
            if (!_vfs.FileExists(path))
                continue;

            try
            {
                string text = _vfs.ReadAllText(path);
                var parsed = CogParser.Parse(text, scriptName);
                _scriptCache[scriptName] = parsed;
                return parsed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COG] Failed to parse '{path}': {ex.Message}");
                break;
            }
        }

        _scriptCache[scriptName] = null;
        return null;
    }

    private void IndexInstanceBindings(CogInstance instance)
    {
        foreach (var sym in instance.Script.Symbols)
        {
            if (sym.IsLocal)
                continue;

            var val = instance.GetSymbolValue(sym.Name);
            int id = val.AsInt;
            if (id < 0)
                continue;

            switch (sym.Type)
            {
                case CogSymbolType.Thing:
                    AddUniqueMapping(_thingToCogs, id, instance);
                    if (sym.Mask != 0 && id < _level.Things.Count)
                        _level.Things[id].ThingFlags |= (uint)SithThingFlags.Captured;
                    break;
                case CogSymbolType.Surface:
                    AddUniqueMapping(_surfaceToCogs, id, instance);
                    if (sym.Mask != 0 && id < _level.Surfaces.Count)
                        _level.Surfaces[id].SurfaceFlags |= 0x2; // SITH_SURFACE_COG_LINKED
                    break;
                case CogSymbolType.Sector:
                    AddUniqueMapping(_sectorToCogs, id, instance);
                    break;
            }
        }
    }

    private static void AddUniqueMapping(Dictionary<int, List<CogInstance>> map, int key, CogInstance instance)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }
        if (!list.Contains(instance))
            list.Add(instance);
    }

    private void RunStartupCogs()
    {
        foreach (var instance in _cogVm.Instances.ToArray())
        {
            if (!instance.Program.Labels.ContainsKey("startup"))
                continue;

            try
            {
                _cogVm.SendMessage(instance, "startup");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COG] Error in '{instance.Script.Name}' startup: {ex.Message}");
            }
        }
    }

    private void LoadNpcs()
    {
        foreach (var thing in _level.Things)
        {
            TrySpawnNpcRuntime(thing);
        }
    }

    private bool TrySpawnNpcRuntime(JklThing thing)
    {
        if (_playersByThing.ContainsKey(thing.ThingIndex) || thing.IsPlayerSpawn)
            return false;

        if ((thing.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0)
            return false;

        if (!JklThing.ShouldSpawnForDifficulty(thing.ThingFlags, Difficulty))
            return false;

        if (thing.SectorIndex < 0 || thing.SectorIndex >= _level.Sectors.Count)
            return false;

        string? aiClassName = ResolveTemplateParam(thing.TemplateName, "aiclass");
        if (string.IsNullOrWhiteSpace(aiClassName))
            return false;

        var aiClass = LoadAiClass(aiClassName);
        float health = ParseTemplateFloat(thing.TemplateName, "health", 40.0f);
        float maxHealth = ParseTemplateFloat(thing.TemplateName, "maxhealth", health);
        float maxVel = ParseTemplateFloat(thing.TemplateName, "maxvel", 0.5f);
        float size = ParseTemplateFloat(thing.TemplateName, "size", 0.065f);
        uint typeFlags = ParseTemplateHexOrInt(thing.TemplateName, "typeflags", 0);
        uint physFlags = ParseTemplateHexOrInt(thing.TemplateName, "physflags", 0x1);
        bool useGravity = (physFlags & 0x1) != 0;
        float blockFov = ParseTemplateFloat(thing.TemplateName, "fov", 0f);
        float blockChance = ParseTemplateFloat(thing.TemplateName, "chance", 0f);
        float blockError = ParseTemplateFloat(thing.TemplateName, "error", 0f);

        string modelName = !string.IsNullOrEmpty(thing.Model3D)
            ? thing.Model3D
            : _level.ResolveModel3D(thing.TemplateName);
        var model = !string.IsNullOrEmpty(modelName) ? TryLoadThreeDo(modelName) : null;

        bool hasSectorFloor = TryGetFloorZBelow(thing.SectorIndex, thing.Position, out float sectorFloorZ);
        bool hasThingFloor = TryGetThingFloorZBelow(thing.Position, thing.Position.Z + 0.05f, out float thingFloorZ, out int carrierThingId);

        float effectiveFloorZ = float.MinValue;
        int onElevatorThingId = -1;
        if (hasSectorFloor)
            effectiveFloorZ = sectorFloorZ;
        if (hasThingFloor && thingFloorZ > effectiveFloorZ)
        {
            effectiveFloorZ = thingFloorZ;
            onElevatorThingId = carrierThingId;
        }
        bool hasEffectiveFloor = hasSectorFloor || hasThingFloor;

        float hipHeightOffset = 0.13f;
        if (model?.InsertOffset.Z > 0.001f)
        {
            hipHeightOffset = model.InsertOffset.Z;
        }
        else if (hasEffectiveFloor && thing.Position.Z - effectiveFloorZ > hipHeightOffset)
        {
            hipHeightOffset = thing.Position.Z - effectiveFloorZ;
        }

        if (hasEffectiveFloor && thing.Position.Z < effectiveFloorZ + hipHeightOffset - 0.005f)
        {
            thing.Position = new Vector3(thing.Position.X, thing.Position.Y, effectiveFloorZ + hipHeightOffset);
        }

        var brain = new AiBrain(aiClass, homePosition: thing.Position, initialMode: AiMode.Active);
        _npcs[thing.ThingIndex] = new NpcRuntime
        {
            Thing = thing,
            Brain = brain,
            Health = health,
            MaxHealth = MathF.Max(1.0f, maxHealth),
            MoveSpeed = MathF.Max(0.15f, maxVel),
            Radius = MathF.Max(0.03f, size),
            HipHeightOffset = hipHeightOffset,
            TypeFlags = typeFlags,
            UseGravity = useGravity,
            BlockFov = blockFov,
            BlockChance = blockChance,
            BlockError = blockError,
            OnElevatorThingId = onElevatorThingId
        };
        return true;
    }

    private AiClass LoadAiClass(string aiClassName)
    {
        if (_aiClassCache.TryGetValue(aiClassName, out var cached))
            return cached;

        try
        {
            var loaded = AiClassReader.Load(_vfs, aiClassName, difficulty: Difficulty);
            _aiClassCache[aiClassName] = loaded;
            return loaded;
        }
        catch
        {
            var fallback = new AiClass { Name = aiClassName };
            _aiClassCache[aiClassName] = fallback;
            return fallback;
        }
    }

    private string? ResolveTemplateParam(string templateName, string paramKey)
    {
        string current = templateName;
        int guard = 0;
        while (!string.IsNullOrEmpty(current) && !current.Equals("none", StringComparison.OrdinalIgnoreCase) && guard++ < 32)
        {
            if (!_level.Templates.TryGetValue(current, out var tmpl))
                break;

            if (tmpl.Parameters.TryGetValue(paramKey, out var val) && !string.IsNullOrEmpty(val))
                return val;

            current = tmpl.BasedOn;
        }
        return null;
    }

    private float ParseTemplateFloat(string templateName, string paramKey, float fallback)
    {
        string? raw = ResolveTemplateParam(templateName, paramKey);
        if (!string.IsNullOrEmpty(raw) &&
            float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
        {
            return val;
        }
        return fallback;
    }

    private uint ParseTemplateHexOrInt(string templateName, string paramKey, uint fallback = 0)
    {
        string? raw = ResolveTemplateParam(templateName, paramKey);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexVal))
                return hexVal;
        }
        else if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint decVal))
        {
            return decVal;
        }

        return fallback;
    }

    /// <summary>
    /// Advances COG timers, moving doors/elevators, player sector/adjoin triggers, weapons, VFX, and NPC AI brains.
    /// Returns any vertical/horizontal platform displacement to apply to the local player when riding an elevator.
    /// </summary>
    public Vector3 Update(float dt, Vector3 playerPos, int playerSector, Vector3? aimDir = null)
    {
        if (dt <= 0f)
            return Vector3.Zero;

        if (_localPlayerThingIndex >= 0 && LocalPlayer is { } localPlayer)
        {
            UpdatePlayerInput(
                _localPlayerThingIndex,
                playerPos,
                playerSector,
                aimDir,
                localPlayer.Thrust,
                localPlayer.IsGrounded,
                localPlayer.IsUnderwater);
        }

        StepWorld(dt);
        return _localPlayerThingIndex >= 0 ? ConsumePlayerPlatformDelta(_localPlayerThingIndex) : Vector3.Zero;
    }

    /// <summary>
    /// Advances the entire headless world simulation by <paramref name="dt"/> seconds across all connected players.
    /// </summary>
    public void StepWorld(float dt)
    {
        if (dt <= 0f)
            return;

        _elapsedLevelTime += dt;

        if (_globalStatusTimer > 0f)
        {
            _globalStatusTimer -= dt;
            if (_globalStatusTimer <= 0f)
            {
                _globalStatusTimer = 0f;
                _globalStatusMessage = null;
            }
        }

        foreach (var player in _players)
        {
            if (player.StatusTimer > 0f)
            {
                player.StatusTimer -= dt;
                if (player.StatusTimer <= 0f)
                {
                    player.StatusTimer = 0f;
                    player.StatusMessage = null;
                }
            }
            player.Inventory.UpdateForceMana(dt);
        }

        UpdateLifeleftTimers(dt);
        UpdateThingTimersAndPulses(dt);
        UpdateThingRotVels(dt);
        UpdateThingVelocities(dt);

        foreach (var player in _players.ToArray())
        {
            if (player.ThingIndex < 0 || player.ThingIndex >= _level.Things.Count)
                continue;

            var thing = _level.Things[player.ThingIndex];
            UpdatePlayerSectorTransitions(player, thing.SectorIndex);
            if (!player.Inventory.IsDead)
            {
                CheckDeathFloorSurfaces(player, thing.Position, thing.SectorIndex);
                CheckPickupTouches(player, thing.Position);
                CheckSightedCogs(player, thing.Position, player.HasAimDirection ? player.AimDirection : null);
            }
        }

        UpdatePlayerWeapons(dt);
        UpdateThingMotions(dt);

        try
        {
            _cogVm.Update(dt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[COG] VM Update error: {ex.Message}");
        }

        UpdateNpcs(dt);
        UpdateProjectiles(dt);
        UpdateSparks(dt);
        UpdateVisualEffects(dt);
        UpdateLightAnimations(dt);
        UpdateScreenColorEffects(dt);
        _soundEvents.Clear();

        foreach (var player in _players)
        {
            if (player.ThingIndex < 0 || player.ThingIndex >= _level.Things.Count || IsActorFrozen(player.ThingIndex))
                continue;

            int sec = _level.Things[player.ThingIndex].SectorIndex;
            if (_sectorThrusts.TryGetValue(sec, out var thrust) && thrust.LengthSquared() > 1e-6f)
                player.PendingPlatformDelta += thrust * dt;
        }
    }

    private void UpdateThingVelocities(float dt)
    {
        if (_sectorThrusts.Count > 0)
        {
            for (int i = 0; i < _level.Things.Count; i++)
            {
                var t = _level.Things[i];
                if (_playersByThing.ContainsKey(t.ThingIndex) ||
                    _destroyedThings.Contains(t.ThingIndex) ||
                    _npcs.ContainsKey(t.ThingIndex) ||
                    _thingMotions.ContainsKey(t.ThingIndex) ||
                    !_sectorThrusts.TryGetValue(t.SectorIndex, out var thrust) ||
                    thrust.LengthSquared() <= 1e-6f)
                {
                    continue;
                }

                bool isDynamic = _thingVelocities.ContainsKey(t.ThingIndex) ||
                    string.Equals(ResolveTemplateParam(t.TemplateName, "move"), "physics", StringComparison.OrdinalIgnoreCase);
                if (isDynamic)
                    _thingVelocities[t.ThingIndex] = GetThingVel(t.ThingIndex) + thrust * dt;
            }
        }

        if (_thingVelocities.Count == 0)
            return;

        const float gravity = 3.5f;
        var keys = new List<int>(_thingVelocities.Keys);

        foreach (int thingId in keys)
        {
            if (thingId < 0 || thingId >= _level.Things.Count ||
                _destroyedThings.Contains(thingId) ||
                _playersByThing.ContainsKey(thingId) ||
                _thingMotions.ContainsKey(thingId))
            {
                _thingVelocities.Remove(thingId);
                continue;
            }

            Vector3 vel = _thingVelocities[thingId];
            var thing = _level.Things[thingId];

            vel.Z -= gravity * dt;
            Vector3 prevPos = thing.Position;
            Vector3 nextPos = prevPos + vel * dt;

            float checkZ = MathF.Max(prevPos.Z, nextPos.Z);
            Vector3 floorQueryPos = new(nextPos.X, nextPos.Y, checkZ);
            bool hasSectorFloor = TryGetFloorZBelow(thing.SectorIndex, floorQueryPos, out float sectorFloorZ);
            bool hasThingFloor = TryGetThingFloorZBelow(floorQueryPos, checkZ + 0.05f, out float thingFloorZ, out _);

            float floorZ = float.MinValue;
            bool hasFloor = false;
            if (hasSectorFloor)
            {
                floorZ = sectorFloorZ;
                hasFloor = true;
            }
            if (hasThingFloor && thingFloorZ > floorZ)
            {
                floorZ = thingFloorZ;
                hasFloor = true;
            }

            if (hasFloor && nextPos.Z <= floorZ && vel.Z <= 0f)
            {
                nextPos.Z = floorZ;
                vel.Z = 0f;
                float damping = MathF.Max(0f, 1f - 6.0f * dt);
                vel.X *= damping;
                vel.Y *= damping;
                if (MathF.Abs(vel.X) < 0.01f) vel.X = 0f;
                if (MathF.Abs(vel.Y) < 0.01f) vel.Y = 0f;
            }

            thing.Position = nextPos;
            if (vel == Vector3.Zero && hasFloor && nextPos.Z <= floorZ + 0.001f)
                _thingVelocities.Remove(thingId);
            else
                _thingVelocities[thingId] = vel;
        }
    }

    /// <summary>
    /// Resets player health and shields for respawn while preserving level progress and inventory.
    /// </summary>
    public void RespawnPlayer(int thingId = -1)
    {
        if (!TryResolvePlayer(thingId, out var player))
            return;

        player.Inventory.ResetForRespawn();
        player.StatusMessage = null;
        player.StatusTimer = 0f;
        player.ActiveFireMode = -1;
        player.FireWait = 0f;
        player.MountWait = 0f;
        player.SaberDamageActive = false;

        var spawn = _level.GetPlayerSpawn();
        player.PrevSectorIndex = spawn?.SectorIndex ?? -1;

        if (IsMultiplayer)
        {
            var classCog = GetOrCreatePlayerClassCog(player);
            if (classCog != null && classCog.Program.Labels.ContainsKey("newplayer"))
            {
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(classCog, "newplayer", senderRef: player.ThingIndex, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
            }
        }
    }

    /// <summary>
    /// Checks for nearby item/powerup things overlapping the local player's body capsule and dispatches
    /// "touched" to their template COG (removing/hiding the pickup if taken).
    /// </summary>
    public void CheckPickupTouches(Vector3 playerPos)
    {
        if (LocalPlayer != null)
            CheckPickupTouches(LocalPlayer, playerPos);
    }

    private void CheckPickupTouches(PlayerState player, Vector3 playerPos)
    {
        if (player.Inventory.IsDead)
            return;

        const float playerRadius = 0.065f;
        const float playerHeight = 0.22f;
        Vector3 playerTop = new(playerPos.X, playerPos.Y, playerPos.Z + playerHeight);

        for (int i = 0; i < _level.Things.Count; i++)
        {
            var thing = _level.Things[i];
            if (thing.IsPlayerSpawn ||
                _playersByThing.ContainsKey(thing.ThingIndex) ||
                _destroyedThings.Contains(thing.ThingIndex) ||
                (thing.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0 ||
                !JklThing.ShouldSpawnForDifficulty(thing.ThingFlags, Difficulty) ||
                string.IsNullOrWhiteSpace(thing.TemplateName))
                continue;

            float itemSize = thing.Size ?? ParseTemplateFloat(thing.TemplateName, "size", 0.10f);
            float touchRadius = playerRadius + MathF.Max(0.02f, itemSize);
            if (DistanceSqPointToSegment(thing.Position, playerPos, playerTop, out _) > touchRadius * touchRadius)
            {
                player.TouchedCapturedThings.Remove(thing.ThingIndex);
                continue;
            }

            string? type = ResolveTemplateParam(thing.TemplateName, "type");
            string? cogName = ResolveTemplateParam(thing.TemplateName, "cog");
            bool isItem = string.Equals(type, "item", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(cogName) && cogName.StartsWith("pow_", StringComparison.OrdinalIgnoreCase));

            if (isItem && !string.IsNullOrWhiteSpace(cogName))
            {
                var cog = GetOrCreateTemplateCog(cogName);
                if (cog == null || !cog.Program.Labels.ContainsKey("touched"))
                    continue;

                _pickupCogs[thing.ThingIndex] = cog;
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(cog, "touched", senderRef: thing.ThingIndex, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
                continue;
            }

            bool isCaptured = (thing.ThingFlags & (uint)SithThingFlags.Captured) != 0;
            float collide = ParseTemplateFloat(thing.TemplateName, "collide", 0f);
            if (!isCaptured || (collide <= 0f && itemSize <= 0f))
                continue;

            if (!_thingToCogs.TryGetValue(thing.ThingIndex, out var linkedCogs))
                continue;

            if (!player.TouchedCapturedThings.Add(thing.ThingIndex))
                continue;

            foreach (var cog in linkedCogs)
            {
                if (!cog.Program.Labels.ContainsKey("touched"))
                    continue;
                if (!cog.MatchesSymbolMask(CogSymbolType.Thing, thing.ThingIndex, 0x400))
                    continue;

                int symLinkId = GetSymbolLinkId(cog, CogSymbolType.Thing, thing.ThingIndex);
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(cog, "touched", senderRef: thing.ThingIndex, senderId: symLinkId, sourceRef: player.ThingIndex, senderType: 3));
            }
        }
    }

    private void CheckSightedCogs(PlayerState player, Vector3 playerPos, Vector3? aimDir)
    {
        if (_thingToCogs.Count == 0)
            return;

        Vector3 eyePos = playerPos + new Vector3(0f, 0f, 0.157f);
        Vector3? normAim = null;
        if (aimDir is Vector3 dir && dir.LengthSquared() > 1e-6f)
            normAim = Vector3.Normalize(dir);

        const float maxSightRange = 12.0f;
        const float maxNoAimRange = 8.0f;

        foreach (var (thingId, cogs) in _thingToCogs)
        {
            if (_sightedThings.Contains(thingId))
                continue;

            if (thingId < 0 || thingId >= _level.Things.Count)
                continue;

            if (_destroyedThings.Contains(thingId))
                continue;

            var thing = _level.Things[thingId];
            uint flags = (uint)GetThingFlags(thingId);
            if ((flags & (uint)SithThingFlags.Captured) == 0)
                continue;
            if ((flags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed | SithThingFlags.Invisible)) != 0)
                continue;
            if (IsThingInvisible(thingId))
                continue;

            bool anySightedCog = false;
            foreach (var cog in cogs)
            {
                if (cog.Program.Labels.ContainsKey("sighted"))
                {
                    anySightedCog = true;
                    break;
                }
            }
            if (!anySightedCog)
                continue;

            Vector3 toThing = thing.Position - eyePos;
            float distSq = toThing.LengthSquared();
            if (distSq > maxSightRange * maxSightRange)
                continue;

            float dist = MathF.Sqrt(distSq);
            Vector3 toThingDir = dist > 1e-5f ? toThing / dist : Vector3.UnitY;

            if (normAim is Vector3 fwd)
            {
                if (Vector3.Dot(fwd, toThingDir) <= 0.25f)
                    continue;
            }
            else if (dist > maxNoAimRange)
            {
                continue;
            }

            if (!HasLineOfSight(eyePos, thing.Position))
                continue;

            _sightedThings.Add(thingId);
            foreach (var cog in cogs)
            {
                if (!cog.Program.Labels.ContainsKey("sighted"))
                    continue;

                int linkId = GetSymbolLinkId(cog, CogSymbolType.Thing, thingId);
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(cog, "sighted", senderRef: thingId, senderId: linkId, sourceRef: player.ThingIndex, senderType: 3));
            }
        }
    }

    private CogInstance? GetOrCreateTemplateCog(string scriptName)
    {
        if (_templateCogInstances.TryGetValue(scriptName, out var cached))
            return cached;

        var script = LoadCogScript(scriptName);
        if (script == null)
        {
            _templateCogInstances[scriptName] = null;
            return null;
        }

        var instance = new CogInstance(script, _nextDynamicCogId++);
        _cogVm.AddInstance(instance);
        _templateCogInstances[scriptName] = instance;
        return instance;
    }

    private void UpdateLifeleftTimers(float dt)
    {
        if (_thingLifeleft.Count == 0)
            return;

        List<int>? expired = null;
        foreach (var (thingId, remaining) in _thingLifeleft)
        {
            float next = remaining - dt;
            if (next <= 0f)
            {
                expired ??= [];
                expired.Add(thingId);
            }
            else
            {
                _thingLifeleft[thingId] = next;
            }
        }

        if (expired != null)
        {
            foreach (int thingId in expired)
                DestroyThing(thingId);
        }
    }

    private void CheckDeathFloorSurfaces(PlayerState player, Vector3 playerPos, int playerSector)
    {
        if (player.Inventory.IsDead || playerSector < 0 || playerSector >= _level.Sectors.Count)
            return;

        var sector = _level.Sectors[playerSector];
        foreach (var surface in sector.Surfaces)
        {
            if (surface.IsPortal || surface.VertexIndices.Length < 3)
                continue;

            if (!_surfaceToCogs.TryGetValue(surface.SurfaceIndex, out var cogs))
                continue;

            int v0Idx = surface.VertexIndices[0];
            if (v0Idx < 0 || v0Idx >= _level.Vertices.Count)
                continue;

            Vector3 v0 = _level.Vertices[v0Idx];
            float distToPlane = Vector3.Dot(playerPos - v0, surface.Normal);
            if (MathF.Abs(distToPlane) > 0.08f)
                continue;

            Vector3 projected = playerPos - surface.Normal * distToPlane;
            if (!IsPointInsidePolygon3D(projected, surface))
                continue;

            foreach (var cog in cogs)
            {
                if (cog.Program.Labels.ContainsKey("touched"))
                {
                    int symLinkId = GetSymbolLinkId(cog, CogSymbolType.Surface, surface.SurfaceIndex);
                    WithPlayerCogContext(player.ThingIndex, () =>
                        SafeSendMessage(cog, "touched", senderRef: surface.SurfaceIndex, senderId: symLinkId, sourceRef: player.ThingIndex, senderType: 6));
                }
            }
        }
    }

    private void UpdatePlayerSectorTransitions(PlayerState player, int curSector)
    {
        if (player.PrevSectorIndex == curSector)
            return;

        int prevSector = player.PrevSectorIndex;
        player.PrevSectorIndex = curSector;

        if (player.ThingIndex == _localPlayerThingIndex)
            UpdateSectorAmbientSound(curSector);

        if (prevSector >= 0 && prevSector < _level.Sectors.Count && curSector >= 0 && curSector < _level.Sectors.Count)
        {
            var prevSecObj = _level.Sectors[prevSector];
            foreach (var surf in prevSecObj.Surfaces)
            {
                if (!surf.IsPortal || surf.MirrorSector?.SectorIndex != curSector)
                    continue;

                DispatchSurfaceCrossed(player, surf.SurfaceIndex);
                if (surf.MirrorSurface != null)
                    DispatchSurfaceCrossed(player, surf.MirrorSurface.SurfaceIndex);
            }
        }

        if (prevSector >= 0 && _sectorToCogs.TryGetValue(prevSector, out var prevSectorCogs))
        {
            foreach (var cog in prevSectorCogs)
            {
                if (cog.Program.Labels.ContainsKey("exited"))
                {
                    int symLinkId = GetSymbolLinkId(cog, CogSymbolType.Sector, prevSector);
                    WithPlayerCogContext(player.ThingIndex, () =>
                        SafeSendMessage(cog, "exited", senderRef: prevSector, senderId: symLinkId, sourceRef: player.ThingIndex, senderType: 5));
                }
            }
        }

        if (curSector >= 0 && _sectorToCogs.TryGetValue(curSector, out var sectorCogs))
        {
            foreach (var cog in sectorCogs)
            {
                if (cog.Program.Labels.ContainsKey("entered"))
                {
                    int symLinkId = GetSymbolLinkId(cog, CogSymbolType.Sector, curSector);
                    WithPlayerCogContext(player.ThingIndex, () =>
                        SafeSendMessage(cog, "entered", senderRef: curSector, senderId: symLinkId, sourceRef: player.ThingIndex, senderType: 5));
                }
            }
        }
    }

    private void DispatchSurfaceCrossed(PlayerState player, int surfaceIndex)
    {
        if (!_surfaceToCogs.TryGetValue(surfaceIndex, out var cogs))
            return;

        foreach (var cog in cogs)
        {
            int symLinkId = GetSymbolLinkId(cog, CogSymbolType.Surface, surfaceIndex);
            if (cog.Program.Labels.ContainsKey("crossed"))
            {
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(cog, "crossed", senderRef: surfaceIndex, senderId: symLinkId, sourceRef: player.ThingIndex, senderType: 6));
            }

            if (cog.Program.Labels.ContainsKey("entered"))
            {
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(cog, "entered", senderRef: surfaceIndex, senderId: symLinkId, sourceRef: player.ThingIndex, senderType: 6));
            }
        }
    }

    private void UpdateThingMotions(float dt)
    {
        foreach (var (thingId, motion) in _thingMotions)
        {
            motion.LastDeltaZ = 0f;
            if (!motion.IsMoving || motion.IsPaused || thingId < 0 || thingId >= _level.Things.Count)
                continue;

            var thing = _level.Things[thingId];
            Vector3 prevPos = thing.Position;
            Vector3 prevPyr = thing.PitchYawRoll;

            // Check if a closing door (moving toward frame 0) is blocked by any player inside its sector
            if (!motion.IsPivotRotation && motion.TargetFrame == 0)
            {
                bool blockedByPlayer = false;
                foreach (var player in _players)
                {
                    if (player.Inventory.IsDead || player.ThingIndex < 0 || player.ThingIndex >= _level.Things.Count)
                        continue;
                    var pThing = _level.Things[player.ThingIndex];
                    if (pThing.SectorIndex == thing.SectorIndex &&
                        Vector3.DistanceSquared(pThing.Position, thing.Position) < 0.09f)
                    {
                        blockedByPlayer = true;
                        break;
                    }
                }

                if (blockedByPlayer && _thingToCogs.TryGetValue(thingId, out var doorCogs))
                {
                    foreach (var cog in doorCogs)
                    {
                        if (cog.Program.Labels.ContainsKey("blocked"))
                            SafeSendMessage(cog, "blocked", thingId, senderType: 3);
                    }
                }
            }

            motion.Elapsed += dt;
            float t = motion.Duration > 1e-5f ? Math.Clamp(motion.Elapsed / motion.Duration, 0f, 1f) : 1f;
            SithMatrix34 prevRotMat = SithMatrix34.Build34(prevPyr, Vector3.Zero);

            if (motion.IsPivotRotation)
            {
                Vector3 curAngleDelta = motion.TargetDeltaPyr * t;
                SithMatrix34 deltaRot = SithMatrix34.Build34(curAngleDelta, Vector3.Zero);
                SithMatrix34 startRot = SithMatrix34.Build34(motion.StartPyr, Vector3.Zero);
                SithMatrix34 curRot = SithMatrix34.Multiply(deltaRot, startRot);
                thing.Position = motion.PivotWorldPos + deltaRot.TransformNormal(motion.StartOffsetFromPivot);
                thing.PitchYawRoll = SithMatrix34.ExtractPyr(curRot);
            }
            else
            {
                thing.Position = Vector3.Lerp(motion.StartPos, motion.TargetPos, t);
                thing.PitchYawRoll = new Vector3(
                    LerpAngleDegrees(motion.StartPyr.X, motion.TargetPyr.X, t),
                    LerpAngleDegrees(motion.StartPyr.Y, motion.TargetPyr.Y, t),
                    LerpAngleDegrees(motion.StartPyr.Z, motion.TargetPyr.Z, t)
                );
            }

            if (t >= 1f)
            {
                if (!motion.IsPivotRotation)
                {
                    thing.Position = motion.TargetPos;
                    thing.PitchYawRoll = motion.TargetPyr;
                }
                else
                {
                    SithMatrix34 finalDeltaRot = SithMatrix34.Build34(motion.TargetDeltaPyr, Vector3.Zero);
                    SithMatrix34 startRot = SithMatrix34.Build34(motion.StartPyr, Vector3.Zero);
                    SithMatrix34 finalRot = SithMatrix34.Multiply(finalDeltaRot, startRot);
                    thing.Position = motion.PivotWorldPos + finalDeltaRot.TransformNormal(motion.StartOffsetFromPivot);
                    thing.PitchYawRoll = SithMatrix34.ExtractPyr(finalRot);
                }
            }

            Vector3 stepDelta = thing.Position - prevPos;
            SithMatrix34 curRotMat = SithMatrix34.Build34(thing.PitchYawRoll, Vector3.Zero);
            SithMatrix34 stepRotMat = SithMatrix34.Multiply(curRotMat, prevRotMat.TransposeRotation());
            bool hasRotDelta = Vector3.DistanceSquared(prevPyr, thing.PitchYawRoll) > 1e-8f;
            motion.LastDeltaZ = stepDelta.Z;

            PropagateAttachedChildren(thingId, prevPos, thing.Position, stepDelta, stepRotMat, hasRotDelta);

            // Carry the player and any NPCs standing on a moving elevator/platform
            bool isElevatorOrPlatform =
                thing.TemplateName.Contains("elev", StringComparison.OrdinalIgnoreCase) ||
                thing.TemplateName.Contains("funicular", StringComparison.OrdinalIgnoreCase);

            if (isElevatorOrPlatform)
            {
                foreach (var player in _players)
                {
                    if (player.ThingIndex < 0 || player.ThingIndex >= _level.Things.Count)
                        continue;

                    Vector3 pPos = _level.Things[player.ThingIndex].Position;
                    float horizDistSq = Vector2.DistanceSquared(
                        new Vector2(pPos.X, pPos.Y),
                        new Vector2(prevPos.X, prevPos.Y));

                    if (horizDistSq < 0.16f && MathF.Abs(pPos.Z - prevPos.Z) < 0.22f)
                        player.PendingPlatformDelta += stepDelta;
                }
            }

            if (stepDelta.LengthSquared() > 1e-12f)
            {
                foreach (var npc in _npcs.Values)
                {
                    if (!npc.IsAlive)
                        continue;

                    bool onThisThing = npc.OnElevatorThingId == thingId;
                    if (!onThisThing && isElevatorOrPlatform)
                    {
                        float maxTopZ = npc.Thing.Position.Z - npc.HipHeightOffset + 0.08f;
                        if (TryGetThingFloorZForThing(thing, npc.Thing.Position, maxTopZ, out float topZ) &&
                            MathF.Abs((topZ + npc.HipHeightOffset) - npc.Thing.Position.Z) <= 0.10f)
                        {
                            onThisThing = true;
                        }
                    }

                    if (onThisThing)
                    {
                        npc.Thing.Position += stepDelta;
                        npc.OnElevatorThingId = thingId;
                    }
                }
            }

            if (t >= 1f)
            {
                motion.CurrentFrame = motion.TargetFrame;
                motion.IsMoving = false;
                motion.IsPivotRotation = false;

                if (_thingToCogs.TryGetValue(thingId, out var cogs))
                {
                    foreach (var cog in cogs)
                    {
                        if (cog.Program.Labels.ContainsKey("arrived"))
                        {
                            int linkId = GetSymbolLinkId(cog, CogSymbolType.Thing, thingId);
                            SafeSendMessage(cog, "arrived", senderRef: thingId, senderId: linkId, sourceRef: thingId, senderType: 3);
                        }
                    }
                }
            }
        }
    }

    private static Vector3 GetThingForward(JklThing thing)
    {
        float yawRad = thing.PitchYawRoll.Y * (MathF.PI / 180f);
        return new Vector3(-MathF.Sin(yawRad), MathF.Cos(yawRad), 0f);
    }

    private void UpdateNpcs(float dt)
    {
        var playerCandidates = new List<AiTargetCandidate>(_players.Count);
        foreach (var p in _players)
        {
            if (p.ThingIndex < 0 || p.ThingIndex >= _level.Things.Count)
                continue;

            bool pVisible = (GetActorFlags(p.ThingIndex) & 0x80) == 0;
            playerCandidates.Add(new AiTargetCandidate(
                p.ThingIndex,
                _level.Things[p.ThingIndex].Position,
                alignment: 1.0f,
                isAlive: !p.Inventory.IsDead,
                isVisible: pVisible));
        }

        foreach (var npc in _npcs.Values)
        {
            npc.LastDistanceMoved = 0f;
            if (!npc.IsAlive)
                continue;

            if (npc.BlindTimer > 0f)
                npc.BlindTimer = MathF.Max(0f, npc.BlindTimer - dt);

            if (npc.BlockAnimTimer > 0f)
                npc.BlockAnimTimer = MathF.Max(0f, npc.BlockAnimTimer - dt);
            if (npc.FireAnimTimer > 0f)
                npc.FireAnimTimer = MathF.Max(0f, npc.FireAnimTimer - dt);
            if (npc.HitAnimTimer > 0f)
                npc.HitAnimTimer = MathF.Max(0f, npc.HitAnimTimer - dt);

            if (npc.Brain.IsAiPaused || IsActorFrozen(npc.Thing.ThingIndex) || npc.BlindTimer > 0f)
            {
                npc.AnimAction = "stand";
                npc.AnimMode = 0;
                npc.LoopAnim = true;
                continue;
            }

            if (_sectorThrusts.TryGetValue(npc.Thing.SectorIndex, out var sectorThrust) && sectorThrust.LengthSquared() > 1e-6f)
            {
                npc.Velocity += sectorThrust * dt;
                if (sectorThrust.Z > 0f || npc.Velocity.LengthSquared() > 0.01f)
                    npc.IsAirborne = true;
            }

            if (npc.IsAirborne || npc.Velocity.LengthSquared() > 1e-6f)
            {
                UpdateAirborneNpc(npc, dt);
            }

            if (npc.OverrideLookTimer > 0f)
            {
                npc.OverrideLookTimer = MathF.Max(0f, npc.OverrideLookTimer - dt);
                if (npc.OverrideLookTimer <= 0f)
                    npc.OverrideLookPos = null;
            }

            Vector3 lookDir = GetThingForward(npc.Thing);

            bool isHostile = npc.Brain.Class.HasInstinct("PrimaryFire")
                || npc.Brain.Class.HasInstinct("SaberFighting")
                || npc.Brain.Class.HasInstinct("LookForTarget")
                || npc.WasDamagedThisTick
                || npc.OverrideFireTargetId.HasValue;
            IReadOnlyList<AiTargetCandidate> targets = playerCandidates;
            if (npc.OverrideFireTargetId is int fireTargetId &&
                fireTargetId >= 0 &&
                fireTargetId < _level.Things.Count &&
                !_playersByThing.ContainsKey(fireTargetId) &&
                !_destroyedThings.Contains(fireTargetId))
            {
                bool targetAlive = !_npcs.TryGetValue(fireTargetId, out var tNpc) || tNpc.IsAlive;
                targets = new[]
                {
                    new AiTargetCandidate(fireTargetId, _level.Things[fireTargetId].Position, alignment: 1.0f, isAlive: targetAlive, isVisible: true)
                };
                npc.Brain.TargetThingIndex = fireTargetId;
                npc.Brain.LastKnownTargetPosition = _level.Things[fireTargetId].Position;
                npc.Brain.Mode |= AiMode.Active | AiMode.TargetVisible;
            }
            var aiContext = new AiWorldContext
            {
                SelfPosition = npc.Thing.Position,
                SelfLookDir = lookDir,
                Health = npc.Health,
                MaxHealth = npc.MaxHealth,
                WasDamaged = npc.WasDamagedThisTick,
                DamagedByThingIndex = npc.WasDamagedThisTick ? (npc.LastDamagerThingId >= 0 ? npc.LastDamagerThingId : (_localPlayerThingIndex >= 0 ? _localPlayerThingIndex : null)) : null,
                DamageSourcePosition = npc.LastDamageSourcePos,
                Targets = isHostile ? targets : Array.Empty<AiTargetCandidate>(),
                Sounds = _soundEvents,
                LineOfSightCheck = HasNpcLineOfSight
            };

            npc.WasDamagedThisTick = false;

            var decision = npc.Brain.Update(dt, aiContext);

            // Resolve active target player for combat checks
            PlayerState? targetPlayer = null;
            if (npc.Brain.TargetThingIndex is int targetId && _playersByThing.TryGetValue(targetId, out var targeted))
                targetPlayer = targeted;
            else
                targetPlayer = FindNearestAlivePlayer(npc.Thing.Position);

            Vector3 targetPos = targetPlayer != null && targetPlayer.ThingIndex < _level.Things.Count
                ? _level.Things[targetPlayer.ThingIndex].Position
                : decision.DesiredLookTarget;

            // Smoothly turn toward desired look target (or COG AISetLookPos override)
            Vector3 desiredLookTarget = npc.OverrideLookPos ?? decision.DesiredLookTarget;
            Vector3 toLook = desiredLookTarget - npc.Thing.Position;
            Vector2 lookXY = new Vector2(toLook.X, toLook.Y);
            if (lookXY.LengthSquared() > 1e-5f)
            {
                float desiredYaw = MathF.Atan2(-lookXY.X, lookXY.Y) * (180f / MathF.PI);
                float curYaw = npc.Thing.PitchYawRoll.Y;
                float deltaYaw = WrapAngle180(desiredYaw - curYaw);
                float maxTurn = 180f * dt;
                float newYaw = curYaw + Math.Clamp(deltaYaw, -maxTurn, maxTurn);
                npc.Thing.PitchYawRoll = new Vector3(npc.Thing.PitchYawRoll.X, newYaw, npc.Thing.PitchYawRoll.Z);
            }

            // Move NPC with wall & floor containment (honoring COG AISetMovePos override)
            bool moved = false;
            if (!npc.IsAirborne)
            {
                Vector3 moveDir = Vector3.Zero;
                float speedScale = decision.ShouldFlee ? 1.25f : 1.0f;
                if (npc.OverrideMoveThingId is int moveThingId &&
                    moveThingId >= 0 &&
                    moveThingId < _level.Things.Count &&
                    !_destroyedThings.Contains(moveThingId))
                {
                    npc.OverrideMovePos = _level.Things[moveThingId].Position;
                }

                if (npc.OverrideMovePos is Vector3 overrideMove)
                {
                    Vector3 toOverride = new(overrideMove.X - npc.Thing.Position.X, overrideMove.Y - npc.Thing.Position.Y, 0f);
                    if (toOverride.LengthSquared() <= 0.12f * 0.12f)
                    {
                        npc.OverrideMovePos = null;
                        npc.OverrideMoveThingId = null;
                    }
                    else
                    {
                        moveDir = Vector3.Normalize(toOverride);
                    }
                }
                else if (decision.HasMovement)
                {
                    moveDir = new Vector3(decision.DesiredMovementDirection.X, decision.DesiredMovementDirection.Y, 0f);
                    if (moveDir.LengthSquared() > 1e-5f)
                        moveDir = Vector3.Normalize(moveDir);
                }

                if (moveDir.LengthSquared() > 1e-5f)
                {
                    float extraSpeedScale = MathF.Max(0.1f, 1.0f + GetActorExtraSpeed(npc.Thing.ThingIndex));
                    float? targetZ = npc.UseGravity ? null : (npc.OverrideMovePos?.Z ?? desiredLookTarget.Z);
                    moved = TryMoveNpc(npc, moveDir, npc.MoveSpeed * speedScale * extraSpeedScale * dt, targetZ);
                }
                else if (npc.UseGravity)
                {
                    if (TryGetFloorZBelow(npc.Thing.SectorIndex, npc.Thing.Position, out float floorZ) &&
                        npc.Thing.Position.Z > floorZ + npc.HipHeightOffset + 0.005f)
                    {
                        npc.IsAirborne = true;
                        UpdateAirborneNpc(npc, dt);
                    }
                }
                else
                {
                    float targetZ = npc.OverrideMovePos?.Z ?? desiredLookTarget.Z;
                    if (MathF.Abs(targetZ - npc.Thing.Position.Z) > 0.02f)
                    {
                        float extraSpeedScale = MathF.Max(0.1f, 1.0f + GetActorExtraSpeed(npc.Thing.ThingIndex));
                        moved = TryMoveNpc(npc, Vector3.Zero, npc.MoveSpeed * speedScale * extraSpeedScale * dt, targetZ);
                    }
                }
            }

            if (decision.ShouldFire)
            {
                var pf = npc.Brain.Class.FindInstinct("PrimaryFire");
                float attackRange = pf != null && pf.FloatArgs[2] > 0f ? pf.FloatArgs[2] : 3.5f;
                string? puppetName = ResolveTemplateParam(npc.Thing.TemplateName, "puppet");
                bool isMeleeNpc = attackRange <= 0.45f ||
                    (!string.IsNullOrEmpty(puppetName) && puppetName.Contains("fist", StringComparison.OrdinalIgnoreCase));

                if (isMeleeNpc)
                {
                    if (targetPlayer != null && Vector3.Distance(npc.Thing.Position, targetPos) <= 0.45f)
                    {
                        npc.FireAnimTimer = 0.45f;
                        npc.FireAnimAction = "fire";
                        npc.LoopAnim = false;
                        _audio?.PlaySoundPos("swingfist01.wav", npc.Thing.Position, 0.8f, 0.4f, 4.0f);
                    }
                }
                else
                {
                    npc.FireAnimTimer = 0.45f;
                    npc.FireAnimAction = "fire";
                    npc.LoopAnim = false;
                    Vector3 muzzle = npc.Thing.Position + new Vector3(0f, 0f, 0.10f);
                    Vector3 aimTarget = targetPos + new Vector3(0f, 0f, 0.10f);
                    Vector3 fireDir = aimTarget - muzzle;
                    if (fireDir.LengthSquared() > 1e-6f)
                    {
                        fireDir = Vector3.Normalize(fireDir);
                        var (boltModel, boltColor) = ResolveNpcProjectileStyle(npc.Thing);
                        SpawnProjectile(
                            muzzle + fireDir * 0.08f,
                            fireDir,
                            damage: 10.0f,
                            ownerThingIndex: npc.Thing.ThingIndex,
                            isEnemy: true,
                            modelName: boltModel,
                            boltColor: boltColor);
                        _audio?.PlaySoundPos("trprsht1.wav", muzzle, 0.85f, 0.5f, 7.0f);
                    }
                }
            }

            if (npc.Brain.SaberAttackRequested)
            {
                npc.FireAnimTimer = 0.45f;
                npc.FireAnimAction = npc.Brain.SaberAttackIndex == 2 ? "fire2" : "fire";
                npc.LoopAnim = false;
                _audio?.PlaySoundPos("ltsaberswing01.wav", npc.Thing.Position, 0.85f, 0.5f, 5.0f);

                float attRange = MathF.Max(npc.Brain.SaberAttackRange, 0.25f);
                if (targetPlayer != null &&
                    !targetPlayer.Inventory.IsDead &&
                    Vector3.Distance(npc.Thing.Position, targetPos) <= attRange &&
                    HasNpcLineOfSight(npc.Thing.Position, targetPos))
                {
                    ApplyDamageToPlayer(targetPlayer, npc.Thing.ThingIndex, npc.Brain.SaberAttackDamage, "ltsaberbodyhit01.wav");
                }
            }

            if (npc.Brain.SpecialAttackRequested)
            {
                npc.FireAnimTimer = 0.65f;
                npc.FireAnimAction = "fire3";
                npc.LoopAnim = false;
                _audio?.PlaySoundPos("ltsaberswing02.wav", npc.Thing.Position, 0.9f, 0.5f, 6.0f);

                Vector3 toTargetXY = new Vector3(targetPos.X - npc.Thing.Position.X, targetPos.Y - npc.Thing.Position.Y, 0f);
                if (toTargetXY.LengthSquared() > 1e-6f)
                {
                    Vector3 lungeDir = Vector3.Normalize(toTargetXY);
                    TryMoveNpc(npc, lungeDir, MathF.Min(0.35f, toTargetXY.Length()), npc.UseGravity ? null : targetPos.Z);
                }

                float specialRange = MathF.Max(npc.Brain.Class.Saberfighting?.Att1Range ?? 0.35f, 0.35f);
                if (targetPlayer != null &&
                    !targetPlayer.Inventory.IsDead &&
                    Vector3.Distance(npc.Thing.Position, targetPos) <= specialRange &&
                    HasNpcLineOfSight(npc.Thing.Position, targetPos))
                {
                    ApplyDamageToPlayer(targetPlayer, npc.Thing.ThingIndex, npc.Brain.SpecialAttackDamage, "ltsaberbodyhit01.wav");
                }
            }

            if (npc.Brain.ForcePowerRequested > 0)
            {
                int targetThingIdx = targetPlayer?.ThingIndex ?? _localPlayerThingIndex;
                DispatchThingCogMessage(
                    npc.Thing.ThingIndex,
                    "user0",
                    new CogValue(targetThingIdx),
                    new CogValue(npc.Brain.ForcePowerRequested));
            }

            bool isAlert = (decision.NewMode & (AiMode.Attacking | AiMode.Searching)) != 0;
            if (npc.BlockAnimTimer > 0f)
            {
                npc.AnimAction = "block";
                npc.AnimMode = 1;
                npc.LoopAnim = false;
            }
            else if (npc.FireAnimTimer > 0f)
            {
                npc.AnimAction = npc.FireAnimAction;
                npc.AnimMode = 1;
                npc.LoopAnim = false;
            }
            else if (npc.HitAnimTimer > 0f)
            {
                npc.AnimAction = "hit";
                npc.AnimMode = 0;
                npc.LoopAnim = false;
            }
            else if (moved)
            {
                npc.AnimAction = "walk";
                npc.AnimMode = isAlert ? 1 : 0;
                npc.LoopAnim = true;
            }
            else
            {
                npc.AnimAction = "stand";
                npc.AnimMode = isAlert ? 1 : 0;
                npc.LoopAnim = true;
            }
        }
    }

    private void UpdateAirborneNpc(NpcRuntime npc, float dt)
    {
        float gravity = npc.UseGravity ? 3.5f : 0f;
        npc.Velocity = new Vector3(npc.Velocity.X, npc.Velocity.Y, npc.Velocity.Z - gravity * dt);

        if (!npc.UseGravity)
        {
            npc.Velocity *= MathF.Max(0f, 1f - 4.0f * dt);
            if (npc.Velocity.LengthSquared() < 1e-4f)
            {
                npc.Velocity = Vector3.Zero;
                npc.IsAirborne = false;
            }
        }

        Vector3 oldPos = npc.Thing.Position;
        Vector3 nextPos = oldPos + npc.Velocity * dt;

        // Update sector index if NPC crossed into an adjoined sector
        var activeSectors = GatherActiveSectors(npc.Thing.SectorIndex);
        foreach (var sec in activeSectors)
        {
            foreach (var surface in sec.Surfaces)
            {
                if (surface.IsPortal || surface.VertexIndices.Length < 3 || surface.Normal.Z <= 0.4f)
                    continue;

                Vector3[] verts = GetSurfaceVertices(surface);
                if (IsPointInsidePolygon2D(nextPos.X, nextPos.Y, verts))
                {
                    npc.Thing.SectorIndex = sec.SectorIndex;
                    break;
                }
            }
        }

        if (npc.Velocity.Z <= 0f && TryGetFloorZBelow(npc.Thing.SectorIndex, nextPos, out float floorZ))
        {
            if (nextPos.Z <= floorZ + npc.HipHeightOffset)
            {
                nextPos.Z = floorZ + npc.HipHeightOffset;
                npc.Velocity = Vector3.Zero;
                npc.IsAirborne = false;
            }
        }

        npc.Thing.Position = nextPos;
        npc.LastDistanceMoved = Vector2.Distance(new Vector2(oldPos.X, oldPos.Y), new Vector2(nextPos.X, nextPos.Y));
    }

    private bool TryMoveNpc(NpcRuntime npc, Vector3 moveDir, float stepDist, float? targetZ = null)
    {
        if (npc.Thing.SectorIndex < 0 || npc.Thing.SectorIndex >= _level.Sectors.Count)
            return false;

        Vector3 oldPos = npc.Thing.Position;
        Vector3 proposed = oldPos + moveDir * stepDist;

        var activeSectors = GatherActiveSectors(npc.Thing.SectorIndex);
        float testZ = proposed.Z;

        // Horizontal wall push against solid surfaces
        foreach (var sec in activeSectors)
        {
            foreach (var surface in sec.Surfaces)
            {
                if (surface.IsPortal || surface.VertexIndices.Length < 3)
                    continue;

                if (MathF.Abs(surface.Normal.Z) > 0.4f)
                    continue;

                Vector3[] polyVerts = GetSurfaceVertices(surface);
                Vector3 testCenter = new Vector3(proposed.X, proposed.Y, testZ);
                Vector3 closest = PlayerPhysics.ClosestPointOnConvexPolygon(testCenter, polyVerts, surface.Normal);
                Vector3 diff = testCenter - closest;
                float dist = diff.Length();

                if (dist < npc.Radius)
                {
                    Vector3 pushDir = dist > 1e-4f ? diff / dist : surface.Normal;
                    pushDir = new Vector3(pushDir.X, pushDir.Y, 0f);
                    if (pushDir.LengthSquared() > 1e-4f)
                    {
                        pushDir = Vector3.Normalize(pushDir);
                        proposed += pushDir * (npc.Radius - dist);
                    }
                }
            }
        }

        // Verify valid floor (and ceiling for flying _flyactor NPCs) at proposed position
        float bestFloorZ = float.MinValue;
        int bestSector = npc.Thing.SectorIndex;
        int bestCarrierThingId = -1;
        bool foundFloor = false;
        float bestCeilingZ = float.MaxValue;
        bool foundCeiling = false;
        float maxStep = npc.Brain.Class.MaxStep > 0f ? MathF.Min(npc.Brain.Class.MaxStep, 0.25f) : 0.20f;

        foreach (var sec in activeSectors)
        {
            foreach (var surface in sec.Surfaces)
            {
                if (surface.IsPortal || surface.VertexIndices.Length < 3)
                    continue;

                if (surface.Normal.Z > 0.4f)
                {
                    Vector3[] floorVerts = GetSurfaceVertices(surface);
                    if (!IsPointInsidePolygon2D(proposed.X, proposed.Y, floorVerts))
                        continue;

                    Vector3 v0 = floorVerts[0];
                    float planeZ = v0.Z - ((surface.Normal.X * (proposed.X - v0.X) + surface.Normal.Y * (proposed.Y - v0.Y)) / surface.Normal.Z);
                    bool withinStep = npc.UseGravity
                        ? MathF.Abs((planeZ + npc.HipHeightOffset) - oldPos.Z) <= maxStep
                        : planeZ <= oldPos.Z - npc.HipHeightOffset + maxStep;
                    if (withinStep && planeZ > bestFloorZ)
                    {
                        bestFloorZ = planeZ;
                        bestSector = sec.SectorIndex;
                        foundFloor = true;
                    }
                }
                else if (!npc.UseGravity && surface.Normal.Z < -0.4f)
                {
                    Vector3[] ceilVerts = GetSurfaceVertices(surface);
                    if (!IsPointInsidePolygon2D(proposed.X, proposed.Y, ceilVerts))
                        continue;

                    Vector3 v0 = ceilVerts[0];
                    float planeZ = v0.Z - ((surface.Normal.X * (proposed.X - v0.X) + surface.Normal.Y * (proposed.Y - v0.Y)) / surface.Normal.Z);
                    if (planeZ >= oldPos.Z - 0.05f && planeZ < bestCeilingZ)
                    {
                        bestCeilingZ = planeZ;
                        foundCeiling = true;
                    }
                }
            }
        }

        float maxThingFloorZ = oldPos.Z - npc.HipHeightOffset + maxStep;
        if (TryGetThingFloorZBelow(proposed, maxThingFloorZ, out float thingFloorZ, out int carrierThingId))
        {
            bool withinStep = !npc.UseGravity || MathF.Abs((thingFloorZ + npc.HipHeightOffset) - oldPos.Z) <= maxStep;
            if (withinStep && thingFloorZ > bestFloorZ)
            {
                bestFloorZ = thingFloorZ;
                bestCarrierThingId = carrierThingId;
                foundFloor = true;
            }
        }

        if (!foundFloor && (npc.UseGravity || !foundCeiling))
            return false;

        float finalZ;
        if (npc.UseGravity)
        {
            finalZ = bestFloorZ + npc.HipHeightOffset;
        }
        else
        {
            float minZ = foundFloor ? bestFloorZ + npc.HipHeightOffset : oldPos.Z - 10f;
            float maxZ = foundCeiling ? bestCeilingZ - MathF.Max(npc.Radius, 0.08f) : float.MaxValue;
            if (maxZ < minZ)
                maxZ = minZ;

            float desiredZ = targetZ ?? oldPos.Z;
            float dz = desiredZ - oldPos.Z;
            float maxVerticalStep = MathF.Max(stepDist, 0f);
            float newZ = MathF.Abs(dz) <= maxVerticalStep
                ? desiredZ
                : oldPos.Z + MathF.Sign(dz) * maxVerticalStep;
            finalZ = Math.Clamp(newZ, minZ, maxZ);
        }

        Vector3 finalPos = new Vector3(proposed.X, proposed.Y, finalZ);
        float dist2D = Vector2.Distance(new Vector2(oldPos.X, oldPos.Y), new Vector2(finalPos.X, finalPos.Y));
        float distMoved = npc.UseGravity ? dist2D : Vector3.Distance(oldPos, finalPos);
        if (distMoved < 1e-4f)
            return false;

        npc.Thing.Position = finalPos;
        npc.Thing.SectorIndex = bestSector;
        npc.OnElevatorThingId = npc.UseGravity ? bestCarrierThingId : -1;
        npc.LastDistanceMoved = distMoved;
        return true;
    }

    private List<JklSector> GatherActiveSectors(int sectorIndex)
    {
        var result = new List<JklSector>(4);
        var cur = _level.Sectors[sectorIndex];
        result.Add(cur);

        foreach (var surf in cur.Surfaces)
        {
            if (surf.IsPortal && surf.MirrorSector != null && !result.Contains(surf.MirrorSector))
            {
                result.Add(surf.MirrorSector);
            }
        }
        return result;
    }

    private Vector3[] GetSurfaceVertices(JklSurface surface)
    {
        var verts = new Vector3[surface.VertexIndices.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            int idx = surface.VertexIndices[i];
            verts[i] = (idx >= 0 && idx < _level.Vertices.Count) ? _level.Vertices[idx] : Vector3.Zero;
        }
        return verts;
    }

    private static bool IsPointInsidePolygon2D(float px, float py, Vector3[] verts)
    {
        bool inside = false;
        int n = verts.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = verts[i].X, yi = verts[i].Y;
            float xj = verts[j].X, yj = verts[j].Y;
            bool intersect = ((yi > py) != (yj > py)) &&
                             (px < (xj - xi) * (py - yi) / ((yj - yi) == 0f ? 1e-6f : (yj - yi)) + xi);
            if (intersect)
                inside = !inside;
        }
        return inside;
    }

    private bool TryGetFloorZBelow(int sectorIndex, Vector3 pos, out float floorZ)
    {
        floorZ = float.MinValue;
        bool found = false;
        var activeSectors = GatherActiveSectors(sectorIndex);

        foreach (var sec in activeSectors)
        {
            foreach (var surface in sec.Surfaces)
            {
                if (surface.IsPortal || surface.VertexIndices.Length < 3 || surface.Normal.Z <= 0.4f)
                    continue;

                Vector3[] floorVerts = GetSurfaceVertices(surface);
                if (!IsPointInsidePolygon2D(pos.X, pos.Y, floorVerts))
                    continue;

                Vector3 v0 = floorVerts[0];
                float planeZ = v0.Z - ((surface.Normal.X * (pos.X - v0.X) + surface.Normal.Y * (pos.Y - v0.Y)) / surface.Normal.Z);
                if (planeZ <= pos.Z + 0.05f && (pos.Z - planeZ) <= 0.50f && planeZ > floorZ)
                {
                    floorZ = planeZ;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Returns the highest walkable 3DO floor Z at <paramref name="pos"/> (with planeZ &lt;= <paramref name="maxZ"/>)
    /// across solid/framed things (<c>NumFrames &gt; 0</c> or <c>collide == 3</c>), along with the carrier thing ID.
    /// </summary>
    private bool TryGetThingFloorZBelow(Vector3 pos, float maxZ, out float floorZ, out int carrierThingId)
    {
        floorZ = float.MinValue;
        carrierThingId = -1;
        bool found = false;

        foreach (var thing in _level.Things)
        {
            if ((thing.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0)
                continue;

            if (_playersByThing.ContainsKey(thing.ThingIndex) || thing.IsPlayerSpawn || _npcs.ContainsKey(thing.ThingIndex))
                continue;

            bool isFramed = _thingFrames.ContainsKey(thing.ThingIndex);
            bool isCollideFace = int.TryParse(ResolveTemplateParam(thing.TemplateName, "collide"), out int collideMode) && collideMode == 3;
            if (!isFramed && !isCollideFace)
                continue;

            if (TryGetThingFloorZForThing(thing, pos, maxZ, out float candidateZ) && candidateZ > floorZ)
            {
                floorZ = candidateZ;
                carrierThingId = thing.ThingIndex;
                found = true;
            }
        }

        return found;
    }

    private bool TryGetThingFloorZForThing(JklThing thing, Vector3 pos, float maxZ, out float floorZ)
    {
        floorZ = float.MinValue;

        string modelName = !string.IsNullOrEmpty(thing.Model3D)
            ? thing.Model3D
            : _level.ResolveModel3D(thing.TemplateName);
        var model = !string.IsNullOrEmpty(modelName) ? TryLoadThreeDo(modelName) : null;
        if (model == null || model.Meshes.Count == 0)
            return false;

        if (model.Radius > 0f)
        {
            float boundR = model.Radius + 0.25f;
            float distSqXY = Vector2.DistanceSquared(
                new Vector2(pos.X, pos.Y),
                new Vector2(thing.Position.X, thing.Position.Y));
            if (distSqXY > boundR * boundR)
                return false;
        }

        Matrix4x4 worldMat = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position).ToMatrix4x4();
        bool found = false;

        if (model.Nodes.Count > 0)
        {
            Matrix4x4[] nodeTransforms = ThreeDoHierarchy.ComputeTransforms(model);
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int meshIdx = model.Nodes[i].MeshIndex;
                if (meshIdx < 0 || meshIdx >= model.Meshes.Count)
                    continue;

                CheckMeshFloorZ(model.Meshes[meshIdx], nodeTransforms[i] * worldMat, pos, maxZ, ref floorZ, ref found);
            }
        }
        else
        {
            for (int m = 0; m < model.Meshes.Count; m++)
            {
                CheckMeshFloorZ(model.Meshes[m], worldMat, pos, maxZ, ref floorZ, ref found);
            }
        }

        return found;
    }

    private static void CheckMeshFloorZ(
        ThreeDoMesh mesh,
        Matrix4x4 meshToWorld,
        Vector3 pos,
        float maxZ,
        ref float bestZ,
        ref bool found)
    {
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            int vertCount = face.VertexIndices.Length;
            if (vertCount < 3)
                continue;

            var worldVerts = new Vector3[vertCount];
            bool valid = true;
            for (int v = 0; v < vertCount; v++)
            {
                int idx = face.VertexIndices[v];
                if (idx < 0 || idx >= mesh.Vertices.Count)
                {
                    valid = false;
                    break;
                }
                worldVerts[v] = Vector3.Transform(mesh.Vertices[idx], meshToWorld);
            }
            if (!valid)
                continue;

            Vector3 worldNormal = face.Normal.LengthSquared() >= 0.0001f
                ? Vector3.TransformNormal(face.Normal, meshToWorld)
                : Vector3.Cross(worldVerts[1] - worldVerts[0], worldVerts[2] - worldVerts[0]);

            if (worldNormal.LengthSquared() < 1e-8f)
                continue;

            worldNormal = Vector3.Normalize(worldNormal);
            if (worldNormal.Z <= 0.4f)
                continue;

            if (!IsPointInsidePolygon2D(pos.X, pos.Y, worldVerts))
                continue;

            Vector3 v0 = worldVerts[0];
            float planeZ = v0.Z - ((worldNormal.X * (pos.X - v0.X) + worldNormal.Y * (pos.Y - v0.Y)) / worldNormal.Z);
            if (planeZ <= maxZ && planeZ > bestZ)
            {
                bestZ = planeZ;
                found = true;
            }
        }
    }

    private bool HasNpcLineOfSight(Vector3 fromFeet, Vector3 toFeet)
    {
        var eyeOffset = new Vector3(0f, 0f, 0.12f);
        return HasLineOfSight(fromFeet + eyeOffset, toFeet + eyeOffset);
    }

    /// <summary>
    /// Returns true if the segment from <paramref name="a"/> to <paramref name="b"/> has unobstructed
    /// line of sight (blocked by neither solid level geometry nor closed doors).
    /// </summary>
    public bool HasLineOfSight(Vector3 a, Vector3 b) => !TryRaycastWorld(a, b, out _);

    /// <summary>
    /// Returns true if the given Thing is a framed door currently closed (at frame 0).
    /// </summary>
    public bool IsDoorClosed(int thingId)
    {
        return _thingMotions.TryGetValue(thingId, out var motion) && !motion.IsMoving && motion.CurrentFrame == 0;
    }

    /// <summary>
    /// Raycasts the segment from <paramref name="a"/> to <paramref name="b"/> against solid world surfaces
    /// and closed framed doors, outputting the closest hit point.
    /// </summary>
    public bool TryRaycastWorld(Vector3 a, Vector3 b, out Vector3 hitPoint) =>
        TryRaycastWorldWithHit(a, b, out hitPoint, out _, out _);

    private bool TryRaycastWorldWithHit(Vector3 a, Vector3 b, out Vector3 hitPoint, out int hitSurfaceId, out int hitDoorId)
    {
        hitPoint = b;
        hitSurfaceId = -1;
        hitDoorId = -1;
        Vector3 ray = b - a;
        float lenSq = ray.LengthSquared();
        if (lenSq < 1e-12f)
            return false;

        float rayLen = MathF.Sqrt(lenSq);
        Vector3 rayMin = Vector3.Min(a, b) - new Vector3(0.05f);
        Vector3 rayMax = Vector3.Max(a, b) + new Vector3(0.05f);
        float closestT = 1.0f;
        bool hitAny = false;

        foreach (var sector in _level.Sectors)
        {
            if (sector.BoundBoxMax.X < rayMin.X || sector.BoundBoxMin.X > rayMax.X ||
                sector.BoundBoxMax.Y < rayMin.Y || sector.BoundBoxMin.Y > rayMax.Y ||
                sector.BoundBoxMax.Z < rayMin.Z || sector.BoundBoxMin.Z > rayMax.Z)
            {
                continue;
            }

            foreach (var surface in sector.Surfaces)
            {
                bool isImpassablePortal = surface.AdjoinIndex >= 0 &&
                    surface.AdjoinIndex < _level.Adjoins.Count &&
                    (_level.Adjoins[surface.AdjoinIndex].Flags & 0x2) == 0;
                if ((surface.IsPortal && !isImpassablePortal) || surface.VertexIndices.Length < 3)
                    continue;

                float denom = Vector3.Dot(ray, surface.Normal);
                // Sith Engine surfaces are single-sided inward normals: only block rays hitting the front face
                if (denom >= -1e-6f * rayLen)
                    continue;

                int v0Idx = surface.VertexIndices[0];
                if (v0Idx < 0 || v0Idx >= _level.Vertices.Count)
                    continue;

                Vector3 v0 = _level.Vertices[v0Idx];
                float t = Vector3.Dot(v0 - a, surface.Normal) / denom;
                if (t < 0f || t > closestT || (hitAny && t >= closestT))
                    continue;

                Vector3 hit = a + ray * t;
                if (IsPointInsidePolygon3D(hit, surface))
                {
                    closestT = t;
                    hitPoint = hit;
                    hitSurfaceId = surface.SurfaceIndex;
                    hitDoorId = -1;
                    hitAny = true;
                }
            }
        }

        foreach (int doorId in _doorThingIds)
        {
            if (doorId < 0 || doorId >= _level.Things.Count || !IsDoorClosed(doorId))
                continue;

            var door = _level.Things[doorId];
            var (localMin, localMax) = GetDoorLocalBounds(door);
            Matrix4x4 worldMat = SithMatrix34.Build34(door.PitchYawRoll, door.Position).ToMatrix4x4();
            if (!Matrix4x4.Invert(worldMat, out Matrix4x4 invWorld))
                continue;

            Vector3 localA = Vector3.Transform(a, invWorld);
            Vector3 localB = Vector3.Transform(b, invWorld);
            if (TryIntersectSegmentAabb(localA, localB, localMin, localMax, out float t) &&
                t >= 0f && t < closestT)
            {
                closestT = t;
                hitPoint = a + ray * t;
                hitSurfaceId = -1;
                hitDoorId = doorId;
                hitAny = true;
            }
        }

        return hitAny;
    }

    private bool IsDoorThing(JklThing thing)
    {
        string current = thing.TemplateName;
        int guard = 0;
        while (!string.IsNullOrEmpty(current) && !current.Equals("none", StringComparison.OrdinalIgnoreCase) && guard++ < 32)
        {
            if (current.Contains("door", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!_level.Templates.TryGetValue(current, out var tmpl))
                break;

            current = tmpl.BasedOn;
        }
        return false;
    }

    private (Vector3 Min, Vector3 Max) GetDoorLocalBounds(JklThing door)
    {
        string modelName = !string.IsNullOrEmpty(door.Model3D)
            ? door.Model3D
            : _level.ResolveModel3D(door.TemplateName);

        string cacheKey = !string.IsNullOrEmpty(modelName) ? modelName : $"__tmpl:{door.TemplateName}";
        if (_doorBoundsCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (!string.IsNullOrEmpty(modelName) && TryLoadThreeDoBounds(modelName, out var modelBounds))
        {
            _doorBoundsCache[cacheKey] = modelBounds;
            return modelBounds;
        }

        float size = ParseTemplateFloat(door.TemplateName, "size", 0.25f);
        float halfExtent = MathF.Max(size, 0.20f);
        var fallback = (
            new Vector3(-halfExtent, -halfExtent, -halfExtent * 1.5f),
            new Vector3(halfExtent, halfExtent, halfExtent * 1.5f)
        );
        _doorBoundsCache[cacheKey] = fallback;
        return fallback;
    }

    private ThreeDoModel? TryLoadThreeDo(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;

        if (_threeDoCache.TryGetValue(modelName, out var cached))
            return cached;

        string[] searchPaths = [$"3do/{modelName}", $"3do/mat/{modelName}", modelName];
        foreach (var path in searchPaths)
        {
            if (!_vfs.FileExists(path))
                continue;

            try
            {
                var model = ThreeDoReader.Parse(_vfs.ReadAllText(path));
                _threeDoCache[modelName] = model;
                return model;
            }
            catch
            {
                // Try next path
            }
        }

        _threeDoCache[modelName] = null;
        return null;
    }

    private bool TryLoadThreeDoBounds(string modelName, out (Vector3 Min, Vector3 Max) bounds)
    {
        var model = TryLoadThreeDo(modelName);
        if (model != null)
        {
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            bool hasVerts = false;

            if (model.Nodes.Count > 0)
            {
                Matrix4x4[] nodeTransforms = ThreeDoHierarchy.ComputeTransforms(model);
                for (int i = 0; i < model.Nodes.Count; i++)
                {
                    int meshIdx = model.Nodes[i].MeshIndex;
                    if (meshIdx < 0 || meshIdx >= model.Meshes.Count)
                        continue;

                    foreach (var v in model.Meshes[meshIdx].Vertices)
                    {
                        Vector3 tv = Vector3.Transform(v, nodeTransforms[i]);
                        min = Vector3.Min(min, tv);
                        max = Vector3.Max(max, tv);
                        hasVerts = true;
                    }
                }
            }
            else
            {
                foreach (var mesh in model.Meshes)
                {
                    foreach (var v in mesh.Vertices)
                    {
                        min = Vector3.Min(min, v);
                        max = Vector3.Max(max, v);
                        hasVerts = true;
                    }
                }
            }

            if (hasVerts)
            {
                const float minThickness = 0.04f;
                Vector3 ext = max - min;
                if (ext.X < minThickness) { float pad = (minThickness - ext.X) * 0.5f; min.X -= pad; max.X += pad; }
                if (ext.Y < minThickness) { float pad = (minThickness - ext.Y) * 0.5f; min.Y -= pad; max.Y += pad; }
                if (ext.Z < minThickness) { float pad = (minThickness - ext.Z) * 0.5f; min.Z -= pad; max.Z += pad; }
                bounds = (min, max);
                return true;
            }
        }

        bounds = default;
        return false;
    }

    private static bool TryIntersectSegmentAabb(
        Vector3 a, Vector3 b, Vector3 boxMin, Vector3 boxMax, out float tHit)
    {
        Vector3 d = b - a;
        float tMin = 0.0f;
        float tMax = 1.0f;

        if (!ClipAxis(a.X, d.X, boxMin.X, boxMax.X, ref tMin, ref tMax) ||
            !ClipAxis(a.Y, d.Y, boxMin.Y, boxMax.Y, ref tMin, ref tMax) ||
            !ClipAxis(a.Z, d.Z, boxMin.Z, boxMax.Z, ref tMin, ref tMax))
        {
            tHit = 1.0f;
            return false;
        }

        tHit = tMin;
        return true;
    }

    private static bool ClipAxis(float origin, float dir, float minB, float maxB, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(dir) < 1e-8f)
            return origin >= minB && origin <= maxB;

        float invD = 1.0f / dir;
        float t1 = (minB - origin) * invD;
        float t2 = (maxB - origin) * invD;
        if (t1 > t2) (t1, t2) = (t2, t1);

        tMin = MathF.Max(tMin, t1);
        tMax = MathF.Min(tMax, t2);
        return tMin <= tMax;
    }

    private bool IsPointInsidePolygon3D(Vector3 point, JklSurface surface)
    {
        int n = surface.VertexIndices.Length;
        for (int i = 0; i < n; i++)
        {
            int idxA = surface.VertexIndices[i];
            int idxB = surface.VertexIndices[(i + 1) % n];
            if (idxA < 0 || idxA >= _level.Vertices.Count || idxB < 0 || idxB >= _level.Vertices.Count)
                return false;

            Vector3 va = _level.Vertices[idxA];
            Vector3 vb = _level.Vertices[idxB];
            Vector3 edge = vb - va;
            Vector3 toPt = point - va;
            if (Vector3.Dot(Vector3.Cross(edge, toPt), surface.Normal) < -0.001f)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Traces an authentic Sith Engine gaze sphere-cast from the player's eye position along
    /// <paramref name="aimDir"/> (<c>sithPlayerActions_Activate</c>: range <c>moveSize + 0.15</c>,
    /// sphere radius <c>0.025</c>) and sends <c>activate</c> to the closest interactive
    /// <c>SITH_SURFACE_COG_LINKED</c> surface or <c>SITH_TF_CAPTURED</c> thing, with a
    /// nearby floor-level switch fallback for recessed wall switches.
    /// </summary>
    private PlayerState? FindNearestAlivePlayer(Vector3 pos)
    {
        PlayerState? best = null;
        float bestDistSq = float.MaxValue;
        foreach (var p in _players)
        {
            if (p.Inventory.IsDead || p.ThingIndex < 0 || p.ThingIndex >= _level.Things.Count)
                continue;

            float dSq = Vector3.DistanceSquared(pos, _level.Things[p.ThingIndex].Position);
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                best = p;
            }
        }
        return best;
    }

    public bool ActivateNearestDoor(Vector3 playerPos, Vector3 aimDir) =>
        ActivateNearestDoor(_localPlayerThingIndex, playerPos, aimDir);

    public bool ActivateNearestDoor(int playerThingId, Vector3 playerPos, Vector3 aimDir)
    {
        const int sithSurfaceCogLinked = 0x2;
        const float sphereRadius = 0.025f;

        Vector3 eyePos = playerPos + new Vector3(0f, 0f, 0.157f);
        Vector3 dir = aimDir.LengthSquared() > 1e-6f
            ? Vector3.Normalize(aimDir)
            : Vector3.UnitY;

        float moveSize = ParseTemplateFloat(_level.GetPlayerSpawn()?.TemplateName ?? "walkplayer", "movesize", 0.065f);
        float range = moveSize + 0.15f;
        Vector3 rayEnd = eyePos + dir * range;
        Vector3 rayMin = Vector3.Min(eyePos, rayEnd) - new Vector3(sphereRadius + 0.05f);
        Vector3 rayMax = Vector3.Max(eyePos, rayEnd) + new Vector3(sphereRadius + 0.05f);

        float bestDist = range + 1f;
        JklSurface? bestSurface = null;
        CogInstance? bestSurfaceCog = null;
        JklThing? bestThing = null;
        CogInstance? bestThingCog = null;

        // 1. Check interactive SITH_SURFACE_COG_LINKED surfaces (non-COG walls/doorframes do not shadow)
        foreach (var sector in _level.Sectors)
        {
            if (sector.BoundBoxMax.X < rayMin.X || sector.BoundBoxMin.X > rayMax.X ||
                sector.BoundBoxMax.Y < rayMin.Y || sector.BoundBoxMin.Y > rayMax.Y ||
                sector.BoundBoxMax.Z < rayMin.Z || sector.BoundBoxMin.Z > rayMax.Z)
            {
                continue;
            }

            foreach (var surface in sector.Surfaces)
            {
                if (surface.VertexIndices.Length < 3 ||
                    (surface.SurfaceFlags & sithSurfaceCogLinked) == 0 ||
                    !TryFindActivatableCog(_surfaceToCogs, CogSymbolType.Surface, surface.SurfaceIndex, out var surfaceCog))
                {
                    continue;
                }

                float denom = Vector3.Dot(dir, surface.Normal);
                if (denom >= -1e-6f)
                    continue;

                int v0Idx = surface.VertexIndices[0];
                if (v0Idx < 0 || v0Idx >= _level.Vertices.Count)
                    continue;

                Vector3 v0 = _level.Vertices[v0Idx];
                float planeDist = Vector3.Dot(eyePos - v0, surface.Normal);
                if (planeDist < -sphereRadius)
                    continue;

                float t = planeDist <= sphereRadius ? 0f : (planeDist - sphereRadius) / -denom;
                if (t < 0f || t > range || t >= bestDist)
                    continue;

                Vector3 centerAtT = eyePos + dir * t;
                float distAtT = Vector3.Dot(centerAtT - v0, surface.Normal);
                Vector3 hitOnPlane = centerAtT - surface.Normal * distAtT;
                if (IsPointInsidePolygon3D(hitOnPlane, surface))
                {
                    bestDist = t;
                    bestSurface = surface;
                    bestSurfaceCog = surfaceCog;
                    bestThing = null;
                    bestThingCog = null;
                }
            }
        }

        // 2. Check interactive SITH_TF_CAPTURED things (excluding items, weapons, disabled/destroyed)
        Vector3 spherePad = new(sphereRadius);
        foreach (var thing in _level.Things)
        {
            if (_playersByThing.ContainsKey(thing.ThingIndex) || _destroyedThings.Contains(thing.ThingIndex))
                continue;

            uint flags = (uint)GetThingFlags(thing.ThingIndex);
            if ((flags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0)
                continue;
            if ((flags & (uint)SithThingFlags.Captured) == 0)
                continue;

            string? thingType = ResolveTemplateParam(thing.TemplateName, "type");
            if (string.Equals(thingType, "item", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thingType, "weapon", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryFindActivatableCog(_thingToCogs, CogSymbolType.Thing, thing.ThingIndex, out var thingCog))
                continue;

            if (TryIntersectActivateThing(thing, eyePos, dir, rayEnd, range, sphereRadius, spherePad, out float hitDist) &&
                hitDist < bestDist)
            {
                bestDist = hitDist;
                bestThing = thing;
                bestThingCog = thingCog;
                bestSurface = null;
                bestSurfaceCog = null;
            }
        }

        int activatorRef = playerThingId >= 0 ? playerThingId : _localPlayerThingIndex;
        if (bestSurface != null && bestSurfaceCog != null)
        {
            int linkId = GetSymbolLinkId(bestSurfaceCog, CogSymbolType.Surface, bestSurface.SurfaceIndex);
            WithPlayerCogContext(activatorRef, () =>
                SafeSendMessage(bestSurfaceCog, "activate", senderRef: bestSurface.SurfaceIndex, senderId: linkId, sourceRef: activatorRef, senderType: 6));
            return true;
        }

        if (bestThing != null && bestThingCog != null)
        {
            int linkId = GetSymbolLinkId(bestThingCog, CogSymbolType.Thing, bestThing.ThingIndex);
            WithPlayerCogContext(activatorRef, () =>
                SafeSendMessage(bestThingCog, "activate", senderRef: bestThing.ThingIndex, senderId: linkId, sourceRef: activatorRef, senderType: 3));
            return true;
        }

        // 3. Fallback for recessed floor-level wall switches (e.g., 0.05m niches below eye height)
        if (TryFindNearbySwitchSurface(playerPos, dir, sithSurfaceCogLinked, out var nearbySurface, out var nearbyCog))
        {
            int linkId = GetSymbolLinkId(nearbyCog!, CogSymbolType.Surface, nearbySurface!.SurfaceIndex);
            WithPlayerCogContext(activatorRef, () =>
                SafeSendMessage(nearbyCog!, "activate", senderRef: nearbySurface!.SurfaceIndex, senderId: linkId, sourceRef: activatorRef, senderType: 6));
            return true;
        }

        return false;
    }

    private static bool TryFindActivatableCog(
        Dictionary<int, List<CogInstance>> map,
        CogSymbolType symbolType,
        int refId,
        out CogInstance? matchedCog)
    {
        if (map.TryGetValue(refId, out var cogs))
        {
            foreach (var cog in cogs)
            {
                if (!cog.MatchesSymbolMask(symbolType, refId))
                    continue;
                if (!cog.Program.Labels.ContainsKey("activate") && !cog.Program.Labels.ContainsKey("activated"))
                    continue;

                matchedCog = cog;
                return true;
            }
        }

        matchedCog = null;
        return false;
    }

    private bool TryFindNearbySwitchSurface(
        Vector3 playerPos,
        Vector3 dir,
        int sithSurfaceCogLinked,
        out JklSurface? bestSurface,
        out CogInstance? bestCog)
    {
        bestSurface = null;
        bestCog = null;

        Vector3 flatDir = new(dir.X, dir.Y, 0f);
        if (flatDir.LengthSquared() <= 1e-6f)
            return false;
        flatDir = Vector3.Normalize(flatDir);

        const float maxHorizDist = 0.28f;
        float bestHorizDistSq = maxHorizDist * maxHorizDist;

        foreach (var sector in _level.Sectors)
        {
            if (sector.BoundBoxMax.X < playerPos.X - maxHorizDist || sector.BoundBoxMin.X > playerPos.X + maxHorizDist ||
                sector.BoundBoxMax.Y < playerPos.Y - maxHorizDist || sector.BoundBoxMin.Y > playerPos.Y + maxHorizDist ||
                sector.BoundBoxMax.Z < playerPos.Z - 0.05f || sector.BoundBoxMin.Z > playerPos.Z + 0.25f)
            {
                continue;
            }

            foreach (var surface in sector.Surfaces)
            {
                if (surface.VertexIndices.Length < 3 ||
                    (surface.SurfaceFlags & sithSurfaceCogLinked) == 0 ||
                    Vector3.Dot(flatDir, surface.Normal) >= -0.2f ||
                    !TryFindActivatableCog(_surfaceToCogs, CogSymbolType.Surface, surface.SurfaceIndex, out var cog))
                {
                    continue;
                }

                Vector3 center = Vector3.Zero;
                int validVerts = 0;
                foreach (int vIdx in surface.VertexIndices)
                {
                    if (vIdx >= 0 && vIdx < _level.Vertices.Count)
                    {
                        center += _level.Vertices[vIdx];
                        validVerts++;
                    }
                }
                if (validVerts == 0)
                    continue;
                center /= validVerts;

                if (center.Z < playerPos.Z - 0.05f || center.Z > playerPos.Z + 0.25f)
                    continue;

                Vector3 toSwitch = new(center.X - playerPos.X, center.Y - playerPos.Y, 0f);
                float horizDistSq = toSwitch.LengthSquared();
                if (horizDistSq > bestHorizDistSq || horizDistSq <= 1e-8f)
                    continue;

                Vector3 flatToSwitch = toSwitch / MathF.Sqrt(horizDistSq);
                if (Vector3.Dot(flatToSwitch, flatDir) <= 0.35f)
                    continue;

                bestHorizDistSq = horizDistSq;
                bestSurface = surface;
                bestCog = cog;
            }
        }

        return bestSurface != null && bestCog != null;
    }

    private bool TryIntersectActivateThing(
        JklThing thing,
        Vector3 eyePos,
        Vector3 dir,
        Vector3 rayEnd,
        float range,
        float sphereRadius,
        Vector3 spherePad,
        out float hitDist)
    {
        hitDist = range + 1f;
        if (IsDoorThing(thing))
        {
            var (localMin, localMax) = GetDoorLocalBounds(thing);
            Matrix4x4 worldMat = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position).ToMatrix4x4();
            if (!Matrix4x4.Invert(worldMat, out Matrix4x4 invWorld))
                return false;

            Vector3 localA = Vector3.Transform(eyePos, invWorld);
            Vector3 localB = Vector3.Transform(rayEnd, invWorld);
            if (TryIntersectSegmentAabb(localA, localB, localMin - spherePad, localMax + spherePad, out float segT) &&
                segT >= 0f && segT <= 1f)
            {
                hitDist = segT * range;
                return true;
            }
            return false;
        }

        float thingRadius = thing.MoveSize ?? thing.Size ?? ParseTemplateFloat(
            thing.TemplateName, "movesize", ParseTemplateFloat(thing.TemplateName, "size", 0.065f));
        float combinedRadius = MathF.Max(0.02f, thingRadius) + sphereRadius;
        Vector3 oc = eyePos - thing.Position;
        float b = Vector3.Dot(oc, dir);
        float c = Vector3.Dot(oc, oc) - combinedRadius * combinedRadius;
        if (c <= 0f)
        {
            hitDist = 0f;
            return true;
        }
        if (b > 0f)
            return false;

        float disc = b * b - c;
        if (disc < 0f)
            return false;

        float t = -b - MathF.Sqrt(disc);
        if (t >= 0f && t <= range)
        {
            hitDist = t;
            return true;
        }
        return false;
    }

    private Vector3 ComputeSurfaceCenter(JklSurface surface)
    {
        if (surface.VertexIndices.Length == 0)
            return Vector3.Zero;

        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (int vIdx in surface.VertexIndices)
        {
            if (vIdx >= 0 && vIdx < _level.Vertices.Count)
            {
                sum += _level.Vertices[vIdx];
                count++;
            }
        }
        return count > 0 ? sum / count : Vector3.Zero;
    }

    /// <summary>
    /// Fires the player's active weapon: spawns a visible blaster projectile for ranged weapons
    /// or performs an immediate melee swing check for the lightsaber.
    /// </summary>
    public int FireWeaponRay(
        Vector3 rayOrigin,
        Vector3 rayDir,
        float maxRange,
        float damage,
        bool isMelee,
        string projectileModel = "bry0.3do",
        string fireSound = "pistol-1.wav",
        float boltSpeed = 8.0f)
    {
        if (rayDir.LengthSquared() < 1e-6f || Inventory.IsDead)
            return -1;

        if (!isMelee)
        {
            if (Inventory.EnergyAmmo < 1f)
                return -1;

            ChangeInv(_localPlayerThingIndex, PlayerInventory.BinEnergy, -1f);
        }

        Vector3 dir = Vector3.Normalize(rayDir);
        _soundEvents.Add(new AiSoundEvent(rayOrigin, isMelee ? 1.5f : 6.0f, _localPlayerThingIndex));

        if (!isMelee)
        {
            string sound = string.IsNullOrWhiteSpace(fireSound) ? "pistol-1.wav" : fireSound;
            string model = string.IsNullOrWhiteSpace(projectileModel) ? "bry0.3do" : projectileModel;
            _audio?.PlaySoundLocal(sound, 0.85f);
            Vector3 spawnPos = TryRaycastWorld(rayOrigin, rayOrigin + dir * 0.02f, out _)
                ? rayOrigin
                : rayOrigin + dir * 0.02f;
            SpawnProjectile(
                spawnPos,
                dir,
                damage,
                ownerThingIndex: _localPlayerThingIndex,
                isEnemy: false,
                modelName: model,
                boltSpeed: boltSpeed,
                boltColor: ResolveBoltColorForModel(model));
            return -1;
        }

        _audio?.PlaySoundLocal("ltsaberswing01.wav", 0.8f);

        Vector3 meleeEnd = rayOrigin + dir * maxRange;
        bool hitWall = TryRaycastWorldWithHit(rayOrigin, meleeEnd, out Vector3 wallHitPos, out int hitSurfaceId, out _);
        float closestDist = hitWall ? Vector3.Distance(rayOrigin, wallHitPos) : maxRange;

        int hitThingId = -1;
        int hitPropId = -1;

        foreach (var npc in _npcs.Values)
        {
            if (!npc.IsAlive)
                continue;

            // Target NPC torso center (above feet position)
            Vector3 npcCenter = npc.Thing.Position + new Vector3(0f, 0f, 0.10f);
            Vector3 toNpc = npcCenter - rayOrigin;
            float proj = Vector3.Dot(toNpc, dir);
            if (proj <= 0f || proj > closestDist)
                continue;

            Vector3 closestOnRay = rayOrigin + dir * proj;
            float perpDist = Vector3.Distance(closestOnRay, npcCenter);
            float hitRadius = MathF.Max(npc.Radius * 1.6f, 0.09f);

            if (perpDist <= hitRadius && HasLineOfSight(rayOrigin, npcCenter))
            {
                closestDist = proj;
                hitThingId = npc.Thing.ThingIndex;
            }
        }

        for (int thingId = 0; thingId < _level.Things.Count; thingId++)
        {
            if (!IsDamagableProp(thingId))
                continue;

            Vector3 propCenter = _level.Things[thingId].Position;
            Vector3 toProp = propCenter - rayOrigin;
            float proj = Vector3.Dot(toProp, dir);
            if (proj <= 0f || proj > closestDist)
                continue;

            Vector3 closestOnRay = rayOrigin + dir * proj;
            float perpDist = Vector3.Distance(closestOnRay, propCenter);
            float hitRadius = GetPropHitRadius(thingId);

            if (perpDist <= hitRadius && HasLineOfSight(rayOrigin, propCenter))
            {
                closestDist = proj;
                hitPropId = thingId;
                hitThingId = -1;
            }
        }

        if (hitPropId >= 0)
        {
            _audio?.PlaySoundPos("ltsaberbodyhit01.wav", _level.Things[hitPropId].Position, 0.9f, 0.5f, 5.0f);
            DispatchThingCogMessage(hitPropId, "damaged", new CogValue(damage), new CogValue(16));
            return hitPropId;
        }

        if (hitThingId < 0)
        {
            if (hitWall && hitSurfaceId >= 0)
                DispatchSurfaceDamaged(hitSurfaceId, damage, damageType: 16);
            return -1;
        }

        if (_npcs.TryGetValue(hitThingId, out var hitNpc) && CanNpcBlock(hitNpc))
        {
            Vector3 npcCenter = hitNpc.Thing.Position + new Vector3(0f, 0f, 0.10f);
            Vector3 toKyle = rayOrigin - npcCenter;
            toKyle = toKyle.LengthSquared() > 1e-6f ? Vector3.Normalize(toKyle) : -dir;
            Vector3 npcForward = GetThingForward(hitNpc.Thing);
            bool kyleIsInvisible = (GetActorFlags(_localPlayerThingIndex) & 0x80) != 0;

            if (!kyleIsInvisible &&
                Vector3.Dot(npcForward, toKyle) >= hitNpc.BlockFov &&
                _random.NextDouble() < hitNpc.BlockChance)
            {
                hitNpc.AnimAction = "block";
                hitNpc.BlockAnimTimer = 0.35f;
                hitNpc.AnimMode = 1;
                hitNpc.LoopAnim = false;
                Vector3 sparkPos = npcCenter + toKyle * 0.06f;
                SpawnSparks(sparkPos, toKyle, count: 12);
                _audio?.PlaySoundPos("ltsaberhit01.wav", sparkPos, 0.95f, 0.5f, 6.0f);
                DispatchThingCogMessage(hitThingId, "blocked");
                return hitThingId;
            }
        }

        _audio?.PlaySoundPos("ltsaberbodyhit01.wav", _level.Things[hitThingId].Position, 0.9f, 0.5f, 5.0f);
        ApplyNpcDamage(hitThingId, damage, rayOrigin, isMelee ? 16 : 2, _localPlayerThingIndex);
        return hitThingId;
    }

    private bool CanNpcBlock(NpcRuntime npc) =>
        npc.IsAlive && (npc.TypeFlags & 0x200) != 0 && (JkGetFlags(npc.Thing.ThingIndex) & 0x1) != 0;

    private void ApplyDamageToPlayer(PlayerState victim, int attackerThingId, float damage, string hitSound)
    {
        if (victim.ThingIndex == _localPlayerThingIndex)
            _audio?.PlaySoundLocal(hitSound, 0.85f);
        else if (victim.ThingIndex >= 0 && victim.ThingIndex < _level.Things.Count)
            _audio?.PlaySoundPos(hitSound, _level.Things[victim.ThingIndex].Position, 0.85f, 0.5f, 6.0f);

        bool wasDead = victim.Inventory.IsDead;
        victim.Inventory.ApplyDamage(damage);
        if (!wasDead && victim.Inventory.IsDead)
        {
            if (victim.ThingIndex == _localPlayerThingIndex)
                _audio?.PlaySoundLocal("kdie01.wav", 0.9f);
            else if (victim.ThingIndex >= 0 && victim.ThingIndex < _level.Things.Count)
                _audio?.PlaySoundPos("kdie01.wav", _level.Things[victim.ThingIndex].Position, 0.9f, 0.5f, 6.0f);

            RecordPlayerDeath(victim, attackerThingId);
            DispatchPlayerKilledMessages(victim, attackerThingId);
        }

        Console.WriteLine($"[COMBAT] Player Thing {victim.ThingIndex} hit by Thing {attackerThingId} for {damage:F0} dmg (HP={victim.Inventory.Health:F0}, Shields={victim.Inventory.Shields:F0})");
    }

    private void RecordPlayerDeath(PlayerState victim, int attackerThingId)
    {
        victim.Killed++;
        if (attackerThingId == victim.ThingIndex || attackerThingId < 0)
        {
            victim.Suicides++;
            if (victim.Score > 0)
                victim.Score--;
        }
        else if (_playersByThing.TryGetValue(attackerThingId, out var attacker))
        {
            attacker.Kills++;
            attacker.Score += (!IsMultiplayer || attacker.Team == 0 || attacker.Team != victim.Team) ? 1 : -1;
        }
    }

    private void SpawnProjectile(
        Vector3 origin,
        Vector3 direction,
        float damage,
        int ownerThingIndex,
        bool isEnemy,
        string modelName,
        float boltSpeed = 8.0f,
        Vector3? boltColor = null)
    {
        Vector3 dir = direction.LengthSquared() > 1e-6f ? Vector3.Normalize(direction) : Vector3.UnitY;
        _projectiles.Add(new Projectile
        {
            Position = origin,
            Direction = dir,
            Velocity = dir * MathF.Max(1.0f, boltSpeed),
            Damage = damage,
            Lifetime = 2.0f,
            OwnerThingIndex = ownerThingIndex,
            IsEnemy = isEnemy,
            ModelName = modelName,
            BoltColor = boltColor ?? ResolveBoltColorForModel(modelName)
        });
    }

    private (string ModelName, Vector3 BoltColor) ResolveNpcProjectileStyle(JklThing npcThing)
    {
        string template = npcThing.TemplateName;
        string? weaponTemplate = ResolveTemplateParam(template, "weapon");
        string? weaponModel = !string.IsNullOrWhiteSpace(weaponTemplate)
            ? ResolveTemplateParam(weaponTemplate, "model3d")
            : null;
        string? cogName = ResolveTemplateParam(template, "cog");
        string? aiClassName = ResolveTemplateParam(template, "aiclass");

        bool isRodian =
            template.Contains("greedo", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("rodian", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(cogName) && cogName.Contains("actor_gr", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(aiClassName) && aiClassName.Contains("grdefault", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(weaponTemplate) && (weaponTemplate.Contains("ebolt", StringComparison.OrdinalIgnoreCase) ||
                                                       weaponTemplate.Contains("grlaser", StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(weaponModel) && weaponModel.Contains("bry1", StringComparison.OrdinalIgnoreCase));

        if (isRodian)
        {
            return (string.IsNullOrWhiteSpace(weaponModel) ? "bry1.3do" : weaponModel, BoltColorGreen);
        }

        bool isRepeater =
            template.Contains("repeater", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(weaponTemplate) && weaponTemplate.Contains("repeater", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(weaponModel) && (weaponModel.Contains("rep", StringComparison.OrdinalIgnoreCase) ||
                                                    weaponModel.Contains("rpt", StringComparison.OrdinalIgnoreCase)));

        if (isRepeater)
        {
            return (string.IsNullOrWhiteSpace(weaponModel) ? "rpt0.3do" : weaponModel, BoltColorGold);
        }

        string resolvedModel = string.IsNullOrWhiteSpace(weaponModel) ? "str0.3do" : weaponModel;
        return (resolvedModel, BoltColorRed);
    }

    private static Vector3 ResolveBoltColorForModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return BoltColorRed;

        if (modelName.Contains("bry1", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("grlaser", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("greedo", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("rodian", StringComparison.OrdinalIgnoreCase))
        {
            return BoltColorGreen;
        }

        if (modelName.Contains("rep", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("rpt", StringComparison.OrdinalIgnoreCase))
        {
            return BoltColorGold;
        }

        return BoltColorRed;
    }

    private void UpdateProjectiles(float dt)
    {
        if (_projectiles.Count == 0)
            return;

        const float gravity = 3.5f;

        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            var proj = _projectiles[i];
            proj.Lifetime -= dt;
            if (proj.Lifetime <= 0f)
            {
                if (!string.IsNullOrWhiteSpace(proj.ExplodeTemplate) && proj.Bounce)
                    TriggerExplosion(proj.Position, proj.ExplodeTemplate, proj.OwnerThingIndex);

                _projectiles.RemoveAt(i);
                continue;
            }

            if (proj.UseGravity)
                proj.Velocity.Z -= gravity * dt;

            if (proj.Velocity.LengthSquared() > 1e-6f)
                proj.Direction = Vector3.Normalize(proj.Velocity);

            Vector3 start = proj.Position;
            Vector3 nextPos = start + proj.Velocity * dt;
            bool hitWall = TryRaycastWorldWithHit(start, nextPos, out Vector3 endPos, out int hitSurfaceId, out _);

            int hitNpcId = -1;
            int hitPropId = -1;
            PlayerState? hitPlayer = null;
            float bestT = 1.0f;
            Vector3 npcHitPos = endPos;
            Vector3 propHitPos = endPos;
            Vector3 playerHitPos = endPos;

            foreach (var npc in _npcs.Values)
            {
                if (!npc.IsAlive || npc.Thing.ThingIndex == proj.OwnerThingIndex)
                    continue;
                if (proj.IsEnemy && npc.Brain.Class.HasInstinct("PrimaryFire"))
                    continue;

                Vector3 npcCenter = npc.Thing.Position + new Vector3(0f, 0f, 0.10f);
                float hitRadius = MathF.Max(npc.Radius * 1.6f, 0.09f) + proj.Radius;
                float distSq = DistanceSqPointToSegment(npcCenter, start, endPos, out float segT);
                if (distSq <= hitRadius * hitRadius && segT < bestT)
                {
                    bestT = segT;
                    hitNpcId = npc.Thing.ThingIndex;
                    npcHitPos = Vector3.Lerp(start, endPos, segT);
                }
            }

            foreach (var targetPlayer in _players)
            {
                if (targetPlayer.Inventory.IsDead || targetPlayer.ThingIndex == proj.OwnerThingIndex)
                    continue;
                if (targetPlayer.ThingIndex < 0 || targetPlayer.ThingIndex >= _level.Things.Count)
                    continue;

                Vector3 targetTorso = _level.Things[targetPlayer.ThingIndex].Position + new Vector3(0f, 0f, 0.13f);
                float hitRadius = 0.08f + proj.Radius;
                float distSq = DistanceSqPointToSegment(targetTorso, start, endPos, out float segT);
                if (distSq <= hitRadius * hitRadius && segT < bestT)
                {
                    bestT = segT;
                    hitPlayer = targetPlayer;
                    hitNpcId = -1;
                    playerHitPos = Vector3.Lerp(start, endPos, segT);
                }
            }

            for (int thingId = 0; thingId < _level.Things.Count; thingId++)
            {
                if (thingId == proj.OwnerThingIndex || !IsDamagableProp(thingId))
                    continue;

                Vector3 propCenter = _level.Things[thingId].Position;
                float hitRadius = GetPropHitRadius(thingId);
                float distSq = DistanceSqPointToSegment(propCenter, start, endPos, out float segT);
                if (distSq <= hitRadius * hitRadius && segT < bestT)
                {
                    bestT = segT;
                    hitPropId = thingId;
                    hitNpcId = -1;
                    hitPlayer = null;
                    propHitPos = Vector3.Lerp(start, endPos, segT);
                }
            }

            if (hitPropId >= 0)
            {
                DispatchThingCogMessageFromSource(hitPropId, proj.OwnerThingIndex, "damaged", new CogValue(proj.Damage), new CogValue(proj.DamageClass));
                TriggerExplosion(propHitPos, proj.ExplodeTemplate, proj.OwnerThingIndex, directHitThingId: hitPropId, fallbackSound: "shothit01.wav");
                _projectiles.RemoveAt(i);
                continue;
            }

            if (hitNpcId >= 0)
            {
                if (_npcs.TryGetValue(hitNpcId, out var hitNpc) && CanNpcBlock(hitNpc))
                {
                    Vector3 toIncoming = -proj.Direction;
                    Vector3 npcForward = GetThingForward(hitNpc.Thing);
                    if (Vector3.Dot(npcForward, toIncoming) >= hitNpc.BlockFov &&
                        _random.NextDouble() < hitNpc.BlockChance)
                    {
                        Vector3 baseDir = toIncoming;
                        if (proj.OwnerThingIndex >= 0 && proj.OwnerThingIndex < _level.Things.Count)
                        {
                            Vector3 toShooter = (GetThingPos(proj.OwnerThingIndex) + new Vector3(0f, 0f, 0.10f)) - npcHitPos;
                            if (toShooter.LengthSquared() > 1e-6f)
                                baseDir = Vector3.Normalize(toShooter);
                        }

                        float scatterScale = hitNpc.BlockError * 0.25f;
                        Vector3 scatter = new Vector3(
                            ((float)_random.NextDouble() - 0.5f) * 2f * scatterScale,
                            ((float)_random.NextDouble() - 0.5f) * 2f * scatterScale,
                            ((float)_random.NextDouble() - 0.5f) * 2f * scatterScale);
                        Vector3 deflectDir = baseDir + scatter;
                        deflectDir = deflectDir.LengthSquared() > 1e-6f ? Vector3.Normalize(deflectDir) : toIncoming;

                        float speed = MathF.Max(1.0f, proj.Velocity.Length());
                        proj.Position = npcHitPos + deflectDir * 0.05f;
                        proj.Direction = deflectDir;
                        proj.Velocity = deflectDir * speed;
                        proj.OwnerThingIndex = hitNpcId;
                        proj.IsEnemy = true;
                        proj.Lifetime = MathF.Max(proj.Lifetime, 1.5f);

                        hitNpc.AnimAction = "block";
                        hitNpc.BlockAnimTimer = 0.35f;
                        hitNpc.AnimMode = 1;
                        hitNpc.LoopAnim = false;
                        _audio?.PlaySoundPos("ltsaberhit01.wav", npcHitPos, 0.9f, 0.5f, 6.0f);
                        SpawnSparks(npcHitPos, deflectDir, count: 12);
                        continue;
                    }
                }

                if (proj.Damage > 0f)
                    ApplyNpcDamage(hitNpcId, proj.Damage, start, proj.DamageClass, proj.OwnerThingIndex);

                TriggerExplosion(npcHitPos, proj.FleshHitTemplate ?? proj.ExplodeTemplate, proj.OwnerThingIndex, directHitThingId: hitNpcId, fallbackSound: "shothit01.wav");
                _projectiles.RemoveAt(i);
                continue;
            }

            if (hitPlayer != null)
            {
                if (proj.Damage > 0f)
                    ApplyDamageToPlayer(hitPlayer, proj.OwnerThingIndex, proj.Damage, "kbodyhit01.wav");

                TriggerExplosion(playerHitPos, proj.FleshHitTemplate ?? proj.ExplodeTemplate, proj.OwnerThingIndex, directHitThingId: hitPlayer.ThingIndex, fallbackSound: "kbodyhit01.wav");
                _projectiles.RemoveAt(i);
                continue;
            }

            if (hitWall)
            {
                if (proj.Bounce && !proj.ExplodeOnImpact)
                {
                    Vector3 normal = hitSurfaceId >= 0 && hitSurfaceId < _level.Surfaces.Count
                        ? _level.Surfaces[hitSurfaceId].Normal
                        : Vector3.UnitZ;
                    Vector3 reflected = Vector3.Reflect(proj.Velocity, normal) * 0.55f;
                    if (reflected.LengthSquared() < 0.04f)
                        reflected = Vector3.Zero;

                    proj.Velocity = reflected;
                    proj.Position = endPos + normal * 0.015f;
                    if (reflected.LengthSquared() > 1e-6f)
                        proj.Direction = Vector3.Normalize(reflected);
                    _audio?.PlaySoundPos("ThermClick01.wav", endPos, 0.45f, 0.3f, 4.0f);
                    continue;
                }

                if (hitSurfaceId >= 0)
                    DispatchSurfaceDamaged(hitSurfaceId, proj.Damage, proj.DamageClass, proj.OwnerThingIndex);

                TriggerExplosion(endPos, proj.ExplodeTemplate, proj.OwnerThingIndex, fallbackSound: "ricochet01.wav");
                _projectiles.RemoveAt(i);
                continue;
            }

            proj.Position = nextPos;
        }
    }

    private bool IsDamagableProp(int thingId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return false;
        if (_playersByThing.ContainsKey(thingId) || _npcs.ContainsKey(thingId) || _destroyedThings.Contains(thingId))
            return false;

        var thing = _level.Things[thingId];
        if (thing.IsPlayerSpawn || thing.IsNonSolid)
            return false;

        int flags = GetThingFlags(thingId);
        if ((flags & (int)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0)
            return false;

        bool isCapturedOrCog = (flags & (int)SithThingFlags.Captured) != 0 ||
            _thingToCogs.ContainsKey(thingId) ||
            !string.IsNullOrWhiteSpace(ResolveTemplateParam(thing.TemplateName, "cog"));
        return isCapturedOrCog && HasThingCogLabel(thingId, "damaged");
    }

    private float GetPropHitRadius(int thingId)
    {
        var thing = _level.Things[thingId];
        float size = thing.Size ?? ParseTemplateFloat(thing.TemplateName, "size", 0.08f);
        return MathF.Max(size, 0.08f);
    }

    private bool HasThingCogLabel(int thingId, string label)
    {
        if (_thingToCogs.TryGetValue(thingId, out var cogs))
        {
            foreach (var cog in cogs)
            {
                if (cog.Program.Labels.ContainsKey(label))
                    return true;
            }
        }

        string templateName = _level.Things[thingId].TemplateName;
        if (!string.IsNullOrWhiteSpace(templateName))
        {
            string? cogName = ResolveTemplateParam(templateName, "cog");
            if (!string.IsNullOrWhiteSpace(cogName))
            {
                var templateCog = GetOrCreateTemplateCog(cogName);
                if (templateCog != null && templateCog.Program.Labels.ContainsKey(label))
                    return true;
            }
        }

        return false;
    }

    private void DispatchSurfaceDamaged(int surfaceIndex, float damage, int damageType, int sourceRefOverride = -1)
    {
        if (surfaceIndex < 0 || surfaceIndex >= _level.Surfaces.Count)
            return;
        if ((_level.Surfaces[surfaceIndex].SurfaceFlags & 0x2) == 0)
            return;
        if (!_surfaceToCogs.TryGetValue(surfaceIndex, out var cogs))
            return;

        int sourceRef = sourceRefOverride >= 0 ? sourceRefOverride : _localPlayerThingIndex;
        var parameters = new CogValue[] { new(damage), new(damageType) };
        foreach (var cog in cogs)
        {
            if (!cog.Program.Labels.ContainsKey("damaged"))
                continue;

            int symLinkId = GetSymbolLinkId(cog, CogSymbolType.Surface, surfaceIndex);
            SafeSendMessageWithReturn(
                cog,
                "damaged",
                senderRef: surfaceIndex,
                senderId: symLinkId,
                sourceRef: sourceRef,
                senderType: 6,
                parameters: parameters);
        }
    }

    private void SpawnSparks(Vector3 pos, Vector3 bounceDir, int count)
    {
        Vector3 dir = bounceDir.LengthSquared() > 1e-6f ? Vector3.Normalize(bounceDir) : Vector3.UnitZ;
        for (int i = 0; i < count; i++)
        {
            float rx = ((float)Random.Shared.NextDouble() - 0.5f) * 1.4f;
            float ry = ((float)Random.Shared.NextDouble() - 0.5f) * 1.4f;
            float rz = ((float)Random.Shared.NextDouble() - 0.2f) * 1.2f;
            Vector3 vel = (dir * 0.85f + new Vector3(rx, ry, rz)) * (0.4f + (float)Random.Shared.NextDouble() * 0.6f);
            float life = 0.16f + (float)Random.Shared.NextDouble() * 0.14f;
            _sparks.Add(new ImpactSpark
            {
                Position = pos + dir * 0.005f,
                Velocity = vel,
                Life = life,
                MaxLife = life
            });
        }
    }

    private void UpdateSparks(float dt)
    {
        for (int i = _sparks.Count - 1; i >= 0; i--)
        {
            var s = _sparks[i];
            s.Life -= dt;
            if (s.Life <= 0f)
            {
                _sparks.RemoveAt(i);
                continue;
            }
            s.Velocity.Z -= 2.5f * dt;
            s.Position += s.Velocity * dt;
        }
    }

    private static float DistanceSqPointToSegment(Vector3 p, Vector3 a, Vector3 b, out float t)
    {
        Vector3 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-8f)
        {
            t = 0f;
            return Vector3.DistanceSquared(p, a);
        }

        t = Math.Clamp(Vector3.Dot(p - a, ab) / lenSq, 0f, 1f);
        Vector3 closest = a + ab * t;
        return Vector3.DistanceSquared(p, closest);
    }

    private void ApplyNpcDamage(int thingId, float damage, Vector3 attackerPos, int damageType = 16, int attackerThingId = -1)
    {
        if (!_npcs.TryGetValue(thingId, out var npc) || !npc.IsAlive)
            return;

        int source = attackerThingId >= 0 ? attackerThingId : _localPlayerThingIndex;
        CogValue damagedResult = DispatchThingCogMessageFromSource(
            thingId,
            source,
            "damaged",
            new CogValue(damage),
            new CogValue(damageType));
        float finalDamage = damagedResult.Type is CogValueType.Int or CogValueType.Float
            ? MathF.Max(0f, damagedResult.AsFloat)
            : damage;

        npc.Health -= finalDamage;
        npc.WasDamagedThisTick = true;
        npc.LastDamagerThingId = source;
        npc.LastDamageSourcePos = attackerPos;
        npc.HitAnimTimer = 0.35f;

        if (npc.Health <= 0f)
        {
            npc.Health = 0f;
            npc.IsAlive = false;
            npc.Thing.IsNonSolid = true;
            npc.Brain.Mode = AiMode.Disabled;
            npc.AnimAction = "death";
            npc.AnimMode = 0;
            npc.LoopAnim = false;

            Console.WriteLine($"[COMBAT] Killed Thing {thingId} ({npc.Thing.TemplateName})");
            string deathSound = npc.Thing.TemplateName.Contains("droid", StringComparison.OrdinalIgnoreCase)
                ? "gonkdie1.wav"
                : "bodyfall03.wav";
            _audio?.PlaySoundPos(deathSound, npc.Thing.Position, 0.9f, 0.5f, 6.0f);
            DispatchThingCogMessageFromSource(thingId, source, "killed");
        }
        else
        {
            Console.WriteLine($"[COMBAT] Hit Thing {thingId} ({npc.Thing.TemplateName}) -> HP {npc.Health:F0}/{npc.MaxHealth:F0}");
        }
    }

    private CogValue DispatchThingCogMessage(int thingId, string message, params CogValue[] parameters) =>
        DispatchThingCogMessageFromSource(thingId, _localPlayerThingIndex, message, parameters);

    private CogValue DispatchThingCogMessageFromSource(int thingId, int sourceRefOverride, string message, params CogValue[] parameters)
    {
        bool isDamaged = string.Equals(message, "damaged", StringComparison.OrdinalIgnoreCase) && parameters.Length > 0;
        CogValue[]? currentParams = parameters.Length > 0 ? (CogValue[])parameters.Clone() : null;
        CogValue lastReturn = CogValue.Null;
        int sourceRef = sourceRefOverride >= 0 ? sourceRefOverride : _localPlayerThingIndex;

        if (_thingToCogs.TryGetValue(thingId, out var cogs))
        {
            foreach (var cog in cogs)
            {
                if (!cog.Program.Labels.ContainsKey(message))
                    continue;

                int symLinkId = GetSymbolLinkId(cog, CogSymbolType.Thing, thingId);
                CogValue ret = SafeSendMessageWithReturn(
                    cog,
                    message,
                    senderRef: thingId,
                    senderId: symLinkId,
                    sourceRef: sourceRef,
                    senderType: 3,
                    parameters: currentParams);

                if (ret.Type is CogValueType.Int or CogValueType.Float)
                {
                    lastReturn = ret;
                    if (isDamaged && currentParams != null)
                        currentParams[0] = ret;
                }
            }
        }

        if (thingId >= 0 && thingId < _level.Things.Count)
        {
            string templateName = _level.Things[thingId].TemplateName;
            if (!string.IsNullOrWhiteSpace(templateName))
            {
                string? cogName = ResolveTemplateParam(templateName, "cog");
                if (!string.IsNullOrWhiteSpace(cogName))
                {
                    var templateCog = GetOrCreateTemplateCog(cogName);
                    if (templateCog != null && templateCog.Program.Labels.ContainsKey(message))
                    {
                        CogValue ret = SafeSendMessageWithReturn(
                            templateCog,
                            message,
                            senderRef: thingId,
                            senderId: 0,
                            sourceRef: sourceRef,
                            senderType: 3,
                            parameters: currentParams);

                        if (ret.Type is CogValueType.Int or CogValueType.Float)
                            lastReturn = ret;
                    }
                }
            }
        }

        return lastReturn;
    }

    private static int GetSymbolLinkId(CogInstance cog, CogSymbolType type, int refId)
    {
        for (int i = 0; i < cog.Script.Symbols.Count && i < cog.SymbolValues.Length; i++)
        {
            var sym = cog.Script.Symbols[i];
            if (sym.Type == type &&
                cog.SymbolValues[i].Type == CogValueType.Int &&
                cog.SymbolValues[i].AsInt == refId)
            {
                return sym.LinkId;
            }
        }

        return 0;
    }

    /// <summary>
    /// Populates <paramref name="destination"/> with the current animation state snapshots for all active NPCs.
    /// </summary>
    public void GetThingAnimationSnapshots(List<ThingAnimationSnapshot> destination)
    {
        destination.Clear();
        foreach (var npc in _npcs.Values)
        {
            destination.Add(new ThingAnimationSnapshot(
                npc.Thing.ThingIndex,
                npc.AnimAction,
                npc.AnimMode,
                npc.LoopAnim,
                npc.LastDistanceMoved));
        }
    }

    private void SafeSendMessage(CogInstance instance, string message, int senderRef = -1, int senderType = -1)
    {
        try
        {
            _cogVm.SendMessage(instance, message, senderRef, -1, -1, senderType, Array.Empty<CogValue>());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[COG] Error in '{instance.Script.Name}' ({message}): {ex.Message}");
        }
    }

    private void SafeSendMessage(CogInstance instance, string message, int senderRef, params CogValue[] parameters)
    {
        try
        {
            _cogVm.SendMessage(instance, message, senderRef, parameters);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[COG] Error in '{instance.Script.Name}' ({message}): {ex.Message}");
        }
    }

    private void SafeSendMessage(CogInstance instance, string message, int senderRef, int senderId, int sourceRef, int senderType = -1)
    {
        try
        {
            _cogVm.SendMessage(instance, message, senderRef, senderId, sourceRef, senderType, Array.Empty<CogValue>());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[COG] Error in '{instance.Script.Name}' ({message}): {ex.Message}");
        }
    }

    private CogValue SafeSendMessageWithReturn(
        CogInstance instance,
        string message,
        int senderRef,
        int senderId,
        int sourceRef,
        int senderType = -1,
        params CogValue[]? parameters)
    {
        try
        {
            return _cogVm.SendMessage(instance, message, senderRef, senderId, sourceRef, senderType, parameters);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[COG] Error in '{instance.Script.Name}' ({message}): {ex.Message}");
            return CogValue.Null;
        }
    }

    private static float LerpAngleDegrees(float a, float b, float t)
    {
        float delta = WrapAngle180(b - a);
        return a + delta * t;
    }

    private static float WrapAngle180(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }

    // -------------------------------------------------------------------------
    // ICogWorldContext implementation
    // -------------------------------------------------------------------------

    public int GetThingSector(int thingId)
    {
        if (thingId >= 0 && thingId < _level.Things.Count)
            return _level.Things[thingId].SectorIndex;
        return -1;
    }

    public Vector3 GetThingPos(int thingId)
    {
        if (thingId >= 0 && thingId < _level.Things.Count)
            return _level.Things[thingId].Position;
        return Vector3.Zero;
    }

    public void MoveToFrame(int thingId, int frame, float speed)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;
        if (!_thingFrames.TryGetValue(thingId, out var frames) || frame < 0 || frame >= frames.Count)
            return;

        var thing = _level.Things[thingId];
        var target = frames[frame];

        if (!_thingMotions.TryGetValue(thingId, out var motion))
        {
            motion = new ThingMotionState();
            _thingMotions[thingId] = motion;
        }

        motion.StartPos = thing.Position;
        motion.TargetPos = target.Position;
        motion.StartPyr = thing.PitchYawRoll;
        motion.TargetPyr = target.PitchYawRoll;
        motion.TargetFrame = frame;
        motion.Elapsed = 0f;
        motion.IsPaused = false;
        motion.IsPivotRotation = false;

        float posDist = Vector3.Distance(motion.StartPos, motion.TargetPos);
        float angleDist = MathF.Max(
            MathF.Abs(WrapAngle180(motion.TargetPyr.Y - motion.StartPyr.Y)),
            MathF.Max(
                MathF.Abs(WrapAngle180(motion.TargetPyr.X - motion.StartPyr.X)),
                MathF.Abs(WrapAngle180(motion.TargetPyr.Z - motion.StartPyr.Z))));

        // Sith Engine MoveToFrame speed is in 0.1 JKL units/sec (0.5 when speed == 0)
        float worldSpeed = speed <= 0f ? 0.5f : speed * 0.1f;
        if (posDist > 1e-4f)
            motion.Duration = posDist / worldSpeed;
        else if (angleDist > 1e-3f)
            motion.Duration = angleDist / MathF.Max(15f, speed * 15f);
        else
            motion.Duration = 0f;

        motion.IsMoving = true;

        if (IsDoorThing(thing) && frame == 1 && thing.SectorIndex >= 0)
        {
            SetSectorAdjoins(thing.SectorIndex, true);
        }

        if (thing.TemplateName.Contains("door", StringComparison.OrdinalIgnoreCase))
        {
            _audio?.PlaySoundPos("df_door1-1.wav", thing.Position, 0.85f, 0.6f, 6.0f);
        }
        else if (thing.TemplateName.Contains("elev", StringComparison.OrdinalIgnoreCase) ||
                 thing.TemplateName.Contains("funicular", StringComparison.OrdinalIgnoreCase))
        {
            _audio?.PlaySoundPos("df_elev1-1.wav", thing.Position, 0.7f, 0.6f, 6.0f);
        }
    }

    public bool IsThingMoving(int thingId)
    {
        return _thingMotions.TryGetValue(thingId, out var motion) && motion.IsMoving;
    }

    public int GetCurFrame(int thingId)
    {
        return _thingMotions.TryGetValue(thingId, out var motion) ? motion.CurrentFrame : 0;
    }

    public int GetLocalPlayerThing() => _activeCogPlayerContext ?? _localPlayerThingIndex;

    public float GetLevelTime() => _elapsedLevelTime;

    public void SetSectorAdjoins(int sectorId, bool visible)
    {
        if (sectorId < 0 || sectorId >= _level.Sectors.Count)
            return;

        _sectorAdjoinsVisible[sectorId] = visible;
        var sector = _level.Sectors[sectorId];

        foreach (var surface in sector.Surfaces)
        {
            if (!_origSurfaceAdjoins.TryGetValue(surface.SurfaceIndex, out int origAdjoin) || origAdjoin < 0)
                continue;

            SetSurfaceAdjoinActive(surface, origAdjoin, visible);

            if (surface.MirrorSurface != null &&
                _origSurfaceAdjoins.TryGetValue(surface.MirrorSurface.SurfaceIndex, out int mirrorOrig) &&
                mirrorOrig >= 0)
            {
                SetSurfaceAdjoinActive(surface.MirrorSurface, mirrorOrig, visible);
            }
        }
    }

    private void SetSurfaceAdjoinActive(JklSurface surface, int origAdjoinIdx, bool visible)
    {
        surface.AdjoinIndex = visible ? origAdjoinIdx : -1;

        if (origAdjoinIdx >= 0 && origAdjoinIdx < _level.Adjoins.Count)
        {
            int baseFlags = _origAdjoinFlags.GetValueOrDefault(origAdjoinIdx, _level.Adjoins[origAdjoinIdx].Flags);
            _level.Adjoins[origAdjoinIdx].Flags = visible ? baseFlags : (baseFlags & ~0x3);
        }
    }

    public void SetSectorLight(int sectorId, float light, float delay)
    {
        if (sectorId < 0 || sectorId >= _level.Sectors.Count)
            return;

        _sectorLights[sectorId] = light;
        _level.Sectors[sectorId].ExtraLight = light;
        OnSectorLightChanged?.Invoke(sectorId, light);
    }

    public int PlaySoundThing(string sound, int thingId, float volume, float minRad, float maxRad, int flags)
    {
        if (string.IsNullOrWhiteSpace(sound) || sound.Trim() == "-1")
            return 0;

        bool looping = (flags & 0x01) != 0;
        string loopKey = looping ? $"{thingId}:{sound}" : string.Empty;
        if (looping && _activeLoopingSounds.TryGetValue(loopKey, out int existingChannel))
            return existingChannel;

        if (_audio == null || !_audio.IsAvailable)
        {
            int synthetic = _nextHeadlessSoundChannel++;
            if (looping)
                _activeLoopingSounds[loopKey] = synthetic;
            return synthetic;
        }

        Vector3 pos = GetThingPos(thingId);
        float minR = minRad > 0f ? minRad : 0.5f;
        float maxR = maxRad > minR ? maxRad : 8.0f;
        int channel = _audio.PlaySoundPos(sound, pos, volume, minR, maxR, looping);
        if (looping && channel > 0)
            _activeLoopingSounds[loopKey] = channel;
        return channel;
    }

    public void PlaySoundLocal(string sound, float volume = 1.0f)
    {
        _audio?.PlaySoundLocal(sound, volume);
    }

    public void PlaySoundPos(string sound, Vector3 position, float volume = 1.0f, float minRad = 0.5f, float maxRad = 8.0f)
    {
        _audio?.PlaySoundPos(sound, position, volume, minRad, maxRad);
    }

    public int PlaySoundLocal(string sound, float volume, float pan, int flags)
    {
        if (string.IsNullOrWhiteSpace(sound))
            return 0;

        if (_audio == null || !_audio.IsAvailable)
            return _nextHeadlessSoundChannel++;

        return _audio.PlaySoundLocal(sound, volume, (flags & 0x01) != 0);
    }

    public int PlaySoundPos(string sound, Vector3 pos, float volume, float minRad, float maxRad, int flags)
    {
        if (string.IsNullOrWhiteSpace(sound))
            return 0;

        if (_audio == null || !_audio.IsAvailable)
            return _nextHeadlessSoundChannel++;

        float minR = minRad > 0f ? minRad : 0.5f;
        float maxR = maxRad > minR ? maxRad : 8.0f;
        return _audio.PlaySoundPos(sound, pos, volume, minR, maxR, (flags & 0x01) != 0);
    }

    public int SendMessage(int cogId, string message)
    {
        if (cogId >= 0)
            _cogVm.SendMessage(cogId, message);
        return 0;
    }

    public void Print(string text)
    {
        Console.WriteLine($"[COG PRINT] {text}");
    }

    public float Rand() => (float)_random.NextDouble();

    public int CreateThing(string template, int refThing)
    {
        int sector = GetThingSector(refThing);
        Vector3 pos = GetThingPos(refThing);
        Vector3 pyr = (refThing >= 0 && refThing < _level.Things.Count)
            ? _level.Things[refThing].PitchYawRoll
            : Vector3.Zero;
        return CreateThingAtPos(template, sector, pos, pyr);
    }

    public int CreateThingAtPos(string template, int sector, Vector3 pos, Vector3 pyr)
    {
        int newId = _level.Things.Count;
        string model3d = !string.IsNullOrWhiteSpace(template) ? _level.ResolveModel3D(template) : string.Empty;

        if (!string.IsNullOrWhiteSpace(template))
        {
            string? type = ResolveTemplateParam(template, "type");
            string? cogName = ResolveTemplateParam(template, "cog");
            bool isItem = string.Equals(type, "item", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(cogName) && cogName.StartsWith("pow_", StringComparison.OrdinalIgnoreCase));

            if (isItem && sector >= 0 && sector < _level.Sectors.Count && TryGetFloorZBelow(sector, pos, out float floorZ))
            {
                var model = !string.IsNullOrEmpty(model3d) ? TryLoadThreeDo(model3d) : null;
                float offsetZ = (model?.InsertOffset.Z > 0.001f) ? model.InsertOffset.Z : 0.03f;
                if (pos.Z > floorZ + offsetZ)
                    pos = new Vector3(pos.X, pos.Y, floorZ + offsetZ);
            }
        }

        var thing = new JklThing
        {
            ThingIndex = newId,
            TemplateName = template ?? string.Empty,
            SectorIndex = sector,
            Position = pos,
            PitchYawRoll = pyr,
            Model3D = model3d,
            ThingFlags = 0
        };
        _level.Things.Add(thing);
        if (!string.IsNullOrWhiteSpace(template))
        {
            string? type = ResolveTemplateParam(template, "type");
            string? sprite = ResolveTemplateParam(template, "sprite");
            if (string.Equals(type, "explosion", StringComparison.OrdinalIgnoreCase))
            {
                float dmg = ParseTemplateFloat(template, "damage", 0f);
                float rng = ParseTemplateFloat(template, "range", 0f);
                if (dmg > 0f && rng > 0f)
                    TriggerExplosion(pos, template, _activeCogPlayerContext ?? _localPlayerThingIndex);
                else
                    SpawnVisualEffectFromTemplate(template, pos);
            }
            else if (!string.IsNullOrWhiteSpace(sprite))
            {
                SpawnVisualEffectFromTemplate(template, pos);
            }
        }
        if (TrySpawnNpcRuntime(thing))
            DispatchThingCogMessage(newId, "created");

        return newId;
    }

    /// <summary>
    /// Captures <paramref name="thingId"/> for COG <paramref name="cogId"/>, setting
    /// <see cref="SithThingFlags.Captured"/> and indexing the COG binding.
    /// </summary>
    public void CaptureThing(int thingId, int cogId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;

        SetThingFlags(thingId, (int)SithThingFlags.Captured);
        foreach (var instance in _cogVm.Instances)
        {
            if (instance.CogId == cogId)
            {
                AddUniqueMapping(_thingToCogs, thingId, instance);
                break;
            }
        }
    }

    public void DestroyThing(int thingId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;

        _destroyedThings.Add(thingId);
        _thingLifeleft.Remove(thingId);

        if (_npcs.TryGetValue(thingId, out var npc))
        {
            npc.IsAlive = false;
            npc.Health = 0f;
            npc.Brain.Mode = AiMode.Disabled;
        }

        var thing = _level.Things[thingId];
        thing.IsNonSolid = true;
        SetThingFlags(thingId, (int)(SithThingFlags.Disabled | SithThingFlags.Destroyed));
    }

    public void SetLifeleft(int thingId, float seconds)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;

        if (seconds <= 0f)
            DestroyThing(thingId);
        else
            _thingLifeleft[thingId] = seconds;
    }

    public float DamageThing(int thingId, float damage, int flags, int damager)
    {
        if (damage <= 0f)
            return 0f;

        if (_playersByThing.TryGetValue(thingId, out var player))
        {
            float before = player.Inventory.Health;
            ApplyDamageToPlayer(player, damager, damage, "kbodyhit01.wav");
            return before - player.Inventory.Health;
        }

        if (_npcs.TryGetValue(thingId, out var npc) && npc.IsAlive)
        {
            float before = npc.Health;
            ApplyNpcDamage(thingId, damage, GetThingPos(damager), flags, damager);
            return MathF.Max(0f, before - npc.Health);
        }

        return 0f;
    }

    public void HealThing(int thingId, float amount)
    {
        if (amount <= 0f)
            return;

        if (_playersByThing.TryGetValue(thingId, out var player))
        {
            player.Inventory.Heal(amount);
        }
        else if (_npcs.TryGetValue(thingId, out var npc) && npc.IsAlive)
        {
            npc.Health = MathF.Min(npc.MaxHealth, npc.Health + amount);
        }
    }

    public float GetThingHealth(int thingId)
    {
        if (_playersByThing.TryGetValue(thingId, out var player))
            return player.Inventory.Health;

        return _npcs.TryGetValue(thingId, out var npc) ? npc.Health : 0f;
    }

    public float GetThingMaxHealth(int thingId)
    {
        if (_playersByThing.TryGetValue(thingId, out var player))
            return player.Inventory.MaxHealth;

        return _npcs.TryGetValue(thingId, out var npc) ? npc.MaxHealth : 100f;
    }

    public string GetThingTemplate(int thingId)
    {
        if (thingId >= 0 && thingId < _level.Things.Count)
            return _level.Things[thingId].TemplateName;
        return string.Empty;
    }

    public void SetThingVel(int thingId, Vector3 vel)
    {
        _thingVelocities[thingId] = vel;
        if (_playersByThing.TryGetValue(thingId, out var player))
        {
            player.VelocityOverride = vel;
            player.HasVelocityOverride = true;
        }
    }

    public void AddThingVel(int thingId, Vector3 vel)
    {
        Vector3 baseVel = _playersByThing.TryGetValue(thingId, out var player) && player.HasVelocityOverride
            ? player.VelocityOverride
            : GetThingVel(thingId);
        SetThingVel(thingId, baseVel + vel);
    }

    public Vector3 GetThingVel(int thingId) => _thingVelocities.GetValueOrDefault(thingId, Vector3.Zero);

    public void ApplyForce(int thingId, Vector3 force)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;

        float mass = ParseTemplateFloat(_level.Things[thingId].TemplateName, "mass", 1.0f);
        float invMass = mass > 0.001f ? 1.0f / mass : 1.0f;
        AddThingVel(thingId, force * invMass);
    }

    public int GetThingClassCog(int thingId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return -1;

        string? cogName = ResolveTemplateParam(_level.Things[thingId].TemplateName, "cog");
        if (string.IsNullOrWhiteSpace(cogName))
            return -1;

        return GetOrCreateTemplateCog(cogName)?.CogId ?? -1;
    }

    public int GetThingType(int thingId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return 0;

        if (_playersByThing.ContainsKey(thingId))
            return 10;

        if (_npcs.ContainsKey(thingId))
            return 2;

        string? type = ResolveTemplateParam(_level.Things[thingId].TemplateName, "type");
        if (string.IsNullOrWhiteSpace(type))
            return 0;

        return type.ToLowerInvariant() switch
        {
            "free" => 0,
            "camera" => 1,
            "actor" => 2,
            "weapon" => 3,
            "debris" => 4,
            "item" => 5,
            "explosion" => 6,
            "cog" => 7,
            "ghost" => 8,
            "corpse" => 9,
            "player" => 10,
            "particle" => 11,
            _ => 0
        };
    }

    public void SetActorExtraSpeed(int thingId, float extraSpeed) =>
        _actorExtraSpeeds[thingId] = extraSpeed;

    public float GetActorExtraSpeed(int thingId) =>
        _actorExtraSpeeds.GetValueOrDefault(thingId, 0f);

    public void StopThing(int thingId)
    {
        _thingVelocities[thingId] = Vector3.Zero;
        if (_thingMotions.TryGetValue(thingId, out var motion))
            motion.IsMoving = false;
    }

    public int GetThingFlags(int thingId)
    {
        if (_thingFlags.TryGetValue(thingId, out int flags))
            return flags;
        if (thingId >= 0 && thingId < _level.Things.Count)
            return (int)_level.Things[thingId].ThingFlags;
        return 0;
    }

    public void SetThingFlags(int thingId, int flags)
    {
        _thingFlags[thingId] = GetThingFlags(thingId) | flags;
        if (thingId >= 0 && thingId < _level.Things.Count)
            _level.Things[thingId].ThingFlags |= (uint)flags;
    }

    public void ClearThingFlags(int thingId, int flags)
    {
        _thingFlags[thingId] = GetThingFlags(thingId) & ~flags;
        if (thingId >= 0 && thingId < _level.Things.Count)
            _level.Things[thingId].ThingFlags &= ~(uint)flags;
    }

    public int GetDifficulty() => Difficulty;

    public int GetActorFlags(int thingId) => _actorFlags.GetValueOrDefault(thingId, 0);

    public void SetActorFlags(int thingId, int flags)
    {
        _actorFlags[thingId] = GetActorFlags(thingId) | flags;
        UpdateThingInvisibleFlag(thingId);
    }

    public void ClearActorFlags(int thingId, int flags)
    {
        _actorFlags[thingId] = GetActorFlags(thingId) & ~flags;
        UpdateThingInvisibleFlag(thingId);
    }

    public int GetThingGeoMode(int thingId) => _origGeoModes.GetValueOrDefault(thingId, 0);

    public int GetThingCurGeoMode(int thingId) => _curGeoModes.GetValueOrDefault(thingId, GetThingGeoMode(thingId));

    public void SetThingCurGeoMode(int thingId, int geoMode)
    {
        _curGeoModes[thingId] = geoMode;
        UpdateThingInvisibleFlag(thingId);
    }

    /// <summary>
    /// Returns true if the thing is currently hidden via <c>SITH_AF_INVISIBLE</c> (<c>0x80</c>)
    /// or <c>CurGeoMode == 0</c>.
    /// </summary>
    public bool IsThingInvisible(int thingId) =>
        (GetActorFlags(thingId) & 0x80) != 0 || GetThingCurGeoMode(thingId) == 0;

    private void UpdateThingInvisibleFlag(int thingId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;

        var thing = _level.Things[thingId];
        bool shouldHide = IsThingInvisible(thingId);
        if (shouldHide)
        {
            if ((thing.ThingFlags & (uint)SithThingFlags.Invisible) == 0)
            {
                thing.ThingFlags |= (uint)SithThingFlags.Invisible;
                _hiddenBySim.Add(thingId);
            }
        }
        else if (_hiddenBySim.Remove(thingId))
        {
            thing.ThingFlags &= ~(uint)SithThingFlags.Invisible;
        }
    }

    public int GetCurSector()
    {
        int pId = GetLocalPlayerThing();
        if (pId >= 0 && _playersByThing.TryGetValue(pId, out var p))
            return p.PrevSectorIndex;
        return -1;
    }

    public float ChangeInv(int thingId, int bin, float delta) =>
        _playersByThing.TryGetValue(thingId, out var player) ? player.Inventory.ChangeInv(bin, delta) : 0f;

    public float GetInv(int thingId, int bin) =>
        _playersByThing.TryGetValue(thingId, out var player) ? player.Inventory.GetInv(bin) : 0f;

    public void SetInv(int thingId, int bin, float val)
    {
        if (_playersByThing.TryGetValue(thingId, out var player))
            player.Inventory.SetInv(bin, val);
    }

    public float GetInvMax(int thingId, int bin) => PlayerInventory.GetInvMax(bin);

    public void SetGoalFlags(int thingId, int goalIdx, int flags)
    {
        if (_playersByThing.TryGetValue(thingId, out var player))
            player.Inventory.SetGoalFlags(goalIdx, flags);
    }

    public void ClearGoalFlags(int thingId, int goalIdx, int flags)
    {
        if (_playersByThing.TryGetValue(thingId, out var player))
            player.Inventory.ClearGoalFlags(goalIdx, flags);
    }

    public int GetGoalFlags(int thingId, int goalIdx) =>
        _playersByThing.TryGetValue(thingId, out var player) ? player.Inventory.GetGoalFlags(goalIdx) : 0;

    public void TakeItem(int itemThingId, int playerThingId)
    {
        CogInstance? cog = null;
        if (!_pickupCogs.TryGetValue(itemThingId, out cog) &&
            itemThingId >= 0 && itemThingId < _level.Things.Count)
        {
            string? cogName = ResolveTemplateParam(_level.Things[itemThingId].TemplateName, "cog");
            if (!string.IsNullOrWhiteSpace(cogName))
                cog = GetOrCreateTemplateCog(cogName);
        }

        DestroyThing(itemThingId);

        if (cog != null && cog.Program.Labels.ContainsKey("taken"))
        {
            WithPlayerCogContext(playerThingId >= 0 ? playerThingId : _localPlayerThingIndex, () =>
                SafeSendMessage(cog, "taken", senderRef: itemThingId, senderId: 0, sourceRef: playerThingId, senderType: 3));
        }
    }

    public int GetActorWeapon(int thingId, int slot)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return -1;

        string paramKey = slot == 2 ? "weapon2" : "weapon";
        string? weapon = ResolveTemplateParam(_level.Things[thingId].TemplateName, paramKey);
        return !string.IsNullOrWhiteSpace(weapon) && !weapon.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? 1
            : -1;
    }

    public int IsMulti() => IsMultiplayer ? 1 : 0;

    public int GetMajorMode() => GetMajorMode(GetLocalPlayerThing());

    public int GetMajorMode(int thingId) =>
        TryResolvePlayer(thingId, out var player) && player.IsUnderwater ? 5 : 0;

    public Vector3 GetThingThrust(int thingId) =>
        _playersByThing.TryGetValue(thingId, out var player) ? player.Thrust : Vector3.Zero;

    public void SetArmedMode(int thingId, int mode)
    {
        if (_playersByThing.TryGetValue(thingId, out var player))
            player.ArmedMode = mode;
    }

    public float GetThingUserData(int thingId) => _thingUserData.GetValueOrDefault(thingId, 0f);

    public void SetThingUserData(int thingId, float data) => _thingUserData[thingId] = data;

    public void AmputateJoint(int thingId, int jointIdx)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;
        if (!_amputatedJoints.TryGetValue(thingId, out var joints))
        {
            joints = [];
            _amputatedJoints[thingId] = joints;
        }
        joints.Add(jointIdx);
    }

    public bool IsJointAmputated(int thingId, int jointIdx) =>
        _amputatedJoints.TryGetValue(thingId, out var joints) && joints.Contains(jointIdx);

    public void ThingLight(int thingId, float light, float fadeTime) => _thingLights[thingId] = light;

    public float GetThingLight(int thingId) => _thingLights.GetValueOrDefault(thingId, 0f);

    public void SetThingHealth(int thingId, float health)
    {
        if (_playersByThing.TryGetValue(thingId, out var player))
        {
            player.Inventory.Health = MathF.Max(0f, health);
        }
        else if (_npcs.TryGetValue(thingId, out var npc))
        {
            npc.Health = MathF.Max(0f, health);
            npc.IsAlive = npc.Health > 0f;
        }
    }

    public void SetCollideType(int thingId, int collideType)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;
        _thingCollideTypes[thingId] = collideType;
        _level.Things[thingId].IsNonSolid = collideType == 0;
    }

    public int GetCollideType(int thingId) => _thingCollideTypes.GetValueOrDefault(thingId, 1);

    public void AISetLookFrame(int thingId, int frame)
    {
        if (_thingFrames.TryGetValue(thingId, out var frames) && frame >= 0 && frame < frames.Count)
            AISetLookPos(thingId, frames[frame].Position);
    }

    public void AISetMoveFrame(int thingId, int frame)
    {
        if (_thingFrames.TryGetValue(thingId, out var frames) && frame >= 0 && frame < frames.Count)
            AISetMovePos(thingId, frames[frame].Position);
    }

    public void AISetMoveSpeed(int thingId, float speed)
    {
        if (_npcs.TryGetValue(thingId, out var npc))
            npc.MoveSpeed = MathF.Max(0f, speed);
    }

    public void AISetClass(int thingId, string aiClass)
    {
        if (string.IsNullOrWhiteSpace(aiClass) || !_npcs.TryGetValue(thingId, out var npc))
            return;

        var newClass = LoadAiClass(aiClass);
        if (ReferenceEquals(npc.Brain.Class, newClass) ||
            string.Equals(npc.Brain.Class.Name, newClass.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var oldBrain = npc.Brain;
        npc.Brain = new AiBrain(newClass, oldBrain.HomePosition, oldBrain.Mode)
        {
            TargetThingIndex = oldBrain.TargetThingIndex,
            LastKnownTargetPosition = oldBrain.LastKnownTargetPosition,
            AlertTimer = oldBrain.AlertTimer,
            FireCooldown = oldBrain.FireCooldown
        };
    }

    public void AISetMovePos(int thingId, Vector3 pos)
    {
        if (_npcs.TryGetValue(thingId, out var npc) && npc.IsAlive)
            npc.OverrideMovePos = pos;
    }

    public void AISetLookPos(int thingId, Vector3 pos)
    {
        if (_npcs.TryGetValue(thingId, out var npc) && npc.IsAlive)
        {
            npc.OverrideLookPos = pos;
            npc.OverrideLookTimer = 2.0f;
        }
    }

    public void AIJump(int thingId, int destThingId, float speed)
    {
        if (!_npcs.TryGetValue(thingId, out var npc) || !npc.IsAlive)
            return;

        Vector3 start = npc.Thing.Position;
        Vector3 destPos = GetThingPos(destThingId);
        Vector3 delta = destPos - start;
        Vector3 horiz = new(delta.X, delta.Y, 0f);
        float horizDist = horiz.Length();

        float flightSpeed = Math.Clamp(speed > 0f ? speed * 0.25f : 1.25f, 0.5f, 3.0f);
        float flightTime = Math.Clamp(horizDist / flightSpeed, 0.35f, 1.5f);
        Vector3 horizVel = horizDist > 1e-4f ? (horiz / horizDist) * (horizDist / flightTime) : Vector3.Zero;

        const float gravity = 3.5f;
        float vz = (delta.Z / flightTime) + 0.5f * gravity * flightTime;
        npc.Velocity = new Vector3(horizVel.X, horizVel.Y, MathF.Max(vz, 0.8f));
        npc.IsAirborne = true;
    }

    public void SectorThrust(int sector, Vector3 dir, float speed)
    {
        if (sector < 0 || sector >= _level.Sectors.Count)
            return;

        Vector3 normDir = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.Zero;
        _sectorThrusts[sector] = normDir * speed;
    }

    public void AIFlee(int thingId, int targetThing)
    {
        if (_npcs.TryGetValue(thingId, out var npc))
        {
            npc.Brain.TargetThingIndex = targetThing;
            npc.Brain.LastKnownTargetPosition = GetThingPos(targetThing);
            npc.Brain.Mode |= AiMode.Fleeing | AiMode.Active;
            npc.Brain.InstinctTimers["Flee"] = AiBrain.DefaultFleeDuration;
        }
    }

    public void AiSetMode(int thingId, int mode)
    {
        if (_npcs.TryGetValue(thingId, out var npc))
            npc.Brain.SetAiModeFlags((uint)mode);
    }

    public void AiClearMode(int thingId, int mode)
    {
        if (_npcs.TryGetValue(thingId, out var npc))
            npc.Brain.ClearAiModeFlags((uint)mode);
    }

    public int AiGetMode(int thingId) =>
        _npcs.TryGetValue(thingId, out var npc) ? (int)npc.Brain.Mode : -1;

    public void SetSurfaceCel(int surfaceId, int cel) => _surfaceCels[surfaceId] = cel;

    public int GetSurfaceCel(int surfaceId) => _surfaceCels.GetValueOrDefault(surfaceId, 0);

    public void SlideSurface(int surfaceId, Vector3 dir, float speed)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return;

        OnSurfaceScroll?.Invoke(surfaceId, new Vector2(dir.X, dir.Y), speed);
    }

    public Vector3 SurfaceCenter(int surfaceId)
    {
        if (surfaceId >= 0 && surfaceId < _level.Surfaces.Count)
            return ComputeSurfaceCenter(_level.Surfaces[surfaceId]);

        return Vector3.Zero;
    }

    public int HasLOS(int thingA, int thingB)
    {
        if (thingA < 0 || thingA >= _level.Things.Count || thingB < 0 || thingB >= _level.Things.Count)
            return 0;

        Vector3 eyeOffset = new(0f, 0f, 0.12f);
        return HasLineOfSight(_level.Things[thingA].Position + eyeOffset, _level.Things[thingB].Position + eyeOffset) ? 1 : 0;
    }

    public float ThingViewDot(int viewerThing, int targetThing)
    {
        if (viewerThing < 0 || viewerThing >= _level.Things.Count || targetThing < 0 || targetThing >= _level.Things.Count)
            return -1000f;

        Vector3 forward = GetThingForward(viewerThing);
        Vector3 toTarget = _level.Things[targetThing].Position - _level.Things[viewerThing].Position;
        if (toTarget.LengthSquared() < 1e-8f)
            return 1f;

        return Vector3.Dot(Vector3.Normalize(forward), Vector3.Normalize(toTarget));
    }

    public int FirstThingInView(int viewerThing, float fov, float maxDist, int typeMask)
    {
        _viewSearchResults.Clear();
        _viewSearchIndex = 0;

        if (viewerThing < 0 || viewerThing >= _level.Things.Count)
            return -1;

        Vector3 viewerPos = _level.Things[viewerThing].Position;
        float minDot = fov > 0f && fov < 360f ? MathF.Cos(fov * 0.5f * (MathF.PI / 180f)) : -1f;
        float maxDistSq = maxDist > 0f ? maxDist * maxDist : float.MaxValue;
        var candidates = new List<(int ThingId, float DistSq)>();

        for (int i = 0; i < _level.Things.Count; i++)
        {
            if (i == viewerThing || _destroyedThings.Contains(i))
                continue;

            var thing = _level.Things[i];
            if (thing.IsPlayerSpawn ||
                (thing.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0)
                continue;

            if (typeMask > 0)
            {
                int thingType = GetThingType(i);
                if (thingType <= 0 || ((typeMask & (1 << thingType)) == 0 && thingType != typeMask))
                    continue;
            }

            float distSq = Vector3.DistanceSquared(viewerPos, thing.Position);
            if (distSq > maxDistSq)
                continue;

            if (ThingViewDot(viewerThing, i) < minDot)
                continue;

            if (HasLOS(viewerThing, i) == 0)
                continue;

            candidates.Add((i, distSq));
        }

        candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        foreach (var (thingId, _) in candidates)
            _viewSearchResults.Add(thingId);

        return _viewSearchResults.Count > 0 ? _viewSearchResults[_viewSearchIndex++] : -1;
    }

    public int NextThingInView() =>
        _viewSearchIndex < _viewSearchResults.Count ? _viewSearchResults[_viewSearchIndex++] : -1;

    private Vector3 GetThingForward(int thingId)
    {
        if (_playersByThing.TryGetValue(thingId, out var player) && player.HasAimDirection)
            return player.AimDirection;

        if (thingId < 0 || thingId >= _level.Things.Count)
            return Vector3.UnitY;

        var pyr = _level.Things[thingId].PitchYawRoll;
        float yawRad = pyr.Y * (MathF.PI / 180f);
        float pitchRad = pyr.X * (MathF.PI / 180f);
        return Vector3.Normalize(new Vector3(
            -MathF.Sin(yawRad) * MathF.Cos(pitchRad),
            MathF.Cos(yawRad) * MathF.Cos(pitchRad),
            MathF.Sin(pitchRad)));
    }

    public void JkEndLevel(int success)
    {
        LevelCompleted = success != 0;
        ShowStatusMessage(LevelCompleted ? "LEVEL COMPLETED!" : "MISSION FAILED!", 10.0f);

        var snapshot = new List<CogInstance>(_cogVm.Instances);
        foreach (var instance in snapshot)
        {
            if (instance.Program.Labels.ContainsKey("shutdown"))
                SafeSendMessage(instance, "shutdown", senderRef: _localPlayerThingIndex, senderId: -1, sourceRef: _localPlayerThingIndex);
        }
    }

    public void JkPrintUniString(int player, int strId)
    {
        string text;
        if (_strings.TryGetCogString(strId, out string localized))
        {
            text = localized;
        }
        else
        {
            text = strId switch
            {
                2 => "BRYAR PISTOL",
                3 => "STORMTROOPER RIFLE",
                4 => "THERMAL DETONATORS",
                10 => "LIGHTSABER",
                11 => "ENERGY CELL",
                12 => "POWER CELL",
                13 => "BATTERY",
                40 => "BACTA TANK",
                41 => "IR GOGGLES",
                60 => "SHIELD RECHARGE",
                201 => "HEALTH PACK",
                207 => "ARMOR VEST",
                209 => "SMUGGLER BACKPACK",
                350 => "OBJECTIVE COMPLETED",
                360 => "SECRET AREA FOUND!",
                _ => $"COG MSG {strId}"
            };
        }

        if (TryResolvePlayer(player, out var targetPlayer))
        {
            targetPlayer.StatusMessage = text;
            targetPlayer.StatusTimer = 3.5f;
        }
        else
        {
            _globalStatusMessage = text;
            _globalStatusTimer = 3.5f;
        }
        Console.WriteLine($"[HUD] {text}");
    }

    public void JkSetFlags(int thingId, int flags) =>
        _jkFlags[thingId] = JkGetFlags(thingId) | flags;

    public int JkGetFlags(int thingId) =>
        _jkFlags.GetValueOrDefault(thingId, 0);

    public void JkClearFlags(int thingId, int flags) =>
        _jkFlags[thingId] = JkGetFlags(thingId) & ~flags;

    public void JkSetWeaponMesh(int thingId, string model) =>
        _weaponMeshes[thingId] = model ?? string.Empty;

    public string JkGetWeaponMesh(int thingId) =>
        _weaponMeshes.GetValueOrDefault(thingId, string.Empty);

    public void JkSetSaberInfo(
        int thingId,
        string sideMat,
        string tipMat,
        float baseRad,
        float tipRad,
        float length,
        string wallSparks,
        string bloodSparks,
        string saberSparks) =>
        _saberInfos[thingId] = new SaberInfo(sideMat, tipMat, baseRad, tipRad, length, wallSparks, bloodSparks, saberSparks);

    /// <summary>
    /// Retrieves lightsaber blade appearance parameters for <paramref name="thingId"/> if configured via <c>JkSetSaberInfo</c>.
    /// </summary>
    public bool TryGetThingSaberInfo(int thingId, out ThingSaberInfo saberInfo)
    {
        if (_saberInfos.TryGetValue(thingId, out var info))
        {
            saberInfo = new ThingSaberInfo(info.SideMat, info.TipMat, info.BaseRad, info.tipRad, info.Length);
            return true;
        }

        saberInfo = default;
        return false;
    }

    /// <summary>
    /// Returns the current animation action name for an active NPC (e.g., <c>"stand"</c>, <c>"block"</c>, <c>"fire"</c>).
    /// </summary>
    public string GetNpcAnimAction(int thingId) =>
        _npcs.TryGetValue(thingId, out var npc) ? npc.AnimAction : string.Empty;

    /// <summary>
    /// Returns whether an active NPC is affected by gravity (<c>physflags &amp; 0x1</c>; <c>false</c> for <c>_flyactor</c>).
    /// </summary>
    public bool GetNpcUseGravity(int thingId) =>
        !_npcs.TryGetValue(thingId, out var npc) || npc.UseGravity;

    public void ShowStatusMessage(string message, float duration = 4.0f)
    {
        if (LocalPlayer != null)
        {
            LocalPlayer.StatusMessage = message;
            LocalPlayer.StatusTimer = duration;
        }
        else
        {
            _globalStatusMessage = message;
            _globalStatusTimer = duration;
        }
    }

    public int PlayKey(int thingId, string keyName, int priority, int flags)
    {
        if (thingId < 0)
            return -1;

        if (!string.IsNullOrWhiteSpace(keyName))
        {
            bool loop = (flags & 0x14) == 0;
            OnPlayThingKeyframe?.Invoke(thingId, keyName, loop);
        }

        return _nextKeyTrackId++;
    }

    public bool IsInCutscene => _localPlayerThingIndex >= 0 && (GetActorFlags(_localPlayerThingIndex) & 0xa00000) != 0;

    /// <summary>
    /// Fast-forwards active COG sleep timers until the intro/cutscene lock (0xa00000) clears.
    /// </summary>
    public void SkipActiveCutscene()
    {
        for (int i = 0; i < 60 && IsInCutscene; i++)
        {
            _cogVm.Update(1.0f);
        }

        if (!IsInCutscene)
        {
            // Advance 3s post-intro timer (e.g. 07_rahnlines.cog user1 unlock) cleanly
            _cogVm.Update(3.5f);
            ShowStatusMessage("DUEL STARTED", 2.0f);
        }
    }
}
