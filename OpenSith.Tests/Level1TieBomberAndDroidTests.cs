using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenSith.Client;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Xunit;

namespace OpenSith.Tests;

/// <summary>
/// Regression tests for Level 1 (<c>01narshadda.jkl</c>) TIE bomber <c>sighted:</c> / <c>PlayKey</c>
/// animation dispatch and grounded idle/roaming droid gravity (<c>powerdroid</c>, <c>r5</c>).
/// </summary>
public class Level1TieBomberAndDroidTests
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    private static VirtualFileSystem? TryMountRetailVfs()
    {
        string res1Path = Path.Combine(SteamPath, "Resource", "Res1hi.gob");
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");

        if (!File.Exists(res2Path) || !File.Exists(jk1Path))
            return null;

        var vfs = new VirtualFileSystem();
        if (File.Exists(res1Path))
            vfs.Mount(GobArchive.FromFile(res1Path));
        vfs.Mount(GobArchive.FromFile(res2Path));
        vfs.Mount(GobArchive.FromFile(jk1Path));
        return vfs;
    }

    private static Vector3 ComputeSectorCenter(JklLevel level, int sectorIndex)
    {
        var sector = level.Sectors[sectorIndex];
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (var surf in sector.Surfaces)
        {
            foreach (int vIdx in surf.VertexIndices)
            {
                if (vIdx >= 0 && vIdx < level.Vertices.Count)
                {
                    sum += level.Vertices[vIdx];
                    count++;
                }
            }
        }
        return count > 0 ? sum / count : Vector3.Zero;
    }

    private static bool TryGetFloorZ(JklLevel level, int sectorIndex, Vector3 pos, out float floorZ)
    {
        floorZ = float.MinValue;
        bool found = false;
        if (sectorIndex < 0 || sectorIndex >= level.Sectors.Count)
            return false;

        foreach (var surf in level.Sectors[sectorIndex].Surfaces)
        {
            if (surf.IsPortal || surf.VertexIndices.Length < 3 || surf.Normal.Z <= 0.4f)
                continue;

            var verts = new Vector3[surf.VertexIndices.Length];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = level.Vertices[surf.VertexIndices[i]];

            if (!IsPointInPolygon2D(pos.X, pos.Y, verts))
                continue;

            Vector3 v0 = verts[0];
            float planeZ = v0.Z - ((surf.Normal.X * (pos.X - v0.X) + surf.Normal.Y * (pos.Y - v0.Y)) / surf.Normal.Z);
            if (planeZ <= pos.Z + 0.05f && planeZ > floorZ)
            {
                floorZ = planeZ;
                found = true;
            }
        }
        return found;
    }

    private static bool IsPointInPolygon2D(float px, float py, Vector3[] verts)
    {
        bool inside = false;
        for (int i = 0, j = verts.Length - 1; i < verts.Length; j = i++)
        {
            float xi = verts[i].X, yi = verts[i].Y;
            float xj = verts[j].X, yj = verts[j].Y;
            bool intersect = ((yi > py) != (yj > py)) &&
                             (px < (xj - xi) * (py - yi) / (yj - yi + 1e-12f) + xi);
            if (intersect)
                inside = !inside;
        }
        return inside;
    }

    [Fact]
    public void PlayKey_InvokesOnPlayThingKeyframe_WithCorrectLoopFlag()
    {
        var level = new JklLevel();
        var vfs = new VirtualFileSystem();
        var sim = new WorldSimulation(level, vfs);

        var events = new List<(int ThingId, string Key, bool Loop)>();
        sim.OnPlayThingKeyframe += (id, key, loop) => events.Add((id, key, loop));

        int track1 = sim.PlayKey(202, "01VTieB.key", 0, 0x14);
        int track2 = sim.PlayKey(10, "idle.key", 0, 0x0);

        Assert.True(track1 >= 0);
        Assert.True(track2 >= 0);
        Assert.Equal(2, events.Count);
        Assert.Equal((202, "01VTieB.key", false), events[0]);
        Assert.Equal((10, "idle.key", true), events[1]);
    }

    [Fact]
    public void Retail01NarShaddaa_TieBomberSighted_PlaysKeyAndDestroysAfterSleep()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs, difficulty: 1);

        var playedKeys = new List<(int ThingId, string Key, bool Loop)>();
        sim.OnPlayThingKeyframe += (id, key, loop) => playedKeys.Add((id, key, loop));

        var tieBomber = level.Things[202];
        Assert.Equal("tiebomber", tieBomber.TemplateName, ignoreCase: true);

        // Place Kyle at Sector 322 / 326 landing pad looking directly toward Thing 202
        int padSector = level.Sectors.Count > 326 ? 326 : 322;
        Vector3 kylePos = ComputeSectorCenter(level, padSector);
        if (TryGetFloorZ(level, padSector, kylePos, out float padFloorZ))
            kylePos = new Vector3(kylePos.X, kylePos.Y, padFloorZ);

        // Ensure within 12 units and clear LOS if sector 326 center is slightly offset
        if (Vector3.Distance(kylePos, tieBomber.Position) > 11.5f ||
            !sim.HasLineOfSight(kylePos + new Vector3(0f, 0f, 0.157f), tieBomber.Position))
        {
            padSector = 322;
            kylePos = ComputeSectorCenter(level, padSector);
            if (TryGetFloorZ(level, padSector, kylePos, out float floor322))
                kylePos = new Vector3(kylePos.X, kylePos.Y, floor322);
        }

        Vector3 aimDir = Vector3.Normalize(tieBomber.Position - (kylePos + new Vector3(0f, 0f, 0.157f)));

        // Step 2.5s (past 2.0s Sleep in 01_TieBomber.cog sighted: handler)
        for (int i = 0; i < 25; i++)
            sim.Update(0.1f, kylePos, padSector, aimDir);

        Assert.Contains(playedKeys, k =>
            k.ThingId == 202 &&
            k.Key.Equals("01VTieB.key", StringComparison.OrdinalIgnoreCase) &&
            !k.Loop);

        // Advance another 8.0s so 01_TieBomber.cog finishes and destroys Thing 202
        for (int i = 0; i < 80; i++)
            sim.Update(0.1f, kylePos, padSector, aimDir);

        uint flags = (uint)sim.GetThingFlags(202);
        Assert.NotEqual(0u, flags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed));
    }

    [Fact]
    public void Retail01NarShaddaa_PowerDroidAndR5_StayGroundedAndMoveCleanly()
    {
        var vfs = TryMountRetailVfs();
        if (vfs == null) return;

        var level = JklReader.Parse(vfs.ReadAllText("jkl/01narshadda.jkl"), "01narshadda");
        var sim = new WorldSimulation(level, vfs, difficulty: 1);

        var powerDroid = level.Things[331];
        var r5 = level.Things[191];
        Assert.Equal("powerdroid", powerDroid.TemplateName, ignoreCase: true);
        Assert.Equal("r5", r5.TemplateName, ignoreCase: true);

        Assert.True(sim.GetNpcUseGravity(331));
        Assert.True(sim.GetNpcUseGravity(191));

        // Perturb Z slightly upward to verify idle/moving gravity pulls them back down
        powerDroid.Position += new Vector3(0f, 0f, 0.05f);
        r5.Position += new Vector3(0f, 0f, 0.05f);

        var spawn = level.GetPlayerSpawn()!;
        Vector3 kylePos = spawn.Position;
        int kyleSec = spawn.SectorIndex;

        for (int i = 0; i < 120; i++)
            sim.Update(1f / 30f, kylePos, kyleSec);

        Assert.True(TryGetFloorZ(level, powerDroid.SectorIndex, powerDroid.Position, out float pdFloorZ),
            "powerdroid should remain over a valid sector floor");
        Assert.True(TryGetFloorZ(level, r5.SectorIndex, r5.Position, out float r5FloorZ),
            "r5 should remain over a valid sector floor");

        // Both droids should be grounded near floorZ + HipHeightOffset (never floating > 0.01 above hip height)
        Assert.InRange(powerDroid.Position.Z - pdFloorZ, 0.02f, 0.25f);
        Assert.InRange(r5.Position.Z - r5FloorZ, 0.02f, 0.25f);
    }
}
