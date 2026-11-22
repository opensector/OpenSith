using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace OpenSith.Core.Animation;

/// <summary>
/// Parser for LucasArts Sith Engine .KEY keyframe animation files.
/// </summary>
public static class KeyframeReader
{
    public static KeyframeTrack Parse(string text, string trackName = "")
    {
        var track = new KeyframeTrack
        {
            Name = trackName
        };

        using var reader = new StringReader(text);
        string? line;
        string currentSection = string.Empty;

        int currentNodeIndex = -1;
        int expectedEntries = 0;
        int entriesRead = 0;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            if (line.StartsWith("SECTION:", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = line["SECTION:".Length..].Trim().ToUpperInvariant();
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            if (currentSection == "HEADER")
            {
                if (tokens[0].Equals("FRAMES", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
                {
                    track.TotalFrames = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                }
                else if (tokens[0].Equals("FPS", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
                {
                    track.Fps = float.Parse(tokens[1], CultureInfo.InvariantCulture);
                }
                else if (tokens[0].Equals("JOINTS", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
                {
                    track.JointCount = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                }
                else if (tokens[0].Equals("TYPE", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
                {
                    string rawType = tokens[1];
                    if (rawType.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(rawType.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hexMask))
                            track.NodeMask = hexMask;
                    }
                    else if (int.TryParse(rawType, NumberStyles.Integer, CultureInfo.InvariantCulture, out int decMask))
                    {
                        track.NodeMask = decMask;
                    }
                }
            }
            else if (currentSection == "KEYFRAME NODES")
            {
                if (tokens[0].Equals("NODES", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else if (tokens[0].Equals("NODE", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
                {
                    currentNodeIndex = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                    if (!track.NodeEntries.ContainsKey(currentNodeIndex))
                    {
                        track.NodeEntries[currentNodeIndex] = new List<KeyframeEntry>();
                    }
                    expectedEntries = 0;
                    entriesRead = 0;
                }
                else if (tokens[0].Equals("MESH", StringComparison.OrdinalIgnoreCase) && tokens.Length > 2 && tokens[1].Equals("NAME", StringComparison.OrdinalIgnoreCase))
                {
                    string meshName = tokens[2].Trim();
                    if (currentNodeIndex >= 0)
                    {
                        track.NodeNameToIndex[meshName] = currentNodeIndex;
                    }
                }
                else if (tokens[0].Equals("ENTRIES", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
                {
                    expectedEntries = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                    entriesRead = 0;
                }
                else if (tokens[0].EndsWith(':') && currentNodeIndex >= 0)
                {
                    // Line format: num: frame: flags: x: y: z: p: y: r:
                    // Example tokens: "0:", "0", "0x0002", "-0.00261900", "-0.00375600", "-0.04429700", "327.49975586", "0.00000000", "0.00000000"
                    if (tokens.Length >= 9)
                    {
                        var entry = new KeyframeEntry
                        {
                            Frame = int.Parse(tokens[1], CultureInfo.InvariantCulture),
                            Flags = tokens[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ?
                                Convert.ToUInt32(tokens[2], 16) :
                                uint.Parse(tokens[2], CultureInfo.InvariantCulture),
                            Position = new Vector3(
                                float.Parse(tokens[3], CultureInfo.InvariantCulture),
                                float.Parse(tokens[4], CultureInfo.InvariantCulture),
                                float.Parse(tokens[5], CultureInfo.InvariantCulture)
                            ),
                            PitchYawRoll = new Vector3(
                                float.Parse(tokens[6], CultureInfo.InvariantCulture),
                                float.Parse(tokens[7], CultureInfo.InvariantCulture),
                                float.Parse(tokens[8], CultureInfo.InvariantCulture)
                            )
                        };

                        // Optional derivative line follows: dx: dy: dz: dp: dy: dr:
                        string? dLine = reader.ReadLine();
                        if (dLine != null)
                        {
                            var dTokens = dLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            if (dTokens.Length >= 6)
                            {
                                entry.DPosition = new Vector3(
                                    float.Parse(dTokens[0], CultureInfo.InvariantCulture),
                                    float.Parse(dTokens[1], CultureInfo.InvariantCulture),
                                    float.Parse(dTokens[2], CultureInfo.InvariantCulture)
                                );
                                entry.DPitchYawRoll = new Vector3(
                                    float.Parse(dTokens[3], CultureInfo.InvariantCulture),
                                    float.Parse(dTokens[4], CultureInfo.InvariantCulture),
                                    float.Parse(dTokens[5], CultureInfo.InvariantCulture)
                                );
                            }
                        }

                        track.NodeEntries[currentNodeIndex].Add(entry);
                        entriesRead++;
                    }
                }
            }
        }

        return track;
    }
}
