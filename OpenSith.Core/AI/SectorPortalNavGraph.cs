using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSith.Core.Physics;
using OpenSith.Core.World;

namespace OpenSith.Core.AI;

/// <summary>
/// Zero-authoring Deathmatch navigation graph built automatically from a Sith Engine
/// <see cref="JklLevel"/> sector-and-portal hierarchy.
/// </summary>
public sealed class SectorPortalNavGraph
{
    public readonly record struct PortalEdge(
        int FromSector,
        int ToSector,
        Vector3 PortalCentroid,
        float Cost,
        float StepDeltaZ,
        bool RequiresJump);

    private sealed class SectorNode
    {
        public required int SectorIndex;
        public required Vector3 FloorCenter;
        public required float MinFloorZ;
        public required float MaxFloorZ;
        public required bool IsUnderwater;
        public required List<PortalEdge> Edges;
    }

    private const int SectorFlagUnderwater = 0x2;
    private const float MaxJumpableStepZ = 0.34f; // ~0.28 JKU jump apex + 0.06 MaxStepHeight
    private const float MaxSafeDropZ = -2.5f;
    private const int MaxAStarExpansions = 2048;

    private readonly SectorNode[] _nodes;
    private readonly JklLevel _level;
    private readonly Dictionary<int, HashSet<int>> _reachableFromCache = new();

    public int SectorCount => _nodes.Length;

    public SectorPortalNavGraph(JklLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        _level = level;

        int count = level.Sectors.Count;
        _nodes = new SectorNode[count];

        for (int i = 0; i < count; i++)
            _nodes[i] = BuildSectorNode(level.Sectors[i], level);

        for (int i = 0; i < count; i++)
            PopulatePortalEdges(level.Sectors[i], _nodes[i], level);
    }

    public bool TryGetSectorCenter(int sectorIndex, out Vector3 floorCenter)
    {
        if (sectorIndex >= 0 && sectorIndex < _nodes.Length)
        {
            floorCenter = _nodes[sectorIndex].FloorCenter;
            return true;
        }

        floorCenter = Vector3.Zero;
        return false;
    }

    public IReadOnlyList<PortalEdge> GetEdges(int sectorIndex) =>
        sectorIndex >= 0 && sectorIndex < _nodes.Length ? _nodes[sectorIndex].Edges : [];

    /// <summary>
    /// Returns true if <paramref name="goalSector"/> is reachable from <paramref name="startSector"/>
    /// via traversable portal edges (cached per source sector via BFS).
    /// </summary>
    public bool CanReach(int startSector, int goalSector)
    {
        if ((uint)startSector >= (uint)_nodes.Length || (uint)goalSector >= (uint)_nodes.Length)
            return false;
        if (startSector == goalSector)
            return true;

        if (!_reachableFromCache.TryGetValue(startSector, out var reachable))
        {
            reachable = [startSector];
            var queue = new Queue<int>();
            queue.Enqueue(startSector);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (var edge in _nodes[cur].Edges)
                {
                    if (reachable.Add(edge.ToSector))
                        queue.Enqueue(edge.ToSector);
                }
            }
            _reachableFromCache[startSector] = reachable;
        }

