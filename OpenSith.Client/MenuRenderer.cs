using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace OpenSith.Client;

/// <summary>
/// Active screen state for the 1997 Jedi Knight menu renderer.
/// </summary>
public enum MenuScreen
{
    None,
    MainMenu,
    LevelSelect,
    PauseMenu,
    LevelComplete,
    Loading
}

/// <summary>
/// Action kind emitted by <see cref="MenuRenderer"/> input handlers.
/// </summary>
public enum MenuActionKind
{
    None,
    StartNewGame,
    LoadLevel,
    ResumeGame,
    RestartLevel,
    NextLevel,
    Quit
}

/// <summary>
/// Immutable action returned by <see cref="MenuRenderer"/> in response to keyboard or mouse input.
/// </summary>
public readonly record struct MenuAction(MenuActionKind Kind, string? JklFile = null, int Difficulty = 1)
{
    public static MenuAction None => new(MenuActionKind.None);
    public static MenuAction StartNewGame => new(MenuActionKind.StartNewGame);
    public static MenuAction LoadLevel(string jklFile, int difficulty) => new(MenuActionKind.LoadLevel, jklFile, difficulty);
    public static MenuAction ResumeGame => new(MenuActionKind.ResumeGame);
    public static MenuAction RestartLevel => new(MenuActionKind.RestartLevel);
    public static MenuAction NextLevel => new(MenuActionKind.NextLevel);
    public static MenuAction Quit => new(MenuActionKind.Quit);
}

/// <summary>
/// Pure Silk.NET.OpenGL 2D orthographic menu renderer displaying the authentic 1997 LucasArts
/// Jedi Knight UI screens loaded from retail .bm and .sft assets via <see cref="IVirtualFileSystem"/>.
/// </summary>
public sealed class MenuRenderer : IDisposable
{
    public const float VirtualWidth = 640.0f;
    public const float VirtualHeight = 480.0f;
    public const int VisibleLevelRows = 11;

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

    private static readonly string[] DifficultyLabels = ["EASY", "MEDIUM", "HARD"];

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MenuVertex(Vector2 Position, Vector2 TexCoord);

    private sealed record BmFrameTexture(uint TextureHandle, int Width, int Height);
    private sealed record BmSprite(int XPos, int YPos, int Width, int Height, BmFrameTexture[] Frames);
    private sealed record SftAtlas(SftFont Font, uint TextureHandle, int Width, int Height);

    private readonly record struct VirtualRect(float X, float Y, float Width, float Height)
    {
        public bool Contains(float x, float y) =>
            x >= X && x <= X + Width && y >= Y && y <= Y + Height;
    }

    private enum ButtonId
    {
        Resume,
        NewGame,
        SelectLevel,
        RestartLevel,
        MainMenu,
        NextLevel,
        Quit,
        Difficulty,
        Back,
        LaunchLevel
    }

    private readonly record struct MenuButton(ButtonId Id, string Label, VirtualRect Bounds);

    private static readonly VirtualRect LevelListBounds = new(60.0f, 88.0f, 520.0f, VisibleLevelRows * 24.0f);
    private const float LevelRowHeight = 24.0f;

    private readonly GL _gl;
    private readonly ShaderProgram _shader;
    private readonly int _uColorLocation;
    private readonly int _uTextureLocation;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _whiteTexture;
    private readonly List<uint> _ownedTextures = new();
    private readonly MenuVertex[] _quadVertices = new MenuVertex[6];

    private readonly BmSprite? _bkMain;
    private readonly BmSprite? _bkSingle;
    private readonly BmSprite? _bkEsc;
    private readonly BmSprite? _bkTally;
    private readonly BmSprite? _bkLoading;

    private readonly SftAtlas? _largeFontNormal;
    private readonly SftAtlas? _largeFontActive;
    private readonly SftAtlas? _medFontNormal;
    private readonly SftAtlas? _medFontActive;
    private readonly SftAtlas? _smallFont;

