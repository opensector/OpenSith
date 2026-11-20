namespace OpenSith.Core.World;

/// <summary>
/// Adjoin portal entry connecting two sectors in the Sith Engine.
/// </summary>
public sealed class JklAdjoin
{
    public int AdjoinIndex { get; set; }
    public int Flags { get; set; }
    public int Mirror { get; set; }
    public float Dist { get; set; }

    public override string ToString() =>
        $"Adjoin {AdjoinIndex}: Flags=0x{Flags:X}, Mirror={Mirror}, Dist={Dist}";
}
