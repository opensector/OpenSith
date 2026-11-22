using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSith.Core.Cog;
using OpenSith.Core.Data;
using OpenSith.Core.Simulation;
using OpenSith.Core.World;

namespace OpenSith.Core.AI;

/// <summary>
/// Skill tier controlling bot reaction speed, aim precision, and tactical discipline.
/// </summary>
public enum BotSkill
{
    Easy = 0,
    Medium = 1,
    Hard = 2
}

/// <summary>
/// Personality/loadout preset for a Deathmatch AI bot.
/// </summary>
public readonly record struct BotArchetype(
    string Name,
    string PreferredModelHint,
    int SaberColorIndex,
    int FavoriteWeaponBin);

/// <summary>
/// Output control frame produced by <see cref="DmBotController.Decide"/> and fed directly
/// into the server's physics + simulation pipeline just like a remote UDP player's input.
/// </summary>
public struct BotTickDecision
{
    public Vector3 WishMoveDir;
    public bool WishJump;
    public Vector3 AimDirection;
    public float Pitch;
    public float Yaw;
    public Vector3 LocalThrust;
    public int DesiredWeaponBin;
    public bool PrimaryFireHeld;
    public bool SecondaryFireHeld;
    public bool UsePressed;
    public int DesiredForceBin;
}

/// <summary>
/// Tactical Deathmatch AI controller supporting all 10 Jedi Knight weapons (Fists through Lightsaber),
/// sector-portal navigation, pickup gathering, stuck recovery, explosive safety, and Force powers.
/// </summary>
public sealed class DmBotController
{
    public static readonly IReadOnlyList<BotArchetype> DefaultArchetypes =
    [
        new("Bot_Kyle",   "Katarn",   2, PlayerInventory.BinBryar),
        new("Bot_Fett",   "Boba",     3, PlayerInventory.BinConcRifle),
        new("Bot_Greedo", "Greedo",   2, PlayerInventory.BinRepeater),
        new("Bot_Jan",    "Jan",      0, PlayerInventory.BinStRifle),
        new("Bot_Yun",    "Yun",      6, PlayerInventory.BinLightsaber),
        new("Bot_Sariss", "Sariss",   4, PlayerInventory.BinBowcaster),
        new("Bot_Jerec",  "Jerec",    4, PlayerInventory.BinRailDet),
        new("Bot_Boc",    "Boc",      5, PlayerInventory.BinLightsaber),
    ];

    private readonly Random _rng;
    private readonly List<Vector3> _waypoints = [];
    private readonly List<bool> _waypointJumps = [];

    private int _waypointIndex;
    private int _roamGoalSector = -1;
    private float _repathTimer;
    private float _strafeTimer;
    private float _strafeSign = 1f;

    private int _trackedTargetThingId = -1;
    private Vector3 _prevTargetPos;
    private Vector3 _targetVelocity;
    private bool _hasPrevTargetPos;

    private Vector3 _aimJitterOffset;
    private float _aimJitterTimer;

    private Vector3 _lastProgressPos;
    private float _stuckTimer;
    private float _stuckSidestepTimer;
    private float _stuckSidestepSign = 1f;

    // Weapon state machines (charge/release for TD & Bowcaster; burst timing for Repeater; plant cooldown for Seq)
    private int _lastRequestedWeaponBin = -1;
    private float _chargeElapsed;
    private float _chargeTarget;
    private int _chargeMode;
    private bool _bowUseSecondaryNext;
    private float _burstElapsed;
    private float _burstTarget;
    private float _burstPauseRemaining;
    private float _saberSwingingTimer;
    private float _saberPauseRemaining;
    private float _seqDropCooldown;
    private Vector3 _lastPlantedTrapPos;
    private float _trapAvoidUntil;
    private float _forceCooldown;
    private float _jumpCooldown;
    private float _reactionTimer;
    private float _losLostTimer = 10f;
    private int _lockedShotMode;
    private float _weaponSwitchCooldown;
    private int _tacticalBiasWeaponBin = -1;
    private float _tacticalBiasTimer;

    public BotArchetype Archetype { get; }
    public BotSkill Skill { get; }

    public DmBotController(BotArchetype archetype, BotSkill skill = BotSkill.Medium, Random? rng = null)
    {
        Archetype = archetype;
        Skill = skill;
        _rng = rng ?? Random.Shared;
        _forceCooldown = 2.0f;
        _tacticalBiasTimer = 15f + (float)_rng.NextDouble() * 7f;
    }

    public void ResetForRespawn(Vector3 spawnPos)
    {
        _waypoints.Clear();
        _waypointJumps.Clear();
        _waypointIndex = 0;
        _roamGoalSector = -1;
        _repathTimer = 0f;
        _trackedTargetThingId = -1;
        _hasPrevTargetPos = false;
        _targetVelocity = Vector3.Zero;
        _lastProgressPos = spawnPos;
        _stuckTimer = 0f;
        _stuckSidestepTimer = 0f;
        _chargeElapsed = 0f;
        _chargeTarget = 0f;
        _burstElapsed = 0f;
        _burstTarget = 0f;
        _burstPauseRemaining = 0f;
        _saberSwingingTimer = 0f;
        _saberPauseRemaining = 0f;
        _seqDropCooldown = 0f;
        _trapAvoidUntil = 0f;
        _forceCooldown = 3.0f;
        _jumpCooldown = 0f;
        _reactionTimer = 0f;
        _losLostTimer = 10f;
        _lockedShotMode = 0;
        _weaponSwitchCooldown = 0f;
        _tacticalBiasWeaponBin = -1;
        _tacticalBiasTimer = 15f + (float)_rng.NextDouble() * 7f;
    }

