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

public class CantinaBoothSectorTest
{
    private readonly ITestOutputHelper _output;
    private const string SteamJk1Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Episode\JK1.GOB";

    public CantinaBoothSectorTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectSector339InDetail()
    {
        if (!File.Exists(SteamJk1Path)) return;

        using var archive = GobArchive.FromFile(SteamJk1Path);
        string text = Encoding.ASCII.GetString(archive.ReadAllBytes("jkl/01narshadda.jkl"));
        var level = JklReader.Parse(text, "01narshadda");

        var sec339 = level.Sectors[339];
        var t0 = level.Things[0];
        _output.WriteLine($"Thing 0 (Kyle): Pos={t0.Position}, Sector={t0.SectorIndex}, Yaw={t0.PitchYawRoll.Y}");
        _output.WriteLine($"Sector 339: Bounds Min={sec339.BoundBoxMin}, Max={sec339.BoundBoxMax}, Center={sec339.Center}");
        _output.WriteLine($"Sector 339 Surfaces count: {sec339.Surfaces.Count}");

        foreach (var s in sec339.Surfaces)
        {
            var v0 = (s.VertexIndices.Length > 0) ? level.Vertices[s.VertexIndices[0]] : Vector3.Zero;
            float dist = Vector3.Dot(t0.Position - v0, s.Normal);
            _output.WriteLine($"  Surf {s.SurfaceIndex}: Mat={s.MaterialIndex}, Adjoin={s.AdjoinIndex}, Normal={s.Normal}, distToKyle={dist:F4}, V0={v0}");
            if (s.Normal.Z > 0.4f)
            {
                // Floor plane math
                float planeZ = v0.Z - ((s.Normal.X * (t0.Position.X - v0.X) + s.Normal.Y * (t0.Position.Y - v0.Y)) / s.Normal.Z);
                _output.WriteLine($"    -> FLOOR planeZ at Kyle XY: {planeZ:F4}");
            }
        }

        // Test PlayerPhysics at Thing 0
        var player = new PlayerPhysics(t0.Position, t0.SectorIndex);
        _output.WriteLine($"\nInitial Player Pos: {player.Position}, Grounded: {player.IsGrounded}");

        float dt = 1.0f / 60.0f;
        for (int i = 0; i < 20; i++)
        {
            player.Update(dt, Vector3.Zero, false, level);
            _output.WriteLine($"Tick {i}: Pos={player.Position}, Sector={player.CurrentSectorIndex}, Grounded={player.IsGrounded}, Vel={player.Velocity}");
        }
    }
}
