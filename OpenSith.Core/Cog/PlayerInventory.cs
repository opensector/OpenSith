using System;
using System.Collections.Generic;

namespace OpenSith.Core.Cog;

/// <summary>
/// Tracks Jedi Knight player vital statistics, weapon ownership, ammo reserves,
/// secrets, and mission goal flags backed by canonical Sith Engine inventory bins.
/// </summary>
public sealed class PlayerInventory
{
    public const int BinFists = 1;
    public const int BinBryar = 2;
    public const int BinStRifle = 3;
    public const int BinLightsaber = 10;
    public const int BinEnergy = 11;
    public const int BinPower = 12;
    public const int BinBattery = 13;
    public const int BinForceMana = 14;
    public const int BinRailCharges = 15;
    public const int BinNewStars = 16;
    public const int BinForceStars = 17;
    public const int BinChoice = 18;
    public const int BinForceRank = 20;
    public const int BinForceJump = 21;
    public const int BinForceSpeed = 22;
    public const int BinForceSeeing = 23;
    public const int BinForcePull = 24;
    public const int BinForceHeal = 25;
    public const int BinForcePersuasion = 26;
    public const int BinForceBlinding = 27;
    public const int BinForceAbsorb = 28;
    public const int BinForceProtection = 29;
    public const int BinForceThrow = 30;
    public const int BinForceGrip = 31;
    public const int BinForceLightning = 32;
    public const int BinForceDestruction = 33;
    public const int BinForceDeadlySight = 34;
    public const int BinShields = 60;
    public const int BinForceArmor = 61;
    public const int BinForceDarkSurge = 64;
    public const int BinForceLightSurge = 65;
    public const int BinSecrets = 70;
    public const int BinMaxSecrets = 71;
    public const int BinForceMorality = 74;
    public const int BinAlignment = 75;
    public const int BinStringOffset = 99;
    public const int BinGoalBase = 100;

    private readonly Dictionary<int, float> _bins = new();
    private readonly HashSet<int> _availableBins = new();
    private float _health = 100f;
    private float _maxHealth = 100f;

    /// <summary>Current player health (0..MaxHealth).</summary>
    public float Health
    {
        get => _health;
        set => _health = Math.Clamp(value, 0f, _maxHealth);
    }

    /// <summary>Maximum player health (default 100).</summary>
    public float MaxHealth
    {
        get => _maxHealth;
        set
        {
            _maxHealth = MathF.Max(1f, value);
            if (_health > _maxHealth)
                _health = _maxHealth;
        }
    }

    /// <summary>Current shield charge (0..200), mapped to bin 60.</summary>
    public float Shields
    {
        get => GetInv(BinShields);
        set => SetInv(BinShields, value);
    }

    /// <summary>Energy cell ammo (0..500), mapped to bin 11.</summary>
    public float EnergyAmmo
    {
        get => GetInv(BinEnergy);
        set => SetInv(BinEnergy, value);
    }

    /// <summary>Power cell ammo (0..500), mapped to bin 12.</summary>
    public float PowerAmmo
    {
        get => GetInv(BinPower);
        set => SetInv(BinPower, value);
    }

    /// <summary>Current Force mana (0..400), mapped to bin 14.</summary>
    public float ForceMana
    {
        get => GetInv(BinForceMana);
        set => SetInv(BinForceMana, value);
    }

    /// <summary>Maximum Force mana based on current Jedi Rank (ForceRank * 50, up to 400).</summary>
    public float MaxForceMana => ForceRank * 50f;

    /// <summary>Spendable Force stars (0..32), mapped to bin 17.</summary>
    public int ForceStars
    {
        get => (int)GetInv(BinForceStars);
        set => SetInv(BinForceStars, value);
    }

    /// <summary>Current Jedi Rank (0..8), mapped to bin 20.</summary>
    public int ForceRank
    {
        get => (int)GetInv(BinForceRank);
        set => SetInv(BinForceRank, value);
    }

    /// <summary>Number of secret areas found in the current level, mapped to bin 70.</summary>
    public int SecretsFound
    {
        get => (int)GetInv(BinSecrets);
        set => SetInv(BinSecrets, value);
    }

    /// <summary>Total secret areas in the current level, mapped to bin 71.</summary>
    public int TotalSecrets
    {
        get => (int)GetInv(BinMaxSecrets);
        set => SetInv(BinMaxSecrets, value);
    }

    /// <summary>Bitmask flags per mission goal index (e.g. 0x1 displayed, 0x2 completed).</summary>
    public Dictionary<int, int> GoalFlags { get; } = new();

    /// <summary>Whether the player possesses the lightsaber (bin 10).</summary>
    public bool HasLightsaber
    {
        get => GetInv(BinLightsaber) > 0f;
        set => SetInv(BinLightsaber, value ? 1f : 0f);
    }

    /// <summary>Whether the player possesses the Bryar pistol (bin 2).</summary>
    public bool HasBryar
    {
        get => GetInv(BinBryar) > 0f;
        set => SetInv(BinBryar, value ? 1f : 0f);
    }

    /// <summary>True when health has dropped to zero.</summary>
    public bool IsDead => _health <= 0f;

