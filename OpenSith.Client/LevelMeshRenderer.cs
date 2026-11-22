using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSith.Core.World;
using Silk.NET.OpenGL;

namespace OpenSith.Client;

public sealed class LevelMeshRenderer : IDisposable
{
    /// <summary>
    /// Sith surface flag for horizon sky surfaces (<c>SITH_SURFACE_HORIZON_SKY</c>).
    /// </summary>
    public const int SurfaceFlagHorizonSky = 0x200;

    /// <summary>
    /// Sith surface flag for ceiling sky surfaces (<c>SITH_SURFACE_CEILING_SKY</c>).
    /// </summary>
    public const int SurfaceFlagCeilingSky = 0x400;

    /// <summary>
    /// Sith surface flag for scrolling surfaces (<c>SITH_SURFACE_SCROLLING</c>).
    /// </summary>
    public const int SurfaceFlagScrolling = 0x800;

    /// <summary>
    /// Bitmask matching either sky surface flag (<see cref="SurfaceFlagHorizonSky"/> | <see cref="SurfaceFlagCeilingSky"/>).
    /// </summary>
    public const int SurfaceFlagSkyMask = SurfaceFlagHorizonSky | SurfaceFlagCeilingSky;

    /// <summary>
    /// Default cel animation rate (Hz) applied to translucent multi-cel water/liquid MATs (e.g. 8-cel <c>00Twater2.mat</c>).
    /// </summary>
    public const float DefaultTranslucentWaterAnimFps = 6.0f;

    /// <summary>
    /// Alpha multiplier used for translucent surfaces (authentic Sith 50% translucency).
    /// </summary>
    public const float TranslucentPassAlpha = 0.50f;

    private readonly GL _gl;
    private readonly JklLevel _level;
    private readonly TextureManager _textureManager;
    private readonly List<MaterialBatch> _batches = [];
    private readonly Dictionary<int, SurfaceMeshSlice> _surfaceSlices = new();
    private readonly Dictionary<int, List<SurfaceMeshSlice>> _sectorSlices = new();
    private readonly List<SurfaceMeshSlice> _skySlices = [];
    private readonly List<SurfaceMeshSlice> _scrollingSlices = [];
    private readonly Dictionary<int, Vector2> _scrollVelocities = new();
    private readonly Dictionary<string, float> _materialAnimFps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, float> _sectorExtraLights = new();
    private float _animTime;
    private Vertex[] _vertices = [];
    private uint _vao;
    private uint _vbo;
    private uint _ebo;
    private bool _geometryDirty;

    /// <summary>
    /// Returns true when the surface is flagged as a horizon sky (0x200) or ceiling sky (0x400).
    /// </summary>
    public static bool IsSkySurface(JklSurface surface) =>
        surface is not null && (surface.SurfaceFlags & SurfaceFlagSkyMask) != 0;

    /// <summary>
    /// Returns true when the surface carries the Sith scrolling flag (0x800).
    /// </summary>
    public static bool IsScrollingSurface(JklSurface surface) =>
        surface is not null && (surface.SurfaceFlags & SurfaceFlagScrolling) != 0;

    /// <summary>
    /// Determines whether a surface renders in the alpha-blended translucent pass.
    /// Sky surfaces always render in the opaque pass so they appear behind translucent windows.
    /// </summary>
    public static bool IsTranslucentSurface(JklSurface surface) =>
        surface is not null &&
        !IsSkySurface(surface) &&
        ((surface.FaceFlags & 0x2) != 0 || (surface.IsPortal && surface.GeometryMode == 4));

    /// <summary>
    /// Returns true when a portal has a valid drawn mirror surface on the opposite sector side.
    /// When both paired faces exist, each renders single-sided from its own sector with authentic sector lighting and UVs.
    /// </summary>
    public static bool HasDrawnMirror(JklSurface surface) =>
        surface is not null &&
        surface.IsPortal &&
        surface.MirrorSurface != null &&
        surface.MirrorSurface.GeometryMode != 0 &&
        surface.MirrorSurface.MaterialIndex >= 0 &&
        surface.MirrorSurface.VertexIndices.Length >= 3;