    private MenuScreen _currentScreen = MenuScreen.MainMenu;
    private MenuScreen _previousScreen = MenuScreen.MainMenu;
    private IReadOnlyList<EpisodeEntry> _episodes = Array.Empty<EpisodeEntry>();
    private int _selectedButtonIndex;
    private int _selectedEpisodeIndex;
    private int _levelScrollOffset;
    private int _difficulty = 1;
    private ButtonId? _hoveredButton;
    private int _hoveredEpisodeIndex = -1;
    private bool _disposed;

    public MenuScreen CurrentScreen
    {
        get => _currentScreen;
        set
        {
            if (_currentScreen == value)
                return;

            _currentScreen = value;
            _selectedButtonIndex = 0;
            _hoveredButton = null;
            _hoveredEpisodeIndex = -1;
        }
    }

    public bool HasLoadedLevel { get; set; }
    public string LoadingLevelName { get; set; } = string.Empty;
    public string CompletedLevelName { get; set; } = string.Empty;

    public int Difficulty
    {
        get => _difficulty;
        set => _difficulty = Math.Clamp(value, 0, DifficultyLabels.Length - 1);
    }

    public IReadOnlyList<EpisodeEntry> Episodes => _episodes;

    public int SelectedEpisodeIndex
    {
        get => _selectedEpisodeIndex;
        set
        {
            if (_episodes.Count == 0)
            {
                _selectedEpisodeIndex = 0;
                _levelScrollOffset = 0;
                return;
            }

            _selectedEpisodeIndex = Math.Clamp(value, 0, _episodes.Count - 1);
            EnsureEpisodeVisible();
        }
    }

