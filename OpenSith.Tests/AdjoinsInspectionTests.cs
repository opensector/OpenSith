using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class AdjoinsInspectionTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public AdjoinsInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public record JklAdjoin(int Index, int Flags, int Mirror, float Dist);

    [Fact]
    public void CheckAdjoinsSection()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        // Parse adjoins
        var adjoins = new List<JklAdjoin>();
        using var reader = new StringReader(text);
        string? line;
        bool inAdjoins = false;
        int count = 0;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith("World adjoins", StringComparison.OrdinalIgnoreCase))
            {
                inAdjoins = true;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                count = int.Parse(parts[2]);
                continue;
            }

            if (inAdjoins)
            {
                if (line.StartsWith("#") || string.IsNullOrEmpty(line)) continue;
                if (adjoins.Count >= count || line.StartsWith("World", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Section", StringComparison.OrdinalIgnoreCase))
                    break;

                string[] tokens = line.Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 4)
                {
                    int idx = int.Parse(tokens[0]);
                    int flags = Convert.ToInt32(tokens[1], 16);
                    int mirror = int.Parse(tokens[2]);
                    float dist = float.Parse(tokens[3]);
                    adjoins.Add(new JklAdjoin(idx, flags, mirror, dist));
                }
            }
        }

        _output.WriteLine($"Parsed {adjoins.Count} adjoins.");

        // Build mapping: AdjoinIndex -> Surface -> Sector
        var adjoinToSurface = new Dictionary<int, (JklSurface Surface, JklSector Sector)>();
        foreach (var sec in level.Sectors)
        {
            foreach (var surf in sec.Surfaces)
            {
                if (surf.AdjoinIndex >= 0)
                {
                    adjoinToSurface[surf.AdjoinIndex] = (surf, sec);
                }
            }
        }

        _output.WriteLine($"Mapped {adjoinToSurface.Count} adjoin surfaces.");

        // Check Sector 0's portals!
        var sec0 = level.Sectors[0];
        _output.WriteLine($"Sector 0 has {sec0.Surfaces.Count} surfaces:");
        foreach (var surf in sec0.Surfaces)
        {
            if (surf.AdjoinIndex >= 0)
            {
                var adj = adjoins[surf.AdjoinIndex];
                if (adjoinToSurface.TryGetValue(adj.Mirror, out var target))
                {
                    _output.WriteLine($"  Portal Surf {surf.SurfaceIndex} (Adjoin {surf.AdjoinIndex}) -> Mirror {adj.Mirror} in Sector {target.Sector.SectorIndex} (Surf {target.Surface.SurfaceIndex})");
                }
                else
                {
                    _output.WriteLine($"  Portal Surf {surf.SurfaceIndex} (Adjoin {surf.AdjoinIndex}) -> Mirror {adj.Mirror} NOT FOUND");
                }
            }
        }
    }
}
