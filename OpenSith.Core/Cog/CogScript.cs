using System;
using System.Collections.Generic;

namespace OpenSith.Core.Cog;

/// <summary>
/// Represents a parsed Jedi Knight COG script with symbols, labels, and AST statements.
/// </summary>
public sealed class CogScript
{
    public string Name { get; set; } = string.Empty;
    public int Flags { get; set; }
    public List<CogSymbol> Symbols { get; set; } = new();
    public Dictionary<string, int> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CogStmt> Statements { get; set; } = new();
}
