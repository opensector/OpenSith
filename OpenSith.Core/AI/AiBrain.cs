using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenSith.Core.AI;

/// <summary>
/// Runtime AI Brain implementing perception (FOV cone, auditory sphere), behavioral instincts,
/// and hierarchical state machine transitions matching LucasArts' Sith Engine (OpenJKDF2 sithAI.c).
/// </summary>
public sealed class AiBrain
{
    public const float DefaultAlertDuration = 5.0f;
    public const float DefaultFireCooldown = 1.0f;
    public const float DefaultFleeDuration = 8.0f;
    public const float LowHealthThresholdRatio = 0.35f;

    /// <summary>
    /// Reference to the AI Class definition providing actor parameters, FOV, and instincts.
    /// </summary>
    public AiClass Class { get; set; }

    /// <summary>
    /// Current AI mode bit flags for this actor.
    /// </summary>
    public AiMode Mode { get; set; }

    /// <summary>
    /// Thing index of the current target entity (-1 if no target).
    /// </summary>
    public int TargetThingIndex { get; set; } = -1;

    /// <summary>
    /// Spawn or patrol guard position to which the actor returns when idle.
    /// </summary>
    public Vector3 HomePosition { get; set; }

    /// <summary>
    /// Last known world position of the target.
    /// </summary>
    public Vector3? LastKnownTargetPosition { get; set; }

    /// <summary>
    /// Countdown timer during which the actor remains in an alerted or searching state.
    /// </summary>
    public float AlertTimer { get; set; }

    /// <summary>
    /// Weapon fire cooldown timer.
    /// </summary>
    public float FireCooldown { get; set; }

    /// <summary>
    /// Per-instinct timers and cooldowns keyed by instinct name.
    /// </summary>
    public Dictionary<string, float> InstinctTimers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the AI is paused via <see cref="AiMode.Disabled"/> (0x2000).
    /// </summary>
    public bool IsAiPaused => (Mode & AiMode.Disabled) != 0;

    /// <summary>
    /// True during a tick when the AI initiates a saber swing.
    /// </summary>
    public bool SaberAttackRequested { get; private set; }

    /// <summary>
    /// 1-based saber attack index (1 or 2) when <see cref="SaberAttackRequested"/> is true, or 0.
    /// </summary>
    public int SaberAttackIndex { get; private set; }

    /// <summary>
    /// Damage of the requested saber attack.
    /// </summary>
    public float SaberAttackDamage { get; private set; }

    /// <summary>
    /// Range of the requested saber attack.
    /// </summary>
    public float SaberAttackRange { get; private set; }

    /// <summary>
    /// True during a tick when the AI triggers its SpecialAttack (e.g. Yun's YUflip.key lunge).
    /// </summary>
    public bool SpecialAttackRequested { get; private set; }

    /// <summary>
    /// Animation mode of the requested SpecialAttack.
    /// </summary>
    public int SpecialAttackAnimMode { get; private set; }

    /// <summary>
    /// Damage of the requested SpecialAttack.
    /// </summary>
    public float SpecialAttackDamage { get; private set; }

    /// <summary>
    /// 1-based Force power slot (1, 2, or 3) emitted this tick to dispatch <c>user0</c> on the boss COG, or 0 if none.
    /// </summary>
    public int ForcePowerRequested { get; private set; }

    /// <summary>
    /// True during a tick when the AI is actively circle-strafing around its target.
    /// </summary>
    public bool IsCircleStrafing { get; private set; }

    private float _saberCooldown;
    private int _nextSaberAttackIndex = 1;
    private float _specialAttackTimer;
    private float _forceCheckTimer;
    private readonly float[] _forceCooldowns = new float[3];
    private float _circleStrafeTimer;
    private float _circleStrafeActiveRemaining;
    private float _circleStrafeDir = 1.0f;
    private float _roamTimer;
    private Vector3 _roamMoveDir;
    private readonly Random _rng;

    public AiBrain(AiClass? aiClass = null, Vector3 homePosition = default, AiMode initialMode = AiMode.Active, Random? rng = null)
    {
        Class = aiClass ?? new AiClass();
        HomePosition = homePosition;
        Mode = initialMode;
        _rng = rng ?? Random.Shared;
    }

