using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class TemplateModelsCheckTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public TemplateModelsCheckTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckTemplatesInJkl()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        int idx = text.IndexOf("Section: TEMPLATES", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            string tplSection = text.Substring(idx, Math.Min(15000, text.Length - idx));
            using var reader = new StringReader(tplSection);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("00table", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("reeyees", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("tusken", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("nbartender", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("bottle", StringComparison.OrdinalIgnoreCase))
                {
                    _output.WriteLine(line);
                }
            }
        }
    }
}
