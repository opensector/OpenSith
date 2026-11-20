using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using Silk.NET.OpenGL;

namespace OpenSith.Client;

/// <summary>
/// Pure Silk.NET.OpenGL 2D orthographic HUD renderer displaying the authentic 1997 LucasArts
/// Jedi Knight pixel-art HUD loaded from retail .bm and .sft assets via <see cref="IVirtualFileSystem"/>.
/// </summary>
public sealed class HudRenderer : IDisposable
{
    private const string VertexShaderSource = @"
        #version 330 core
        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec2 aTexCoord;
        uniform mat4 uProjection;
        out vec2 vTexCoord;
        void main()
        {
            vTexCoord = aTexCoord;
            gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
        }
    ";

    private const string FragmentShaderSource = @"
        #version 330 core
        in vec2 vTexCoord;
        uniform sampler2D uTexture;
        uniform vec4 uColor;
        out vec4 FragColor;
        void main()
        {
            FragColor = texture(uTexture, vTexCoord) * uColor;
        }
    ";

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct HudVertex(Vector2 Position, Vector2 TexCoord);

    private sealed record BmFrameTexture(uint TextureHandle, int Width, int Height);
    private sealed record BmSprite(int XPos, int YPos, int Width, int Height, BmFrameTexture[] Frames);
    private sealed record SftAtlas(SftFont Font, uint TextureHandle, int Width, int Height);

    private readonly GL _gl;
    private readonly ShaderProgram _shader;
    private readonly int _uColorLocation;
    private readonly int _uTextureLocation;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _whiteTexture;
    private readonly List<uint> _ownedTextures = new();
    private readonly HudVertex[] _quadVertices = new HudVertex[6];

    private readonly BmSprite? _statusLeft;
    private readonly BmSprite? _statusRight;
    private readonly BmSprite? _stHealth;
    private readonly BmSprite? _stShield;
    private readonly BmSprite? _stFrc;
    private readonly BmSprite? _stBat;
    private readonly BmSprite? _stFieldLite;

    private readonly SftAtlas? _helthNum;
    private readonly SftAtlas? _armorNum;
    private readonly SftAtlas? _amoNums;
    private readonly SftAtlas? _msgFont;

    private bool _disposed;

    public unsafe HudRenderer(GL gl, IVirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(vfs);

        _gl = gl;
        _shader = new ShaderProgram(gl, VertexShaderSource, FragmentShaderSource);
        _uColorLocation = _gl.GetUniformLocation(_shader.Handle, "uColor");
        _uTextureLocation = _gl.GetUniformLocation(_shader.Handle, "uTexture");

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(6 * sizeof(HudVertex)), null, BufferUsageARB.DynamicDraw);