    /// <summary>
    /// Returns true if <paramref name="inv"/> holds both ownership (or BinFists) and sufficient ammo
    /// for at least one firing mode of <paramref name="weaponBin"/>.
    /// </summary>
    public static bool HasUsableAmmo(PlayerInventory inv, int weaponBin)
    {
        if (weaponBin == PlayerInventory.BinFists)
            return true;
        if (inv.GetInv(weaponBin) <= 0f)
            return false;

        return weaponBin switch
        {
            PlayerInventory.BinBryar => inv.EnergyAmmo >= 1f,
            PlayerInventory.BinStRifle => inv.EnergyAmmo >= 2f,
            PlayerInventory.BinThermDet => inv.GetInv(PlayerInventory.BinThermDet) >= 1f,
            PlayerInventory.BinBowcaster => inv.PowerAmmo >= 2f,
            PlayerInventory.BinRepeater => inv.PowerAmmo >= 1f,
            PlayerInventory.BinRailDet => inv.RailCharges >= 1f,
            PlayerInventory.BinSeqCharge => inv.GetInv(PlayerInventory.BinSeqCharge) >= 1f,
            PlayerInventory.BinConcRifle => inv.PowerAmmo >= 4f,
            PlayerInventory.BinLightsaber => true,
            _ => false
        };
    }

    /// <summary>
    /// Scores all 10 weapons cleanly based on target range, LOS, ammo reserves, self-splash safety,
    /// and bot archetype preference.
    /// </summary>
    public int SelectBestWeapon(
        PlayerInventory inv,
        float targetDist,
        bool hasLos,
        float powerBoost = 1.0f)
    {
        int bestBin = PlayerInventory.BinFists;
        float bestScore = 5f;
        bool hasSafeAlternative = false;

        for (int bin = PlayerInventory.BinBryar; bin <= PlayerInventory.BinLightsaber; bin++)
        {
            if (!HasUsableAmmo(inv, bin))
                continue;

            bool isPointBlankSplashRisk =
                (bin == PlayerInventory.BinThermDet && targetDist < 0.65f) ||
                (bin == PlayerInventory.BinRailDet && targetDist < 0.70f) ||
                (bin == PlayerInventory.BinSeqCharge && targetDist < 0.55f);

            if (!isPointBlankSplashRisk)
                hasSafeAlternative = true;
        }

        for (int bin = PlayerInventory.BinFists; bin <= PlayerInventory.BinLightsaber; bin++)
        {
            if (!HasUsableAmmo(inv, bin))
                continue;

            float score = ScoreWeaponForSituation(bin, inv, targetDist, hasLos, hasSafeAlternative, powerBoost);
            if (bin == Archetype.FavoriteWeaponBin && score > 0f)
            {
                score += 220f;
                if (bin == PlayerInventory.BinLightsaber && targetDist <= 2.6f)
                    score += 180f;
            }
            else if (bin == _tacticalBiasWeaponBin && score > 0f)
            {
                score += 90f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestBin = bin;
            }
        }

        return bestBin;
    }

