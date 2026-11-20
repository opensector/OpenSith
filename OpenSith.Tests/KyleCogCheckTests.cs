using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class KyleCogCheckTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    public KyleCogCheckTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ReadKyleCog()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        var entry = archive.Entries.FirstOrDefault(e => e.Path.EndsWith("kyle.cog", StringComparison.OrdinalIgnoreCase));
        if (entry.Length > 0)
        {
            string content = Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
            _output.WriteLine("kyle.cog:");
            _output.WriteLine(content.Substring(0, Math.Min(1200, content.Length)));
        }
    }
}
