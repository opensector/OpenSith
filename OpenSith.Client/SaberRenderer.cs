using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;

namespace OpenSith.Client;

/// <summary>
/// Pure Silk.NET.OpenGL lightsaber blade renderer reproducing Jedi Knight's retail
/// <c>rdPolyline</c> (<c>jkSaber.c</c> / <c>rdPolyline.c</c>) using <c>sabergreen1.mat</c>
/// (32x256 blade edge) and <c>sabergreen0.mat</c> (32x32 color-keyed tip/base caps),
/// with a procedural fallback when no <see cref="TextureManager"/> is attached.
/// </summary>
public sealed class SaberRenderer : IDisposable
{
    private const string TexturedVertexShaderSource = @"
        #version 330 core
        layout (location = 0) in vec3 aPos;
        layout (location = 1) in vec2 aTexCoord;

        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec2 vTexCoord;

        void main()
        {
            vTexCoord = aTexCoord;
            gl_Position = uProjection * uView * vec4(aPos, 1.0);
        }
    ";

    private const string TexturedFragmentShaderSource = @"
        #version 330 core
        in vec2 vTexCoord;

        uniform sampler2D uTexture;
        uniform float uAlphaBoost;

        out vec4 FragColor;

        void main()
        {
            vec4 tex = texture(uTexture, vTexCoord);
            if (tex.a < 0.1)
                discard;

            float alpha = clamp(tex.a * uAlphaBoost, 0.0, 1.0);
            FragColor = vec4(tex.rgb, alpha);
        }
    ";

    private const string ProceduralVertexShaderSource = @"
        #version 330 core
        layout (location = 0) in vec2 aQuad;

        uniform mat4 uView;
        uniform mat4 uProjection;
        uniform vec3 uBasePos;
        uniform vec3 uBladeDir;
        uniform vec3 uSideVec;
        uniform float uHalfWidth;
        uniform float uBladeLength;
        uniform float uBasePad;
        uniform float uTipPad;

        out vec2 vLocalPos;
        out vec2 vQuadCoord;

        void main()
        {
            float yPos = mix(-uBasePad, uBladeLength + uTipPad, aQuad.y);
            float xPos = aQuad.x * uHalfWidth;
            vec3 worldPos = uBasePos + uBladeDir * yPos + uSideVec * xPos;
            vLocalPos = vec2(xPos, yPos);
            vQuadCoord = aQuad;
            gl_Position = uProjection * uView * vec4(worldPos, 1.0);
        }
    ";

    private const string ProceduralFragmentShaderSource = @"
        #version 330 core
        in vec2 vLocalPos;
        in vec2 vQuadCoord;

        uniform vec3 uColor;
        uniform float uBladeLength;
        uniform float uBaseRadius;
        uniform float uTipRadius;
        uniform float uBasePad;
        uniform float uTipPad;
        uniform float uAlphaBoost;
        uniform float uTime;

        out vec4 FragColor;

        void main()
        {
            float clampedY = clamp(vLocalPos.y, 0.0, uBladeLength);
            float t = clampedY / max(uBladeLength, 1e-5);
            float radiusAtY = mix(uBaseRadius, uTipRadius, t);

            vec2 delta = vec2(vLocalPos.x, vLocalPos.y - clampedY);
            float dist = length(delta);
            float d = dist / max(radiusAtY, 1e-5);

            float shimmer = 0.94 + 0.06 * sin(t * 26.0 - uTime * 54.0) * sin(t * 17.0 + uTime * 37.0);
            float core = smoothstep(0.88, 0.36, d);
            float innerHalo = exp(-d * d * 1.15);
            float outerHalo = exp(-d * d * 0.36) * 0.52;
            float halo = (innerHalo * 0.78 + outerHalo * 0.55) * shimmer * uAlphaBoost;

            float sideFade = smoothstep(1.0, 0.80, abs(vQuadCoord.x));
            float baseFade = smoothstep(-uBasePad, -uBasePad * 0.25, vLocalPos.y);
            float tipFade = smoothstep(uBladeLength + uTipPad, uBladeLength + uTipPad * 0.25, vLocalPos.y);
            float edgeFade = sideFade * baseFade * tipFade;

            vec3 rgb = mix(uColor, vec3(1.0), clamp(core * 1.12, 0.0, 1.0));
            float alpha = clamp((core * 1.05 + halo) * edgeFade, 0.0, 1.0);

            if (alpha <= 0.001)
                discard;

            FragColor = vec4(rgb, alpha);
        }
    ";

