using System;
using System.Numerics;
using OpenSith.Core.Animation;

namespace OpenSith.Core.Models;

/// <summary>
/// 3x4 Matrix structure equivalent to LucasArts RenderDroid rdMatrix34.
/// Used for authentic skeletal hierarchy transforms in the Sith Engine.
/// </summary>
public struct SithMatrix34
{
    public Vector3 Rvec;  // X axis (Right)
    public Vector3 Lvec;  // Y axis (Look / Forward)
    public Vector3 Uvec;  // Z axis (Up)
    public Vector3 Scale; // Translation (Position)

    public static SithMatrix34 Identity => new()
    {
        Rvec = new Vector3(1, 0, 0),
        Lvec = new Vector3(0, 1, 0),
        Uvec = new Vector3(0, 0, 1),
        Scale = Vector3.Zero
    };

    public static SithMatrix34 BuildTranslate(Vector3 trans)
    {
        var m = Identity;
        m.Scale = trans;
        return m;
    }

    /// <summary>
    /// Builds a 3x4 matrix from Euler angles (in degrees: pitch=X, yaw=Y, roll=Z) and position.
    /// Matches LucasArts rdMatrix_Build34 exactly.
    /// </summary>
    public static SithMatrix34 Build34(Vector3 pyr, Vector3 pos)
    {
        float px = pyr.X * (MathF.PI / 180f);
        float py = pyr.Y * (MathF.PI / 180f);
        float pz = pyr.Z * (MathF.PI / 180f);

        float x_sin = MathF.Sin(px), x_cos = MathF.Cos(px);
        float y_sin = MathF.Sin(py), y_cos = MathF.Cos(py);
        float z_sin = MathF.Sin(pz), z_cos = MathF.Cos(pz);

        var res = new SithMatrix34();
        res.Rvec.X = -(z_sin * y_sin) * x_sin + (z_cos * y_cos);
        res.Rvec.Y = ((z_sin * y_cos) * x_sin) + (z_cos * y_sin);
        res.Rvec.Z = -z_sin * x_cos;

        res.Lvec.X = -y_sin * x_cos;
        res.Lvec.Y = y_cos * x_cos;
        res.Lvec.Z = x_sin;

        res.Uvec.X = ((z_cos * y_sin) * x_sin) + (z_sin * y_cos);
        res.Uvec.Y = -x_sin * (y_cos * z_cos) + (y_sin * z_sin);
        res.Uvec.Z = z_cos * x_cos;

        res.Scale = pos;
        return res;
    }

    /// <summary>
    /// Multiplies matrix a by matrix b: transforms by b first, then by a.
    /// Matches LucasArts rdMatrix_Multiply34.
    /// </summary>
    public static SithMatrix34 Multiply(SithMatrix34 a, SithMatrix34 b)
    {
        var dest = new SithMatrix34();
        dest.Rvec.X = (a.Uvec.X * b.Rvec.Z) + (b.Rvec.Y * a.Lvec.X) + (b.Rvec.X * a.Rvec.X);
        dest.Rvec.Y = (a.Rvec.Y * b.Rvec.X) + (a.Lvec.Y * b.Rvec.Y) + (a.Uvec.Y * b.Rvec.Z);
        dest.Rvec.Z = (a.Rvec.Z * b.Rvec.X) + (a.Uvec.Z * b.Rvec.Z) + (a.Lvec.Z * b.Rvec.Y);

        dest.Lvec.X = (b.Lvec.X * a.Rvec.X) + (b.Lvec.Z * a.Uvec.X) + (b.Lvec.Y * a.Lvec.X);
        dest.Lvec.Y = (b.Lvec.Z * a.Uvec.Y) + (b.Lvec.Y * a.Lvec.Y) + (b.Lvec.X * a.Rvec.Y);
        dest.Lvec.Z = (b.Lvec.Z * a.Uvec.Z) + (a.Lvec.Z * b.Lvec.Y) + (b.Lvec.X * a.Rvec.Z);

        dest.Uvec.X = (b.Uvec.X * a.Rvec.X) + (b.Uvec.Y * a.Lvec.X) + (b.Uvec.Z * a.Uvec.X);
        dest.Uvec.Y = (b.Uvec.X * a.Rvec.Y) + (b.Uvec.Y * a.Lvec.Y) + (b.Uvec.Z * a.Uvec.Y);
        dest.Uvec.Z = (b.Uvec.Z * a.Uvec.Z) + (b.Uvec.X * a.Rvec.Z) + (b.Uvec.Y * a.Lvec.Z);

        dest.Scale.X = (b.Scale.X * a.Rvec.X) + (b.Scale.Z * a.Uvec.X) + (b.Scale.Y * a.Lvec.X) + a.Scale.X;
        dest.Scale.Y = (b.Scale.X * a.Rvec.Y) + (b.Scale.Y * a.Lvec.Y) + (b.Scale.Z * a.Uvec.Y) + a.Scale.Y;
        dest.Scale.Z = (b.Scale.Y * a.Lvec.Z) + (b.Scale.X * a.Rvec.Z) + (b.Scale.Z * a.Uvec.Z) + a.Scale.Z;
        return dest;
    }

    /// <summary>
    /// Applies b after a. Matches LucasArts rdMatrix_PostMultiply34.
    /// </summary>
    public static SithMatrix34 PostMultiply(SithMatrix34 a, SithMatrix34 b)
    {
        return Multiply(b, a);
    }

