using System;
using System.IO;
using System.Numerics;
using OpenSith.Core.AI;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class AiBrainTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    #region 1. Sight Cone Detection & Distance Tests

    [Fact]
    public void Stormtrooper_170Degrees_InsideAndOutsideFov()
    {
        // Stormtrooper: FOV = 170 degrees (cos(85 deg) ~ 0.08715574), SightDistance = 5.0
        var aiClass = new AiClass
        {
            FovDegrees = 170.0f,
            SightDistance = 5.0f
        };
        var brain = new AiBrain(aiClass);

        var selfPos = Vector3.Zero;
        var lookDir = new Vector3(0, 0, 1); // Looking along +Z

        // 1. Directly in front: angle = 0 deg, dot = 1.0 -> TRUE
        Assert.True(brain.CanSeeTarget(selfPos, lookDir, new Vector3(0, 0, 3.0f)));

        // 2. 45 degrees to the right: dot = cos(45 deg) ~ 0.7071 >= 0.087 -> TRUE
        float x45 = MathF.Sin(45.0f * (MathF.PI / 180.0f)) * 3.0f;
        float z45 = MathF.Cos(45.0f * (MathF.PI / 180.0f)) * 3.0f;
        Assert.True(brain.CanSeeTarget(selfPos, lookDir, new Vector3(x45, 0, z45)));

        // 3. 80 degrees to the right: dot = cos(80 deg) ~ 0.1736 >= 0.087 -> TRUE
        float x80 = MathF.Sin(80.0f * (MathF.PI / 180.0f)) * 3.0f;
        float z80 = MathF.Cos(80.0f * (MathF.PI / 180.0f)) * 3.0f;
        Assert.True(brain.CanSeeTarget(selfPos, lookDir, new Vector3(x80, 0, z80)));

        // 4. 85 degrees (on boundary): dot = cos(85 deg) -> TRUE
        float x85 = MathF.Sin(85.0f * (MathF.PI / 180.0f)) * 3.0f;
        float z85 = MathF.Cos(85.0f * (MathF.PI / 180.0f)) * 3.0f;
        Assert.True(brain.CanSeeTarget(selfPos, lookDir, new Vector3(x85, 0, z85)));

        // 5. 90 degrees (perpendicular, to right): dot = 0.0 < 0.087 -> FALSE (outside 170 deg cone)
        Assert.False(brain.CanSeeTarget(selfPos, lookDir, new Vector3(3.0f, 0, 0)));

        // 6. Directly behind (180 degrees): dot = -1.0 -> FALSE
        Assert.False(brain.CanSeeTarget(selfPos, lookDir, new Vector3(0, 0, -3.0f)));
    }

    [Fact]
    public void ReeYees_200Degrees_CanSeeSidewaysAndSlightlyBehind()
    {
        // Ree-Yees: FOV = 200 degrees (cos(100 deg) ~ -0.17364818), SightDistance = 5.0
        var aiClass = new AiClass
        {
            FovDegrees = 200.0f,
            SightDistance = 5.0f
        };
        var brain = new AiBrain(aiClass);

        var selfPos = Vector3.Zero;
        var lookDir = new Vector3(0, 0, 1);

        // 1. Directly in front -> TRUE
        Assert.True(brain.CanSeeTarget(selfPos, lookDir, new Vector3(0, 0, 4.0f)));

        // 2. Perpendicular (90 degrees to side): dot = 0.0 >= -0.1736 -> TRUE!
        Assert.True(brain.CanSeeTarget(selfPos, lookDir, new Vector3(4.0f, 0, 0)));
        Assert.True(brain.CanSeeTarget(selfPos, lookDir, new Vector3(-4.0f, 0, 0)));

        // 3. 95 degrees (slightly behind the sides): dot = cos(95 deg) ~ -0.087 >= -0.1736 -> TRUE!
        float x95 = MathF.Sin(95.0f * (MathF.PI / 180.0f)) * 3.0f;
        float z95 = MathF.Cos(95.0f * (MathF.PI / 180.0f)) * 3.0f;
        Assert.True(brain.CanSeeTarget(selfPos, lookDir, new Vector3(x95, 0, z95)));

        // 4. 110 degrees (beyond 200 deg cone): dot = cos(110 deg) ~ -0.342 < -0.1736 -> FALSE!
        float x110 = MathF.Sin(110.0f * (MathF.PI / 180.0f)) * 3.0f;
        float z110 = MathF.Cos(110.0f * (MathF.PI / 180.0f)) * 3.0f;
        Assert.False(brain.CanSeeTarget(selfPos, lookDir, new Vector3(x110, 0, z110)));

        // 5. Directly behind (180 degrees): dot = -1.0 < -0.1736 -> FALSE
        Assert.False(brain.CanSeeTarget(selfPos, lookDir, new Vector3(0, 0, -4.0f)));
    }

    [Fact]
    public void SightDistance_BoundaryConditions()
    {
        var aiClass = new AiClass
        {
            FovDegrees = 180.0f,
            SightDistance = 10.0f
        };
        var brain = new AiBrain(aiClass);

        var self = Vector3.Zero;
        var look = Vector3.UnitZ;

        // Inside distance
        Assert.True(brain.CanSeeTarget(self, look, new Vector3(0, 0, 9.9f)));

        // Exact distance
        Assert.True(brain.CanSeeTarget(self, look, new Vector3(0, 0, 10.0f)));

        // Outside distance
        Assert.False(brain.CanSeeTarget(self, look, new Vector3(0, 0, 10.1f)));
        Assert.False(brain.CanSeeTarget(self, look, new Vector3(0, 0, 20.0f)));

        // Same position (distance = 0)
        Assert.True(brain.CanSeeTarget(self, look, Vector3.Zero));

        // Zero look direction returns false without throwing
        Assert.False(brain.CanSeeTarget(self, Vector3.Zero, new Vector3(0, 0, 5.0f)));
    }

    [Fact]
    public void Full360Degrees_SeesInAllDirections()
    {
        var aiClass = new AiClass
        {
            FovDegrees = 360.0f,
            SightDistance = 15.0f
        };
        var brain = new AiBrain(aiClass);

        var self = new Vector3(10, 0, 10);
        var look = new Vector3(0, 0, 1);

        // Front
        Assert.True(brain.CanSeeTarget(self, look, new Vector3(10, 0, 15)));
        // Back
        Assert.True(brain.CanSeeTarget(self, look, new Vector3(10, 0, 5)));
        // Left
        Assert.True(brain.CanSeeTarget(self, look, new Vector3(5, 0, 10)));
        // Right
        Assert.True(brain.CanSeeTarget(self, look, new Vector3(15, 0, 10)));
        // Beyond SightDistance
        Assert.False(brain.CanSeeTarget(self, look, new Vector3(10, 0, 30)));
    }

    #endregion

    #region 2. Hearing Detection Tests

    [Fact]
    public void Hearing_DetectionInsideAndOutsideDistance()
    {
        var aiClass = new AiClass
        {
            HeardDistance = 8.0f
        };
        var brain = new AiBrain(aiClass);

        var selfPos = new Vector3(5, 0, 5);

        // Sound within heard distance
        var closeSound = new Vector3(5, 0, 11); // Distance = 6.0
        Assert.True(brain.CanHearSound(selfPos, closeSound));

        // Sound exactly at heard distance
        var edgeSound = new Vector3(5, 0, 13); // Distance = 8.0
        Assert.True(brain.CanHearSound(selfPos, edgeSound));

        // Sound beyond heard distance
        var farSound = new Vector3(5, 0, 14); // Distance = 9.0
        Assert.False(brain.CanHearSound(selfPos, farSound));
    }

    [Fact]
    public void Hearing_WithSoundRadiusExpansion()
    {
        var aiClass = new AiClass
        {
            HeardDistance = 2.0f
        };
        var brain = new AiBrain(aiClass);

        var selfPos = Vector3.Zero;
        var soundPos = new Vector3(0, 0, 5.0f); // Distance = 5.0

        // Without sound radius (max dist = 2.0) -> Cannot hear
        Assert.False(brain.CanHearSound(selfPos, soundPos, soundRadius: 0.0f));

        // With sound radius = 2.0 (max dist = 4.0) -> Cannot hear
        Assert.False(brain.CanHearSound(selfPos, soundPos, soundRadius: 2.0f));

        // With sound radius = 3.5 (max dist = 5.5 >= 5.0) -> Can hear!
        Assert.True(brain.CanHearSound(selfPos, soundPos, soundRadius: 3.5f));
    }

    [Fact]
    public void Hearing_IsOmnidirectional()
    {
        var aiClass = new AiClass { HeardDistance = 5.0f };
        var brain = new AiBrain(aiClass);
        var selfPos = Vector3.Zero;

        // Sounds from all cardinal directions are heard identically
        Assert.True(brain.CanHearSound(selfPos, new Vector3(0, 0, 4.0f)));
        Assert.True(brain.CanHearSound(selfPos, new Vector3(0, 0, -4.0f)));
        Assert.True(brain.CanHearSound(selfPos, new Vector3(4.0f, 0, 0)));
        Assert.True(brain.CanHearSound(selfPos, new Vector3(-4.0f, 0, 0)));
    }

    #endregion

    #region 3. State Transitions Tests

    [Fact]
    public void StateTransition_IdleToAttacking_WhenTargetSpotted()
    {
        var aiClass = new AiClass
        {
            FovDegrees = 180.0f,
            SightDistance = 20.0f
        };
        aiClass.Instincts.Add(new AiInstinct("PrimaryFire", [2000.0f, 0.8f]));
        aiClass.Instincts.Add(new AiInstinct("Follow", [2.0f]));

        var brain = new AiBrain(aiClass, homePosition: Vector3.Zero, initialMode: AiMode.None);
        Assert.Equal(AiMode.None, brain.Mode);

        var targetPos = new Vector3(0, 0, 10.0f);
        var context = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            DirectTargetPosition = targetPos,
            DirectTargetIndex = 42
        };

        var decision = brain.Update(0.1f, context);

        // Should transition to Attacking, TargetVisible, and Active
        Assert.True((brain.Mode & AiMode.Attacking) != 0, "Brain should have Attacking mode");
        Assert.True((brain.Mode & AiMode.TargetVisible) != 0, "Brain should have TargetVisible mode");
        Assert.True((brain.Mode & AiMode.Active) != 0, "Brain should have Active mode");
        Assert.False((brain.Mode & AiMode.Searching) != 0, "Brain should not have Searching mode");

        Assert.Equal(42, brain.TargetThingIndex);
        Assert.Equal(targetPos, brain.LastKnownTargetPosition);
        Assert.True(decision.ModeChanged);
        Assert.True(decision.ShouldFire);
        Assert.Equal(targetPos, decision.DesiredLookTarget);
        Assert.True(decision.HasMovement);
        Assert.True(Vector3.Dot(decision.DesiredMovementDirection, Vector3.UnitZ) > 0.9f);
    }

    [Fact]
    public void StateTransition_AttackingToSearching_WhenTargetLost()
    {
        var aiClass = new AiClass
        {
            FovDegrees = 180.0f,
            SightDistance = 20.0f
        };
        aiClass.Instincts.Add(new AiInstinct("PrimaryFire", [2000.0f]));
        aiClass.Instincts.Add(new AiInstinct("Follow", [2.0f]));

        var brain = new AiBrain(aiClass, homePosition: Vector3.Zero, initialMode: AiMode.Attacking | AiMode.Active);
        brain.SetTarget(7, new Vector3(0, 0, 10.0f));

        // Target moves behind cover / out of sight
        var context = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            Targets = Array.Empty<AiTargetCandidate>() // No visible targets
        };

        var decision = brain.Update(0.1f, context);

        // Attacking flag should be replaced with Searching
        Assert.False((brain.Mode & AiMode.Attacking) != 0, "Attacking should be cleared");
        Assert.False((brain.Mode & AiMode.TargetVisible) != 0, "TargetVisible should be cleared");
        Assert.True((brain.Mode & AiMode.Searching) != 0, "Searching mode should be set");
        Assert.False(decision.ShouldFire);
        Assert.Equal(new Vector3(0, 0, 10.0f), decision.DesiredLookTarget);
    }

    [Fact]
    public void StateTransition_SearchingToIdle_WhenAlertTimerExpires()
    {
        var aiClass = new AiClass
        {
            SightDistance = 20.0f
        };
        var brain = new AiBrain(aiClass, homePosition: Vector3.Zero, initialMode: AiMode.Searching | AiMode.Active);
        brain.AlertTimer = 0.5f;

        var context = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ
        };

        // Advance past alert timer
        brain.Update(0.6f, context);

        Assert.False((brain.Mode & AiMode.Searching) != 0, "Searching mode should expire");
    }

    [Fact]
    public void StateTransition_Sleeping_WakesUpOnSoundOrDamage()
    {
        var aiClass = new AiClass
        {
            WakeupDistance = 5.0f,
            HeardDistance = 10.0f
        };
        var brain = new AiBrain(aiClass, initialMode: AiMode.Sleeping);

        // Idle tick while asleep without stimuli: stays asleep
        var idleContext = new AiWorldContext { SelfPosition = Vector3.Zero, Health = 100f };
        brain.Update(0.1f, idleContext);
        Assert.True((brain.Mode & AiMode.Sleeping) != 0);

        // Sound stimulus within hearing distance wakes actor up
        var soundContext = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            Sounds = [new AiSoundEvent(new Vector3(0, 0, 6.0f))]
        };
        brain.Update(0.1f, soundContext);

        Assert.False((brain.Mode & AiMode.Sleeping) != 0, "Actor should wake up");
        Assert.True((brain.Mode & AiMode.Active) != 0, "Actor should now be active");
    }

    #endregion

    #region 4. Flee Instinct Tests (pednarsh.ai, gonk.ai, stdefault.ai)

    [Fact]
    public void Flee_Pednarsh_FleesWhenDamaged()
    {
        // Pednarsh: civilian NPC with Flee instinct
        var pedClass = new AiClass
        {
            Name = "pednarsh.ai",
            Alignment = -1.0f,
            FovDegrees = 165.0f,
            SightDistance = 7.0f,
            HeardDistance = 1.5f
        };
        pedClass.Instincts.Add(new AiInstinct("SenseDanger", []));
        pedClass.Instincts.Add(new AiInstinct("Flee", [10.0f, 10000.0f, 10.0f]));
        pedClass.Instincts.Add(new AiInstinct("OpenDoors", []));

        var brain = new AiBrain(pedClass, homePosition: Vector3.Zero, initialMode: AiMode.Active);
        Assert.False((brain.Mode & AiMode.Fleeing) != 0);

        // Attacker is in front at (0, 0, 5) and damages pednarsh
        var context = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            Health = 90.0f,
            MaxHealth = 100.0f,
            WasDamaged = true,
            DamageSourcePosition = new Vector3(0, 0, 5.0f)
        };

        var decision = brain.Update(0.1f, context);

        Assert.True((brain.Mode & AiMode.Fleeing) != 0, "Pednarsh should transition to Fleeing");
        Assert.True(decision.ShouldFlee);
        Assert.False(decision.ShouldFire);
        Assert.True((brain.Mode & AiMode.Moving) != 0, "Pednarsh should be moving away");

        // Flee movement should point AWAY from the damage source (along -Z)
        Assert.True(decision.DesiredMovementDirection.Z < -0.9f,
            $"Expected movement away from threat (-Z), got {decision.DesiredMovementDirection}");
    }

    [Fact]
    public void Flee_Gonk_FleesWhenLowHealth()
    {
        // Gonk droid: has Flee instinct
        var gonkClass = new AiClass
        {
            Name = "gonk.ai",
            Alignment = 0.0f,
            FovDegrees = 210.0f,
            SightDistance = 6.0f,
            HeardDistance = 0.5f
        };
        gonkClass.Instincts.Add(new AiInstinct("SenseDanger", []));
        gonkClass.Instincts.Add(new AiInstinct("Flee", [10.0f, 10000.0f, 10.0f]));

        var brain = new AiBrain(gonkClass, homePosition: Vector3.Zero, initialMode: AiMode.Active);

        // Gonk drops to low health (20 HP out of 100)
        var context = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            Health = 20.0f,
            MaxHealth = 100.0f,
            WasDamaged = false,
            DamageSourcePosition = new Vector3(3.0f, 0, 0)
        };

        var decision = brain.Update(0.1f, context);

        Assert.True((brain.Mode & AiMode.Fleeing) != 0, "Gonk should flee when at low health");
        Assert.True(decision.ShouldFlee);
        Assert.True(decision.DesiredMovementDirection.X < -0.9f,
            $"Expected movement away from threat (-X), got {decision.DesiredMovementDirection}");
    }

    [Fact]
    public void Flee_Stormtrooper_DoesNotFleeWhenDamaged_FightsToDeath()
    {
        // Stormtrooper has combat instincts but NO Flee instinct
        var stClass = new AiClass
        {
            Name = "stdefault.ai",
            Alignment = -1.0f,
            FovDegrees = 170.0f,
            SightDistance = 5.0f,
            HeardDistance = 2.0f
        };
        stClass.Instincts.Add(new AiInstinct("LookForTarget", []));
        stClass.Instincts.Add(new AiInstinct("PrimaryFire", [2000.0f, 0.8f]));
        stClass.Instincts.Add(new AiInstinct("Follow", [2.0f]));

        var brain = new AiBrain(stClass, homePosition: Vector3.Zero, initialMode: AiMode.Attacking | AiMode.Active);

        // Stormtrooper is heavily damaged (10 HP out of 100)
        var targetPos = new Vector3(0, 0, 4.0f);
        var context = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            Health = 10.0f,
            MaxHealth = 100.0f,
            WasDamaged = true,
            DamageSourcePosition = targetPos,
            DirectTargetPosition = targetPos,
            DirectTargetIndex = 1
        };

        var decision = brain.Update(0.1f, context);

        // Stormtrooper should NEVER flee
        Assert.False((brain.Mode & AiMode.Fleeing) != 0, "Stormtrooper without Flee instinct must not flee");
        Assert.False(decision.ShouldFlee);
        Assert.True((brain.Mode & AiMode.Attacking) != 0, "Stormtrooper remains in combat");
    }

    #endregion

    #region 5. Retail GOB Integration Tests

    [Fact]
    public void RetailGob_ReeYees_PerceptionAndCombatTransitions()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var ry = AiClassReader.Load(archive, "rydefault.ai", difficulty: 1);

        Assert.Equal("rydefault.ai", ry.Name);
        Assert.Equal(200.0f, ry.FovDegrees);
        Assert.Equal(5.0f, ry.SightDistance);
        Assert.True(ry.HasInstinct("PrimaryFire"));
        Assert.True(ry.HasInstinct("Flee"));

        var brain = new AiBrain(ry, homePosition: Vector3.Zero, initialMode: AiMode.Active);

        // 1. Perception check: 200 deg FOV allows seeing sideways at 90 degrees
        Assert.True(brain.CanSeeTarget(Vector3.Zero, Vector3.UnitZ, new Vector3(4.0f, 0, 0)));
        // Beyond SightDistance: cannot see
        Assert.False(brain.CanSeeTarget(Vector3.Zero, Vector3.UnitZ, new Vector3(6.0f, 0, 0)));

        // 2. Combat behavior: spots Kyle ahead at 3.0m -> transitions to Attacking and fires
        var kylePos = new Vector3(0, 0, 3.0f);
        var combatContext = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            Health = 100.0f,
            DirectTargetPosition = kylePos,
            DirectTargetIndex = 0
        };

        var attackDecision = brain.Update(0.1f, combatContext);
        Assert.True((brain.Mode & AiMode.Attacking) != 0);
        Assert.True(attackDecision.ShouldFire);
        Assert.False(attackDecision.ShouldFlee);

        // 3. Flee behavior: Ree-Yees is damaged -> transitions to Fleeing
        var hurtContext = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            Health = 25.0f,
            MaxHealth = 100.0f,
            WasDamaged = true,
            DamageSourcePosition = kylePos,
            DirectTargetPosition = kylePos,
            DirectTargetIndex = 0
        };

        var fleeDecision = brain.Update(0.1f, hurtContext);
        Assert.True((brain.Mode & AiMode.Fleeing) != 0);
        Assert.False((brain.Mode & AiMode.Attacking) != 0);
        Assert.True(fleeDecision.ShouldFlee);
        Assert.False(fleeDecision.ShouldFire);
    }

    [Fact]
    public void RetailGob_Pednarsh_PerceptionAndFleeTransitions()
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var ped = AiClassReader.Load(archive, "pednarsh.ai", difficulty: 1);

        Assert.Equal("pednarsh.ai", ped.Name);
        Assert.Equal(165.0f, ped.FovDegrees);
        Assert.Equal(7.0f, ped.SightDistance);
        Assert.Equal(1.5f, ped.HeardDistance);
        Assert.True(ped.HasInstinct("Flee"));
        Assert.False(ped.HasInstinct("PrimaryFire"));

        var brain = new AiBrain(ped, homePosition: new Vector3(0, 0, 0), initialMode: AiMode.Active);

        // 1. Sight cone check: 165 deg FOV (half angle = 82.5 deg)
        // 90 deg (perpendicular) is OUTSIDE 165 deg cone
        Assert.False(brain.CanSeeTarget(Vector3.Zero, Vector3.UnitZ, new Vector3(4.0f, 0, 0)));
        // In front at 6.0m is INSIDE
        Assert.True(brain.CanSeeTarget(Vector3.Zero, Vector3.UnitZ, new Vector3(0, 0, 6.0f)));
        // In front at 8.0m is OUTSIDE (SightDistance = 7.0)
        Assert.False(brain.CanSeeTarget(Vector3.Zero, Vector3.UnitZ, new Vector3(0, 0, 8.0f)));

        // 2. Flee check: takes damage -> enters Fleeing mode immediately
        var context = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            WasDamaged = true,
            DamageSourcePosition = new Vector3(0, 0, 3.0f)
        };

        var decision = brain.Update(0.1f, context);
        Assert.True((brain.Mode & AiMode.Fleeing) != 0);
        Assert.True(decision.ShouldFlee);
        Assert.False(decision.ShouldFire);
        Assert.True(decision.DesiredMovementDirection.Z < 0f, "Should move away from damage source");
    }

    #endregion
}