    // Authentic Jedi Knight saber dimensions from jkPlayer.c:387 / kyle.cog (base_rad=0.0032, tip_rad=0.0018, len=0.12)
    private const float BladeLength = 0.12f;
    private const float BaseRadius = 0.0032f;
    private const float TipRadius = 0.0018f;
    private const float CapDepthEpsilon = 0.001f;
    private const float BaseQuadHalfWidth = 0.0125f;

    private const string DefaultSideMaterialName = "sabergreen1.mat";
    private const string DefaultTipMaterialName = "sabergreen0.mat";

    private readonly GL _gl;
    private readonly ShaderProgram _texturedShader;
    private readonly uint _polyVao;
    private readonly uint _polyVbo;
    private readonly uint _polyEbo;
    private readonly float[] _polyVertices = new float[12 * 5];

    private readonly ShaderProgram _proceduralShader;
    private readonly uint _procVao;
    private readonly uint _procVbo;
    private readonly uint _procEbo;

    private readonly Dictionary<string, uint> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private TextureManager? _textures;
    private bool _disposed;

    public SaberRenderer(GL gl, TextureManager? textures = null)
    {
        _gl = gl;
        _textures = textures;
        _texturedShader = new ShaderProgram(gl, TexturedVertexShaderSource, TexturedFragmentShaderSource);
        (_polyVao, _polyVbo, _polyEbo) = BuildPolylineBuffers();

        _proceduralShader = new ShaderProgram(gl, ProceduralVertexShaderSource, ProceduralFragmentShaderSource);
        (_procVao, _procVbo, _procEbo) = BuildProceduralQuad();
    }

    /// <summary>
    /// Attaches or replaces the <see cref="TextureManager"/> used to load retail
    /// lightsaber <c>.mat</c> textures (e.g., <c>sabergreen1.mat</c> / <c>sabergreen0.mat</c>).
    /// </summary>
    public void SetTextureManager(TextureManager textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _textures = textures;
        _textureCache.Clear();
    }

    /// <summary>
    /// Renders the first-person lightsaber blade with swing arc modulation and additive glow.
    /// </summary>
    public void RenderFirstPersonBlade(
        Matrix4x4 cameraView,
        Matrix4x4 bladeProjection,
        Vector3 bladeBaseWorldPos,
        Vector3 bladeForwardDir,
        Vector3 bladeColor,
        float swingProgress,
        float lengthScale = 1.0f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        float clampedSwing = Math.Clamp(swingProgress, 0.0f, 1.0f);
        float swingArc = MathF.Sin(clampedSwing * MathF.PI);

        RenderBladeInternal(
            cameraView,
            bladeProjection,
            bladeBaseWorldPos,
            bladeForwardDir,
            bladeColor,
            auraScale: 1.0f + 0.35f * swingArc,
            auraAlphaBoost: 1.0f + 0.45f * swingArc,
            lengthScale: lengthScale);
    }

    /// <summary>
    /// Renders a third-person lightsaber blade attached to a character's hand in world space.
    /// </summary>
    public void RenderThirdPersonBlade(
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 handWorldPos,
        Vector3 bladeDir,
        Vector3 bladeColor,
        float lengthScale = 1.0f,
        string sideMat = DefaultSideMaterialName,
        string tipMat = DefaultTipMaterialName,
        float bladeLength = BladeLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        RenderBladeInternal(
            view,
            projection,
            handWorldPos,
            bladeDir,
            bladeColor,
            auraScale: 1.0f,
            auraAlphaBoost: 1.0f,
            lengthScale: lengthScale,
            sideMat: sideMat,
            tipMat: tipMat,
            bladeLength: bladeLength);
    }

