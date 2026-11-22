using System.Collections.Generic;
using System.Numerics;
using OpenSith.Core.Cog;
using OpenSith.Core.Data;

namespace OpenSith.Core.Simulation;

/// <summary>
/// Per-player simulation state isolated by player Thing ID so
/// <see cref="WorldSimulation"/> supports single-player, listen-server,
/// and dedicated-server multi-player sessions (<c>0..N</c> players).
/// </summary>
public sealed class PlayerState
{
    public int ThingIndex { get; init; }
    public int PlayerSlot { get; init; }
    public string Name { get; set; } = string.Empty;
    public int Team { get; set; }
    public int Score { get; set; }
    public int Kills { get; set; }
    public int Killed { get; set; }
    public int Suicides { get; set; }

    public PlayerInventory Inventory { get; } = new();
    public Dictionary<int, int> InvActivated { get; } = new();
    public Dictionary<int, float> ActiveBinStartTime { get; } = new();
    public Dictionary<int, (float Interval, float Remaining)> ActiveBinFireTimers { get; } = new();
    public Dictionary<int, float> BinWaitTimers { get; } = new();
    public int SuperFlags { get; set; }
    public int? AlignmentChoice { get; set; }

    public int PrevSectorIndex { get; set; } = -1;
    public Vector3 WorldPosition { get; set; }
    public bool HasInputPosition { get; set; }
    public Vector3 AimDirection { get; set; } = Vector3.UnitY;
    public bool HasAimDirection { get; set; }
    public Vector3 Thrust { get; set; }
    public bool IsGrounded { get; set; } = true;
    public bool IsUnderwater { get; set; }
    public int ArmedMode { get; set; } = 1;
    public bool HasVelocityOverride { get; set; }
    public Vector3 VelocityOverride { get; set; }
    public Vector3 PendingPlatformDelta { get; set; }

    // Weapons, POV viewmodel & saber swing state
    public int CurWeaponBin { get; set; } = PlayerInventory.BinBryar;
    public int CurInvWeaponBin { get; set; } = PlayerInventory.BinBryar;
    public int ActiveFireMode { get; set; } = -1;
    public float FireRate { get; set; }
    public float FireWait { get; set; }
    public float MountWait { get; set; }
    public float FireHoldElapsed { get; set; }
    public bool FiredThisTick { get; set; }
    public bool SaberDamageActive { get; set; }
    public float SaberActiveTimer { get; set; }
    public float SaberDamage { get; set; }
    public float SaberCollideLength { get; set; }
    public float SaberStunLength { get; set; }
    public HashSet<int> SaberHitThisSwing { get; } = [];
    public string PovModel { get; set; } = "bryv.3do";
    public string PovKey { get; set; } = "bryvpst1.key";
    public float PovKeyTime { get; set; }
    public bool PovKeyLoop { get; set; } = true;
    public bool PovKeyEndPause { get; set; }
    public int PovTrackId { get; set; }
    public Vector3 PovWaggleMove { get; set; }
    public float PovWaggleSpeed { get; set; }
    public Vector3 PovShakePos { get; set; }
    public Vector3 PovShakeRot { get; set; }
    public float PovShakeTimer { get; set; }
    public float PovShakeDuration { get; set; }
    public float PovShakeIntensity { get; set; }

    // Per-player COG ownership
    public CogInstance? ClassCog { get; set; }
    public Dictionary<int, CogInstance> BinCogs { get; } = new();

    // Appearance & customization loadout (synced SP & MP)
    public int ModelIndex { get; set; }
    public int SaberColorIndex { get; set; } = PlayerCustomizationCatalog.DefaultSaberColorIndex;
    public PlayerLoadoutConfig? Loadout { get; set; }
    public bool SandboxWeaponsGranted { get; set; }

    // Per-player notifications & trigger tracking
    public string? StatusMessage { get; set; }
    public float StatusTimer { get; set; }
    public HashSet<int> TouchedCapturedThings { get; } = [];
    public int SelectedItemBin { get; set; } = PlayerInventory.BinBacta;
    public int SelectedForceBin { get; set; } = PlayerInventory.BinForceJump;
}
