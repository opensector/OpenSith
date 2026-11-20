using System;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class AfterSurfacesDumpTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public AfterSurfacesDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectAfterSurfaces()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        int surfIdx = text.IndexOf("World surfaces", StringComparison.OrdinalIgnoreCase);
        // Find next section after World surfaces
        int nextSec = text.IndexOf("Section:", surfIdx, StringComparison.OrdinalIgnoreCase);
        if (nextSec >= 0)
        {
            _output.WriteLine("Next section after surfaces:");
            _output.WriteLine(text.Substring(nextSec, Math.Min(600, text.Length - nextSec)));
        }

        // Also check if there is SURFACE NORMALS or ADJOINS before next section
        int adjoinIdx = text.IndexOf("World adjoins", surfIdx, StringComparison.OrdinalIgnoreCase);
        if (adjoinIdx >= 0)
        {
            _output.WriteLine("\nWorld adjoins found:");
            _output.WriteLine(text.Substring(adjoinIdx, Math.Min(600, text.Length - adjoinIdx)));
        }
    }
}
