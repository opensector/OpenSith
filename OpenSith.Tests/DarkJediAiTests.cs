using System;
using System.IO;
using System.Numerics;
using OpenSith.Core.AI;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class DarkJediAiTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    private static AiClass LoadYunAi(int difficulty)
    {
        if (!File.Exists(SteamRes2Path))
            throw new FileNotFoundException($"Retail Res2.gob not found at {SteamRes2Path}");

        using var gob = GobArchive.FromFile(SteamRes2Path);
        return AiClassReader.Load(gob, "misc/ai/yun.ai", difficulty);
    }

    [Fact]
    public void Synthetic_ParseDarkJediInstincts_PopulatesAllConfigs()
    {
        const string aiText = """
            alignment=-1.0, rank=0.5, fov=180, maxstep=1.5, sightdist=7.0, heardist=2.5
            Saberfighting   0.40, 0.25, 20.0, 1600.0, 0.25, 30.0, 925.0
            ForcePowers     5000.0, 0.0, 5.0, 0.0, 4.0, 0.0, 0.0, 0.20, 0.60, 0.0, 12000.0, 15000.0, 0.0
            SpecialAttack   4000.0, 0.50, 0.5, 2.0, 2667.0, 9.0, 0, 40.0, 5000.0
            CircleStrafe    5000.0, 30.0, 0.6, 500.0, 0.0
            """;

        var ai = AiClassReader.Parse(aiText, "synthetic_yun.ai");

        Assert.True(ai.HasSaberfighting);
        Assert.True(ai.HasForcePowers);
        Assert.True(ai.HasSpecialAttack);
        Assert.True(ai.HasCircleStrafe);

        Assert.NotNull(ai.Saberfighting);
        Assert.Equal(0.40f, ai.Saberfighting!.SkipChance);
        Assert.Equal(0.25f, ai.Saberfighting.Att1Range);
        Assert.Equal(20.0f, ai.Saberfighting.Att1Damage);
        Assert.Equal(1600.0f, ai.Saberfighting.Att1CooldownMs);
        Assert.Equal(0.25f, ai.Saberfighting.Att2Range);
        Assert.Equal(30.0f, ai.Saberfighting.Att2Damage);
        Assert.Equal(925.0f, ai.Saberfighting.Att2CooldownMs);

        Assert.NotNull(ai.ForcePowers);
        Assert.Equal(5.0f, ai.ForceCheckIntervalSec);
        Assert.Equal(5000.0f, ai.ForcePowers!.IntervalMs);
        Assert.Equal(0.0f, ai.ForcePowers.Power1.MinDist);
        Assert.Equal(5.0f, ai.ForcePowers.Power1.MaxDist);
        Assert.Equal(0.20f, ai.ForcePowers.Power1.Probability);
        Assert.Equal(12000.0f, ai.ForcePowers.Power1.CooldownMs);
        Assert.Equal(0.0f, ai.ForcePowers.Power2.MinDist);
        Assert.Equal(4.0f, ai.ForcePowers.Power2.MaxDist);
        Assert.Equal(0.60f, ai.ForcePowers.Power2.Probability);
        Assert.Equal(15000.0f, ai.ForcePowers.Power2.CooldownMs);

        Assert.NotNull(ai.SpecialAttack);
        Assert.Equal(4000.0f, ai.SpecialAttack!.IntervalMs);
        Assert.Equal(0.50f, ai.SpecialAttack.Probability);
        Assert.Equal(0.5f, ai.SpecialAttack.MinDist);
        Assert.Equal(2.0f, ai.SpecialAttack.MaxDist);
        Assert.Equal(2667.0f, ai.SpecialAttack.DurationMs);
        Assert.Equal(9, ai.SpecialAttack.AnimMode);
        Assert.Equal(40.0f, ai.SpecialAttack.Damage);
        Assert.Equal(5000.0f, ai.SpecialAttack.CooldownMs);

        Assert.NotNull(ai.CircleStrafe);
        Assert.Equal(5000.0f, ai.CircleStrafe!.IntervalMs);
        Assert.Equal(30.0f, ai.CircleStrafe.AngleDeg);
        Assert.Equal(0.6f, ai.CircleStrafe.Speed);
        Assert.Equal(500.0f, ai.CircleStrafe.DurationMs);
        Assert.False(ai.CircleStrafe.RandomStrafe);
    }

    [Theory]
    [InlineData(0, 0.40f, 15.0f, 1800.0f, 20.0f, 1000.0f, 15000.0f, 18000.0f, 30.0f)] // Easy: yun.ai0
    [InlineData(1, 0.40f, 20.0f, 1600.0f, 30.0f, 925.0f, 12000.0f, 15000.0f, 40.0f)]  // Medium: yun.ai
    [InlineData(2, 0.30f, 30.0f, 1500.0f, 30.0f, 850.0f, 11000.0f, 13000.0f, 50.0f)]  // Hard: yun.ai2
    public void Retail_YunAi_AllDifficulties_MatchExactInstinctValues(
        int difficulty,
        float expectedSkipChance,
        float expectedAtt1Damage,
        float expectedAtt1CooldownMs,
        float expectedAtt2Damage,
        float expectedAtt2CooldownMs,
        float expectedFp1CooldownMs,
        float expectedFp2CooldownMs,
        float expectedSpecialDamage)
    {
        if (!File.Exists(SteamRes2Path))
            return;

        var yun = LoadYunAi(difficulty);

        Assert.Equal(-1.0f, yun.Alignment);
        Assert.Equal(0.5f, yun.Rank);
        Assert.Equal(180.0f, yun.FovDegrees);
        Assert.Equal(1.5f, yun.MaxStep);
        Assert.Equal(7.0f, yun.SightDistance);
        Assert.Equal(2.5f, yun.HeardDistance);

        Assert.True(yun.HasSaberfighting);
        Assert.True(yun.HasForcePowers);
        Assert.True(yun.HasSpecialAttack);
        Assert.True(yun.HasCircleStrafe);

        Assert.Equal(expectedSkipChance, yun.Saberfighting!.SkipChance);
        Assert.Equal(0.25f, yun.Saberfighting.Att1Range);
        Assert.Equal(expectedAtt1Damage, yun.Saberfighting.Att1Damage);
        Assert.Equal(expectedAtt1CooldownMs, yun.Saberfighting.Att1CooldownMs);
        Assert.Equal(0.25f, yun.Saberfighting.Att2Range);
        Assert.Equal(expectedAtt2Damage, yun.Saberfighting.Att2Damage);
        Assert.Equal(expectedAtt2CooldownMs, yun.Saberfighting.Att2CooldownMs);

        Assert.Equal(5.0f, yun.ForceCheckIntervalSec);
        Assert.Equal(0.0f, yun.ForcePowers!.Power1.MinDist);
        Assert.Equal(5.0f, yun.ForcePowers.Power1.MaxDist);
        Assert.Equal(0.20f, yun.ForcePowers.Power1.Probability);
        Assert.Equal(expectedFp1CooldownMs, yun.ForcePowers.Power1.CooldownMs);
        Assert.Equal(0.0f, yun.ForcePowers.Power2.MinDist);
        Assert.Equal(4.0f, yun.ForcePowers.Power2.MaxDist);
        Assert.Equal(0.60f, yun.ForcePowers.Power2.Probability);
        Assert.Equal(expectedFp2CooldownMs, yun.ForcePowers.Power2.CooldownMs);

        Assert.Equal(4000.0f, yun.SpecialAttack!.IntervalMs);
        Assert.Equal(0.50f, yun.SpecialAttack.Probability);
        Assert.Equal(0.5f, yun.SpecialAttack.MinDist);
        Assert.Equal(2.0f, yun.SpecialAttack.MaxDist);
        Assert.Equal(2667.0f, yun.SpecialAttack.DurationMs);
        Assert.Equal(9, yun.SpecialAttack.AnimMode);
        Assert.Equal(expectedSpecialDamage, yun.SpecialAttack.Damage);
        Assert.Equal(5000.0f, yun.SpecialAttack.CooldownMs);

        Assert.Equal(5000.0f, yun.CircleStrafe!.IntervalMs);
        Assert.Equal(30.0f, yun.CircleStrafe.AngleDeg);
        Assert.Equal(0.6f, yun.CircleStrafe.Speed);
        Assert.Equal(500.0f, yun.CircleStrafe.DurationMs);
        Assert.False(yun.CircleStrafe.RandomStrafe);
    }

    [Fact]
    public void YunBrain_PauseFlag0x2000_FreezesAndUnfreezesCleanly()
    {
        var yun = AiClassReader.Parse("""
            alignment=-1.0, fov=180, sightdist=7.0
            Saberfighting 0.0, 0.25, 20.0, 1600.0, 0.25, 30.0, 925.0
            ForcePowers 5000.0, 0.0, 5.0, 0.0, 4.0, 0.0, 0.0, 1.0, 0.0, 0.0, 12000.0, 15000.0, 0.0
            """, "yun.ai");

        var brain = new AiBrain(yun, rng: new Random(1234));
        var context = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            DirectTargetPosition = new Vector3(0, 0, 0.20f),
            DirectTargetIndex = 1
        };

        // Simulate 07_rahnlines.cog: AiSetMode(yun, 0x2000)
        brain.SetAiModeFlags(0x2000u);
        Assert.True(brain.IsAiPaused);

        var pausedDecision = brain.Update(0.1f, context);
        Assert.False(pausedDecision.HasMovement);
        Assert.False(brain.SaberAttackRequested);
        Assert.False(brain.SpecialAttackRequested);
        Assert.Equal(0, brain.ForcePowerRequested);

        // Simulate 07_rahnlines.cog: AiClearMode(yun, 0x2000)
        brain.ClearAiModeFlags(0x2000u);
        Assert.False(brain.IsAiPaused);

        var activeDecision = brain.Update(0.1f, context);
        Assert.True((activeDecision.NewMode & AiMode.Attacking) != 0);
        Assert.True(brain.SaberAttackRequested);
        Assert.Equal(1, brain.SaberAttackIndex);
        Assert.Equal(20.0f, brain.SaberAttackDamage);
        Assert.Equal(1, brain.ForcePowerRequested);
    }

    [Fact]
    public void YunBrain_Saberfighting_AlternatesAttack1And2WithCooldowns()
    {
        var yun = File.Exists(SteamRes2Path)
            ? LoadYunAi(1)
            : AiClassReader.Parse("alignment=-1.0, fov=180, sightdist=7.0\nSaberfighting 0.40, 0.25, 20.0, 1600.0, 0.25, 30.0, 925.0", "yun.ai");

        var brain = new AiBrain(yun, rng: new Random(42));
        var meleeContext = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            DirectTargetPosition = new Vector3(0, 0, 0.20f),
            DirectTargetIndex = 1
        };

        bool sawAtt1 = false;
        bool sawAtt2 = false;

        // Step in 0.1s increments; SkipChance=0.40 may skip occasionally, so run up to 6s
        for (int i = 0; i < 60; i++)
        {
            brain.Update(0.1f, meleeContext);
            if (brain.SaberAttackRequested)
            {
                if (brain.SaberAttackIndex == 1)
                {
                    sawAtt1 = true;
                    Assert.Equal(20.0f, brain.SaberAttackDamage);
                    Assert.Equal(0.25f, brain.SaberAttackRange);
                }
                else if (brain.SaberAttackIndex == 2)
                {
                    sawAtt2 = true;
                    Assert.Equal(30.0f, brain.SaberAttackDamage);
                    Assert.Equal(0.25f, brain.SaberAttackRange);
                }
            }
        }

        Assert.True(sawAtt1, "Expected Saberfighting Attack 1 to trigger.");
        Assert.True(sawAtt2, "Expected Saberfighting Attack 2 to trigger.");
    }

    [Fact]
    public void YunBrain_CircleStrafe_SpecialAttack_And_ForcePowers_TriggerInRange()
    {
        var yun = File.Exists(SteamRes2Path)
            ? LoadYunAi(1)
            : AiClassReader.Parse("""
                alignment=-1.0, fov=180, sightdist=7.0
                Saberfighting 0.40, 0.25, 20.0, 1600.0, 0.25, 30.0, 925.0
                ForcePowers 5000.0, 0.0, 5.0, 0.0, 4.0, 0.0, 0.0, 0.20, 0.60, 0.0, 12000.0, 15000.0, 0.0
                SpecialAttack 4000.0, 0.50, 0.5, 2.0, 2667.0, 9.0, 0, 40.0, 5000.0
                CircleStrafe 5000.0, 30.0, 0.6, 500.0, 0.0
                """, "yun.ai");

        var brain = new AiBrain(yun, rng: new Random(7));
        // Distance = 1.2f is inside SpecialAttack [0.5, 2.0], ForcePowers [0, 4.0]/[0, 5.0], and CircleStrafe <= 3.0
        var midRangeContext = new AiWorldContext
        {
            SelfPosition = Vector3.Zero,
            SelfLookDir = Vector3.UnitZ,
            DirectTargetPosition = new Vector3(0, 0, 1.2f),
            DirectTargetIndex = 1
        };

        bool sawCircleStrafe = false;
        bool sawSpecialAttack = false;
        bool sawForcePower = false;

        for (int i = 0; i < 120; i++)
        {
            var decision = brain.Update(0.1f, midRangeContext);
            if (brain.IsCircleStrafing)
            {
                sawCircleStrafe = true;
                Assert.True(decision.HasMovement);
            }

            if (brain.SpecialAttackRequested)
            {
                sawSpecialAttack = true;
                Assert.Equal(9, brain.SpecialAttackAnimMode);
                Assert.Equal(40.0f, brain.SpecialAttackDamage);
            }

            if (brain.ForcePowerRequested is 1 or 2)
            {
                sawForcePower = true;
            }
        }

        Assert.True(sawCircleStrafe, "Expected CircleStrafe to activate in close/mid combat.");
        Assert.True(sawSpecialAttack, "Expected SpecialAttack (YUflip.key) to trigger in [0.5, 2.0] range.");
        Assert.True(sawForcePower, "Expected ForcePowerRequested (1 or 2) to emit for boss COG user0.");
    }

    [Fact]
    public void Synthetic_ParseRoamInstinct_PopulatesRoamConfig()
    {
        const string aiText = """
            alignment=0.0, rank=0.5, fov=210, maxstep=0.0, sightdist=6.0, heardist=0.5
            SenseDanger 100.0, 0.00
            Flee 10.0, 10000.0, 10.0
            Roam 2000.0, 1.0
            """;

        var ai = AiClassReader.Parse(aiText, "synthetic_gonkroam.ai");

        Assert.True(ai.HasRoam);
        Assert.NotNull(ai.RoamConfig);
        Assert.Equal(2000.0f, ai.RoamConfig!.IntervalMs);
        Assert.Equal(2.0f, ai.RoamConfig.IntervalSec);
        Assert.Equal(1.0f, ai.RoamConfig.MaxDist);
    }

    [Fact]
    public void Synthetic_RoamBrain_WandersAroundHomeWithSpeedScaleAndPauses()
    {
        var ai = AiClassReader.Parse("""
            alignment=0.0, fov=210, sightdist=6.0
            Roam 2000.0, 1.0
            """, "roam_test.ai");

        var home = new Vector3(10f, 0f, -5f);
        var pos = home;
        var brain = new AiBrain(ai, homePosition: home, rng: new Random(42));

        bool sawMovement = false;
        bool sawIdlePause = false;
        float maxDistFromHome = 0f;

        for (int i = 0; i < 200; i++)
        {
            var decision = brain.Update(0.1f, new AiWorldContext
            {
                SelfPosition = pos,
                SelfLookDir = Vector3.UnitZ
            });

            if (decision.HasMovement)
            {
                sawMovement = true;
                Assert.InRange(decision.DesiredMovementDirection.Length(), 0.49f, 0.51f);
                Assert.True((decision.NewMode & AiMode.Moving) != 0);
                pos += decision.DesiredMovementDirection * 0.1f;
            }
            else
            {
                sawIdlePause = true;
                Assert.True((decision.NewMode & AiMode.Moving) == 0);
            }

            float dist = Vector3.Distance(pos, home);
            if (dist > maxDistFromHome)
                maxDistFromHome = dist;
        }

        Assert.True(sawMovement, "Expected Roam AI to emit movement.");
        Assert.True(sawIdlePause, "Expected Roam AI to include brief idle pauses.");
        Assert.InRange(maxDistFromHome, 0.05f, 1.5f);

        // Pausing AI must stop roaming movement
        brain.SetAiModeFlags(AiMode.Disabled);
        var pausedDecision = brain.Update(0.1f, new AiWorldContext
        {
            SelfPosition = pos,
            SelfLookDir = Vector3.UnitZ
        });
        Assert.False(pausedDecision.HasMovement);
    }

    [Theory]
    [InlineData("misc/ai/gonkroam.ai")]
    [InlineData("misc/ai/drroam.ai")]
    public void Retail_RoamDroids_ParseAndEmitMovementOverTime(string aiPath)
    {
        if (!File.Exists(SteamRes2Path))
            return;

        using var gob = GobArchive.FromFile(SteamRes2Path);
        var ai = AiClassReader.Load(gob, aiPath, difficulty: 1);

        Assert.True(ai.HasRoam);
        Assert.NotNull(ai.RoamConfig);
        Assert.Equal(2000.0f, ai.RoamConfig!.IntervalMs);
        Assert.Equal(1.0f, ai.RoamConfig.MaxDist);

        var home = Vector3.Zero;
        var pos = home;
        var brain = new AiBrain(ai, homePosition: home, rng: new Random(123));

        bool sawMovement = false;
        for (int i = 0; i < 60; i++)
        {
            var decision = brain.Update(0.1f, new AiWorldContext
            {
                SelfPosition = pos,
                SelfLookDir = Vector3.UnitZ
            });

            if (decision.HasMovement)
            {
                sawMovement = true;
                pos += decision.DesiredMovementDirection * 0.1f;
            }
        }

        Assert.True(sawMovement, $"Expected retail {aiPath} to produce roaming movement over time.");
    }
}