    /// <summary>
    /// Determines whether a surface renders double-sided (cull face disabled).
    /// One-sided portals (no drawn mirror) and surfaces with <c>FaceFlags &amp; 0x1</c> render double-sided;
    /// paired back-to-back portals render single-sided per face to avoid Z-fighting while preserving per-side lighting.
    /// </summary>
    public static bool IsDoubleSidedSurface(JklSurface surface) =>
        surface is not null &&
        ((surface.FaceFlags & 0x1) != 0 || (surface.IsPortal && !HasDrawnMirror(surface)));

    /// <summary>
    /// Resolves the active animation cel index for a multi-cel material at <paramref name="elapsedSeconds"/> and <paramref name="fps"/>.
    /// </summary>
    public static int ComputeAnimatedCelIndex(int celCount, float elapsedSeconds, float fps)
    {
        if (celCount <= 1 || fps <= 0.0f)
            return 0;

        int frame = (int)MathF.Floor(MathF.Max(0.0f, elapsedSeconds) * fps);
        return ((frame % celCount) + celCount) % celCount;
    }

    /// <summary>
    /// Returns true when a translucent multi-cel world material (such as 8-cel <c>00Twater2.mat</c>)
    /// should animate automatically without requiring an explicit COG <c>MaterialAnim</c> call.
    /// </summary>
    public static bool ShouldAutoAnimateTranslucentMaterial(string materialName, bool isTranslucent, int celCount) =>
        isTranslucent && celCount >= 4 && !string.IsNullOrWhiteSpace(materialName);

    /// <summary>
    /// Converts a Sith COG <c>SlideWall</c>/<c>SlideSurface</c> velocity vector into normalized UV scroll units per second.
    /// </summary>
    public static Vector2 ComputeSlideWallUvVelocity(Vector2 dir, float speed) =>
        dir * (speed * 0.25f);

    /// <summary>
    /// Computes the effective vertex light intensity for a surface vertex slot, clamped to <c>[0.0f, 1.0f]</c>
    /// to match Sith Engine <c>sithRender</c> light clamping.
    /// Sky surfaces are always unlit at full intensity (<c>1.0f</c>), matching Sith Engine <c>RD_LIGHTMODE_FULLYLIT</c>.
    /// </summary>
    public static float ComputeVertexIntensity(JklSurface surface, int vertexSlot, float sectorExtraLight = 0.0f)
    {
        if (IsSkySurface(surface))
            return 1.0f;

        float baseIntensity = (vertexSlot >= 0 && vertexSlot < surface.VertexIntensities.Length)
            ? surface.VertexIntensities[vertexSlot]
            : 1.0f;

        return Math.Clamp(baseIntensity + surface.ExtraLight + sectorExtraLight, 0.0f, 1.0f);
    }

    /// <summary>
    /// Advances a normalized UV scroll offset by <paramref name="uvVelocity"/> * <paramref name="dt"/>,
    /// wrapping components into (-1, 1) to preserve precision with repeat-wrapped textures.
    /// </summary>
    public static Vector2 AdvanceUvOffset(Vector2 currentOffset, Vector2 uvVelocity, float dt)
    {
        if (dt <= 0.0f)
            return currentOffset;

        return new Vector2(
            (currentOffset.X + uvVelocity.X * dt) % 1.0f,
            (currentOffset.Y + uvVelocity.Y * dt) % 1.0f);
    }

    private readonly struct Vertex
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector2 TexCoord;
        public readonly float Intensity;

