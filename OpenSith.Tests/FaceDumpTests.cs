using System;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class FaceDumpTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    public FaceDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DumpFaces()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("3do/00crte.3do"));
        int facesIdx = text.IndexOf("FACES", StringComparison.OrdinalIgnoreCase);
        if (facesIdx >= 0)
        {
            _output.WriteLine("FACES in 00crte.3do:");
            _output.WriteLine(text.Substring(facesIdx, Math.Min(1200, text.Length - facesIdx)));
        }
    }
}
