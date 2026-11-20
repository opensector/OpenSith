using System;

namespace OpenSith.Core.Materials;

/// <summary>
/// A 32-bit RGBA color representation (1 byte per channel).
/// </summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba32 Transparent = new(0, 0, 0, 0);
    public static readonly Rgba32 Black = new(0, 0, 0, 255);
    public static readonly Rgba32 White = new(255, 255, 255, 255);

    public uint ToRgbaUInt32() => (uint)(R | (G << 8) | (B << 16) | (A << 24));
    public uint ToBgraUInt32() => (uint)(B | (G << 8) | (R << 16) | (A << 24));

    public override string ToString() => $"RGBA({R}, {G}, {B}, {A})";
}