    private unsafe void RenderBladeInternal(
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 baseWorldPos,
        Vector3 bladeDir,
        Vector3 bladeColor,
        float auraScale,
        float auraAlphaBoost,
        float lengthScale,
        string sideMat = DefaultSideMaterialName,
        string tipMat = DefaultTipMaterialName,
        float bladeLength = BladeLength)
    {
        // Snapshot OpenGL state modified during the additive billboard pass.
        bool wasDepthTestEnabled = _gl.IsEnabled(EnableCap.DepthTest);
        bool wasBlendEnabled = _gl.IsEnabled(EnableCap.Blend);
        bool wasCullEnabled = _gl.IsEnabled(EnableCap.CullFace);
        _gl.GetBoolean(GetPName.DepthWritemask, out bool prevDepthMask);
        _gl.GetInteger(GetPName.BlendSrcRgb, out int prevSrcRgb);
        _gl.GetInteger(GetPName.BlendDstRgb, out int prevDstRgb);
        _gl.GetInteger(GetPName.BlendSrcAlpha, out int prevSrcAlpha);
        _gl.GetInteger(GetPName.BlendDstAlpha, out int prevDstAlpha);
        _gl.GetInteger(GetPName.CurrentProgram, out int prevProgram);
        _gl.GetInteger(GetPName.VertexArrayBinding, out int prevVao);
        _gl.GetInteger(GetPName.ActiveTexture, out int prevActiveTexture);
        _gl.GetInteger(GetPName.TextureBinding2D, out int prevTexture2D);

        try
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.CullFace);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            _gl.DepthMask(false);

            Vector3 dir = bladeDir.LengthSquared() > 1e-8f
                ? Vector3.Normalize(bladeDir)
                : Vector3.UnitY;

