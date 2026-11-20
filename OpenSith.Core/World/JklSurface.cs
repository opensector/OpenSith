using System.Numerics;

namespace OpenSith.Core.World;

/// <summary>
/// A planar surface (wall, floor, ceiling, or portal adjoin) within a Sith level.
/// </summary>
public sealed class JklSurface
{
    public int SurfaceIndex { get; set; }
    public int MaterialIndex { get; set; } = -1;
    public int SurfaceFlags { get; set; }
    public int FaceFlags { get; set; }
    public int GeometryMode { get; set; }
    public int LightingMode { get; set; }
    public int TextureMode { get; set; }
    public int AdjoinIndex { get; set; } = -1;
    public float ExtraLight { get; set; }
    public int[] VertexIndices { get; set; } = [];
    public int[] TextureIndices { get; set; } = [];
    public float[] VertexIntensities { get; set; } = [];
    public Vector3 Normal { get; set; }

    public bool IsPortal => AdjoinIndex >= 0;

    /// <summary>
    /// The linked surface on the other side of this portal.
    /// </summary>
    public JklSurface? MirrorSurface { get; set; }

    /// <summary>
    /// The linked adjoining sector on the other side of this portal.
    /// </summary>
    public JklSector? MirrorSector { get; set; }
}