    public static SithMatrix34 PostTranslate(SithMatrix34 mat, Vector3 vec)
    {
        return PostMultiply(mat, BuildTranslate(vec));
    }

    public Vector3 TransformPoint(Vector3 src)
    {
        return new Vector3(
            Lvec.X * src.Y + Uvec.X * src.Z + Rvec.X * src.X + Scale.X,
            Lvec.Y * src.Y + Uvec.Y * src.Z + Rvec.Y * src.X + Scale.Y,
            Uvec.Z * src.Z + Lvec.Z * src.Y + Rvec.Z * src.X + Scale.Z
        );
    }

    public Vector3 TransformNormal(Vector3 src)
    {
        return new Vector3(
            Lvec.X * src.Y + Uvec.X * src.Z + Rvec.X * src.X,
            Lvec.Y * src.Y + Uvec.Y * src.Z + Rvec.Y * src.X,
            Uvec.Z * src.Z + Lvec.Z * src.Y + Rvec.Z * src.X
        );
    }

    /// <summary>
    /// Extracts Euler angles (pitch=X, yaw=Y, roll=Z in degrees) matching the inverse of <see cref="Build34"/>.
    /// Matches LucasArts <c>rdMatrix_ExtractAngles34</c>.
    /// </summary>
    public static Vector3 ExtractPyr(SithMatrix34 m)
    {
        const float radToDeg = 180f / MathF.PI;
        float sinP = Math.Clamp(m.Lvec.Z, -1f, 1f);
        float pitch = MathF.Asin(sinP) * radToDeg;

        if (MathF.Abs(sinP) < 0.99999f)
        {
            float yaw = MathF.Atan2(-m.Lvec.X, m.Lvec.Y) * radToDeg;
            float roll = MathF.Atan2(-m.Rvec.Z, m.Uvec.Z) * radToDeg;
            return new Vector3(pitch, yaw, roll);
        }

        float gimbalYaw = MathF.Atan2(m.Rvec.Y, m.Rvec.X) * radToDeg;
        return new Vector3(pitch, gimbalYaw, 0f);
    }

    /// <summary>
    /// Returns the pure-rotation inverse (transpose of the 3x3 basis) with zero translation.
    /// </summary>
    public SithMatrix34 TransposeRotation() => new()
    {
        Rvec = new Vector3(Rvec.X, Lvec.X, Uvec.X),
        Lvec = new Vector3(Rvec.Y, Lvec.Y, Uvec.Y),
        Uvec = new Vector3(Rvec.Z, Lvec.Z, Uvec.Z),
        Scale = Vector3.Zero
    };

    /// <summary>
    /// Converts to standard row-major Matrix4x4 compatible with System.Numerics (v' = v * M).
    /// </summary>
    public Matrix4x4 ToMatrix4x4()
    {
        return new Matrix4x4(
            Rvec.X, Rvec.Y, Rvec.Z, 0,
            Lvec.X, Lvec.Y, Lvec.Z, 0,
            Uvec.X, Uvec.Y, Uvec.Z, 0,
            Scale.X, Scale.Y, Scale.Z, 1
        );
    }
}

/// <summary>
/// Evaluates skeletal joint hierarchies for 3DO models using authentic LucasArts Sith Engine mechanics.
/// </summary>
public static class ThreeDoHierarchy
{
    public static Matrix4x4[] ComputeTransforms(ThreeDoModel model, KeyframeTrack? keyframe = null, float time = 0f)
    {
        int numNodes = model.Nodes.Count;
        if (numNodes == 0) return Array.Empty<Matrix4x4>();

        var jointMatrices = new SithMatrix34[numNodes];
        for (int i = 0; i < numNodes; i++)
        {
            Vector3 pos;
            Vector3 pyr;
            if (keyframe != null && keyframe.NodeEntries.TryGetValue(i, out var entries) && entries.Count > 0)
            {
                keyframe.SampleNode(i, time, out pos, out pyr);
            }
            else
            {
                pos = model.Nodes[i].Position;
                pyr = model.Nodes[i].PitchYawRoll;
            }
            jointMatrices[i] = SithMatrix34.Build34(pyr, pos);
        }

        var result = new SithMatrix34[numNodes];

        void AccumulateNode(int nodeIdx, SithMatrix34 parentPlacement)
        {
            var node = model.Nodes[nodeIdx];
            var mat = SithMatrix34.BuildTranslate(node.Pivot);
            mat = SithMatrix34.PostMultiply(mat, jointMatrices[nodeIdx]);
            if (node.ParentIndex >= 0 && node.ParentIndex < numNodes)
            {
                var parentNode = model.Nodes[node.ParentIndex];
                mat = SithMatrix34.PostTranslate(mat, -parentNode.Pivot);
            }
            var world = SithMatrix34.Multiply(parentPlacement, mat);
            result[nodeIdx] = world;

            for (int i = 0; i < numNodes; i++)
            {
                if (model.Nodes[i].ParentIndex == nodeIdx)
                {
                    AccumulateNode(i, world);
                }
            }
        }

        for (int i = 0; i < numNodes; i++)
        {
            if (model.Nodes[i].ParentIndex < 0 || model.Nodes[i].ParentIndex >= numNodes)
            {
                AccumulateNode(i, SithMatrix34.Identity);
            }
        }

        var matrices = new Matrix4x4[numNodes];
        for (int i = 0; i < numNodes; i++)
        {
            matrices[i] = result[i].ToMatrix4x4();
        }
        return matrices;
    }
}
