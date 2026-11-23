using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenSith.Core.Cog;
using OpenSith.Core.Data;
using OpenSith.Core.Materials;
using OpenSith.Core.Models;
using OpenSith.Core.Networking;
using OpenSith.Core.Physics;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Silk.NET.Input;

namespace OpenSith.Client;

public static partial class Program
{
    private sealed class RemotePlayerVisual
    {
        public Vector3 Position;
        public Vector3 TargetPosition;
        public float Pitch;
        public float Yaw;
        public bool IsDead;
        public bool IsMovingFlag;
        public bool IsFiringFlag;
        public bool IsUnderwaterFlag;
        public bool IsAirborneFlag;
        public bool SaberActive;
        public int WeaponBin;
        public byte ModelIndex;
        public byte SaberColorIndex = PlayerCustomizationCatalog.DefaultSaberColorIndex;
        public Vector2 SmoothedHorizVel;
        public float SmoothedVelZ;
        public float AirborneSeconds;
        public float DescendingSeconds;
        public float MoveGraceSeconds;
        public bool Initialized;
    }

    private static string _assetsDir = SteamPath;
    private static string? _connectHost;
    private static int _connectPort = NetCodec.DefaultPort;
    private static string _playerName = "Kyle";
    private static int _playerTeam;
    private static string? _episodeGobOverride;

    private static MultiplayerClient? _netClient;
    private static ServerWelcomePacket? _netWelcome;
    private static ServerSnapshotPacket? _latestSnapshot;
    private static int _netLocalThingId = -1;
    private static uint _appliedTeleportSeq = 1;
    private static uint _appliedVelocitySeq;
    private static byte _actionSeq = 1;
    private static byte _pendingWeaponBin;
    private static byte _pendingItemBin;
    private static bool _pendingUsePressed;
    private static bool _pendingRespawnPressed;
    private static byte _heldForceBin;
    private static bool _fireKeyPrimary;
    private static bool _fireKeySecondary;
    private static bool _pendingPrimaryFireTap;
    private static bool _pendingSecondaryFireTap;
    private static int _lastNetWeaponBin = -1;
    private static string _netPovKey = "bryvpst1.key";
    private static float _netPovKeyTime;
    private static float _lastServerPovKeyTime;
    private static bool _netPovKeyLoop = true;
    private static float _netInputSendAccumulator;
    private static bool _lastSentPrimaryHeld;
    private static bool _lastSentSecondaryHeld;
    private static byte _lastSentForceBin;

    private static string? _netBannerMessage;
    private static float _netBannerTimer;

    private static readonly List<NetWorldEvent> _pendingNetEvents = [];
    private static readonly List<WorldSimulation.Projectile> _netProjectiles = [];
    private static readonly List<WorldSimulation.VisualEffect> _netVisualEffects = [];
    private static readonly List<SolidThingCollider> _netSolidColliders = [];
    private static readonly List<HudRenderer.ScoreboardRow> _scoreboardRows = [];
    private static readonly Dictionary<int, RemotePlayerVisual> _remotePlayers = new();
    private static readonly HashSet<int> _seenRemoteThingIds = [];
    private static readonly Dictionary<int, (int LocalChannel, int OwnerThingId)> _netLoopSounds = new();

    private static bool IsMultiplayerClient => _netClient != null;

    private static bool IsCurrentPlayerDead() =>
        IsMultiplayerClient
            ? (_latestSnapshot != null && (_latestSnapshot.Local.Flags & LocalPlayerSnapFlags.IsDead) != 0)
            : (_worldSim?.Inventory.IsDead == true);

    private static bool IsCurrentPlayerFrozen() =>
        IsMultiplayerClient
            ? (_latestSnapshot != null && (_latestSnapshot.Local.Flags & LocalPlayerSnapFlags.IsFrozen) != 0)
            : (_worldSim?.IsPlayerFrozen == true);

    private static int ResolveActiveLocalThingId() =>
        IsMultiplayerClient ? _netLocalThingId : (_worldSim?.GetLocalPlayerThing() ?? -1);

    private static void AdvanceActionSeq()
    {
        unchecked
        {
            _actionSeq++;
        }
    }

    private static bool TryConnectMultiplayerHandshake()
    {
        if (string.IsNullOrWhiteSpace(_connectHost))
            return true;

        Console.WriteLine($"Connecting to dedicated server {_connectHost}:{_connectPort} as \"{_playerName}\" (Team {_playerTeam})...");
        var client = new MultiplayerClient();
        try
        {
            _netWelcome = client.ConnectAsync(
                    _connectHost,
                    _connectPort,
                    _playerName,
                    _playerTeam,
                    TimeSpan.FromSeconds(6),
                    initialLoadout: _playerLoadout)
                .GetAwaiter()
                .GetResult();
            _netClient = client;
            _netLocalThingId = _netWelcome.PlayerThingId;
            _startupLevel = _netWelcome.LevelFile;
            _appliedTeleportSeq = 1;
            _appliedVelocitySeq = 0;

            Console.WriteLine($"Connected! ClientId={_netWelcome.ClientId}, PlayerThing={_netWelcome.PlayerThingId}, Level={_netWelcome.LevelFile} ({_netWelcome.EpisodeGob})");
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to connect: {ex.Message}");
            Console.ResetColor();
            client.Dispose();
            return false;
        }
    }

    private static void MountExtraEpisodeArchives(VirtualFileSystem vfs, string jk1Path)
    {
        string? epGob = !string.IsNullOrWhiteSpace(_episodeGobOverride)
            ? _episodeGobOverride
            : _netWelcome?.EpisodeGob;

        if (string.IsNullOrWhiteSpace(epGob))
            return;

        string epPath = Path.IsPathRooted(epGob)
            ? epGob
            : Path.Combine(_assetsDir, "Episode", epGob);

        if (File.Exists(epPath) &&
            (!File.Exists(jk1Path) || !string.Equals(Path.GetFullPath(epPath), Path.GetFullPath(jk1Path), StringComparison.OrdinalIgnoreCase)))
        {
            vfs.Mount(GobArchive.FromFile(epPath));
        }
    }

