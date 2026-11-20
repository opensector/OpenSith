using System.Numerics;

namespace OpenSith.Core.Models;

/// <summary>
/// A polygonal face within a Sith 3DO mesh.
/// </summary>
public sealed class ThreeDoFace
{
    public int MaterialIndex { get; set; }
    public int FaceType { get; set; }
    public int GeometryMode { get; set; }
    public int LightingMode { get; set; }
    public int TextureMode { get; set; }
    public float ExtraLight { get; set; }
    public Vector3 Normal { get; set; }
    public int[] VertexIndices { get; set; } = [];
    public int[] TextureIndices { get; set; } = [];
}
