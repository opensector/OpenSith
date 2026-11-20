using System;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class Sector361InspectionTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public Sector361InspectionTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectSector361Collision()
    {
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");
        if (!File.Exists(jk1Path)) return;

        using var archive = GobArchive.FromFile(jk1Path);
        var entry = archive.Entries.First(e => e.Path.EndsWith("01narshadda.jkl", StringComparison.OrdinalIgnoreCase));
        string jklText = System.Text.Encoding.ASCII.GetString(archive.ReadAllBytes(entry.Path));
        var level = JklReader.Parse(jklText);

        var sec = level.Sectors[361];
        Vector3 kylePos = new Vector3(-9.415f, -4.875f, -1.325f);
        float radius = 0.065f;
        float height = 0.22f;

        _output.WriteLine($"Inspecting Sector 361 at Kyle pos: {kylePos}, Radius={radius}");

        for (int i = 0; i < sec.Surfaces.Count; i++)
        {
            var s = sec.Surfaces[i];
            float sMinX = float.MaxValue, sMaxX = float.MinValue;
            float sMinY = float.MaxValue, sMaxY = float.MinValue;
            float sMinZ = float.MaxValue, sMaxZ = float.MinValue;

            for (int v = 0; v < s.VertexIndices.Length; v++)
            {
                var pt = level.Vertices[s.VertexIndices[v]];
                sMinX = MathF.Min(sMinX, pt.X);
                sMaxX = MathF.Max(sMaxX, pt.X);
                sMinY = MathF.Min(sMinY, pt.Y);
                sMaxY = MathF.Max(sMaxY, pt.Y);
                sMinZ = MathF.Min(sMinZ, pt.Z);
                sMaxZ = MathF.Max(sMaxZ, pt.Z);
            }

            Vector3 v0 = level.Vertices[s.VertexIndices[0]];
            float distToPlane = Vector3.Dot(kylePos - v0, s.Normal);

            string pType = s.IsPortal ? $"PORTAL -> {s.MirrorSector?.SectorIndex}" : "SOLID";
            _output.WriteLine($"Surf {s.SurfaceIndex,4}: Normal=({s.Normal.X:F3}, {s.Normal.Y:F3}, {s.Normal.Z:F3}) [{pType}]");
            _output.WriteLine($"   Bounds: X=[{sMinX:F3}, {sMaxX:F3}], Y=[{sMinY:F3}, {sMaxY:F3}], Z=[{sMinZ:F3}, {sMaxZ:F3}]");
            _output.WriteLine($"   DistToPlane={distToPlane:F4}");

            // Check if player cylinder overlaps surface bounds
            bool xOverlap = (kylePos.X + radius >= sMinX - radius) && (kylePos.X - radius <= sMaxX + radius);
            bool yOverlap = (kylePos.Y + radius >= sMinY - radius) && (kylePos.Y - radius <= sMaxY + radius);
            bool zOverlap = (kylePos.Z + height >= sMinZ) && (kylePos.Z <= sMaxZ);

            if (xOverlap && yOverlap && zOverlap)
            {
                _output.WriteLine($"   >>> OVERLAPS BOUNDS! IsPortal={s.IsPortal}");
            }
        }
    }
}
