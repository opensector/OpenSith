using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using OpenSith.Core.Models;
using OpenSith.Core.World;

namespace OpenSith.Core.Physics;

/// <summary>
/// Represents a solid cylindrical obstacle (such as a door, actor, or walkstruct) for player collision.
/// </summary>
public readonly record struct SolidThingCollider(Vector3 Position, float Radius, float Height, float BottomOffset = 0.0f);

/// <summary>
/// First-person player physics and sector-constrained collision controller.
/// Replicates Kyle Katarn's movement, gravity, slope handling, jumping, and portal traversal
/// using authentic LucasArts Sith Engine principles.
/// </summary>
public sealed class PlayerPhysics
{
    private const int SectorFlagUnderwater = 0x2; // SITH_SECTOR_UNDERWATER (sithSector.h / sithPhysics.c)
    private const float UnderwaterSpeedScale = 0.6f;
    private const float UnderwaterDragPer60HzTick = 0.85f;
    private const float UnderwaterBuoyancyGravityScale = 0.04f;

    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    public int CurrentSectorIndex { get; set; }
    public bool IsGrounded { get; private set; }
    public bool IsUnderwater { get; private set; }

    public float Radius { get; set; } = 0.065f;
    public float Height { get; set; } = 0.18f;
    public float PelvisOffset { get; set; } = 0.12f; // Authentic ky.3do insertOffset.z (feet -> pelvis)
    public float EyeOffsetFromPelvis { get; set; } = 0.037f; // Authentic walkplayer eyeoffset
    public float EyeHeight { get; set; } = 0.18f; // Standing eye height from feet
    public float MaxStepHeight { get; set; } = 0.06f;
    public float MoveSpeed { get; set; } = 2.0f;
    public float JumpSpeed { get; set; } = 1.4f;
    public float Gravity { get; set; } = 3.5f;

    /// <summary>
    /// Optional resolver for loading 3DO models by filename for authentic SITH_COLLIDE_FACE collision.
    /// </summary>
    public Func<string, ThreeDoModel?>? ModelResolver { get; set; }

    public Vector3 PelvisPosition => Position + new Vector3(0, 0, PelvisOffset);
    public Vector3 EyePosition => Position + new Vector3(0, 0, EyeHeight);

    public PlayerPhysics(Vector3 spawnPosition, int startSector)
    {
        Position = spawnPosition;
        CurrentSectorIndex = startSector;
    }

    /// <summary>
    /// Configures pelvis height offset from the player's 3DO model (InsertOffset.Z, defaulting to 0.12m).
    /// </summary>
    public void ConfigureFromModel(ThreeDoModel? model)
    {
        if (model != null && model.InsertOffset.Z > 0.0f)
        {
            PelvisOffset = model.InsertOffset.Z;
        }
    }

    /// <summary>
    /// Updates player position and velocity for one physics tick.
    /// </summary>
    public void Update(
        float dt,
        Vector3 wishDirection,
        bool wishJump,
        JklLevel level,
        IReadOnlyList<SolidThingCollider>? solidThings = null,
        Func<string, ThreeDoModel?>? modelResolver = null)
    {
        IsUnderwater = IsSectorUnderwater(CurrentSectorIndex, level);

        if (IsUnderwater)
        {
            UpdateUnderwaterVelocity(dt, wishDirection, wishJump, level);
        }
        else
        {
            // 1. Horizontal movement
            Vector3 horizontalWish = new Vector3(wishDirection.X, wishDirection.Y, 0.0f);
            if (horizontalWish.LengthSquared() > 0.001f)
            {
                horizontalWish = Vector3.Normalize(horizontalWish);
            }

            Vector3 targetVelocity = horizontalWish * MoveSpeed;
            float accel = IsGrounded ? 14.0f : 3.0f;
            Velocity = new Vector3(
                MathF.Min(MathF.Max(Velocity.X + (targetVelocity.X - Velocity.X) * accel * dt, -MoveSpeed), MoveSpeed),
                MathF.Min(MathF.Max(Velocity.Y + (targetVelocity.Y - Velocity.Y) * accel * dt, -MoveSpeed), MoveSpeed),
                Velocity.Z
            );

            // 2. Jump & Gravity
            if (IsGrounded)
            {
                if (wishJump)
                {
                    Velocity = new Vector3(Velocity.X, Velocity.Y, JumpSpeed);
                    IsGrounded = false;
                }
            }
            else
            {
                Velocity = new Vector3(Velocity.X, Velocity.Y, Velocity.Z - (Gravity * dt));
            }
        }

        // 3. Integrate position
        Vector3 newPos = Position + (Velocity * dt);

        // Void safety clamp (e.g. falling into endless abyss of Nar Shaddaa)
        if (newPos.Z < -20.0f)
        {
            var spawn = level.GetPlayerSpawn();
            if (spawn != null)
            {
                newPos = spawn.Position;
                CurrentSectorIndex = spawn.SectorIndex;
                Velocity = Vector3.Zero;
            }
        }

        // 4. Sector-constrained collision, solid thing collision, and portal traversal
        newPos = ResolveCollisionsAndSector(newPos, level, solidThings, modelResolver ?? ModelResolver);

        Position = newPos;
    }

