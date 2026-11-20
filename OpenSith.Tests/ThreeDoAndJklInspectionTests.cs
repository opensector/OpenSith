using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class ThreeDoAndJklInspectionTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public ThreeDoAndJklInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Inspect3DoFile()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        byte[] bytes = archive.ReadAllBytes("3do/00crte.3do");
        _output.WriteLine($"3do/00crte.3do length: {bytes.Length} bytes");

        string text = Encoding.ASCII.GetString(bytes, 0, Math.Min(500, bytes.Length));
        _output.WriteLine("First 500 characters of 00crte.3do:");
        _output.WriteLine(text);
    }

    [Fact]
    public void InspectJk1GobLevels()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        _output.WriteLine($"Total files in JK1.GOB: {archive.EntryCount}");

        var jkls = archive.Entries.Where(e => e.Path.EndsWith(".jkl", StringComparison.OrdinalIgnoreCase)).ToList();
        _output.WriteLine($"Found {jkls.Count} JKL level files in JK1.GOB:");
        foreach (var jkl in jkls.Take(10))
        {
            _output.WriteLine($"  {jkl.Path} ({jkl.Length} bytes)");
        }

        if (jkls.Count > 0)
        {
            var firstJkl = jkls.First();
            byte[] bytes = archive.ReadAllBytes(firstJkl.Path);
            string text = Encoding.ASCII.GetString(bytes, 0, Math.Min(500, bytes.Length));
            _output.WriteLine($"\nFirst 500 characters of {firstJkl.Path}:");
            _output.WriteLine(text);
        }
    }
}
