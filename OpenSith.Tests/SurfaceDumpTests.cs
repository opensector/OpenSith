using System;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class SurfaceDumpTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public SurfaceDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FindSurfacesDefinition()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        int surfIdx = text.IndexOf("World surfaces", StringComparison.OrdinalIgnoreCase);
        if (surfIdx < 0)
            surfIdx = text.IndexOf("SURFACES", text.IndexOf("Section: SECTORS", StringComparison.OrdinalIgnoreCase) + 500, StringComparison.OrdinalIgnoreCase);

        if (surfIdx >= 0)
        {
            _output.WriteLine("Surfaces section excerpt:");
            _output.WriteLine(text.Substring(surfIdx, Math.Min(1200, text.Length - surfIdx)));
        }
    }
}
