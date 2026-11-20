using System;
using System.IO;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class HierarchyDumpTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    public HierarchyDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DumpHierarchyDef()
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("3do/00crte.3do"));
        int hierIdx = text.IndexOf("SECTION: HIERARCHYDEF", StringComparison.OrdinalIgnoreCase);
        if (hierIdx >= 0)
        {
            _output.WriteLine("HIERARCHYDEF in 00crte.3do:");
            _output.WriteLine(text.Substring(hierIdx, Math.Min(1000, text.Length - hierIdx)));
        }

        // Also check saber or ky.3do
        var saberEntry = archive.Entries.FirstOrDefault(e => e.Path.EndsWith("saber.3do", StringComparison.OrdinalIgnoreCase));
        if (saberEntry.Length > 0)
        {
            string saberText = Encoding.ASCII.GetString(archive.ReadAllBytes(saberEntry.Path));
            int sHier = saberText.IndexOf("SECTION: HIERARCHYDEF", StringComparison.OrdinalIgnoreCase);
            if (sHier >= 0)
            {
                _output.WriteLine("\nHIERARCHYDEF in saber.3do:");
                _output.WriteLine(saberText.Substring(sHier, Math.Min(1000, saberText.Length - sHier)));
            }
        }
    }
}
