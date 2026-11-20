using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class EpisodeJklCheckTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public EpisodeJklCheckTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckEpisodeJkl()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        var entries = archive.Entries.Where(e => e.Path.Contains("episode", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var e in entries)
        {
            _output.WriteLine($"Found: {e.Path}");
            string content = Encoding.ASCII.GetString(archive.ReadAllBytes(e.Path));
            _output.WriteLine(content.Substring(0, Math.Min(500, content.Length)));
        }
    }
}
