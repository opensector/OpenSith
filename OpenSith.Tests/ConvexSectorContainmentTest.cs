using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class ConvexSectorContainmentTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public ConvexSectorContainmentTest(ITestOutputHelper output)
    {
        _output = output;
    }

    public static bool IsPointInsideSector(Vector3 pos, JklSector sector, JklLevel level, float epsilon = 0.02f)
    {
        // A point is inside a convex sector if it is on the positive side of all its surface planes
        // (within epsilon)
        for (int i = 0; i < sector.Surfaces.Count; i++)
        {
            var s = sector.Surfaces[i];
            if (s.VertexIndices.Length < 3) continue;
            Vector3 v0 = level.Vertices[s.VertexIndices[0]];
            float dist = Vector3.Dot(pos - v0, s.Normal);
            if (dist < -epsilon)
            {
                return false;
            }
        }
        return true;
    }

    [Fact]
    public void TestCantinaSectorSequenceContainment()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;

        using var archive = GobArchive.FromFile(jk1Path);
        var entry = archive.Entries.First(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        string jklText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
        var level = JklReader.Parse(jklText);

        // Test points:
        // Point in booth:
        Vector3 pBooth = new Vector3(-10.40f, -5.35f, -1.15f);
        Assert.True(IsPointInsideSector(pBooth, level.Sectors[339], level));
        _output.WriteLine($"Point {pBooth} is INSIDE Sector 339: TRUE");

        // Point in hallway 349:
        Vector3 pHall = new Vector3(-10.00f, -4.875f, -1.25f);
        Assert.True(IsPointInsideSector(pHall, level.Sectors[349], level));
        _output.WriteLine($"Point {pHall} is INSIDE Sector 349: TRUE");

        // Point in arch 337:
        Vector3 pArch337 = new Vector3(-9.675f, -4.875f, -1.25f);
        Assert.True(IsPointInsideSector(pArch337, level.Sectors[337], level));
        _output.WriteLine($"Point {pArch337} is INSIDE Sector 337: TRUE");

        // Point in foyer 361:
        Vector3 pFoyer361 = new Vector3(-9.500f, -5.000f, -1.25f);
        Assert.True(IsPointInsideSector(pFoyer361, level.Sectors[361], level));
        _output.WriteLine($"Point {pFoyer361} is INSIDE Sector 361: TRUE");

        // Point in arch 352:
        Vector3 pArch352 = new Vector3(-9.325f, -5.375f, -1.25f);
        Assert.True(IsPointInsideSector(pArch352, level.Sectors[352], level));
        _output.WriteLine($"Point {pArch352} is INSIDE Sector 352: TRUE");

        // Point in cantina 370:
        Vector3 pCantina370 = new Vector3(-9.000f, -5.375f, -1.25f);
        Assert.True(IsPointInsideSector(pCantina370, level.Sectors[370], level));
        _output.WriteLine($"Point {pCantina370} is INSIDE Sector 370: TRUE");
    }
}
