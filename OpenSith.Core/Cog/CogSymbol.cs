namespace OpenSith.Core.Cog;

/// <summary>
/// Represents a symbol definition in a COG script symbol table.
/// </summary>
public sealed class CogSymbol
{
    public string Name { get; set; } = string.Empty;
    public CogSymbolType Type { get; set; }
    public bool IsLocal { get; set; }
    public int LinkId { get; set; } = -1;
    public int Mask { get; set; } = 0x401;
    public string? Desc { get; set; }
    public string? DefaultValue { get; set; }

    public override string ToString() =>
        $"{Type} {Name}{(DefaultValue != null ? $"={DefaultValue}" : "")}" +
        $"{(IsLocal ? " local" : "")}{(LinkId >= 0 ? $" linkid={LinkId}" : "")}" +
        $"{(Mask != 0x401 ? $" mask=0x{Mask:x}" : "")}{(Desc != null ? $" desc=\"{Desc}\"" : "")}";
}
