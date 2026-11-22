using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Numerics;
using OpenSith.Core.AI;
using OpenSith.Core.Audio;
using OpenSith.Core.Cog;
using OpenSith.Core.Data;
using OpenSith.Core.Materials;
using OpenSith.Core.World;

namespace OpenSith.Core.Simulation;

public sealed partial class WorldSimulation
{
    public enum VisualEffectKind : byte
    {
        Billboard = 0,
        ParticleRing = 1,
        BeamTrail = 2
    }

    /// <summary>
    /// Active transient sprite/blast/particle/beam visual effect in world space.
    /// </summary>
    public sealed class VisualEffect
    {
        public VisualEffectKind Kind = VisualEffectKind.Billboard;
        public Vector3 Position;
        public Vector3 EndPosition;
        public string SpriteName = string.Empty;
        public string MaterialName = string.Empty;
        public float Width = 0.25f;
        public float Height = 0.25f;
        public float Age;
        public float Duration = 0.5f;
        public float MaxLight = 0.5f;
        public Vector3 ColorTint = Vector3.One;
        public bool IsAdditive = true;
        public float ExpandRadius;
        public int ParticleCount;
        public float PitchSpreadDeg = 360f;
        public float TrailSegmentSize = 0.25f;
        public float JitterRadius;
    }

    private static readonly IReadOnlyDictionary<int, string> CanonicalBinCogFallbacks =
        new Dictionary<int, string>
        {
            [PlayerInventory.BinFists] = "weap_fists.cog",
            [PlayerInventory.BinBryar] = "weap_bryar.cog",
            [PlayerInventory.BinStRifle] = "weap_strifle.cog",
            [PlayerInventory.BinThermDet] = "weap_thermdet.cog",
            [PlayerInventory.BinBowcaster] = "weap_crossbow.cog",
            [PlayerInventory.BinRepeater] = "weap_repeater.cog",
            [PlayerInventory.BinRailDet] = "weap_raildet.cog",
            [PlayerInventory.BinSeqCharge] = "weap_seqcharge.cog",
            [PlayerInventory.BinConcRifle] = "weap_concrifle.cog",
            [PlayerInventory.BinLightsaber] = "weap_saber.cog",
            [PlayerInventory.BinForceRank] = "force_well.cog",
            [PlayerInventory.BinForceJump] = "force_jump.cog",
            [PlayerInventory.BinForceSpeed] = "force_speed.cog",
            [PlayerInventory.BinForceSeeing] = "force_seeing.cog",
            [PlayerInventory.BinForcePull] = "force_pull.cog",
            [PlayerInventory.BinForceHeal] = "force_heal.cog",
            [PlayerInventory.BinForcePersuasion] = "force_persuasion.cog",
            [PlayerInventory.BinForceBlinding] = "force_blinding.cog",
            [PlayerInventory.BinForceAbsorb] = "force_absorb.cog",
            [PlayerInventory.BinForceProtection] = "force_protection.cog",
            [PlayerInventory.BinForceThrow] = "force_throw.cog",
            [PlayerInventory.BinForceGrip] = "force_grip.cog",
            [PlayerInventory.BinForceLightning] = "force_lightning.cog",
            [PlayerInventory.BinForceDestruction] = "force_destruction.cog",
            [PlayerInventory.BinForceDeadlySight] = "force_deadlysight.cog",
            [PlayerInventory.BinBacta] = "item_bacta.cog",
            [PlayerInventory.BinIrGoggles] = "item_irgoggles.cog",
            [PlayerInventory.BinFieldLight] = "item_fieldlight.cog",
            [PlayerInventory.BinShields] = "item_shields.cog",
        };

    private readonly List<VisualEffect> _visualEffects = [];
    private readonly Dictionary<string, SprSprite?> _sprCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SoundClass?> _sndCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CogInstance, int> _playerCogOwners = new();
    private bool _fireWaitExplicitlySet;

    public IReadOnlyList<VisualEffect> VisualEffects => _visualEffects;

    /// <summary>
    /// Invoked whenever a new transient explosion/blast sprite effect is spawned.
    /// </summary>
    public event Action<VisualEffect>? OnVisualEffectSpawned;

    /// <summary>
    /// Optional explicit Light (1) / Dark (2) campaign alignment choice for <see cref="JkGetChoice"/>.
    /// When unset, falls back to alignment inventory bin 75 (&lt; 0 = Dark, &gt;= 0 = Light).
    /// </summary>
    public int? PlayerAlignmentChoice
    {
        get => LocalPlayer?.AlignmentChoice;
        set
        {
            if (LocalPlayer != null)
                LocalPlayer.AlignmentChoice = value;
        }
    }

    /// <summary>
    /// Invoked when <c>SelectWeapon</c> / <c>AssignWeapon</c> equips a weapon bin for the local player.
    /// </summary>
    public event Action<int>? OnPlayerWeaponAssigned;

    public bool IsFieldLightActive(int thingId) => (GetActorFlags(thingId) & 0x4) != 0;

    public bool IsPlayerBlinded(int thingId) => (GetActorFlags(thingId) & 0x800) != 0;

    private T WithPlayerCogContext<T>(int playerThingId, Func<T> action)
    {
        int? prev = _activeCogPlayerContext;
        _activeCogPlayerContext = playerThingId;
        try
        {
            return action();
        }
        finally
        {
            _activeCogPlayerContext = prev;
        }
    }

    private void WithPlayerCogContext(int playerThingId, Action action)
    {
        int? prev = _activeCogPlayerContext;
        _activeCogPlayerContext = playerThingId;
        try
        {
            action();
        }
        finally
        {
            _activeCogPlayerContext = prev;
        }
    }

    private void RunInInstancePlayerScope(CogInstance instance, Action action)
    {
        if (_playerCogOwners.TryGetValue(instance, out int ownerThingId) && _playersByThing.ContainsKey(ownerThingId))
            WithPlayerCogContext(ownerThingId, action);
        else
            action();
    }

    private Vector3 GetPlayerWorldPos(PlayerState player) =>
        player.HasInputPosition
            ? player.WorldPosition
            : (player.ThingIndex >= 0 && player.ThingIndex < _level.Things.Count ? _level.Things[player.ThingIndex].Position : Vector3.Zero);

    private Vector3 GetShooterOrigin(int shooterThing)
    {
        if (_playersByThing.TryGetValue(shooterThing, out var shooterPlayer))
            return GetPlayerWorldPos(shooterPlayer) + new Vector3(0f, 0f, 0.155f);

        return shooterThing >= 0 && shooterThing < _level.Things.Count
            ? _level.Things[shooterThing].Position
            : Vector3.Zero;
    }

    private bool TryResolvePlayer(int thingId, [NotNullWhen(true)] out PlayerState? player)
    {
        int targetId = thingId >= 0 ? thingId : GetLocalPlayerThing();
        if (targetId >= 0 && _playersByThing.TryGetValue(targetId, out player))
            return true;

        player = null;
        return false;
    }

    private string? ResolveBinCogName(int binIdx)
    {
        if (_itemDefinitions.TryGetValue(binIdx, out var def) && !string.IsNullOrWhiteSpace(def.CogName))
            return def.CogName;

        return CanonicalBinCogFallbacks.GetValueOrDefault(binIdx);
    }

