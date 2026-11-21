using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenSith.Core.Animation;
using OpenSith.Core.Audio;
using OpenSith.Core.Cog;
using OpenSith.Core.Models;
using OpenSith.Core.World;

namespace OpenSith.Client;

public sealed partial class WorldSimulation
{
    private readonly Dictionary<int, int> _thingAttachFlags = new();
    private readonly Dictionary<int, float> _thingTimers = new();
    private readonly Dictionary<int, (float Interval, float Remaining)> _thingPulses = new();
    private readonly Dictionary<int, float> _thingMasses = new();
    private readonly Dictionary<int, int> _thingPhysicsFlags = new();
    private readonly Dictionary<int, Vector3> _thingRotVels = new();
    private readonly Dictionary<string, float> _soundLenCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _keyLenCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _cameraFocus = new();

    private int _currentCamera;
    private int _superFlags;
    private int _mapModeFlags;

    /// <summary>
    /// Optional explicit Light (1) / Dark (2) campaign alignment choice for <see cref="JkGetChoice"/>.
    /// When unset, falls back to alignment inventory bin 75 (&lt; 0 = Dark, &gt;= 0 = Light).
    /// </summary>
    public int? PlayerAlignmentChoice { get; set; }

    /// <summary>
    /// Invoked when <c>TeleportThing</c> moves the local player to a new position, PYR, and sector.
    /// </summary>
    public event Action<Vector3, Vector3, int>? OnPlayerTeleported;

    /// <summary>
    /// Invoked when <c>AssignWeapon</c> equips a weapon bin for the local player.
    /// </summary>
    public event Action<int>? OnPlayerWeaponAssigned;

    private void UpdateThingTimersAndPulses(float dt)
    {
        if (dt <= 0f)
            return;

        if (_thingTimers.Count > 0)
        {
            List<int>? expired = null;
            foreach (var key in new List<int>(_thingTimers.Keys))
            {
                if (_destroyedThings.Contains(key))
                {
                    _thingTimers.Remove(key);
                    continue;
                }

                float rem = _thingTimers[key] - dt;
                if (rem <= 0f)
                {
                    expired ??= [];
                    expired.Add(key);
                }
                else
                {
                    _thingTimers[key] = rem;
                }
            }

            if (expired != null)
            {
                foreach (int thingId in expired)
                {
                    _thingTimers.Remove(thingId);
                    DispatchThingCogMessage(thingId, "timer");
                }
            }
        }

        if (_thingPulses.Count > 0)
        {
            List<int>? fired = null;
            foreach (var key in new List<int>(_thingPulses.Keys))
            {
                if (_destroyedThings.Contains(key))
                {
                    _thingPulses.Remove(key);
                    continue;
                }

                var entry = _thingPulses[key];
                if (entry.Interval <= 0f)
                {
                    _thingPulses.Remove(key);
                    continue;
                }

                float rem = entry.Remaining - dt;
                if (rem <= 0f)
                {
                    float nextRem = rem + entry.Interval;
                    _thingPulses[key] = (entry.Interval, nextRem > 0f ? nextRem : entry.Interval);
                    fired ??= [];
                    fired.Add(key);
                }
                else
                {
                    _thingPulses[key] = (entry.Interval, rem);
                }
            }

            if (fired != null)
            {
                foreach (int thingId in fired)
                    DispatchThingCogMessage(thingId, "pulse");
            }
        }
    }

    private void UpdateThingRotVels(float dt)
    {
        if (dt <= 0f || _thingRotVels.Count == 0)
            return;

        foreach (var (thingId, rotVel) in _thingRotVels)
        {
            if (thingId < 0 || thingId >= _level.Things.Count || _destroyedThings.Contains(thingId))
                continue;

            var thing = _level.Things[thingId];
            Vector3 cur = thing.PitchYawRoll;
            thing.PitchYawRoll = new Vector3(
                WrapAngle180(cur.X + rotVel.X * dt),
                WrapAngle180(cur.Y + rotVel.Y * dt),
                WrapAngle180(cur.Z + rotVel.Z * dt));
        }
    }