            float bladeLen = bladeLength * Math.Clamp(lengthScale, 0.0f, 1.0f);
            if (TryGetRetailSaberTextures(sideMat, tipMat, out uint sideTex, out uint tipTex))
            {
                RenderTexturedPolyline(view, projection, baseWorldPos, dir, sideTex, tipTex, auraAlphaBoost, bladeLen);
            }
            else
            {
                RenderProceduralFallback(view, projection, baseWorldPos, dir, bladeColor, auraScale, auraAlphaBoost, bladeLen);
            }
        }
        finally
        {
            _gl.BindVertexArray((uint)prevVao);
            _gl.ActiveTexture((TextureUnit)prevActiveTexture);
            _gl.BindTexture(TextureTarget.Texture2D, (uint)prevTexture2D);
            _gl.UseProgram((uint)prevProgram);
            _gl.DepthMask(prevDepthMask);
            _gl.BlendFuncSeparate(
                (BlendingFactor)prevSrcRgb,
                (BlendingFactor)prevDstRgb,
                (BlendingFactor)prevSrcAlpha,
                (BlendingFactor)prevDstAlpha);

            if (wasBlendEnabled)
                _gl.Enable(EnableCap.Blend);
            else
                _gl.Disable(EnableCap.Blend);

            if (wasCullEnabled)
                _gl.Enable(EnableCap.CullFace);
            else
                _gl.Disable(EnableCap.CullFace);

            if (wasDepthTestEnabled)
                _gl.Enable(EnableCap.DepthTest);
            else
                _gl.Disable(EnableCap.DepthTest);
        }
    }

    private bool TryGetRetailSaberTextures(string sideMat, string tipMat, out uint sideTexHandle, out uint tipTexHandle)
    {
        if (_textures == null || string.IsNullOrWhiteSpace(sideMat) || string.IsNullOrWhiteSpace(tipMat))
        {
            sideTexHandle = 0;
            tipTexHandle = 0;
            return false;
        }

        if (!TryGetOrLoadTexture(sideMat, out sideTexHandle) ||
            !TryGetOrLoadTexture(tipMat, out tipTexHandle))
        {
            sideTexHandle = 0;
            tipTexHandle = 0;
            return false;
        }

        return true;
    }

    private bool TryGetOrLoadTexture(string matName, out uint handle)
    {
        if (_textureCache.TryGetValue(matName, out handle))
            return handle != 0;

        if (_textures == null)
        {
            handle = 0;
            return false;
        }

        var tex = _textures.GetOrCreateTexture(matName);
        if (tex.Handle == _textures.DefaultWhiteTexture)
        {
            _textureCache[matName] = 0;
            handle = 0;
            return false;
        }

        _textureCache[matName] = tex.Handle;
        handle = tex.Handle;
        return true;
    }

    private unsafe void RenderTexturedPolyline(
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 baseWorldPos,
        Vector3 dir,
        uint sideTexHandle,
        uint tipTexHandle,
        float alphaBoost,
        float bladeLength)
    {
        GetCameraBasis(view, out Vector3 camRight, out Vector3 camUp, out Vector3 camBack);

        Vector3 tipWorldPos = baseWorldPos + dir * bladeLength;
        Vector3 tipCenter = tipWorldPos + camBack * CapDepthEpsilon;
        Vector3 baseCenter = baseWorldPos + camBack * CapDepthEpsilon;
        Vector3 side = ComputeBillboardSideVector(view, baseWorldPos, dir, bladeLength);

        // Reproduce jkSaber_PolylineRand longitudinal V-scroll along sabergreen1.mat
        float timeSec = (float)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        float vOffset = (timeSec * 4.5f) % 1.0f;

        // Quad 0 (vertices 0..3): Tip cap (sabergreen0.mat)
        WriteQuad(0,
            tipCenter - camRight * TipRadius - camUp * TipRadius, 0f, 0f,
            tipCenter + camRight * TipRadius - camUp * TipRadius, 1f, 0f,
            tipCenter + camRight * TipRadius + camUp * TipRadius, 1f, 1f,
            tipCenter - camRight * TipRadius + camUp * TipRadius, 0f, 1f);

        // Quad 1 (vertices 4..7): Base cap (sabergreen0.mat)
        WriteQuad(4,
            baseCenter - camRight * BaseRadius - camUp * BaseRadius, 0f, 0f,
            baseCenter + camRight * BaseRadius - camUp * BaseRadius, 1f, 0f,
            baseCenter + camRight * BaseRadius + camUp * BaseRadius, 1f, 1f,
            baseCenter - camRight * BaseRadius + camUp * BaseRadius, 0f, 1f);

        // Quad 2 (vertices 8..11): Blade ribbon (sabergreen1.mat)
        WriteQuad(8,
            tipWorldPos + side * TipRadius, 1f, vOffset,
            tipWorldPos - side * TipRadius, 0f, vOffset,
            baseWorldPos - side * BaseRadius, 0f, 1f + vOffset,
            baseWorldPos + side * BaseRadius, 1f, 1f + vOffset);

        _gl.BindVertexArray(_polyVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _polyVbo);
        fixed (float* vPtr = _polyVertices)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(_polyVertices.Length * sizeof(float)), vPtr);
        }

        _texturedShader.Use();
        _texturedShader.SetUniform("uView", view);
        _texturedShader.SetUniform("uProjection", projection);
        _texturedShader.SetUniform("uTexture", 0);
        _texturedShader.SetUniform("uAlphaBoost", alphaBoost);

        _gl.ActiveTexture(TextureUnit.Texture0);

        // Draw tip and base caps with sabergreen0.mat (12 indices)
        _gl.BindTexture(TextureTarget.Texture2D, tipTexHandle);
        _gl.DrawElements(PrimitiveType.Triangles, 12, DrawElementsType.UnsignedShort, (void*)0);

        // Draw blade edge ribbon with sabergreen1.mat (6 indices starting at byte offset 12 * 2)
        _gl.BindTexture(TextureTarget.Texture2D, sideTexHandle);
        _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, (void*)(12 * sizeof(ushort)));
    }

    private unsafe void RenderProceduralFallback(
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 baseWorldPos,
        Vector3 dir,
        Vector3 bladeColor,
        float auraScale,
        float auraAlphaBoost,
        float bladeLength)
    {
        float timeSec = (float)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        float shimmer = MathF.Sin(timeSec * 47.0f) * 0.55f + MathF.Sin(timeSec * 73.0f) * 0.45f;
        float bladeLen = bladeLength * (1.0f + 0.012f * MathF.Sin(timeSec * 61.0f));
        float coreScale = 1.0f + 0.04f * shimmer;
        float halfWidth = BaseQuadHalfWidth * auraScale * (1.0f + 0.06f * shimmer);
        float basePad = halfWidth * 0.75f;
        float tipPad = halfWidth;
        float alphaBoost = auraAlphaBoost * (1.0f + 0.06f * MathF.Sin(timeSec * 89.0f));

        Vector3 side = ComputeBillboardSideVector(view, baseWorldPos, dir, bladeLen);
        Vector3 clampedColor = Vector3.Clamp(bladeColor, Vector3.Zero, Vector3.One);

        _proceduralShader.Use();
        _proceduralShader.SetUniform("uView", view);
        _proceduralShader.SetUniform("uProjection", projection);
        _proceduralShader.SetUniform("uBasePos", baseWorldPos);
        _proceduralShader.SetUniform("uBladeDir", dir);
        _proceduralShader.SetUniform("uSideVec", side);
        _proceduralShader.SetUniform("uHalfWidth", halfWidth);
        _proceduralShader.SetUniform("uBladeLength", bladeLen);
        _proceduralShader.SetUniform("uBaseRadius", BaseRadius * coreScale);
        _proceduralShader.SetUniform("uTipRadius", TipRadius * coreScale);
        _proceduralShader.SetUniform("uBasePad", basePad);
        _proceduralShader.SetUniform("uTipPad", tipPad);
        _proceduralShader.SetUniform("uColor", clampedColor);
        _proceduralShader.SetUniform("uAlphaBoost", alphaBoost);
        _proceduralShader.SetUniform("uTime", timeSec);

        _gl.BindVertexArray(_procVao);
        _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, (void*)0);
    }

    private void WriteQuad(
        int startVertex,
        Vector3 p0, float u0, float v0,
        Vector3 p1, float u1, float v1,
        Vector3 p2, float u2, float v2,
        Vector3 p3, float u3, float v3)
    {
        WriteVertex(startVertex, p0, u0, v0);
        WriteVertex(startVertex + 1, p1, u1, v1);
        WriteVertex(startVertex + 2, p2, u2, v2);
        WriteVertex(startVertex + 3, p3, u3, v3);
    }

    private void WriteVertex(int index, Vector3 pos, float u, float v)
    {
        int o = index * 5;
        _polyVertices[o] = pos.X;
        _polyVertices[o + 1] = pos.Y;
        _polyVertices[o + 2] = pos.Z;
        _polyVertices[o + 3] = u;
        _polyVertices[o + 4] = v;
    }

    private static void GetCameraBasis(Matrix4x4 view, out Vector3 right, out Vector3 up, out Vector3 back)
    {
        if (Matrix4x4.Invert(view, out Matrix4x4 invView))
        {
            right = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
            up = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
            back = Vector3.Normalize(new Vector3(invView.M31, invView.M32, invView.M33));
            return;
        }

        right = Vector3.UnitX;
        up = Vector3.UnitY;
        back = Vector3.UnitZ;
    }

    private static Vector3 ComputeBillboardSideVector(Matrix4x4 view, Vector3 baseWorldPos, Vector3 bladeDir, float bladeLength)
    {
        Vector3 midPoint = baseWorldPos + bladeDir * (bladeLength * 0.5f);
        Vector3 camPos = Matrix4x4.Invert(view, out Matrix4x4 invView)
            ? new Vector3(invView.M41, invView.M42, invView.M43)
            : midPoint - Vector3.UnitY;

        Vector3 toCam = camPos - midPoint;
        toCam = toCam.LengthSquared() > 1e-8f
            ? Vector3.Normalize(toCam)
            : Vector3.UnitY;

        Vector3 side = Vector3.Cross(bladeDir, toCam);
        if (side.LengthSquared() < 1e-6f)
        {
            Vector3 fallback = MathF.Abs(bladeDir.Z) < 0.95f ? Vector3.UnitZ : Vector3.UnitX;
            side = Vector3.Cross(bladeDir, fallback);
        }

        return Vector3.Normalize(side);
    }

    private unsafe (uint Vao, uint Vbo, uint Ebo) BuildPolylineBuffers()
    {
        ushort[] indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            8, 9, 10, 8, 10, 11
        ];

        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        uint ebo = _gl.GenBuffer();

        _gl.BindVertexArray(vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_polyVertices.Length * sizeof(float)), null, BufferUsageARB.DynamicDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (ushort* iPtr = indices)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), iPtr, BufferUsageARB.StaticDraw);
        }

        uint stride = 5 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.BindVertexArray(0);

        return (vao, vbo, ebo);
    }

    private unsafe (uint Vao, uint Vbo, uint Ebo) BuildProceduralQuad()
    {
        Vector2[] vertices =
        [
            new(-1.0f, 0.0f),
            new(1.0f, 0.0f),
            new(1.0f, 1.0f),
            new(-1.0f, 1.0f)
        ];

        ushort[] indices = [0, 1, 2, 0, 2, 3];

        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        uint ebo = _gl.GenBuffer();

        _gl.BindVertexArray(vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (Vector2* vPtr = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(Vector2)), vPtr, BufferUsageARB.StaticDraw);
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (ushort* iPtr = indices)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), iPtr, BufferUsageARB.StaticDraw);
        }

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (uint)sizeof(Vector2), (void*)0);
        _gl.BindVertexArray(0);

        return (vao, vbo, ebo);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gl.DeleteBuffer(_polyVbo);
        _gl.DeleteBuffer(_polyEbo);
        _gl.DeleteVertexArray(_polyVao);
        _texturedShader.Dispose();

        _gl.DeleteBuffer(_procVbo);
        _gl.DeleteBuffer(_procEbo);
        _gl.DeleteVertexArray(_procVao);
        _proceduralShader.Dispose();
    }
}