    public BotTickDecision Decide(
        float dt,
        WorldSimulation sim,
        PlayerState selfPlayer,
        JklThing selfThing,
        SectorPortalNavGraph navGraph,
        IReadOnlyList<WorldSimulation.PickupInfo> activePickups)
    {
        ArgumentNullException.ThrowIfNull(sim);
        ArgumentNullException.ThrowIfNull(selfPlayer);
        ArgumentNullException.ThrowIfNull(selfThing);
        ArgumentNullException.ThrowIfNull(navGraph);
        ArgumentNullException.ThrowIfNull(activePickups);

        Vector3 selfPos = selfThing.Position;
        Vector3 eyePos = selfPos + new Vector3(0f, 0f, 0.155f);
        int selfSector = Math.Clamp(selfThing.SectorIndex, 0, Math.Max(0, navGraph.SectorCount - 1));
        float levelTime = sim.GetLevelTime();

        AdvanceTimers(dt, selfPos);

        var decision = new BotTickDecision
        {
            DesiredWeaponBin = selfPlayer.CurWeaponBin
        };

        // 1. Target Acquisition
        PlayerState? bestEnemy = FindBestEnemy(sim, selfPlayer, eyePos, out Vector3 enemyPos, out Vector3 enemyCenter, out float enemyDist, out bool hasLos);
        int prevTrackedTargetId = _trackedTargetThingId;
        UpdateTrackedTargetVelocity(dt, bestEnemy, enemyPos);

        if (bestEnemy != null && hasLos)
        {
            if (_losLostTimer >= 0.6f || prevTrackedTargetId != bestEnemy.ThingIndex)
            {
                _reactionTimer = Skill switch
                {
                    BotSkill.Easy => 0.42f,
                    BotSkill.Medium => 0.24f,
                    _ => 0.10f
                };
            }
            _losLostTimer = 0f;
        }
        else if (_tacticalBiasTimer <= 0f)
        {
            RefreshTacticalBiasWeapon(selfPlayer.Inventory);
        }

        // 2. Choose weapon across all 10 bins with switch hysteresis
        float boost = MathF.Max(1f, selfPlayer.Inventory.GetInv(PlayerInventory.BinPowerBoost));
        float evalDist = bestEnemy != null ? enemyDist : 2.5f;
        int chosenWeapon = SelectBestWeapon(selfPlayer.Inventory, evalDist, hasLos, boost);
        int curBin = selfPlayer.CurWeaponBin;
        bool curHasSplashRisk =
            (curBin == PlayerInventory.BinThermDet && evalDist < 0.65f) ||
            (curBin == PlayerInventory.BinRailDet && evalDist < 0.70f) ||
            (curBin == PlayerInventory.BinSeqCharge && evalDist < 0.55f);
        if (_lastRequestedWeaponBin >= 0 &&
            _weaponSwitchCooldown > 0f &&
            WorldSimulation.IsValidWeaponBin(curBin) &&
            HasUsableAmmo(selfPlayer.Inventory, curBin) &&
            !curHasSplashRisk)
        {
            chosenWeapon = curBin;
        }
        decision.DesiredWeaponBin = chosenWeapon;

        if (_lastRequestedWeaponBin != chosenWeapon)
        {
            _lastRequestedWeaponBin = chosenWeapon;
            _weaponSwitchCooldown = 2.0f;
            _chargeElapsed = 0f;
            _chargeTarget = 0f;
            _burstElapsed = 0f;
            _burstTarget = 0f;
            _burstPauseRemaining = 0f;
            _lockedShotMode = 0;
            _saberSwingingTimer = 0f;
            _saberPauseRemaining = 0f;
            // Ensure at least 1 released tick when swapping away from held trigger weapons
            return FinalizePassiveOrientation(decision, selfThing.PitchYawRoll, selfPos, enemyPos, hasLos);
        }

        // 3. Evaluate Trap Retreat & Pickups
        bool avoidingOwnTrap = levelTime < _trapAvoidUntil &&
            Vector2.Distance(new Vector2(selfPos.X, selfPos.Y), new Vector2(_lastPlantedTrapPos.X, _lastPlantedTrapPos.Y)) < 0.72f;

        bool urgentPickup = selfPlayer.Inventory.Health < 55f ||
                            selfPlayer.Inventory.Shields < 40f ||
                            chosenWeapon == PlayerInventory.BinFists;

        WorldSimulation.PickupInfo? desiredPickup = SelectDesiredPickup(
            selfPlayer.Inventory, selfSector, selfPos, urgentPickup || bestEnemy == null || !hasLos, navGraph, activePickups);

        // 4. Movement & Navigation
        bool wantJump = false;
        Vector3 wishMove = Vector3.Zero;

        if (avoidingOwnTrap)
        {
            Vector3 away = selfPos - _lastPlantedTrapPos;
            away.Z = 0f;
            wishMove = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitY;
        }
        else if (bestEnemy != null && hasLos && (!urgentPickup || enemyDist < 2.2f))
        {
            wishMove = ComputeCombatMovement(chosenWeapon, selfPos, enemyPos, enemyDist);
        }
        else if (desiredPickup is { } pickup)
        {
            wishMove = FollowNavPath(selfSector, selfPos, pickup.SectorIndex, pickup.Position, selfPlayer.IsGrounded, selfPlayer.IsUnderwater, navGraph, ref wantJump);
        }
        else if (bestEnemy != null)
        {
            int enemySector = bestEnemy.ThingIndex >= 0 ? sim.GetThingSector(bestEnemy.ThingIndex) : selfSector;
            if (enemySector < 0) enemySector = selfSector;
            wishMove = FollowNavPath(selfSector, selfPos, enemySector, enemyPos, selfPlayer.IsGrounded, selfPlayer.IsUnderwater, navGraph, ref wantJump);
        }
        else
        {
            if (_roamGoalSector < 0 || _roamGoalSector == selfSector || _repathTimer <= 0f)
                _roamGoalSector = navGraph.PickRoamSector(selfSector, _rng);

            if (navGraph.TryGetSectorCenter(_roamGoalSector, out Vector3 roamCenter))
                wishMove = FollowNavPath(selfSector, selfPos, _roamGoalSector, roamCenter, selfPlayer.IsGrounded, selfPlayer.IsUnderwater, navGraph, ref wantJump);
        }

        // Stuck recovery: sidestep horizontally and pulse jump/use only on fresh stuck transitions
        float movedHoriz = Vector2.Distance(new Vector2(selfPos.X, selfPos.Y), new Vector2(_lastProgressPos.X, _lastProgressPos.Y));
        if (movedHoriz > 0.08f || wishMove.LengthSquared() <= 0.01f)
        {
            _lastProgressPos = selfPos;
            _stuckTimer = 0f;
        }
        else
        {
            _stuckTimer += dt;
        }

        if (_stuckSidestepTimer > 0f && wishMove.LengthSquared() > 1e-4f)
        {
            Vector3 side = Vector3.Normalize(Vector3.Cross(wishMove, Vector3.UnitZ)) * _stuckSidestepSign;
            wishMove = Vector3.Normalize(wishMove * 0.45f + side * 0.85f);
        }

        if (_stuckTimer >= 0.75f)
        {
            _stuckTimer = 0f;
            _stuckSidestepTimer = 0.45f;
            _stuckSidestepSign = -_stuckSidestepSign;
            _repathTimer = 0f;
            _roamGoalSector = -1;
            _waypoints.Clear();
            if (selfPlayer.IsGrounded && _jumpCooldown <= 0f)
                wantJump = true;
            decision.UsePressed = true;
        }

        // 5. Aiming (with projectile lead + ballistic pitch + skill jitter)
        Vector3 lookTarget = hasLos && bestEnemy != null
            ? ComputeAimPoint(chosenWeapon, eyePos, enemyCenter, enemyDist)
            : (wishMove.LengthSquared() > 1e-4f ? eyePos + wishMove * 2.0f : eyePos + Vector3.UnitY);

        Vector3 aimVec = lookTarget - eyePos;
        Vector3 aimDir = aimVec.LengthSquared() > 1e-6f ? Vector3.Normalize(aimVec) : Vector3.UnitY;
        float yaw = -MathF.Atan2(aimDir.X, aimDir.Y) * (180f / MathF.PI);
        float pitch = MathF.Asin(Math.Clamp(aimDir.Z, -0.95f, 0.95f)) * (180f / MathF.PI);

        // 6. Execute Weapon Firing State Machine
        if (bestEnemy != null && selfPlayer.MountWait <= 0.01f)
        {
            ComputeWeaponCombat(
                dt,
                levelTime,
                chosenWeapon,
                selfPlayer.Inventory,
                selfPos,
                enemyDist,
                hasLos,
                avoidingOwnTrap,
                ref decision,
                ref wishMove,
                ref aimDir,
                ref pitch);
        }

        // 7. Opportunistic Force Powers
        decision.DesiredForceBin = SelectForcePower(selfPlayer.Inventory, bestEnemy != null, hasLos, enemyDist, wishMove.LengthSquared() > 0.1f);

        if (wantJump)
        {
            if (_jumpCooldown > 0f || (!selfPlayer.IsGrounded && !selfPlayer.IsUnderwater))
                wantJump = false;
            else
                _jumpCooldown = 1.15f;
        }

        decision.WishMoveDir = wishMove;
        decision.WishJump = wantJump;
        decision.AimDirection = aimDir;
        decision.Yaw = yaw;
        decision.Pitch = pitch;
        decision.LocalThrust = ComputeLocalThrust(wishMove, yaw);
        return decision;
    }