    /// <summary>
    /// Sets one or more <see cref="AiMode"/> flags (e.g. COG <c>AiSetMode(thing, 0x2000)</c>).
    /// </summary>
    public void SetAiModeFlags(AiMode flags) => Mode |= flags;

    /// <summary>
    /// Sets raw AI mode bit flags (e.g. COG <c>AiSetMode(thing, 0x2000)</c>).
    /// </summary>
    public void SetAiModeFlags(uint flags) => Mode |= (AiMode)flags;

    /// <summary>
    /// Clears one or more <see cref="AiMode"/> flags (e.g. COG <c>AiClearMode(thing, 0x2000)</c>).
    /// </summary>
    public void ClearAiModeFlags(AiMode flags) => Mode &= ~flags;

    /// <summary>
    /// Clears raw AI mode bit flags (e.g. COG <c>AiClearMode(thing, 0x2000)</c>).
    /// </summary>
    public void ClearAiModeFlags(uint flags) => Mode &= ~(AiMode)flags;

    /// <summary>
    /// Evaluates visual perception cone against a target position.
    /// Checks: distance &lt;= AiClass.SightDistance AND dot(normalize(targetPos - selfPos), normalize(selfLookDir)) &gt;= AiClass.Fov.
    /// </summary>
    /// <param name="selfPos">Current position of the AI actor.</param>
    /// <param name="selfLookDir">Facing/look direction vector of the AI actor.</param>
    /// <param name="targetPos">Target world position being tested.</param>
    /// <returns>True if the target is within the actor's visual field of view cone and distance.</returns>
    public bool CanSeeTarget(Vector3 selfPos, Vector3 selfLookDir, Vector3 targetPos)
    {
        ArgumentNullException.ThrowIfNull(Class);

        Vector3 toTarget = targetPos - selfPos;
        float dist = toTarget.Length();

        if (dist > Class.SightDistance)
            return false;

        // If target is directly at self's position, it is considered visible
        if (dist <= 1e-6f)
            return true;

        float lookLen = selfLookDir.Length();
        if (lookLen <= 1e-6f)
            return false;

        Vector3 normToTarget = toTarget / dist;
        Vector3 normLook = selfLookDir / lookLen;
        float dot = Vector3.Dot(normToTarget, normLook);

        // Sith Engine: dot >= AiClass.Fov (cos(half angle))
        return dot >= (Class.Fov - 1e-5f);
    }

    /// <summary>
    /// Evaluates auditory perception against an emitted sound event.
    /// Checks: distance &lt;= AiClass.HeardDistance + soundRadius.
    /// </summary>
    /// <param name="selfPos">Current position of the AI actor.</param>
    /// <param name="soundPos">World position where the sound originated.</param>
    /// <param name="soundRadius">Optional acoustic radius expansion.</param>
    /// <returns>True if the sound falls within the actor's hearing range.</returns>
    public bool CanHearSound(Vector3 selfPos, Vector3 soundPos, float soundRadius = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(Class);

        float dist = Vector3.Distance(selfPos, soundPos);
        return dist <= (Class.HeardDistance + soundRadius + 1e-5f);
    }

    /// <summary>
    /// Manually sets a target for the AI actor.
    /// </summary>
    public void SetTarget(int thingIndex, Vector3 targetPos)
    {
        TargetThingIndex = thingIndex;
        LastKnownTargetPosition = targetPos;
        AlertTimer = DefaultAlertDuration;
        Mode |= AiMode.TargetVisible | AiMode.Active;
    }

    /// <summary>
    /// Clears the current target and resets tracking state.
    /// </summary>
    public void ClearTarget()
    {
        TargetThingIndex = -1;
        LastKnownTargetPosition = null;
        Mode &= ~AiMode.TargetVisible;
    }

