using System;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class JklDumpTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public JklDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DumpJklGeoresourceAndSectors()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        // Materials excerpt
        int matIdx = text.IndexOf("SECTION: MATERIALS", StringComparison.OrdinalIgnoreCase);
        if (matIdx >= 0)
        {
            _output.WriteLine("MATERIALS excerpt:");
            _output.WriteLine(text.Substring(matIdx, Math.Min(600, text.Length - matIdx)));
        }

        // GEORESOURCE excerpt
        int geoIdx = text.IndexOf("SECTION: GEORESOURCE", StringComparison.OrdinalIgnoreCase);
        if (geoIdx >= 0)
        {
            _output.WriteLine("\nGEORESOURCE excerpt:");
            _output.WriteLine(text.Substring(geoIdx, Math.Min(800, text.Length - geoIdx)));
        }

        // SECTORS excerpt
        int secIdx = text.IndexOf("Section: SECTORS", StringComparison.OrdinalIgnoreCase);
        if (secIdx >= 0)
        {
            _output.WriteLine("\nSECTORS excerpt:");
            _output.WriteLine(text.Substring(secIdx, Math.Min(1200, text.Length - secIdx)));
        }
    }
}