    private CogInstance? GetOrCreatePlayerClassCog(PlayerState player)
    {
        if (player.ClassCog != null)
            return player.ClassCog;

        string templateName = (player.ThingIndex >= 0 && player.ThingIndex < _level.Things.Count)
            ? _level.Things[player.ThingIndex].TemplateName
            : "walkplayer";
        string? cogName = ResolveTemplateParam(templateName, "cog");
        if (string.IsNullOrWhiteSpace(cogName))
            cogName = "kyle.cog";

        var script = LoadCogScript(cogName);
        if (script == null)
            return null;

        var instance = new CogInstance(script, _nextDynamicCogId++);
        if (instance.SymbolSlots.ContainsKey("player"))
            instance.SetSymbolValue("player", new CogValue(player.ThingIndex));
        _cogVm.AddInstance(instance);
        player.ClassCog = instance;
        _playerCogOwners[instance] = player.ThingIndex;

        if (instance.Program.Labels.ContainsKey("startup"))
        {
            WithPlayerCogContext(player.ThingIndex, () =>
                SafeSendMessage(instance, "startup", senderRef: player.ThingIndex, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
        }

        return instance;
    }

    private CogInstance? GetOrCreatePlayerBinCog(PlayerState player, int binIdx)
    {
        if (player.BinCogs.TryGetValue(binIdx, out var existing))
            return existing;

        string? cogName = ResolveBinCogName(binIdx);
        if (string.IsNullOrWhiteSpace(cogName))
            return null;

        var script = LoadCogScript(cogName);
        if (script == null)
            return null;

        var instance = new CogInstance(script, _nextDynamicCogId++);
        if (instance.SymbolSlots.ContainsKey("player"))
            instance.SetSymbolValue("player", new CogValue(player.ThingIndex));
        _cogVm.AddInstance(instance);
        player.BinCogs[binIdx] = instance;
        _playerCogOwners[instance] = player.ThingIndex;

        if (instance.Program.Labels.ContainsKey("startup"))
        {
            WithPlayerCogContext(player.ThingIndex, () =>
                SafeSendMessage(instance, "startup", senderRef: player.ThingIndex, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
        }

        return instance;
    }

    private void DispatchPlayerKilledMessages(PlayerState victim, int killerThingId)
    {
        WithPlayerCogContext(victim.ThingIndex, () =>
        {
            int source = killerThingId >= 0 ? killerThingId : victim.ThingIndex;
            if (victim.ClassCog != null && victim.ClassCog.Program.Labels.ContainsKey("killed"))
                SafeSendMessage(victim.ClassCog, "killed", senderRef: victim.ThingIndex, senderId: 0, sourceRef: source, senderType: 3);

            foreach (var binCog in victim.BinCogs.Values)
            {
                if (binCog.Program.Labels.ContainsKey("killed"))
                    SafeSendMessage(binCog, "killed", senderRef: victim.ThingIndex, senderId: 0, sourceRef: source, senderType: 3);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Per-Player COG-Driven Weapons & Saber
    // -------------------------------------------------------------------------

    public void SelectWeapon(int thingId, int weaponBin)
    {
        if (!IsValidWeaponBin(weaponBin))
            return;

        if (!TryResolvePlayer(thingId, out var player))
            return;

        if (weaponBin != PlayerInventory.BinFists && player.Inventory.GetInv(weaponBin) <= 0f)
            return;

        SelectWeaponInternal(player, weaponBin, assignMode: false);
    }

    public void AssignWeapon(int playerThing, int binIdx)
    {
        if (!TryResolvePlayer(playerThing, out var player))
            return;

        player.Inventory.SetInvAvailable(binIdx, true);
        if (player.Inventory.GetInv(binIdx) <= 0f)
            player.Inventory.SetInv(binIdx, 1f);

        SelectWeaponInternal(player, binIdx, assignMode: true);
    }

    private void SelectWeaponInternal(PlayerState player, int weaponBin, bool assignMode)
    {
        int oldBin = player.CurWeaponBin;
        if (oldBin != weaponBin)
        {
            if (player.BinCogs.TryGetValue(oldBin, out var oldCog) &&
                oldCog.Program.Labels.ContainsKey("deselected"))
            {
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(oldCog, "deselected", senderRef: oldBin, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
            }

            player.ActiveFireMode = -1;
            player.FireRate = 0f;
            player.FireHoldElapsed = 0f;
            player.FiredThisTick = false;
            player.SaberDamageActive = false;
            player.SaberActiveTimer = 0f;
            player.SaberHitThisSwing.Clear();
            _weaponMeshes.Remove(player.ThingIndex);
        }

        player.CurWeaponBin = weaponBin;
        player.CurInvWeaponBin = weaponBin;

        var newCog = GetOrCreatePlayerBinCog(player, weaponBin);
        if (newCog != null && newCog.Program.Labels.ContainsKey("selected"))
        {
            WithPlayerCogContext(player.ThingIndex, () =>
                SafeSendMessage(newCog, "selected", senderRef: assignMode ? 1 : 0, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
        }
        else
        {
            ApplyDefaultWeaponPov(player, weaponBin);
        }

        if (player.ThingIndex == _localPlayerThingIndex)
            OnPlayerWeaponAssigned?.Invoke(weaponBin);
    }

    private static void ApplyDefaultWeaponPov(PlayerState player, int weaponBin)
    {
        (player.PovModel, player.PovKey) = weaponBin switch
        {
            PlayerInventory.BinFists => ("fstv.3do", "fstvpst1.key"),
            PlayerInventory.BinBryar => ("bryv.3do", "bryvpst1.key"),
            PlayerInventory.BinStRifle => ("strv.3do", "strvpst1.key"),
            PlayerInventory.BinThermDet => ("detv.3do", "detvpst1.key"),
            PlayerInventory.BinBowcaster => ("bowv.3do", "bowvpst1.key"),
            PlayerInventory.BinRepeater => ("rptv.3do", "rptvpst1.key"),
            PlayerInventory.BinRailDet => ("rldv.3do", "rldvpst1.key"),
            PlayerInventory.BinSeqCharge => ("seqv.3do", "seqvpst1.key"),
            PlayerInventory.BinConcRifle => ("conv.3do", "convpst1.key"),
            PlayerInventory.BinLightsaber => ("sabv.3do", "sabvhold.key"),
            _ => ("bryv.3do", "bryvpst1.key")
        };
        player.PovKeyTime = 0f;
        player.PovKeyLoop = true;
        player.PovKeyEndPause = false;
    }

    public bool BeginWeaponFire(int playerThing, int mode = 0)
    {
        if (!TryResolvePlayer(playerThing, out var player))
            return false;
        if (player.Inventory.IsDead || IsActorFrozen(player.ThingIndex))
            return false;
        if (player.ActiveFireMode == mode)
            return true;
        if (player.ActiveFireMode >= 0)
            EndWeaponFire(player.ThingIndex, player.ActiveFireMode);

        var cog = GetOrCreatePlayerBinCog(player, player.CurWeaponBin);
        if (cog == null || !cog.Program.Labels.ContainsKey("activated"))
            return false;

        player.FireHoldElapsed = 0f;
        WithPlayerCogContext(player.ThingIndex, () =>
            SafeSendMessage(cog, "activated", senderRef: mode, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
        return true;
    }

    public bool EndWeaponFire(int playerThing, int mode = 0)
    {
        if (!TryResolvePlayer(playerThing, out var player))
            return false;
        if (player.ActiveFireMode != mode)
            return false;

        var cog = GetOrCreatePlayerBinCog(player, player.CurWeaponBin);
        if (cog == null)
        {
            player.ActiveFireMode = -1;
            player.FireRate = 0f;
            player.FireHoldElapsed = 0f;
            return false;
        }

        if (cog.Program.Labels.ContainsKey("deactivated"))
        {
            WithPlayerCogContext(player.ThingIndex, () =>
                SafeSendMessage(cog, "deactivated", senderRef: mode, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
        }

        if (player.ActiveFireMode == mode)
        {
            player.ActiveFireMode = -1;
            player.FireRate = 0f;
            player.FireHoldElapsed = 0f;
        }

        return true;
    }

    public void ActivateWeapon(int weaponThing, float fireRate, int mode)
    {
        if (!TryResolvePlayer(weaponThing, out var player))
            return;

        float safeRate = float.IsFinite(fireRate) && fireRate > 0f ? fireRate : 0f;
        player.ActiveFireMode = mode;
        player.FireRate = safeRate;
        player.FireHoldElapsed = 0f;

        if (safeRate > 0f && player.FireWait <= 0f && player.MountWait <= 0f && !player.Inventory.IsDead)
        {
            TriggerWeaponFire(player, mode);
            player.FiredThisTick = true;
        }
    }

    public float DeactivateWeapon(int weaponThing, int mode)
    {
        if (!TryResolvePlayer(weaponThing, out var player))
            return 0f;
        if (player.ActiveFireMode != mode)
            return 0f;

        float elapsed = player.FireHoldElapsed;
        float remainingWait = player.FireWait > 0f ? player.FireWait : player.FireRate;
        if (remainingWait > 0f)
            player.MountWait = MathF.Max(player.MountWait, remainingWait);

        player.ActiveFireMode = -1;
        player.FireRate = 0f;
        player.FireHoldElapsed = 0f;
        player.FiredThisTick = false;
        return elapsed;
    }

    public void ChangeFireRate(int weaponThing, float fireRate)
    {
        if (!TryResolvePlayer(weaponThing, out var player))
            return;

        float safeWait = float.IsFinite(fireRate) && fireRate > 0f ? fireRate : 0f;
        player.FireRate = safeWait;
        player.FireWait = safeWait;
        _fireWaitExplicitlySet = true;
    }

    public void SetFireWait(int thingId, float wait)
    {
        if (!TryResolvePlayer(thingId, out var player))
            return;

        player.FireWait = float.IsFinite(wait) && wait > 0f ? wait : 0f;
        _fireWaitExplicitlySet = true;
    }

    public void SetMountWait(int thingId, float wait)
    {
        if (!TryResolvePlayer(thingId, out var player))
            return;

        player.MountWait = float.IsFinite(wait) && wait > 0f ? wait : 0f;
    }

    public int GetCurWeaponMode()
    {
        int id = GetLocalPlayerThing();
        return id >= 0 && _playersByThing.TryGetValue(id, out var player) ? player.ActiveFireMode : -1;
    }

    public int GetAutoSwitch() => 1;

    public int GetAutoReload() => 1;

    public int AutoSelectWeapon(int thingId, int mode)
    {
        if (!TryResolvePlayer(thingId, out var player))
            return PlayerInventory.BinFists;

        int bestBin = -1;
        float bestPriority = -1f;

        for (int bin = PlayerInventory.BinFists; bin <= PlayerInventory.BinLightsaber; bin++)
        {
            if (bin != PlayerInventory.BinFists && player.Inventory.GetInv(bin) <= 0f)
                continue;

            float priority = GetWeaponPriority(player.ThingIndex, bin, mode);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestBin = bin;
            }
        }

        if (bestBin > 0)
            return bestBin;

        var inv = player.Inventory;
        if (inv.EnergyAmmo > 0f)
        {
            if (inv.GetInv(PlayerInventory.BinRepeater) > 0f)
                return PlayerInventory.BinRepeater;
            if (inv.GetInv(PlayerInventory.BinStRifle) > 0f)
                return PlayerInventory.BinStRifle;
            if (inv.HasBryar)
                return PlayerInventory.BinBryar;
        }

        return inv.HasLightsaber ? PlayerInventory.BinLightsaber : PlayerInventory.BinFists;
    }

    public float GetWeaponPriority(int playerThing, int binIdx, int mode)
    {
        if (!TryResolvePlayer(playerThing, out var player))
            return -1f;

        if (binIdx != PlayerInventory.BinFists && player.Inventory.GetInv(binIdx) <= 0f)
            return -1f;

        var cog = GetOrCreatePlayerBinCog(player, binIdx);
        if (cog != null && cog.Program.Labels.ContainsKey("autoselect"))
        {
            CogValue ret = WithPlayerCogContext(player.ThingIndex, () =>
                SafeSendMessageWithReturn(cog, "autoselect", senderRef: mode, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
            if (ret.Type is CogValueType.Int or CogValueType.Float)
                return ret.AsFloat;
        }

        return binIdx switch
        {
            PlayerInventory.BinConcRifle when player.Inventory.PowerAmmo >= 4f => 800f,
            PlayerInventory.BinRailDet when player.Inventory.RailCharges >= 1f => 700f,
            PlayerInventory.BinRepeater when player.Inventory.PowerAmmo >= 1f => 600f,
            PlayerInventory.BinStRifle when player.Inventory.EnergyAmmo >= 2f => 550f,
            PlayerInventory.BinBowcaster when player.Inventory.PowerAmmo >= 2f => 525f,
            PlayerInventory.BinBryar when player.Inventory.EnergyAmmo >= 1f => 500f,
            PlayerInventory.BinThermDet => 300f,
            PlayerInventory.BinLightsaber => 200f,
            PlayerInventory.BinFists => 10f,
            _ => -1f
        };
    }

    public int GetCurWeapon(int thingId) =>
        _playersByThing.TryGetValue(thingId, out var player) ? player.CurWeaponBin : PlayerInventory.BinFists;

    public void SetCurWeapon(int playerThing, int binIdx)
    {
        if (TryResolvePlayer(playerThing, out var player))
            player.CurWeaponBin = binIdx;
    }

    public void SetCurInvWeapon(int thingId, int weapon)
    {
        if (TryResolvePlayer(thingId, out var player))
            player.CurInvWeaponBin = weapon;
    }

    public int GetCurInvWeapon(int thingId) =>
        _playersByThing.TryGetValue(thingId, out var player) ? player.CurInvWeaponBin : -1;

    public void JkSetPovModel(int thingId, string model)
    {
        if (TryResolvePlayer(thingId, out var player) && !string.IsNullOrWhiteSpace(model))
            player.PovModel = model.Trim();
    }

    public int JkPlayPovKey(int thingId, string keyframe, int priority, int flags)
    {
        if (!TryResolvePlayer(thingId, out var player) || string.IsNullOrWhiteSpace(keyframe))
            return -1;

        player.PovKey = keyframe.Trim();
        player.PovKeyTime = 0f;
        player.PovKeyLoop = (flags & 0x14) == 0;
        player.PovKeyEndPause = (flags & 0x10) != 0;
        player.PovTrackId = _nextKeyTrackId++;
        return player.PovTrackId;
    }

    public void JkStopPovKey(int thingId, int track, float fadeTime)
    {
        if (!TryResolvePlayer(thingId, out var player))
            return;

        if (player.PovTrackId == track || track < 0)
        {
            player.PovKeyLoop = true;
            player.PovKeyEndPause = false;
            player.PovKeyTime = 0f;
        }
    }

    public void JkSetWaggle(int thingId, Vector3 moveVec, float speed)
    {
        if (!TryResolvePlayer(thingId, out var player))
            return;

        player.PovWaggleMove = moveVec;
        player.PovWaggleSpeed = speed;
    }

    public void SetPovShake(Vector3 posOffset, Vector3 rotOffset, float duration, float intensity)
    {
        if (!TryResolvePlayer(-1, out var player))
            return;

        player.PovShakePos = posOffset;
        player.PovShakeRot = rotOffset;
        player.PovShakeDuration = MathF.Max(0.01f, duration);
        player.PovShakeTimer = player.PovShakeDuration;
        player.PovShakeIntensity = intensity;
    }

    public void JkEnableSaber(int thingId, float damage, float collideLength, float stunLength)
    {
        if (!TryResolvePlayer(thingId, out var player))
            return;

        player.SaberDamageActive = true;
        player.SaberActiveTimer = MathF.Max(0.25f, stunLength > 0f ? stunLength : collideLength);
        player.SaberDamage = damage;
        player.SaberCollideLength = collideLength;
        player.SaberStunLength = stunLength;
        player.SaberHitThisSwing.Clear();
    }

    public void JkDisableSaber(int thingId)
    {
        if (!TryResolvePlayer(thingId, out var player))
            return;

        player.SaberDamageActive = false;
        player.SaberActiveTimer = 0f;
        player.SaberHitThisSwing.Clear();
    }

    public int JkGetSaberCam() => 0;

    public int PlayMode(int thingId, int mode)
    {
        int track = _nextKeyTrackId++;
        if (thingId < 0)
            return track;

        string action = mode switch
        {
            2 => "stand",
            8 => "fire",
            18 => "fire2",
            25 => "hit",
            38 => "fire",
            41 or 42 => "stand",
            _ => "stand"
        };
        if (_npcs.TryGetValue(thingId, out var npc))
        {
            npc.AnimAction = action;
            npc.AnimMode = 0;
        }
        return track;
    }

    public int JkGetChoice()
    {
        if (TryResolvePlayer(-1, out var player))
        {
            if (player.AlignmentChoice.HasValue)
                return player.AlignmentChoice.Value;
            return player.Inventory.GetInv(PlayerInventory.BinAlignment) < 0f ? 2 : 1;
        }
        return 1;
    }

    public void JkSetSuperFlags(int flags)
    {
        if (TryResolvePlayer(-1, out var player))
            player.SuperFlags |= flags;
    }

    public void JkClearSuperFlags(int flags)
    {
        if (TryResolvePlayer(-1, out var player))
            player.SuperFlags &= ~flags;
    }

    public int JkGetSuperFlags() =>
        TryResolvePlayer(-1, out var player) ? player.SuperFlags : 0;

    private void TriggerWeaponFire(PlayerState player, int mode)
    {
        var cog = GetOrCreatePlayerBinCog(player, player.CurWeaponBin);
        if (cog == null || !cog.Program.Labels.ContainsKey("fire"))
            return;

        _fireWaitExplicitlySet = false;
        WithPlayerCogContext(player.ThingIndex, () =>
            SafeSendMessage(cog, "fire", senderRef: mode, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));

        if (!_fireWaitExplicitlySet && player.FireWait <= 0f && player.FireRate > 0f)
            player.FireWait = player.FireRate;
    }

    private void UpdatePlayerWeapons(float dt)
    {
        foreach (var player in _players)
        {
            if (player.BinWaitTimers.Count > 0)
            {
                foreach (int bin in new List<int>(player.BinWaitTimers.Keys))
                {
                    float next = player.BinWaitTimers[bin] - dt;
                    if (next <= 0f)
                        player.BinWaitTimers.Remove(bin);
                    else
                        player.BinWaitTimers[bin] = next;
                }
            }

            if (player.FiredThisTick)
                player.FiredThisTick = false;
            else if (player.FireWait > 0f)
                player.FireWait = MathF.Max(0f, player.FireWait - dt);
            if (player.MountWait > 0f)
                player.MountWait = MathF.Max(0f, player.MountWait - dt);

            player.PovKeyTime += dt;
            if (!player.PovKeyLoop && !string.IsNullOrWhiteSpace(player.PovKey))
            {
                float keyLen = GetKeyLen(player.PovKey);
                if (keyLen > 0.002f && player.PovKeyTime >= keyLen)
                    player.PovKeyTime = MathF.Max(0f, keyLen - 0.001f);
            }

            if (player.PovShakeTimer > 0f)
            {
                player.PovShakeTimer = MathF.Max(0f, player.PovShakeTimer - dt);
                if (player.PovShakeTimer <= 0f)
                    player.PovShakePos = Vector3.Zero;
            }

            if (player.Inventory.IsDead)
            {
                player.ActiveBinFireTimers.Clear();
                player.ActiveFireMode = -1;
                player.FireRate = 0f;
            }
            else if (player.ActiveBinFireTimers.Count > 0)
            {
                foreach (int bin in new List<int>(player.ActiveBinFireTimers.Keys))
                {
                    if (!player.ActiveBinFireTimers.TryGetValue(bin, out var timer))
                        continue;

                    float remaining = timer.Remaining - dt;
                    if (remaining <= 0f)
                    {
                        player.ActiveBinFireTimers[bin] = (timer.Interval, timer.Interval);
                        var binCog = GetOrCreatePlayerBinCog(player, bin);
                        if (binCog != null && binCog.Program.Labels.ContainsKey("fire"))
                        {
                            WithPlayerCogContext(player.ThingIndex, () =>
                                SafeSendMessage(binCog, "fire", senderRef: bin, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
                        }
                    }
                    else
                    {
                        player.ActiveBinFireTimers[bin] = (timer.Interval, remaining);
                    }
                }
            }

            if (player.ActiveFireMode >= 0)
            {
                player.FireHoldElapsed += dt;
                if (player.FireRate > 0f && player.FireWait <= 0f && player.MountWait <= 0f && !player.Inventory.IsDead)
                {
                    TriggerWeaponFire(player, player.ActiveFireMode);
                }
            }

            if (player.SaberDamageActive)
            {
                if (player.SaberCollideLength > 0f && !player.Inventory.IsDead)
                    UpdateSaberMeleeSweep(player);

                player.SaberActiveTimer -= dt;
                if (player.SaberActiveTimer <= 0f)
                {
                    player.SaberDamageActive = false;
                    player.SaberActiveTimer = 0f;
                    player.SaberHitThisSwing.Clear();
                }
            }
        }
    }

    private void UpdateSaberMeleeSweep(PlayerState player)
    {
        if (player.ThingIndex < 0 || player.ThingIndex >= _level.Things.Count)
            return;

        Vector3 rayOrigin = GetShooterOrigin(player.ThingIndex);
        Vector3 dir = GetThingForward(player.ThingIndex);
        float maxRange = MathF.Max(player.SaberCollideLength + 0.25f, 0.45f);
        float damage = MathF.Max(player.SaberDamage, 25f);

        Vector3 meleeEnd = rayOrigin + dir * maxRange;
        bool hitWall = TryRaycastWorldWithHit(rayOrigin, meleeEnd, out Vector3 wallHitPos, out int hitSurfaceId, out _);
        float closestDist = hitWall ? Vector3.Distance(rayOrigin, wallHitPos) : maxRange;

        foreach (var npc in _npcs.Values)
        {
            if (!npc.IsAlive || player.SaberHitThisSwing.Contains(npc.Thing.ThingIndex))
                continue;

            Vector3 npcCenter = npc.Thing.Position + new Vector3(0f, 0f, 0.10f);
            Vector3 toNpc = npcCenter - rayOrigin;
            float proj = Vector3.Dot(toNpc, dir);
            if (proj <= 0f || proj > closestDist)
                continue;

            float perpDist = Vector3.Distance(rayOrigin + dir * proj, npcCenter);
            float hitRadius = MathF.Max(npc.Radius * 1.6f, 0.09f);
            if (perpDist > hitRadius || !HasLineOfSight(rayOrigin, npcCenter))
                continue;

            player.SaberHitThisSwing.Add(npc.Thing.ThingIndex);

            if (CanNpcBlock(npc))
            {
                Vector3 toAttacker = rayOrigin - npcCenter;
                toAttacker = toAttacker.LengthSquared() > 1e-6f ? Vector3.Normalize(toAttacker) : -dir;
                Vector3 npcForward = GetThingForward(npc.Thing);
                bool attackerInvisible = (GetActorFlags(player.ThingIndex) & 0x80) != 0;

                if (!attackerInvisible &&
                    Vector3.Dot(npcForward, toAttacker) >= npc.BlockFov &&
                    _random.NextDouble() < npc.BlockChance)
                {
                    npc.AnimAction = "block";
                    npc.BlockAnimTimer = 0.35f;
                    npc.AnimMode = 1;
                    npc.LoopAnim = false;
                    Vector3 sparkPos = npcCenter + toAttacker * 0.06f;
                    SpawnSparks(sparkPos, toAttacker, count: 12);
                    _audio?.PlaySoundPos("ltsaberhit01.wav", sparkPos, 0.95f, 0.5f, 6.0f);
                    DispatchThingCogMessageFromSource(npc.Thing.ThingIndex, player.ThingIndex, "blocked");
                    continue;
                }
            }

            _audio?.PlaySoundPos("ltsaberbodyhit01.wav", npc.Thing.Position, 0.9f, 0.5f, 5.0f);
            ApplyNpcDamage(npc.Thing.ThingIndex, damage, rayOrigin, damageType: 16, attackerThingId: player.ThingIndex);
        }

        foreach (var targetPlayer in _players)
        {
            if (targetPlayer.ThingIndex == player.ThingIndex || targetPlayer.Inventory.IsDead ||
                player.SaberHitThisSwing.Contains(targetPlayer.ThingIndex))
                continue;

            if (targetPlayer.ThingIndex < 0 || targetPlayer.ThingIndex >= _level.Things.Count)
                continue;

            Vector3 targetCenter = _level.Things[targetPlayer.ThingIndex].Position + new Vector3(0f, 0f, 0.10f);
            Vector3 toTarget = targetCenter - rayOrigin;
            float proj = Vector3.Dot(toTarget, dir);
            if (proj <= 0f || proj > closestDist)
                continue;

            float perpDist = Vector3.Distance(rayOrigin + dir * proj, targetCenter);
            if (perpDist <= 0.10f && HasLineOfSight(rayOrigin, targetCenter))
            {
                player.SaberHitThisSwing.Add(targetPlayer.ThingIndex);
                ApplyDamageToPlayer(targetPlayer, player.ThingIndex, damage, "ltsaberbodyhit01.wav");
            }
        }

        for (int thingId = 0; thingId < _level.Things.Count; thingId++)
        {
            if (player.SaberHitThisSwing.Contains(thingId) || !IsDamagableProp(thingId))
                continue;

            Vector3 propCenter = _level.Things[thingId].Position;
            float proj = Vector3.Dot(propCenter - rayOrigin, dir);
            if (proj <= 0f || proj > closestDist)
                continue;

            float perpDist = Vector3.Distance(rayOrigin + dir * proj, propCenter);
            if (perpDist <= GetPropHitRadius(thingId) && HasLineOfSight(rayOrigin, propCenter))
            {
                player.SaberHitThisSwing.Add(thingId);
                _audio?.PlaySoundPos("ltsaberbodyhit01.wav", propCenter, 0.9f, 0.5f, 5.0f);
                DispatchThingCogMessageFromSource(thingId, player.ThingIndex, "damaged", new CogValue(damage), new CogValue(16));
            }
        }

        if (hitWall && hitSurfaceId >= 0)
        {
            int surfaceHitMarker = -1000 - hitSurfaceId;
            if (player.SaberHitThisSwing.Add(surfaceHitMarker))
                DispatchSurfaceDamaged(hitSurfaceId, damage, damageType: 16, sourceRefOverride: player.ThingIndex);
        }
    }

    // -------------------------------------------------------------------------
    // Template-Driven FireProjectile, Explosions & VFX
    // -------------------------------------------------------------------------

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

        var (right, forward, up) = GetThingBasis(shooterThing);
        bool shooterIsPlayer = _playersByThing.ContainsKey(shooterThing);
        Vector3 originPos = GetShooterOrigin(shooterThing);
        Vector3 targetSpawnPos = originPos + right * fireOffset.X + forward * fireOffset.Y + up * fireOffset.Z;

        Vector3 aimDir = forward;
        float errX = float.IsFinite(aimError.X) ? aimError.X : 0f;
        float errY = float.IsFinite(aimError.Y) ? aimError.Y : 0f;
        float errZ = float.IsFinite(aimError.Z) ? aimError.Z : 0f;
        if (errX != 0f || errY != 0f || errZ != 0f)
        {
            float pitchRad = errX * (MathF.PI / 180f);
            float yawRad = -errY * (MathF.PI / 180f);
            aimDir = Vector3.Normalize(forward + up * MathF.Sin(pitchRad) + right * MathF.Sin(yawRad));
            right = Vector3.Cross(aimDir, Vector3.UnitZ);
            right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
            up = Vector3.Normalize(Vector3.Cross(right, aimDir));
        }

        if (!float.IsFinite(scale) || scale < 0f)
            scale = 1.0f;

        string tmpl = projectileTemplate?.Trim() ?? string.Empty;
        Vector3 localVel = ParseTemplateVector3(tmpl, "vel", new Vector3(0f, 8f, 0f));
        Vector3 worldVel = right * localVel.X + aimDir * localVel.Y + up * localVel.Z;
        if (worldVel.LengthSquared() < 1e-5f)
            worldVel = aimDir * 8.0f;

        if ((scaleFlags & 0x1) != 0 && scale > 0f)
            worldVel *= scale;

        float baseDamage = ParseTemplateFloat(tmpl, "damage", 10.0f);
        if ((scaleFlags & 0x2) != 0 && scale > 0f)
            baseDamage *= scale;
        else if (baseDamage <= 0f && scale > 0f && !string.IsNullOrWhiteSpace(ResolveTemplateParam(tmpl, "explode")))
            baseDamage = 0f;

        float lifetime = ParseTemplateFloat(tmpl, "timer", 2.0f);
        if (lifetime <= 0f)
            lifetime = 2.0f;

        uint physFlags = ParseTemplateHexOrInt(tmpl, "physflags", 0x200);
        uint typeFlags = ParseTemplateHexOrInt(tmpl, "typeflags", 0x40d);
        int damageClass = (int)ParseTemplateHexOrInt(tmpl, "damageclass", 2);
        float minDamage = MathF.Max(0f, ParseTemplateFloat(tmpl, "mindamage", 0f));
        float damageDecayRate = MathF.Max(0f, ParseTemplateFloat(tmpl, "rate", 0f));
        if (baseDamage <= 0f || minDamage >= baseDamage)
            damageDecayRate = 0f;
        float radius = MathF.Max(ParseTemplateFloat(tmpl, "size", 0.01f), 0.01f);

        Vector3 spawnPos = targetSpawnPos;
        if (Vector3.DistanceSquared(originPos, targetSpawnPos) > 1e-6f)
        {
            if (TryRaycastWorldWithHit(originPos, targetSpawnPos, out Vector3 wallHit, out _, out _))
            {
                spawnPos = Vector3.Lerp(originPos, wallHit, 0.5f);
            }
            else if (HasEntityObstruction(shooterThing, !shooterIsPlayer, originPos, targetSpawnPos, radius))
            {
                spawnPos = originPos;
            }
        }

        string? explodeTpl = ResolveTemplateParam(tmpl, "explode");
        string? fleshHitTpl = ResolveTemplateParam(tmpl, "fleshhit");

        if (!string.IsNullOrWhiteSpace(fireSound) &&
            !fireSound.Equals("-1", StringComparison.OrdinalIgnoreCase) &&
            !fireSound.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _audio?.PlaySoundPos(fireSound, spawnPos, 0.85f, 0.5f, 7.0f);
        }

        // Hitscan instant-impact weapons (SITH_WF_INSTANT_IMPACT 0x8000): +concblast2, +force_lightning
        if ((typeFlags & 0x8000) != 0)
        {
            FireInstantBeamWeapon(
                shooterThing,
                !shooterIsPlayer,
                tmpl,
                originPos,
                spawnPos,
                aimDir,
                baseDamage,
                damageClass,
                explodeTpl,
                fleshHitTpl);
            return -1;
        }

        string modelName = !string.IsNullOrWhiteSpace(tmpl) ? _level.ResolveModel3D(tmpl) : string.Empty;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            string? spriteRef = ResolveTemplateParam(tmpl, "sprite");
            modelName = string.IsNullOrWhiteSpace(spriteRef) ? "str0.3do" : string.Empty;
        }

        bool useGravity = (physFlags & 0x1) != 0;
        bool hasSurfaceBounceFlag = (physFlags & 0x20) != 0;
        bool bounce = hasSurfaceBounceFlag && (typeFlags & 0x4) != 0;
        bool laserBounce = (typeFlags & 0x80000) != 0;
        bool sticky = (typeFlags & 0x200000) != 0 && (typeFlags & 0x4) == 0 &&
            (tmpl.StartsWith("+raildet2", StringComparison.OrdinalIgnoreCase) || (physFlags & 0x88) != 0);
        if (!sticky && tmpl.StartsWith("+seqchrg", StringComparison.OrdinalIgnoreCase))
            sticky = true;

        bool proximityDetonate = tmpl.Equals("+seqchrg", StringComparison.OrdinalIgnoreCase);
        bool explodeOnTimeout = bounce || sticky;
        bool explodeOnImpact = !bounce && !sticky && !laserBounce;

        _projectiles.Add(new Projectile
        {
            Position = spawnPos,
            Direction = Vector3.Normalize(worldVel),
            Velocity = worldVel,
            Damage = baseDamage,
            MinDamage = minDamage,
            DamageDecayRate = damageDecayRate,
            Lifetime = lifetime,
            OwnerThingIndex = shooterThing,
            IsEnemy = !shooterIsPlayer,
            ModelName = modelName,
            BoltColor = ResolveBoltColorForModel(modelName, tmpl),
            UseGravity = useGravity,
            Bounce = bounce,
            LaserBounce = laserBounce,
            BouncesRemaining = laserBounce ? 3 : 0,
            Sticky = sticky,
            ProximityDetonate = proximityDetonate,
            ProximityRange = 0.45f,
            ExplodeOnImpact = explodeOnImpact,
            ExplodeOnTimeout = explodeOnTimeout,
            ExplodeTemplate = explodeTpl,
            FleshHitTemplate = fleshHitTpl,
            DamageClass = damageClass,
            Radius = radius
        });

        return -1;
    }

    private void FireInstantBeamWeapon(
        int shooterThing,
        bool shooterIsEnemy,
        string tmpl,
        Vector3 rayOrigin,
        Vector3 beamStartPos,
        Vector3 aimDir,
        float baseDamage,
        int damageClass,
        string? explodeTpl,
        string? fleshHitTpl)
    {
        float range = MathF.Max(0.25f, ParseTemplateFloat(tmpl, "range", 5.0f));
        float minDamage = ParseTemplateFloat(tmpl, "mindamage", 0f);
        float damageRate = ParseTemplateFloat(tmpl, "rate", 0f);
        float impactForce = ParseTemplateFloat(tmpl, "force", 0f);
        float elementSize = MathF.Max(0.04f, ParseTemplateFloat(tmpl, "elementsize", 0.25f));
        float trailCylRadius = ParseTemplateFloat(tmpl, "trailcylradius", 0f);
        bool isLightning = tmpl.Contains("lightning", StringComparison.OrdinalIgnoreCase) || trailCylRadius > 0.001f;

        Vector3 rayEnd = rayOrigin + aimDir * range;
        float closestDist = range;
        Vector3 hitPos = rayEnd;
        bool hitAny = TryRaycastWorldWithHit(rayOrigin, rayEnd, out Vector3 wallHit, out int hitSurfaceId, out _);
        if (hitAny)
        {
            closestDist = Vector3.Distance(rayOrigin, wallHit);
            hitPos = wallHit;
        }
        else
        {
            hitSurfaceId = -1;
        }

        int hitNpcId = -1;
        int hitPropId = -1;
        PlayerState? hitPlayer = null;
        const float beamEntityRadius = 0.045f;

        foreach (var npc in _npcs.Values)
        {
            if (!npc.IsAlive || npc.Thing.ThingIndex == shooterThing)
                continue;
            if (shooterIsEnemy && npc.Brain.Class.HasInstinct("PrimaryFire"))
                continue;

            Vector3 npcCenter = npc.Thing.Position + new Vector3(0f, 0f, 0.10f);
            float hitRadius = MathF.Max(npc.Radius * 1.6f, 0.09f) + beamEntityRadius;
            float distSq = DistanceSqPointToSegment(npcCenter, rayOrigin, hitPos, out float segT);
            float distAlongRay = segT * closestDist;
            if (distSq <= hitRadius * hitRadius && distAlongRay < closestDist)
            {
                closestDist = distAlongRay;
                hitPos = Vector3.Lerp(rayOrigin, hitPos, segT);
                hitNpcId = npc.Thing.ThingIndex;
                hitSurfaceId = -1;
            }
        }

        foreach (var targetPlayer in _players)
        {
            if (targetPlayer.Inventory.IsDead || targetPlayer.ThingIndex == shooterThing ||
                targetPlayer.ThingIndex < 0 || targetPlayer.ThingIndex >= _level.Things.Count)
                continue;

            Vector3 targetTorso = _level.Things[targetPlayer.ThingIndex].Position + new Vector3(0f, 0f, 0.13f);
            float hitRadius = 0.08f + beamEntityRadius;
            float distSq = DistanceSqPointToSegment(targetTorso, rayOrigin, hitPos, out float segT);
            float distAlongRay = segT * closestDist;
            if (distSq <= hitRadius * hitRadius && distAlongRay < closestDist)
            {
                closestDist = distAlongRay;
                hitPos = Vector3.Lerp(rayOrigin, hitPos, segT);
                hitPlayer = targetPlayer;
                hitNpcId = -1;
                hitSurfaceId = -1;
            }
        }

        for (int thingId = 0; thingId < _level.Things.Count; thingId++)
        {
            if (thingId == shooterThing || !IsDamagableProp(thingId))
                continue;

            Vector3 propCenter = _level.Things[thingId].Position;
            float hitRadius = GetPropHitRadius(thingId);
            float distSq = DistanceSqPointToSegment(propCenter, rayOrigin, hitPos, out float segT);
            float distAlongRay = segT * closestDist;
            if (distSq <= hitRadius * hitRadius && distAlongRay < closestDist)
            {
                closestDist = distAlongRay;
                hitPos = Vector3.Lerp(rayOrigin, hitPos, segT);
                hitPropId = thingId;
                hitPlayer = null;
                hitNpcId = -1;
                hitSurfaceId = -1;
            }
        }

        float finalDamage = damageRate > 0f
            ? MathF.Max(minDamage, baseDamage - damageRate * closestDist)
            : baseDamage;

        if (hitPropId >= 0)
        {
            if (finalDamage > 0f)
                DispatchThingCogMessageFromSource(hitPropId, shooterThing, "damaged", new CogValue(finalDamage), new CogValue(damageClass));
            TriggerExplosion(hitPos, fleshHitTpl ?? explodeTpl, shooterThing, directHitThingId: hitPropId, fallbackSound: isLightning ? "ForceLitning02.wav" : "concuss2.wav");
        }
        else if (hitNpcId >= 0)
        {
            if (finalDamage > 0f)
                ApplyNpcDamage(hitNpcId, finalDamage, rayOrigin, damageClass, shooterThing);
            if (impactForce > 0f && _npcs.TryGetValue(hitNpcId, out var npc))
                npc.Velocity += aimDir * (impactForce * 0.015f);
            TriggerExplosion(hitPos, fleshHitTpl ?? explodeTpl, shooterThing, directHitThingId: hitNpcId, fallbackSound: isLightning ? "ForceLitning02.wav" : "kbodyhit01.wav");
        }
        else if (hitPlayer != null)
        {
            if (finalDamage > 0f)
                ApplyDamageToPlayer(hitPlayer, shooterThing, finalDamage, isLightning ? "ForceLitning02.wav" : "kbodyhit01.wav");
            if (impactForce > 0f)
            {
                float hitMass = hitPlayer.ThingIndex >= 0 && hitPlayer.ThingIndex < _level.Things.Count
                    ? MathF.Max(1f, ParseTemplateFloat(_level.Things[hitPlayer.ThingIndex].TemplateName, "mass", 150f))
                    : 150f;
                AddThingVel(hitPlayer.ThingIndex, aimDir * (impactForce / hitMass));
            }
            TriggerExplosion(hitPos, fleshHitTpl ?? explodeTpl, shooterThing, directHitThingId: hitPlayer.ThingIndex, fallbackSound: isLightning ? "ForceLitning02.wav" : "kbodyhit01.wav");
        }
        else if (hitAny)
        {
            if (hitSurfaceId >= 0 && finalDamage > 0f)
                DispatchSurfaceDamaged(hitSurfaceId, finalDamage, damageClass, shooterThing);
            TriggerExplosion(hitPos, explodeTpl, shooterThing, fallbackSound: isLightning ? "ForceLitning02.wav" : "concuss2.wav");
        }

        string? trailThing = ResolveTemplateParam(tmpl, "trailthing");
        string? trailMat = !string.IsNullOrWhiteSpace(trailThing)
            ? ResolveTemplateParam(trailThing, "material")
            : null;

        var beamVfx = new VisualEffect
        {
            Kind = VisualEffectKind.BeamTrail,
            Position = beamStartPos,
            EndPosition = hitPos,
            SpriteName = tmpl,
            MaterialName = !string.IsNullOrWhiteSpace(trailMat) ? trailMat : (isLightning ? "00ramp4.mat" : "dusty.mat"),
            Width = isLightning ? 0.025f : 0.14f,
            Height = isLightning ? 0.025f : 0.14f,
            Duration = isLightning ? 0.24f : 0.45f,
            MaxLight = 0.6f,
            ColorTint = isLightning ? new Vector3(0.58f, 0.85f, 1.0f) : new Vector3(0.35f, 0.85f, 1.0f),
            IsAdditive = true,
            TrailSegmentSize = elementSize,
            JitterRadius = isLightning ? MathF.Max(0.035f, trailCylRadius) : 0f
        };
        _visualEffects.Add(beamVfx);
        OnVisualEffectSpawned?.Invoke(beamVfx);
    }

    private bool HasEntityObstruction(
        int shooterThing,
        bool shooterIsEnemy,
        Vector3 originPos,
        Vector3 targetSpawnPos,
        float projectileRadius)
    {
        foreach (var npc in _npcs.Values)
        {
            if (!npc.IsAlive || npc.Thing.ThingIndex == shooterThing)
                continue;
            if (shooterIsEnemy && npc.Brain.Class.HasInstinct("PrimaryFire"))
                continue;

            Vector3 npcCenter = npc.Thing.Position + new Vector3(0f, 0f, 0.10f);
            float hitRadius = MathF.Max(npc.Radius * 1.6f, 0.09f) + projectileRadius;
            if (DistanceSqPointToSegment(npcCenter, originPos, targetSpawnPos, out _) <= hitRadius * hitRadius)
                return true;
        }

        foreach (var targetPlayer in _players)
        {
            if (targetPlayer.Inventory.IsDead || targetPlayer.ThingIndex == shooterThing)
                continue;

            Vector3 targetTorso = GetPlayerWorldPos(targetPlayer) + new Vector3(0f, 0f, 0.13f);
            float hitRadius = 0.08f + projectileRadius;
            if (DistanceSqPointToSegment(targetTorso, originPos, targetSpawnPos, out _) <= hitRadius * hitRadius)
                return true;
        }

        for (int thingId = 0; thingId < _level.Things.Count; thingId++)
        {
            if (thingId == shooterThing || !IsDamagableProp(thingId))
                continue;

            Vector3 propCenter = _level.Things[thingId].Position;
            float hitRadius = GetPropHitRadius(thingId);
            if (DistanceSqPointToSegment(propCenter, originPos, targetSpawnPos, out _) <= hitRadius * hitRadius)
                return true;
        }

        return false;
    }

    private (Vector3 Right, Vector3 Forward, Vector3 Up) GetThingBasis(int thingId)
    {
        Vector3 forward = GetThingForward(thingId);
        forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitY;

        Vector3 right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;

        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        return (right, forward, up);
    }

    private Vector3 ParseTemplateVector3(string templateName, string paramKey, Vector3 fallback)
    {
        string? raw = ResolveTemplateParam(templateName, paramKey);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        string cleaned = raw.Trim().Trim('(', ')', '\'', '"');
        string[] parts = cleaned.Split(['/', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 &&
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            return new Vector3(x, y, z);
        }

        return fallback;
    }

    private void TriggerExplosion(
        Vector3 position,
        string? explosionTemplate,
        int ownerThingIndex,
        int directHitThingId = -1,
        string fallbackSound = "ricochet01.wav")
    {
        if (string.IsNullOrWhiteSpace(explosionTemplate) ||
            explosionTemplate.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            SpawnSparks(position, Vector3.UnitZ, count: 10);
            _audio?.PlaySoundPos(fallbackSound, position, 0.75f, 0.5f, 6.0f);
            return;
        }

        string tmpl = explosionTemplate.Trim();
        string? tmplType = ResolveTemplateParam(tmpl, "type");
        if (string.Equals(tmplType, "particle", StringComparison.OrdinalIgnoreCase))
        {
            SpawnParticleCloudFromTemplate(tmpl, position, fallbackRadius: 0.35f);
            _audio?.PlaySoundPos(fallbackSound, position, 0.75f, 0.5f, 6.0f);
            return;
        }

        float damage = ParseTemplateFloat(tmpl, "damage", 0f);
        float range = ParseTemplateFloat(tmpl, "range", 0f);
        float force = ParseTemplateFloat(tmpl, "force", 0f);
        int damageClass = (int)ParseTemplateHexOrInt(tmpl, "damageclass", 4);
        float duration = ParseTemplateFloat(tmpl, "blasttime", ParseTemplateFloat(tmpl, "timer", 0.5f));
        if (duration <= 0f)
            duration = 0.5f;
        float maxLight = ParseTemplateFloat(tmpl, "maxlight", 0.5f);

        SpawnVisualEffectFromTemplate(tmpl, position, duration, maxLight);

        string? createThing = ResolveTemplateParam(tmpl, "creatething");
        if (!string.IsNullOrWhiteSpace(createThing) && !createThing.Equals("none", StringComparison.OrdinalIgnoreCase))
            SpawnParticleCloudFromTemplate(createThing, position, fallbackRadius: range > 0f ? range : 0.45f, fallbackDuration: duration);

        if (range > 0f && (damage > 0f || force > 0f))
            ApplyExplosionSplashDamage(position, range, damage, force, damageClass, ownerThingIndex, directHitThingId);

        string? soundClassName = ResolveTemplateParam(tmpl, "soundclass");
        if (!string.IsNullOrWhiteSpace(soundClassName) &&
            !soundClassName.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            TryResolveSoundClassEntry(soundClassName, SoundEvent.Create, out var sndEntry) &&
            sndEntry.HasSound)
        {
            float vol = sndEntry.MaxVolume > 0f ? sndEntry.MaxVolume : 1.0f;
            float minR = sndEntry.MinRadius > 0f ? sndEntry.MinRadius : 0.8f;
            float maxR = sndEntry.MaxRadius > minR ? sndEntry.MaxRadius : 10.0f;
            _audio?.PlaySoundPos(sndEntry.SoundFileName, position, vol, minR, maxR);
        }
        else if (damage > 0f && range >= 0.25f)
        {
            _audio?.PlaySoundPos("ThermalExpl01.wav", position, 0.95f, 0.8f, 12.0f);
        }
        else
        {
            _audio?.PlaySoundPos(fallbackSound, position, 0.75f, 0.5f, 6.0f);
        }

        SpawnSparks(position, Vector3.UnitZ, count: range > 0.2f ? 16 : 8);
    }

    private void SpawnParticleCloudFromTemplate(
        string templateName,
        Vector3 position,
        float fallbackRadius = 0.45f,
        float fallbackDuration = 0.5f)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            return;

        string tmpl = templateName.Trim();
        float timer = ParseTemplateFloat(tmpl, "timer", fallbackDuration);
        if (timer <= 0f) timer = MathF.Max(0.25f, fallbackDuration);
        float tmplRange = ParseTemplateFloat(tmpl, "range", 0f);
        float expandRadius = MathF.Max(fallbackRadius, tmplRange);
        int count = Math.Clamp((int)ParseTemplateFloat(tmpl, "count", 48f), 16, 96);
        float pitchSpread = ParseTemplateFloat(tmpl, "pitchrange", 360f);
        float elemSize = MathF.Max(0.015f, ParseTemplateFloat(tmpl, "elementsize", 0.012f));
        string? matName = ResolveTemplateParam(tmpl, "material");

        Vector3 tint = ResolveParticleTint(tmpl);
        var ringFx = new VisualEffect
        {
            Kind = VisualEffectKind.ParticleRing,
            Position = position,
            SpriteName = tmpl,
            MaterialName = !string.IsNullOrWhiteSpace(matName) ? matName : "dusty.mat",
            Width = MathF.Max(0.03f, elemSize * 2.5f),
            Height = MathF.Max(0.03f, elemSize * 2.5f),
            Duration = MathF.Max(0.28f, timer * 2.5f),
            MaxLight = 0.5f,
            ColorTint = tint,
            IsAdditive = true,
            ExpandRadius = expandRadius,
            ParticleCount = count,
            PitchSpreadDeg = pitchSpread
        };
        _visualEffects.Add(ringFx);
        OnVisualEffectSpawned?.Invoke(ringFx);
    }

    private static Vector3 ResolveParticleTint(string templateName)
    {
        string low = templateName.ToLowerInvariant();
        if (low.Contains("conc")) return new Vector3(0.32f, 0.84f, 1.0f);
        if (low.Contains("lightning")) return new Vector3(0.58f, 0.85f, 1.0f);
        if (low.Contains("dest")) return new Vector3(0.85f, 1.0f, 0.45f);
        if (low.Contains("fire")) return new Vector3(1.0f, 0.56f, 0.14f);
        if (low.Contains("saber")) return new Vector3(1.0f, 0.90f, 0.55f);
        return new Vector3(0.85f, 0.80f, 0.72f);
    }

    private bool TryResolveSoundClassEntry(
        string soundClassName,
        SoundEvent evt,
        [NotNullWhen(true)] out SoundClassEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(soundClassName))
            return false;

        if (!_sndCache.TryGetValue(soundClassName, out var snd))
        {
            string[] candidates =
            [
                $"misc/snd/{soundClassName}",
                $"sound/{soundClassName}",
                soundClassName
            ];

            foreach (string path in candidates)
            {
                string? resolved = _vfs.FileExists(path) ? path : (_vfs.FileExists(path.ToLowerInvariant()) ? path.ToLowerInvariant() : null);
                if (resolved != null)
                {
                    try
                    {
                        snd = SndReader.Parse(_vfs.ReadAllText(resolved), soundClassName);
                        break;
                    }
                    catch
                    {
                    }
                }
            }
            _sndCache[soundClassName] = snd;
        }

        if (snd == null)
            return false;

        entry = snd.GetFirst(evt);
        if (entry != null && entry.HasSound)
            return true;

        foreach (var candidate in snd.AllEntries)
        {
            if (candidate.HasSound)
            {
                entry = candidate;
                return true;
            }
        }

        entry = null;
        return false;
    }

    private void SpawnVisualEffectFromTemplate(string templateName, Vector3 position, float duration = -1f, float maxLight = 0.5f)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            return;

        if (duration <= 0f)
        {
            duration = ParseTemplateFloat(templateName, "blasttime", ParseTemplateFloat(templateName, "timer", 0.5f));
            if (duration <= 0f)
                duration = 0.5f;
        }

        string? spriteName = ResolveTemplateParam(templateName, "sprite");
        string? matName = ResolveTemplateParam(templateName, "material");
        float width = 0.35f;
        float height = 0.35f;
        Vector3 finalPos = position;

        if (!string.IsNullOrWhiteSpace(spriteName) && !spriteName.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!_sprCache.TryGetValue(spriteName, out var spr))
            {
                SprSprite.TryLoad(_vfs, spriteName, out spr);
                _sprCache[spriteName] = spr;
            }

            if (spr != null)
            {
                matName = spr.MaterialName;
                width = MathF.Max(0.05f, spr.Width);
                height = MathF.Max(0.05f, spr.Height);
                finalPos += spr.Offset * 0.25f;
            }
        }

        if (string.IsNullOrWhiteSpace(matName))
        {
            if (string.IsNullOrWhiteSpace(spriteName) || spriteName.Equals("none", StringComparison.OrdinalIgnoreCase))
                return;

            string stem = Path.GetFileNameWithoutExtension(spriteName);
            matName = !string.IsNullOrWhiteSpace(stem) ? $"{stem}.mat" : "detx.mat";
        }

        var vfx = new VisualEffect
        {
            Kind = VisualEffectKind.Billboard,
            Position = finalPos,
            SpriteName = spriteName ?? string.Empty,
            MaterialName = matName,
            Width = width,
            Height = height,
            Age = 0f,
            Duration = duration,
            MaxLight = maxLight,
            ColorTint = Vector3.One,
            IsAdditive = true
        };
        _visualEffects.Add(vfx);
        OnVisualEffectSpawned?.Invoke(vfx);
    }

    private void ApplyExplosionSplashDamage(
        Vector3 origin,
        float range,
        float damage,
        float force,
        int damageClass,
        int ownerThingIndex,
        int directHitThingId)
    {
        float invRange = 1.0f / MathF.Max(0.01f, range);

        foreach (var npc in _npcs.Values)
        {
            if (!npc.IsAlive)
                continue;

            Vector3 npcCenter = npc.Thing.Position + new Vector3(0f, 0f, 0.10f);
            bool isDirectHit = npc.Thing.ThingIndex == directHitThingId;
            float dist = isDirectHit ? 0f : Vector3.Distance(origin, npcCenter);
            if (dist > range || (!isDirectHit && !HasLineOfSight(origin, npcCenter)))
                continue;

            float ratio = dist * invRange;
            float falloff = Math.Clamp(1f - ratio * ratio, 0.25f, 1f);
            if (damage > 0f)
                ApplyNpcDamage(npc.Thing.ThingIndex, damage * falloff, origin, damageClass, ownerThingIndex);

            if (force > 0f)
            {
                Vector3 pushDir = dist > 1e-4f ? Vector3.Normalize(npcCenter - origin) : Vector3.UnitZ;
                npc.Velocity += pushDir * (force * 0.02f * falloff) + new Vector3(0f, 0f, 0.6f * falloff);
                npc.IsAirborne = true;
            }
        }

        foreach (var player in _players)
        {
            if (player.Inventory.IsDead || player.ThingIndex < 0 || player.ThingIndex >= _level.Things.Count)
                continue;

            Vector3 pCenter = GetPlayerWorldPos(player) + new Vector3(0f, 0f, 0.10f);
            bool isDirectHit = player.ThingIndex == directHitThingId;
            float dist = isDirectHit ? 0f : Vector3.Distance(origin, pCenter);
            if (dist > range || (!isDirectHit && !HasLineOfSight(origin, pCenter)))
                continue;

            float ratio = dist * invRange;
            float falloff = Math.Clamp(1f - ratio * ratio, 0.25f, 1f);
            if (damage > 0f)
                ApplyDamageToPlayer(player, ownerThingIndex, damage * falloff, "kbodyhit01.wav");

            if (force > 0f)
            {
                Vector3 pushDir = dist > 1e-4f ? Vector3.Normalize(pCenter - origin) : Vector3.UnitZ;
                float mass = MathF.Max(1f, ParseTemplateFloat(_level.Things[player.ThingIndex].TemplateName, "mass", 150f));
                AddThingVel(player.ThingIndex, pushDir * (force * falloff / mass) + new Vector3(0f, 0f, MathF.Min(0.8f, force * 0.002f * falloff)));
            }
        }

        for (int thingId = 0; thingId < _level.Things.Count; thingId++)
        {
            if (!IsDamagableProp(thingId))
                continue;

            Vector3 propPos = _level.Things[thingId].Position;
            float dist = Vector3.Distance(origin, propPos);
            if (dist > range || !HasLineOfSight(origin, propPos))
                continue;

            float ratio = dist * invRange;
            float falloff = Math.Clamp(1f - ratio * ratio, 0.25f, 1f);
            if (damage > 0f && thingId != directHitThingId)
                DispatchThingCogMessageFromSource(thingId, ownerThingIndex, "damaged", new CogValue(damage * falloff), new CogValue(damageClass));
        }
    }

    private void UpdateVisualEffects(float dt)
    {
        for (int i = _visualEffects.Count - 1; i >= 0; i--)
        {
            _visualEffects[i].Age += dt;
            if (_visualEffects[i].Age >= _visualEffects[i].Duration)
                _visualEffects.RemoveAt(i);
        }
    }

    // -------------------------------------------------------------------------
    // Per-Player Items, Force Powers & Bin COG Verbs
    // -------------------------------------------------------------------------

    public bool UseItem(int playerThing, int itemBin)
    {
        if (!IsValidItemBin(itemBin))
            return false;

        if (!TryResolvePlayer(playerThing, out var player))
            return false;
        if (player.Inventory.IsDead || IsActorFrozen(player.ThingIndex))
            return false;
        if (player.BinWaitTimers.GetValueOrDefault(itemBin, 0f) > 0f)
            return false;

        player.SelectedItemBin = itemBin;
        var cog = GetOrCreatePlayerBinCog(player, itemBin);
        if (cog == null)
            return false;

        if (cog.Program.Labels.ContainsKey("activated"))
        {
            WithPlayerCogContext(player.ThingIndex, () =>
                SafeSendMessage(cog, "activated", senderRef: itemBin, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
        }
        return true;
    }

    internal static bool IsValidWeaponBin(int bin) =>
        bin >= PlayerInventory.BinFists && bin <= PlayerInventory.BinLightsaber;

    internal static bool IsValidItemBin(int bin) =>
        (bin >= 40 && bin <= 51) || bin == PlayerInventory.BinShields;

    internal static bool IsValidForceBin(int bin) =>
        bin >= PlayerInventory.BinForceJump && bin <= PlayerInventory.BinForceDeadlySight;

    public bool BeginForcePower(int playerThing, int forceBin, Vector3 aimDir)
    {
        if (!IsValidForceBin(forceBin))
            return false;

        if (!TryResolvePlayer(playerThing, out var player))
            return false;

        if (aimDir.LengthSquared() > 1e-6f)
        {
            player.AimDirection = Vector3.Normalize(aimDir);
            player.HasAimDirection = true;
        }

        if (player.Inventory.IsDead || IsActorFrozen(player.ThingIndex))
            return false;
        if (player.Inventory.GetInv(forceBin) <= 0f)
            return false;
        if (player.BinWaitTimers.GetValueOrDefault(forceBin, 0f) > 0f)
            return false;

        player.SelectedForceBin = forceBin;
        var cog = GetOrCreatePlayerBinCog(player, forceBin);
        if (cog != null && cog.Program.Labels.ContainsKey("activated"))
        {
            WithPlayerCogContext(player.ThingIndex, () =>
                SafeSendMessage(cog, "activated", senderRef: forceBin, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
        }

        bool pulledAny = false;
        if (cog == null && forceBin == PlayerInventory.BinForcePull)
            pulledAny = ApplyForcePullToNearbyItems(player, aimDir);

        return cog != null || pulledAny;
    }

    public bool EndForcePower(int playerThing, int forceBin)
    {
        if (!IsValidForceBin(forceBin))
            return false;

        if (!TryResolvePlayer(playerThing, out var player))
            return false;

        var cog = GetOrCreatePlayerBinCog(player, forceBin);
        if (cog == null || !cog.Program.Labels.ContainsKey("deactivated"))
            return false;

        WithPlayerCogContext(player.ThingIndex, () =>
            SafeSendMessage(cog, "deactivated", senderRef: forceBin, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
        return true;
    }

    /// <summary>
    /// Backwards-compatible Force power activation entry point for the local player.
    /// </summary>
    public bool ActivateForcePower(int forceBin, Vector3 aimDir)
    {
        if (BeginForcePower(_localPlayerThingIndex, forceBin, aimDir))
            return true;

        if (forceBin == PlayerInventory.BinForcePull &&
            TryResolvePlayer(_localPlayerThingIndex, out var localPlayer) &&
            !localPlayer.Inventory.IsDead &&
            !IsActorFrozen(localPlayer.ThingIndex) &&
            GetOrCreatePlayerBinCog(localPlayer, forceBin) == null)
        {
            return ApplyForcePullToNearbyItems(localPlayer, aimDir);
        }

        return false;
    }

    private bool ApplyForcePullToNearbyItems(PlayerState player, Vector3 aimDir)
    {
        int playerThing = player.ThingIndex;
        if (playerThing < 0 || playerThing >= _level.Things.Count)
            return false;

        Vector3 playerPos = GetThingPos(playerThing);
        Vector3 forward = aimDir.LengthSquared() > 1e-6f ? Vector3.Normalize(aimDir) : player.AimDirection;
        Vector3 eyeOffset = new(0f, 0f, 0.12f);
        const float maxPullDist = 8.0f;
        const float minViewDot = 0.5f;
        bool pulledAny = false;

        for (int i = 0; i < _level.Things.Count; i++)
        {
            if (_playersByThing.ContainsKey(i) || _destroyedThings.Contains(i) ||
                _thingMotions.ContainsKey(i) || _npcs.ContainsKey(i))
                continue;

            var thing = _level.Things[i];
            if (thing.IsPlayerSpawn ||
                (thing.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0)
                continue;

            string? type = ResolveTemplateParam(thing.TemplateName, "type");
            string? itemCog = ResolveTemplateParam(thing.TemplateName, "cog");
            bool isPullable = string.Equals(type, "item", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "weapon", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(itemCog) && itemCog.StartsWith("pow_", StringComparison.OrdinalIgnoreCase));

            if (!isPullable)
                continue;

            Vector3 toThing = thing.Position - playerPos;
            float dist = toThing.Length();
            if (dist <= 0.01f || dist > maxPullDist)
                continue;

            if (Vector3.Dot(forward, toThing / dist) < minViewDot)
                continue;

            if (!HasLineOfSight(playerPos + eyeOffset, thing.Position + eyeOffset))
                continue;

            Vector3 pullDir = Vector3.Normalize(playerPos - thing.Position);
            SetThingVel(i, pullDir * 3.5f + new Vector3(0f, 0f, 0.8f));
            pulledAny = true;
        }

        return pulledAny;
    }

    public void ActivateBin(int playerThing, float delay, int binIdx)
    {
        if (!TryResolvePlayer(playerThing, out var player))
            return;

        SetInvActivated(player.ThingIndex, binIdx, 1);
        player.ActiveBinStartTime[binIdx] = _elapsedLevelTime;

        if (float.IsFinite(delay) && delay > 0f)
        {
            player.ActiveBinFireTimers[binIdx] = (delay, delay);
            var cog = GetOrCreatePlayerBinCog(player, binIdx);
            if (cog != null && cog.Program.Labels.ContainsKey("fire"))
            {
                WithPlayerCogContext(player.ThingIndex, () =>
                    SafeSendMessage(cog, "fire", senderRef: binIdx, senderId: 0, sourceRef: player.ThingIndex, senderType: 3));
            }
        }
        else
        {
            player.ActiveBinFireTimers.Remove(binIdx);
        }
    }

    public float DeactivateBin(int playerThing, int binIdx)
    {
        if (!TryResolvePlayer(playerThing, out var player))
            return 0f;

        player.ActiveBinFireTimers.Remove(binIdx);
        bool wasActive = IsInvActivated(player.ThingIndex, binIdx) != 0;
        SetInvActivated(player.ThingIndex, binIdx, 0);
        if (player.ActiveBinStartTime.Remove(binIdx, out float startTime))
            return MathF.Max(0f, _elapsedLevelTime - startTime);

        return wasActive ? 0.05f : 0f;
    }

    public int IsInvActivated(int playerThing, int binIdx) =>
        TryResolvePlayer(playerThing, out var player) ? player.InvActivated.GetValueOrDefault(binIdx, 0) : 0;

    public void SetInvActivated(int playerThing, int binIdx, int activated)
    {
        if (TryResolvePlayer(playerThing, out var player))
            player.InvActivated[binIdx] = activated != 0 ? 1 : 0;
    }

    public void SetInvAvailable(int playerThing, int binIdx, int available)
    {
        if (TryResolvePlayer(playerThing, out var player))
            player.Inventory.SetInvAvailable(binIdx, available != 0);
    }

    public int IsInvAvailable(int playerThing, int binIdx) =>
        TryResolvePlayer(playerThing, out var player) && player.Inventory.IsInvAvailable(binIdx) ? 1 : 0;

    public void SetBinWait(int playerThing, int binIdx, float wait)
    {
        if (!TryResolvePlayer(playerThing, out var player))
            return;

        if (float.IsFinite(wait) && wait > 0f)
            player.BinWaitTimers[binIdx] = wait;
        else
            player.BinWaitTimers.Remove(binIdx);
    }

    public float SkillTarget(int targetThing, int sourceThing, float param0, float param1)
    {
        if (targetThing < 0 || targetThing >= _level.Things.Count)
        {
            targetThing = FirstThingInView(sourceThing, 30f, 12f, 0);
            if (targetThing < 0)
                return -1f;
        }

        if (_playersByThing.TryGetValue(targetThing, out var targetPlayer))
        {
            var classCog = GetOrCreatePlayerClassCog(targetPlayer);
            if (classCog != null && classCog.Program.Labels.ContainsKey("skill"))
            {
                CogValue ret = WithPlayerCogContext(targetPlayer.ThingIndex, () =>
                    SafeSendMessageWithReturn(
                        classCog,
                        "skill",
                        senderRef: targetThing,
                        senderId: 0,
                        sourceRef: sourceThing,
                        senderType: 3,
                        parameters: [new CogValue(param0), new CogValue(param1)]));
                if (ret.Type is CogValueType.Int or CogValueType.Float)
                    return ret.AsFloat;
            }
        }

        int bin = (int)MathF.Round(param0);
        if (bin == PlayerInventory.BinForcePull)
        {
            Vector3 dir = GetThingPos(sourceThing) - GetThingPos(targetThing);
            Vector3 pullDir = dir.LengthSquared() > 1e-5f ? Vector3.Normalize(dir) : Vector3.Zero;
            SetThingVel(targetThing, pullDir * 3.5f + new Vector3(0f, 0f, 0.8f));
        }
        else if (bin == PlayerInventory.BinForceBlinding && _npcs.TryGetValue(targetThing, out var blindedNpc) && blindedNpc.IsAlive)
        {
            blindedNpc.BlindTimer = Math.Clamp(param1 * 1.5f, 1.5f, 6f);
        }
        else if (bin == PlayerInventory.BinForceGrip && _npcs.TryGetValue(targetThing, out var grippedNpc) && grippedNpc.IsAlive)
        {
            ApplyNpcDamage(targetThing, MathF.Max(10f, param1 * 8f), GetThingPos(sourceThing), damageType: 8, attackerThingId: sourceThing);
            grippedNpc.HitAnimTimer = 0.5f;
        }

        return 1f;
    }

    // -------------------------------------------------------------------------
    // Multi-Player / Dedicated Server COG Context Verbs
    // -------------------------------------------------------------------------

    public int IsServer() => 1;

    public int GetNumPlayers() => _players.Count;

    public int GetPlayerThing(int playerIdx) =>
        playerIdx >= 0 && playerIdx < _players.Count ? _players[playerIdx].ThingIndex : -1;

    public int GetPlayerTeam(int playerThing) =>
        TryResolvePlayer(playerThing, out var player) ? player.Team : 0;

    public void SetPlayerTeam(int playerThing, int team)
    {
        if (TryResolvePlayer(playerThing, out var player))
            player.Team = team;
    }

    public int GetPlayerScore(int playerThing) =>
        TryResolvePlayer(playerThing, out var player) ? player.Score : 0;

    public void SetPlayerScore(int playerThing, int score)
    {
        if (TryResolvePlayer(playerThing, out var player))
            player.Score = score;
    }

    public int GetPlayerKills(int playerThing) =>
        TryResolvePlayer(playerThing, out var player) ? player.Kills : 0;

    public int GetPlayerKilled(int playerThing) =>
        TryResolvePlayer(playerThing, out var player) ? player.Killed : 0;

    public int GetPlayerSuicides(int playerThing) =>
        TryResolvePlayer(playerThing, out var player) ? player.Suicides : 0;
}
