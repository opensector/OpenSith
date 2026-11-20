using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSith.Core.Models;

namespace OpenSith.Core.Animation;

/// <summary>
/// A single keyframe entry for one hierarchical skeletal joint/node.
/// </summary>
public sealed class KeyframeEntry
{
    public int Frame { get; set; }
    public uint Flags { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 PitchYawRoll { get; set; } // Pitch (X), Yaw (Y), Roll (Z) in degrees
    public Vector3 DPosition { get; set; }
    public Vector3 DPitchYawRoll { get; set; }
}

/// <summary>
/// Keyframe animation track for an entire multi-joint 3DO mesh.
/// </summary>
public sealed class KeyframeTrack
{
    public string Name { get; set; } = string.Empty;
    public float Fps { get; set; } = 15.0f;
    public int TotalFrames { get; set; }
    public int JointCount { get; set; }

    public Dictionary<int, List<KeyframeEntry>> NodeEntries { get; } = new();
    public Dictionary<string, int> NodeNameToIndex { get; } = new(StringComparer.OrdinalIgnoreCase);

    public float Duration => TotalFrames > 0 && Fps > 0 ? TotalFrames / Fps : 0f;

    /// <summary>
    /// Samples local position and Euler angles (degrees) for a joint at time t.
    /// Follows LucasArts Sith Engine rdPuppet_BuildJointMatrices mechanics.
    /// </summary>
    public void SampleNode(int nodeIndex, float time, out Vector3 pos, out Vector3 pyr)
    {
        if (!NodeEntries.TryGetValue(nodeIndex, out var entries) || entries.Count == 0)
        {
            pos = Vector3.Zero;
            pyr = Vector3.Zero;
            return;
        }

        if (entries.Count == 1 || TotalFrames <= 0)
        {
            pos = entries[0].Position;
            pyr = entries[0].PitchYawRoll;
            return;
        }

        // Loop time
        float dur = Duration;
        float loopedTime = dur > 0 ? (time % dur) : 0;
        if (loopedTime < 0) loopedTime += dur;
        float currentFrame = loopedTime * Fps;

        // Find surrounding keyframe entries
        KeyframeEntry prev = entries[0];
        KeyframeEntry next = entries[^1];

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Frame <= currentFrame)
            {
                prev = entries[i];
            }
            if (entries[i].Frame >= currentFrame)
            {
                next = entries[i];
                break;
            }
        }

        if (prev.Frame == next.Frame || prev == next)
        {
            pos = prev.Position;
            pyr = prev.PitchYawRoll;
            return;
        }

        float dt = currentFrame - prev.Frame;

        // Position: check bit 0 (velocity)
        if ((prev.Flags & 1) != 0)
        {
            pos = prev.Position + (prev.DPosition * dt);
        }
        else
        {
            float alpha = dt / (float)(next.Frame - prev.Frame);
            alpha = Math.Clamp(alpha, 0f, 1f);
            pos = Vector3.Lerp(prev.Position, next.Position, alpha);
        }

        // Rotation: check bit 1 (angular velocity)
        if ((prev.Flags & 2) != 0)
        {
            pyr = prev.PitchYawRoll + (prev.DPitchYawRoll * dt);
        }
        else
        {
            float alpha = dt / (float)(next.Frame - prev.Frame);
            alpha = Math.Clamp(alpha, 0f, 1f);
            pyr = new Vector3(
                LerpAngleDegrees(prev.PitchYawRoll.X, next.PitchYawRoll.X, alpha),
                LerpAngleDegrees(prev.PitchYawRoll.Y, next.PitchYawRoll.Y, alpha),
                LerpAngleDegrees(prev.PitchYawRoll.Z, next.PitchYawRoll.Z, alpha)
            );
        }
    }

    /// <summary>
    /// Computes the interpolated local transform matrix for a given node at time t.
    /// </summary>
    public Matrix4x4 SampleNodeTransform(int nodeIndex, float time)
    {
        SampleNode(nodeIndex, time, out var pos, out var pyr);
        return SithMatrix34.Build34(pyr, pos).ToMatrix4x4();
    }

    public static float LerpAngleDegrees(float a, float b, float t)
    {
        float diff = (b - a) % 360f;
        if (diff > 180f) diff -= 360f;
        if (diff < -180f) diff += 360f;
        return a + (diff * t);
    }
}
