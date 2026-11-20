using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;

namespace OpenSith.Core.AI;

/// <summary>
/// Parser and loader for Sith Engine AI Class definition (.AI) files.
/// Fully supports difficulty-based variations (.ai0, .ai2), path resolution, and retail Sith Engine quirks.
/// </summary>
public static class AiClassReader
{
    private static readonly char[] Delimiters = [' ', '\t', ','];

    /// <summary>
    /// Parses an AI class from raw text content.
    /// </summary>
    public static AiClass Parse(string text, string name = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        var aiClass = new AiClass { Name = name };

        using var reader = new StringReader(text);
        string? rawLine;

        while ((rawLine = reader.ReadLine()) != null)
        {
            string line = StripComments(rawLine).Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                continue;

            if (IsHeaderLine(tokens))
            {
                ParseHeader(tokens, aiClass);
            }
            else
            {
                var instinct = ParseInstinct(tokens);
                if (instinct != null)
                {
                    aiClass.Instincts.Add(instinct);
                    ApplySpecialInstinctConfig(aiClass, instinct);
                }
            }
        }

        return aiClass;
    }

    /// <summary>
    /// Reads and parses an AI class from a stream.
    /// </summary>
    public static AiClass Read(Stream stream, string name = "")
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return Parse(reader.ReadToEnd(), name);
    }

    /// <summary>
    /// Loads an AI class from a Virtual File System, accounting for game difficulty (0=Easy, 1=Medium, 2=Hard).
    /// </summary>
    public static AiClass Load(IVirtualFileSystem vfs, string path, int difficulty = 1)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(path);

        string resolvedPath = ResolvePath(vfs.FileExists, path, difficulty);
        string text = vfs.ReadAllText(resolvedPath);
        return Parse(text, Path.GetFileName(path));
    }

    /// <summary>
    /// Loads an AI class from a GOB archive, accounting for game difficulty (0=Easy, 1=Medium, 2=Hard).
    /// </summary>
    public static AiClass Load(GobArchive archive, string path, int difficulty = 1)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(path);

        string resolvedPath = ResolvePath(archive.Contains, path, difficulty);
        byte[] data = archive.ReadAllBytes(resolvedPath);
        string text = Encoding.ASCII.GetString(data);
        return Parse(text, Path.GetFileName(path));
    }

    /// <summary>
    /// Loads an AI class directly from a disk file, accounting for game difficulty (0=Easy, 1=Medium, 2=Hard).
    /// </summary>
    public static AiClass LoadFromFile(string filePath, int difficulty = 1)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        string resolvedPath = ResolvePath(File.Exists, filePath, difficulty);
        string text = File.ReadAllText(resolvedPath);
        return Parse(text, Path.GetFileName(filePath));
    }

    private static string StripComments(string line)
    {
        int hashIdx = line.IndexOf('#');
        int semiIdx = line.IndexOf(';');

        int commentIdx = -1;
        if (hashIdx >= 0 && semiIdx >= 0)
            commentIdx = Math.Min(hashIdx, semiIdx);
        else if (hashIdx >= 0)
            commentIdx = hashIdx;
        else if (semiIdx >= 0)
            commentIdx = semiIdx;

        return commentIdx >= 0 ? line[..commentIdx] : line;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var parts = line.Split(Delimiters, StringSplitOptions.RemoveEmptyEntries);
        tokens.AddRange(parts);
        return tokens;
    }

    private static bool IsHeaderLine(List<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.Contains('='))
                return true;
        }

        if (tokens.Count > 0)
        {
            string first = tokens[0].ToLowerInvariant();
            if (first is "alignment" or "rank" or "maxstep" or "sightdist" or "heardist" or "wakeupdist" or "fov" or "accuracy")
                return true;
        }

        return false;
    }

    private static void ParseHeader(List<string> tokens, AiClass aiClass)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            string key;
            string value;

            int eqIdx = token.IndexOf('=');
            if (eqIdx > 0)
            {
                key = token[..eqIdx].Trim();
                value = token[(eqIdx + 1)..].Trim();

                if (string.IsNullOrEmpty(value) && i + 1 < tokens.Count)
                {
                    i++;
                    value = tokens[i].Trim();
                }
            }
            else if (i + 1 < tokens.Count && tokens[i + 1].StartsWith('='))
            {
                key = token.Trim();
                string nextToken = tokens[i + 1];
                i++;
                if (nextToken.Length > 1)
                {
                    value = nextToken[1..].Trim();
                }
                else if (i + 1 < tokens.Count)
                {
                    i++;
                    value = tokens[i].Trim();
                }
                else
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            ApplyHeaderProperty(aiClass, key, value);
        }
    }

    private static void ApplyHeaderProperty(AiClass aiClass, string key, string value)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            return;

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatVal))
            return;

        switch (key.ToLowerInvariant())
        {
            case "alignment":
                aiClass.Alignment = floatVal;
                break;
            case "rank":
                aiClass.Rank = floatVal;
                break;
            case "maxstep":
                aiClass.MaxStep = floatVal;
                break;
            case "sightdist":
                aiClass.SightDistance = floatVal;
                break;
            case "heardist":
                aiClass.HeardDistance = floatVal;
                break;
            case "wakeupdist":
                aiClass.WakeupDistance = floatVal;
                break;
            case "accuracy":
                aiClass.Accuracy = floatVal;
                break;
            case "fov":
                aiClass.FovDegrees = floatVal;
                break;
        }
    }

    private static AiInstinct? ParseInstinct(List<string> tokens)
    {
        if (tokens.Count == 0)
            return null;

        string name = tokens[0];
        AiInstinct.TryGetDefaultParams(name, out int p1, out int p2, out int p3);

        int argCount = Math.Min(tokens.Count - 1, AiInstinct.MaxArguments);
        var instinct = new AiInstinct
        {
            Name = name,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            ArgCount = argCount
        };

        for (int i = 0; i < argCount; i++)
        {
            if (float.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                instinct.FloatArgs[i] = val;
                instinct.IntArgs[i] = (int)val;
            }
        }

        return instinct;
    }

    private static void ApplySpecialInstinctConfig(AiClass aiClass, AiInstinct instinct)
    {
        float[] f = instinct.FloatArgs;
        switch (instinct.Name.ToLowerInvariant())
        {
            case "saberfighting":
                aiClass.Saberfighting = new AiSaberfightingConfig
                {
                    SkipChance = f[0],
                    Att1Range = f[1],
                    Att1Damage = f[2],
                    Att1CooldownMs = f[3],
                    Att2Range = f[4],
                    Att2Damage = f[5],
                    Att2CooldownMs = f[6],
                    Att3Range = f[7],
                    Att3Damage = f[8],
                    Att3CooldownMs = f[9],
                    Att4Range = f[10],
                    Att4Damage = f[11],
                    Att4CooldownMs = f[12]
                };
                break;

            case "forcepowers":
                aiClass.ForcePowers = new AiForcePowerConfig
                {
                    IntervalMs = f[0],
                    Power1 = new AiForcePowerSlot { MinDist = f[1], MaxDist = f[2], Probability = f[7], CooldownMs = f[10] },
                    Power2 = new AiForcePowerSlot { MinDist = f[3], MaxDist = f[4], Probability = f[8], CooldownMs = f[11] },
                    Power3 = new AiForcePowerSlot { MinDist = f[5], MaxDist = f[6], Probability = f[9], CooldownMs = f[12] }
                };
                break;

            case "specialattack":
                aiClass.SpecialAttack = new AiSpecialAttackConfig
                {
                    IntervalMs = f[0],
                    Probability = f[1],
                    MinDist = f[2],
                    MaxDist = f[3],
                    DurationMs = f[4],
                    AnimMode = instinct.IntArgs[5],
                    Reserved = f[6],
                    Damage = f[7],
                    CooldownMs = f[8]
                };
                break;

            case "circlestrafe":
                aiClass.CircleStrafe = new AiCircleStrafeConfig
                {
                    IntervalMs = f[0],
                    AngleDeg = f[1],
                    Speed = f[2],
                    DurationMs = f[3],
                    RandomStrafe = instinct.IntArgs[4] != 0
                };
                break;

            case "roam":
                aiClass.RoamConfig = new AiRoamConfig
                {
                    IntervalMs = f[0],
                    MaxDist = f[1]
                };
                break;
        }
    }

    private static string ResolvePath(Func<string, bool> exists, string path, int difficulty)
    {
        string normalized = path.Replace('\\', '/');

        List<string> candidateBases = new();
        if (normalized.StartsWith("misc/ai/", StringComparison.OrdinalIgnoreCase))
        {
            candidateBases.Add(normalized);
        }
        else
        {
            candidateBases.Add("misc/ai/" + normalized);
            candidateBases.Add(normalized);
        }

        // Try difficulty-specific file first (if difficulty is 0 or 2, etc.)
        if (difficulty != 1)
        {
            string diffSuffix = difficulty.ToString(CultureInfo.InvariantCulture);
            foreach (var baseCandidate in candidateBases)
            {
                string diffCandidate = baseCandidate + diffSuffix;
                if (exists(diffCandidate))
                    return diffCandidate;
            }
        }

        // Try base file
        foreach (var baseCandidate in candidateBases)
        {
            if (exists(baseCandidate))
                return baseCandidate;
        }

        // Fallback to default.ai if not already default.ai
        if (!normalized.EndsWith("default.ai", StringComparison.OrdinalIgnoreCase))
        {
            if (exists("misc/ai/default.ai"))
                return "misc/ai/default.ai";
            if (exists("default.ai"))
                return "default.ai";
        }

        throw new FileNotFoundException($"AI file '{path}' could not be resolved (difficulty {difficulty}).");
    }
}
