using System.Collections.Generic;

namespace OpenSith.Core.World;

/// <summary>
/// Definition of a Sith Engine actor, structure, or item template.
/// </summary>
public sealed class JklTemplate
{
    public string Name { get; set; } = string.Empty;
    public string BasedOn { get; set; } = string.Empty;
    public string Model3D { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{Name} : {BasedOn} (model={Model3D})";
}