    private void UpdateUnderwaterVelocity(float dt, Vector3 wishDirection, bool wishJump, JklLevel level)
    {
        float swimSpeed = MoveSpeed * UnderwaterSpeedScale;
        float wishZ = wishJump ? MathF.Max(wishDirection.Z, 1.0f) : wishDirection.Z;
        Vector3 swimWish = new Vector3(wishDirection.X, wishDirection.Y, wishZ);
        if (swimWish.LengthSquared() > 0.001f)
        {
            swimWish = Vector3.Normalize(swimWish);
        }

        // Exponential water drag (0.85 per 60Hz tick)
        float waterDrag = MathF.Pow(UnderwaterDragPer60HzTick, dt * 60.0f);
        Vector3 vel = Velocity * waterDrag;

        if (swimWish.LengthSquared() > 0.001f)
        {
            Vector3 targetVelocity = swimWish * swimSpeed;
            vel += (targetVelocity - vel) * MathF.Min(1.0f, 8.0f * dt);
        }
        else
        {
            // Gentle underwater buoyancy/gravity when not actively swimming vertically
            vel.Z -= Gravity * UnderwaterBuoyancyGravityScale * dt;
        }

        // Allow jumping out of the water when at a water surface adjoin to an air sector
        if (wishJump && IsAtWaterSurface(CurrentSectorIndex, level))
        {
            vel.Z = MathF.Max(vel.Z, JumpSpeed);
        }

        Velocity = vel;
        IsGrounded = false;
    }

    private static bool IsSectorUnderwater(int sectorIndex, JklLevel level)
    {
        return sectorIndex >= 0 &&
               sectorIndex < level.Sectors.Count &&
               (level.Sectors[sectorIndex].Flags & SectorFlagUnderwater) != 0;
    }

    private bool IsAtWaterSurface(int sectorIndex, JklLevel level)
    {
        if (sectorIndex < 0 || sectorIndex >= level.Sectors.Count)
            return false;

        var curSector = level.Sectors[sectorIndex];
        foreach (var surface in curSector.Surfaces)
        {
            if (!surface.IsPortal || surface.MirrorSector == null)
                continue;

            if ((surface.MirrorSector.Flags & SectorFlagUnderwater) != 0)
                continue;

            if (surface.VertexIndices.Length < 3)
                continue;

            Vector3[] portalVerts = GetSurfaceVertices(surface, level);
            GetPolygonZBounds(portalVerts, out float minZ, out float maxZ);
            if (Position.Z + Height >= minZ - 0.05f && Position.Z <= maxZ + 0.05f)
            {
                return true;
            }
        }

        return false;
    }