        return reachable.Contains(goalSector);
    }

    /// <summary>
    /// Computes an A* path of sector portal crossing points from <paramref name="startSector"/>
    /// to <paramref name="goalSector"/>, appending <paramref name="goalPos"/> as the final waypoint.
    /// </summary>
    public bool FindPath(
        int startSector,
        Vector3 startPos,
        int goalSector,
        Vector3 goalPos,
        List<Vector3> outWaypoints,
        List<bool>? outRequiresJump = null)
    {
        ArgumentNullException.ThrowIfNull(outWaypoints);
        outWaypoints.Clear();
        outRequiresJump?.Clear();

        if (startSector < 0 || startSector >= _nodes.Length || goalSector < 0 || goalSector >= _nodes.Length)
            return false;

        if (startSector == goalSector)
        {
            outWaypoints.Add(goalPos);
            outRequiresJump?.Add(false);
            return true;
        }

        var frontier = new PriorityQueue<int, float>();
        var gScore = new Dictionary<int, float> { [startSector] = 0f };
        var cameFrom = new Dictionary<int, PortalEdge>();
        var closed = new HashSet<int>();

        frontier.Enqueue(startSector, Vector3.Distance(startPos, goalPos));

        bool found = false;
        int expansions = 0;

        while (frontier.TryDequeue(out int current, out _) && expansions < MaxAStarExpansions)
        {
            if (current == goalSector)
            {
                found = true;
                break;
            }

            if (!closed.Add(current))
                continue;

            expansions++;
            float currentG = gScore[current];
            Vector3 currentPos = current == startSector ? startPos : _nodes[current].FloorCenter;

            foreach (var edge in _nodes[current].Edges)
            {
                if (closed.Contains(edge.ToSector))
                    continue;

                float stepCost = Vector3.Distance(currentPos, edge.PortalCentroid) +
                                 Vector3.Distance(edge.PortalCentroid, _nodes[edge.ToSector].FloorCenter);
                float tentativeG = currentG + MathF.Max(0.05f, stepCost);

                if (!gScore.TryGetValue(edge.ToSector, out float prevG) || tentativeG < prevG)
                {
                    gScore[edge.ToSector] = tentativeG;
                    cameFrom[edge.ToSector] = edge;
                    float heuristic = Vector3.Distance(_nodes[edge.ToSector].FloorCenter, goalPos);
                    frontier.Enqueue(edge.ToSector, tentativeG + heuristic);
                }
            }
        }

        if (!found)
            return false;

        var reverseEdges = new List<PortalEdge>();
        int cursor = goalSector;
        while (cursor != startSector && cameFrom.TryGetValue(cursor, out var edge))
        {
            reverseEdges.Add(edge);
            cursor = edge.FromSector;
        }

        if (cursor != startSector)
            return false;

        for (int i = reverseEdges.Count - 1; i >= 0; i--)
        {
            outWaypoints.Add(reverseEdges[i].PortalCentroid);
            outRequiresJump?.Add(reverseEdges[i].RequiresJump);
        }

        outWaypoints.Add(goalPos);
        outRequiresJump?.Add(false);
        return true;
    }

    /// <summary>
    /// Selects a reachable sector different from <paramref name="currentSector"/> for roaming.
    /// </summary>
    public int PickRoamSector(int currentSector, Random? rng = null)
    {
        if (_nodes.Length <= 1)
            return Math.Clamp(currentSector, 0, Math.Max(0, _nodes.Length - 1));

        rng ??= Random.Shared;
        if (currentSector < 0 || currentSector >= _nodes.Length)
            return rng.Next(_nodes.Length);

        var visited = new HashSet<int> { currentSector };
        var queue = new Queue<int>();
        var candidates = new List<int>();
        queue.Enqueue(currentSector);

        while (queue.Count > 0 && visited.Count < 128)
        {
            int sec = queue.Dequeue();
            foreach (var edge in _nodes[sec].Edges)
            {
                if (visited.Add(edge.ToSector))
                {
                    candidates.Add(edge.ToSector);
                    queue.Enqueue(edge.ToSector);
                }
            }
        }

        return candidates.Count > 0
            ? candidates[rng.Next(candidates.Count)]
            : currentSector;
    }

    private static SectorNode BuildSectorNode(JklSector sector, JklLevel level)
    {
        Vector3 floorAccum = Vector3.Zero;
        int floorCount = 0;
        float minFloorZ = float.MaxValue;
        float maxFloorZ = float.MinValue;

        foreach (var surface in sector.Surfaces)
        {
            if (surface.VertexIndices.Length < 3 || PlayerPhysics.IsPassablePortal(surface, level))
                continue;

            bool isWalkableFloor = surface.Normal.Z >= 0.6f &&
                (surface.SurfaceFlags == 0 || (surface.SurfaceFlags & 0x1) != 0);
            if (!isWalkableFloor)
                continue;

            Vector3 centroid = ComputeSurfaceCentroid(surface, level, out float surfMinZ, out float surfMaxZ);
            floorAccum += centroid;
            floorCount++;
            if (surfMinZ < minFloorZ) minFloorZ = surfMinZ;
            if (surfMaxZ > maxFloorZ) maxFloorZ = surfMaxZ;
        }

        Vector3 floorCenter;
        if (floorCount > 0)
        {
            floorCenter = floorAccum / floorCount;
            floorCenter.Z = minFloorZ + 0.02f;
        }
        else
        {
            minFloorZ = sector.BoundBoxMin.Z;
            maxFloorZ = sector.BoundBoxMin.Z;
            floorCenter = new Vector3(sector.Center.X, sector.Center.Y, sector.BoundBoxMin.Z + 0.02f);
        }

        return new SectorNode
        {
            SectorIndex = sector.SectorIndex,
            FloorCenter = floorCenter,
            MinFloorZ = minFloorZ,
            MaxFloorZ = maxFloorZ,
            IsUnderwater = (sector.Flags & SectorFlagUnderwater) != 0,
            Edges = []
        };
    }

    private void PopulatePortalEdges(JklSector sector, SectorNode fromNode, JklLevel level)
    {
        foreach (var surface in sector.Surfaces)
        {
            if (!PlayerPhysics.IsPassablePortal(surface, level) || surface.MirrorSector == null)
                continue;

            int targetIdx = surface.MirrorSector.SectorIndex;
            if (targetIdx == fromNode.SectorIndex || targetIdx < 0 || targetIdx >= _nodes.Length)
                continue;

            SectorNode toNode = _nodes[targetIdx];
            Vector3 centroid = ComputeSurfaceCentroid(surface, level, out float portalMinZ, out float portalMaxZ);

            float stepDeltaZ = toNode.MinFloorZ - fromNode.MaxFloorZ;
            bool underwaterAssist = fromNode.IsUnderwater || toNode.IsUnderwater;
            if (!underwaterAssist)
            {
                if (portalMinZ > fromNode.MaxFloorZ + MaxJumpableStepZ)
                    continue;
                if (stepDeltaZ > MaxJumpableStepZ || stepDeltaZ < MaxSafeDropZ)
                    continue;
            }

            float targetFloorZ = Math.Clamp(MathF.Max(fromNode.MinFloorZ, toNode.MinFloorZ) + 0.04f, portalMinZ, MathF.Max(portalMinZ, portalMaxZ - 0.02f));
            centroid.Z = targetFloorZ;

            bool requiresJump = !underwaterAssist && stepDeltaZ > 0.06f;
            float cost = MathF.Max(0.05f, Vector3.Distance(fromNode.FloorCenter, centroid) + Vector3.Distance(centroid, toNode.FloorCenter));

            fromNode.Edges.Add(new PortalEdge(
                fromNode.SectorIndex,
                targetIdx,
                centroid,
                cost,
                stepDeltaZ,
                requiresJump));
        }
    }

    private static Vector3 ComputeSurfaceCentroid(JklSurface surface, JklLevel level, out float minZ, out float maxZ)
    {
        Vector3 sum = Vector3.Zero;
        int valid = 0;
        minZ = float.MaxValue;
        maxZ = float.MinValue;

        foreach (int vIdx in surface.VertexIndices)
        {
            if ((uint)vIdx >= (uint)level.Vertices.Count)
                continue;

            Vector3 v = level.Vertices[vIdx];
            sum += v;
            valid++;
            if (v.Z < minZ) minZ = v.Z;
            if (v.Z > maxZ) maxZ = v.Z;
        }

        if (valid == 0)
        {
            minZ = 0f;
            maxZ = 0f;
            return Vector3.Zero;
        }

        return sum / valid;
    }
}
