using System;

namespace OpenSith.Core.Vfs;

/// <summary>
/// Represents a single file entry inside a Sith engine GOB / GOO archive.
/// </summary>
public readonly record struct GobEntry(
    string Path,
    int Offset,
    int Length
)
{
    public override string ToString() => $"{Path} (Offset: 0x{Offset:X8}, Size: {Length} bytes)";
}
