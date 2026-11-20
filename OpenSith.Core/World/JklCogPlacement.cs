using System.Collections.Generic;

namespace OpenSith.Core.World;

/// <summary>
/// Placed COG script instance in a Jedi Knight level (.JKL) file with positional symbol arguments.
/// </summary>
public sealed class JklCogPlacement
{
    public int Index { get; set; }
    public string ScriptName { get; set; } = string.Empty;
    public List<string> SymbolArgs { get; set; } = [];

    public override string ToString() =>
        $"Cog {Index}: {ScriptName} ({SymbolArgs.Count} args)";
}
