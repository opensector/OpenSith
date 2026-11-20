using System;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class ThingsDumpTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public ThingsDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectThingsSection()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        int thingsIdx = text.IndexOf("Section: Things", StringComparison.OrdinalIgnoreCase);
        if (thingsIdx >= 0)
        {
            _output.WriteLine("Things section excerpt:");
            _output.WriteLine(text.Substring(thingsIdx, Math.Min(1200, text.Length - thingsIdx)));
        }

        int tmplIdx = text.IndexOf("Section: TEMPLATES", StringComparison.OrdinalIgnoreCase);
        if (tmplIdx >= 0)
        {
            _output.WriteLine("\nTEMPLATES section excerpt:");
            _output.WriteLine(text.Substring(tmplIdx, Math.Min(600, text.Length - tmplIdx)));
        }
    }
}