    private Vector3 ResolveCollisionsAndSector(
        Vector3 proposedPos,
        JklLevel level,
        IReadOnlyList<SolidThingCollider>? solidThings,
        Func<string, ThreeDoModel?>? modelResolver)
    {
        if (CurrentSectorIndex < 0 || CurrentSectorIndex >= level.Sectors.Count)
            return proposedPos;

        // 1. Gather active sectors: CurrentSector plus any adjoining sectors connected by portals
        // that the player's bounding sphere / cylinder can reach.
        var activeSectors = new List<JklSector>(4);
        var curSector = level.Sectors[CurrentSectorIndex];
        activeSectors.Add(curSector);

        foreach (var surface in curSector.Surfaces)
        {
            if (surface.IsPortal && surface.MirrorSector != null)
            {
                if (!activeSectors.Contains(surface.MirrorSector))
                {
                    activeSectors.Add(surface.MirrorSector);
                }
            }
        }

        // Include 2nd-degree adjoining sectors (threshold/archway sectors can be 0.05m thick, thinner than Radius)
        int firstHopCount = activeSectors.Count;
        for (int i = 1; i < firstHopCount; i++)
        {
            var hop1 = activeSectors[i];
            foreach (var surface in hop1.Surfaces)
            {
                if (surface.IsPortal && surface.MirrorSector != null && !activeSectors.Contains(surface.MirrorSector))
                {
                    activeSectors.Add(surface.MirrorSector);
                }
            }
        }

        // 2a. Ceiling checks across all active sectors
        foreach (var sec in activeSectors)
        {
            foreach (var surface in sec.Surfaces)
            {
                if (surface.VertexIndices.Length < 3) continue;

                Vector3 normal = surface.Normal;
                if (IsPassablePortal(surface, level) || normal.Z >= -0.4f) continue;

                int v0Idx = surface.VertexIndices[0];
                if (v0Idx < 0 || v0Idx >= level.Vertices.Count) continue;
                Vector3 v0 = level.Vertices[v0Idx];

                Vector3[] ceilVerts = GetSurfaceVertices(surface, level);
                if (IsPointInsideConvexPolygon2D(proposedPos.X, proposedPos.Y, ceilVerts, 0.05f))
                {
                    GetPolygonZBounds(ceilVerts, out float minCeilZ, out float maxCeilZ);
                    float ceilingZ = v0.Z - ((normal.X * (proposedPos.X - v0.X) + normal.Y * (proposedPos.Y - v0.Y)) / normal.Z);
                    ceilingZ = Math.Clamp(ceilingZ, minCeilZ, maxCeilZ);
                    if (ceilingZ >= proposedPos.Z && proposedPos.Z + Height > ceilingZ)
                    {
                        proposedPos = new Vector3(proposedPos.X, proposedPos.Y, ceilingZ - Height);
                        if (Velocity.Z > 0.0f)
                        {
                            Velocity = new Vector3(Velocity.X, Velocity.Y, 0.0f);
                        }
                    }
                }
            }
        }

        // 2b. Solid thing / door collisions (before wall pushback so sector walls stay hard bounds)
        ResolveSolidThingCollisions(ref proposedPos, level, solidThings, modelResolver);

        // 2c. Solid wall collisions across all active sectors (with MaxStepHeight ledge tolerance)
        float testZ = proposedPos.Z + MathF.Min(0.08f, Height * 0.5f);
        foreach (var sec in activeSectors)
        {
            foreach (var surface in sec.Surfaces)
            {
                if (surface.VertexIndices.Length < 3) continue;

                Vector3 normal = surface.Normal;
                bool isCeiling = normal.Z < -0.6f;
                bool isWalkableFloor = normal.Z >= 0.6f && (surface.SurfaceFlags == 0 || (surface.SurfaceFlags & 0x1) != 0);
                if (IsPassablePortal(surface, level) || isCeiling || isWalkableFloor) continue;

                Vector3[] polyVerts = GetSurfaceVertices(surface, level);

                // Skip low ledges / steps within MaxStepHeight so player can walk up smoothly
                float maxPolyZ = float.MinValue;
                for (int i = 0; i < polyVerts.Length; i++)
                {
                    if (polyVerts[i].Z > maxPolyZ) maxPolyZ = polyVerts[i].Z;
                }
                if (maxPolyZ <= proposedPos.Z + MaxStepHeight)
                {
                    continue;
                }

                // Solid wall: test true 3D convex polygon closest point against player cylinder
                Vector3 testCenter = new Vector3(proposedPos.X, proposedPos.Y, testZ);
                Vector3 closest = ClosestPointOnConvexPolygon(testCenter, polyVerts, normal);
                Vector3 diff = testCenter - closest;
                float dist = diff.Length();

                if (dist < Radius)
                {
                    Vector3 colNormal = dist > 0.0001f ? (diff / dist) : normal;
                    // Keep push strictly horizontal for walls
                    colNormal = new Vector3(colNormal.X, colNormal.Y, 0.0f);
                    if (colNormal.LengthSquared() > 0.0001f)
                    {
                        colNormal = Vector3.Normalize(colNormal);
                        float push = Radius - dist;
                        proposedPos += colNormal * push;

                        // Wall sliding: remove velocity directed into wall
                        float velIntoWall = Vector3.Dot(Velocity, colNormal);
                        if (velIntoWall < 0.0f)
                        {
                            Velocity -= colNormal * velIntoWall;
                        }
                    }
                }
            }
        }

        // 2d. Floor checks on wall-resolved XY (swept against previous Z so falling never tunnels through floors,
        // while upward step-up snapping remains capped at MaxStepHeight)
        float floorZ = float.MinValue;
        bool foundFloor = false;
        float floorSweepCeiling = MathF.Max(Position.Z, proposedPos.Z) + MaxStepHeight;
        foreach (var sec in activeSectors)
        {
            foreach (var surface in sec.Surfaces)
            {
                if (surface.VertexIndices.Length < 3) continue;

                Vector3 normal = surface.Normal;
                if (IsPassablePortal(surface, level) ||
                    normal.Z < 0.6f ||
                    (surface.SurfaceFlags != 0 && (surface.SurfaceFlags & 0x1) == 0))
                {
                    continue;
                }

                Vector3[] floorVerts = GetSurfaceVertices(surface, level);
                TryUpdateFloorFromPolygon(proposedPos, floorVerts, normal, floorSweepCeiling, ref floorZ, ref foundFloor);
            }
        }

        // Check Collide == 3 (SITH_COLLIDE_FACE) 3DO things for walkable top faces (worldNormal.Z >= 0.6f)
        if (modelResolver != null)
        {
            for (int i = 0; i < level.Things.Count; i++)
            {
                var thing = level.Things[i];
                if (!TryGetThingCollideInfo(thing, level, out int collideMode, out _, out _, out _, out _) ||
                    collideMode != 3)
                {
                    continue;
                }

                ThreeDoModel? model = ResolveThingThreeDoModel(thing, level, modelResolver);
                if (model == null || model.Meshes.Count == 0)
                    continue;

                UpdateFloorFromThreeDoModel(proposedPos, thing, model, floorSweepCeiling, ref floorZ, ref foundFloor);
            }
        }

        // 3. Floor clamping & grounding
        if (IsUnderwater)
        {
            // Underwater: prevent clipping below the floor, without snapping to it.
            if (foundFloor && proposedPos.Z <= floorZ)
            {
                proposedPos = new Vector3(proposedPos.X, proposedPos.Y, floorZ);
                if (Velocity.Z < 0.0f)
                {
                    Velocity = new Vector3(Velocity.X, Velocity.Y, 0.0f);
                }
            }
            IsGrounded = false;
        }
        else if (foundFloor && proposedPos.Z <= floorZ + 0.03f && Velocity.Z <= 0.0f)
        {
            proposedPos = new Vector3(proposedPos.X, proposedPos.Y, floorZ);
            Velocity = new Vector3(Velocity.X, Velocity.Y, 0.0f);
            IsGrounded = true;
        }
        else
        {
            IsGrounded = false;
        }

        // 4. Dynamic Sector Determination (after floor step-up clamping so sector tracks stepped-onto ledges)
        CurrentSectorIndex = FindSectorAtPosition(proposedPos, CurrentSectorIndex, level);
        IsUnderwater = IsSectorUnderwater(CurrentSectorIndex, level);

        return proposedPos;
    }

    private void ResolveSolidThingCollisions(
        ref Vector3 proposedPos,
        JklLevel level,
        IReadOnlyList<SolidThingCollider>? solidThings,
        Func<string, ThreeDoModel?>? modelResolver)
    {
        for (int i = 0; i < level.Things.Count; i++)
        {
            CollideWithLevelThing(ref proposedPos, level.Things[i], level, modelResolver);
        }

        if (solidThings != null)
        {
            for (int i = 0; i < solidThings.Count; i++)
            {
                CollideWithCylinder(ref proposedPos, solidThings[i]);
            }
        }
    }

    private void CollideWithCylinder(ref Vector3 proposedPos, SolidThingCollider collider)
    {
        if (!OverlapsVertically(proposedPos, collider.Position.Z, collider.Height, collider.BottomOffset))
            return;

        float dx = proposedPos.X - collider.Position.X;
        float dy = proposedPos.Y - collider.Position.Y;
        float combinedRadius = Radius + collider.Radius;
        float distSq = (dx * dx) + (dy * dy);

        if (distSq >= combinedRadius * combinedRadius)
            return;

        float dist = MathF.Sqrt(distSq);
        Vector2 pushNormal = dist > 0.0001f
            ? new Vector2(dx / dist, dy / dist)
            : new Vector2(1.0f, 0.0f);

        PushOutHorizontally(ref proposedPos, pushNormal, combinedRadius - dist);
    }