    /// <summary>
    /// Executes one AI simulation tick, updating perception, instinct timers, state transitions,
    /// and generating the tactical decision.
    /// </summary>
    /// <param name="dt">Elapsed delta time in seconds.</param>
    /// <param name="context">Snapshot of the surrounding world state and sensory inputs.</param>
    /// <returns>Tactical AI decision for movement, aim, firing, and mode updates.</returns>
    public AiDecision Update(float dt, AiWorldContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResetPerTickRequests();

        AiMode prevMode = Mode;
        AiEventType triggeredEvents = AiEventType.None;

        // If Disabled (0x2000 pause flag), do not perform updates
        if (IsAiPaused)
        {
            return new AiDecision
            {
                DesiredMovementDirection = Vector3.Zero,
                DesiredLookTarget = context.SelfPosition + context.SelfLookDir,
                ShouldFire = false,
                ShouldFlee = false,
                NewMode = Mode,
                TargetThingIndex = TargetThingIndex,
                ModeChanged = false
            };
        }

        // Sleeping mode: evaluate wakeup stimuli (damage, wake distance, sounds)
        if ((Mode & AiMode.Sleeping) != 0)
        {
            bool wokeUp = context.WasDamaged;

            if (!wokeUp && Class.WakeupDistance > 0f)
            {
                if (context.DirectTargetPosition.HasValue &&
                    Vector3.Distance(context.SelfPosition, context.DirectTargetPosition.Value) <= Class.WakeupDistance)
                {
                    wokeUp = true;
                }

                if (!wokeUp && context.Targets != null)
                {
                    foreach (var target in context.Targets)
                    {
                        if (target.IsAlive && Vector3.Distance(context.SelfPosition, target.Position) <= Class.WakeupDistance)
                        {
                            wokeUp = true;
                            break;
                        }
                    }
                }
            }

            if (!wokeUp && context.Sounds != null)
            {
                foreach (var sound in context.Sounds)
                {
                    if (CanHearSound(context.SelfPosition, sound.Position, sound.SoundRadius))
                    {
                        wokeUp = true;
                        break;
                    }
                }
            }

            if (wokeUp)
            {
                Mode &= ~AiMode.Sleeping;
                Mode |= AiMode.Active;
            }
            else
            {
                return new AiDecision
                {
                    DesiredMovementDirection = Vector3.Zero,
                    DesiredLookTarget = context.SelfPosition + context.SelfLookDir,
                    ShouldFire = false,
                    ShouldFlee = false,
                    NewMode = Mode,
                    TargetThingIndex = TargetThingIndex,
                    ModeChanged = false
                };
            }
        }

        // Advance timers
        if (AlertTimer > 0f)
            AlertTimer = MathF.Max(0f, AlertTimer - dt);

        if (FireCooldown > 0f)
            FireCooldown = MathF.Max(0f, FireCooldown - dt);

        foreach (var key in new List<string>(InstinctTimers.Keys))
        {
            InstinctTimers[key] = MathF.Max(0f, InstinctTimers[key] - dt);
        }

        AdvanceDarkJediTimers(dt);

        // 1. Evaluate Fleeing Condition
        // In Sith Engine, actors with the "Flee" instinct transition to Fleeing when damaged or at low health
        bool hasFleeInstinct = Class.HasInstinct("Flee");
        bool isLowHealth = context.Health <= (context.MaxHealth * LowHealthThresholdRatio);
        bool isFleeTimerActive = InstinctTimers.TryGetValue("Flee", out float fleeRemaining) && fleeRemaining > 0f;

        bool shouldFlee = false;
        if (hasFleeInstinct)
        {
            if (context.WasDamaged || isLowHealth)
            {
                shouldFlee = true;
                var fleeInstinct = Class.FindInstinct("Flee");
                float fleeDuration = DefaultFleeDuration;
                if (fleeInstinct != null && fleeInstinct.FloatArgs[1] > 0f)
                {
                    fleeDuration = fleeInstinct.FloatArgs[1] > 50f
                        ? fleeInstinct.FloatArgs[1] / 1000f
                        : fleeInstinct.FloatArgs[1];
                }
                InstinctTimers["Flee"] = fleeDuration;
            }
            else if (isFleeTimerActive || (Mode & AiMode.Fleeing) != 0)
            {
                shouldFlee = isFleeTimerActive;
            }
        }

        if (shouldFlee)
        {
            Mode |= AiMode.Fleeing | AiMode.Active;
            Mode &= ~AiMode.Attacking;
        }
        else
        {
            Mode &= ~AiMode.Fleeing;
        }

        // 2. Perception & Target Acquisition
        Vector3? visibleTargetPos = null;
        int spottedTargetIndex = -1;

        // Check direct target from context if provided
        if (context.DirectTargetPosition.HasValue)
        {
            Vector3 directPos = context.DirectTargetPosition.Value;
            if (CanSeeTarget(context.SelfPosition, context.SelfLookDir, directPos))
            {
                bool losOk = context.LineOfSightCheck == null || context.LineOfSightCheck(context.SelfPosition, directPos);
                if (losOk)
                {
                    visibleTargetPos = directPos;
                    spottedTargetIndex = context.DirectTargetIndex ?? (TargetThingIndex >= 0 ? TargetThingIndex : 0);
                }
            }
        }

        // Check candidates in target list
        if (!visibleTargetPos.HasValue && context.Targets != null)
        {
            float closestDistSq = float.MaxValue;
            foreach (var candidate in context.Targets)
            {
                if (!candidate.IsAlive || !candidate.IsVisible)
                    continue;

                if (CanSeeTarget(context.SelfPosition, context.SelfLookDir, candidate.Position))
                {
                    if (context.LineOfSightCheck != null && !context.LineOfSightCheck(context.SelfPosition, candidate.Position))
                        continue;

                    float distSq = Vector3.DistanceSquared(context.SelfPosition, candidate.Position);
                    if (distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        visibleTargetPos = candidate.Position;
                        spottedTargetIndex = candidate.ThingIndex;
                    }
                }
            }
        }

        // 3. Audio Perception if no visual target
        Vector3? heardSoundPos = null;
        int heardSourceIndex = -1;
        if (!visibleTargetPos.HasValue && context.Sounds != null)
        {
            foreach (var sound in context.Sounds)
            {
                if (CanHearSound(context.SelfPosition, sound.Position, sound.SoundRadius))
                {
                    heardSoundPos = sound.Position;
                    heardSourceIndex = sound.SourceThingIndex;
                    triggeredEvents |= AiEventType.Sound;
                    break;
                }
            }
        }

        // 4. Update Modes and Transitions
        if (visibleTargetPos.HasValue)
        {
            TargetThingIndex = spottedTargetIndex;
            LastKnownTargetPosition = visibleTargetPos.Value;
            AlertTimer = DefaultAlertDuration;
            Mode |= AiMode.TargetVisible | AiMode.Active;

            if (!shouldFlee)
            {
                // Has combat instincts -> Attacking
                bool hasCombatInstinct = Class.HasInstinct("PrimaryFire") ||
                                         Class.HasInstinct("Follow") ||
                                         Class.HasInstinct("Charge") ||
                                         Class.HasInstinct("Leap") ||
                                         Class.HasInstinct("TurretFire") ||
                                         Class.HasInstinct("CircleStrafe") ||
                                         Class.HasInstinct("Retreat") ||
                                         Class.HasInstinct("BlindFire") ||
                                         Class.HasSaberfighting ||
                                         Class.HasSpecialAttack ||
                                         Class.HasForcePowers;

                if (hasCombatInstinct)
                {
                    Mode |= AiMode.Attacking;
                    Mode &= ~AiMode.Searching;
                }
                else
                {
                    Mode |= AiMode.Searching;
                    Mode &= ~AiMode.Attacking;
                }
            }
        }
        else
        {
            // Target is not visible this frame
            Mode &= ~AiMode.TargetVisible;

            if (heardSoundPos.HasValue)
            {
                LastKnownTargetPosition = heardSoundPos.Value;
                if (heardSourceIndex >= 0 && TargetThingIndex < 0)
                    TargetThingIndex = heardSourceIndex;

                AlertTimer = DefaultAlertDuration;
                if (!shouldFlee)
                {
                    Mode |= AiMode.Searching | AiMode.Active;
                    Mode &= ~AiMode.Attacking;
                }
            }
            else if ((Mode & AiMode.Attacking) != 0)
            {
                // Lost visual on target -> transition from Attacking to Searching
                Mode &= ~AiMode.Attacking;
                Mode |= AiMode.Searching;
                AlertTimer = DefaultAlertDuration;
            }
            else if ((Mode & AiMode.Searching) != 0 && AlertTimer <= 0f)
            {
                // Alert duration expired -> return to Idle/None
                Mode &= ~AiMode.Searching;
            }
        }

        // 5. Generate Tactical Decision
        Vector3 desiredMoveDir = Vector3.Zero;
        Vector3 desiredLookTarget = context.SelfPosition + context.SelfLookDir;
        bool shouldFire = false;

        if (shouldFlee)
        {
            Vector3 threatPos = visibleTargetPos ?? LastKnownTargetPosition ?? context.DamageSourcePosition ?? HomePosition;
            Vector3 awayFromThreat = context.SelfPosition - threatPos;

            if (awayFromThreat.LengthSquared() > 1e-4f)
            {
                desiredMoveDir = Vector3.Normalize(awayFromThreat);
                desiredLookTarget = context.SelfPosition + desiredMoveDir;
            }
            else
            {
                // If directly on top of threat, flee along reverse of self look dir
                desiredMoveDir = -context.SelfLookDir;
                desiredLookTarget = context.SelfPosition + desiredMoveDir;
            }

            Mode |= AiMode.Moving;
        }
        else if ((Mode & AiMode.Attacking) != 0 && visibleTargetPos.HasValue)
        {
            desiredLookTarget = visibleTargetPos.Value;
            Vector3 toTarget = visibleTargetPos.Value - context.SelfPosition;
            float dist = toTarget.Length();

            // Primary fire evaluation
            if (Class.HasInstinct("PrimaryFire") && FireCooldown <= 0f)
            {
                shouldFire = true;
                var pf = Class.FindInstinct("PrimaryFire");
                float cooldown = DefaultFireCooldown;
                if (pf != null && pf.FloatArgs[0] > 0f)
                {
                    cooldown = pf.FloatArgs[0] > 50f ? pf.FloatArgs[0] / 1000f : pf.FloatArgs[0];
                }
                FireCooldown = cooldown;
                InstinctTimers["PrimaryFire"] = cooldown;
                triggeredEvents |= AiEventType.Fire;
            }

            // Movement toward target if follow instinct is configured (non-Dark-Jedi path)
            if (Class.HasSaberfighting || Class.HasSpecialAttack || Class.HasForcePowers || Class.HasCircleStrafe)
            {
                EvaluateDarkJediCombat(toTarget, dist, ref desiredMoveDir, ref triggeredEvents);
            }
            else if (Class.HasInstinct("Follow") && dist > 2.0f)
            {
                desiredMoveDir = Vector3.Normalize(toTarget);
                Mode |= AiMode.Moving;
            }
            else
            {
                Mode &= ~AiMode.Moving;
            }
        }
        else if ((Mode & AiMode.Searching) != 0 && LastKnownTargetPosition.HasValue)
        {
            desiredLookTarget = LastKnownTargetPosition.Value;
            Vector3 toLastKnown = LastKnownTargetPosition.Value - context.SelfPosition;
            if (toLastKnown.Length() > 0.5f)
            {
                desiredMoveDir = Vector3.Normalize(toLastKnown);
                Mode |= AiMode.Moving;
            }
            else
            {
                Mode &= ~AiMode.Moving;
            }
        }
        else
        {
            // Idle state: roam around home position if Roam instinct is present, otherwise return home
            if (Class.HasRoam)
            {
                EvaluateRoam(context.SelfPosition, ref desiredMoveDir, ref desiredLookTarget);
            }
            else if (Class.HasInstinct("ReturnHome") && Vector3.Distance(context.SelfPosition, HomePosition) > 0.5f)
            {
                Vector3 toHome = HomePosition - context.SelfPosition;
                desiredMoveDir = Vector3.Normalize(toHome);
                desiredLookTarget = HomePosition;
                Mode |= AiMode.Moving;
            }
            else
            {
                Mode &= ~AiMode.Moving;
            }
        }

        if (Mode != prevMode)
            triggeredEvents |= AiEventType.ModeChanged;

        return new AiDecision
        {
            DesiredMovementDirection = desiredMoveDir,
            DesiredLookTarget = desiredLookTarget,
            ShouldFire = shouldFire,
            ShouldFlee = shouldFlee,
            NewMode = Mode,
            TargetThingIndex = TargetThingIndex,
            ModeChanged = Mode != prevMode,
            TriggeredEvents = triggeredEvents
        };
    }

