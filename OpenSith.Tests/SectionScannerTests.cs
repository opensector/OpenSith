using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class SectionScannerTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public SectionScannerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Scan3DoSections()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("3do/00crte.3do"));
        var sections = Regex.Matches(text, @"SECTION:\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);

        _output.WriteLine("Sections in 3do/00crte.3do:");
        foreach (Match m in sections)
        {
            _output.WriteLine("  " + m.Value);
        }

        // Print more of GEOMETRYDEF
        int geomIdx = text.IndexOf("SECTION: GEOMETRYDEF", StringComparison.OrdinalIgnoreCase);
        if (geomIdx >= 0)
        {
            _output.WriteLine("\nGEOMETRYDEF excerpt (up to 1200 chars):");
            _output.WriteLine(text.Substring(geomIdx, Math.Min(1200, text.Length - geomIdx)));
        }
    }

    [Fact]
    public void ScanJklSections()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var sections = Regex.Matches(text, @"SECTION:\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);

        _output.WriteLine("Sections in jkl/01narshadda.jkl:");
        foreach (Match m in sections)
        {
            _output.WriteLine("  " + m.Value);
        }
    }
}
