using System.Collections.Generic;
using System.Numerics;

namespace OpenSith.Core.Models;

/// <summary>
/// Representation of a LucasArts Sith Engine 3DO 3D Model.
/// Used for characters, weapons, props, and item pickups.
/// </summary>
public sealed class ThreeDoModel
{
    public string Version { get; set; } = "3DO 2.1";
    public List<string> Materials { get; set; } = [];
    public float Radius { get; set; }
    public Vector3 InsertOffset { get; set; }
    public List<ThreeDoMesh> Meshes { get; set; } = [];
    public List<ThreeDoHierarchyNode> Nodes { get; set; } = [];
}
