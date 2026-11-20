using System;
using System.IO;
using System.Linq;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class InspectAssetsTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    public InspectAssetsTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DiscoverCmpAndMatFiles()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        _output.WriteLine($"Total files in Res2.gob: {archive.EntryCount}");

        var cmps = archive.Entries.Where(e => e.Path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        _output.WriteLine("Sample CMP files:");
        foreach (var cmp in cmps)
        {
            _output.WriteLine($"  {cmp.Path} ({cmp.Length} bytes)");
        }

        var mats = archive.Entries.Where(e => e.Path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        _output.WriteLine("Sample MAT files:");
        foreach (var mat in mats)
        {
            _output.WriteLine($"  {mat.Path} ({mat.Length} bytes)");
        }

        // Also check JKL and 3DO
        var jkls = archive.Entries.Where(e => e.Path.EndsWith(".jkl", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        _output.WriteLine("Sample JKL files:");
        foreach (var jkl in jkls)
        {
            _output.WriteLine($"  {jkl.Path} ({jkl.Length} bytes)");
        }

        var threeDos = archive.Entries.Where(e => e.Path.EndsWith(".3do", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        _output.WriteLine("Sample 3DO files:");
        foreach (var m in threeDos)
        {
            _output.WriteLine($"  {m.Path} ({m.Length} bytes)");
        }
    }
}
