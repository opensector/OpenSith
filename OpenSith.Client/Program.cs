using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenSith.Core.Cog;
using OpenSith.Core.Materials;
using OpenSith.Core.Models;
using OpenSith.Core.Physics;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenSith.Client;

public static class Program
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    private static IWindow? _window;
    private static GL? _gl;
    private static IInputContext? _input;

    private static VirtualFileSystem? _vfs;
    private static EpisodeDefinition? _episode;
    private static JklLevel? _level;
    private static CmpPalette? _palette;
    private static readonly Dictionary<string, ThreeDoModel?> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    private static ShaderProgram? _shader;
    private static TextureManager? _textures;
    private static LevelMeshRenderer? _meshRenderer;
    private static ThingMeshRenderer? _thingRenderer;
    private static HudRenderer? _hudRenderer;
    private static SaberRenderer? _saberRenderer;
    private static MenuRenderer? _menuRenderer;
    private static AudioEngine? _audio;
    private static WorldSimulation? _worldSim;
    private static Camera _camera = new();
    private static PlayerPhysics? _player;

    private static string? _startupLevel;
    private static string _currentLevelFile = "01narshadda.jkl";
    private static int _currentDifficulty = 1;

    private static Vector2 _lastMousePos;
    private static bool _firstMouseMove = true;
    private static bool _flyMode = false;
    private static bool _thirdPerson = false;
    private static int _selectedWeapon = 2; // 1 = Lightsaber (sabv.3do), 2 = Bryar (bryv.3do), 3 = StRifle (strv.3do), 4 = Repeater (rptv.3do)
    private static float _weaponAnimTime = 0.0f;
    private static float _weaponFireTimer = 0.0f;
    private static float _weaponFireCooldown = 0.35f;
    private const float SaberIgniteDuration = 0.35f;
    private static float _saberIgniteTimer = 0.0f;
    private static int _saberSlashIndex = 0;
    private static string _activeSaberPovKey = "sabvsnp1.key";
    private static string _activeSaberExtKey = "kysnap1.key";
    private static float _playerMoveBobTime = 0.0f;
    private static bool _playerMoving = false;
    private static bool _mouseLeftDown = false;
    private static readonly bool[] _keys = new bool[512];

    public static void Main(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("         OPENSITH - JEDI KNIGHT ENGINE            ");
        Console.WriteLine("==================================================");

        string res1Path = Path.Combine(SteamPath, "Resource", "Res1hi.gob");
        string res2Path = Path.Combine(SteamPath, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(SteamPath, "Episode", "JK1.GOB");

        if (!File.Exists(res2Path) || !File.Exists(jk1Path))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Retail assets not found at '{SteamPath}'.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("Mounting VFS archives...");
        _vfs = new VirtualFileSystem();
        if (File.Exists(res1Path))
            _vfs.Mount(GobArchive.FromFile(res1Path));
        _vfs.Mount(GobArchive.FromFile(res2Path));
        _vfs.Mount(GobArchive.FromFile(jk1Path));

        if (_vfs.FileExists("episode.jk"))
        {
            _episode = EpisodeDefinition.Parse(_vfs.ReadAllText("episode.jk"));
            Console.WriteLine($"Loaded episode.jk: \"{_episode.Title}\" ({_episode.GetPlayableLevels().Count} playable levels)");
        }

        ParseCliArgs(args);

        // Configure Silk.NET Window
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 720);
        options.Title = "OpenSith - Star Wars Jedi Knight: Dark Forces II";
        options.VSync = false; // Uncapped high-refresh rate!

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;

        Console.WriteLine("\nStarting graphics window:");
        Console.WriteLine("  • WASD: Walk / Move");
        Console.WriteLine("  • Space: Jump (or fly up in fly mode)");
        Console.WriteLine("  • Shift: Sprint");
        Console.WriteLine("  • F: Toggle Fly/Noclip mode");
        Console.WriteLine("  • V / F1: Toggle 1st-person / 3rd-person camera");
        Console.WriteLine("  • E: Activate / open nearby door");
        Console.WriteLine("  • 1 / 2 / 3 / 4: Switch weapon (1=Lightsaber, 2=Bryar Pistol, 3=Stormtrooper Rifle, 4=Repeater)");
        Console.WriteLine("  • F5 / F6 / F7 / F8 / F9: Force Jump / Speed / Pull / Heal / Seeing");
        Console.WriteLine("  • Left Click / Ctrl / Enter: Fire / Swing weapon");
        Console.WriteLine("  • B: Toggle Texture Filtering (Retro Point vs Direct3D Bilinear)");
        Console.WriteLine("  • P / F3: Log current Sector & XYZ position (console + opensith_positions.log)");
        Console.WriteLine("  • Mouse / Arrow Keys: Look around");
        Console.WriteLine("  • Escape: Pause Menu");
        _window.Run();
    }

    private static void ParseCliArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--level", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _startupLevel = args[++i];
            }
            else if (string.Equals(args[i], "--difficulty", StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length &&
                     int.TryParse(args[++i], out int diff))
            {
                _currentDifficulty = Math.Clamp(diff, 0, 2);
            }
        }
    }

    private static void OnLoad()
    {
        _gl = _window!.CreateOpenGL();
        _input = _window!.CreateInput();

        // Enable Depth Testing
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);

        // Ensure double-sided rendering for 3DO geometry
        _gl.Disable(EnableCap.CullFace);

        // Setup Shaders
        string vShader = @"
            #version 330 core
            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec3 aNormal;
            layout (location = 2) in vec2 aTexCoord;
            layout (location = 3) in float aIntensity;

            uniform mat4 uModel;
            uniform mat4 uView;
            uniform mat4 uProjection;

            out vec2 vTexCoord;
            out float vIntensity;

            void main()
            {
                gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
                vTexCoord = aTexCoord;
                vIntensity = aIntensity;
            }
        ";

        string fShader = @"
            #version 330 core
            in vec2 vTexCoord;
            in float vIntensity;

            uniform sampler2D uTexture;
            uniform vec3 uTint;
            uniform float uAlpha;
            uniform float uAlphaTest;

            out vec4 FragColor;

            void main()
            {
                vec4 texColor = texture(uTexture, vTexCoord);
                if (uAlphaTest > 0.5 && texColor.a < 0.1) discard;
                vec3 lighted = texColor.rgb * max(vIntensity, 0.25) + uTint;
                FragColor = vec4(lighted, texColor.a * uAlpha);
            }
        ";

        _shader = new ShaderProgram(_gl, vShader, fShader);
        _hudRenderer = new HudRenderer(_gl, _vfs!);
        _saberRenderer = new SaberRenderer(_gl);
        _menuRenderer = new MenuRenderer(_gl, _vfs!);
        _menuRenderer.SetEpisodes(_episode?.GetPlayableLevels());
        _menuRenderer.Difficulty = _currentDifficulty;

        // Input setup
        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += (k, key, code) =>
            {
                if (_menuRenderer != null && _menuRenderer.CurrentScreen != MenuScreen.None)
                {
                    MenuAction action = _menuRenderer.HandleKeyDown(key);
                    HandleMenuAction(action);
                    return;
                }

                if ((int)key < _keys.Length) _keys[(int)key] = true;
                if (key == Key.Escape)
                {
                    OpenMenu(MenuScreen.PauseMenu);
                    return;
                }
                if (key == Key.B) _textures?.ToggleFiltering();
                if (key == Key.V || key == Key.F1)
                {
                    _thirdPerson = !_thirdPerson;
                    Console.WriteLine($"Camera: {(_thirdPerson ? "3rd-Person" : "1st-Person")}");
                }

                if (_worldSim?.Inventory.IsDead == true)
                {
                    if (key == Key.R && _player != null && _level != null)
                    {
                        var spawn = _level.GetPlayerSpawn();
                        if (spawn != null)
                        {
                            _player.Position = spawn.Position;
                            _player.Velocity = Vector3.Zero;
                            _player.CurrentSectorIndex = spawn.SectorIndex;
                            _camera.Yaw = spawn.PitchYawRoll.Y;
                            _camera.Pitch = 0f;
                        }
                        _worldSim.RespawnPlayer();
                    }
                    return;
                }

                if (key == Key.F) _flyMode = !_flyMode;
                if (key == Key.P || key == Key.F3) LogPlayerPosition();
                if (key == Key.Space && _worldSim?.IsInCutscene == true) _worldSim.SkipActiveCutscene();
                if (key == Key.E && _player != null)
                {
                    _worldSim?.ActivateNearestDoor(_player.Position, _camera.Forward);
                }
                int forceBin = key switch
                {
                    Key.F5 => PlayerInventory.BinForceJump,
                    Key.F6 => PlayerInventory.BinForceSpeed,
                    Key.F7 => PlayerInventory.BinForcePull,
                    Key.F8 => PlayerInventory.BinForceHeal,
                    Key.F9 => PlayerInventory.BinForceSeeing,
                    _ => -1
                };
                if (forceBin >= 0)
                {
                    _worldSim?.ActivateForcePower(forceBin, _camera.Forward);
                }
                if (key == Key.Number1)
                {
                    bool wasSaber = _selectedWeapon == 1;
                    _selectedWeapon = 1;
                    _weaponFireTimer = 0f;
                    _weaponAnimTime = 0f;
                    if (!wasSaber)
                        _saberIgniteTimer = SaberIgniteDuration;
                    _audio?.PlaySoundLocal("ltsaberon01.wav", 0.7f);
                    Console.WriteLine("Weapon: Lightsaber");
                }
                if (key == Key.Number2)
                {
                    _selectedWeapon = 2;
                    _weaponFireTimer = 0f;
                    _weaponAnimTime = 0f;
                    _audio?.PlaySoundLocal("df_bry_ready.wav", 0.7f);
                    Console.WriteLine("Weapon: Bryar Pistol");
                }
                if (key == Key.Number3)
                {
                    _selectedWeapon = 3;
                    _weaponFireTimer = 0f;
                    _weaponAnimTime = 0f;
                    _audio?.PlaySoundLocal("df_rif_ready.wav", 0.7f);
                    Console.WriteLine("Weapon: Stormtrooper Rifle");
                }
                if (key == Key.Number4)
                {
                    _selectedWeapon = 4;
                    _weaponFireTimer = 0f;
                    _weaponAnimTime = 0f;
                    _audio?.PlaySoundLocal("df_bry_ready.wav", 0.7f);
                    Console.WriteLine("Weapon: Imperial Repeater");
                }
                if (key == Key.ControlLeft || key == Key.ControlRight || key == Key.Enter) FireCurrentWeapon();
            };
            keyboard.KeyUp += (k, key, code) =>
            {
                if ((int)key < _keys.Length) _keys[(int)key] = false;
            };
        }

        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += (m, button) =>
            {
                if (_menuRenderer != null && _menuRenderer.CurrentScreen != MenuScreen.None)
                {
                    if (button == MouseButton.Left && _window != null)
                    {
                        MenuAction action = _menuRenderer.HandleMouseClick(
                            m.Position.X,
                            m.Position.Y,
                            _window.Size.X,
                            _window.Size.Y);
                        HandleMenuAction(action);
                    }
                    return;
                }

                if (button == MouseButton.Left)
                {
                    _mouseLeftDown = true;
                    FireCurrentWeapon();
                }
            };

            mouse.MouseUp += (m, button) =>
            {
                if (button == MouseButton.Left)
                    _mouseLeftDown = false;
            };

            mouse.MouseMove += (m, pos) =>
            {
                if (_menuRenderer != null && _menuRenderer.CurrentScreen != MenuScreen.None)
                {
                    if (_window != null)
                        _menuRenderer.HandleMouseMove(pos.X, pos.Y, _window.Size.X, _window.Size.Y);
                    return;
                }

                if (_firstMouseMove)
                {
                    _lastMousePos = pos;
                    _firstMouseMove = false;
                    return;
                }

                float xOffset = (pos.X - _lastMousePos.X) * 0.15f;
                float yOffset = (_lastMousePos.Y - pos.Y) * 0.15f;
                _lastMousePos = pos;

                _camera.Yaw -= xOffset;
                _camera.Pitch = Math.Clamp(_camera.Pitch + yOffset, -89.0f, 89.0f);
            };
        }

        if (!string.IsNullOrWhiteSpace(_startupLevel))
        {
            LoadLevel(_startupLevel, _currentDifficulty);
        }
        else
        {
            _menuRenderer.HasLoadedLevel = false;
            OpenMenu(MenuScreen.MainMenu);
        }
    }

    private static void LoadLevel(string jklFileName, int difficulty)
    {
        if (_vfs == null || _gl == null)
            return;

        string normalizedFile = NormalizeJklFileName(jklFileName);
        string? jklPath = ResolveVfsPath($"jkl/{normalizedFile}") ?? ResolveVfsPath(normalizedFile);
        if (jklPath == null)
        {
            Console.WriteLine($"Error: Level '{normalizedFile}' not found in VFS.");
            return;
        }

        _thingRenderer?.Dispose();
        _thingRenderer = null;
        _meshRenderer?.Dispose();
        _meshRenderer = null;
        _textures?.Dispose();
        _textures = null;
        _audio?.Dispose();
        _audio = null;
        _modelCache.Clear();

        _currentLevelFile = normalizedFile;
        _currentDifficulty = Math.Clamp(difficulty, 0, 2);

        Console.WriteLine($"Loading level: {normalizedFile} (difficulty {_currentDifficulty})...");
        string levelName = Path.GetFileNameWithoutExtension(normalizedFile);
        string jklText = _vfs.ReadAllText(jklPath);
        _level = JklReader.Parse(jklText, levelName);
        Console.WriteLine($"Loaded: {_level.Sectors.Count} sectors, {_level.Surfaces.Count} surfaces, {_level.Vertices.Count} vertices, {_level.TextureVertices.Count} UVs.");

        string? cmpName = _level.ColorMaps.FirstOrDefault();
        string cmpPath = !string.IsNullOrWhiteSpace(cmpName)
            ? ResolveVfsPath($"misc/cmp/{Path.GetFileName(cmpName)}") ?? "misc/cmp/01narsh.cmp"
            : "misc/cmp/01narsh.cmp";
        if (!_vfs.FileExists(cmpPath))
            cmpPath = "misc/cmp/01narsh.cmp";

        Console.WriteLine($"Loading palette ({cmpPath})...");
        _palette = CmpPalette.Parse(_vfs.ReadAllBytes(cmpPath));

        _textures = new TextureManager(_gl, _vfs, _palette);
        _meshRenderer = new LevelMeshRenderer(_gl, _level, _textures);
        _meshRenderer.SetSkyScroll(new Vector2(0.01f, 0.005f));
        _thingRenderer = new ThingMeshRenderer(_gl, _vfs, _textures);
        _saberRenderer?.SetTextureManager(_textures);
        _audio = new AudioEngine(_vfs);

        var spawn = _level.GetPlayerSpawn();
        if (spawn != null)
        {
            _player = new PlayerPhysics(spawn.Position, spawn.SectorIndex);
            _camera.Yaw = spawn.PitchYawRoll.Y;
            _camera.Pitch = 0f;
            _camera.Position = _player.EyePosition;
            Console.WriteLine($"Spawned Kyle Katarn at: {_player.Position} in Sector {_player.CurrentSectorIndex} (Yaw: {_camera.Yaw}°)");
        }
        else
        {
            _player = new PlayerPhysics(Vector3.Zero, 0);
            _camera.Yaw = 0f;
            _camera.Pitch = 0f;
            _camera.Position = _player.EyePosition;
        }

        _player.ConfigureFromModel(GetOrLoadModel("ky.3do"));
        _player.ModelResolver = name => string.IsNullOrEmpty(name) ? null : GetOrLoadModel(name);
        _selectedWeapon = 2;

        _worldSim = new WorldSimulation(_level, _vfs, _audio, _currentDifficulty, runStartupCogs: false);
        _worldSim.OnSectorLightChanged += (sectorId, light) =>
            _meshRenderer?.SetSectorExtraLight(sectorId, light);
        _worldSim.OnSurfaceScroll += (surfaceId, dir, speed) =>
            _meshRenderer?.SetSurfaceScroll(surfaceId, dir * speed);
        _worldSim.OnSurfaceGeoModeChanged += (surfaceId, geoMode) =>
            _meshRenderer?.SetSurfaceGeoMode(surfaceId, geoMode);
        _worldSim.OnSurfaceMaterialChanged += (surfaceId, matName) =>
            _meshRenderer?.SetSurfaceMaterial(surfaceId, matName);
        _worldSim.OnSurfaceLightChanged += (surfaceId, light) =>
            _meshRenderer?.SetSurfaceExtraLight(surfaceId, light);
        _worldSim.OnPlayThingKeyframe += (thingId, key, loop) =>
            _thingRenderer?.SetThingKeyframe(thingId, key, mode: 0, loop: loop);
        _worldSim.OnPlayerTeleported += (pos, pyr, sector) =>
        {
            if (_player != null)
            {
                _player.Position = pos;
                _player.Velocity = Vector3.Zero;
                _player.CurrentSectorIndex = sector;
            }
            _camera.Yaw = pyr.Y;
        };
        _worldSim.OnPlayerWeaponAssigned += binIdx =>
        {
            int mapped = binIdx == 10 ? 1 : binIdx;
            if (mapped is >= 1 and <= 4)
                _selectedWeapon = mapped;
        };
        _worldSim.StartLevel();

        if (string.Equals(normalizedFile, "01narshadda.jkl", StringComparison.OrdinalIgnoreCase))
            _audio.PlayLoopingMusic("01cantinaband02.wav", 0.35f);

        Console.WriteLine($"WorldSimulation ready: {_worldSim.ActiveCogCount} COGs, {_worldSim.FramedThingCount} framed entities, {_worldSim.ActiveNpcCount} AI NPCs.");
        _weaponAnimTime = 0.0f;
        _weaponFireTimer = 0.0f;
        _weaponFireCooldown = 0.35f;
        _saberIgniteTimer = 0.0f;
        _saberSlashIndex = 0;
        _activeSaberPovKey = "sabvsnp1.key";
        _activeSaberExtKey = "kysnap1.key";
        _playerMoveBobTime = 0.0f;
        _playerMoving = false;
        _mouseLeftDown = false;
        Array.Clear(_keys);

        if (_window != null)
            _window.Title = $"OpenSith - {normalizedFile}";

        if (_menuRenderer != null)
        {
            _menuRenderer.HasLoadedLevel = true;
            _menuRenderer.Difficulty = _currentDifficulty;
            _menuRenderer.CurrentScreen = MenuScreen.None;
        }

        SetCursorMode(CursorMode.Raw);
    }

    private static void HandleMenuAction(MenuAction action)
    {
        switch (action.Kind)
        {
            case MenuActionKind.StartNewGame:
                LoadLevel("01narshadda.jkl", action.Difficulty);
                break;

            case MenuActionKind.LoadLevel:
                if (!string.IsNullOrWhiteSpace(action.JklFile))
                    LoadLevel(action.JklFile, action.Difficulty);
                break;

            case MenuActionKind.ResumeGame:
                if (_menuRenderer != null)
                    _menuRenderer.CurrentScreen = MenuScreen.None;
                SetCursorMode(CursorMode.Raw);
                break;

            case MenuActionKind.RestartLevel:
                if (!string.IsNullOrWhiteSpace(_currentLevelFile))
                    LoadLevel(_currentLevelFile, _currentDifficulty);
                break;

            case MenuActionKind.NextLevel:
            {
                EpisodeEntry? next = _episode?.GetNextLevelAfter(_currentLevelFile);
                if (next != null)
                    LoadLevel(next.FileName, _currentDifficulty);
                else
                    OpenMenu(MenuScreen.MainMenu);
                break;
            }

            case MenuActionKind.Quit:
                _window?.Close();
                break;
        }
    }

    private static void OpenMenu(MenuScreen screen)
    {
        if (_menuRenderer != null)
            _menuRenderer.CurrentScreen = screen;

        _mouseLeftDown = false;
        Array.Clear(_keys);
        SetCursorMode(CursorMode.Normal);
    }

    private static void SetCursorMode(CursorMode mode)
    {
        if (_input == null)
            return;

        foreach (var mouse in _input.Mice)
        {
            if (mouse.Cursor != null)
                mouse.Cursor.CursorMode = mode;
        }

        _firstMouseMove = true;
    }

    private static string NormalizeJklFileName(string jklFileName)
    {
        string trimmed = Path.GetFileName(jklFileName.Trim());
        return trimmed.EndsWith(".jkl", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}.jkl";
    }

    private static string? ResolveVfsPath(string path)
    {
        if (_vfs == null)
            return null;

        if (_vfs.FileExists(path))
            return path;

        string lower = path.ToLowerInvariant();
        return _vfs.FileExists(lower) ? lower : null;
    }

    private static void FireCurrentWeapon()
    {
        if (_worldSim == null || _player == null || _worldSim.Inventory.IsDead || _weaponFireTimer > 0f)
            return;

        bool isSaber = _selectedWeapon == 1;
        if (!isSaber && _worldSim.Inventory.EnergyAmmo < 1f)
            return;

        (float cooldown, float range, float damage, string boltModel, string fireSound, float boltSpeed) = _selectedWeapon switch
        {
            1 => (0.53f, 0.45f, 35.0f, string.Empty, string.Empty, 0f),
            3 => (0.15f, 10.0f, 20.0f, "str0.3do", "trprsht2.wav", 9.0f),
            4 => (0.10f, 10.0f, 18.0f, "rpt0.3do", "repeat-1.wav", 12.0f),
            _ => (0.35f, 10.0f, 25.0f, "bry0.3do", "pistol-1.wav", 8.0f)
        };

        _weaponFireCooldown = cooldown;
        _weaponFireTimer = cooldown;
        _weaponAnimTime = 0f;

        if (isSaber)
        {
            SelectSaberSlashKeyframes();
        }

        Vector3 rayOrigin = _thirdPerson ? _player.EyePosition : _camera.Position;
        _worldSim.FireWeaponRay(rayOrigin, _camera.Forward, range, damage, isMelee: isSaber, boltModel, fireSound, boltSpeed);
    }

    private static void SelectSaberSlashKeyframes()
    {
        bool fwd = _keys[(int)Key.W];
        bool back = _keys[(int)Key.S];
        bool left = _keys[(int)Key.A];
        bool right = _keys[(int)Key.D];

        int slash = _saberSlashIndex;
        _saberSlashIndex = (_saberSlashIndex + 1) % 3;

        if (right && !left)
        {
            _activeSaberPovKey = "sabvr1.key";
            _activeSaberExtKey = "kysabrr1.key";
        }
        else if (left && !right)
        {
            _activeSaberPovKey = "sabvl1.key";
            _activeSaberExtKey = "kysabrl1.key";
        }
        else if (back && !fwd)
        {
            bool even = (slash & 1) == 0;
            _activeSaberPovKey = even ? "sabvb1.key" : "sabvb2.key";
            _activeSaberExtKey = even ? "kysabrb1.key" : "kysabrb2.key";
        }
        else if (fwd && !back)
        {
            (_activeSaberPovKey, _activeSaberExtKey) = slash switch
            {
                1 => ("sabvf2.key", "kysabrf2.key"),
                _ => ("sabvf1.key", "kysabrf1.key")
            };
        }
        else
        {
            bool even = (slash & 1) == 0;
            _activeSaberPovKey = even ? "sabvsnp1.key" : "sabvsnp2.key";
            _activeSaberExtKey = even ? "kysnap1.key" : "kysnap2.key";
        }
    }

    private static void OnUpdate(double deltaTime)
    {
        if (_menuRenderer != null && _menuRenderer.CurrentScreen != MenuScreen.None)
            return;

        if (_worldSim == null || _level == null)
            return;

        // Clamp deltaTime so the first frame after OnLoad (or window drag pauses) cannot cause physics tunneling
        float dt = MathF.Min((float)deltaTime, 0.05f);
        _meshRenderer?.Update(dt);

        bool isDead = _worldSim?.Inventory.IsDead == true;

        if (!isDead)
        {
            const float keyTurnSpeed = 120.0f;
            if (_keys[(int)Key.Left]) _camera.Yaw += keyTurnSpeed * dt;
            if (_keys[(int)Key.Right]) _camera.Yaw -= keyTurnSpeed * dt;
            if (_keys[(int)Key.Up]) _camera.Pitch = Math.Clamp(_camera.Pitch + keyTurnSpeed * dt, -89.0f, 89.0f);
            if (_keys[(int)Key.Down]) _camera.Pitch = Math.Clamp(_camera.Pitch - keyTurnSpeed * dt, -89.0f, 89.0f);
        }

        _weaponAnimTime += dt;
        if (_weaponFireTimer > 0f)
            _weaponFireTimer = MathF.Max(0f, _weaponFireTimer - dt);
        if (_saberIgniteTimer > 0f)
            _saberIgniteTimer = MathF.Max(0f, _saberIgniteTimer - dt);

        if (!isDead && (_mouseLeftDown || _keys[(int)Key.ControlLeft] || _keys[(int)Key.ControlRight] || _keys[(int)Key.Enter]))
            FireCurrentWeapon();

        Vector2 oldPlayerXY = _player != null ? new Vector2(_player.Position.X, _player.Position.Y) : Vector2.Zero;

        if (isDead)
        {
            _playerMoving = false;
        }
        else
        {
            bool isUnderwater = _player != null && _player.IsUnderwater;
            Vector3 wishDir = Vector3.Zero;
            if (isUnderwater)
            {
                if (_keys[(int)Key.W]) wishDir += _camera.Forward;
                if (_keys[(int)Key.S]) wishDir -= _camera.Forward;
                if (_keys[(int)Key.D]) wishDir += _camera.Right;
                if (_keys[(int)Key.A]) wishDir -= _camera.Right;
                if (_keys[(int)Key.Space]) wishDir += Vector3.UnitZ;
                if (_keys[(int)Key.C]) wishDir -= Vector3.UnitZ;
            }
            else
            {
                Vector3 forwardFlat = Vector3.Normalize(new Vector3(_camera.Forward.X, _camera.Forward.Y, 0));
                Vector3 rightFlat = Vector3.Normalize(new Vector3(_camera.Right.X, _camera.Right.Y, 0));

                if (_keys[(int)Key.W]) wishDir += forwardFlat;
                if (_keys[(int)Key.S]) wishDir -= forwardFlat;
                if (_keys[(int)Key.D]) wishDir += rightFlat;
                if (_keys[(int)Key.A]) wishDir -= rightFlat;
            }

            _playerMoving = wishDir.LengthSquared() > 0.01f;
            if (_playerMoving)
                _playerMoveBobTime += dt;

            if (_flyMode)
            {
                float speed = 10.0f * (_keys[(int)Key.ShiftLeft] ? 2.5f : 1.0f) * dt;
                Vector3 flyMove = Vector3.Zero;
                if (_keys[(int)Key.W]) flyMove += _camera.Forward;
                if (_keys[(int)Key.S]) flyMove -= _camera.Forward;
                if (_keys[(int)Key.D]) flyMove += _camera.Right;
                if (_keys[(int)Key.A]) flyMove -= _camera.Right;
                if (_keys[(int)Key.Space]) flyMove += Vector3.UnitZ;
                if (_keys[(int)Key.C]) flyMove -= Vector3.UnitZ;

                if (flyMove.LengthSquared() > 0)
                {
                    _player!.Position += Vector3.Normalize(flyMove) * speed;
                }
            }
            else
            {
                bool wishJump = _keys[(int)Key.Space];
                _player!.MoveSpeed = _keys[(int)Key.ShiftLeft] ? 4.0f : 2.0f;
                _player.Update(
                    dt,
                    wishDir,
                    wishJump,
                    _level!,
                    modelResolver: name => string.IsNullOrEmpty(name) ? null : GetOrLoadModel(name));
            }
        }

        if (_worldSim != null && _player != null)
        {
            Vector3 elevDelta = _worldSim.Update(dt, _player.Position, _player.CurrentSectorIndex, _camera.Forward);
            if (!isDead && elevDelta != Vector3.Zero && !_flyMode)
            {
                _player.Position += elevDelta;
            }

            if (_thingRenderer != null)
                _worldSim.SyncThingAnimations(_thingRenderer);
        }

        // Sync Kyle (Thing 0, ky.3do) position, yaw, and puppet animation
        if (_level != null && _level.Things.Count > 0 && _player != null)
        {
            var kyle = _level.Things[0];
            kyle.Position = _player.PelvisPosition;
            kyle.PitchYawRoll = new Vector3(0f, _camera.Yaw, 0f);
            kyle.SectorIndex = _player.CurrentSectorIndex;

            int kyMode = _player.IsUnderwater
                ? (_selectedWeapon == 1 ? 5 : 4)
                : (_selectedWeapon == 1 ? 2 : 1);
            if (!isDead && _selectedWeapon == 1 && _weaponFireTimer > 0f)
            {
                // ky.pup Mode=2 inherits fire=kypnchl0.key (fist punch) from Mode=0;
                // weap_saber.cog drives saber slashes explicitly via PlayKey (kysnap*/kysabrf*).
                _thingRenderer?.SetThingKeyframe(0, _activeSaberExtKey, mode: kyMode, loop: false);
            }
            else
            {
                string kyAction = isDead
                    ? "death"
                    : (_weaponFireTimer > 0f ? "fire" : (_playerMoving ? "run" : "stand"));
                _thingRenderer?.SetThingAnimation(0, kyAction, kyMode, loop: !isDead);
            }

            float dist = isDead
                ? 0f
                : Vector2.Distance(oldPlayerXY, new Vector2(_player.Position.X, _player.Position.Y));
            _thingRenderer?.SetThingDistanceMoved(0, dist);
        }

        _thingRenderer?.Update(dt);

        if (_player != null)
        {
            if (_thirdPerson)
            {
                Vector3 desiredCamPos = _player.EyePosition - _camera.Forward * 0.30f + Vector3.UnitZ * 0.04f;
                if (_worldSim != null && _worldSim.TryRaycastWorld(_player.EyePosition, desiredCamPos, out Vector3 camHit))
                {
                    Vector3 pullDir = Vector3.Normalize(_player.EyePosition - desiredCamPos);
                    float hitDist = Vector3.Distance(_player.EyePosition, camHit);
                    _camera.Position = hitDist > 0.03f
                        ? camHit + pullDir * 0.03f
                        : _player.EyePosition;
                }
                else
                {
                    _camera.Position = desiredCamPos;
                }
            }
            else
            {
                _camera.Position = _player.EyePosition;
            }
        }

        _audio?.Update(_camera.Position, _camera.Forward, _camera.Up);

        if (_worldSim?.LevelCompleted == true && _menuRenderer != null)
        {
            _menuRenderer.CompletedLevelName = _episode?.FindLevelEntry(_currentLevelFile)?.DisplayName ?? _currentLevelFile;
            OpenMenu(MenuScreen.LevelComplete);
        }
    }

    private static void OnRender(double deltaTime)
    {
        _gl!.ClearColor(0.02f, 0.02f, 0.04f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        bool renderWorld = _worldSim != null &&
                           _level != null &&
                           _meshRenderer != null &&
                           _menuRenderer?.CurrentScreen is not (MenuScreen.MainMenu or MenuScreen.Loading);

        if (renderWorld)
        {
            _shader!.Use();

            float aspect = (float)_window!.Size.X / Math.Max(1, _window.Size.Y);
            Matrix4x4 model = Matrix4x4.Identity;
            Matrix4x4 view = _camera.GetViewMatrix();
            Matrix4x4 projection = _camera.GetProjectionMatrix(aspect);

            _shader.SetUniform("uModel", model);
            _shader.SetUniform("uView", view);
            _shader.SetUniform("uProjection", projection);
            _shader.SetUniform("uTint", _worldSim?.CurrentScreenTint ?? Vector3.Zero);
            _shader.SetUniform("uAlpha", 1.0f);

            _meshRenderer!.Render(_shader);

            // Render 3DO entities (pass -1 in 3rd-person so Kyle Thing 0 is rendered)
            _thingRenderer?.RenderThings(_shader, _level!, playerThingIndex: _thirdPerson ? -1 : 0);

            // Render 3rd-person lightsaber blade from Kyle's right hand (ky.3do node 12 / k_rhand)
            Vector3 saberColor = new(0.12f, 1.00f, 0.32f);
            if (_thirdPerson && _selectedWeapon == 1 && _saberRenderer != null &&
                _level!.Things.Count > 0 && _worldSim!.Inventory.IsDead != true)
            {
                var kyle = _level.Things[0];
                Matrix4x4 kyleWorld = SithMatrix34.Build34(kyle.PitchYawRoll, kyle.Position).ToMatrix4x4();
                Vector3 handWorldPos;
                Vector3 bladeDir;
                if (_thingRenderer?.TryGetThingNodeTransform(0, 12, out Matrix4x4 rightHandLocal) == true)
                {
                    Matrix4x4 rightHandWorld = rightHandLocal * kyleWorld;
                    handWorldPos = Vector3.Transform(Vector3.Zero, rightHandWorld);
                    bladeDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rightHandWorld));
                }
                else
                {
                    handWorldPos = Vector3.Transform(new Vector3(0.0104f, 0.0557f, -0.0129f), kyleWorld);
                    bladeDir = Vector3.Normalize(Vector3.TransformNormal(new Vector3(0.205f, 0.567f, 0.798f), kyleWorld));
                }
                float bladeLengthScale = _saberIgniteTimer > 0f
                    ? Math.Clamp(1.0f - (_saberIgniteTimer / SaberIgniteDuration), 0.05f, 1.0f)
                    : 1.0f;
                _saberRenderer.RenderThirdPersonBlade(view, projection, handWorldPos, bladeDir, saberColor, bladeLengthScale);
            }

            // Render active NPC lightsaber blades (e.g. Yun's saberyellow1.mat/saberyellow0.mat)
            if (_saberRenderer != null && _worldSim != null && _level != null)
            {
                foreach (var npcThing in _level.Things)
                {
                    if (npcThing.ThingIndex == 0 ||
                        (npcThing.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed | SithThingFlags.Invisible)) != 0 ||
                        (_worldSim.JkGetFlags(npcThing.ThingIndex) & 0x1) == 0 ||
                        !_worldSim.TryGetThingSaberInfo(npcThing.ThingIndex, out var npcSaber))
                    {
                        continue;
                    }

                    Matrix4x4 npcWorld = SithMatrix34.Build34(npcThing.PitchYawRoll, npcThing.Position).ToMatrix4x4();
                    Vector3 npcHandPos;
                    Vector3 npcBladeDir;
                    if (_thingRenderer?.TryGetThingPrimaryWeaponNodeTransform(npcThing.ThingIndex, out Matrix4x4 saberJointLocal) == true)
                    {
                        Matrix4x4 saberWorld = saberJointLocal * npcWorld;
                        npcHandPos = Vector3.Transform(Vector3.Zero, saberWorld);
                        npcBladeDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, saberWorld));
                    }
                    else
                    {
                        npcHandPos = npcThing.Position + new Vector3(0f, 0f, 0.04f);
                        npcBladeDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, npcWorld));
                    }

                    _saberRenderer.RenderThirdPersonBlade(
                        view,
                        projection,
                        npcHandPos,
                        npcBladeDir,
                        saberColor,
                        lengthScale: 1.0f,
                        sideMat: npcSaber.SideMat,
                        tipMat: npcSaber.TipMat,
                        bladeLength: npcSaber.Length);
                }
            }

            // Render active 3D blaster projectiles and wall impact sparks
            _thingRenderer?.RenderProjectiles(_shader, _worldSim!.Projectiles);
            _thingRenderer?.RenderSparks(_shader, _worldSim!.Sparks);

            // Render first-person weapon viewmodel when in 1st-person view
            if (!_thirdPerson && _thingRenderer != null && _worldSim!.Inventory.IsDead != true)
            {
                bool isSaber = _selectedWeapon == 1;
                bool isIgnitingSaber = isSaber && _saberIgniteTimer > 0f && _weaponFireTimer <= 0f;
                (string weaponModel, string weaponKey) = _selectedWeapon switch
                {
                    1 => ("sabv.3do", _weaponFireTimer > 0f ? _activeSaberPovKey : (isIgnitingSaber ? "sabvmnt.key" : "sabvhold.key")),
                    3 => ("strv.3do", "strvpst1.key"),
                    4 => ("rptv.3do", "rptvpst1.key"),
                    _ => ("bryv.3do", "bryvpst1.key")
                };

                float animTime = _weaponFireTimer > 0f
                    ? _weaponAnimTime
                    : (isIgnitingSaber ? (SaberIgniteDuration - _saberIgniteTimer) : 0.0f);
                float bobX = _playerMoving ? MathF.Cos(_playerMoveBobTime * 8.0f) * 0.0012f : 0f;
                float bobZ = _playerMoving ? MathF.Sin(_playerMoveBobTime * 16.0f) * 0.0012f : 0f;
                float recoilY = (!isSaber && _weaponFireTimer > 0f && _weaponFireCooldown > 0f)
                    ? -0.004f * (_weaponFireTimer / _weaponFireCooldown)
                    : 0f;
                Vector3 localOffset = GetViewmodelBaseOffset(isSaber) + new Vector3(bobX, recoilY, bobZ);

                // Authentic jkPlayer_DrawPov -eyeOffset placement (slightly right-of-center & low)
                _thingRenderer.RenderViewmodel(
                    _shader,
                    _camera,
                    weaponModel,
                    weaponKey,
                    animTime,
                    aspect,
                    localOffset);

                if (isSaber && _saberRenderer != null &&
                    _thingRenderer.TryGetViewmodelNodeTransform(weaponModel, weaponKey, animTime, nodeIndex: 5, out Matrix4x4 joint5Local))
                {
                    float fovRad = MathF.PI / 180.0f * ThingMeshRenderer.ViewmodelVerticalFovDegrees;
                    Matrix4x4 bladeProjection = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspect, 0.005f, 10.0f);
                    Matrix4x4 viewmodelWorld = ThingMeshRenderer.BuildViewmodelWorldMatrix(_camera, localOffset);
                    Matrix4x4 joint5World = joint5Local * viewmodelWorld;
                    Vector3 bladeBaseWorldPos = Vector3.Transform(Vector3.Zero, joint5World);
                    Vector3 bladeForwardDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, joint5World));
                    float swingProgress = _weaponFireTimer > 0f
                        ? Math.Clamp(1.0f - (_weaponFireTimer / 0.53f), 0.0f, 1.0f)
                        : 0.0f;
                    float bladeLengthScale = _saberIgniteTimer > 0f
                        ? Math.Clamp(1.0f - (_saberIgniteTimer / SaberIgniteDuration), 0.05f, 1.0f)
                        : 1.0f;

                    _saberRenderer.RenderFirstPersonBlade(
                        view,
                        bladeProjection,
                        bladeBaseWorldPos,
                        bladeForwardDir,
                        saberColor,
                        swingProgress,
                        bladeLengthScale);
                }
            }

            // Render 2D HUD overlay
            if (_hudRenderer != null)
            {
                var inv = _worldSim!.Inventory;
                _hudRenderer.Render(
                    _window.Size.X,
                    _window.Size.Y,
                    inv.Health,
                    inv.MaxHealth,
                    inv.Shields,
                    200.0f,
                    (int)inv.EnergyAmmo,
                    _selectedWeapon,
                    inv.IsDead,
                    _worldSim.StatusMessage ?? (inv.IsDead
                    ? "PRESS R TO RESPAWN"
                    : (_worldSim.IsInCutscene ? "INTRO - PRESS SPACE TO SKIP" : null)));
            }
        }

        if (_menuRenderer != null && _menuRenderer.CurrentScreen != MenuScreen.None)
        {
            _menuRenderer.Render(_window!.Size.X, _window.Size.Y);
        }
    }

    /// <summary>
    /// Returns the Sith-basis camera-local placement offset (X=right, Y=forward, Z=up;
    /// mapped to OpenGL camera space X=right, Y=up, Z=-forward by BuildViewmodelWorldMatrix)
    /// for the active first-person viewmodel using authentic jkPlayer_DrawPov -eyeOffset (0, 0, -0.037).
    /// </summary>
    public static Vector3 GetViewmodelBaseOffset(bool isSaber)
    {
        float eyeOffsetZ = _player?.EyeOffsetFromPelvis ?? 0.037f;
        return new Vector3(0f, 0f, -eyeOffsetZ);
    }

    private static ThreeDoModel? GetOrLoadModel(string modelName)
    {
        if (_vfs == null || string.IsNullOrEmpty(modelName))
            return null;

        if (_modelCache.TryGetValue(modelName, out var cached))
            return cached;

        string[] searchPaths = [$"3do/{modelName}", $"3do/mat/{modelName}", modelName];
        foreach (string path in searchPaths)
        {
            if (_vfs.FileExists(path))
            {
                var parsed = ThreeDoReader.Parse(_vfs.ReadAllText(path));
                _modelCache[modelName] = parsed;
                return parsed;
            }
        }

        _modelCache[modelName] = null;
        return null;
    }

    private static void OnClosing()
    {
        _menuRenderer?.Dispose();
        _saberRenderer?.Dispose();
        _hudRenderer?.Dispose();
        _audio?.Dispose();
        _thingRenderer?.Dispose();
        _meshRenderer?.Dispose();
        _textures?.Dispose();
        _shader?.Dispose();
        _gl?.Dispose();
        _vfs?.Dispose();
    }

    private static void LogPlayerPosition()
    {
        if (_player == null)
            return;

        Vector3 p = _player.Position;
        Vector3 fwd = _camera.Forward;
        int secIdx = _player.CurrentSectorIndex;

        string portalsSummary = string.Empty;
        if (_level != null && secIdx >= 0 && secIdx < _level.Sectors.Count)
        {
            var portals = new List<string>();
            foreach (var s in _level.Sectors[secIdx].Surfaces)
            {
                if (!s.IsPortal || s.MirrorSector == null) continue;
                int flags = (s.AdjoinIndex >= 0 && s.AdjoinIndex < _level.Adjoins.Count)
                    ? _level.Adjoins[s.AdjoinIndex].Flags
                    : -1;
                portals.Add($"Sec{s.MirrorSector.SectorIndex}(Adj={s.AdjoinIndex},Flags=0x{flags:X})");
            }
            if (portals.Count > 0)
                portalsSummary = " | Portals: " + string.Join(", ", portals.Distinct());
        }

        string aheadSummary = string.Empty;
        if (_worldSim != null && _worldSim.TryRaycastWorld(_camera.Position, _camera.Position + fwd * 1.5f, out Vector3 hit))
        {
            float dist = Vector3.Distance(_camera.Position, hit);
            aheadSummary = $" | WallAhead={dist:F3}m@({hit.X:F3},{hit.Y:F3},{hit.Z:F3})";
        }

        string line = $"[POS] Sec={secIdx} Pos=({p.X:F3}, {p.Y:F3}, {p.Z:F3}) Yaw={_camera.Yaw:F1} Pitch={_camera.Pitch:F1} Fwd=({fwd.X:F2}, {fwd.Y:F2}, {fwd.Z:F2}) Grounded={_player.IsGrounded} Fly={_flyMode}{aheadSummary}{portalsSummary}";
        Console.WriteLine(line);

        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "opensith_positions.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // Ignore file logging errors
        }

        _worldSim?.ShowStatusMessage($"SEC {secIdx} ({p.X:F2}, {p.Y:F2}, {p.Z:F2})", 4.0f);
    }
}
