using System.Collections.Generic;
using System.Numerics;

namespace OpenSith.Core.World;

/// <summary>
/// Representation of a parsed Jedi Knight level (.JKL) file.
/// </summary>
public sealed class JklLevel
{
    public string Name { get; set; } = string.Empty;
    public List<string> Sounds { get; set; } = [];
    public List<string> Materials { get; set; } = [];
    public List<string> ColorMaps { get; set; } = [];
    public List<Vector3> Vertices { get; set; } = [];
    public List<Vector2> TextureVertices { get; set; } = [];
    public List<JklSurface> Surfaces { get; set; } = [];
    public List<JklSector> Sectors { get; set; } = [];
    public List<JklAdjoin> Adjoins { get; set; } = [];
    public List<string> CogScripts { get; set; } = [];
    public List<JklCogPlacement> Cogs { get; set; } = [];
    public List<JklThing> Things { get; set; } = [];
    public Dictionary<string, JklTemplate> Templates { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the 3DO model filename for a given template name following the inheritance chain.
    /// </summary>
    public string ResolveModel3D(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return string.Empty;

        string current = templateName;
        while (Templates.TryGetValue(current, out var tpl))
        {
            if (!string.IsNullOrEmpty(tpl.Model3D))
                return tpl.Model3D;

            if (string.IsNullOrEmpty(tpl.BasedOn) || tpl.BasedOn.Equals("none", System.StringComparison.OrdinalIgnoreCase))
                break;

            current = tpl.BasedOn;
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets the primary player spawn point (walkplayer or kyle) in this level.
    /// Returns Thing 0 (canonical single-player campaign start in the Cantina booth) or first valid walkplayer.
    /// </summary>
    public JklThing? GetPlayerSpawn()
    {
        // 1. Thing 0 is the canonical single-player spawn (the Cantina booth)
        if (Things.Count > 0 && Things[0].IsPlayerSpawn)
            return Things[0];

        // 2. Otherwise find first player spawn
        return Things.Find(t => t.IsPlayerSpawn) ?? (Things.Count > 0 ? Things[0] : null);
    }
}
