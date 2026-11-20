using System;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class PolygonCollisionTest
{
    private readonly ITestOutputHelper _output;

    public PolygonCollisionTest(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Computes the exact closest point on a convex 3D polygon to point C.
    /// </summary>
    public static Vector3 ClosestPointOnPolygon(Vector3 C, Vector3[] vertices, Vector3 normal)
    {
        // 1. Project C onto polygon plane
        Vector3 v0 = vertices[0];
        float distToPlane = Vector3.Dot(C - v0, normal);
        Vector3 proj = C - (distToPlane * normal);

        // 2. Check if proj is inside the polygon using edge cross products
        bool inside = true;
        int n = vertices.Length;

        for (int i = 0; i < n; i++)
        {
            Vector3 a = vertices[i];
            Vector3 b = vertices[(i + 1) % n];
            Vector3 edge = b - a;
            Vector3 toProj = proj - a;
            Vector3 cross = Vector3.Cross(edge, toProj);

            // If cross . normal < 0, proj is on the outside of edge (a -> b)
            if (Vector3.Dot(cross, normal) < -0.0001f)
            {
                inside = false;
                break;
            }
        }

        if (inside)
        {
            return proj;
        }

        // 3. Point is outside polygon face: find closest point on boundary segments
        float minSqDist = float.MaxValue;
        Vector3 closestPoint = vertices[0];

        for (int i = 0; i < n; i++)
        {
            Vector3 a = vertices[i];
            Vector3 b = vertices[(i + 1) % n];
            Vector3 ab = b - a;
            float t = Vector3.Dot(C - a, ab) / Vector3.Dot(ab, ab);
            t = Math.Clamp(t, 0f, 1f);
            Vector3 ptOnSeg = a + (t * ab);
            float sqDist = (C - ptOnSeg).LengthSquared();
            if (sqDist < minSqDist)
            {
                minSqDist = sqDist;
                closestPoint = ptOnSeg;
            }
        }

        return closestPoint;
    }

    [Fact]
    public void TestDoorwayChamferNoPhantomCollision()
    {
        // Doorway chamfer Surf 3270 from Sector 337:
        // v0: (-9.700, -4.800, -1.000)
        // v1: (-9.700, -4.750, -1.050)
        // v2: (-9.650, -4.750, -1.050)
        // v3: (-9.650, -4.800, -1.000)
        // Normal = (0, -0.707, -0.707)
        Vector3[] chamferVerts = [
            new Vector3(-9.700f, -4.800f, -1.000f),
            new Vector3(-9.700f, -4.750f, -1.050f),
            new Vector3(-9.650f, -4.750f, -1.050f),
            new Vector3(-9.650f, -4.800f, -1.000f)
        ];
        Vector3 normal = Vector3.Normalize(new Vector3(0, -0.7071f, -0.7071f));

        // Player walking through the center of the doorway at Z = -1.30 to -1.08
        // Test points along Kyle's body at X = -9.675 (midpoint), Y = -4.875 (doorway center):
        float radius = 0.065f;
        for (float z = -1.30f; z <= -1.08f; z += 0.05f)
        {
            Vector3 kylePt = new Vector3(-9.675f, -4.875f, z);
            Vector3 closest = ClosestPointOnPolygon(kylePt, chamferVerts, normal);
            float dist = (kylePt - closest).Length();

            _output.WriteLine($"Kyle at {kylePt}: Closest={closest}, Dist={dist:F4}");
            Assert.True(dist > radius, $"Distance ({dist}) must be > radius ({radius}) so no phantom collision occurs!");
        }
    }
}