    private static void SetupMultiplayerLevelState(string normalizedFile)
    {
        if (_level == null || _netWelcome == null)
            return;

        foreach (var t in _level.Things)
        {
            if (t.IsPlayerSpawn)
            {
                t.ThingFlags |= (uint)(SithThingFlags.Disabled | SithThingFlags.Invisible);
                t.IsNonSolid = true;
            }
        }

        EnsureLevelThingSlot(_netWelcome.PlayerThingId, _netWelcome.SpawnPosition, _netWelcome.SpawnYaw, _netWelcome.SpawnSector, visible: true);

        _netLocalThingId = _netWelcome.PlayerThingId;
        string initialMpModel = _modelCatalog.Count > 0
            ? _modelCatalog[Math.Clamp(_playerLoadout.ModelIndex, 0, _modelCatalog.Count - 1)].Model3D
            : "ky.3do";
        if (_netLocalThingId >= 0 && _netLocalThingId < _level.Things.Count)
            _level.Things[_netLocalThingId].Model3D = initialMpModel;
        _player = new PlayerPhysics(_netWelcome.SpawnPosition, _netWelcome.SpawnSector);
        _player.ConfigureFromModel(GetOrLoadModel(initialMpModel) ?? GetOrLoadModel("ky.3do"));
        _player.ModelResolver = name => string.IsNullOrEmpty(name) ? null : GetOrLoadModel(name);

        _camera.Yaw = _netWelcome.SpawnYaw;
        _camera.Pitch = 0f;
        _camera.Position = _player.EyePosition;

        _worldSim = null;
        _latestSnapshot = null;
        _remotePlayers.Clear();
        StopAllNetLoopSounds();
        _netProjectiles.Clear();
        _netVisualEffects.Clear();
        _pendingNetEvents.Clear();
        _appliedTeleportSeq = 1;
        _appliedVelocitySeq = 0;
        _lastNetWeaponBin = -1;
        _localAirborneSeconds = 0f;
        _localDescendingSeconds = 0f;
        _pendingPrimaryFireTap = false;
        _pendingSecondaryFireTap = false;
        _netPovKey = "bryvpst1.key";
        _netPovKeyTime = 0f;
        _lastServerPovKeyTime = 0f;
        _netPovKeyLoop = true;
        _netInputSendAccumulator = 0f;
        _lastSentPrimaryHeld = false;
        _lastSentSecondaryHeld = false;
        _lastSentForceBin = 0;

        if (_window != null)
            _window.Title = $"OpenSith MP - {_playerName} @ {_connectHost}:{_connectPort} ({normalizedFile})";
    }

    private static void EnsureLevelThingSlot(int thingIndex, Vector3 pos, float yaw, int sector, bool visible)
    {
        if (_level == null || thingIndex < 0)
            return;

        while (_level.Things.Count <= thingIndex)
        {
            int idx = _level.Things.Count;
            _level.Things.Add(new JklThing
            {
                ThingIndex = idx,
                TemplateName = "walkplayer",
                SectorIndex = sector,
                Position = pos,
                PitchYawRoll = new Vector3(0f, yaw, 0f),
                Model3D = "ky.3do",
                ThingFlags = visible
                    ? (uint)(SithThingFlags.EmitLight | SithThingFlags.Captured | SithThingFlags.Splashes)
                    : (uint)(SithThingFlags.Disabled | SithThingFlags.Invisible)
            });
        }

        var thing = _level.Things[thingIndex];
        if (visible)
        {
            thing.ThingFlags &= ~(uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed | SithThingFlags.Invisible);
            thing.IsNonSolid = false;
            if (string.IsNullOrEmpty(thing.Model3D))
                thing.Model3D = "ky.3do";
            if (string.IsNullOrEmpty(thing.TemplateName))
                thing.TemplateName = "walkplayer";
        }
    }