        uint stride = (uint)sizeof(HudVertex);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)sizeof(Vector2));
        _gl.BindVertexArray(0);

        _gl.GetInteger(GetPName.ActiveTexture, out int prevActiveTex);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GetPName.TextureBinding2D, out int prevTex2D);

        try
        {
            _whiteTexture = UploadRgbaTexture(1, 1, [255, 255, 255, 255]);

            CmpPalette? uiPalette = null;
            bool paletteLoaded = false;

            CmpPalette? GetUiPalette()
            {
                if (!paletteLoaded)
                {
                    paletteLoaded = true;
                    if (vfs.FileExists("misc/cmp/uicolormap.cmp"))
                        uiPalette = CmpPalette.Parse(vfs.ReadAllBytes("misc/cmp/uicolormap.cmp"));
                }

                return uiPalette;
            }

            _statusLeft = LoadBm(vfs, "ui/bm/statusLeft16.bm", "ui/bm/statusLeft.bm", GetUiPalette);
            _statusRight = LoadBm(vfs, "ui/bm/statusRight16.bm", "ui/bm/statusRight.bm", GetUiPalette);
            _stHealth = LoadBm(vfs, "ui/bm/stHealth16.bm", "ui/bm/stHealth.bm", GetUiPalette);
            _stShield = LoadBm(vfs, "ui/bm/stShield16.bm", "ui/bm/stShield.bm", GetUiPalette);
            _stFrc = LoadBm(vfs, "ui/bm/stFrc16.bm", "ui/bm/stFrc.bm", GetUiPalette);
            _stBat = LoadBm(vfs, "ui/bm/stBat16.bm", "ui/bm/stBat.bm", GetUiPalette);
            _stFieldLite = LoadBm(vfs, "ui/bm/stFieldLite16.bm", "ui/bm/stFieldLite.bm", GetUiPalette);

            _helthNum = LoadSft(vfs, "ui/sft/HelthNum16.sft", "ui/sft/HelthNum.sft", GetUiPalette);
            _armorNum = LoadSft(vfs, "ui/sft/ArmorNum16.sft", "ui/sft/ArmorNum.sft", GetUiPalette);
            _amoNums = LoadSft(vfs, "ui/sft/AmoNums16.sft", "ui/sft/AmoNums.sft", GetUiPalette);
            _msgFont = LoadSft(vfs, "ui/sft/msgFont16.sft", "ui/sft/msgFont.sft", GetUiPalette);
        }
        finally
        {
            _gl.BindTexture(TextureTarget.Texture2D, (uint)prevTex2D);
            _gl.ActiveTexture((TextureUnit)prevActiveTex);
        }
    }

    /// <summary>
    /// Renders the 2D Jedi Knight HUD overlay while preserving caller OpenGL state.
    /// </summary>
    public void Render(
        int screenWidth,
        int screenHeight,
        float health,
        float maxHealth,
        float shields,
        float maxShields,
        int ammo,
        int selectedWeapon,
        bool isDead,
        string? statusMessage = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (screenWidth <= 0 || screenHeight <= 0)
            return;

        // Snapshot OpenGL state modified by the 2D pass so 3D rendering remains unaffected.
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

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GetPName.TextureBinding2D, out int prevTexture2D);

        try
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.CullFace);
            _gl.DepthMask(false);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            Matrix4x4 ortho = Matrix4x4.CreateOrthographicOffCenter(
                0.0f, screenWidth, screenHeight, 0.0f, -1.0f, 1.0f);

            _shader.Use();
            _shader.SetUniform("uProjection", ortho);
            if (_uTextureLocation >= 0)
                _gl.Uniform1(_uTextureLocation, 0);

            _gl.BindVertexArray(_vao);

            float hudScale = MathF.Max(1.0f, MathF.Round(screenHeight / 480.0f));

            if (isDead)
                DrawSolidRect(0.0f, 0.0f, screenWidth, screenHeight, new Vector4(0.45f, 0.02f, 0.02f, 0.22f));
            else
                DrawCrosshair(screenWidth * 0.5f, screenHeight * 0.5f, hudScale, selectedWeapon);

            DrawBottomLeftStatus(screenHeight, hudScale, health, maxHealth, shields);
            DrawBottomRightStatus(screenWidth, screenHeight, hudScale, ammo, selectedWeapon);
            DrawStatusMessages(screenWidth, hudScale, isDead, statusMessage);
        }
        finally
        {
            _gl.BindVertexArray((uint)prevVao);
            _gl.BindTexture(TextureTarget.Texture2D, (uint)prevTexture2D);
            _gl.ActiveTexture((TextureUnit)prevActiveTexture);
            _gl.UseProgram((uint)prevProgram);
            _gl.BlendFuncSeparate(
                (BlendingFactor)prevSrcRgb,
                (BlendingFactor)prevDstRgb,
                (BlendingFactor)prevSrcAlpha,
                (BlendingFactor)prevDstAlpha);
            _gl.DepthMask(prevDepthMask);

            if (wasDepthTestEnabled)
                _gl.Enable(EnableCap.DepthTest);
            else
                _gl.Disable(EnableCap.DepthTest);

            if (wasBlendEnabled)
                _gl.Enable(EnableCap.Blend);
            else
                _gl.Disable(EnableCap.Blend);

            if (wasCullEnabled)
                _gl.Enable(EnableCap.CullFace);
            else
                _gl.Disable(EnableCap.CullFace);
        }
    }

    private void DrawBottomLeftStatus(
        int screenHeight,
        float hudScale,
        float health,
        float maxHealth,
        float shields)
    {
        if (_statusLeft is null)
            return;

        float leftX = 0.0f;
        float leftY = screenHeight - _statusLeft.Height * hudScale;
        DrawSprite(_statusLeft, 0, leftX, leftY, hudScale);

        if (_stShield is { Frames.Length: > 0 })
        {
            int frameCount = _stShield.Frames.Length;
            int shieldFrame = Math.Clamp((frameCount - 1) * (200 - (int)shields) / 200, 0, frameCount - 1);
            DrawSprite(
                _stShield,
                shieldFrame,
                leftX + _stShield.XPos * hudScale,
                leftY + _stShield.YPos * hudScale,
                hudScale);
        }

        if (_stHealth is { Frames.Length: > 0 })
        {
            int frameCount = _stHealth.Frames.Length;
            int denom = Math.Max(1, (int)maxHealth);
            int healthFrame = Math.Clamp((frameCount - 1) * ((int)maxHealth - (int)health) / denom, 0, frameCount - 1);
            DrawSprite(
                _stHealth,
                healthFrame,
                leftX + _stHealth.XPos * hudScale,
                leftY + _stHealth.YPos * hudScale,
                hudScale);
        }

        if (_armorNum is not null)
        {
            string armorText = $"{Math.Clamp((int)shields, 0, 200):D3}";
            DrawSftText(_armorNum, armorText, leftX + 23.0f * hudScale, leftY + 43.0f * hudScale, hudScale);
        }

        if (_helthNum is not null)
        {
            string healthText = $"{Math.Clamp((int)health, 0, 999):D3}";
            DrawSftText(_helthNum, healthText, leftX + 13.0f * hudScale, leftY + 35.0f * hudScale, hudScale);
        }
    }

    private void DrawBottomRightStatus(
        int screenWidth,
        int screenHeight,
        float hudScale,
        int ammo,
        int selectedWeapon)
    {
        if (_statusRight is null)
            return;

        float rightX = screenWidth - _statusRight.Width * hudScale;
        float rightY = screenHeight - _statusRight.Height * hudScale;
        DrawSprite(_statusRight, 0, rightX, rightY, hudScale);

        if (_stBat is { Frames.Length: > 0 })
        {
            DrawSprite(
                _stBat,
                _stBat.Frames.Length - 1,
                rightX + _stBat.XPos * hudScale,
                rightY + _stBat.YPos * hudScale,
                hudScale);
        }

        if (_stFieldLite is { Frames.Length: > 0 })
        {
            DrawSprite(
                _stFieldLite,
                0,
                rightX + _stFieldLite.XPos * hudScale,
                rightY + _stFieldLite.YPos * hudScale,
                hudScale);
        }

        if (_stFrc is { Frames.Length: > 0 })
        {
            DrawSprite(
                _stFrc,
                0,
                rightX + _stFrc.XPos * hudScale,
                rightY + _stFrc.YPos * hudScale,
                hudScale);
        }

        if (_amoNums is not null)
        {
            string ammoText = selectedWeapon == 1 ? ":::" : $"{Math.Clamp(ammo, 0, 999):D3}";
            DrawSftText(_amoNums, ammoText, rightX + 13.0f * hudScale, rightY + 19.0f * hudScale, hudScale);
        }
    }

    private void DrawStatusMessages(int screenWidth, float hudScale, bool isDead, string? statusMessage)
    {
        if (_msgFont is null)
            return;

        float lineY = 16.0f * hudScale;
        float lineStep = (_msgFont.Font.LineHeight + 2) * hudScale;

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            string[] lines = statusMessage.Split('\n', StringSplitOptions.TrimEntries);
            foreach (string line in lines)
            {
                if (line.Length == 0)
                    continue;

                DrawCenteredSftText(_msgFont, line, screenWidth, lineY, hudScale);
                lineY += lineStep;
            }
        }

        if (isDead && (string.IsNullOrWhiteSpace(statusMessage) ||
                       !statusMessage.Contains("RESPAWN", StringComparison.OrdinalIgnoreCase)))
        {
            DrawCenteredSftText(_msgFont, "PRESS R TO RESPAWN", screenWidth, lineY, hudScale);
        }
    }

    private void DrawCenteredSftText(SftAtlas atlas, string text, int screenWidth, float y, float scale)
    {
        float textW = MeasureSftTextWidth(atlas, text, scale);
        float x = MathF.Floor((screenWidth - textW) * 0.5f);
        DrawSftText(atlas, text, x, y, scale);
    }

    private void DrawCrosshair(float cx, float cy, float hudScale, int selectedWeapon)
    {
        float centerX = MathF.Round(cx);
        float centerY = MathF.Round(cy);
        float pixel = hudScale;
        float armLen = 3.0f * pixel;
        float gap = 2.0f * pixel;

        Vector4 color = selectedWeapon == 1
            ? new Vector4(0.55f, 1.00f, 0.80f, 0.85f)
            : new Vector4(0.95f, 0.96f, 0.85f, 0.85f);
        Vector4 shadow = new Vector4(0.0f, 0.0f, 0.0f, 0.50f);

        _gl.BindTexture(TextureTarget.Texture2D, _whiteTexture);

        // Subtle shadow backing for readability on bright geometry
        SetColor(shadow);
        DrawQuad(centerX - pixel, centerY - gap - armLen - pixel, pixel * 2.0f, armLen + pixel * 2.0f, 0f, 0f, 1f, 1f);
        DrawQuad(centerX - pixel, centerY + gap - pixel, pixel * 2.0f, armLen + pixel * 2.0f, 0f, 0f, 1f, 1f);
        DrawQuad(centerX - gap - armLen - pixel, centerY - pixel, armLen + pixel * 2.0f, pixel * 2.0f, 0f, 0f, 1f, 1f);
        DrawQuad(centerX + gap - pixel, centerY - pixel, armLen + pixel * 2.0f, pixel * 2.0f, 0f, 0f, 1f, 1f);

        // Minimal crisp crosshair ticks + center pixel
        SetColor(color);
        DrawQuad(centerX - pixel * 0.5f, centerY - gap - armLen, pixel, armLen, 0f, 0f, 1f, 1f);
        DrawQuad(centerX - pixel * 0.5f, centerY + gap, pixel, armLen, 0f, 0f, 1f, 1f);
        DrawQuad(centerX - gap - armLen, centerY - pixel * 0.5f, armLen, pixel, 0f, 0f, 1f, 1f);
        DrawQuad(centerX + gap, centerY - pixel * 0.5f, armLen, pixel, 0f, 0f, 1f, 1f);
        DrawQuad(centerX - pixel * 0.5f, centerY - pixel * 0.5f, pixel, pixel, 0f, 0f, 1f, 1f);
    }

    private void DrawSolidRect(float x, float y, float width, float height, Vector4 color)
    {
        _gl.BindTexture(TextureTarget.Texture2D, _whiteTexture);
        SetColor(color);
        DrawQuad(x, y, width, height, 0.0f, 0.0f, 1.0f, 1.0f);
    }

    private void DrawSprite(BmSprite sprite, int frameIndex, float x, float y, float scale)
    {
        if ((uint)frameIndex >= (uint)sprite.Frames.Length)
            return;

        BmFrameTexture frame = sprite.Frames[frameIndex];
        _gl.BindTexture(TextureTarget.Texture2D, frame.TextureHandle);
        SetColor(Vector4.One);
        DrawQuad(x, y, frame.Width * scale, frame.Height * scale, 0.0f, 0.0f, 1.0f, 1.0f);
    }

    private float MeasureSftTextWidth(SftAtlas atlas, string text, float scale) =>
        MeasureSftTextWidth(atlas.Font, text, scale);

    /// <summary>
    /// Measures the pixel width of <paramref name="text"/> rendered with <paramref name="font"/> at <paramref name="scale"/>.
    /// </summary>
    public static float MeasureSftTextWidth(SftFont font, string text, float scale = 1.0f)
    {
        if (string.IsNullOrEmpty(text))
            return 0.0f;

        int totalPx = 0;
        int spacing = font.MarginY;
        int spaceWidth = GetSpaceWidth(font);
        int count = 0;

        foreach (char ch in text)
        {
            int charW;
            if (char.IsWhiteSpace(ch))
                charW = spaceWidth;
            else if (font.TryGetGlyphOrDefault(ch, out SftGlyph glyph))
                charW = glyph.Width;
            else
                continue;

            if (count > 0)
                totalPx += spacing;

            totalPx += charW;
            count++;
        }

        return totalPx * scale;
    }

    private void DrawSftText(SftAtlas atlas, string text, float x, float y, float scale)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _gl.BindTexture(TextureTarget.Texture2D, atlas.TextureHandle);
        SetColor(Vector4.One);

        float cursorX = x;
        float invTexW = 1.0f / atlas.Width;
        int spacing = atlas.Font.MarginY;
        int spaceWidth = GetSpaceWidth(atlas.Font);

        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                cursorX += (spaceWidth + spacing) * scale;
            }
            else if (atlas.Font.TryGetGlyphOrDefault(ch, out SftGlyph glyph))
            {
                float u0 = glyph.XOffset * invTexW;
                float u1 = (glyph.XOffset + glyph.Width) * invTexW;
                DrawQuad(cursorX, y, glyph.Width * scale, glyph.Height * scale, u0, 0.0f, u1, 1.0f);
                cursorX += (glyph.Width + spacing) * scale;
            }
        }
    }

    private static int GetSpaceWidth(SftFont font)
    {
        if (font.TryGetGlyph(' ', out SftGlyph spaceGlyph) && spaceGlyph.Width > 0)
            return spaceGlyph.Width;

        if (font.MarginX > 0)
            return font.MarginX;

        if (font.TryGetGlyph('n', out SftGlyph nGlyph) && nGlyph.Width > 0)
            return Math.Max(4, nGlyph.Width);

        if (font.TryGetGlyph('I', out SftGlyph iGlyph) && iGlyph.Width > 0)
            return Math.Max(4, iGlyph.Width);

        return Math.Max(6, (int)MathF.Round(font.LineHeight * 0.35f));
    }

    private void SetColor(Vector4 color)
    {
        if (_uColorLocation >= 0)
            _gl.Uniform4(_uColorLocation, color.X, color.Y, color.Z, color.W);
    }

    private unsafe void DrawQuad(
        float x,
        float y,
        float width,
        float height,
        float u0,
        float v0,
        float u1,
        float v1)
    {
        if (width <= 0.0f || height <= 0.0f)
            return;

        float x2 = x + width;
        float y2 = y + height;

        _quadVertices[0] = new HudVertex(new Vector2(x, y), new Vector2(u0, v0));
        _quadVertices[1] = new HudVertex(new Vector2(x2, y), new Vector2(u1, v0));
        _quadVertices[2] = new HudVertex(new Vector2(x2, y2), new Vector2(u1, v1));
        _quadVertices[3] = new HudVertex(new Vector2(x, y), new Vector2(u0, v0));
        _quadVertices[4] = new HudVertex(new Vector2(x2, y2), new Vector2(u1, v1));
        _quadVertices[5] = new HudVertex(new Vector2(x, y2), new Vector2(u0, v1));

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (HudVertex* ptr = _quadVertices)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(6 * sizeof(HudVertex)), ptr);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private BmSprite? LoadBm(
        IVirtualFileSystem vfs,
        string path16,
        string path8,
        Func<CmpPalette?> getUiPalette)
    {
        string? resolvedPath = vfs.FileExists(path16)
            ? path16
            : vfs.FileExists(path8)
                ? path8
                : null;

        if (resolvedPath is null)
            return null;

        BmBitmap bm = BmBitmap.Parse(vfs.ReadAllBytes(resolvedPath));
        if (bm.Frames.Count == 0)
            return null;

        CmpPalette? palette = bm.PixelFormat.BitsPerPixel == 8 && !bm.HasEmbeddedPalette
            ? getUiPalette()
            : null;

        var frames = new BmFrameTexture[bm.Frames.Count];
        for (int i = 0; i < bm.Frames.Count; i++)
        {
            BmFrame frame = bm.Frames[i];
            byte[] rgba = frame.ToRgbaByteBuffer(palette);
            uint tex = UploadRgbaTexture(frame.Width, frame.Height, rgba);
            frames[i] = new BmFrameTexture(tex, frame.Width, frame.Height);
        }

        return new BmSprite(bm.XPos, bm.YPos, bm.Width, bm.Height, frames);
    }

    private SftAtlas? LoadSft(
        IVirtualFileSystem vfs,
        string path16,
        string path8,
        Func<CmpPalette?> getUiPalette)
    {
        string? resolvedPath = vfs.FileExists(path16)
            ? path16
            : vfs.FileExists(path8)
                ? path8
                : null;

        if (resolvedPath is null)
            return null;

        SftFont font = SftFont.Parse(vfs.ReadAllBytes(resolvedPath));
        CmpPalette? palette = font.Bitmap.PixelFormat.BitsPerPixel == 8 && !font.Bitmap.HasEmbeddedPalette
            ? getUiPalette()
            : null;

        byte[] rgba = font.ToRgbaByteBuffer(palette);
        uint tex = UploadRgbaTexture(font.Bitmap.Width, font.Bitmap.Height, rgba);
        return new SftAtlas(font, tex, font.Bitmap.Width, font.Bitmap.Height);
    }

    private unsafe uint UploadRgbaTexture(int width, int height, byte[] rgbaData)
    {
        uint handle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, handle);

        fixed (byte* ptr = rgbaData)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                ptr);
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        _ownedTextures.Add(handle);
        return handle;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (uint tex in _ownedTextures)
            _gl.DeleteTexture(tex);
        _ownedTextures.Clear();

        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