    private void PropagateAttachedChildren(
        int parentId,
        Vector3 prevParentPos,
        Vector3 newParentPos,
        Vector3 stepDelta,
        SithMatrix34 stepRotMat,
        bool hasRotationDelta,
        HashSet<int>? visited = null)
    {
        if (_thingParents.Count == 0)
            return;

        if (stepDelta.LengthSquared() <= 1e-12f && !hasRotationDelta)
            return;

        visited ??= [parentId];
        bool parentIsPivotRotating = _thingMotions.TryGetValue(parentId, out var pm) && pm.IsPivotRotation;

        foreach (var (childId, assignedParent) in _thingParents)
        {
            if (assignedParent != parentId ||
                childId < 0 ||
                childId >= _level.Things.Count ||
                _destroyedThings.Contains(childId) ||
                !visited.Add(childId))
            {
                continue;
            }

            var child = _level.Things[childId];
            Vector3 oldChildPos = child.Position;
            Vector3 newChildPos = oldChildPos + stepDelta;
            SithMatrix34 childStepRot = SithMatrix34.Identity;
            bool childRotated = false;

            bool rotateWithParent = parentIsPivotRotating || (_thingAttachFlags.GetValueOrDefault(childId, 0) & 0x8) != 0;
            if (hasRotationDelta && rotateWithParent)
            {
                Vector3 rel = newChildPos - newParentPos;
                newChildPos = newParentPos + stepRotMat.TransformNormal(rel);
                var childCurRot = SithMatrix34.Build34(child.PitchYawRoll, Vector3.Zero);
                child.PitchYawRoll = SithMatrix34.ExtractPyr(SithMatrix34.Multiply(stepRotMat, childCurRot));
                childStepRot = stepRotMat;
                childRotated = true;
            }

            child.Position = newChildPos;
            PropagateAttachedChildren(childId, oldChildPos, newChildPos, newChildPos - oldChildPos, childStepRot, childRotated, visited);
        }
    }

    // -------------------------------------------------------------------------
    // Frame, Pivot Rotation & Attachment COG Verbs
    // -------------------------------------------------------------------------

    public void RotatePivot(int thingId, int frameIdx, float time)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;
        if (!_thingFrames.TryGetValue(thingId, out var frames) || frameIdx < 0 || frameIdx >= frames.Count)
            return;

        var thing = _level.Things[thingId];
        var pivotFrame = frames[frameIdx];

        if (!_thingMotions.TryGetValue(thingId, out var motion))
        {
            motion = new ThingMotionState();
            _thingMotions[thingId] = motion;
        }