    private static void UpdateMultiplayerClient(float dt)
    {
        if (_netClient == null || _level == null || _player == null)
            return;

        bool menuOpen = _menuRenderer != null && _menuRenderer.CurrentScreen != MenuScreen.None;
        bool gotSnapshot = _netClient.PollSnapshots(_pendingNetEvents, dt, out Vector3 accumulatedPlatformDelta);
        _latestSnapshot = _netClient.LatestSnapshot;

        if (_latestSnapshot != null)
        {
            _netPovKeyTime += dt;
            if (!_netPovKeyLoop && _thingRenderer != null)
            {
                float povDur = _thingRenderer.GetKeyframeDuration(_netPovKey);
                if (povDur > 0.002f)
                    _netPovKeyTime = MathF.Min(_netPovKeyTime, MathF.Max(0f, povDur - 0.001f));
            }
        }

        for (int i = _netVisualEffects.Count - 1; i >= 0; i--)
        {
            _netVisualEffects[i].Age += dt;
            if (_netVisualEffects[i].Age >= _netVisualEffects[i].Duration)
                _netVisualEffects.RemoveAt(i);
        }

        if (_netBannerTimer > 0f)
        {
            _netBannerTimer -= dt;
            if (_netBannerTimer <= 0f)
            {
                _netBannerTimer = 0f;
                _netBannerMessage = null;
            }
        }

        foreach (var proj in _netProjectiles)
            proj.Position += proj.Velocity * dt;

        ProcessNetworkEvents();

        if (gotSnapshot && _latestSnapshot != null)
            ApplyServerSnapshot(_latestSnapshot, accumulatedPlatformDelta);

        // Interpolate remote player puppets smoothly toward server state
        float lerpAlpha = Math.Clamp(dt * 18f, 0f, 1f);
        foreach (var (thingId, rp) in _remotePlayers)
        {
            Vector3 oldPos = rp.Position;
            rp.Position = Vector3.Lerp(rp.Position, rp.TargetPosition, lerpAlpha);
            Vector2 deltaXY = new(rp.Position.X - oldPos.X, rp.Position.Y - oldPos.Y);
            float dist = deltaXY.Length();
            Vector2 instVel = dt > 1e-4f ? deltaXY / dt : Vector2.Zero;
            rp.SmoothedHorizVel = Vector2.Lerp(rp.SmoothedHorizVel, instVel, Math.Clamp(dt * 12f, 0f, 1f));
            float instVelZ = dt > 1e-4f ? (rp.Position.Z - oldPos.Z) / dt : 0f;
            rp.SmoothedVelZ = float.Lerp(rp.SmoothedVelZ, instVelZ, Math.Clamp(dt * 12f, 0f, 1f));
            float smoothedSpeed = rp.SmoothedHorizVel.Length();
            if (!rp.IsDead && rp.IsAirborneFlag && !rp.IsUnderwaterFlag)
            {
                rp.AirborneSeconds += dt;
                rp.DescendingSeconds = rp.SmoothedVelZ < -0.05f ? (rp.DescendingSeconds + dt) : 0f;
            }
            else
            {
                rp.AirborneSeconds = 0f;
                rp.DescendingSeconds = 0f;
            }
            bool movingNow = !rp.IsDead && (rp.IsMovingFlag || smoothedSpeed > 0.12f);
            rp.MoveGraceSeconds = movingNow ? 0.12f : MathF.Max(0f, rp.MoveGraceSeconds - dt);

            if (thingId >= 0 && thingId < _level.Things.Count)
            {
                var thing = _level.Things[thingId];
                float rpPelvis = !string.IsNullOrEmpty(thing.Model3D) &&
                    GetOrLoadModel(thing.Model3D) is { InsertOffset.Z: > 0f } rpModel
                    ? rpModel.InsertOffset.Z
                    : _player.PelvisOffset;
                thing.Position = rp.Position + new Vector3(0f, 0f, rpPelvis);
                thing.PitchYawRoll = new Vector3(0f, rp.Yaw, 0f);

                bool remoteAirborne = rp.AirborneSeconds >= 0.14f || (rp.AirborneSeconds > 0f && rp.SmoothedVelZ > 0.25f);
                var animSel = ThingMeshRenderer.ResolvePlayerAnimationState(
                    rp.IsDead,
                    rp.IsUnderwaterFlag,
                    remoteAirborne,
                    movingNow || rp.MoveGraceSeconds > 0f,
                    rp.IsFiringFlag,
                    rp.WeaponBin,
                    rp.SmoothedHorizVel,
                    rp.Yaw,
                    rp.SmoothedVelZ,
                    rp.DescendingSeconds);
                _thingRenderer?.SetThingAnimation(thingId, animSel.BaseAction, animSel.Mode, animSel.BaseLoop);
                if (rp.IsDead)
                    _thingRenderer?.ClearThingOverlayAnimation(thingId);
                else
                    _thingRenderer?.SetThingOverlayAnimation(thingId, animSel.OverlayActionOrKey, animSel.Mode, animSel.OverlayLoop);
                if (!rp.IsDead)
                    _thingRenderer?.SetThingDistanceMoved(thingId, dist);
            }
        }

        if (_netLoopSounds.Count > 0 && _audio != null)
        {
            foreach (var loop in _netLoopSounds.Values)
            {
                if (loop.OwnerThingId >= 0 && loop.OwnerThingId != _netLocalThingId && loop.OwnerThingId < _level.Things.Count)
                    _audio.SetSoundPosition(loop.LocalChannel, _level.Things[loop.OwnerThingId].Position);
            }
        }

        bool isDead = IsCurrentPlayerDead();
        bool isFrozen = IsCurrentPlayerFrozen();
        if (!menuOpen && !isDead && !isFrozen)
        {
            const float keyTurnSpeed = 120.0f;
            if (_keys[(int)Key.Left]) _camera.Yaw += keyTurnSpeed * dt;
            if (_keys[(int)Key.Right]) _camera.Yaw -= keyTurnSpeed * dt;
            if (_keys[(int)Key.Up]) _camera.Pitch = Math.Clamp(_camera.Pitch + keyTurnSpeed * dt, -89.0f, 89.0f);
            if (_keys[(int)Key.Down]) _camera.Pitch = Math.Clamp(_camera.Pitch - keyTurnSpeed * dt, -89.0f, 89.0f);
        }

        if (_saberIgniteTimer > 0f)
            _saberIgniteTimer = MathF.Max(0f, _saberIgniteTimer - dt);

        Vector2 oldPlayerXY = new(_player.Position.X, _player.Position.Y);
        Vector3 localThrust = Vector3.Zero;

        if (menuOpen || isDead || isFrozen)
        {
            _playerMoving = false;
        }
        else
        {
            Vector3 wishDir = BuildWishDirection(out localThrust);
            _playerMoving = wishDir.LengthSquared() > 0.01f;
            if (_playerMoving)
                _playerMoveBobTime += dt;

            if (_flyMode)
            {
                float speed = 10.0f * (_keys[(int)Key.ShiftLeft] ? 2.5f : 1.0f) * dt;
                Vector3 flyMove = BuildFlyMoveVector();
                if (flyMove.LengthSquared() > 0)
                    _player.Position += Vector3.Normalize(flyMove) * speed;
            }
            else
            {
                BuildMultiplayerSolidColliders();
                bool wishJump = _keys[(int)Key.Space];
                float baseSpeed = _keys[(int)Key.ShiftLeft] ? 4.0f : 2.0f;
                float extraSpeed = _latestSnapshot?.Local.ExtraSpeed ?? 0f;
                _player.MoveSpeed = baseSpeed * MathF.Max(0.25f, 1.0f + extraSpeed);
                _player.Update(
                    dt,
                    wishDir,
                    wishJump,
                    _level,
                    solidThings: _netSolidColliders,
                    modelResolver: name => string.IsNullOrEmpty(name) ? null : GetOrLoadModel(name));
            }
        }

        int weaponBin = _latestSnapshot?.Local.CurWeaponBin ?? PlayerInventory.BinBryar;
        bool primaryHeld = !menuOpen && !isDead && (_mouseLeftDown || _fireKeyPrimary || _pendingPrimaryFireTap);
        bool secondaryHeld = !menuOpen && !isDead && (_mouseRightDown || _fireKeySecondary || _pendingSecondaryFireTap);
        if (menuOpen || isDead)
        {
            _pendingPrimaryFireTap = false;
            _pendingSecondaryFireTap = false;
        }
        bool localAirborneAnim = UpdateLocalAirborneTiming(dt, isDead);

        // Update local 3rd-person Kyle animation
        if (_netLocalThingId >= 0 && _netLocalThingId < _level.Things.Count)
        {
            var localThing = _level.Things[_netLocalThingId];
            localThing.Position = _player.PelvisPosition;
            localThing.PitchYawRoll = new Vector3(0f, _camera.Yaw, 0f);
            localThing.SectorIndex = _player.CurrentSectorIndex;

            Vector2 localDeltaXY = new(_player.Position.X - oldPlayerXY.X, _player.Position.Y - oldPlayerXY.Y);
            float dist = isDead ? 0f : localDeltaXY.Length();
            Vector2 localVelXY = dt > 1e-4f ? localDeltaXY / dt : Vector2.Zero;
            var animSel = ThingMeshRenderer.ResolvePlayerAnimationState(
                isDead,
                _player.IsUnderwater,
                localAirborneAnim,
                _playerMoving,
                primaryHeld || secondaryHeld,
                weaponBin,
                localVelXY,
                _camera.Yaw,
                _player.Velocity.Z,
                _localDescendingSeconds);
            _thingRenderer?.SetThingAnimation(_netLocalThingId, animSel.BaseAction, animSel.Mode, animSel.BaseLoop);
            if (isDead)
                _thingRenderer?.ClearThingOverlayAnimation(_netLocalThingId);
            else
                _thingRenderer?.SetThingOverlayAnimation(_netLocalThingId, animSel.OverlayActionOrKey, animSel.Mode, animSel.OverlayLoop);
            _thingRenderer?.SetThingDistanceMoved(_netLocalThingId, dist);
        }

        _thingRenderer?.Update(dt);
        UpdateCameraTransform();
        _audio?.Update(_camera.Position, _camera.Forward, _camera.Up);

        ClientInputFlags flags = ClientInputFlags.None;
        if (_player.IsGrounded) flags |= ClientInputFlags.IsGrounded;
        if (_player.IsUnderwater) flags |= ClientInputFlags.IsUnderwater;
        if (primaryHeld) flags |= ClientInputFlags.PrimaryFireHeld;
        if (secondaryHeld) flags |= ClientInputFlags.SecondaryFireHeld;
        if (_pendingUsePressed) flags |= ClientInputFlags.UsePressed;
        if (_pendingRespawnPressed) flags |= ClientInputFlags.RespawnPressed;

        byte activeForceBin = (!menuOpen && !isDead) ? _heldForceBin : (byte)0;
        bool hasDiscreteAction =
            _pendingUsePressed ||
            _pendingRespawnPressed ||
            _pendingWeaponBin != 0 ||
            _pendingItemBin != 0 ||
            _pendingPrimaryFireTap ||
            _pendingSecondaryFireTap;
        bool stateChanged =
            primaryHeld != _lastSentPrimaryHeld ||
            secondaryHeld != _lastSentSecondaryHeld ||
            activeForceBin != _lastSentForceBin;

        _netInputSendAccumulator += dt;
        const float MinInputSendInterval = 1.0f / 60.0f;
        if (hasDiscreteAction || stateChanged || _netInputSendAccumulator >= MinInputSendInterval)
        {
            _netInputSendAccumulator = 0f;
            _lastSentPrimaryHeld = primaryHeld;
            _lastSentSecondaryHeld = secondaryHeld;
            _lastSentForceBin = activeForceBin;

            _netClient.SendInput(new ClientInputPacket
            {
                Flags = flags,
                Position = _player.Position,
                Sector = _player.CurrentSectorIndex,
                AimDirection = _camera.Forward,
                Pitch = _camera.Pitch,
                Yaw = _camera.Yaw,
                Thrust = localThrust,
                ActionSeq = _actionSeq,
                RequestedWeaponBin = _pendingWeaponBin,
                RequestedItemBin = _pendingItemBin,
                ActiveForceBin = activeForceBin
            });

            _pendingUsePressed = false;
            _pendingRespawnPressed = false;
            _pendingWeaponBin = 0;
            _pendingItemBin = 0;
            _pendingPrimaryFireTap = false;
            _pendingSecondaryFireTap = false;
        }
    }