    public unsafe MenuRenderer(GL gl, IVirtualFileSystem vfs)
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
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(6 * sizeof(MenuVertex)), null, BufferUsageARB.DynamicDraw);

        uint stride = (uint)sizeof(MenuVertex);
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

            _bkMain = LoadBm(vfs, "ui/bm/bkmain.bm", GetUiPalette);
            _bkSingle = LoadBm(vfs, "ui/bm/bksingle.bm", GetUiPalette);
            _bkEsc = LoadBm(vfs, "ui/bm/bkesc.bm", GetUiPalette);
            _bkTally = LoadBm(vfs, "ui/bm/bktally.bm", GetUiPalette);
            _bkLoading = LoadBm(vfs, "ui/bm/bkloading.bm", GetUiPalette);

            _largeFontNormal = LoadSft(vfs, GetUiPalette, "ui/sft/large0.sft", "ui/sft/large1.sft", "ui/sft/msgFont16.sft", "ui/sft/msgfont16.sft");
            _largeFontActive = LoadSft(vfs, GetUiPalette, "ui/sft/large1.sft", "ui/sft/large0.sft", "ui/sft/msgFont16.sft", "ui/sft/msgfont16.sft");
            _medFontNormal = LoadSft(vfs, GetUiPalette, "ui/sft/med0.sft", "ui/sft/med1.sft", "ui/sft/msgFont16.sft", "ui/sft/msgfont16.sft");
            _medFontActive = LoadSft(vfs, GetUiPalette, "ui/sft/med1.sft", "ui/sft/med0.sft", "ui/sft/msgFont16.sft", "ui/sft/msgfont16.sft");
            _smallFont = LoadSft(vfs, GetUiPalette, "ui/sft/small0.sft", "ui/sft/msgFont16.sft", "ui/sft/msgfont16.sft");
        }
        finally
        {
            _gl.BindTexture(TextureTarget.Texture2D, (uint)prevTex2D);
            _gl.ActiveTexture((TextureUnit)prevActiveTex);
        }
    }

    /// <summary>
    /// Replaces the campaign level list shown in <see cref="MenuScreen.LevelSelect"/>.
    /// </summary>
    public void SetEpisodes(IReadOnlyList<EpisodeEntry>? episodes)
    {
        _episodes = episodes ?? Array.Empty<EpisodeEntry>();
        _selectedEpisodeIndex = 0;
        _levelScrollOffset = 0;
        _hoveredEpisodeIndex = -1;
    }

    /// <summary>
    /// Converts window pixel coordinates into 640x480 virtual menu coordinates.
    /// </summary>
    public static bool TryWindowToVirtual(float x, float y, int winW, int winH, out Vector2 virtualPos)
    {
        if (winW <= 0 || winH <= 0)
        {
            virtualPos = default;
            return false;
        }

        float scale = MathF.Min(winW / VirtualWidth, winH / VirtualHeight);
        if (scale <= 0.0f)
        {
            virtualPos = default;
            return false;
        }

        float offsetX = (winW - VirtualWidth * scale) * 0.5f;
        float offsetY = (winH - VirtualHeight * scale) * 0.5f;
        float vx = (x - offsetX) / scale;
        float vy = (y - offsetY) / scale;
        virtualPos = new Vector2(vx, vy);

        return vx >= 0.0f && vx <= VirtualWidth && vy >= 0.0f && vy <= VirtualHeight;
    }

    /// <summary>
    /// Processes keyboard navigation and activation for the active menu screen.
    /// </summary>
    public MenuAction HandleKeyDown(Key key)
    {
        if (_currentScreen == MenuScreen.None || _currentScreen == MenuScreen.Loading)
            return MenuAction.None;

        if (_currentScreen == MenuScreen.LevelSelect)
            return HandleLevelSelectKey(key);

        IReadOnlyList<MenuButton> buttons = GetScreenButtons(_currentScreen);
        if (buttons.Count == 0)
            return MenuAction.None;

        if (key == Key.Escape)
        {
            if (_currentScreen == MenuScreen.PauseMenu || (_currentScreen == MenuScreen.MainMenu && HasLoadedLevel))
            {
                CurrentScreen = MenuScreen.None;
                return MenuAction.ResumeGame;
            }

            if (_currentScreen == MenuScreen.LevelComplete)
            {
                CurrentScreen = MenuScreen.MainMenu;
                return MenuAction.None;
            }

            return MenuAction.None;
        }

        if (key is Key.Up or Key.W)
        {
            _selectedButtonIndex = (_selectedButtonIndex - 1 + buttons.Count) % buttons.Count;
            return MenuAction.None;
        }

        if (key is Key.Down or Key.S)
        {
            _selectedButtonIndex = (_selectedButtonIndex + 1) % buttons.Count;
            return MenuAction.None;
        }

        if (key is Key.Enter or Key.KeypadEnter or Key.Space)
        {
            int idx = Math.Clamp(_selectedButtonIndex, 0, buttons.Count - 1);
            return ActivateButton(buttons[idx].Id);
        }

        return MenuAction.None;
    }

    /// <summary>
    /// Updates hover state from window mouse coordinates.
    /// </summary>
    public void HandleMouseMove(float x, float y, int winW, int winH)
    {
        _hoveredButton = null;
        _hoveredEpisodeIndex = -1;

        if (_currentScreen == MenuScreen.None || _currentScreen == MenuScreen.Loading)
            return;

        if (!TryWindowToVirtual(x, y, winW, winH, out Vector2 vPos))
            return;

        if (_currentScreen == MenuScreen.LevelSelect && LevelListBounds.Contains(vPos.X, vPos.Y))
        {
            int row = (int)((vPos.Y - LevelListBounds.Y) / LevelRowHeight);
            int epIdx = _levelScrollOffset + row;
            if (epIdx >= 0 && epIdx < _episodes.Count)
                _hoveredEpisodeIndex = epIdx;
        }

        IReadOnlyList<MenuButton> buttons = GetScreenButtons(_currentScreen);
        for (int i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].Bounds.Contains(vPos.X, vPos.Y))
            {
                _hoveredButton = buttons[i].Id;
                _selectedButtonIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// Processes a mouse click in window pixel coordinates and returns any resulting <see cref="MenuAction"/>.
    /// </summary>
    public MenuAction HandleMouseClick(float x, float y, int winW, int winH)
    {
        if (_currentScreen == MenuScreen.None || _currentScreen == MenuScreen.Loading)
            return MenuAction.None;

        if (!TryWindowToVirtual(x, y, winW, winH, out Vector2 vPos))
            return MenuAction.None;

        if (_currentScreen == MenuScreen.LevelSelect && LevelListBounds.Contains(vPos.X, vPos.Y))
        {
            int row = (int)((vPos.Y - LevelListBounds.Y) / LevelRowHeight);
            int epIdx = _levelScrollOffset + row;
            if (epIdx >= 0 && epIdx < _episodes.Count)
            {
                SelectedEpisodeIndex = epIdx;
                _hoveredEpisodeIndex = epIdx;
            }

            return MenuAction.None;
        }

        IReadOnlyList<MenuButton> buttons = GetScreenButtons(_currentScreen);
        for (int i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].Bounds.Contains(vPos.X, vPos.Y))
            {
                _selectedButtonIndex = i;
                return ActivateButton(buttons[i].Id);
            }
        }

        return MenuAction.None;
    }

    /// <summary>
    /// Renders the active 640x480 menu screen scaled cleanly into the target window while preserving OpenGL state.
    /// </summary>
    public void Render(int screenWidth, int screenHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_currentScreen == MenuScreen.None || screenWidth <= 0 || screenHeight <= 0)
            return;

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

            Matrix4x4 windowOrtho = Matrix4x4.CreateOrthographicOffCenter(
                0.0f, screenWidth, screenHeight, 0.0f, -1.0f, 1.0f);

            _shader.Use();
            if (_uTextureLocation >= 0)
                _gl.Uniform1(_uTextureLocation, 0);

            _gl.BindVertexArray(_vao);

            // Fill letterbox / pillarbox backdrop in window space
            _shader.SetUniform("uProjection", windowOrtho);
            DrawSolidRect(0.0f, 0.0f, screenWidth, screenHeight, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

            // Switch to 640x480 virtual space mapped into the centered 4:3 viewport
            float scale = MathF.Min(screenWidth / VirtualWidth, screenHeight / VirtualHeight);
            float offsetX = (screenWidth - VirtualWidth * scale) * 0.5f;
            float offsetY = (screenHeight - VirtualHeight * scale) * 0.5f;
            Matrix4x4 virtualOrtho = Matrix4x4.CreateOrthographicOffCenter(
                -offsetX / scale,
                (screenWidth - offsetX) / scale,
                (screenHeight - offsetY) / scale,
                -offsetY / scale,
                -1.0f,
                1.0f);
            _shader.SetUniform("uProjection", virtualOrtho);

            switch (_currentScreen)
            {
                case MenuScreen.MainMenu:
                    RenderMainMenu();
                    break;
                case MenuScreen.LevelSelect:
                    RenderLevelSelect();
                    break;
                case MenuScreen.PauseMenu:
                    RenderPauseMenu();
                    break;
                case MenuScreen.LevelComplete:
                    RenderLevelComplete();
                    break;
                case MenuScreen.Loading:
                    RenderLoading();
                    break;
            }
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

    private void RenderMainMenu()
    {
        DrawBackground(_bkMain);
        RenderButtonList(
            GetScreenButtons(MenuScreen.MainMenu),
            _largeFontNormal ?? _medFontNormal,
            _largeFontActive ?? _medFontActive);
    }

    private void RenderPauseMenu()
    {
        DrawBackground(_bkEsc);
        SftAtlas? titleFont = _largeFontActive ?? _largeFontNormal;
        if (titleFont is not null)
            DrawCenteredSftText(titleFont, "PAUSED", 56.0f, Vector4.One);

        RenderButtonList(
            GetScreenButtons(MenuScreen.PauseMenu),
            _medFontNormal ?? _largeFontNormal,
            _medFontActive ?? _largeFontActive);
    }

    private void RenderLevelComplete()
    {
        DrawBackground(_bkTally);

        SftAtlas? titleFont = _largeFontActive ?? _largeFontNormal;
        if (titleFont is not null)
            DrawCenteredSftText(titleFont, "LEVEL COMPLETED", 70.0f, Vector4.One);

        SftAtlas? subFont = _medFontNormal ?? _medFontActive;
        if (subFont is not null && !string.IsNullOrWhiteSpace(CompletedLevelName))
            DrawCenteredSftText(subFont, CompletedLevelName.ToUpperInvariant(), 124.0f, Vector4.One);

        RenderButtonList(
            GetScreenButtons(MenuScreen.LevelComplete),
            _largeFontNormal ?? _medFontNormal,
            _largeFontActive ?? _medFontActive);
    }

    private void RenderLoading()
    {
        DrawBackground(_bkLoading);

        SftAtlas? font = _largeFontActive ?? _largeFontNormal ?? _medFontActive ?? _medFontNormal ?? _smallFont;
        if (font is null)
            return;

        string text = string.IsNullOrWhiteSpace(LoadingLevelName)
            ? "LOADING..."
            : $"LOADING {LoadingLevelName.ToUpperInvariant()}...";

        float y = (VirtualHeight - font.Font.LineHeight) * 0.5f;
        DrawSolidRect(40.0f, y - 10.0f, VirtualWidth - 80.0f, font.Font.LineHeight + 20.0f, new Vector4(0.0f, 0.0f, 0.0f, 0.55f));
        DrawCenteredSftText(font, text, y, Vector4.One);
    }

    private void RenderLevelSelect()
    {
        DrawBackground(_bkSingle);

        SftAtlas? titleFont = _largeFontActive ?? _largeFontNormal;
        if (titleFont is not null)
            DrawCenteredSftText(titleFont, "SELECT LEVEL", 28.0f, Vector4.One);

        // Subtle dark backdrop behind the scrollable level list for legibility
        DrawSolidRect(
            LevelListBounds.X,
            LevelListBounds.Y,
            LevelListBounds.Width,
            LevelListBounds.Height,
            new Vector4(0.0f, 0.0f, 0.0f, 0.55f));

        SftAtlas? listFont = _smallFont ?? _medFontNormal ?? _largeFontNormal;
        if (listFont is not null)
        {
            if (_episodes.Count == 0)
            {
                DrawCenteredSftText(listFont, "NO CAMPAIGN LEVELS FOUND", LevelListBounds.Y + 110.0f, new Vector4(0.75f, 0.75f, 0.75f, 1.0f));
            }
            else
            {
                int end = Math.Min(_episodes.Count, _levelScrollOffset + VisibleLevelRows);
                for (int i = _levelScrollOffset; i < end; i++)
                {
                    int row = i - _levelScrollOffset;
                    float rowY = LevelListBounds.Y + row * LevelRowHeight;
                    bool isSelected = i == _selectedEpisodeIndex;
                    bool isHovered = i == _hoveredEpisodeIndex;

                    if (isSelected)
                    {
                        DrawSolidRect(
                            LevelListBounds.X + 2.0f,
                            rowY + 1.0f,
                            LevelListBounds.Width - 14.0f,
                            LevelRowHeight - 2.0f,
                            new Vector4(0.72f, 0.56f, 0.16f, 0.38f));
                    }
                    else if (isHovered)
                    {
                        DrawSolidRect(
                            LevelListBounds.X + 2.0f,
                            rowY + 1.0f,
                            LevelListBounds.Width - 14.0f,
                            LevelRowHeight - 2.0f,
                            new Vector4(0.45f, 0.42f, 0.25f, 0.22f));
                    }

                    EpisodeEntry entry = _episodes[i];
                    string title = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.FileName : entry.DisplayName;
                    string label = $"{entry.LevelNumber:D2}. {title.ToUpperInvariant()}";
                    Vector4 textColor = isSelected
                        ? new Vector4(1.0f, 0.96f, 0.55f, 1.0f)
                        : isHovered
                            ? new Vector4(0.98f, 0.98f, 0.85f, 1.0f)
                            : new Vector4(0.82f, 0.84f, 0.80f, 1.0f);

                    float textY = rowY + MathF.Max(2.0f, (LevelRowHeight - listFont.Font.LineHeight) * 0.5f);
                    DrawSftText(listFont, label, LevelListBounds.X + 10.0f, textY, textColor);
                }

                // Scrollbar indicator when list exceeds visible rows
                if (_episodes.Count > VisibleLevelRows)
                {
                    float trackX = LevelListBounds.X + LevelListBounds.Width - 8.0f;
                    float trackY = LevelListBounds.Y + 4.0f;
                    float trackH = LevelListBounds.Height - 8.0f;
                    DrawSolidRect(trackX, trackY, 4.0f, trackH, new Vector4(0.2f, 0.2f, 0.2f, 0.55f));

                    float thumbH = MathF.Max(18.0f, trackH * ((float)VisibleLevelRows / _episodes.Count));
                    float maxScroll = Math.Max(1, _episodes.Count - VisibleLevelRows);
                    float thumbY = trackY + (trackH - thumbH) * (_levelScrollOffset / maxScroll);
                    DrawSolidRect(trackX, thumbY, 4.0f, thumbH, new Vector4(0.92f, 0.78f, 0.34f, 0.9f));
                }
            }
        }

        RenderButtonList(
            GetScreenButtons(MenuScreen.LevelSelect),
            _medFontNormal ?? _smallFont ?? _largeFontNormal,
            _medFontActive ?? _smallFont ?? _largeFontActive);
    }

    private void RenderButtonList(IReadOnlyList<MenuButton> buttons, SftAtlas? normalFont, SftAtlas? activeFont)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            MenuButton button = buttons[i];
            bool isActive = _hoveredButton == button.Id || _selectedButtonIndex == i;
            SftAtlas? font = isActive ? (activeFont ?? normalFont) : (normalFont ?? activeFont);

            if (font is null)
                continue;

            float textW = MeasureSftTextWidth(font, button.Label);
            float textX = button.Bounds.X + MathF.Floor((button.Bounds.Width - textW) * 0.5f);
            float textY = button.Bounds.Y + MathF.Floor((button.Bounds.Height - font.Font.LineHeight) * 0.5f);

            if (isActive && font.Font.TryGetGlyph('>', out SftGlyph arrowGlyph))
                DrawSftText(font, ">", textX - arrowGlyph.Width - 8.0f, textY, Vector4.One);

            DrawSftText(font, button.Label, textX, textY, Vector4.One);
        }
    }

    private MenuAction HandleLevelSelectKey(Key key)
    {
        if (key == Key.Escape)
        {
            CurrentScreen = _previousScreen == MenuScreen.PauseMenu ? MenuScreen.PauseMenu : MenuScreen.MainMenu;
            return MenuAction.None;
        }

        if (key is Key.Up or Key.W)
        {
            if (_episodes.Count > 0)
                SelectedEpisodeIndex = (_selectedEpisodeIndex - 1 + _episodes.Count) % _episodes.Count;
            return MenuAction.None;
        }

        if (key is Key.Down or Key.S)
        {
            if (_episodes.Count > 0)
                SelectedEpisodeIndex = (_selectedEpisodeIndex + 1) % _episodes.Count;
            return MenuAction.None;
        }

        if (key == Key.PageUp)
        {
            if (_episodes.Count > 0)
                SelectedEpisodeIndex = Math.Max(0, _selectedEpisodeIndex - VisibleLevelRows);
            return MenuAction.None;
        }

        if (key == Key.PageDown)
        {
            if (_episodes.Count > 0)
                SelectedEpisodeIndex = Math.Min(_episodes.Count - 1, _selectedEpisodeIndex + VisibleLevelRows);
            return MenuAction.None;
        }

        if (key is Key.Left or Key.A)
        {
            _difficulty = (_difficulty - 1 + DifficultyLabels.Length) % DifficultyLabels.Length;
            return MenuAction.None;
        }

        if (key is Key.Right or Key.D)
        {
            _difficulty = (_difficulty + 1) % DifficultyLabels.Length;
            return MenuAction.None;
        }

        if (key is Key.Enter or Key.KeypadEnter)
            return LaunchSelectedLevel();

        return MenuAction.None;
    }

    private IReadOnlyList<MenuButton> GetScreenButtons(MenuScreen screen)
    {
        switch (screen)
        {
            case MenuScreen.MainMenu:
            {
                var list = new List<MenuButton>(4);
                float y = HasLoadedLevel ? 185.0f : 202.0f;
                float step = HasLoadedLevel ? 39.0f : 44.0f;
                float height = HasLoadedLevel ? 38.0f : 42.0f;

                if (HasLoadedLevel)
                {
                    list.Add(new MenuButton(ButtonId.Resume, "RESUME", new VirtualRect(160.0f, y, 320.0f, height)));
                    y += step;
                }

                list.Add(new MenuButton(ButtonId.NewGame, "NEW GAME", new VirtualRect(160.0f, y, 320.0f, height)));
                y += step;
                list.Add(new MenuButton(ButtonId.SelectLevel, "SELECT LEVEL", new VirtualRect(160.0f, y, 320.0f, height)));
                y += step;
                list.Add(new MenuButton(ButtonId.Quit, "QUIT", new VirtualRect(160.0f, y, 320.0f, height)));
                return list;
            }

            case MenuScreen.LevelSelect:
                return
                [
                    new MenuButton(ButtonId.Difficulty, $"DIFFICULTY: {DifficultyLabels[_difficulty]}", new VirtualRect(60.0f, 372.0f, 260.0f, 32.0f)),
                    new MenuButton(ButtonId.Back, "BACK", new VirtualRect(60.0f, 416.0f, 160.0f, 34.0f)),
                    new MenuButton(ButtonId.LaunchLevel, "LAUNCH LEVEL", new VirtualRect(360.0f, 416.0f, 220.0f, 34.0f)),
                ];

            case MenuScreen.PauseMenu:
                return
                [
                    new MenuButton(ButtonId.Resume, "RESUME", new VirtualRect(180.0f, 140.0f, 280.0f, 34.0f)),
                    new MenuButton(ButtonId.RestartLevel, "RESTART LEVEL", new VirtualRect(180.0f, 184.0f, 280.0f, 34.0f)),
                    new MenuButton(ButtonId.SelectLevel, "SELECT LEVEL", new VirtualRect(180.0f, 228.0f, 280.0f, 34.0f)),
                    new MenuButton(ButtonId.MainMenu, "MAIN MENU", new VirtualRect(180.0f, 272.0f, 280.0f, 34.0f)),
                    new MenuButton(ButtonId.Quit, "QUIT", new VirtualRect(180.0f, 316.0f, 280.0f, 34.0f)),
                ];

            case MenuScreen.LevelComplete:
                return
                [
                    new MenuButton(ButtonId.NextLevel, "NEXT LEVEL", new VirtualRect(180.0f, 236.0f, 280.0f, 36.0f)),
                    new MenuButton(ButtonId.MainMenu, "MAIN MENU", new VirtualRect(180.0f, 286.0f, 280.0f, 36.0f)),
                ];

            default:
                return Array.Empty<MenuButton>();
        }
    }

    private MenuAction ActivateButton(ButtonId id)
    {
        switch (id)
        {
            case ButtonId.Resume:
                CurrentScreen = MenuScreen.None;
                return MenuAction.ResumeGame;

            case ButtonId.NewGame:
                return MenuAction.StartNewGame;

            case ButtonId.SelectLevel:
                _previousScreen = _currentScreen;
                CurrentScreen = MenuScreen.LevelSelect;
                return MenuAction.None;

            case ButtonId.RestartLevel:
                return MenuAction.RestartLevel;

            case ButtonId.MainMenu:
                CurrentScreen = MenuScreen.MainMenu;
                return MenuAction.None;

            case ButtonId.NextLevel:
                return MenuAction.NextLevel;

            case ButtonId.Quit:
                return MenuAction.Quit;

            case ButtonId.Difficulty:
                _difficulty = (_difficulty + 1) % DifficultyLabels.Length;
                return MenuAction.None;

            case ButtonId.Back:
                CurrentScreen = _previousScreen == MenuScreen.PauseMenu ? MenuScreen.PauseMenu : MenuScreen.MainMenu;
                return MenuAction.None;

            case ButtonId.LaunchLevel:
                return LaunchSelectedLevel();

            default:
                return MenuAction.None;
        }
    }

    private MenuAction LaunchSelectedLevel()
    {
        if (_episodes.Count == 0)
            return MenuAction.None;

        int idx = Math.Clamp(_selectedEpisodeIndex, 0, _episodes.Count - 1);
        string jklFile = _episodes[idx].FileName;
        if (string.IsNullOrWhiteSpace(jklFile))
            return MenuAction.None;

        return MenuAction.LoadLevel(jklFile, _difficulty);
    }

    private void EnsureEpisodeVisible()
    {
        if (_episodes.Count <= VisibleLevelRows)
        {
            _levelScrollOffset = 0;
            return;
        }

        if (_selectedEpisodeIndex < _levelScrollOffset)
            _levelScrollOffset = _selectedEpisodeIndex;
        else if (_selectedEpisodeIndex >= _levelScrollOffset + VisibleLevelRows)
            _levelScrollOffset = _selectedEpisodeIndex - VisibleLevelRows + 1;

        int maxOffset = Math.Max(0, _episodes.Count - VisibleLevelRows);
        _levelScrollOffset = Math.Clamp(_levelScrollOffset, 0, maxOffset);
    }

    private void DrawBackground(BmSprite? sprite)
    {
        if (sprite is { Frames.Length: > 0 })
        {
            BmFrameTexture frame = sprite.Frames[0];
            _gl.BindTexture(TextureTarget.Texture2D, frame.TextureHandle);
            SetColor(Vector4.One);
            DrawQuad(0.0f, 0.0f, VirtualWidth, VirtualHeight, 0.0f, 0.0f, 1.0f, 1.0f);
            return;
        }

        DrawSolidRect(0.0f, 0.0f, VirtualWidth, VirtualHeight, new Vector4(0.05f, 0.06f, 0.09f, 1.0f));
    }

    private void DrawSolidRect(float x, float y, float width, float height, Vector4 color)
    {
        _gl.BindTexture(TextureTarget.Texture2D, _whiteTexture);
        SetColor(color);
        DrawQuad(x, y, width, height, 0.0f, 0.0f, 1.0f, 1.0f);
    }

    private void DrawCenteredSftText(SftAtlas atlas, string text, float y, Vector4 color)
    {
        float textW = MeasureSftTextWidth(atlas, text);
        float x = MathF.Floor((VirtualWidth - textW) * 0.5f);
        DrawSftText(atlas, text, x, y, color);
    }

    private static float MeasureSftTextWidth(SftAtlas atlas, string text) =>
        MeasureSftTextWidth(atlas.Font, text);

    /// <summary>
    /// Measures the unscaled pixel width of <paramref name="text"/> rendered with <paramref name="font"/>.
    /// </summary>
    public static float MeasureSftTextWidth(SftFont font, string text)
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

        return totalPx;
    }

    private void DrawSftText(SftAtlas atlas, string text, float x, float y, Vector4 color)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _gl.BindTexture(TextureTarget.Texture2D, atlas.TextureHandle);
        SetColor(color);

        float cursorX = x;
        float invTexW = 1.0f / atlas.Width;
        int spacing = atlas.Font.MarginY;
        int spaceWidth = GetSpaceWidth(atlas.Font);

        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                cursorX += spaceWidth + spacing;
            }
            else if (atlas.Font.TryGetGlyphOrDefault(ch, out SftGlyph glyph))
            {
                float u0 = glyph.XOffset * invTexW;
                float u1 = (glyph.XOffset + glyph.Width) * invTexW;
                DrawQuad(cursorX, y, glyph.Width, glyph.Height, u0, 0.0f, u1, 1.0f);
                cursorX += glyph.Width + spacing;
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

        _quadVertices[0] = new MenuVertex(new Vector2(x, y), new Vector2(u0, v0));
        _quadVertices[1] = new MenuVertex(new Vector2(x2, y), new Vector2(u1, v0));
        _quadVertices[2] = new MenuVertex(new Vector2(x2, y2), new Vector2(u1, v1));
        _quadVertices[3] = new MenuVertex(new Vector2(x, y), new Vector2(u0, v0));
        _quadVertices[4] = new MenuVertex(new Vector2(x2, y2), new Vector2(u1, v1));
        _quadVertices[5] = new MenuVertex(new Vector2(x, y2), new Vector2(u0, v1));

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (MenuVertex* ptr = _quadVertices)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(6 * sizeof(MenuVertex)), ptr);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private BmSprite? LoadBm(IVirtualFileSystem vfs, string path, Func<CmpPalette?> getUiPalette)
    {
        if (!vfs.FileExists(path))
            return null;

        BmBitmap bm = BmBitmap.Parse(vfs.ReadAllBytes(path));
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

    private SftAtlas? LoadSft(IVirtualFileSystem vfs, Func<CmpPalette?> getUiPalette, params string[] candidatePaths)
    {
        string? resolvedPath = null;
        foreach (string path in candidatePaths)
        {
            if (vfs.FileExists(path))
            {
                resolvedPath = path;
                break;
            }
        }

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
