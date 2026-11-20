using System.Numerics;

namespace OpenSith.Core.Models;

/// <summary>
/// A hierarchical bone node in a 3DO model, connecting meshes with parent/child relationships and pivot transforms.
/// </summary>
public sealed class ThreeDoHierarchyNode
{
    public int NodeIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Flags { get; set; }
    public int NodeType { get; set; }
    public int MeshIndex { get; set; }
    public int ParentIndex { get; set; }
    public int FirstChildIndex { get; set; }
    public int NextSiblingIndex { get; set; }
    public Vector3 Pivot { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 PitchYawRoll { get; set; }
}