        // In Sith Engine RotatePivot, the target frame position is the world pivot center
        // and the target frame PYR is the delta rotation vector (negated when time < 0).
        motion.StartPos = thing.Position;
        motion.TargetPos = thing.Position;
        motion.StartPyr = thing.PitchYawRoll;
        motion.PivotWorldPos = pivotFrame.Position;
        motion.StartOffsetFromPivot = thing.Position - pivotFrame.Position;
        motion.TargetDeltaPyr = time >= 0f ? pivotFrame.PitchYawRoll : -pivotFrame.PitchYawRoll;
        var deltaRot = SithMatrix34.Build34(motion.TargetDeltaPyr, Vector3.Zero);
        var startRot = SithMatrix34.Build34(thing.PitchYawRoll, Vector3.Zero);
        motion.TargetPyr = SithMatrix34.ExtractPyr(SithMatrix34.Multiply(deltaRot, startRot));
        motion.TargetFrame = frameIdx;
        motion.Elapsed = 0f;
        motion.Duration = MathF.Abs(time);
        motion.IsPivotRotation = true;
        motion.IsPaused = false;
        motion.IsMoving = true;
    }

    public void JumpToFrame(int thingId, int frameIdx, int sectorId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;
        if (!_thingFrames.TryGetValue(thingId, out var frames) || frameIdx < 0 || frameIdx >= frames.Count)
            return;

        var thing = _level.Things[thingId];
        var target = frames[frameIdx];
        Vector3 prevPos = thing.Position;
        Vector3 prevPyr = thing.PitchYawRoll;

        thing.Position = target.Position;
        thing.PitchYawRoll = target.PitchYawRoll;
        if (sectorId >= 0 && sectorId < _level.Sectors.Count)
            thing.SectorIndex = sectorId;

        if (!_thingMotions.TryGetValue(thingId, out var motion))
        {
            motion = new ThingMotionState();
            _thingMotions[thingId] = motion;
        }

        motion.CurrentFrame = frameIdx;
        motion.TargetFrame = frameIdx;
        motion.IsMoving = false;
        motion.IsPaused = false;
        motion.IsPivotRotation = false;
        motion.Elapsed = 0f;
        motion.Duration = 0f;

        var prevRot = SithMatrix34.Build34(prevPyr, Vector3.Zero);
        var curRot = SithMatrix34.Build34(thing.PitchYawRoll, Vector3.Zero);
        var stepRotMat = SithMatrix34.Multiply(curRot, prevRot.TransposeRotation());
        bool hasRotDelta = Vector3.DistanceSquared(prevPyr, thing.PitchYawRoll) > 1e-8f;
        PropagateAttachedChildren(thingId, prevPos, thing.Position, thing.Position - prevPos, stepRotMat, hasRotDelta);
    }

    public void SkipToFrame(int thingId, int frameIdx, float speed) => MoveToFrame(thingId, frameIdx, speed);

    public void PathMovePause(int thingId)
    {
        if (_thingMotions.TryGetValue(thingId, out var motion) && motion.IsMoving)
            motion.IsPaused = true;
    }

    public void PathMoveResume(int thingId)
    {
        if (_thingMotions.TryGetValue(thingId, out var motion))
            motion.IsPaused = false;
    }

    public int GetGoalFrame(int thingId) =>
        _thingMotions.TryGetValue(thingId, out var motion) ? motion.TargetFrame : -1;

    public void AttachThingToThingEx(int thingId, int attachToThingId, int attachFlags)
    {
        if (thingId >= 0 && thingId < _level.Things.Count &&
            attachToThingId >= 0 && attachToThingId < _level.Things.Count &&
            thingId != attachToThingId)
        {
            _thingParents[thingId] = attachToThingId;
            _thingAttachFlags[thingId] = attachFlags;
        }
    }

    public void DetachThing(int thingId)
    {
        _thingParents.Remove(thingId);
        _thingAttachFlags.Remove(thingId);
    }

    public int GetThingParent(int thingId) => _thingParents.GetValueOrDefault(thingId, -1);

    public void SetThingAttachFlags(int thingId, int flags) =>
        _thingAttachFlags[thingId] = GetAttachFlags(thingId) | flags;

    public int GetAttachFlags(int thingId) => _thingAttachFlags.GetValueOrDefault(thingId, 0);

    // -------------------------------------------------------------------------
    // Teleport, Physics, Orientation & Thing Timer COG Verbs
    // -------------------------------------------------------------------------

    public void TeleportThing(int thingId, int destThingId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count ||
            destThingId < 0 || destThingId >= _level.Things.Count)
        {
            return;
        }

        var thing = _level.Things[thingId];
        var dest = _level.Things[destThingId];

        thing.Position = dest.Position;
        thing.PitchYawRoll = dest.PitchYawRoll;
        if (dest.SectorIndex >= 0 && dest.SectorIndex < _level.Sectors.Count)
            thing.SectorIndex = dest.SectorIndex;

        _thingVelocities.Remove(thingId);

        if (thingId == _localPlayerThingIndex)
        {
            UpdatePlayerSectorTransitions(thing.SectorIndex);
            OnPlayerTeleported?.Invoke(dest.Position, dest.PitchYawRoll, thing.SectorIndex);
        }
    }

    public void SetThingTimer(int thingId, float seconds)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;

        if (seconds <= 0f)
            _thingTimers.Remove(thingId);
        else
            _thingTimers[thingId] = seconds;
    }

    public void SetThingPulse(int thingId, float interval)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return;

        if (interval <= 0f)
            _thingPulses.Remove(thingId);
        else
            _thingPulses[thingId] = (interval, interval);
    }

    public float GetThingMass(int thingId)
    {
        if (_thingMasses.TryGetValue(thingId, out float mass))
            return mass;

        if (thingId >= 0 && thingId < _level.Things.Count)
            return ParseTemplateFloat(_level.Things[thingId].TemplateName, "mass", 1.0f);

        return 0f;
    }

    public void SetThingMass(int thingId, float mass) => _thingMasses[thingId] = MathF.Max(0f, mass);

    public int GetPhysicsFlags(int thingId)
    {
        if (_thingPhysicsFlags.TryGetValue(thingId, out int flags))
            return flags;

        if (thingId >= 0 && thingId < _level.Things.Count)
            return (int)ParseTemplateHexOrInt(_level.Things[thingId].TemplateName, "physflags", 0x1);

        return 0;
    }

    public void SetPhysicsFlags(int thingId, int flags) =>
        _thingPhysicsFlags[thingId] = GetPhysicsFlags(thingId) | flags;

    public void ClearPhysicsFlags(int thingId, int flags) =>
        _thingPhysicsFlags[thingId] = GetPhysicsFlags(thingId) & ~flags;

    public void SetThingLook(int thingId, Vector3 lookVec)
    {
        if (thingId < 0 || thingId >= _level.Things.Count || lookVec.LengthSquared() < 1e-8f)
            return;

        Vector3 dir = Vector3.Normalize(lookVec);
        float yaw = MathF.Atan2(-dir.X, dir.Y) * (180f / MathF.PI);
        float pitch = MathF.Asin(Math.Clamp(dir.Z, -1f, 1f)) * (180f / MathF.PI);
        var cur = _level.Things[thingId].PitchYawRoll;
        _level.Things[thingId].PitchYawRoll = new Vector3(pitch, yaw, cur.Z);
    }

    public Vector3 GetThingLVec(int thingId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return Vector3.Zero;

        return GetThingForward(thingId);
    }

    public void SetThingRotVel(int thingId, Vector3 rotVel) => _thingRotVels[thingId] = rotVel;

    public Vector3 GetThingRotVel(int thingId) => _thingRotVels.GetValueOrDefault(thingId, Vector3.Zero);

    public float GetLifeLeft(int thingId) => _thingLifeleft.GetValueOrDefault(thingId, 0f);

    public void SetThingCollideSize(int thingId, float size)
    {
        if (thingId >= 0 && thingId < _level.Things.Count)
            _level.Things[thingId].Size = size;
    }

    public int IsThingVisible(int thingId)
    {
        if (thingId < 0 || thingId >= _level.Things.Count || _destroyedThings.Contains(thingId))
            return 0;

        if (IsThingInvisible(thingId))
            return 0;

        uint flags = (uint)GetThingFlags(thingId);
        if ((flags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed | SithThingFlags.Invisible)) != 0)
            return 0;

        return HasLOS(_localPlayerThingIndex, thingId);
    }

    // -------------------------------------------------------------------------
    // AI, Force SuperFlags, Weapons, Audio, Camera & Cutscene COG Verbs
    // -------------------------------------------------------------------------

    public void AISetMoveThing(int thingId, int targetThingId)
    {
        if (_npcs.TryGetValue(thingId, out var npc) && npc.IsAlive)
        {
            npc.OverrideMoveThingId = targetThingId;
            if (targetThingId >= 0 && targetThingId < _level.Things.Count)
                npc.OverrideMovePos = _level.Things[targetThingId].Position;
        }
    }

    public void AISetFireTarget(int thingId, int targetThingId)
    {
        if (_npcs.TryGetValue(thingId, out var npc) && npc.IsAlive)
            npc.OverrideFireTargetId = targetThingId >= 0 ? targetThingId : null;
    }

    public void JkSetSuperFlags(int flags) => _superFlags |= flags;

    public void JkClearSuperFlags(int flags) => _superFlags &= ~flags;

    public int JkGetSuperFlags() => _superFlags;

    public int JkGetChoice()
    {
        if (PlayerAlignmentChoice.HasValue)
            return PlayerAlignmentChoice.Value;

        // Sith alignment bin 75: >= 0 is Light Side (1), < 0 is Dark Side (2)
        return Inventory.GetInv(75) < 0f ? 2 : 1;
    }

    public void AssignWeapon(int playerThing, int binIdx)
    {
        if (playerThing == _localPlayerThingIndex || playerThing < 0)
        {
            Inventory.SetInvAvailable(binIdx, true);
            if (Inventory.GetInv(binIdx) <= 0f)
                Inventory.SetInv(binIdx, 1f);
            SelectWeapon(playerThing, binIdx);
            OnPlayerWeaponAssigned?.Invoke(binIdx);
        }
    }

    public int CreateThingNR(string template, int refThing) => CreateThing(template, refThing);

    public int CreateThingAtPosNR(string template, int sector, Vector3 pos, Vector3 pyr) =>
        CreateThingAtPos(template, sector, pos, pyr);

    public int FireProjectile(
        int shooterThing,
        string projectileTemplate,
        string fireSound,
        int mode,
        Vector3 fireOffset,
        Vector3 aimError,
        float scale,
        int scaleFlags,
        float autoaimFov,
        float autoaimMaxDist)
    {
        if (shooterThing < 0 || shooterThing >= _level.Things.Count)
            return -1;

        var shooter = _level.Things[shooterThing];
        Vector3 forward = GetThingForward(shooterThing);
        Vector3 spawnPos = shooter.Position + fireOffset;
        string modelName = !string.IsNullOrWhiteSpace(projectileTemplate)
            ? _level.ResolveModel3D(projectileTemplate)
            : "str0.3do";
        if (string.IsNullOrWhiteSpace(modelName))
            modelName = "str0.3do";

        if (!string.IsNullOrWhiteSpace(fireSound))
            _audio?.PlaySoundPos(fireSound, spawnPos, 0.85f, 0.5f, 7.0f);

        SpawnProjectile(
            spawnPos,
            forward + aimError * 0.01f,
            damage: 10.0f * (scale > 0f ? scale : 1.0f),
            ownerThingIndex: shooterThing,
            isEnemy: shooterThing != _localPlayerThingIndex,
            modelName: modelName);

        return _projectiles.Count - 1;
    }

    public void StopSound(int channel, float fadeOut)
    {
        if (channel > 0 && _activeLoopingSounds.Count > 0)
        {
            List<string>? toRemove = null;
            foreach (var kvp in _activeLoopingSounds)
            {
                if (kvp.Value == channel)
                {
                    toRemove ??= [];
                    toRemove.Add(kvp.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (string key in toRemove)
                    _activeLoopingSounds.Remove(key);
            }
        }
        _audio?.StopSound(channel, fadeOut);
    }

    public void ChangeSoundPitch(int channel, float pitch, float changeTime) =>
        _audio?.ChangeSoundPitch(channel, pitch, changeTime);

    public void ChangeSoundVol(int channel, float volume, float fadeTime) =>
        _audio?.ChangeSoundVol(channel, volume, fadeTime);

    public void SetMusicVol(float vol) => _audio?.SetMusicVolume(vol);

    public float GetSoundLen(string sound)
    {
        if (string.IsNullOrWhiteSpace(sound))
            return 0f;

        if (_audio != null)
            return _audio.GetSoundDuration(sound);

        string key = sound.Trim();
        if (_soundLenCache.TryGetValue(key, out float cached))
            return cached;

        float len = 0f;
        foreach (string path in new[] { $"sound/{key}", $"voice/{key}", key })
        {
            if (_vfs.FileExists(path) &&
                WavReader.TryParse(_vfs.ReadAllBytes(path), out var info) &&
                info != null)
            {
                len = (float)info.DurationSeconds;
                break;
            }
        }

        _soundLenCache[key] = len;
        return len;
    }

    public float GetKeyLen(string keyframe)
    {
        if (string.IsNullOrWhiteSpace(keyframe))
            return 0f;

        string key = Path.GetFileName(keyframe.Trim());
        if (_keyLenCache.TryGetValue(key, out float cached))
            return cached;

        float duration = 0f;
        foreach (string path in new[] { $"3do/key/{key}", $"key/{key}", keyframe.Trim(), key })
        {
            if (_vfs.FileExists(path))
            {
                try
                {
                    var track = KeyframeReader.Parse(_vfs.ReadAllText(path), key);
                    duration = track.Duration;
                }
                catch
                {
                    duration = 0f;
                }
                break;
            }
        }

        _keyLenCache[key] = duration;
        return duration;
    }

    public void CycleCamera() => _currentCamera = _currentCamera == 0 ? 1 : 0;

    public int GetCurrentCamera() => _currentCamera;

    public void SetCurrentCamera(int camIdx) => _currentCamera = camIdx;

    public void SetCameraFocus(int camIdx, int thingId) => _cameraFocus[camIdx] = thingId;

    public int GetPrimaryFocus(int cameraIdx) =>
        _cameraFocus.TryGetValue(cameraIdx, out int thingId) ? thingId : _localPlayerThingIndex;

    public void JkBeginCutscene() => SetActorFlags(_localPlayerThingIndex, 0xa00000);

    public void JkEndCutscene() => ClearActorFlags(_localPlayerThingIndex, 0xa00000);

    public void SetMapModeFlags(int flags) => _mapModeFlags |= flags;

    public void ClearMapModeFlags(int flags) => _mapModeFlags &= ~flags;

    public int GetMapModeFlags() => _mapModeFlags;
}
