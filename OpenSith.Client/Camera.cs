using System;
using System.Numerics;

namespace OpenSith.Client;

/// <summary>
/// First-person / Freecam camera for navigating Sith engine worlds.
/// </summary>
public sealed class Camera
{
    public Vector3 Position { get; set; }
    public float Pitch { get; set; } // Pitch in degrees (-89 to 89)
    public float Yaw { get; set; }   // Yaw in degrees

    public float FovDegrees { get; set; } = 62.0f; // ~93 deg horizontal on 16:9 (matching classic 90 deg JK)
    public float NearPlane { get; set; } = 0.012f; // Matches SITHCAMERA_ZNEAR_FIRSTPERSON to prevent close wall clipping
    public float FarPlane { get; set; } = 1000.0f;

    public Vector3 Forward
    {
        get
        {
            float pitchRad = MathF.PI / 180.0f * Pitch;
            float yawRad = MathF.PI / 180.0f * Yaw;

            // In Jedi Knight / Sith Engine coordinates (+X Right, +Y Look/Forward, +Z Up):
            // Lvec = (-sin(yaw) * cos(pitch), cos(yaw) * cos(pitch), sin(pitch))
            return Vector3.Normalize(new Vector3(
                -MathF.Sin(yawRad) * MathF.Cos(pitchRad),
                MathF.Cos(yawRad) * MathF.Cos(pitchRad),
                MathF.Sin(pitchRad)
            ));
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitZ));
    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

    public Matrix4x4 GetViewMatrix()
    {
        return Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
    }

    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        float fovRad = MathF.PI / 180.0f * FovDegrees;
        return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, NearPlane, FarPlane);
    }
}