    private void CollideWithOrientedBox(
        ref Vector3 proposedPos,
        Vector3 boxCenter,
        float yawDeg,
        float halfWidth,
        float halfThickness,
        float height,
        float bottomOffset)
    {
        if (!OverlapsVertically(proposedPos, boxCenter.Z, height, bottomOffset))
            return;

        float yawRad = yawDeg * (MathF.PI / 180.0f);
        float cosYaw = MathF.Cos(yawRad);
        float sinYaw = MathF.Sin(yawRad);
        Vector2 right = new Vector2(cosYaw, sinYaw);
        Vector2 forward = new Vector2(-sinYaw, cosYaw);

        Vector2 rel = new Vector2(proposedPos.X - boxCenter.X, proposedPos.Y - boxCenter.Y);
        float localX = Vector2.Dot(rel, right);
        float localY = Vector2.Dot(rel, forward);

        float clampedX = Math.Clamp(localX, -halfWidth, halfWidth);
        float clampedY = Math.Clamp(localY, -halfThickness, halfThickness);

        float dx = localX - clampedX;
        float dy = localY - clampedY;
        float distSq = (dx * dx) + (dy * dy);

        if (distSq >= Radius * Radius)
            return;

        Vector2 localNormal;
        float push;
        if (distSq > 0.00000001f)
        {
            float dist = MathF.Sqrt(distSq);
            localNormal = new Vector2(dx / dist, dy / dist);
            push = Radius - dist;
        }
        else
        {
            // Player center is inside the OBB: push out along the shortest penetration axis.
            float penX = halfWidth - MathF.Abs(localX);
            float penY = halfThickness - MathF.Abs(localY);
            if (penY <= penX)
            {
                localNormal = new Vector2(0.0f, localY >= 0.0f ? 1.0f : -1.0f);
                push = Radius + penY;
            }
            else
            {
                localNormal = new Vector2(localX >= 0.0f ? 1.0f : -1.0f, 0.0f);
                push = Radius + penX;
            }
        }

        Vector2 worldNormal = (right * localNormal.X) + (forward * localNormal.Y);
        PushOutHorizontally(ref proposedPos, worldNormal, push);
    }

    private bool OverlapsVertically(Vector3 proposedPos, float centerZ, float height, float bottomOffset)
    {
        float thingMinZ = centerZ + bottomOffset;
        float thingMaxZ = thingMinZ + height;
        float playerMinZ = proposedPos.Z;
        float playerMaxZ = proposedPos.Z + Height;

        return playerMaxZ > thingMinZ && playerMinZ < thingMaxZ - 0.001f;
    }

    private void PushOutHorizontally(ref Vector3 proposedPos, Vector2 pushNormal, float push)
    {
        proposedPos += new Vector3(pushNormal.X * push, pushNormal.Y * push, 0.0f);

        float velIntoThing = (Velocity.X * pushNormal.X) + (Velocity.Y * pushNormal.Y);
        if (velIntoThing < 0.0f)
        {
            Velocity -= new Vector3(pushNormal.X * velIntoThing, pushNormal.Y * velIntoThing, 0.0f);
        }
    }