    private float ScoreWeaponForSituation(
        int bin,
        PlayerInventory inv,
        float dist,
        bool hasLos,
        bool hasSafeAlternative,
        float boost)
    {
        switch (bin)
        {
            case PlayerInventory.BinFists:
                return dist <= 0.38f ? 180f : 12f;

            case PlayerInventory.BinLightsaber:
                if (dist <= 0.85f) return 730f;
                if (dist <= 1.75f) return 520f;
                return hasLos ? 300f : 360f;

            case PlayerInventory.BinBryar:
                return (dist > 3.5f ? 590f : 540f) + (hasLos ? 20f : 0f);

            case PlayerInventory.BinStRifle:
                return (dist is >= 0.4f and <= 3.6f ? 580f : 470f) + (hasLos ? 20f : 0f);

            case PlayerInventory.BinRepeater:
            {
                float score = dist > 4.2f
                    ? 440f
                    : (dist is >= 0.6f and <= 3.2f ? 595f : 535f);
                if (boost >= 1.5f) score += 25f;
                if (hasLos) score += 20f;
                if (inv.PowerAmmo < 25f) score -= 60f;
                return score;
            }

            case PlayerInventory.BinBowcaster:
                if (!hasLos && dist < 3.5f)
                    return 610f; // Bounce mode handles corners well
                return dist is >= 0.8f and <= 5.5f ? 605f : 500f;

            case PlayerInventory.BinThermDet:
                if (dist < 0.65f && hasSafeAlternative)
                    return -500f;
                if (dist is >= 0.9f and <= 3.6f)
                    return hasLos ? 550f : 620f;
                return 280f;

            case PlayerInventory.BinRailDet:
                if (dist < 0.70f && hasSafeAlternative)
                    return -500f;
                return (dist >= 1.2f ? 625f : 440f) + (hasLos ? 25f : 10f);

            case PlayerInventory.BinSeqCharge:
                if (_seqDropCooldown > 0f && hasSafeAlternative)
                    return 40f;
                if (dist < 0.50f && hasSafeAlternative)
                    return -400f;
                return dist is >= 0.6f and <= 2.4f ? 430f : 220f;

            case PlayerInventory.BinConcRifle:
                if (dist < 1.15f)
                {
                    // Concussion secondary (+concblast2) is safe at close range!
                    return inv.PowerAmmo >= 4f && hasLos ? 490f : 150f;
                }
                return inv.PowerAmmo >= 16f ? 630f : (inv.PowerAmmo >= 8f ? 560f : 420f);

            default:
                return -1f;
        }
    }

