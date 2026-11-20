using System;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class RendererUvInspectionTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public RendererUvInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectSector0RenderedVertices()
    {
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");

        using var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(res2Path));
        vfs.Mount(GobArchive.FromFile(jk1Path));

        string jklText = vfs.ReadAllText("jkl/01narshadda.jkl");
        var level = JklReader.Parse(jklText, "01narshadda");

        var sec0 = level.Sectors[0];
        _output.WriteLine($"Sector 0 Surfaces: {sec0.Surfaces.Count}");

        foreach (var surf in sec0.Surfaces)
        {
            if (surf.MaterialIndex < 0) continue;
            string matName = level.Materials[surf.MaterialIndex];

            _output.WriteLine($"\nSurface {surf.SurfaceIndex}: Mat={matName} (idx {surf.MaterialIndex}), Normal={surf.Normal}, Verts={surf.VertexIndices.Length}");
            for (int i = 0; i < surf.VertexIndices.Length; i++)
            {
                int vIdx = surf.VertexIndices[i];
                int uvIdx = surf.TextureIndices[i];
                var pos = (vIdx >= 0 && vIdx < level.Vertices.Count) ? level.Vertices[vIdx] : Vector3.Zero;
                var rawUv = (uvIdx >= 0 && uvIdx < level.TextureVertices.Count) ? level.TextureVertices[uvIdx] : Vector2.Zero;
                float intensity = (i < surf.VertexIntensities.Length) ? surf.VertexIntensities[i] : 1.0f;
                _output.WriteLine($"  [{i}] V={vIdx}: Pos={pos}, UVIdx={uvIdx}: RawUV={rawUv}, Intensity={intensity}");
            }
        }
    }
}