    private static bool TryGetThingCollideInfo(
        JklThing thing,
        JklLevel level,
        out int collideMode,
        out bool isDoor,
        out bool isActor,
        out bool isWalkstruct,
        out bool isWalkablePlatform)
    {
        collideMode = 0;
        isDoor = false;
        isActor = false;
        isWalkstruct = false;
        isWalkablePlatform = false;

        const uint NonInteractiveThingFlags = (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed);
        if (thing.IsNonSolid ||
            (thing.ThingFlags & NonInteractiveThingFlags) != 0 ||
            thing.IsPlayerSpawn ||
            string.IsNullOrWhiteSpace(thing.TemplateName))
        {
            return false;
        }

        string? type = ResolveTemplateParameter(level, thing.TemplateName, "type");
        if (string.Equals(type, "item", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "ghost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "explosion", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "weapon", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "corpse", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        isDoor = thing.TemplateName.Contains("door", StringComparison.OrdinalIgnoreCase);
        isActor = string.Equals(type, "actor", StringComparison.OrdinalIgnoreCase) ||
                  thing.TemplateName.Equals("_actor", StringComparison.OrdinalIgnoreCase);
        isWalkstruct = IsTemplateOrBasedOn(level, thing.TemplateName, "_walkstruct");
        isWalkablePlatform = thing.TemplateName.Contains("catwalk", StringComparison.OrdinalIgnoreCase) ||
                             thing.TemplateName.Contains("elev", StringComparison.OrdinalIgnoreCase) ||
                             thing.TemplateName.Contains("funicular", StringComparison.OrdinalIgnoreCase);

        string? collideStr = ResolveTemplateParameter(level, thing.TemplateName, "collide");
        if (collideStr != null && int.TryParse(collideStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCollide))
        {
            collideMode = parsedCollide;
        }
        else if (collideStr == null)
        {
            collideMode = (isDoor || isWalkstruct) ? 3 : (isActor ? 1 : 0);
        }

        return collideMode != 0;
    }

    private static ThreeDoModel? ResolveThingThreeDoModel(
        JklThing thing,
        JklLevel level,
        Func<string, ThreeDoModel?>? modelResolver)
    {
        if (modelResolver == null)
            return null;

        string modelName = !string.IsNullOrEmpty(thing.Model3D)
            ? thing.Model3D
            : level.ResolveModel3D(thing.TemplateName);

        return !string.IsNullOrEmpty(modelName) ? modelResolver(modelName) : null;
    }

    private void CollideWithLevelThing(
        ref Vector3 proposedPos,
        JklThing thing,
        JklLevel level,
        Func<string, ThreeDoModel?>? modelResolver)
    {
        if (!TryGetThingCollideInfo(thing, level, out int collideMode, out bool isDoor, out bool isActor, out bool isWalkstruct, out bool isWalkablePlatform))
            return;

        // SITH_COLLIDE_FACE (3): Authentic sphere vs transformed 3DO triangle/polygon mesh collision
        if (collideMode == 3)
        {
            ThreeDoModel? model = ResolveThingThreeDoModel(thing, level, modelResolver);
            if (model != null && model.Meshes.Count > 0)
            {
                CollideWithThreeDoModel(ref proposedPos, thing, model);
                return;
            }

            // Fallback when no 3DO model is available (e.g. minimal synthetic unit tests)
            if (!isDoor && (!isWalkstruct || isWalkablePlatform))
                return;

            float fbSize = thing.Size ?? TryParseTemplateFloat(level, thing.TemplateName, "size");
            float fbMovesize = thing.MoveSize ?? TryParseTemplateFloat(level, thing.TemplateName, "movesize");
            float fbHeight = TryParseTemplateFloat(level, thing.TemplateName, "height");

            if (isDoor)
            {
                float halfWidth = fbSize > 0.0f ? fbSize : 0.16f;
                float halfThickness = fbMovesize > 0.0f ? MathF.Min(fbMovesize, 0.055f) : 0.045f;
                float doorHeight = fbHeight > 0.0f ? fbHeight : 0.30f;
                CollideWithOrientedBox(
                    ref proposedPos,
                    thing.Position,
                    thing.PitchYawRoll.Y,
                    halfWidth,
                    halfThickness,
                    doorHeight,
                    -doorHeight * 0.5f);
            }
            else
            {
                float radius = fbMovesize > 0.0f ? fbMovesize : (fbSize > 0.0f ? fbSize : 0.08f);
                float height = fbHeight > 0.0f ? fbHeight * 2.0f : MathF.Max(0.10f, radius * 2.0f);
                CollideWithCylinder(ref proposedPos, new SolidThingCollider(thing.Position, radius, height, -height * 0.5f));
            }
            return;
        }

        // SITH_COLLIDE_SPHERE (1): Authentic sphere-vs-sphere using thing's MoveSize / Size
        if (collideMode == 1)
        {
            if (!isDoor && !isActor && (!isWalkstruct || isWalkablePlatform))
                return;

            float size = thing.Size ?? TryParseTemplateFloat(level, thing.TemplateName, "size");
            float movesize = thing.MoveSize ?? TryParseTemplateFloat(level, thing.TemplateName, "movesize");
            float sphereRadius = movesize > 0.0f ? movesize : (size > 0.0f ? size : 0.065f);

            CollideWithSphere(ref proposedPos, thing.Position, sphereRadius);
        }
    }

    private void CollideWithSphere(ref Vector3 proposedPos, Vector3 thingCenter, float thingRadius)
    {
        Vector3 sphereCenter = proposedPos + new Vector3(0.0f, 0.0f, PelvisOffset);
        Vector3 diff = sphereCenter - thingCenter;
        float combinedRadius = Radius + thingRadius;
        float distSq = diff.LengthSquared();

        if (distSq >= combinedRadius * combinedRadius)
            return;

        float dist = MathF.Sqrt(distSq);
        Vector3 normal = dist > 0.0001f ? (diff / dist) : Vector3.UnitX;
        float push = combinedRadius - dist;

        ApplySpherePush(ref proposedPos, normal, push);
    }

    private void CollideWithThreeDoModel(ref Vector3 proposedPos, JklThing thing, ThreeDoModel model)
    {
        Vector3 sphereCenter = proposedPos + new Vector3(0.0f, 0.0f, PelvisOffset);

        if (model.Nodes.Count == 0 && model.Radius > 0.0f)
        {
            float maxDist = model.Radius + Radius;
            if (Vector3.DistanceSquared(sphereCenter, thing.Position) > maxDist * maxDist)
                return;
        }

        SithMatrix34 thingWorld = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position);

        if (model.Nodes.Count > 0)
        {
            Matrix4x4[] nodeTransforms = ThreeDoHierarchy.ComputeTransforms(model);
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int meshIdx = model.Nodes[i].MeshIndex;
                if (meshIdx < 0 || meshIdx >= model.Meshes.Count)
                    continue;

                if (!Matrix4x4.Invert(nodeTransforms[i], out Matrix4x4 invNode))
                    continue;

                Vector3 thingLocalCenter = InverseTransformPoint(thingWorld, proposedPos + new Vector3(0.0f, 0.0f, PelvisOffset));
                Vector3 meshCenter = Vector3.Transform(thingLocalCenter, invNode);

                if (CollideWithThreeDoMesh(ref proposedPos, model.Meshes[meshIdx], meshCenter, thingWorld, nodeTransforms[i]))
                {
                    // Continue checking remaining meshes with updated proposedPos
                }
            }
        }
        else
        {
            for (int m = 0; m < model.Meshes.Count; m++)
            {
                Vector3 thingLocalCenter = InverseTransformPoint(thingWorld, proposedPos + new Vector3(0.0f, 0.0f, PelvisOffset));
                CollideWithThreeDoMesh(ref proposedPos, model.Meshes[m], thingLocalCenter, thingWorld, Matrix4x4.Identity);
            }
        }
    }