    private void ResetPerTickRequests()
    {
        SaberAttackRequested = false;
        SaberAttackIndex = 0;
        SaberAttackDamage = 0f;
        SaberAttackRange = 0f;
        SpecialAttackRequested = false;
        SpecialAttackAnimMode = 0;
        SpecialAttackDamage = 0f;
        ForcePowerRequested = 0;
        IsCircleStrafing = false;
    }

    private void AdvanceDarkJediTimers(float dt)
    {
        if (_saberCooldown > 0f)
            _saberCooldown = MathF.Max(0f, _saberCooldown - dt);

        if (_specialAttackTimer > 0f)
            _specialAttackTimer = MathF.Max(0f, _specialAttackTimer - dt);

        if (_forceCheckTimer > 0f)
            _forceCheckTimer = MathF.Max(0f, _forceCheckTimer - dt);

        for (int i = 0; i < _forceCooldowns.Length; i++)
        {
            if (_forceCooldowns[i] > 0f)
                _forceCooldowns[i] = MathF.Max(0f, _forceCooldowns[i] - dt);
        }

        if (_circleStrafeTimer > 0f)
            _circleStrafeTimer = MathF.Max(0f, _circleStrafeTimer - dt);

        if (_circleStrafeActiveRemaining > 0f)
            _circleStrafeActiveRemaining = MathF.Max(0f, _circleStrafeActiveRemaining - dt);

        if (_roamTimer > 0f)
            _roamTimer = MathF.Max(0f, _roamTimer - dt);
    }

