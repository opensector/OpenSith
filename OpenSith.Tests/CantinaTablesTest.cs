using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class CantinaTablesTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public CantinaTablesTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FindAllPropsAndTemplates()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        // Group things by template name
        var templates = level.Things.GroupBy(t => t.TemplateName).Select(g => new { Template = g.Key, Count = g.Count() }).OrderByDescending(g => g.Count);
        foreach (var t in templates)
        {
            _output.WriteLine($"{t.Template,25} : {t.Count}");
        }
    }
}