    private bool CollideWithThreeDoMesh(
        ref Vector3 proposedPos,
        ThreeDoMesh mesh,
        Vector3 meshCenter,
        SithMatrix34 thingWorld,
        Matrix4x4 nodeToThing)
    {
        if (mesh.Radius > 0.0f)
        {
            float maxR = mesh.Radius + Radius;
            if (meshCenter.LengthSquared() > maxR * maxR)
                return false;
        }

        bool collided = false;
        bool hasNodeTransform = nodeToThing != Matrix4x4.Identity;

        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            int vertCount = face.VertexIndices.Length;
            if (vertCount < 3)
                continue;

            var faceVerts = new Vector3[vertCount];
            bool valid = true;
            for (int v = 0; v < vertCount; v++)
            {
                int idx = face.VertexIndices[v];
                if (idx < 0 || idx >= mesh.Vertices.Count)
                {
                    valid = false;
                    break;
                }
                faceVerts[v] = mesh.Vertices[idx];
            }
            if (!valid)
                continue;

            Vector3 faceNormal = face.Normal;
            if (faceNormal.LengthSquared() < 0.0001f)
            {
                Vector3 cross = Vector3.Cross(faceVerts[1] - faceVerts[0], faceVerts[2] - faceVerts[0]);
                if (cross.LengthSquared() < 1e-8f)
                    continue;
                faceNormal = Vector3.Normalize(cross);
            }

            float planeDist = Vector3.Dot(meshCenter - faceVerts[0], faceNormal);
            if (planeDist > Radius || planeDist < -0.02f)
                continue;

            Vector3 closest = ClosestPointOnConvexPolygon(meshCenter, faceVerts, faceNormal);
            Vector3 diff = meshCenter - closest;
            float distSq = diff.LengthSquared();

            if (distSq >= Radius * Radius)
                continue;

            Vector3 localNormal;
            float push;
            if (planeDist < 0.0f)
            {
                localNormal = faceNormal;
                push = Radius - planeDist;
            }
            else if (distSq > 1e-8f)
            {
                float dist = MathF.Sqrt(distSq);
                localNormal = diff / dist;
                push = Radius - dist;
            }
            else
            {
                localNormal = faceNormal;
                push = Radius;
            }

            Vector3 thingNormal = hasNodeTransform
                ? Vector3.TransformNormal(localNormal, nodeToThing)
                : localNormal;
            Vector3 worldNormal = thingWorld.TransformNormal(thingNormal);
            if (worldNormal.LengthSquared() < 1e-8f)
                continue;

            worldNormal = Vector3.Normalize(worldNormal);
            // Walkable top faces (worldNormal.Z >= 0.6f) are handled by step 2d floor grounding
            // so the pelvis sphere never pulls the player's feet down into 3DO platforms/elevators.
            if (worldNormal.Z >= 0.6f)
                continue;

            Vector3 prevPos = proposedPos;
            ApplySpherePush(ref proposedPos, worldNormal, push);

            // Update meshCenter in case multiple faces on the same mesh touch the sphere
            Vector3 worldDelta = proposedPos - prevPos;
            if (worldDelta.LengthSquared() > 0.0f)
            {
                Vector3 thingDelta = InverseTransformNormal(thingWorld, worldDelta);
                if (hasNodeTransform && Matrix4x4.Invert(nodeToThing, out Matrix4x4 invNode))
                {
                    thingDelta = Vector3.TransformNormal(thingDelta, invNode);
                }
                meshCenter += thingDelta;
            }

            collided = true;
        }