    /// <summary>
    /// Applies damage, absorbing into shields before reducing health.
    /// </summary>
    public void ApplyDamage(float amount)
    {
        if (amount <= 0f || IsDead)
            return;

        float shields = Shields;
        if (shields > 0f)
        {
            float absorbed = MathF.Min(shields, amount);
            Shields = shields - absorbed;
            amount -= absorbed;
        }

        Health = _health - amount;
    }

    /// <summary>
    /// Restores health up to <see cref="MaxHealth"/>.
    /// </summary>
    public void Heal(float amount)
    {
        if (amount <= 0f)
            return;

        Health = _health + amount;
    }

    /// <summary>
    /// Adds shield energy up to the 200 maximum.
    /// </summary>
    public void AddShields(float amount)
    {
        if (amount <= 0f)
            return;

        Shields = Shields + amount;
    }

    /// <summary>
    /// Passively regenerates Force mana scaled by Jedi Rank (<see cref="ForceRank"/> * 0.5 mana/sec),
    /// capped at <see cref="MaxForceMana"/> (<see cref="ForceRank"/> * 50), matching retail force_well.cog.
    /// </summary>
    public void UpdateForceMana(float dt)
    {
        if (dt <= 0f || IsDead)
            return;

        int rank = ForceRank;
        if (rank <= 0)
            return;

        float maxMana = MaxForceMana;
        float current = ForceMana;
        if (current < maxMana)
        {
            float regenerated = current + (rank * 0.5f) * dt;
            ForceMana = MathF.Min(regenerated, maxMana);
        }
        else if (current > maxMana)
        {
            ForceMana = maxMana;
        }
    }

    /// <summary>
    /// Resets vital stats (health and shields) for player respawn while preserving level state.
    /// </summary>
    public void ResetForRespawn()
    {
        _health = _maxHealth;
        Shields = 0f;
    }

    /// <summary>Gets the current value of an inventory bin.</summary>
    public float GetInv(int bin) => _bins.GetValueOrDefault(bin, 0f);

    /// <summary>Sets an inventory bin value, clamped to its valid range.</summary>
    public void SetInv(int bin, float value)
    {
        float clamped = Math.Clamp(value, GetInvMin(bin), GetInvMax(bin));
        _bins[bin] = clamped;

        if (bin >= BinGoalBase && bin < BinGoalBase + 16)
            GoalFlags[bin - BinGoalBase] = (int)clamped;
    }

    /// <summary>Adjusts an inventory bin by <paramref name="delta"/> and returns the new value.</summary>
    public float ChangeInv(int bin, float delta)
    {
        float updated = GetInv(bin) + delta;
        SetInv(bin, updated);
        return GetInv(bin);
    }

    /// <summary>Sets whether an inventory bin is available to the player.</summary>
    public void SetInvAvailable(int bin, bool available)
    {
        if (available)
            _availableBins.Add(bin);
        else
            _availableBins.Remove(bin);
    }

    /// <summary>Returns whether an inventory bin is marked available to the player.</summary>
    public bool IsInvAvailable(int bin) => _availableBins.Contains(bin);

    /// <summary>Gets the minimum allowed value for an inventory bin.</summary>
    public static float GetInvMin(int bin) => bin switch
    {
        74 => -40f,
        75 => -100f,
        _ => 0f
    };

    /// <summary>Gets the maximum allowed value for an inventory bin per retail items.dat.</summary>
    public static float GetInvMax(int bin) => bin switch
    {
        >= 1 and <= 3 => 1f,
        4 => 30f,
        >= 5 and <= 7 => 1f,
        8 => 30f,
        9 or 10 => 1f,
        11 or 12 => 500f,
        13 => 200f,
        14 => 400f,
        15 => 30f,
        16 or 17 => 32f,
        18 => 2f,
        20 => 8f,
        >= 21 and <= 34 => 4f,
        40 => 5f,
        >= 41 and <= 51 => 1f,
        60 or 61 => 200f,
        62 => 1f,
        63 => 2f,
        >= 64 and <= 66 => 1f,
        70 or 71 => 20f,
        74 => 40f,
        75 => 100f,
        99 => 99999f,
        _ => 1000f
    };

    /// <summary>Sets (ORs) goal flags for the specified goal index.</summary>
    public void SetGoalFlags(int goalIdx, int flags)
    {
        int updated = GetGoalFlags(goalIdx) | flags;
        GoalFlags[goalIdx] = updated;
        if (goalIdx is >= 0 and < 16)
            _bins[BinGoalBase + goalIdx] = updated;
    }

    /// <summary>Clears (AND-NOTs) goal flags for the specified goal index.</summary>
    public void ClearGoalFlags(int goalIdx, int flags)
    {
        int updated = GetGoalFlags(goalIdx) & ~flags;
        GoalFlags[goalIdx] = updated;
        if (goalIdx is >= 0 and < 16)
            _bins[BinGoalBase + goalIdx] = updated;
    }

    /// <summary>Gets the current bitmask flags for the specified goal index.</summary>
    public int GetGoalFlags(int goalIdx) => GoalFlags.GetValueOrDefault(goalIdx, 0);
}
