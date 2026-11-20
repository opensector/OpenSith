using System;
using System.IO;
using System.Numerics;
using System.Text;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class Sector0InspectionTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public Sector0InspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectSector0AndThing172()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var t172 = level.Things[172];
        _output.WriteLine($"Thing 172: {t172}, Extra='{t172.ExtraArguments}'");

        var sec0 = level.Sectors[0];
        _output.WriteLine($"Sector 0: Bounds Min={sec0.BoundBoxMin}, Max={sec0.BoundBoxMax}, Center={sec0.Center}, Radius={sec0.Radius}");
        _output.WriteLine($"Sector 0 Surfaces Count: {sec0.Surfaces.Count}");

        foreach (var s in sec0.Surfaces)
        {
            var v0 = (s.VertexIndices.Length > 0) ? level.Vertices[s.VertexIndices[0]] : Vector3.Zero;
            _output.WriteLine($"  Surf {s.SurfaceIndex}: Norm={s.Normal}, Mat={s.MaterialIndex}, Adjoin={s.AdjoinIndex}, Verts={s.VertexIndices.Length}, V0={v0}");
        }

        // Test PlayerPhysics at Thing 172
        var player = new PlayerPhysics(t172.Position, t172.SectorIndex);
        _output.WriteLine($"Initial Pos: {player.Position}, Grounded: {player.IsGrounded}");

        // Step physics 30 ticks
        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 30; i++)
        {
            player.Update(dt, Vector3.Zero, false, level);
            _output.WriteLine($"Tick {i}: Pos={player.Position}, Grounded={player.IsGrounded}, Vel={player.Velocity}");
        }
    }
}