        return collided;
    }

    private void TryUpdateFloorFromPolygon(
        Vector3 proposedPos,
        Vector3[] floorVerts,
        Vector3 normal,
        float floorSweepCeiling,
        ref float floorZ,
        ref bool foundFloor)
    {
        if (floorVerts.Length < 3 || !IsPointInsideConvexPolygon2D(proposedPos.X, proposedPos.Y, floorVerts, 0.05f))
            return;

        GetPolygonZBounds(floorVerts, out float minFloorZ, out float maxFloorZ);
        if (minFloorZ > floorSweepCeiling)
            return;

        Vector3 v0 = floorVerts[0];
        float planeZ = v0.Z - ((normal.X * (proposedPos.X - v0.X) + normal.Y * (proposedPos.Y - v0.Y)) / normal.Z);
        planeZ = Math.Clamp(planeZ, minFloorZ, maxFloorZ);

        float prevPlaneZ = v0.Z - ((normal.X * (Position.X - v0.X) + normal.Y * (Position.Y - v0.Y)) / normal.Z);
        prevPlaneZ = Math.Clamp(prevPlaneZ, minFloorZ, maxFloorZ);

        if (planeZ > floorZ && MathF.Min(planeZ, prevPlaneZ) <= floorSweepCeiling)
        {
            floorZ = planeZ;
            foundFloor = true;
        }
    }

    private void UpdateFloorFromThreeDoModel(
        Vector3 proposedPos,
        JklThing thing,
        ThreeDoModel model,
        float floorSweepCeiling,
        ref float floorZ,
        ref bool foundFloor)
    {
        if (model.Nodes.Count == 0 && model.Radius > 0.0f)
        {
            float maxR = model.Radius + Radius + 0.05f + MaxStepHeight;
            if (Vector3.DistanceSquared(proposedPos, thing.Position) > maxR * maxR)
                return;
        }

        SithMatrix34 thingWorld = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position);

        if (model.Nodes.Count > 0)
        {
            Matrix4x4[] nodeTransforms = ThreeDoHierarchy.ComputeTransforms(model);
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int meshIdx = model.Nodes[i].MeshIndex;
                if (meshIdx < 0 || meshIdx >= model.Meshes.Count)
                    continue;

                UpdateFloorFromThreeDoMesh(proposedPos, model.Meshes[meshIdx], thingWorld, nodeTransforms[i], floorSweepCeiling, ref floorZ, ref foundFloor);
            }
        }
        else
        {
            for (int m = 0; m < model.Meshes.Count; m++)
            {
                UpdateFloorFromThreeDoMesh(proposedPos, model.Meshes[m], thingWorld, Matrix4x4.Identity, floorSweepCeiling, ref floorZ, ref foundFloor);
            }
        }
    }

    private void UpdateFloorFromThreeDoMesh(
        Vector3 proposedPos,
        ThreeDoMesh mesh,
        SithMatrix34 thingWorld,
        Matrix4x4 nodeToThing,
        float floorSweepCeiling,
        ref float floorZ,
        ref bool foundFloor)
    {
        bool hasNodeTransform = nodeToThing != Matrix4x4.Identity;
        if (mesh.Radius > 0.0f)
        {
            Vector3 thingLocalPos = InverseTransformPoint(thingWorld, proposedPos);
            Vector3 meshLocalPos = hasNodeTransform && Matrix4x4.Invert(nodeToThing, out Matrix4x4 invNode)
                ? Vector3.Transform(thingLocalPos, invNode)
                : thingLocalPos;
            float maxR = mesh.Radius + Radius + 0.05f + MaxStepHeight;
            if (meshLocalPos.LengthSquared() > maxR * maxR)
                return;
        }

        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            int vertCount = face.VertexIndices.Length;
            if (vertCount < 3)
                continue;

            bool valid = true;
            for (int v = 0; v < vertCount; v++)
            {
                int idx = face.VertexIndices[v];
                if (idx < 0 || idx >= mesh.Vertices.Count)
                {
                    valid = false;
                    break;
                }
            }
            if (!valid)
                continue;

            Vector3 faceNormal = face.Normal;
            if (faceNormal.LengthSquared() < 0.0001f)
            {
                Vector3 v0 = mesh.Vertices[face.VertexIndices[0]];
                Vector3 v1 = mesh.Vertices[face.VertexIndices[1]];
                Vector3 v2 = mesh.Vertices[face.VertexIndices[2]];
                Vector3 cross = Vector3.Cross(v1 - v0, v2 - v0);
                if (cross.LengthSquared() < 1e-8f)
                    continue;
                faceNormal = Vector3.Normalize(cross);
            }

            Vector3 thingNormal = hasNodeTransform
                ? Vector3.TransformNormal(faceNormal, nodeToThing)
                : faceNormal;
            Vector3 worldNormal = thingWorld.TransformNormal(thingNormal);
            if (worldNormal.LengthSquared() < 1e-8f)
                continue;

            worldNormal = Vector3.Normalize(worldNormal);
            if (worldNormal.Z < 0.6f)
                continue;

            var worldVerts = new Vector3[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                Vector3 localPt = mesh.Vertices[face.VertexIndices[v]];
                if (hasNodeTransform)
                {
                    localPt = Vector3.Transform(localPt, nodeToThing);
                }
                worldVerts[v] = thingWorld.TransformPoint(localPt);
            }

            TryUpdateFloorFromPolygon(proposedPos, worldVerts, worldNormal, floorSweepCeiling, ref floorZ, ref foundFloor);
        }
    }

    private void ApplySpherePush(ref Vector3 proposedPos, Vector3 worldNormal, float push)
    {
        if (MathF.Abs(worldNormal.Z) < 0.5f)
        {
            Vector2 horiz = new Vector2(worldNormal.X, worldNormal.Y);
            if (horiz.LengthSquared() > 1e-8f)
            {
                PushOutHorizontally(ref proposedPos, Vector2.Normalize(horiz), push);
            }
        }
        else
        {
            proposedPos += worldNormal * push;
            float velInto = Vector3.Dot(Velocity, worldNormal);
            if (velInto < 0.0f)
            {
                Velocity -= worldNormal * velInto;
            }
        }
    }

    private static Vector3 InverseTransformPoint(SithMatrix34 mat, Vector3 worldPoint)
    {
        Vector3 d = worldPoint - mat.Scale;
        return new Vector3(
            Vector3.Dot(d, mat.Rvec),
            Vector3.Dot(d, mat.Lvec),
            Vector3.Dot(d, mat.Uvec));
    }

    private static Vector3 InverseTransformNormal(SithMatrix34 mat, Vector3 worldDir)
    {
        return new Vector3(
            Vector3.Dot(worldDir, mat.Rvec),
            Vector3.Dot(worldDir, mat.Lvec),
            Vector3.Dot(worldDir, mat.Uvec));
    }

    private static string? ResolveTemplateParameter(JklLevel level, string templateName, string key)
    {
        string current = templateName;
        for (int depth = 0; depth < 16 && !string.IsNullOrEmpty(current); depth++)
        {
            if (!level.Templates.TryGetValue(current, out var tpl))
                break;

            if (tpl.Parameters.TryGetValue(key, out var val))
                return val;

            if (string.IsNullOrEmpty(tpl.BasedOn) || tpl.BasedOn.Equals("none", StringComparison.OrdinalIgnoreCase))
                break;

            current = tpl.BasedOn;
        }
        return null;
    }

    private static bool IsTemplateOrBasedOn(JklLevel level, string templateName, string ancestorName)
    {
        string current = templateName;
        for (int depth = 0; depth < 16 && !string.IsNullOrEmpty(current); depth++)
        {
            if (current.Equals(ancestorName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!level.Templates.TryGetValue(current, out var tpl))
                break;

            if (string.IsNullOrEmpty(tpl.BasedOn) || tpl.BasedOn.Equals("none", StringComparison.OrdinalIgnoreCase))
                break;

            current = tpl.BasedOn;
        }
        return false;
    }

    private static float TryParseTemplateFloat(JklLevel level, string templateName, string key)
    {
        string? raw = ResolveTemplateParameter(level, templateName, key);
        if (raw != null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            return parsed;
        return 0.0f;
    }

    /// <summary>
    /// Returns true if the surface is an adjoin portal with the PASSABLE (0x2) flag,
    /// or if the level has no adjoin table (minimal synthetic test levels).
    /// </summary>
    public static bool IsPassablePortal(JklSurface surface, JklLevel level)
    {
        if (!surface.IsPortal)
            return false;

        if (surface.AdjoinIndex >= 0 && surface.AdjoinIndex < level.Adjoins.Count)
            return (level.Adjoins[surface.AdjoinIndex].Flags & 0x2) != 0;

        // Fallback for minimal synthetic tests with empty level.Adjoins
        return true;
    }

    private static Vector3[] GetSurfaceVertices(JklSurface surface, JklLevel level)
    {
        var verts = new Vector3[surface.VertexIndices.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            int idx = surface.VertexIndices[i];
            verts[i] = (idx >= 0 && idx < level.Vertices.Count) ? level.Vertices[idx] : Vector3.Zero;
        }
        return verts;
    }

    /// <summary>
    /// Computes the exact closest point on a 3D convex polygon to point C.
    /// Eliminates phantom collision caused by axis-aligned bounding box clamping.
    /// </summary>
    public static Vector3 ClosestPointOnConvexPolygon(Vector3 C, Vector3[] vertices, Vector3 normal)
    {
        if (vertices.Length == 0) return C;

        Vector3 v0 = vertices[0];
        float distToPlane = Vector3.Dot(C - v0, normal);
        Vector3 proj = C - (distToPlane * normal);

        // Check if proj is inside polygon face (supports both CCW and CW vertex winding relative to normal)
        Vector3 windingNormal = normal;
        int n = vertices.Length;
        if (n >= 3)
        {
            Vector3 edgeCross = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]);
            if (Vector3.Dot(edgeCross, normal) < 0.0f)
            {
                windingNormal = -normal;
            }
        }

        bool inside = true;
        for (int i = 0; i < n; i++)
        {
            Vector3 a = vertices[i];
            Vector3 b = vertices[(i + 1) % n];
            Vector3 edge = b - a;
            Vector3 toProj = proj - a;
            Vector3 cross = Vector3.Cross(edge, toProj);

            if (Vector3.Dot(cross, windingNormal) < -0.0001f)
            {
                inside = false;
                break;
            }
        }

        if (inside)
        {
            return proj;
        }

        // Outside face: find closest point on boundary edges
        float minSqDist = float.MaxValue;
        Vector3 closest = vertices[0];

        for (int i = 0; i < n; i++)
        {
            Vector3 a = vertices[i];
            Vector3 b = vertices[(i + 1) % n];
            Vector3 ab = b - a;
            float lenSq = Vector3.Dot(ab, ab);
            if (lenSq < 0.000001f) continue;

            float t = Math.Clamp(Vector3.Dot(C - a, ab) / lenSq, 0f, 1f);
            Vector3 ptOnSeg = a + (t * ab);
            float sqDist = (C - ptOnSeg).LengthSquared();
            if (sqDist < minSqDist)
            {
                minSqDist = sqDist;
                closest = ptOnSeg;
            }
        }

        return closest;
    }

    private static void GetPolygonZBounds(Vector3[] vertices, out float minZ, out float maxZ)
    {
        minZ = float.MaxValue;
        maxZ = float.MinValue;
        for (int i = 0; i < vertices.Length; i++)
        {
            float z = vertices[i].Z;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
    }

    private static bool IsPointInsideConvexPolygon2D(float px, float py, Vector3[] vertices, float margin)
    {
        if (vertices.Length < 3) return false;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
        }

        if (px < minX - margin || px > maxX + margin ||
            py < minY - margin || py > maxY + margin)
        {
            return false;
        }

        // Exact 2D convex polygon edge check with signed distance tolerance (supports both CW and CCW winding)
        bool hasPositive = false;
        bool hasNegative = false;
        int n = vertices.Length;
        for (int i = 0; i < n; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % n];
            float ex = b.X - a.X;
            float ey = b.Y - a.Y;
            float len = MathF.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6f) continue;

            float signedDist = ((px - a.X) * ey - (py - a.Y) * ex) / len;
            if (signedDist > margin) hasPositive = true;
            if (signedDist < -margin) hasNegative = true;
            if (hasPositive && hasNegative) return false;
        }

        return true;
    }

    /// <summary>
    /// Determines the correct sector for a point, searching current sector, adjoins, and broad-phase.
    /// Follows LucasArts sithSector_FindSectorAtPos.
    /// </summary>
    public static int FindSectorAtPosition(Vector3 pos, int currentSectorIndex, JklLevel level)
    {
        if (currentSectorIndex >= 0 && currentSectorIndex < level.Sectors.Count)
        {
            var curSector = level.Sectors[currentSectorIndex];
            if (IsPointInsideSector(pos, curSector, level))
            {
                return currentSectorIndex;
            }

            // Test immediate adjoining sectors
            foreach (var surface in curSector.Surfaces)
            {
                if (surface.IsPortal && surface.MirrorSector != null)
                {
                    if (IsPointInsideSector(pos, surface.MirrorSector, level))
                    {
                        return surface.MirrorSector.SectorIndex;
                    }
                }
            }

            // Test 2nd-degree adjoining sectors (doorway arches are often only 0.05m thick!)
            foreach (var surface in curSector.Surfaces)
            {
                if (surface.IsPortal && surface.MirrorSector != null)
                {
                    foreach (var s2 in surface.MirrorSector.Surfaces)
                    {
                        if (s2.IsPortal && s2.MirrorSector != null && s2.MirrorSector != curSector)
                        {
                            if (IsPointInsideSector(pos, s2.MirrorSector, level))
                            {
                                return s2.MirrorSector.SectorIndex;
                            }
                        }
                    }
                }
            }
        }

        // Broad-phase search if displaced
        for (int i = 0; i < level.Sectors.Count; i++)
        {
            if (IsPointInsideSector(pos, level.Sectors[i], level))
            {
                return i;
            }
        }

        return currentSectorIndex;
    }

    /// <summary>
    /// Tests if a 3D point is inside a convex sector.
    /// In the Sith Engine, all sectors are convex polyhedra bounded by inward-facing planes.
    /// </summary>
    public static bool IsPointInsideSector(Vector3 pos, JklSector sector, JklLevel level, float epsilon = 0.03f)
    {
        for (int i = 0; i < sector.Surfaces.Count; i++)
        {
            var s = sector.Surfaces[i];
            if (s.VertexIndices.Length < 3) continue;

            int v0Idx = s.VertexIndices[0];
            if (v0Idx < 0 || v0Idx >= level.Vertices.Count) continue;

            Vector3 v0 = level.Vertices[v0Idx];
            float dist = Vector3.Dot(pos - v0, s.Normal);
            if (dist < -epsilon)
            {
                return false;
            }
        }
        return true;
    }
}
