using System;
using System.IO;
using System.Linq;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class MaterialLoadDiagnosticTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public MaterialLoadDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestHowManyMaterialsLoadSuccessfully()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));
        vfs.Mount(GobArchive.FromFile(jk1Path));

        string jklText = vfs.ReadAllText("jkl/01narshadda.jkl");
        var level = JklReader.Parse(jklText, "01narshadda");

        byte[] cmpBytes = vfs.ReadAllBytes("misc/cmp/01narsh.cmp");
        var palette = CmpPalette.Parse(cmpBytes);

        int totalMats = level.Materials.Count;
        int foundCount = 0;
        int parsedCount = 0;
        int missingCount = 0;

        _output.WriteLine($"Total materials listed in 01narshadda: {totalMats}");

        foreach (var matName in level.Materials.Skip(400).Take(30))
        {
            string[] searchPaths = [
                $"mat/{matName}",
                $"3do/mat/{matName}",
                $"misc/mat/{matName}",
                matName
            ];

            byte[]? matBytes = null;
            string foundPath = "";
            foreach (var p in searchPaths)
            {
                if (vfs.FileExists(p))
                {
                    matBytes = vfs.ReadAllBytes(p);
                    foundPath = p;
                    break;
                }
            }

            if (matBytes != null)
            {
                foundCount++;
                try
                {
                    var mat = MatMaterial.Parse(matBytes);
                    parsedCount++;
                    _output.WriteLine($"[OK] {matName} -> {foundPath} ({mat.Width}x{mat.Height}, {mat.BitDepth}-bit, {mat.RecordCount} recs)");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[PARSE ERR] {matName} -> {foundPath}: {ex.Message}");
                }
            }
            else
            {
                missingCount++;
                _output.WriteLine($"[NOT FOUND] {matName}");
            }
        }
    }
}