    private void ComputeWeaponCombat(
        float dt,
        float levelTime,
        int weaponBin,
        PlayerInventory inv,
        Vector3 selfPos,
        float dist,
        bool hasLos,
        bool avoidingOwnTrap,
        ref BotTickDecision decision,
        ref Vector3 wishMove,
        ref Vector3 aimDir,
        ref float pitch)
    {
        if (_reactionTimer > 0f &&
            weaponBin != PlayerInventory.BinFists &&
            weaponBin != PlayerInventory.BinLightsaber)
        {
            return;
        }

        switch (weaponBin)
        {
            case PlayerInventory.BinFists:
                if (hasLos && dist <= 0.42f)
                {
                    if (_saberSwingingTimer <= 0f && _saberPauseRemaining <= 0f)
                    {
                        _saberSwingingTimer = 0.28f;
                        _saberPauseRemaining = 0.08f;
                    }
                    if (_saberSwingingTimer > 0f)
                    {
                        decision.PrimaryFireHeld = true;
                        _saberSwingingTimer -= dt;
                    }
                    else
                    {
                        _saberPauseRemaining -= dt;
                    }
                }
                break;

            case PlayerInventory.BinLightsaber:
                if (dist <= 0.70f)
                {
                    if (_saberSwingingTimer <= 0f && _saberPauseRemaining <= 0f)
                    {
                        bool heavy = Skill >= BotSkill.Medium && dist <= 0.40f && _rng.NextDouble() < 0.20;
                        _chargeMode = heavy ? 1 : 0;
                        _saberSwingingTimer = 0.16f;
                        _saberPauseRemaining = heavy ? 0.55f : 0.18f;
                    }
                    if (_saberSwingingTimer > 0f)
                    {
                        if (_chargeMode == 1)
                            decision.SecondaryFireHeld = true;
                        else
                            decision.PrimaryFireHeld = true;
                        _saberSwingingTimer -= dt;
                    }
                    else
                    {
                        _saberPauseRemaining -= dt;
                    }
                }
                break;

            case PlayerInventory.BinBryar:
                if (TryPrepareBurstWindow(dt, hasLos && dist <= 12.0f))
                {
                    if (_burstTarget <= 0f)
                    {
                        _lockedShotMode = 0;
                        _burstTarget = NextSkillRange(0.55f, 0.35f, 0.85f, 0.45f, 1.10f, 0.45f);
                        _burstElapsed = 0f;
                    }
                    StepBurstHold(
                        dt,
                        _burstElapsed + dt >= _burstTarget ? NextSkillRange(0.55f, 0.30f, 0.35f, 0.20f, 0.22f, 0.16f) : 0f,
                        ref decision);
                }
                break;

            case PlayerInventory.BinStRifle:
                if (TryPrepareBurstWindow(dt, hasLos && dist <= 11.0f))
                {
                    if (_burstTarget <= 0f)
                    {
                        _lockedShotMode = (dist < 1.35f && _rng.NextDouble() < 0.45) ? 1 : 0;
                        _burstTarget = NextSkillRange(0.30f, 0.18f, 0.42f, 0.22f, 0.50f, 0.28f);
                        _burstElapsed = 0f;
                    }
                    StepBurstHold(
                        dt,
                        _burstElapsed + dt >= _burstTarget ? NextSkillRange(0.70f, 0.35f, 0.45f, 0.30f, 0.30f, 0.20f) : 0f,
                        ref decision);
                }
                break;

            case PlayerInventory.BinRepeater:
                if (TryPrepareBurstWindow(dt, hasLos && dist <= 10.0f))
                {
                    if (_burstTarget <= 0f)
                    {
                        _lockedShotMode = (dist <= 1.25f && Skill >= BotSkill.Medium && _rng.NextDouble() < 0.35) ? 1 : 0;
                        _burstTarget = _lockedShotMode == 1
                            ? 0.22f
                            : NextSkillRange(0.22f, 0.14f, 0.30f, 0.18f, 0.38f, 0.22f);
                        _burstElapsed = 0f;
                    }
                    // Release between bursts so repeat-1.wav fires and spread resets
                    float pauseOnEnd = 0f;
                    if (_burstElapsed + dt >= _burstTarget)
                    {
                        pauseOnEnd = _lockedShotMode == 1
                            ? 0.65f + (float)_rng.NextDouble() * 0.30f
                            : NextSkillRange(0.75f, 0.35f, 0.50f, 0.30f, 0.35f, 0.20f);
                    }
                    StepBurstHold(dt, pauseOnEnd, ref decision);
                }
                break;

            case PlayerInventory.BinThermDet:
                if (dist < 0.65f)
                {
                    // Point-blank safety: cancel hold or back off without releasing in face
                    _chargeElapsed = 0f;
                    _chargeTarget = 0f;
                    break;
                }
                if (hasLos || dist <= 4.5f)
                {
                    if (_chargeTarget <= 0f)
                    {
                        _chargeMode = hasLos ? 0 : 1; // Mode 0 = impact (+grenade1), Mode 1 = bounce (+grenade2)
                        float minScale = _chargeMode == 0 ? 0.72f : 0.30f;
                        _chargeTarget = Math.Clamp(0.55f + dist * 0.18f, minScale, 1.75f);
                        _chargeElapsed = 0f;
                    }

                    _chargeElapsed += dt;
                    if (_chargeElapsed < _chargeTarget)
                    {
                        if (_chargeMode == 1)
                            decision.SecondaryFireHeld = true;
                        else
                            decision.PrimaryFireHeld = true;
                    }
                    else
                    {
                        // Release to throw detonator!
                        _chargeElapsed = 0f;
                        _chargeTarget = 0f;
                    }
                }
                break;

            case PlayerInventory.BinBowcaster:
                if (hasLos || dist <= 5.0f)
                {
                    if (!hasLos || _bowUseSecondaryNext || dist > 3.8f)
                    {
                        // Mode 1: ricochet energy bolt (+crossbowbolt3)
                        decision.SecondaryFireHeld = true;
                        _burstElapsed += dt;
                        if (_burstElapsed >= 0.7f)
                        {
                            _burstElapsed = 0f;
                            _bowUseSecondaryNext = false;
                        }
                    }
                    else
                    {
                        // Mode 0: hold-and-release multi-bolt spread (+crossbowbolt + 2x +crossbowbolt2)
                        if (_chargeTarget <= 0f)
                        {
                            _chargeTarget = 0.66f + (float)_rng.NextDouble() * 0.25f;
                            _chargeElapsed = 0f;
                        }

                        _chargeElapsed += dt;
                        if (_chargeElapsed < _chargeTarget)
                        {
                            decision.PrimaryFireHeld = true;
                        }
                        else
                        {
                            _chargeElapsed = 0f;
                            _chargeTarget = 0f;
                            _bowUseSecondaryNext = true;
                        }
                    }
                }
                break;

            case PlayerInventory.BinRailDet:
                if (TryPrepareBurstWindow(dt, dist >= 0.70f && (hasLos || dist <= 3.5f)))
                {
                    if (_burstTarget <= 0f)
                    {
                        _lockedShotMode = (!hasLos || _rng.NextDouble() < 0.20) ? 1 : 0;
                        _burstTarget = 0.16f;
                        _burstElapsed = 0f;
                    }
                    StepBurstHold(
                        dt,
                        _burstElapsed + dt >= _burstTarget ? NextSkillRange(1.9f, 0.6f, 1.45f, 0.45f, 1.15f, 0.35f) : 0f,
                        ref decision);
                }
                break;

            case PlayerInventory.BinSeqCharge:
                if (!avoidingOwnTrap && _seqDropCooldown <= 0f && dist is >= 0.45f and <= 3.5f)
                {
                    // Plant Mode 1 (+seqchrg2) proximity mine or Mode 0 (+seqchrg) timed mine
                    bool proximity = !hasLos || dist > 1.3f || _rng.NextDouble() < 0.65;
                    if (proximity)
                        decision.SecondaryFireHeld = true;
                    else
                        decision.PrimaryFireHeld = true;

                    _lastPlantedTrapPos = selfPos;
                    _trapAvoidUntil = levelTime + 4.0f;
                    _seqDropCooldown = 3.5f;
                }
                break;

            case PlayerInventory.BinConcRifle:
                if (TryPrepareBurstWindow(dt, hasLos && dist <= 9.0f))
                {
                    if (_burstTarget <= 0f)
                    {
                        _lockedShotMode = (dist < 1.10f || inv.PowerAmmo < 8f ||
                            (dist <= 3.5f && _targetVelocity.LengthSquared() > 1.5f && _rng.NextDouble() < 0.15))
                            ? 1
                            : 0;
                        _burstTarget = 0.16f;
                        _burstElapsed = 0f;
                    }

                    // Enforce point-blank safety even if enemy rushes mid-window
                    if (dist < 1.10f)
                        _lockedShotMode = 1;

                    StepBurstHold(
                        dt,
                        _burstElapsed + dt >= _burstTarget ? NextSkillRange(2.2f, 0.6f, 1.7f, 0.5f, 1.35f, 0.4f) : 0f,
                        ref decision);
                }
                break;
        }
    }

