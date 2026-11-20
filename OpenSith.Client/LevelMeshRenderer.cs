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

    private readonly GL _gl;
    private readonly TextureManager _textureManager;
    private readonly List<MaterialBatch> _batches = [];
    private readonly Dictionary<int, SurfaceMeshSlice> _surfaceSlices = new();
    private readonly Dictionary<int, List<SurfaceMeshSlice>> _sectorSlices = new();
    private readonly List<SurfaceMeshSlice> _skySlices = [];
    private readonly List<SurfaceMeshSlice> _scrollingSlices = [];
    private Vertex[] _vertices = [];
    private uint _vao;
    private uint _vbo;
    private uint _ebo;

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
    /// Determines whether a surface renders double-sided (cull face disabled).
    /// </summary>
    public static bool IsDoubleSidedSurface(JklSurface surface) =>
        surface is not null && (surface.IsPortal || (surface.FaceFlags & 0x1) != 0);

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
        public int ElementStart { get; }
        public int ElementCount { get; }

        public MaterialBatch(string materialName, bool doubleSided, bool isTranslucent, bool isSky, int start, int count)
        {
            MaterialName = materialName;
            DoubleSided = doubleSided;
            IsTranslucent = isTranslucent;
            IsSky = isSky;
            ElementStart = start;
            ElementCount = count;
        }
    }

    private sealed class SurfaceMeshSlice
    {
        public int SurfaceIndex { get; }
        public int SectorIndex { get; }
        public bool IsSky { get; }
        public int VertexStart { get; }
        public int VertexCount { get; }
        public Vector2[] BaseTexCoords { get; }
        public float[] BaseIntensities { get; }
        public Vector2 UvVelocity { get; set; }
        public Vector2 UvOffset { get; set; }

        public SurfaceMeshSlice(
            int surfaceIndex,
            int sectorIndex,
            bool isSky,
            int vertexStart,
            int vertexCount,
            Vector2[] baseTexCoords,
            float[] baseIntensities)
        {
            SurfaceIndex = surfaceIndex;
            SectorIndex = sectorIndex;
            IsSky = isSky;
            VertexStart = vertexStart;
            VertexCount = vertexCount;
            BaseTexCoords = baseTexCoords;
            BaseIntensities = baseIntensities;
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

    private readonly record struct BatchKey(string MaterialName, bool DoubleSided, bool IsTranslucent, bool IsSky);

    private sealed class BatchKeyComparer : IEqualityComparer<BatchKey>
    {
        public static readonly BatchKeyComparer Instance = new();

        public bool Equals(BatchKey x, BatchKey y) =>
            x.DoubleSided == y.DoubleSided &&
            x.IsTranslucent == y.IsTranslucent &&
            x.IsSky == y.IsSky &&
            string.Equals(x.MaterialName, y.MaterialName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(BatchKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.MaterialName),
                obj.DoubleSided,
                obj.IsTranslucent,
                obj.IsSky);
    }

    public int BatchCount => _batches.Count;

    public LevelMeshRenderer(GL gl, JklLevel level, TextureManager textureManager)
    {
        _gl = gl;
        _textureManager = textureManager;
        BuildGeometry(level);
    }

    private unsafe void BuildGeometry(JklLevel level)
    {
        var vertices = new List<Vertex>();
        var indicesByBatch = new Dictionary<BatchKey, List<uint>>(BatchKeyComparer.Instance);

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

            // Deduplicate coplanar back-to-back portal surfaces when both sides are drawn
            if (surface.IsPortal && surface.MirrorSurface != null)
            {
                var mirror = surface.MirrorSurface;
                bool mirrorIsDrawn = mirror.GeometryMode != 0 &&
                                     mirror.MaterialIndex >= 0 &&
                                     mirror.VertexIndices.Length >= 3;
                if (mirrorIsDrawn && surface.SurfaceIndex > mirror.SurfaceIndex)
                    continue;
            }

            string matName = (surface.MaterialIndex < level.Materials.Count)
                ? level.Materials[surface.MaterialIndex]
                : string.Empty;

            var texInfo = _textureManager.GetOrCreateTexture(matName);
            float texW = texInfo.Width > 0 ? texInfo.Width : 128.0f;
            float texH = texInfo.Height > 0 ? texInfo.Height : 128.0f;

            bool isSky = IsSkySurface(surface);
            bool doubleSided = IsDoubleSidedSurface(surface);
            bool isTranslucent = IsTranslucentSurface(surface);
            var batchKey = new BatchKey(matName, doubleSided, isTranslucent, isSky);
            if (!indicesByBatch.TryGetValue(batchKey, out var indexList))
            {
                indexList = new List<uint>();
                indicesByBatch[batchKey] = indexList;
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

            float sectorExtraLight = (sectorIdx >= 0 && sectorIdx < level.Sectors.Count)
                ? level.Sectors[sectorIdx].ExtraLight
                : 0.0f;

            int vertexStart = vertices.Count;
            uint baseVertexIndex = (uint)vertexStart;
            var baseTexCoords = new Vector2[nverts];
            var baseIntensities = new float[nverts];

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
                baseIntensities[i] = isSky ? 1.0f : rawIntensity + surface.ExtraLight;

                float intensity = ComputeVertexIntensity(surface, i, sectorExtraLight);
                vertices.Add(new Vertex(pos, surface.Normal, uv, intensity));
            }

            var slice = new SurfaceMeshSlice(
                surface.SurfaceIndex,
                sectorIdx,
                isSky,
                vertexStart,
                nverts,
                baseTexCoords,
                baseIntensities);
            _surfaceSlices[surface.SurfaceIndex] = slice;
            if (isSky)
                _skySlices.Add(slice);
            if (sectorIdx >= 0)
            {
                if (!_sectorSlices.TryGetValue(sectorIdx, out var sectorList))
                {
                    sectorList = [];
                    _sectorSlices[sectorIdx] = sectorList;
                }
                sectorList.Add(slice);
            }

            // Fan triangulation: (0, i, i+1) for i = 1 .. nverts - 2
            for (int i = 1; i < nverts - 1; i++)
            {
                indexList.Add(baseVertexIndex + 0);
                indexList.Add(baseVertexIndex + (uint)i);
                indexList.Add(baseVertexIndex + (uint)(i + 1));
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
                    foreach (var (key, indexList) in indicesByBatch)
                    {
                        if (key.IsTranslucent != translucent || key.IsSky != isSky || key.DoubleSided != doubleSided)
                            continue;

                        int start = allIndices.Count;
                        allIndices.AddRange(indexList);
                        _batches.Add(new MaterialBatch(key.MaterialName, key.DoubleSided, key.IsTranslucent, key.IsSky, start, indexList.Count));
                    }
                }
            }
        }

        _vertices = vertices.ToArray();

        // Upload to GPU (DynamicDraw allows live UV scrolling and sector extra-light updates)
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
                BufferUsageARB.StaticDraw
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
    /// Advances active UV scrolling animations and uploads updated vertex slices to the GPU.
    /// </summary>
    public void Update(float dt)
    {
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

    public void Render(ShaderProgram shader)
    {
        _gl.BindVertexArray(_vao);

        // Enable backface culling for inward-facing sector walls/ceilings/floors so
        // adjacent/stacked sectors never Z-fight through each other's back faces.
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        bool cullingEnabled = true;

        // Pass 1: Opaque sky & world surfaces (rendered before translucent windows)
        // Disable colorkey discard on solid (non-portal) world surfaces so grates like 00fgrte2.mat / 10cgrid1.mat
        // on Adjoin=-1 walls/vents render dark gaps instead of punching holes into the void.
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        shader.SetUniform("uAlpha", 1.0f);
        shader.SetUniform("uAlphaTest", 0.0f);

        foreach (var batch in _batches)
        {
            if (batch.IsTranslucent) continue;
            shader.SetUniform("uAlphaTest", batch.DoubleSided ? 1.0f : 0.0f);
            DrawBatch(batch, shader, ref cullingEnabled);
        }

        // Pass 2: Translucent window/portal surfaces (alpha-blended without depth write)
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        shader.SetUniform("uAlpha", 0.35f);
        shader.SetUniform("uAlphaTest", 1.0f);

        foreach (var batch in _batches)
        {
            if (!batch.IsTranslucent) continue;
            DrawBatch(batch, shader, ref cullingEnabled);
        }

        // Restore render state and double-sided culling for downstream 3DO rendering
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        shader.SetUniform("uAlpha", 1.0f);
        shader.SetUniform("uAlphaTest", 1.0f);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
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

        var texInfo = _textureManager.GetOrCreateTexture(batch.MaterialName);
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