    private static void ProcessNetworkEvents()
    {
        if (_pendingNetEvents.Count == 0)
            return;

        foreach (var evt in _pendingNetEvents)
        {
            switch (evt.Kind)
            {
                case NetEventKind.ExplosionVfx:
                    _netVisualEffects.Add(new WorldSimulation.VisualEffect
                    {
                        Kind = WorldSimulation.VisualEffectKind.Billboard,
                        Position = evt.Position,
                        SpriteName = evt.Text0,
                        MaterialName = string.IsNullOrWhiteSpace(evt.Text1) ? "detx.mat" : evt.Text1,
                        Width = MathF.Max(0.05f, evt.FloatParam0),
                        Height = MathF.Max(0.05f, evt.FloatParam0),
                        Duration = MathF.Max(0.1f, evt.FloatParam1),
                        MaxLight = evt.IntParam0 != 0 ? 0.5f : 0f,
                        ColorTint = Vector3.One,
                        IsAdditive = true
                    });
                    break;

                case NetEventKind.ParticleRing:
                {
                    (Vector3 tint, float pitchSpread, int pCount) = NetCodec.UnpackRingMeta(evt.IntParam0);
                    bool isSmoke = WorldSimulation.IsSmokeParticleEffect(evt.Text0, evt.Text1);
                    float expandRadius = MathF.Max(0.02f, evt.FloatParam0);
                    float particleSize = isSmoke
                        ? Math.Clamp(expandRadius * 0.25f, 0.009f, 0.025f)
                        : Math.Clamp(expandRadius * 0.08f, 0.009f, 0.035f);
                    _netVisualEffects.Add(new WorldSimulation.VisualEffect
                    {
                        Kind = WorldSimulation.VisualEffectKind.ParticleRing,
                        Position = evt.Position,
                        SpriteName = evt.Text0,
                        MaterialName = string.IsNullOrWhiteSpace(evt.Text1) ? "dusty.mat" : evt.Text1,
                        Width = particleSize,
                        Height = particleSize,
                        ExpandRadius = expandRadius,
                        Duration = MathF.Max(0.12f, evt.FloatParam1),
                        MaxLight = isSmoke ? 0f : 0.5f,
                        ColorTint = tint,
                        ParticleCount = pCount,
                        PitchSpreadDeg = pitchSpread,
                        IsAdditive = !isSmoke
                    });
                    break;
                }

                case NetEventKind.BeamTrail:
                {
                    (Vector3 tint, bool isLightning) = NetCodec.UnpackBeamMeta(evt.IntParam0);
                    _netVisualEffects.Add(new WorldSimulation.VisualEffect
                    {
                        Kind = WorldSimulation.VisualEffectKind.BeamTrail,
                        Position = evt.Position,
                        EndPosition = evt.EndPosition,
                        SpriteName = evt.Text0,
                        MaterialName = string.IsNullOrWhiteSpace(evt.Text1) ? (isLightning ? "00ramp4.mat" : "dusty.mat") : evt.Text1,
                        Width = isLightning ? 0.025f : 0.14f,
                        Height = isLightning ? 0.025f : 0.14f,
                        TrailSegmentSize = MathF.Max(0.04f, evt.FloatParam0),
                        Duration = MathF.Max(0.15f, evt.FloatParam1),
                        JitterRadius = isLightning ? 0.05f : 0f,
                        MaxLight = 0.6f,
                        ColorTint = tint,
                        IsAdditive = true
                    });
                    break;
                }

                case NetEventKind.SoundPos:
                    if (!string.IsNullOrWhiteSpace(evt.Text0))
                    {
                        float vol = Math.Clamp(evt.FloatParam0, 0.1f, 1.5f);
                        if (evt.FloatParam1 > 0.5f)
                            _audio?.PlaySoundPos(evt.Text0, evt.Position, vol, 0.5f, 8.0f);
                        else
                            _audio?.PlaySoundLocal(evt.Text0, vol);
                    }
                    break;

                case NetEventKind.LoopSoundStart:
                    if (!string.IsNullOrWhiteSpace(evt.Text0))
                    {
                        if (_netLoopSounds.Remove(evt.IntParam0, out var existingLoop))
                            _audio?.StopSound(existingLoop.LocalChannel, 0.02f);

                        int ownerThingId = (int)MathF.Round(evt.FloatParam1);
                        float vol = Math.Clamp(evt.FloatParam0, 0.1f, 1.5f);
                        int localCh = _audio == null
                            ? 0
                            : (ownerThingId == _netLocalThingId
                                ? _audio.PlaySoundLocal(evt.Text0, vol, looping: true)
                                : _audio.PlaySoundPos(evt.Text0, evt.Position, vol, 0.5f, 8.0f, looping: true));
                        if (localCh > 0)
                            _netLoopSounds[evt.IntParam0] = (localCh, ownerThingId);
                    }
                    break;

                case NetEventKind.LoopSoundStop:
                    if (_netLoopSounds.Remove(evt.IntParam0, out var stoppedLoop))
                        _audio?.StopSound(stoppedLoop.LocalChannel, MathF.Max(0f, evt.FloatParam0));
                    break;

                case NetEventKind.KillFeed:
                    _netBannerMessage = evt.IntParam0 != 0 || string.IsNullOrWhiteSpace(evt.Text0)
                        ? $"{evt.Text1} died"
                        : $"{evt.Text0} eliminated {evt.Text1}";
                    _netBannerTimer = 4.0f;
                    Console.WriteLine($"[KILL] {_netBannerMessage}");
                    break;

                case NetEventKind.StatusMessage:
                    if (!string.IsNullOrWhiteSpace(evt.Text0))
                    {
                        _netBannerMessage = evt.Text0;
                        _netBannerTimer = 3.5f;
                        Console.WriteLine($"[MP] {_netBannerMessage}");
                    }
                    break;

                case NetEventKind.SurfaceScroll:
                    _meshRenderer?.SetSurfaceScroll(
                        evt.IntParam0,
                        LevelMeshRenderer.ComputeSlideWallUvVelocity(
                            new Vector2(evt.Position.X, evt.Position.Y),
                            evt.Position.Z));
                    break;

                case NetEventKind.MaterialAnim:
                    _meshRenderer?.SetMaterialAnim(evt.Text0, evt.FloatParam0);
                    break;

                case NetEventKind.ThingPlayKey:
                    if (!string.IsNullOrWhiteSpace(evt.Text0) && evt.IntParam0 >= 0)
                    {
                        if (evt.FloatParam0 > 0.5f)
                            _thingRenderer?.SetThingOverlayAnimation(evt.IntParam0, evt.Text0, mode: 0, loop: true);
                        else
                            _thingRenderer?.PlayThingOneShotOverlay(evt.IntParam0, evt.Text0, mode: 0, autoClear: true);
                    }
                    break;
            }
        }

        _pendingNetEvents.Clear();
    }

