using System;
using System.Collections.Generic;

namespace OpenSith.Core.Audio;

/// <summary>
/// Sith Engine sound class event modes (SITH_SC).
/// Indices 0..95 match LucasArts Sith Engine internal sound class tables.
/// </summary>
public enum SoundEvent
{
    Reserved = 0,
    Create = 1,
    Activate = 2,
    StartMove = 3,
    StopMove = 4,
    Moving = 5,
    LWalkHard = 6,
    RWalkHard = 7,
    LRunHard = 8,
    RRunHard = 9,
    LWalkMetal = 10,
    RWalkMetal = 11,
    LRunMetal = 12,
    RRunMetal = 13,
    LWalkWater = 14,
    RWalkWater = 15,
    LRunWater = 16,
    RRunWater = 17,
    LWalkPuddle = 18,
    RWalkPuddle = 19,
    LRunPuddle = 20,
    RRunPuddle = 21,
    LWalkEarth = 22,
    RWalkEarth = 23,
    LRunEarth = 24,
    RRunEarth = 25,
    EnterWater = 26,
    EnterWaterSlow = 27,
    ExitWater = 28,
    ExitWaterSlow = 29,
    LSwimSurface = 30,
    RSwimSurface = 31,
    TreadSurface = 32,
    LSwimUnder = 33,
    RSwimUnder = 34,
    TreadUnder = 35,
    Jump = 36,
    JumpMetal = 37,
    JumpWater = 38,
    JumpEarth = 39,
    LandHard = 40,
    LandMetal = 41,
    LandWater = 42,
    LandPuddle = 43,
    LandEarth = 44,
    LandHurt = 45,
    HitHard = 46,
    HitMetal = 47,
    HitEarth = 48,
    Deflected = 49,
    ScrapeHard = 50,
    ScrapeMetal = 51,
    ScrapeEarth = 52,
    HitDamaged = 53,
    Falling = 54,
    CorpseHit = 55,
    HurtImpact = 56,
    HurtEnergy = 57,
    HurtFire = 58,
    HurtMagic = 59,
    HurtSpecial = 60,
    Drowning = 61,
    Choking = 62,
    Death1 = 63,
    Death2 = 64,
    DeathUnder = 65,
    Drowned = 66,
    Splattered = 67,
    Pant = 68,
    Breath = 69,
    Gasp = 70,
    Fire1 = 71,
    Fire2 = 72,
    Fire3 = 73,
    Fire4 = 74,
    Curious = 75,
    Alert = 76,
    Idle = 77,
    Gloat = 78,
    Fear = 79,
    Boast = 80,
    Happy = 81,
    Victory = 82,
    Help = 83,
    Flee = 84,
    Search = 85,
    Calm = 86,
    Surprise = 87,
    Reserved1 = 88,
    Reserved2 = 89,
    Reserved3 = 90,
    Reserved4 = 91,
    Reserved5 = 92,
    Reserved6 = 93,
    Reserved7 = 94,
    Reserved8 = 95,
}

/// <summary>
/// String conversion and alias resolution helpers for <see cref="SoundEvent"/>.
/// </summary>
public static class SoundEventExtensions
{
    private static readonly string[] StandardNames =
    {
        "--reserved--",
        "create",
        "activate",
        "startmove",
        "stopmove",
        "moving",
        "lwalkhard",
        "rwalkhard",
        "lrunhard",
        "rrunhard",
        "lwalkmetal",
        "rwalkmetal",
        "lrunmetal",
        "rrunmetal",
        "lwalkwater",
        "rwalkwater",
        "lrunwater",
        "rrunwater",
        "lwalkpuddle",
        "rwalkpuddle",
        "lrunpuddle",
        "rrunpuddle",
        "lwalkearth",
        "rwalkearth",
        "lrunearth",
        "rrunearth",
        "enterwater",
        "enterwaterslow",
        "exitwater",
        "exitwaterslow",
        "lswimsurface",
        "rswimsurface",
        "treadsurface",
        "lswimunder",
        "rswimunder",
        "treadunder",
        "jump",
        "jumpmetal",
        "jumpwater",
        "jumpearth",
        "landhard",
        "landmetal",
        "landwater",
        "landpuddle",
        "landearth",
        "landhurt",
        "hithard",
        "hitmetal",
        "hitearth",
        "deflected",
        "scrapehard",
        "scrapemetal",
        "scrapeearth",
        "hitdamaged",
        "falling",
        "corpsehit",
        "hurtimpact",
        "hurtenergy",
        "hurtfire",
        "hurtmagic",
        "hurtspecial",
        "drowning",
        "choking",
        "death1",
        "death2",
        "deathunder",
        "drowned",
        "splattered",
        "pant",
        "breath",
        "gasp",
        "fire1",
        "fire2",
        "fire3",
        "fire4",
        "curious",
        "alert",
        "idle",
        "gloat",
        "fear",
        "boast",
        "happy",
        "victory",
        "help",
        "flee",
        "search",
        "calm",
        "surprise",
        "reserved1",
        "reserved2",
        "reserved3",
        "reserved4",
        "reserved5",
        "reserved6",
        "reserved7",
        "reserved8",
    };

    private static readonly Dictionary<string, SoundEvent> NameToEventMap = new(StringComparer.OrdinalIgnoreCase);

    static SoundEventExtensions()
    {
        for (int i = 0; i < StandardNames.Length; i++)
        {
            NameToEventMap[StandardNames[i]] = (SoundEvent)i;
        }

        // Retail asset typos / aliases
        NameToEventMap["suprise"] = SoundEvent.Surprise;
        NameToEventMap["choked"] = SoundEvent.Choking;
    }

    /// <summary>Gets the canonical Sith Engine string name for this sound event.</summary>
    public static string ToKey(this SoundEvent evt)
    {
        int idx = (int)evt;
        return (idx >= 0 && idx < StandardNames.Length) ? StandardNames[idx] : evt.ToString().ToLowerInvariant();
    }

    /// <summary>Parses a sound event name string into a <see cref="SoundEvent"/>.</summary>
    public static bool TryParse(string name, out SoundEvent soundEvent)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            soundEvent = SoundEvent.Reserved;
            return false;
        }

        return NameToEventMap.TryGetValue(name.Trim(), out soundEvent);
    }
}
