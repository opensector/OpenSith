using System.Collections.Generic;
using System.Numerics;

namespace OpenSith.Core.Models;

/// <summary>
/// A 3D polygonal mesh component of a 3DO model.
/// </summary>
public sealed class ThreeDoMesh
{
    public int MeshIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public float Radius { get; set; }
    public int GeometryMode { get; set; }
    public int LightingMode { get; set; }
    public int TextureMode { get; set; }
    public List<Vector3> Vertices { get; set; } = [];
    public List<float> VertexIntensities { get; set; } = [];
    public List<Vector2> TextureVertices { get; set; } = [];
    public List<ThreeDoFace> Faces { get; set; } = [];
}
