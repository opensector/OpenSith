using System.Collections.Generic;
using System.Numerics;

namespace OpenSith.Core.World;

/// <summary>
/// A convex 3D room/polyhedron in the Sith engine sector-portal graph.
/// </summary>
public sealed class JklSector
{
    public int SectorIndex { get; set; }
    public int Flags { get; set; }
    public float AmbientLight { get; set; }
    public float ExtraLight { get; set; }
    public int ColorMapIndex { get; set; }
    public Vector3 Tint { get; set; }
    public Vector3 BoundBoxMin { get; set; }
    public Vector3 BoundBoxMax { get; set; }
    public Vector3 Center { get; set; }
    public float Radius { get; set; }
    public int[] VertexIndices { get; set; } = [];
    public int FirstSurfaceIndex { get; set; }
    public int SurfaceCount { get; set; }
    public List<JklSurface> Surfaces { get; set; } = [];
}