        public Vertex(Vector3 pos, Vector3 norm, Vector2 uv, float intensity)
        {
            Position = pos;
            Normal = norm;
            TexCoord = uv;
            Intensity = intensity;
        }
    }

    private sealed class MaterialBatch
    {
        public string MaterialName { get; }
        public bool DoubleSided { get; }
        public bool IsTranslucent { get; }
        public bool IsSky { get; }
        public bool AllowAlphaTest { get; }
        public int ElementStart { get; }
        public int ElementCount { get; }

        public MaterialBatch(string materialName, bool doubleSided, bool isTranslucent, bool isSky, bool allowAlphaTest, int start, int count)
        {
            MaterialName = materialName;
            DoubleSided = doubleSided;
            IsTranslucent = isTranslucent;
            IsSky = isSky;
            AllowAlphaTest = allowAlphaTest;
            ElementStart = start;
            ElementCount = count;
        }
    }

    private sealed class SurfaceMeshSlice
    {
        public int SurfaceIndex { get; }
        public int SectorIndex { get; }
        public bool IsSky { get; }
        public bool DoubleSided { get; }
        public bool IsTranslucent { get; }
        public int VertexStart { get; }
        public int VertexCount { get; }
        public Vector2[] BaseTexCoords { get; }
        public float[] BaseIntensities { get; }
        public float[] RawVertexIntensities { get; }
        public int IndexStart { get; set; }
        public int IndexCount { get; set; }
        public uint[] BaseIndices { get; set; } = [];
        public bool IsVisible { get; set; } = true;
        public Vector2 UvVelocity { get; set; }
        public Vector2 UvOffset { get; set; }

        public SurfaceMeshSlice(
            int surfaceIndex,
            int sectorIndex,
            bool isSky,
            bool doubleSided,
            bool isTranslucent,
            int vertexStart,
            int vertexCount,
            Vector2[] baseTexCoords,
            float[] baseIntensities,
            float[] rawVertexIntensities)
        {
            SurfaceIndex = surfaceIndex;
            SectorIndex = sectorIndex;
            IsSky = isSky;
            DoubleSided = doubleSided;
            IsTranslucent = isTranslucent;
            VertexStart = vertexStart;
            VertexCount = vertexCount;
            BaseTexCoords = baseTexCoords;
            BaseIntensities = baseIntensities;
            RawVertexIntensities = rawVertexIntensities;
        }
    }

    private readonly struct PlacedSurfaceBounds
    {
        public readonly Vector3 Normal;
        public readonly float PlaneDist;
        public readonly Vector3 Min;
        public readonly Vector3 Max;

        public PlacedSurfaceBounds(Vector3 normal, float planeDist, Vector3 min, Vector3 max)
        {
            Normal = normal;
            PlaneDist = planeDist;
            Min = min;
            Max = max;
        }
    }

    private readonly record struct BatchKey(string MaterialName, bool DoubleSided, bool IsTranslucent, bool IsSky, bool AllowAlphaTest);

    private sealed class BatchKeyComparer : IEqualityComparer<BatchKey>
    {
        public static readonly BatchKeyComparer Instance = new();

        public bool Equals(BatchKey x, BatchKey y) =>
            x.DoubleSided == y.DoubleSided &&
            x.IsTranslucent == y.IsTranslucent &&
            x.IsSky == y.IsSky &&
            x.AllowAlphaTest == y.AllowAlphaTest &&
            string.Equals(x.MaterialName, y.MaterialName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(BatchKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.MaterialName),
                obj.DoubleSided,
                obj.IsTranslucent,
                obj.IsSky,
                obj.AllowAlphaTest);
    }

    public int BatchCount => _batches.Count;

    public LevelMeshRenderer(GL gl, JklLevel level, TextureManager textureManager)
    {
        _gl = gl;
        _level = level ?? throw new ArgumentNullException(nameof(level));
        _textureManager = textureManager;
        BuildGeometry(_level);
    }

    private unsafe void BuildGeometry(JklLevel level)
    {
        var vertices = new List<Vertex>();
        var indicesByBatch = new Dictionary<BatchKey, List<uint>>(BatchKeyComparer.Instance);
        var slicesByBatch = new Dictionary<BatchKey, List<(SurfaceMeshSlice Slice, int LocalOffset)>>(BatchKeyComparer.Instance);

        // Map each surface to its parent sector so we can detect same-sector coplanar overlaps and apply sector extra light
        var surfaceToSector = new Dictionary<int, int>();
        foreach (var sector in level.Sectors)
        {
            foreach (var secSurface in sector.Surfaces)
            {
                surfaceToSector[secSurface.SurfaceIndex] = sector.SectorIndex;
            }
        }
        var sectorPlacedSurfaces = new Dictionary<int, List<PlacedSurfaceBounds>>();

        // Group surfaces by material, culling mode, translucency, and sky flag
        foreach (var surface in level.Surfaces)
        {
            // Skip Not Drawn (GeometryMode == 0), untextured, or degenerate surfaces
            if (surface.GeometryMode == 0 || surface.MaterialIndex < 0 || surface.VertexIndices.Length < 3)
                continue;

            string matName = (surface.MaterialIndex < level.Materials.Count)
                ? level.Materials[surface.MaterialIndex]
                : string.Empty;

            var texInfo = _textureManager.GetOrCreateTexture(matName);
            float texW = texInfo.Width > 0 ? texInfo.Width : 128.0f;
            float texH = texInfo.Height > 0 ? texInfo.Height : 128.0f;

            bool isSky = IsSkySurface(surface);
            bool doubleSided = IsDoubleSidedSurface(surface);
            bool isTranslucent = IsTranslucentSurface(surface);
            bool allowAlphaTest = surface.IsPortal || (surface.FaceFlags & 0x1) != 0;
            var batchKey = new BatchKey(matName, doubleSided, isTranslucent, isSky, allowAlphaTest);
            if (!indicesByBatch.TryGetValue(batchKey, out var indexList))
            {
                indexList = new List<uint>();
                indicesByBatch[batchKey] = indexList;
                slicesByBatch[batchKey] = [];
            }

            // Compute bounds and check for coplanar overlapping non-portal overlays in the same sector
            Vector3 normalOffset = Vector3.Zero;
            int nverts = surface.VertexIndices.Length;
            Vector3 minBounds = new Vector3(float.MaxValue);
            Vector3 maxBounds = new Vector3(float.MinValue);
            Vector3 firstPos = Vector3.Zero;

            for (int i = 0; i < nverts; i++)
            {
                int vIdx = surface.VertexIndices[i];
                Vector3 pos = (vIdx >= 0 && vIdx < level.Vertices.Count) ? level.Vertices[vIdx] : Vector3.Zero;
                if (i == 0) firstPos = pos;
                minBounds = Vector3.Min(minBounds, pos);
                maxBounds = Vector3.Max(maxBounds, pos);
            }

            int sectorIdx = surfaceToSector.TryGetValue(surface.SurfaceIndex, out int foundSecIdx) ? foundSecIdx : -1;
            if (!surface.IsPortal && sectorIdx >= 0)
            {
                if (!sectorPlacedSurfaces.TryGetValue(sectorIdx, out var placedList))
                {
                    placedList = new List<PlacedSurfaceBounds>();
                    sectorPlacedSurfaces[sectorIdx] = placedList;
                }

                float planeDist = Vector3.Dot(surface.Normal, firstPos);
                int overlapCount = CountCoplanarOverlaps(placedList, surface.Normal, planeDist, minBounds, maxBounds);
                if (overlapCount > 0)
                {
                    normalOffset = surface.Normal * (0.0005f * overlapCount);
                }
                placedList.Add(new PlacedSurfaceBounds(surface.Normal, planeDist, minBounds, maxBounds));
            }

            float sectorExtraLight = _sectorExtraLights.TryGetValue(sectorIdx, out float overrideSecLight)
                ? overrideSecLight
                : (sectorIdx >= 0 && sectorIdx < level.Sectors.Count ? level.Sectors[sectorIdx].ExtraLight : 0.0f);

            int vertexStart = vertices.Count;
            uint baseVertexIndex = (uint)vertexStart;
            var baseTexCoords = new Vector2[nverts];
            var baseIntensities = new float[nverts];
            var rawVertexIntensities = new float[nverts];

            // Emit vertices for this polygon
            for (int i = 0; i < nverts; i++)
            {
                int vIdx = surface.VertexIndices[i];
                int uvIdx = surface.TextureIndices[i];

                Vector3 pos = (vIdx >= 0 && vIdx < level.Vertices.Count) ? level.Vertices[vIdx] : Vector3.Zero;
                pos += normalOffset;

                Vector2 rawUv = (uvIdx >= 0 && uvIdx < level.TextureVertices.Count)
                    ? level.TextureVertices[uvIdx]
                    : Vector2.Zero;

                // Normalize pixel coordinates using texture dimensions
                Vector2 uv = new Vector2(rawUv.X / texW, rawUv.Y / texH);
                baseTexCoords[i] = uv;

                float rawIntensity = (i < surface.VertexIntensities.Length) ? surface.VertexIntensities[i] : 1.0f;
                rawVertexIntensities[i] = rawIntensity;
                baseIntensities[i] = isSky ? 1.0f : rawIntensity + surface.ExtraLight;

                float intensity = ComputeVertexIntensity(surface, i, sectorExtraLight);
                vertices.Add(new Vertex(pos, surface.Normal, uv, intensity));
            }

            int localIndexOffset = indexList.Count;
            var triIndices = new List<uint>((nverts - 2) * 3);
            // Fan triangulation: (0, i, i+1) for i = 1 .. nverts - 2, enforcing CCW winding along surface.Normal on single-sided surfaces
            for (int i = 1; i < nverts - 1; i++)
            {
                uint a = baseVertexIndex + 0;
                uint b = baseVertexIndex + (uint)i;
                uint c = baseVertexIndex + (uint)(i + 1);
                if (!doubleSided)
                {
                    Vector3 posA = vertices[(int)a].Position;
                    Vector3 posB = vertices[(int)b].Position;
                    Vector3 posC = vertices[(int)c].Position;
                    if (Vector3.Dot(Vector3.Cross(posB - posA, posC - posA), surface.Normal) < 0f)
                        (b, c) = (c, b);
                }
                triIndices.Add(a);
                triIndices.Add(b);
                triIndices.Add(c);
                indexList.Add(a);
                indexList.Add(b);
                indexList.Add(c);
            }

            var slice = new SurfaceMeshSlice(
                surface.SurfaceIndex,
                sectorIdx,
                isSky,
                doubleSided,
                isTranslucent,
                vertexStart,
                nverts,
                baseTexCoords,
                baseIntensities,
                rawVertexIntensities)
            {
                IndexCount = triIndices.Count,
                BaseIndices = triIndices.ToArray(),
                IsVisible = true
            };
            _surfaceSlices[surface.SurfaceIndex] = slice;
            slicesByBatch[batchKey].Add((slice, localIndexOffset));
            if (isSky)
                _skySlices.Add(slice);
            if (_scrollVelocities.TryGetValue(surface.SurfaceIndex, out var scrollVel) && scrollVel != Vector2.Zero)
            {
                slice.UvVelocity = scrollVel;
                _scrollingSlices.Add(slice);
            }
            if (sectorIdx >= 0)
            {
                if (!_sectorSlices.TryGetValue(sectorIdx, out var sectorList))
                {
                    sectorList = [];
                    _sectorSlices[sectorIdx] = sectorList;
                }
                sectorList.Add(slice);
            }
        }

        // Assemble unified index buffer and record batches:
        // Pass 1 (opaque): sky batches first, then normal opaque geometry; single-sided before double-sided.
        // Pass 2 (translucent): alpha-blended windows/portals after all opaque and sky geometry.
        var allIndices = new List<uint>();
        foreach (bool translucent in new[] { false, true })
        {
            foreach (bool isSky in new[] { true, false })
            {
                foreach (bool doubleSided in new[] { false, true })
                {
                    foreach (bool allowAlphaTest in new[] { false, true })
                    {
                        foreach (var (key, indexList) in indicesByBatch)
                        {
                            if (key.IsTranslucent != translucent ||
                                key.IsSky != isSky ||
                                key.DoubleSided != doubleSided ||
                                key.AllowAlphaTest != allowAlphaTest)
                            {
                                continue;
                            }

                            int start = allIndices.Count;
                            allIndices.AddRange(indexList);
                            foreach (var (slice, localOffset) in slicesByBatch[key])
                            {
                                slice.IndexStart = start + localOffset;
                            }
                            _batches.Add(new MaterialBatch(key.MaterialName, key.DoubleSided, key.IsTranslucent, key.IsSky, key.AllowAlphaTest, start, indexList.Count));
                        }
                    }
                }
            }
        }

        _vertices = vertices.ToArray();

        // Upload to GPU (DynamicDraw allows live UV scrolling, sector/surface light updates, and EBO surface toggles)
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);

        // VBO
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (Vertex* vPtr = _vertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(_vertices.Length * sizeof(Vertex)),
                vPtr,
                BufferUsageARB.DynamicDraw
            );
        }

        // EBO
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* iPtr = allIndices.ToArray())
        {
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(allIndices.Count * sizeof(uint)),
                iPtr,
                BufferUsageARB.DynamicDraw
            );
        }

        // Vertex Attributes (Stride: sizeof(Vertex) = 36 bytes)
        uint stride = (uint)sizeof(Vertex);

        // 0: Position (vec3)
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);

        // 1: Normal (vec3)
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);

        // 2: TexCoord (vec2)
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)24);

        // 3: Intensity (float)
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)32);

        _gl.BindVertexArray(0);
    }

    private static int CountCoplanarOverlaps(
        List<PlacedSurfaceBounds> placed,
        Vector3 normal,
        float planeDist,
        Vector3 minBounds,
        Vector3 maxBounds)
    {
        int count = 0;
        const float eps = 0.001f;
        bool checkX = MathF.Abs(normal.X) < 0.9f;
        bool checkY = MathF.Abs(normal.Y) < 0.9f;
        bool checkZ = MathF.Abs(normal.Z) < 0.9f;

        for (int i = 0; i < placed.Count; i++)
        {
            var other = placed[i];
            if (Vector3.Dot(normal, other.Normal) < 0.999f) continue;
            if (MathF.Abs(planeDist - other.PlaneDist) > 0.001f) continue;

            if (checkX && (maxBounds.X <= other.Min.X + eps || other.Max.X <= minBounds.X + eps)) continue;
            if (checkY && (maxBounds.Y <= other.Min.Y + eps || other.Max.Y <= minBounds.Y + eps)) continue;
            if (checkZ && (maxBounds.Z <= other.Min.Z + eps || other.Max.Z <= minBounds.Z + eps)) continue;

            count++;
        }

        return count;
    }

    /// <summary>
    /// Assigns a UV scroll velocity (in normalized texture units per second) to the given surface.
    /// Passing <see cref="Vector2.Zero"/> stops scrolling while preserving the current UV offset.
    /// </summary>
    public void SetSurfaceScroll(int surfaceIndex, Vector2 uvVelocity)
    {
        _scrollVelocities[surfaceIndex] = uvVelocity;
        if (!_surfaceSlices.TryGetValue(surfaceIndex, out var slice))
            return;

        slice.UvVelocity = uvVelocity;
        if (uvVelocity != Vector2.Zero)
        {
            if (!_scrollingSlices.Contains(slice))
                _scrollingSlices.Add(slice);
        }
        else
        {
            _scrollingSlices.Remove(slice);
        }
    }

    /// <summary>
    /// Assigns a uniform UV scroll velocity (in normalized texture units per second) to all sky surfaces.
    /// </summary>
    public void SetSkyScroll(Vector2 uvVelocity)
    {
        foreach (var slice in _skySlices)
        {
            SetSurfaceScroll(slice.SurfaceIndex, uvVelocity);
        }
    }

    /// <summary>
    /// Dynamically toggles or updates a surface's geometry visibility mode (<c>SetFaceGeoMode</c>).
    /// Uses an O(1) EBO slice toggle when the surface is already batched, or marks geometry for rebuild otherwise.
    /// </summary>
    public unsafe void SetSurfaceGeoMode(int surfaceIndex, int geoMode)
    {
        if (surfaceIndex < 0 || surfaceIndex >= _level.Surfaces.Count)
            return;

        var surface = _level.Surfaces[surfaceIndex];
        surface.GeometryMode = geoMode;

        if (!_surfaceSlices.TryGetValue(surfaceIndex, out var slice))
        {
            if (geoMode != 0 && surface.MaterialIndex >= 0 && surface.VertexIndices.Length >= 3)
                _geometryDirty = true;
            else if (surface.IsPortal)
                _geometryDirty = true;
            return;
        }

        bool visible = geoMode != 0;
        if (!visible &&
            surface.IsPortal &&
            surface.MirrorSurface != null &&
            surface.MirrorSurface.GeometryMode != 0 &&
            surface.MirrorSurface.MaterialIndex >= 0)
        {
            _geometryDirty = true;
            return;
        }

        if (visible &&
            (slice.IsTranslucent != IsTranslucentSurface(surface) ||
             slice.DoubleSided != IsDoubleSidedSurface(surface)))
        {
            _geometryDirty = true;
            return;
        }

        if (_geometryDirty || slice.IndexCount <= 0)
            return;

        if (slice.IsVisible == visible)
            return;

        slice.IsVisible = visible;
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        if (visible)
        {
            fixed (uint* idxPtr = slice.BaseIndices)
            {
                _gl.BufferSubData(
                    BufferTargetARB.ElementArrayBuffer,
                    (nint)(slice.IndexStart * sizeof(uint)),
                    (nuint)(slice.IndexCount * sizeof(uint)),
                    idxPtr);
            }
        }
        else
        {
            uint[] zeros = new uint[slice.IndexCount];
            fixed (uint* zeroPtr = zeros)
            {
                _gl.BufferSubData(
                    BufferTargetARB.ElementArrayBuffer,
                    (nint)(slice.IndexStart * sizeof(uint)),
                    (nuint)(slice.IndexCount * sizeof(uint)),
                    zeroPtr);
            }
        }
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Assigns a new material name to <paramref name="surfaceIndex"/> and schedules a GPU batch rebuild.
    /// </summary>
    public void SetSurfaceMaterial(int surfaceIndex, string materialName)
    {
        if (surfaceIndex < 0 || surfaceIndex >= _level.Surfaces.Count || string.IsNullOrWhiteSpace(materialName))
            return;

        int matIdx = -1;
        for (int i = 0; i < _level.Materials.Count; i++)
        {
            if (string.Equals(_level.Materials[i], materialName, StringComparison.OrdinalIgnoreCase))
            {
                matIdx = i;
                break;
            }
        }

        if (matIdx < 0)
        {
            matIdx = _level.Materials.Count;
            _level.Materials.Add(materialName);
        }

        _level.Surfaces[surfaceIndex].MaterialIndex = matIdx;
        _geometryDirty = true;
    }

    /// <summary>
    /// Updates dynamic extra lighting on a single surface (<c>SurfaceLightAnim</c>).
    /// </summary>
    public void SetSurfaceExtraLight(int surfaceIndex, float extraLight)
    {
        if (surfaceIndex < 0 || surfaceIndex >= _level.Surfaces.Count)
            return;

        var surface = _level.Surfaces[surfaceIndex];
        surface.ExtraLight = extraLight;

        if (!_surfaceSlices.TryGetValue(surfaceIndex, out var slice) || slice.IsSky)
            return;

        float sectorExtra = _sectorExtraLights.TryGetValue(slice.SectorIndex, out float secOv)
            ? secOv
            : (slice.SectorIndex >= 0 && slice.SectorIndex < _level.Sectors.Count ? _level.Sectors[slice.SectorIndex].ExtraLight : 0.0f);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        for (int i = 0; i < slice.VertexCount; i++)
        {
            int vIdx = slice.VertexStart + i;
            var current = _vertices[vIdx];
            slice.BaseIntensities[i] = slice.RawVertexIntensities[i] + extraLight;
            float intensity = Math.Clamp(slice.BaseIntensities[i] + sectorExtra, 0.0f, 1.0f);
            _vertices[vIdx] = new Vertex(current.Position, current.Normal, current.TexCoord, intensity);
        }
        UploadVertexRangeBound(slice.VertexStart, slice.VertexCount);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>
    /// Sets or clears the cel animation rate (Hz) for all world surfaces referencing <paramref name="materialName"/>.
    /// </summary>
    public void SetMaterialAnim(string materialName, float fps)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            return;

        if (fps > 0.0f)
            _materialAnimFps[materialName] = fps;
        else
            _materialAnimFps.Remove(materialName);
    }

    /// <summary>
    /// Advances animated world material timers and active UV scrolling animations, uploading updated vertex slices to the GPU.
    /// </summary>
    public void Update(float dt)
    {
        if (_geometryDirty)
            RebuildGeometry();

        if (dt > 0.0f)
            _animTime += dt;

        if (dt <= 0.0f || _scrollingSlices.Count == 0)
            return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        foreach (var slice in _scrollingSlices)
        {
            slice.UvOffset = AdvanceUvOffset(slice.UvOffset, slice.UvVelocity, dt);
            for (int i = 0; i < slice.VertexCount; i++)
            {
                int vIdx = slice.VertexStart + i;
                var current = _vertices[vIdx];
                _vertices[vIdx] = new Vertex(
                    current.Position,
                    current.Normal,
                    slice.BaseTexCoords[i] + slice.UvOffset,
                    current.Intensity);
            }
            UploadVertexRangeBound(slice.VertexStart, slice.VertexCount);
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>
    /// Updates dynamic extra lighting for all non-sky surfaces in the specified sector.
    /// Sky surfaces remain fullbright (<c>1.0f</c>).
    /// </summary>
    public void SetSectorExtraLight(int sectorIndex, float extraLight)
    {
        _sectorExtraLights[sectorIndex] = extraLight;
        if (!_sectorSlices.TryGetValue(sectorIndex, out var slices) || slices.Count == 0)
            return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        foreach (var slice in slices)
        {
            if (slice.IsSky)
                continue;

            for (int i = 0; i < slice.VertexCount; i++)
            {
                int vIdx = slice.VertexStart + i;
                var current = _vertices[vIdx];
                float intensity = Math.Clamp(slice.BaseIntensities[i] + extraLight, 0.0f, 1.0f);
                _vertices[vIdx] = new Vertex(current.Position, current.Normal, current.TexCoord, intensity);
            }
            UploadVertexRangeBound(slice.VertexStart, slice.VertexCount);
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    private void RebuildGeometry()
    {
        _geometryDirty = false;
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_ebo != 0) _gl.DeleteBuffer(_ebo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);

        _batches.Clear();
        _surfaceSlices.Clear();
        _sectorSlices.Clear();
        _skySlices.Clear();
        _scrollingSlices.Clear();

        BuildGeometry(_level);
    }

    private unsafe void UploadVertexRangeBound(int vertexStart, int vertexCount)
    {
        if (vertexCount <= 0)
            return;

        fixed (Vertex* vPtr = &_vertices[vertexStart])
        {
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                (nint)(vertexStart * sizeof(Vertex)),
                (nuint)(vertexCount * sizeof(Vertex)),
                vPtr);
        }
    }

    /// <summary>
    /// Renders Pass 1 (opaque sky and world geometry) before 3D entities and sprites.
    /// </summary>
    public void RenderOpaque(ShaderProgram shader)
    {
        if (_geometryDirty)
            RebuildGeometry();

        _gl.BindVertexArray(_vao);

        // Enable backface culling for inward-facing sector walls/ceilings/floors so
        // adjacent/stacked sectors never Z-fight through each other's back faces.
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        bool cullingEnabled = true;

        // Pass 1: Opaque sky & world surfaces (rendered before entities and translucent portals)
        // Disable colorkey discard on solid (non-portal) world surfaces so grates like 00fgrte2.mat / 10cgrid1.mat
        // on Adjoin=-1 walls/vents render dark gaps instead of punching holes into the void.
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        shader.SetUniform("uAlpha", 1.0f);
        shader.SetUniform("uAlphaTest", 0.0f);

        foreach (var batch in _batches)
        {
            if (batch.IsTranslucent) continue;
            shader.SetUniform("uAlphaTest", batch.AllowAlphaTest ? 1.0f : 0.0f);
            DrawBatch(batch, shader, ref cullingEnabled);
        }

        // Restore default double-sided state for downstream 3DO entity rendering
        shader.SetUniform("uAlpha", 1.0f);
        shader.SetUniform("uAlphaTest", 1.0f);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Renders Pass 2 (translucent water and glass portal surfaces) after opaque world and 3DO entities
    /// so submerged geometry depth-tests cleanly behind the translucent surface.
    /// </summary>
    public void RenderTranslucent(ShaderProgram shader)
    {
        if (_geometryDirty)
            RebuildGeometry();

        _gl.BindVertexArray(_vao);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        bool cullingEnabled = true;

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        shader.SetUniform("uAlpha", TranslucentPassAlpha);
        shader.SetUniform("uAlphaTest", 1.0f);

        foreach (var batch in _batches)
        {
            if (!batch.IsTranslucent) continue;
            DrawBatch(batch, shader, ref cullingEnabled);
        }

        // Restore render state and double-sided culling for downstream rendering
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        shader.SetUniform("uAlpha", 1.0f);
        shader.SetUniform("uAlphaTest", 1.0f);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
    }

    public void Render(ShaderProgram shader)
    {
        RenderOpaque(shader);
        RenderTranslucent(shader);
    }

    private unsafe void DrawBatch(MaterialBatch batch, ShaderProgram shader, ref bool cullingEnabled)
    {
        if (batch.DoubleSided && cullingEnabled)
        {
            _gl.Disable(EnableCap.CullFace);
            cullingEnabled = false;
        }
        else if (!batch.DoubleSided && !cullingEnabled)
        {
            _gl.Enable(EnableCap.CullFace);
            cullingEnabled = true;
        }

        var cels = _textureManager.GetOrCreateTextureCels(batch.MaterialName);
        float animFps = _materialAnimFps.TryGetValue(batch.MaterialName, out float explicitFps)
            ? explicitFps
            : (ShouldAutoAnimateTranslucentMaterial(batch.MaterialName, batch.IsTranslucent, cels.Length)
                ? DefaultTranslucentWaterAnimFps
                : 0.0f);
        int celIdx = ComputeAnimatedCelIndex(cels.Length, _animTime, animFps);
        var texInfo = cels[celIdx];
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texInfo.Handle);
        shader.SetUniform("uTexture", 0);

        _gl.DrawElements(
            PrimitiveType.Triangles,
            (uint)batch.ElementCount,
            DrawElementsType.UnsignedInt,
            (void*)(batch.ElementStart * sizeof(uint))
        );
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
    }
}
