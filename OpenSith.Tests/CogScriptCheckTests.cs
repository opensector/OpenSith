using System;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CogScriptCheckTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public CogScriptCheckTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckCogs()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        int idx = text.IndexOf("Section: cogs", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            _output.WriteLine(text.Substring(idx, Math.Min(1000, text.Length - idx)));
        }
    }
}