    private float NextFloat() => (float)_rng.NextDouble();

    private void EvaluateDarkJediCombat(Vector3 toTarget, float dist, ref Vector3 desiredMoveDir, ref AiEventType triggeredEvents)
    {
        // 1. Saberfighting (alternating attack 1 / 2)
        if (Class.Saberfighting is { } saber && _saberCooldown <= 0f)
        {
            int attackIdx = _nextSaberAttackIndex;
            float range = attackIdx == 1 ? saber.Att1Range : saber.Att2Range;
            float damage = attackIdx == 1 ? saber.Att1Damage : saber.Att2Damage;
            float cooldownSec = attackIdx == 1 ? saber.Att1CooldownSec : saber.Att2CooldownSec;

            if (dist <= range)
            {
                if (saber.SkipChance > 0f && NextFloat() < saber.SkipChance)
                {
                    // Brief pause before re-evaluating swing (500ms in jkAI.c)
                    _saberCooldown = 0.5f;
                }
                else
                {
                    SaberAttackRequested = true;
                    SaberAttackIndex = attackIdx;
                    SaberAttackDamage = damage;
                    SaberAttackRange = range;
                    _saberCooldown = cooldownSec;
                    _nextSaberAttackIndex = (attackIdx == 1 && saber.Att2Range > 0f) ? 2 : 1;
                    triggeredEvents |= AiEventType.Fire;
                }
            }
        }

        // 2. SpecialAttack (YUflip.key lunge when in [minDist, maxDist])
        if (Class.SpecialAttack is { } special && _specialAttackTimer <= 0f)
        {
            if (dist >= special.MinDist && dist <= special.MaxDist)
            {
                if (NextFloat() <= special.Probability)
                {
                    SpecialAttackRequested = true;
                    SpecialAttackAnimMode = special.AnimMode;
                    SpecialAttackDamage = special.Damage;
                    _specialAttackTimer = special.CooldownSec;
                    triggeredEvents |= AiEventType.Fire;
                }
                else
                {
                    _specialAttackTimer = special.IntervalSec;
                }
            }
        }

        // 3. ForcePowers (emit ForcePowerRequested 1..3 for boss COG user0 dispatch)
        if (Class.ForcePowers is { } fp && _forceCheckTimer <= 0f)
        {
            _forceCheckTimer = fp.IntervalSec > 0f ? fp.IntervalSec : 1.0f;
            float roll = NextFloat();

            for (int i = 0; i < 3; i++)
            {
                var slot = fp.GetPower(i + 1);
                if (_forceCooldowns[i] <= 0f &&
                    slot.Probability > 0f &&
                    roll < slot.Probability &&
                    dist >= slot.MinDist &&
                    dist <= slot.MaxDist)
                {
                    ForcePowerRequested = i + 1;
                    _forceCooldowns[i] = slot.CooldownSec;
                    break;
                }
            }
        }

        // 4. CircleStrafe & Approach movement
        if (Class.CircleStrafe is { } cs && dist <= 3.0f && (_circleStrafeActiveRemaining > 0f || _circleStrafeTimer <= 0f))
        {
            if (_circleStrafeActiveRemaining <= 0f)
            {
                _circleStrafeActiveRemaining = cs.DurationSec > 0f ? cs.DurationSec : 0.5f;
                _circleStrafeTimer = cs.IntervalSec;
                _circleStrafeDir = cs.RandomStrafe ? (NextFloat() >= 0.5f ? 1f : -1f) : -_circleStrafeDir;
            }

            Vector3 normToTarget = dist > 1e-5f ? toTarget / dist : Vector3.UnitZ;
            Vector3 up = MathF.Abs(Vector3.Dot(normToTarget, Vector3.UnitY)) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(normToTarget, up)) * _circleStrafeDir;

            float saberRange = Class.Saberfighting?.Att1Range ?? 0.25f;
            Vector3 strafeDir = dist > saberRange * 1.5f
                ? Vector3.Normalize(tangent + normToTarget * 0.5f)
                : tangent;

            IsCircleStrafing = true;
            desiredMoveDir = strafeDir;
            Mode |= AiMode.Moving;
            return;
        }

