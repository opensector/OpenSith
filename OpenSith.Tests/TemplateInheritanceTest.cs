using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class TemplateInheritanceTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public TemplateInheritanceTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public class TemplateDef
    {
        public string Name { get; set; } = "";
        public string BasedOn { get; set; } = "";
        public string Model3D { get; set; } = "";
    }

    [Fact]
    public void TestParseTemplates()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));

        var templates = new Dictionary<string, TemplateDef>(StringComparer.OrdinalIgnoreCase);

        int idx = text.IndexOf("Section: TEMPLATES", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            using var reader = new StringReader(text.Substring(idx));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("#") || string.IsNullOrEmpty(line) || line.StartsWith("Section", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("World templates", StringComparison.OrdinalIgnoreCase)) continue;

                string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2)
                {
                    string name = tokens[0];
                    string basedOn = tokens[1];
                    string model = "";
                    for (int i = 2; i < tokens.Length; i++)
                    {
                        if (tokens[i].StartsWith("model3d=", StringComparison.OrdinalIgnoreCase))
                        {
                            model = tokens[i]["model3d=".Length..];
                            break;
                        }
                    }
                    templates[name] = new TemplateDef { Name = name, BasedOn = basedOn, Model3D = model };
                }
            }
        }

        // Resolve inheritance
        foreach (var t in templates.Values)
        {
            if (string.IsNullOrEmpty(t.Model3D) && !string.IsNullOrEmpty(t.BasedOn))
            {
                string curr = t.BasedOn;
                while (templates.TryGetValue(curr, out var parent))
                {
                    if (!string.IsNullOrEmpty(parent.Model3D))
                    {
                        t.Model3D = parent.Model3D;
                        break;
                    }
                    curr = parent.BasedOn;
                }
            }
        }

        _output.WriteLine($"Parsed {templates.Count} templates.");
        _output.WriteLine($"00table: {templates["00table"].Model3D}");
        _output.WriteLine($"tuskensoused: {templates["tuskensoused"].Model3D}");
        _output.WriteLine($"reeyeesbhgun: {templates["reeyeesbhgun"].Model3D}");
        _output.WriteLine($"bottle04: {templates["bottle04"].Model3D}");
        _output.WriteLine($"nbartender: {templates["nbartender"].Model3D}");

        Assert.Equal("00tabl.3do", templates["00table"].Model3D);
        Assert.Equal("thout.3do", templates["tuskensoused"].Model3D);
        Assert.Equal("rhstr.3do", templates["reeyeesbhgun"].Model3D);
    }
}