    private static void ApplyServerSnapshot(ServerSnapshotPacket snap, Vector3 accumulatedPlatformDelta)
    {
        if (_level == null || _player == null)
            return;

        if ((snap.Local.Flags & LocalPlayerSnapFlags.HasTeleport) != 0 &&
            snap.Local.TeleportSeq > _appliedTeleportSeq)
        {
            _appliedTeleportSeq = snap.Local.TeleportSeq;
            _player.Position = snap.Local.TeleportPosition;
            _player.Velocity = Vector3.Zero;
            _player.CurrentSectorIndex = snap.Local.TeleportSector;
            _camera.Yaw = snap.Local.TeleportYaw;
            _localAirborneSeconds = 0f;
            _localDescendingSeconds = 0f;
            if (_netLocalThingId >= 0)
                _thingRenderer?.ClearThingOverlayAnimation(_netLocalThingId);
        }

        if ((snap.Local.Flags & LocalPlayerSnapFlags.HasVelocityOverride) != 0 &&
            snap.Local.VelocitySeq > _appliedVelocitySeq)
        {
            _appliedVelocitySeq = snap.Local.VelocitySeq;
            _player.ApplyExternalVelocity(snap.Local.VelocityOverride);
        }

        if (!_flyMode &&
            (snap.Local.Flags & LocalPlayerSnapFlags.IsDead) == 0 &&
            accumulatedPlatformDelta.LengthSquared() > 1e-8f)
        {
            _player.Position += accumulatedPlatformDelta;
        }

        if (_lastNetWeaponBin != snap.Local.CurWeaponBin)
        {
            if (snap.Local.CurWeaponBin == PlayerInventory.BinLightsaber)
                _saberIgniteTimer = SaberIgniteDuration;
            _lastNetWeaponBin = snap.Local.CurWeaponBin;
        }

        string srvPovKey = string.IsNullOrWhiteSpace(snap.Local.PovKey) ? "bryvpst1.key" : snap.Local.PovKey;
        if (!srvPovKey.Equals(_netPovKey, StringComparison.OrdinalIgnoreCase) ||
            snap.Local.PovKeyTime + 0.02f < _lastServerPovKeyTime)
        {
            _netPovKey = srvPovKey;
            _netPovKeyTime = snap.Local.PovKeyTime;
        }
        else if (MathF.Abs(snap.Local.PovKeyTime - _netPovKeyTime) > 0.12f)
        {
            _netPovKeyTime = snap.Local.PovKeyTime;
        }
        else
        {
            _netPovKeyTime = float.Lerp(_netPovKeyTime, snap.Local.PovKeyTime, 0.35f);
        }
        _lastServerPovKeyTime = snap.Local.PovKeyTime;

        // Apply replicated dynamic world objects (doors, elevators, pickups)
        foreach (var dt in snap.DynamicThings)
        {
            if (dt.ThingIndex < 0 || dt.ThingIndex >= _level.Things.Count || dt.ThingIndex == _netLocalThingId)
                continue;

            var thing = _level.Things[dt.ThingIndex];
            thing.Position = dt.Position;
            thing.PitchYawRoll = dt.PitchYawRoll;
            thing.ThingFlags = dt.ThingFlags;
            thing.SectorIndex = dt.SectorIndex;
            thing.IsNonSolid = (dt.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0;
        }

        // Sync active players
        _seenRemoteThingIds.Clear();
        foreach (var ps in snap.Players)
        {
            _seenRemoteThingIds.Add(ps.ThingIndex);
            EnsureLevelThingSlot(ps.ThingIndex, ps.Position, ps.Yaw, 0, visible: true);

            if (_modelCatalog.Count > 0 && ps.ThingIndex >= 0 && ps.ThingIndex < _level.Things.Count)
            {
                string model3d = _modelCatalog[Math.Clamp((int)ps.ModelIndex, 0, _modelCatalog.Count - 1)].Model3D;
                _level.Things[ps.ThingIndex].Model3D = model3d;
            }

            if (ps.ThingIndex == _netLocalThingId)
            {
                _netPovKeyLoop = (ps.AnimFlags & RemotePlayerAnimFlags.PovKeyLoop) != 0;
                continue;
            }

            if (!_remotePlayers.TryGetValue(ps.ThingIndex, out var rp))
            {
                rp = new RemotePlayerVisual { Position = ps.Position };
                _remotePlayers[ps.ThingIndex] = rp;
            }

            if (!rp.Initialized || Vector3.DistanceSquared(rp.Position, ps.Position) > 4.0f)
            {
                bool wasInitialized = rp.Initialized;
                rp.Position = ps.Position;
                rp.SmoothedHorizVel = Vector2.Zero;
                rp.SmoothedVelZ = 0f;
                rp.AirborneSeconds = 0f;
                rp.DescendingSeconds = 0f;
                rp.MoveGraceSeconds = 0f;
                if (wasInitialized)
                    _thingRenderer?.ClearThingOverlayAnimation(ps.ThingIndex);
            }
            rp.TargetPosition = ps.Position;
            rp.Pitch = ps.Pitch;
            rp.Yaw = ps.Yaw;
            rp.IsDead = (ps.AnimFlags & RemotePlayerAnimFlags.IsDead) != 0;
            rp.IsMovingFlag = (ps.AnimFlags & RemotePlayerAnimFlags.IsMoving) != 0;
            rp.IsFiringFlag = (ps.AnimFlags & RemotePlayerAnimFlags.IsFiring) != 0;
            rp.IsUnderwaterFlag = (ps.AnimFlags & RemotePlayerAnimFlags.IsUnderwater) != 0;
            rp.IsAirborneFlag = (ps.AnimFlags & RemotePlayerAnimFlags.IsAirborne) != 0;
            rp.SaberActive = (ps.AnimFlags & RemotePlayerAnimFlags.SaberActive) != 0;
            rp.WeaponBin = ps.WeaponBin;
            rp.ModelIndex = ps.ModelIndex;
            rp.SaberColorIndex = ps.SaberColorIndex;
            rp.Initialized = true;
        }

        // Hide remote players who left
        foreach (int prevThingId in new List<int>(_remotePlayers.Keys))
        {
            if (!_seenRemoteThingIds.Contains(prevThingId))
            {
                _remotePlayers.Remove(prevThingId);
                StopNetLoopSoundsForOwner(prevThingId);
                _thingRenderer?.ClearThingOverlayAnimation(prevThingId);
                if (prevThingId >= 0 && prevThingId < _level.Things.Count)
                {
                    _level.Things[prevThingId].ThingFlags |= (uint)(SithThingFlags.Disabled | SithThingFlags.Invisible);
                    _level.Things[prevThingId].IsNonSolid = true;
                }
            }
        }

        // Replace active projectiles from authoritative server snapshot
        _netProjectiles.Clear();
        foreach (var pr in snap.Projectiles)
        {
            float r = (pr.BoltColorRgba & 0xFF) / 255f;
            float g = ((pr.BoltColorRgba >> 8) & 0xFF) / 255f;
            float b = ((pr.BoltColorRgba >> 16) & 0xFF) / 255f;
            _netProjectiles.Add(new WorldSimulation.Projectile
            {
                Position = pr.Position,
                Velocity = pr.Velocity,
                Direction = pr.Velocity.LengthSquared() > 1e-6f ? Vector3.Normalize(pr.Velocity) : Vector3.UnitY,
                BoltColor = new Vector3(r, g, b),
                ModelName = pr.ModelName
            });
        }
    }

    private static void BuildMultiplayerSolidColliders()
    {
        _netSolidColliders.Clear();
        if (_level == null)
            return;

        foreach (var (thingId, rp) in _remotePlayers)
        {
            if (!rp.IsDead)
                _netSolidColliders.Add(new SolidThingCollider(rp.Position, 0.065f, 0.18f));
        }
    }

    private static void RenderMultiplayerWorldAndHud(
        int renderWidth,
        int renderHeight,
        float aspect,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        if (_shader == null || _meshRenderer == null || _level == null)
            return;

        var lpSnap = _latestSnapshot?.Local;
        Vector3 serverTint = lpSnap != null && (lpSnap.Flags & LocalPlayerSnapFlags.HasScreenTint) != 0
            ? lpSnap.ScreenTint
            : Vector3.Zero;
        Vector3 localSectorTint = Vector3.Zero;
        if (_player != null && _player.CurrentSectorIndex >= 0 && _player.CurrentSectorIndex < _level.Sectors.Count)
        {
            Vector3 rawSecTint = _level.Sectors[_player.CurrentSectorIndex].Tint;
            localSectorTint = new Vector3(
                Math.Clamp(rawSecTint.X, 0f, 1f),
                Math.Clamp(rawSecTint.Y, 0f, 1f),
                Math.Clamp(rawSecTint.Z, 0f, 1f)) * 0.35f;
        }
        Vector3 tint = Vector3.Clamp(
            Vector3.Max(serverTint, localSectorTint),
            Vector3.Zero,
            new Vector3(0.75f, 0.75f, 0.75f));

        _shader.SetUniform("uModel", Matrix4x4.Identity);
        _shader.SetUniform("uView", view);
        _shader.SetUniform("uProjection", projection);
        _shader.SetUniform("uTint", tint);
        _shader.SetUniform("uAlpha", 1.0f);

        _meshRenderer.RenderOpaque(_shader);
        _thingRenderer?.RenderThings(_shader, _level, playerThingIndex: _thirdPerson ? -1 : _netLocalThingId);

        int activeWeaponBin = lpSnap?.CurWeaponBin ?? PlayerInventory.BinBryar;
        bool isDead = IsCurrentPlayerDead();
        bool isSaber = activeWeaponBin == PlayerInventory.BinLightsaber;
        bool saberActive = isSaber && !isDead;

        if (_thingRenderer != null)
        {
            if (_thirdPerson && !isDead && _netLocalThingId >= 0 && _netLocalThingId < _level.Things.Count)
            {
                _thingRenderer.RenderAttachedWeaponMesh(
                    _shader,
                    _level.Things[_netLocalThingId],
                    PlayerCustomizationCatalog.GetThirdPersonWeaponModel(activeWeaponBin));
            }

            foreach (var (thingId, rp) in _remotePlayers)
            {
                if (!rp.IsDead && thingId >= 0 && thingId < _level.Things.Count)
                {
                    _thingRenderer.RenderAttachedWeaponMesh(
                        _shader,
                        _level.Things[thingId],
                        PlayerCustomizationCatalog.GetThirdPersonWeaponModel(rp.WeaponBin));
                }
            }
        }
        SaberColorOption localSaberOpt = PlayerCustomizationCatalog.ResolveSaberOption(
            _saberCatalog,
            lpSnap?.SaberColorIndex ?? _playerLoadout.SaberColorIndex);
        Vector3 saberColor = localSaberOpt.FallbackRgb;

        // Local player 3rd-person saber blade
        if (_thirdPerson && saberActive && _saberRenderer != null &&
            _netLocalThingId >= 0 && _netLocalThingId < _level.Things.Count)
        {
            RenderPlayerThirdPersonSaber(
                _netLocalThingId,
                view,
                projection,
                saberColor,
                _saberIgniteTimer > 0f ? Math.Clamp(1.0f - (_saberIgniteTimer / SaberIgniteDuration), 0.05f, 1.0f) : 1.0f,
                localSaberOpt.SideMaterial,
                localSaberOpt.TipMaterial);
        }

        // Remote players' 3rd-person saber blades
        if (_saberRenderer != null)
        {
            foreach (var (thingId, rp) in _remotePlayers)
            {
                if (!rp.IsDead && rp.SaberActive && thingId >= 0 && thingId < _level.Things.Count)
                {
                    SaberColorOption rpSaber = PlayerCustomizationCatalog.ResolveSaberOption(_saberCatalog, rp.SaberColorIndex);
                    RenderPlayerThirdPersonSaber(
                        thingId,
                        view,
                        projection,
                        rpSaber.FallbackRgb,
                        1.0f,
                        rpSaber.SideMaterial,
                        rpSaber.TipMaterial);
                }
            }
        }

        _thingRenderer?.RenderProjectiles(_shader, _netProjectiles);

        _shader.Use();
        _shader.SetUniform("uModel", Matrix4x4.Identity);
        _shader.SetUniform("uView", view);
        _shader.SetUniform("uProjection", projection);
        _shader.SetUniform("uTint", tint);
        _meshRenderer.RenderTranslucent(_shader);

        _thingRenderer?.RenderVisualEffects(_shader, _camera, _netVisualEffects);

        // First-person POV viewmodel & saber blade
        if (!_thirdPerson && _thingRenderer != null && lpSnap != null && !isDead)
        {
            string weaponModel = string.IsNullOrWhiteSpace(lpSnap.PovModel) ? "bryv.3do" : lpSnap.PovModel;
            string weaponKey = string.IsNullOrWhiteSpace(_netPovKey) ? "bryvpst1.key" : _netPovKey;
            float animTime = _netPovKeyTime;
            if (!_netPovKeyLoop)
            {
                float povDur = _thingRenderer.GetKeyframeDuration(weaponKey);
                if (povDur > 0.002f)
                    animTime = MathF.Min(animTime, MathF.Max(0f, povDur - 0.001f));
            }
            float bobX = _playerMoving ? MathF.Cos(_playerMoveBobTime * 8.0f) * 0.0012f : 0f;
            float bobZ = _playerMoving ? MathF.Sin(_playerMoveBobTime * 16.0f) * 0.0012f : 0f;
            Vector3 localOffset = GetViewmodelBaseOffset(isSaber) + new Vector3(bobX, 0f, bobZ) + lpSnap.PovShakePos;

            _thingRenderer.RenderViewmodel(_shader, _camera, weaponModel, weaponKey, animTime, aspect, localOffset);

            if (saberActive && _saberRenderer != null &&
                _thingRenderer.TryGetViewmodelNodeTransform(weaponModel, weaponKey, animTime, nodeIndex: 5, out Matrix4x4 joint5Local))
            {
                float fovRad = MathF.PI / 180.0f * ThingMeshRenderer.ViewmodelVerticalFovDegrees;
                Matrix4x4 bladeProjection = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspect, 0.005f, 10.0f);
                Matrix4x4 viewmodelWorld = ThingMeshRenderer.BuildViewmodelWorldMatrix(_camera, localOffset);
                Matrix4x4 joint5World = joint5Local * viewmodelWorld;
                Vector3 bladeBaseWorldPos = Vector3.Transform(Vector3.Zero, joint5World);
                Vector3 bladeForwardDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, joint5World));
                float swingProgress = (lpSnap.Flags & LocalPlayerSnapFlags.SaberDamageActive) != 0
                    ? Math.Clamp(animTime / 0.45f, 0.0f, 1.0f)
                    : 0.0f;
                float bladeLengthScale = _saberIgniteTimer > 0f
                    ? Math.Clamp(1.0f - (_saberIgniteTimer / SaberIgniteDuration), 0.05f, 1.0f)
                    : 1.0f;

                _saberRenderer.RenderFirstPersonBlade(
                    view,
                    bladeProjection,
                    bladeBaseWorldPos,
                    bladeForwardDir,
                    saberColor,
                    swingProgress,
                    bladeLengthScale,
                    localSaberOpt.SideMaterial,
                    localSaberOpt.TipMaterial);
            }
        }

        if (_hudRenderer != null)
        {
            int displayAmmo = activeWeaponBin switch
            {
                PlayerInventory.BinThermDet => lpSnap?.ThermDetAmmo ?? 0,
                PlayerInventory.BinBowcaster or PlayerInventory.BinRepeater or PlayerInventory.BinConcRifle => lpSnap?.PowerAmmo ?? 0,
                PlayerInventory.BinRailDet => lpSnap?.RailAmmo ?? 0,
                PlayerInventory.BinSeqCharge => lpSnap?.SeqAmmo ?? 0,
                _ => lpSnap?.EnergyAmmo ?? 0
            };

            string? statusMsg = !string.IsNullOrWhiteSpace(lpSnap?.StatusMessage)
                ? lpSnap.StatusMessage
                : (_netBannerMessage ?? (isDead ? "PRESS R TO RESPAWN" : null));

            _hudRenderer.Render(
                renderWidth,
                renderHeight,
                lpSnap?.Health ?? 100f,
                lpSnap?.MaxHealth ?? 100f,
                lpSnap?.Shields ?? 100f,
                200.0f,
                displayAmmo,
                activeWeaponBin,
                isDead,
                statusMsg,
                lpSnap?.ForceMana ?? 0f,
                lpSnap?.MaxForceMana ?? 0f,
                lpSnap?.Battery ?? 100f,
                lpSnap != null && (lpSnap.Flags & LocalPlayerSnapFlags.FieldLightOn) != 0,
                BuildActiveForceHudLabel());

            if (_keys[(int)Key.Tab] && _latestSnapshot != null)
            {
                _scoreboardRows.Clear();
                foreach (var p in _latestSnapshot.Players)
                {
                    _scoreboardRows.Add(new HudRenderer.ScoreboardRow(
                        p.Name,
                        p.Team,
                        p.Kills,
                        p.Deaths,
                        p.Score,
                        p.ThingIndex == _netLocalThingId,
                        (p.AnimFlags & RemotePlayerAnimFlags.IsDead) != 0));
                }
                _scoreboardRows.Sort((a, b) => b.Score.CompareTo(a.Score));
                _hudRenderer.RenderScoreboard(
                    renderWidth,
                    renderHeight,
                    $"OPENSITH MP - {_currentLevelFile}",
                    _scoreboardRows);
            }
        }
    }

    private static void RenderPlayerThirdPersonSaber(
        int thingId,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 saberColor,
        float lengthScale,
        string sideMat = "sabergreen1.mat",
        string tipMat = "sabergreen0.mat")
    {
        if (_saberRenderer == null || _level == null)
            return;

        var thing = _level.Things[thingId];
        Matrix4x4 kyleWorld = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position).ToMatrix4x4();
        Vector3 handWorldPos;
        Vector3 bladeDir;
        if (_thingRenderer?.TryGetThingPrimaryWeaponNodeTransform(thingId, out Matrix4x4 rightHandLocal) == true)
        {
            Matrix4x4 rightHandWorld = rightHandLocal * kyleWorld;
            handWorldPos = Vector3.Transform(Vector3.Zero, rightHandWorld);
            bladeDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rightHandWorld));
        }
        else
        {
            handWorldPos = Vector3.Transform(new Vector3(0.0104f, 0.0557f, -0.0129f), kyleWorld);
            bladeDir = Vector3.Normalize(Vector3.TransformNormal(new Vector3(0.205f, 0.567f, 0.798f), kyleWorld));
        }

        _saberRenderer.RenderThirdPersonBlade(view, projection, handWorldPos, bladeDir, saberColor, lengthScale, sideMat, tipMat);
    }

    private static Vector3 BuildWishDirection(out Vector3 localThrust)
    {
        float fwdInput = (_keys[(int)Key.W] ? 1f : 0f) - (_keys[(int)Key.S] ? 1f : 0f);
        float rightInput = (_keys[(int)Key.D] ? 1f : 0f) - (_keys[(int)Key.A] ? 1f : 0f);
        float upInput = (_keys[(int)Key.Space] ? 1f : 0f) - (_keys[(int)Key.C] ? 1f : 0f);
        localThrust = new Vector3(rightInput, fwdInput, upInput);

        bool isUnderwater = _player != null && _player.IsUnderwater;
        Vector3 wishDir = Vector3.Zero;
        if (isUnderwater)
        {
            if (_keys[(int)Key.W]) wishDir += _camera.Forward;
            if (_keys[(int)Key.S]) wishDir -= _camera.Forward;
            if (_keys[(int)Key.D]) wishDir += _camera.Right;
            if (_keys[(int)Key.A]) wishDir -= _camera.Right;
            if (_keys[(int)Key.Space]) wishDir += Vector3.UnitZ;
            if (_keys[(int)Key.C]) wishDir -= Vector3.UnitZ;
        }
        else
        {
            Vector3 forwardFlat = Vector3.Normalize(new Vector3(_camera.Forward.X, _camera.Forward.Y, 0));
            Vector3 rightFlat = Vector3.Normalize(new Vector3(_camera.Right.X, _camera.Right.Y, 0));
            if (_keys[(int)Key.W]) wishDir += forwardFlat;
            if (_keys[(int)Key.S]) wishDir -= forwardFlat;
            if (_keys[(int)Key.D]) wishDir += rightFlat;
            if (_keys[(int)Key.A]) wishDir -= rightFlat;
        }

        return wishDir;
    }

    private static Vector3 BuildFlyMoveVector()
    {
        Vector3 flyMove = Vector3.Zero;
        if (_keys[(int)Key.W]) flyMove += _camera.Forward;
        if (_keys[(int)Key.S]) flyMove -= _camera.Forward;
        if (_keys[(int)Key.D]) flyMove += _camera.Right;
        if (_keys[(int)Key.A]) flyMove -= _camera.Right;
        if (_keys[(int)Key.Space]) flyMove += Vector3.UnitZ;
        if (_keys[(int)Key.C]) flyMove -= Vector3.UnitZ;
        return flyMove;
    }

    private static void UpdateCameraTransform()
    {
        if (_player == null)
            return;

        if (_thirdPerson)
        {
            Vector3 desiredCamPos = _player.EyePosition - _camera.Forward * 0.30f + Vector3.UnitZ * 0.04f;
            if (_worldSim != null && _worldSim.TryRaycastWorld(_player.EyePosition, desiredCamPos, out Vector3 camHit))
            {
                Vector3 pullDir = Vector3.Normalize(_player.EyePosition - desiredCamPos);
                float hitDist = Vector3.Distance(_player.EyePosition, camHit);
                _camera.Position = hitDist > 0.03f
                    ? camHit + pullDir * 0.03f
                    : _player.EyePosition;
            }
            else
            {
                _camera.Position = desiredCamPos;
            }
        }
        else
        {
            _camera.Position = _player.EyePosition;
        }
    }

    private static void StopNetLoopSoundsForOwner(int ownerThingId)
    {
        if (_netLoopSounds.Count == 0)
            return;

        List<int>? toRemove = null;
        foreach (var (serverCh, info) in _netLoopSounds)
        {
            if (info.OwnerThingId == ownerThingId)
            {
                toRemove ??= [];
                toRemove.Add(serverCh);
            }
        }

        if (toRemove != null)
        {
            foreach (int serverCh in toRemove)
            {
                if (_netLoopSounds.Remove(serverCh, out var info))
                    _audio?.StopSound(info.LocalChannel, 0.05f);
            }
        }
    }

    private static void StopAllNetLoopSounds()
    {
        if (_netLoopSounds.Count == 0)
            return;

        foreach (var info in _netLoopSounds.Values)
            _audio?.StopSound(info.LocalChannel, 0f);
        _netLoopSounds.Clear();
    }

    private static void DisposeMultiplayerClient()
    {
        StopAllNetLoopSounds();
        _netClient?.Dispose();
        _netClient = null;
    }
}