    private float NextSkillRange(float easyMin, float easySpan, float medMin, float medSpan, float hardMin, float hardSpan)
    {
        float rand = (float)_rng.NextDouble();
        return Skill switch
        {
            BotSkill.Easy => easyMin + rand * easySpan,
            BotSkill.Medium => medMin + rand * medSpan,
            _ => hardMin + rand * hardSpan
        };
    }

    private bool TryPrepareBurstWindow(float dt, bool canFire)
    {
        if (_burstPauseRemaining > 0f)
        {
            _burstPauseRemaining -= dt;
            return false;
        }

        if (!canFire)
        {
            _burstElapsed = 0f;
            _burstTarget = 0f;
            return false;
        }

        return true;
    }

    private void StepBurstHold(float dt, float pauseOnEnd, ref BotTickDecision decision)
    {
        _burstElapsed += dt;
        if (_burstElapsed < _burstTarget)
        {
            if (_lockedShotMode == 1)
                decision.SecondaryFireHeld = true;
            else
                decision.PrimaryFireHeld = true;
        }
        else
        {
            _burstElapsed = 0f;
            _burstTarget = 0f;
            _burstPauseRemaining = pauseOnEnd;
        }
    }

    private Vector3 ComputeCombatMovement(
        int weaponBin,
        Vector3 selfPos,
        Vector3 enemyPos,
        float dist)
    {
        Vector3 toEnemy = enemyPos - selfPos;
        toEnemy.Z = 0f;
        Vector3 fwd = toEnemy.LengthSquared() > 1e-6f ? Vector3.Normalize(toEnemy) : Vector3.UnitY;
        Vector3 tangent = Vector3.Normalize(new Vector3(-fwd.Y, fwd.X, 0f)) * _strafeSign;

        float minDesiredDist;
        float maxDesiredDist;

        switch (weaponBin)
        {
            case PlayerInventory.BinFists:
                minDesiredDist = 0.14f;
                maxDesiredDist = 0.22f;
                break;
            case PlayerInventory.BinLightsaber:
                minDesiredDist = 0.22f;
                maxDesiredDist = 0.35f;
                break;
            case PlayerInventory.BinThermDet:
            case PlayerInventory.BinRailDet:
                minDesiredDist = 0.90f;
                maxDesiredDist = 2.40f;
                break;
            case PlayerInventory.BinSeqCharge:
                minDesiredDist = 1.00f;
                maxDesiredDist = 2.50f;
                break;
            case PlayerInventory.BinConcRifle:
                minDesiredDist = 1.25f;
                maxDesiredDist = 3.20f;
                break;
            default:
                minDesiredDist = 0.65f;
                maxDesiredDist = 2.60f;
                break;
        }

        if (dist < minDesiredDist)
            return Vector3.Normalize(-fwd * 0.85f + tangent * 0.55f);

        if (dist > maxDesiredDist)
            return Vector3.Normalize(fwd * 0.85f + tangent * 0.45f);

        return tangent;
    }

    private Vector3 FollowNavPath(
        int startSector,
        Vector3 selfPos,
        int goalSector,
        Vector3 goalPos,
        bool isGrounded,
        bool isUnderwater,
        SectorPortalNavGraph navGraph,
        ref bool wantJump)
    {
        bool needRepath = _repathTimer <= 0f || _waypoints.Count == 0 || _waypointIndex >= _waypoints.Count;
        if (needRepath)
        {
            _repathTimer = 0.45f;
            _waypointIndex = 0;
            if (!navGraph.FindPath(startSector, selfPos, goalSector, goalPos, _waypoints, _waypointJumps))
            {
                if (startSector != goalSector)
                {
                    _roamGoalSector = -1;
                    var fallbackEdges = navGraph.GetEdges(startSector);
                    if (fallbackEdges.Count > 0)
                        return NormalizeSwimOrFlatDirection(fallbackEdges[0].PortalCentroid - selfPos, isUnderwater);
                    return Vector3.Zero;
                }

                return NormalizeSwimOrFlatDirection(goalPos - selfPos, isUnderwater);
            }
        }

        while (_waypointIndex < _waypoints.Count - 1)
        {
            Vector3 wp = _waypoints[_waypointIndex];
            float horizDist = Vector2.Distance(new Vector2(selfPos.X, selfPos.Y), new Vector2(wp.X, wp.Y));
            if (horizDist <= 0.18f)
                _waypointIndex++;
            else
                break;
        }

        Vector3 activeTarget = _waypoints[Math.Clamp(_waypointIndex, 0, _waypoints.Count - 1)];
        float horizDistToTarget = Vector2.Distance(
            new Vector2(selfPos.X, selfPos.Y),
            new Vector2(activeTarget.X, activeTarget.Y));
        if (isUnderwater)
        {
            if (activeTarget.Z > selfPos.Z + 0.18f && _jumpCooldown <= 0f && horizDistToTarget <= 0.45f)
                wantJump = true;
        }
        else if (_waypointIndex < _waypointJumps.Count &&
                 _waypointJumps[_waypointIndex] &&
                 isGrounded &&
                 _jumpCooldown <= 0f &&
                 horizDistToTarget <= 0.38f &&
                 activeTarget.Z > selfPos.Z + 0.05f)
        {
            wantJump = true;
        }

        return NormalizeSwimOrFlatDirection(activeTarget - selfPos, isUnderwater);
    }

