using System;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CogInstancesCheckTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public CogInstancesCheckTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckCogInstances()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        int idx = text.IndexOf("Section: cogs", StringComparison.OrdinalIgnoreCase);
        // Find next "Section: cogs" (not cogscripts)
        int realCogsIdx = text.IndexOf("Section: cogs", idx + 10, StringComparison.OrdinalIgnoreCase);
        if (realCogsIdx >= 0)
        {
            _output.WriteLine(text.Substring(realCogsIdx, Math.Min(1200, text.Length - realCogsIdx)));
        }
    }
}