        if (SpecialAttackRequested && dist > 0.1f)
        {
            desiredMoveDir = Vector3.Normalize(toTarget);
            Mode |= AiMode.Moving;
            return;
        }

        if (Class.HasSaberfighting)
        {
            float stopDist = MathF.Max(0.05f, (Class.Saberfighting?.Att1Range ?? 0.25f) * 0.8f);
            if (dist > stopDist)
            {
                desiredMoveDir = Vector3.Normalize(toTarget);
                Mode |= AiMode.Moving;
            }
            else
            {
                Mode &= ~AiMode.Moving;
            }
        }
        else if (Class.HasInstinct("Follow") && dist > 2.0f)
        {
            desiredMoveDir = Vector3.Normalize(toTarget);
            Mode |= AiMode.Moving;
        }
        else
        {
            Mode &= ~AiMode.Moving;
        }
    }

    private void EvaluateRoam(Vector3 selfPos, ref Vector3 desiredMoveDir, ref Vector3 desiredLookTarget)
    {
        var roam = Class.RoamConfig;
        float intervalMs = roam?.IntervalMs ?? 0f;
        float maxDist = roam?.MaxDist ?? 0f;
        if (roam == null)
        {
            var instinct = Class.FindInstinct("Roam");
            if (instinct != null)
            {
                intervalMs = instinct.FloatArgs[0];
                maxDist = instinct.FloatArgs[1];
            }
        }

        float intervalSec = intervalMs > 0f ? intervalMs / 1000.0f : 2.0f;
        float roamRadius = MathF.Abs(maxDist) > 0f ? MathF.Abs(maxDist) : 1.0f;

        Vector3 offsetFromHome = selfPos - HomePosition;
        offsetFromHome.Y = 0f;
        float distFromHome = offsetFromHome.Length();

        if (_roamTimer <= 0f)
        {
            // Brief idle pause ~30% of the time after a movement phase
            if (_roamMoveDir != Vector3.Zero && NextFloat() < 0.30f)
            {
                _roamMoveDir = Vector3.Zero;
                _roamTimer = intervalSec * (0.25f + 0.25f * NextFloat());
            }
            else
            {
                if (distFromHome >= roamRadius * 0.85f && distFromHome > 1e-4f)
                {
                    // Steer gently back toward HomePosition with slight angular variation
                    Vector3 toHome = -offsetFromHome / distFromHome;
                    float baseAngle = MathF.Atan2(toHome.Z, toHome.X);
                    float jitter = (NextFloat() - 0.5f) * (MathF.PI * 0.5f);
                    float angle = baseAngle + jitter;
                    _roamMoveDir = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                }
                else
                {
                    float angle = NextFloat() * MathF.Tau;
                    _roamMoveDir = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                }

                _roamTimer = intervalSec * (0.6f + 0.4f * NextFloat());
            }
        }
        else if (_roamMoveDir != Vector3.Zero && distFromHome > roamRadius && distFromHome > 1e-4f)
        {
            // Keep actor within roam radius if it stepped past the boundary
            _roamMoveDir = -offsetFromHome / distFromHome;
        }

        if (_roamMoveDir != Vector3.Zero)
        {
            desiredMoveDir = _roamMoveDir * 0.5f;
            desiredLookTarget = selfPos + _roamMoveDir;
            Mode |= AiMode.Moving;
        }
        else
        {
            desiredMoveDir = Vector3.Zero;
            Mode &= ~AiMode.Moving;
        }
    }
}