    private static Vector3 NormalizeSwimOrFlatDirection(Vector3 dir, bool isUnderwater)
    {
        if (isUnderwater)
        {
            float dZ = dir.Z;
            dir.Z = 0f;
            if (dir.LengthSquared() > 1e-6f)
                dir = Vector3.Normalize(dir);
            dir.Z = Math.Clamp(dZ * 2.0f, -1f, 1f);
            return dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.Zero;
        }

        dir.Z = 0f;
        return dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.Zero;
    }

    private WorldSimulation.PickupInfo? SelectDesiredPickup(
        PlayerInventory inv,
        int selfSector,
        Vector3 selfPos,
        bool includeGeneralUpgrades,
        SectorPortalNavGraph navGraph,
        IReadOnlyList<WorldSimulation.PickupInfo> pickups)
    {
        WorldSimulation.PickupInfo? best = null;
        float bestScore = 0f;

        foreach (var p in pickups)
        {
            if (p.SectorIndex >= 0 && !navGraph.CanReach(selfSector, p.SectorIndex))
                continue;

            float baseValue = 0f;
            if (p.IsHealth && inv.Health < 90f)
                baseValue = inv.Health < 55f ? 320f : 120f;
            else if (p.IsShield && inv.Shields < 90f)
                baseValue = inv.Shields < 40f ? 260f : 110f;
            else if (p.WeaponBin >= 0)
            {
                bool alreadyOwns = inv.GetInv(p.WeaponBin) > 0f;
                baseValue = alreadyOwns ? 65f : (p.WeaponBin == Archetype.FavoriteWeaponBin ? 260f : 205f);
            }
            else if (p.IsAmmo)
                baseValue = (inv.PowerAmmo < 40f || inv.EnergyAmmo < 40f) ? 160f : 65f;
            else if (p.IsPowerup && includeGeneralUpgrades)
                baseValue = 130f;

            if (baseValue <= 0f)
                continue;
            if (!includeGeneralUpgrades && baseValue < 200f)
                continue;

            float horiz = Vector2.Distance(new Vector2(selfPos.X, selfPos.Y), new Vector2(p.Position.X, p.Position.Y));
            float effDist = horiz + MathF.Abs(p.Position.Z - selfPos.Z) * 3.5f;
            float score = baseValue / (1.0f + effDist * 0.45f);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return best;
    }

    private static PlayerState? FindBestEnemy(
        WorldSimulation sim,
        PlayerState selfPlayer,
        Vector3 selfEyePos,
        out Vector3 enemyPos,
        out Vector3 enemyCenter,
        out float enemyDist,
        out bool hasLos)
    {
        PlayerState? best = null;
        float bestScore = float.MaxValue;
        enemyPos = Vector3.Zero;
        enemyCenter = Vector3.Zero;
        enemyDist = float.MaxValue;
        hasLos = false;

        foreach (var candidate in sim.Players)
        {
            if (candidate.ThingIndex == selfPlayer.ThingIndex || candidate.Inventory.IsDead)
                continue;
            if (selfPlayer.Team != 0 && candidate.Team == selfPlayer.Team)
                continue;

            Vector3 pos = candidate.HasInputPosition
                ? candidate.WorldPosition
                : (candidate.ThingIndex >= 0 ? sim.GetThingPos(candidate.ThingIndex) : Vector3.Zero);
            Vector3 center = pos + new Vector3(0f, 0f, 0.13f);
            float dist = Vector3.Distance(selfEyePos, center);
            bool los = sim.CheckLineOfSight(selfEyePos, center);
            float effectiveScore = los ? dist : dist + 100f;

            if (effectiveScore < bestScore)
            {
                bestScore = effectiveScore;
                best = candidate;
                enemyPos = pos;
                enemyCenter = center;
                enemyDist = dist;
                hasLos = los;
            }
        }

        return best;
    }

    private Vector3 ComputeAimPoint(int weaponBin, Vector3 eyePos, Vector3 targetCenter, float dist)
    {
        float projSpeed = weaponBin switch
        {
            PlayerInventory.BinBryar => 4.0f,
            PlayerInventory.BinStRifle => 6.0f,
            PlayerInventory.BinThermDet => 1.8f,
            PlayerInventory.BinBowcaster => 5.0f,
            PlayerInventory.BinRepeater => 6.0f,
            PlayerInventory.BinRailDet => 2.5f,
            PlayerInventory.BinConcRifle => 7.0f,
            _ => 0f
        };

        Vector3 aimTarget = targetCenter;
        if (projSpeed > 0.1f)
        {
            float leadTime = MathF.Min(0.85f, dist / projSpeed);
            aimTarget += _targetVelocity * (leadTime * (Skill == BotSkill.Easy ? 0.35f : 0.85f));
        }

        // Ballistic loft for gravity-affected thrown/fired ordnance
        if (weaponBin == PlayerInventory.BinThermDet)
        {
            aimTarget.Z += MathF.Min(0.45f, dist * 0.10f);
        }
        else if (weaponBin == PlayerInventory.BinRailDet)
        {
            float t = MathF.Min(0.6f, dist / 2.5f);
            aimTarget.Z += 0.5f * 3.5f * t * t * 0.35f;
        }

        return aimTarget + _aimJitterOffset;
    }

    private void RefreshTacticalBiasWeapon(PlayerInventory inv)
    {
        Span<int> candidates = stackalloc int[10];
        int count = 0;
        for (int bin = PlayerInventory.BinBryar; bin <= PlayerInventory.BinLightsaber; bin++)
        {
            if (HasUsableAmmo(inv, bin))
                candidates[count++] = bin;
        }

        _tacticalBiasWeaponBin = count > 0 ? candidates[_rng.Next(count)] : -1;
        _tacticalBiasTimer = 15f + (float)_rng.NextDouble() * 7f;
    }

    private int SelectForcePower(PlayerInventory inv, bool hasEnemy, bool hasLos, float enemyDist, bool isMoving)
    {
        if (_forceCooldown > 0f || inv.ForceMana < 20f)
            return 0;

        if (inv.Health < 55f && inv.GetInv(PlayerInventory.BinForceHeal) > 0f)
        {
            _forceCooldown = 6.0f;
            return PlayerInventory.BinForceHeal;
        }

        if (hasEnemy && hasLos)
        {
            if (enemyDist <= 1.45f && inv.GetInv(PlayerInventory.BinForceLightning) > 0f && inv.ForceMana >= 45f)
            {
                _forceCooldown = Skill == BotSkill.Hard ? 6.5f : 9.0f;
                return PlayerInventory.BinForceLightning;
            }
            if (enemyDist is >= 1.2f and <= 4.0f && inv.GetInv(PlayerInventory.BinForceDestruction) > 0f && inv.ForceMana >= 50f)
            {
                _forceCooldown = 7.5f;
                return PlayerInventory.BinForceDestruction;
            }
            if (enemyDist is >= 0.8f and <= 2.6f && inv.GetInv(PlayerInventory.BinForceThrow) > 0f && inv.ForceMana >= 35f)
            {
                _forceCooldown = 6.5f;
                return PlayerInventory.BinForceThrow;
            }
        }

        if (isMoving && hasEnemy && enemyDist > 3.5f && inv.GetInv(PlayerInventory.BinForceSpeed) > 0f && inv.ForceMana >= 40f)
        {
            _forceCooldown = 10.0f;
            return PlayerInventory.BinForceSpeed;
        }

        return 0;
    }

    private void AdvanceTimers(float dt, Vector3 selfPos)
    {
        _repathTimer = MathF.Max(0f, _repathTimer - dt);
        _seqDropCooldown = MathF.Max(0f, _seqDropCooldown - dt);
        _forceCooldown = MathF.Max(0f, _forceCooldown - dt);
        _stuckSidestepTimer = MathF.Max(0f, _stuckSidestepTimer - dt);
        _jumpCooldown = MathF.Max(0f, _jumpCooldown - dt);
        _reactionTimer = MathF.Max(0f, _reactionTimer - dt);
        _weaponSwitchCooldown = MathF.Max(0f, _weaponSwitchCooldown - dt);
        _tacticalBiasTimer = MathF.Max(0f, _tacticalBiasTimer - dt);
        _losLostTimer = MathF.Min(10f, _losLostTimer + dt);

        _strafeTimer += dt;
        if (_strafeTimer >= 1.8f)
        {
            _strafeTimer = 0f;
            _strafeSign = -_strafeSign;
        }

        _aimJitterTimer -= dt;
        if (_aimJitterTimer <= 0f)
        {
            _aimJitterTimer = 0.30f + (float)_rng.NextDouble() * 0.25f;
            float spread = Skill switch
            {
                BotSkill.Easy => 0.14f,
                BotSkill.Medium => 0.055f,
                _ => 0.015f
            };
            _aimJitterOffset = new Vector3(
                ((float)_rng.NextDouble() - 0.5f) * 2f * spread,
                ((float)_rng.NextDouble() - 0.5f) * 2f * spread,
                ((float)_rng.NextDouble() - 0.5f) * spread);
        }
    }

    private void UpdateTrackedTargetVelocity(float dt, PlayerState? bestEnemy, Vector3 enemyPos)
    {
        if (bestEnemy == null || dt <= 1e-5f)
        {
            _trackedTargetThingId = -1;
            _hasPrevTargetPos = false;
            _targetVelocity = Vector3.Zero;
            return;
        }

        if (_trackedTargetThingId == bestEnemy.ThingIndex && _hasPrevTargetPos)
        {
            Vector3 instVel = (enemyPos - _prevTargetPos) / dt;
            if (instVel.LengthSquared() < 100f) // Ignore respawn teleport spikes
                _targetVelocity = Vector3.Lerp(_targetVelocity, instVel, 0.35f);
        }
        else
        {
            _trackedTargetThingId = bestEnemy.ThingIndex;
            _targetVelocity = Vector3.Zero;
            _hasPrevTargetPos = true;
        }

        _prevTargetPos = enemyPos;
    }

    private static Vector3 ComputeLocalThrust(Vector3 worldMoveDir, float yawDeg)
    {
        if (worldMoveDir.LengthSquared() < 1e-6f)
            return Vector3.Zero;

        float yawRad = yawDeg * (MathF.PI / 180f);
        Vector2 fwd = new(-MathF.Sin(yawRad), MathF.Cos(yawRad));
        Vector2 right = new(MathF.Cos(yawRad), MathF.Sin(yawRad));
        Vector2 horiz = new(worldMoveDir.X, worldMoveDir.Y);
        float len = horiz.Length();
        if (len > 1e-6f)
            horiz /= len;

        return new Vector3(Vector2.Dot(horiz, right), Vector2.Dot(horiz, fwd), worldMoveDir.Z);
    }

    private static BotTickDecision FinalizePassiveOrientation(
        BotTickDecision decision,
        Vector3 currentPyr,
        Vector3 selfPos,
        Vector3 enemyPos,
        bool hasLos)
    {
        if (hasLos)
        {
            Vector3 to = enemyPos - selfPos;
            Vector3 dir = to.LengthSquared() > 1e-6f ? Vector3.Normalize(to) : Vector3.UnitY;
            decision.AimDirection = dir;
            decision.Yaw = -MathF.Atan2(dir.X, dir.Y) * (180f / MathF.PI);
            decision.Pitch = MathF.Asin(Math.Clamp(dir.Z, -0.95f, 0.95f)) * (180f / MathF.PI);
        }
        else
        {
            decision.Pitch = currentPyr.X;
            decision.Yaw = currentPyr.Y;
            float yawRad = currentPyr.Y * (MathF.PI / 180f);
            decision.AimDirection = new Vector3(-MathF.Sin(yawRad), MathF.Cos(yawRad), 0f);
        }
        return decision;
    }
}
