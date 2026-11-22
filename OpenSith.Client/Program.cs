using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenSith.Core.Cog;
using OpenSith.Core.Data;
using OpenSith.Core.Materials;
using OpenSith.Core.Models;
using OpenSith.Core.Networking;
using OpenSith.Core.Physics;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenSith.Client;

public static partial class Program
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
    private const float SaberIgniteDuration = 0.70f;
    private static float _saberIgniteTimer = 0.0f;
    private static float _playerMoveBobTime = 0.0f;
    private static bool _playerMoving = false;
    private static bool _mouseLeftDown = false;
    private static bool _mouseRightDown = false;
    private static readonly bool[] _keys = new bool[512];

    private static IReadOnlyList<ModelSelectionEntry> _modelCatalog = [new ModelSelectionEntry(0, "ky.3do", "ky.snd", "Katarn")];
    private static IReadOnlyList<SaberColorOption> _saberCatalog = PlayerCustomizationCatalog.DefaultSaberColors;
    private static PlayerLoadoutConfig _playerLoadout = new();
    private static string? _cliModelArg;
    private static string? _cliSaberArg;
    private static string? _cliForceArg;
    private static bool _cliAllWeapons;
    private static int _selectedForcePowerIndex;
    private static readonly Dictionary<Key, int> _activeForceKeyBins = new();

    public static void Main(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("         OPENSITH - JEDI KNIGHT ENGINE            ");
        Console.WriteLine("==================================================");

        ParseCliArgs(args);

        string res1Path = Path.Combine(_assetsDir, "Resource", "Res1hi.gob");
        string res2Path = Path.Combine(_assetsDir, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(_assetsDir, "Episode", "JK1.GOB");

        if (!File.Exists(res2Path))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Retail assets not found at '{_assetsDir}'.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("Mounting VFS archives...");
        _vfs = new VirtualFileSystem();
        if (File.Exists(res1Path))
            _vfs.Mount(GobArchive.FromFile(res1Path));
        _vfs.Mount(GobArchive.FromFile(res2Path));
        if (File.Exists(jk1Path))
            _vfs.Mount(GobArchive.FromFile(jk1Path));

        _modelCatalog = PlayerCustomizationCatalog.LoadModels(_vfs);
        _saberCatalog = PlayerCustomizationCatalog.LoadSaberColors(_vfs);
        ResolveCliLoadout();

        if (!TryConnectMultiplayerHandshake())
            return;

        MountExtraEpisodeArchives(_vfs, jk1Path);

        if (_vfs.FileExists("episode.jk"))
        {
            _episode = EpisodeDefinition.Parse(_vfs.ReadAllText("episode.jk"));
            Console.WriteLine($"Loaded episode.jk: \"{_episode.Title}\" ({_episode.GetPlayableLevels().Count} playable levels)");
        }

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
        _window.FocusChanged += focused =>
        {
            if (!focused)
                ReleaseAllHeldInputs(endWorldSimActions: true);
        };

        Console.WriteLine("\nStarting graphics window:");
        Console.WriteLine("  • WASD: Walk / Move");
        Console.WriteLine("  • Space: Jump (or fly up in fly mode)");
        Console.WriteLine("  • Shift: Sprint");
        Console.WriteLine("  • F: Toggle Fly/Noclip mode");
        Console.WriteLine("  • V / F1: Toggle 1st-person / 3rd-person camera");
        Console.WriteLine("  • E: Activate / open nearby door");
        Console.WriteLine("  • 1..9 / 0: Switch weapon (1=Fists, 2=Bryar, 3=StRifle, 4=ThermDet, 5=Bowcaster, 6=Repeater, 7=RailDet, 8=SeqChg, 9=ConcRifle, 0=Lightsaber)");
        Console.WriteLine("  • H / I / L: Use Bacta Tank / Toggle IR Goggles / Toggle Field Light");
        Console.WriteLine("  • F5..F12 (+Shift+F5..F10): All 14 Force Powers | [ / ]: Cycle Unlocked Force | G: Cast Selected");
        Console.WriteLine("  • Escape: Pause Menu & CUSTOMIZE / FORCE (28 Models, 7 Saber Colors, Force Rank)");
        Console.WriteLine("  • Left Click / Ctrl / Enter: Primary Fire | Right Click / Z: Secondary Fire");
        Console.WriteLine("  • B: Toggle Texture Filtering (Retro Point vs Direct3D Bilinear)");
        Console.WriteLine("  • Tab: Show Multiplayer Scoreboard");
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
            else if (string.Equals(args[i], "--connect", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string target = args[++i].Trim();
                int colon = target.LastIndexOf(':');
                if (colon > 0 && int.TryParse(target[(colon + 1)..], out int port))
                {
                    _connectHost = target[..colon];
                    _connectPort = Math.Clamp(port, 1, 65535);
                }
                else
                {
                    _connectHost = target;
                    _connectPort = NetCodec.DefaultPort;
                }
            }
            else if (string.Equals(args[i], "--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _playerName = args[++i].Trim();
            }
            else if (string.Equals(args[i], "--team", StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length &&
                     int.TryParse(args[++i], out int team))
            {
                _playerTeam = Math.Clamp(team, 0, 2);
            }
            else if (string.Equals(args[i], "--episode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _episodeGobOverride = args[++i].Trim();
            }
            else if (string.Equals(args[i], "--assets", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _assetsDir = args[++i];
            }
            else if (string.Equals(args[i], "--model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _cliModelArg = args[++i].Trim();
            }
            else if (string.Equals(args[i], "--saber", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _cliSaberArg = args[++i].Trim();
            }
            else if (string.Equals(args[i], "--force", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _cliForceArg = args[++i].Trim();
            }
            else if (string.Equals(args[i], "--all-weapons", StringComparison.OrdinalIgnoreCase))
            {
                _cliAllWeapons = true;
            }
        }
    }

    private static void ResolveCliLoadout()
    {
        if (!string.IsNullOrWhiteSpace(_cliForceArg))
        {
            string f = _cliForceArg.ToLowerInvariant();
            if (f is "none" or "off" or "0") _playerLoadout.ApplyPreset(ForcePreset.None);
            else if (f is "light" or "jedi") _playerLoadout.ApplyPreset(ForcePreset.LightJedi);
            else if (f is "dark" or "sith") _playerLoadout.ApplyPreset(ForcePreset.DarkJedi);
            else if (f is "all" or "max" or "master" or "sandbox") _playerLoadout.ApplyPreset(ForcePreset.MasterSandbox);
        }

        if (_cliAllWeapons)
            _playerLoadout.GrantAllWeapons = true;

        if (!string.IsNullOrWhiteSpace(_cliModelArg) &&
            PlayerCustomizationCatalog.TryResolveModelIndex(_modelCatalog, _cliModelArg, out int modelIdx))
        {
            _playerLoadout.ModelIndex = modelIdx;
        }

        if (!string.IsNullOrWhiteSpace(_cliSaberArg) &&
            PlayerCustomizationCatalog.TryResolveSaberIndex(_saberCatalog, _cliSaberArg, out int saberIdx))
        {
            _playerLoadout.SaberColorIndex = saberIdx;
        }

        _playerLoadout.ClampToPolicy(PlayerLoadoutConfig.MaxJediRank, allowAllWeapons: true, _modelCatalog.Count, _saberCatalog.Count);
        EnsureValidSelectedForcePower();
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
        _menuRenderer.ConfigureCustomization(_modelCatalog, _saberCatalog, _playerLoadout);

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

                bool wasDown = (uint)key < (uint)_keys.Length && _keys[(int)key];
                if ((uint)key < (uint)_keys.Length) _keys[(int)key] = true;
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

                if (IsCurrentPlayerDead())
                {
                    if (key == Key.R && _player != null && _level != null)
                    {
                        if (IsMultiplayerClient)
                        {
                            _pendingRespawnPressed = true;
                            AdvanceActionSeq();
                        }
                        else if (_worldSim != null)
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
                    }
                    return;
                }

                if (key == Key.F) _flyMode = !_flyMode;
                if (key == Key.P || key == Key.F3) LogPlayerPosition();
                if (key == Key.Space && _worldSim?.IsInCutscene == true) _worldSim.SkipActiveCutscene();

                int localPlayerId = ResolveActiveLocalThingId();
                if (key == Key.E && _player != null)
                {
                    if (IsMultiplayerClient)
                    {
                        _pendingUsePressed = true;
                        AdvanceActionSeq();
                    }
                    else
                    {
                        _worldSim?.ActivateNearestDoor(localPlayerId, _player.Position, _camera.Forward);
                    }
                }

                if (!wasDown && (IsMultiplayerClient || (_worldSim != null && localPlayerId >= 0)))
                {
                    if (key == Key.LeftBracket)
                        CycleSelectedForcePower(-1);
                    else if (key == Key.RightBracket)
                        CycleSelectedForcePower(+1);

                    bool shiftHeld = _keys[(int)Key.ShiftLeft] || _keys[(int)Key.ShiftRight];
                    int forceBin = MapKeyToForceBin(key, shiftHeld);
                    if (forceBin < 0 && key == Key.G)
                    {
                        EnsureValidSelectedForcePower();
                        if (GetCurrentForceRank(_selectedForcePowerIndex) > 0)
                            forceBin = PlayerCustomizationCatalog.ForcePowers[_selectedForcePowerIndex].Bin;
                    }

                    if (forceBin >= 0)
                    {
                        _activeForceKeyBins[key] = forceBin;
                        if (IsMultiplayerClient)
                            _heldForceBin = (byte)forceBin;
                        else
                            _worldSim?.BeginForcePower(localPlayerId, forceBin, _camera.Forward);
                    }

                    int itemBin = key switch
                    {
                        Key.H => PlayerInventory.BinBacta,
                        Key.I => PlayerInventory.BinIrGoggles,
                        Key.L => PlayerInventory.BinFieldLight,
                        _ => -1
                    };
                    if (itemBin >= 0)
                    {
                        if (IsMultiplayerClient)
                        {
                            _pendingItemBin = (byte)itemBin;
                            AdvanceActionSeq();
                        }
                        else
                        {
                            _worldSim?.UseItem(localPlayerId, itemBin);
                        }
                    }

                    int weaponBin = key switch
                    {
                        Key.Number1 => PlayerInventory.BinFists,
                        Key.Number2 => PlayerInventory.BinBryar,
                        Key.Number3 => PlayerInventory.BinStRifle,
                        Key.Number4 => PlayerInventory.BinThermDet,
                        Key.Number5 => PlayerInventory.BinBowcaster,
                        Key.Number6 => PlayerInventory.BinRepeater,
                        Key.Number7 => PlayerInventory.BinRailDet,
                        Key.Number8 => PlayerInventory.BinSeqCharge,
                        Key.Number9 => PlayerInventory.BinConcRifle,
                        Key.Number0 => PlayerInventory.BinLightsaber,
                        _ => -1
                    };
                    if (weaponBin > 0)
                    {
                        if (IsMultiplayerClient)
                        {
                            _pendingWeaponBin = (byte)weaponBin;
                            AdvanceActionSeq();
                        }
                        else
                        {
                            _worldSim?.SelectWeapon(localPlayerId, weaponBin);
                        }
                    }

                    if (key == Key.ControlLeft || key == Key.ControlRight || key == Key.Enter)
                    {
                        _fireKeyPrimary = true;
                        if (!IsMultiplayerClient)
                            _worldSim?.BeginWeaponFire(localPlayerId, 0);
                    }
                    else if (key == Key.Z)
                    {
                        _fireKeySecondary = true;
                        if (!IsMultiplayerClient)
                            _worldSim?.BeginWeaponFire(localPlayerId, 1);
                    }
                }
            };
            keyboard.KeyUp += (k, key, code) =>
            {
                if ((uint)key < (uint)_keys.Length) _keys[(int)key] = false;

                int releasedForceBin = _activeForceKeyBins.Remove(key, out int recordedBin)
                    ? recordedBin
                    : -1;

                if (IsMultiplayerClient)
                {
                    if (releasedForceBin >= 0 && _heldForceBin == (byte)releasedForceBin)
                    {
                        _heldForceBin = 0;
                        foreach (int remainingBin in _activeForceKeyBins.Values)
                        {
                            _heldForceBin = (byte)remainingBin;
                            break;
                        }
                    }
                    if (key == Key.ControlLeft || key == Key.ControlRight || key == Key.Enter)
                        _fireKeyPrimary = false;
                    else if (key == Key.Z)
                        _fireKeySecondary = false;
                    return;
                }

                if (_worldSim == null)
                    return;

                int localPlayerId = _worldSim.GetLocalPlayerThing();
                if (localPlayerId < 0)
                    return;

                if (releasedForceBin >= 0)
                    _worldSim.EndForcePower(localPlayerId, releasedForceBin);

                if (key == Key.ControlLeft || key == Key.ControlRight || key == Key.Enter)
                {
                    _fireKeyPrimary = false;
                    _worldSim.EndWeaponFire(localPlayerId, 0);
                }
                else if (key == Key.Z)
                {
                    _fireKeySecondary = false;
                    _worldSim.EndWeaponFire(localPlayerId, 1);
                }
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

                if (IsCurrentPlayerDead())
                    return;

                if (IsMultiplayerClient)
                {
                    if (button == MouseButton.Left) _mouseLeftDown = true;
                    else if (button == MouseButton.Right) _mouseRightDown = true;
                    return;
                }

                if (_worldSim == null)
                    return;

                int localPlayerId = _worldSim.GetLocalPlayerThing();
                if (localPlayerId < 0)
                    return;

                if (button == MouseButton.Left)
                {
                    _mouseLeftDown = true;
                    _worldSim.BeginWeaponFire(localPlayerId, 0);
                }
                else if (button == MouseButton.Right)
                {
                    _mouseRightDown = true;
                    _worldSim.BeginWeaponFire(localPlayerId, 1);
                }
            };

            mouse.MouseUp += (m, button) =>
            {
                int localPlayerId = _worldSim?.GetLocalPlayerThing() ?? -1;
                if (button == MouseButton.Left)
                {
                    _mouseLeftDown = false;
                    if (!IsMultiplayerClient && localPlayerId >= 0)
                        _worldSim?.EndWeaponFire(localPlayerId, 0);
                }
                else if (button == MouseButton.Right)
                {
                    _mouseRightDown = false;
                    if (!IsMultiplayerClient && localPlayerId >= 0)
                        _worldSim?.EndWeaponFire(localPlayerId, 1);
                }
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

        if (IsMultiplayerClient)
        {
            SetupMultiplayerLevelState(normalizedFile);
            _saberIgniteTimer = 0.0f;
            _playerMoveBobTime = 0.0f;
            _playerMoving = false;
            ReleaseAllHeldInputs(endWorldSimActions: false);

            if (_menuRenderer != null)
            {
                _menuRenderer.HasLoadedLevel = true;
                _menuRenderer.Difficulty = _currentDifficulty;
                _menuRenderer.CurrentScreen = MenuScreen.None;
            }

            SetCursorMode(CursorMode.Raw);
            return;
        }

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

        string initialModelName = _modelCatalog.Count > 0
            ? _modelCatalog[Math.Clamp(_playerLoadout.ModelIndex, 0, _modelCatalog.Count - 1)].Model3D
            : "ky.3do";
        _player.ConfigureFromModel(GetOrLoadModel(initialModelName) ?? GetOrLoadModel("ky.3do"));
        _player.ModelResolver = name => string.IsNullOrEmpty(name) ? null : GetOrLoadModel(name);

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
            if (binIdx == PlayerInventory.BinLightsaber)
                _saberIgniteTimer = SaberIgniteDuration;
        };
        _worldSim.StartLevel();
        if (_cliForceArg != null || _cliAllWeapons || _playerLoadout.ModelIndex != 0 ||
            _playerLoadout.SaberColorIndex != PlayerCustomizationCatalog.DefaultSaberColorIndex ||
            _playerLoadout.JediRank > 0 || HasAnyForcePower(_playerLoadout))
        {
            _worldSim.ApplyPlayerCustomization(_worldSim.GetLocalPlayerThing(), _playerLoadout);
        }

        if (_worldSim.LocalPlayer != null &&
            _worldSim.LocalPlayer.CurWeaponBin == PlayerInventory.BinBryar &&
            _worldSim.Inventory.HasBryar)
        {
            _worldSim.SelectWeapon(_worldSim.GetLocalPlayerThing(), PlayerInventory.BinBryar);
        }

        if (string.Equals(normalizedFile, "01narshadda.jkl", StringComparison.OrdinalIgnoreCase))
            _audio.PlayLoopingMusic("01cantinaband02.wav", 0.35f);

        Console.WriteLine($"WorldSimulation ready: {_worldSim.ActiveCogCount} COGs, {_worldSim.FramedThingCount} framed entities, {_worldSim.ActiveNpcCount} AI NPCs.");
        _saberIgniteTimer = 0.0f;
        _playerMoveBobTime = 0.0f;
        _playerMoving = false;
        ReleaseAllHeldInputs(endWorldSimActions: false);

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

            case MenuActionKind.ApplyCustomization:
                if (action.Loadout != null)
                    ApplyMenuLoadout(action.Loadout);
                if (_menuRenderer != null && _menuRenderer.HasLoadedLevel)
                {
                    _menuRenderer.CurrentScreen = MenuScreen.None;
                    SetCursorMode(CursorMode.Raw);
                }
                else
                {
                    OpenMenu(MenuScreen.MainMenu);
                }
                break;

            case MenuActionKind.Quit:
                _window?.Close();
                break;
        }
    }

    private static void OpenMenu(MenuScreen screen)
    {
        SyncLocalLoadoutFromLiveState();
        if (_menuRenderer != null)
        {
            _menuRenderer.SyncLoadout(_playerLoadout);
            _menuRenderer.CurrentScreen = screen;
        }

        ReleaseAllHeldInputs(endWorldSimActions: true);
        SetCursorMode(CursorMode.Normal);
    }

    private static void ReleaseAllHeldInputs(bool endWorldSimActions)
    {
        int localPlayerId = _worldSim?.GetLocalPlayerThing() ?? -1;
        if (endWorldSimActions && !IsMultiplayerClient && _worldSim != null && localPlayerId >= 0)
        {
            if (_mouseLeftDown || _fireKeyPrimary) _worldSim.EndWeaponFire(localPlayerId, 0);
            if (_mouseRightDown || _fireKeySecondary) _worldSim.EndWeaponFire(localPlayerId, 1);
            foreach (int bin in _activeForceKeyBins.Values)
                _worldSim.EndForcePower(localPlayerId, bin);
            if (_heldForceBin > 0)
                _worldSim.EndForcePower(localPlayerId, _heldForceBin);
        }

        _mouseLeftDown = false;
        _mouseRightDown = false;
        _fireKeyPrimary = false;
        _fireKeySecondary = false;
        _heldForceBin = 0;
        _activeForceKeyBins.Clear();
        Array.Clear(_keys);
    }

    private static void ApplyMenuLoadout(PlayerLoadoutConfig cfg)
    {
        _playerLoadout = cfg.Clone();
        _playerLoadout.ClampToPolicy(PlayerLoadoutConfig.MaxJediRank, allowAllWeapons: true, _modelCatalog.Count, _saberCatalog.Count);

        string model3d = _modelCatalog.Count > 0
            ? _modelCatalog[Math.Clamp(_playerLoadout.ModelIndex, 0, _modelCatalog.Count - 1)].Model3D
            : "ky.3do";

        if (IsMultiplayerClient)
        {
            _netClient?.SendConfig(_playerLoadout);
            if (_level != null && _netLocalThingId >= 0 && _netLocalThingId < _level.Things.Count)
                _level.Things[_netLocalThingId].Model3D = model3d;
            _player?.ConfigureFromModel(GetOrLoadModel(model3d) ?? GetOrLoadModel("ky.3do"));
        }
        else if (_worldSim != null)
        {
            int localId = _worldSim.GetLocalPlayerThing();
            _worldSim.ApplyPlayerCustomization(localId, _playerLoadout);
            _player?.ConfigureFromModel(GetOrLoadModel(model3d) ?? GetOrLoadModel("ky.3do"));
        }

        EnsureValidSelectedForcePower();
    }

    private static void SyncLocalLoadoutFromLiveState()
    {
        if (IsMultiplayerClient)
        {
            if (_latestSnapshot != null)
            {
                _playerLoadout = NetCodec.DecodeLoadout(
                    _latestSnapshot.Local.ModelIndex,
                    _latestSnapshot.Local.SaberColorIndex,
                    _latestSnapshot.Local.JediRank,
                    _latestSnapshot.Local.LoadoutFlags,
                    _latestSnapshot.Local.PackedForceRanks);
            }
            return;
        }

        if (_worldSim?.LocalPlayer is { } lp)
        {
            _playerLoadout.ModelIndex = Math.Clamp(lp.ModelIndex, 0, Math.Max(0, _modelCatalog.Count - 1));
            _playerLoadout.SaberColorIndex = Math.Clamp(lp.SaberColorIndex, 0, Math.Max(0, _saberCatalog.Count - 1));
            _playerLoadout.JediRank = lp.Inventory.ForceRank;
            if (lp.Loadout != null)
                _playerLoadout.GrantAllWeapons = lp.Loadout.GrantAllWeapons;
            for (int i = 0; i < PlayerCustomizationCatalog.ForcePowers.Count; i++)
                _playerLoadout.ForceRanks[i] = (byte)Math.Clamp((int)lp.Inventory.GetInv(PlayerCustomizationCatalog.ForcePowers[i].Bin), 0, 4);
        }
    }

    private static bool HasAnyForcePower(PlayerLoadoutConfig cfg)
    {
        for (int i = 0; i < cfg.ForceRanks.Length; i++)
        {
            if (cfg.ForceRanks[i] > 0)
                return true;
        }
        return false;
    }

    private static int GetCurrentForceRank(int powerIndex)
    {
        if (powerIndex < 0 || powerIndex >= PlayerCustomizationCatalog.ForcePowers.Count)
            return 0;

        int bin = PlayerCustomizationCatalog.ForcePowers[powerIndex].Bin;
        if (IsMultiplayerClient)
        {
            if (_latestSnapshot != null)
            {
                Span<byte> ranks = stackalloc byte[PlayerLoadoutConfig.ForcePowerCount];
                NetCodec.UnpackForceRanks(_latestSnapshot.Local.PackedForceRanks, ranks);
                return ranks[powerIndex];
            }
            return _playerLoadout.ForceRanks[powerIndex];
        }

        return _worldSim != null ? (int)_worldSim.Inventory.GetInv(bin) : _playerLoadout.ForceRanks[powerIndex];
    }

    private static void EnsureValidSelectedForcePower()
    {
        if (GetCurrentForceRank(_selectedForcePowerIndex) > 0)
            return;

        for (int i = 0; i < PlayerCustomizationCatalog.ForcePowers.Count; i++)
        {
            if (GetCurrentForceRank(i) > 0)
            {
                _selectedForcePowerIndex = i;
                return;
            }
        }
    }

    private static void CycleSelectedForcePower(int delta)
    {
        int count = PlayerCustomizationCatalog.ForcePowers.Count;
        for (int step = 1; step <= count; step++)
        {
            int idx = (_selectedForcePowerIndex + delta * step + count * 4) % count;
            if (GetCurrentForceRank(idx) > 0)
            {
                _selectedForcePowerIndex = idx;
                return;
            }
        }
    }

    private static string? BuildActiveForceHudLabel()
    {
        int jediRank = IsMultiplayerClient
            ? (_latestSnapshot?.Local.JediRank ?? _playerLoadout.JediRank)
            : (_worldSim?.Inventory.ForceRank ?? _playerLoadout.JediRank);
        if (jediRank <= 0)
            return null;

        EnsureValidSelectedForcePower();
        int pRank = GetCurrentForceRank(_selectedForcePowerIndex);
        if (pRank <= 0)
            return null;

        ForcePowerOption fp = PlayerCustomizationCatalog.ForcePowers[_selectedForcePowerIndex];
        return $"FORCE: {fp.Name.ToUpperInvariant()} ({pRank}) [G]";
    }

    private static int MapKeyToForceBin(Key key, bool shiftHeld = false)
    {
        if (shiftHeld)
        {
            return key switch
            {
                Key.F5 => PlayerInventory.BinForcePersuasion,
                Key.F6 => PlayerInventory.BinForceBlinding,
                Key.F7 => PlayerInventory.BinForceAbsorb,
                Key.F8 => PlayerInventory.BinForceProtection,
                Key.F9 => PlayerInventory.BinForceLightning,
                Key.F10 => PlayerInventory.BinForceDeadlySight,
                _ => -1
            };
        }

        return key switch
        {
            Key.F5 => PlayerInventory.BinForceJump,
            Key.F6 => PlayerInventory.BinForceSpeed,
            Key.F7 => PlayerInventory.BinForcePull,
            Key.F8 => PlayerInventory.BinForceHeal,
            Key.F9 => PlayerInventory.BinForceSeeing,
            Key.F10 => PlayerInventory.BinForceThrow,
            Key.F11 => PlayerInventory.BinForceGrip,
            Key.F12 => PlayerInventory.BinForceDestruction,
            _ => -1
        };
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

    private static void OnUpdate(double deltaTime)
    {
        // Clamp deltaTime so the first frame after OnLoad (or window drag pauses) cannot cause physics tunneling
        float dt = MathF.Min((float)deltaTime, 0.05f);
        if (IsMultiplayerClient)
        {
            _meshRenderer?.Update(dt);
            UpdateMultiplayerClient(dt);
            return;
        }

        if (_menuRenderer != null && _menuRenderer.CurrentScreen != MenuScreen.None)
            return;

        if (_worldSim == null || _level == null)
            return;

        _meshRenderer?.Update(dt);

        bool isDead = _worldSim.Inventory.IsDead;
        bool isFrozen = _worldSim.IsPlayerFrozen;

        if (!isDead && !isFrozen)
        {
            const float keyTurnSpeed = 120.0f;
            if (_keys[(int)Key.Left]) _camera.Yaw += keyTurnSpeed * dt;
            if (_keys[(int)Key.Right]) _camera.Yaw -= keyTurnSpeed * dt;
            if (_keys[(int)Key.Up]) _camera.Pitch = Math.Clamp(_camera.Pitch + keyTurnSpeed * dt, -89.0f, 89.0f);
            if (_keys[(int)Key.Down]) _camera.Pitch = Math.Clamp(_camera.Pitch - keyTurnSpeed * dt, -89.0f, 89.0f);
        }

        if (_saberIgniteTimer > 0f)
            _saberIgniteTimer = MathF.Max(0f, _saberIgniteTimer - dt);

        Vector2 oldPlayerXY = _player != null ? new Vector2(_player.Position.X, _player.Position.Y) : Vector2.Zero;
        Vector3 localThrust = Vector3.Zero;

        if (isDead || isFrozen)
        {
            _playerMoving = false;
        }
        else
        {
            float fwdInput = (_keys[(int)Key.W] ? 1f : 0f) - (_keys[(int)Key.S] ? 1f : 0f);
            float rightInput = (_keys[(int)Key.D] ? 1f : 0f) - (_keys[(int)Key.A] ? 1f : 0f);
            float upInput = (_keys[(int)Key.Space] ? 1f : 0f) - (_keys[(int)Key.C] ? 1f : 0f);
            localThrust = new Vector3(rightInput, fwdInput, upInput);

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
                float baseSpeed = _keys[(int)Key.ShiftLeft] ? 4.0f : 2.0f;
                _player!.MoveSpeed = baseSpeed * MathF.Max(0.25f, 1.0f + _worldSim.PlayerExtraSpeed);
                _player.Update(
                    dt,
                    wishDir,
                    wishJump,
                    _level!,
                    modelResolver: name => string.IsNullOrEmpty(name) ? null : GetOrLoadModel(name));
            }
        }

        int localPlayerId = _worldSim.GetLocalPlayerThing();
        if (_player != null)
        {
            if (localPlayerId >= 0)
            {
                _worldSim.UpdatePlayerInput(
                    localPlayerId,
                    _player.Position,
                    _player.CurrentSectorIndex,
                    _camera.Forward,
                    localThrust,
                    _player.IsGrounded,
                    _player.IsUnderwater);
            }

            _worldSim.StepWorld(dt);

            if (localPlayerId >= 0)
            {
                Vector3 elevDelta = _worldSim.ConsumePlayerPlatformDelta(localPlayerId);
                if (!isDead && elevDelta != Vector3.Zero && !_flyMode)
                    _player.Position += elevDelta;

                if (!isDead && !_flyMode && _worldSim.TryConsumePlayerVelocityOverride(localPlayerId, out Vector3 velOverride))
                    _player.ApplyExternalVelocity(velOverride);
            }

            if (_thingRenderer != null)
                _worldSim.SyncThingAnimations(_thingRenderer);
        }

        // Sync player Thing position, yaw, and puppet animation
        int renderPlayerId = localPlayerId >= 0 ? localPlayerId : 0;
        var lpState = _worldSim.LocalPlayer;
        bool usingSaber = lpState?.CurWeaponBin == PlayerInventory.BinLightsaber;
        bool isFiring = lpState != null && (lpState.ActiveFireMode >= 0 || lpState.FireWait > 0f);
        if (_level != null && renderPlayerId >= 0 && renderPlayerId < _level.Things.Count && _player != null)
        {
            var kyle = _level.Things[renderPlayerId];
            kyle.Position = _player.PelvisPosition;
            kyle.PitchYawRoll = new Vector3(0f, _camera.Yaw, 0f);
            kyle.SectorIndex = _player.CurrentSectorIndex;

            int kyMode = _player.IsUnderwater
                ? (usingSaber ? 5 : 4)
                : (usingSaber ? 2 : 1);
            string kyAction = isDead
                ? "death"
                : (isFiring ? "fire" : (_playerMoving ? "run" : "stand"));
            _thingRenderer?.SetThingAnimation(renderPlayerId, kyAction, kyMode, loop: !isDead);

            float dist = isDead
                ? 0f
                : Vector2.Distance(oldPlayerXY, new Vector2(_player.Position.X, _player.Position.Y));
            _thingRenderer?.SetThingDistanceMoved(renderPlayerId, dist);
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

        bool renderWorld = (IsMultiplayerClient || _worldSim != null) &&
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

            if (IsMultiplayerClient)
            {
                RenderMultiplayerWorldAndHud(aspect, view, projection);
            }
            else
            {
                _shader.SetUniform("uModel", model);
                _shader.SetUniform("uView", view);
                _shader.SetUniform("uProjection", projection);
                _shader.SetUniform("uTint", _worldSim?.CurrentScreenTint ?? Vector3.Zero);
                _shader.SetUniform("uAlpha", 1.0f);

                _meshRenderer!.Render(_shader);

                int localPlayerId = _worldSim!.GetLocalPlayerThing();
                int renderPlayerId = localPlayerId >= 0 ? localPlayerId : 0;
                var lpState = _worldSim.LocalPlayer;
                int activeWeaponBin = lpState?.CurWeaponBin ?? PlayerInventory.BinBryar;
                bool isSaber = activeWeaponBin == PlayerInventory.BinLightsaber;
                bool isSaberActive = isSaber && ((_worldSim.JkGetFlags(renderPlayerId) & 0x85) != 0 || _saberIgniteTimer > 0f);

                // Render 3DO entities (pass -1 in 3rd-person so player Thing is rendered)
                _thingRenderer?.RenderThings(_shader, _level!, playerThingIndex: _thirdPerson ? -1 : renderPlayerId);

                if (_thirdPerson && _worldSim.Inventory.IsDead != true &&
                    activeWeaponBin != PlayerInventory.BinFists &&
                    renderPlayerId >= 0 && renderPlayerId < _level!.Things.Count)
                {
                    string? mesh = _worldSim.JkGetWeaponMesh(renderPlayerId);
                    if (string.IsNullOrWhiteSpace(mesh) || mesh.Equals("fistg.3do", StringComparison.OrdinalIgnoreCase))
                        mesh = PlayerCustomizationCatalog.GetThirdPersonWeaponModel(activeWeaponBin);
                    _thingRenderer?.RenderAttachedWeaponMesh(_shader, _level.Things[renderPlayerId], mesh);
                }

                // Render 3rd-person lightsaber blade from Kyle's right hand (ky.3do node 12 / k_rhand)
                SaberColorOption localSaberOpt = PlayerCustomizationCatalog.ResolveSaberOption(_saberCatalog, lpState?.SaberColorIndex ?? _playerLoadout.SaberColorIndex);
                string localSideMat = localSaberOpt.SideMaterial;
                string localTipMat = localSaberOpt.TipMaterial;
                float localBladeLen = 0.12f;
                Vector3 saberColor = localSaberOpt.FallbackRgb;
                if (_worldSim.TryGetThingSaberInfo(renderPlayerId, out var simSaberInfo) && !string.IsNullOrWhiteSpace(simSaberInfo.SideMat))
                {
                    localSideMat = simSaberInfo.SideMat;
                    localTipMat = simSaberInfo.TipMat;
                    if (simSaberInfo.Length > 0.01f)
                        localBladeLen = simSaberInfo.Length;
                    SaberColorOption? matched = PlayerCustomizationCatalog.FindSaberBySideMaterial(_saberCatalog, localSideMat);
                    if (matched != null)
                        saberColor = matched.FallbackRgb;
                }
                if (_thirdPerson && isSaberActive && _saberRenderer != null &&
                    renderPlayerId >= 0 && renderPlayerId < _level!.Things.Count && _worldSim.Inventory.IsDead != true)
                {
                    var kyle = _level.Things[renderPlayerId];
                    Matrix4x4 kyleWorld = SithMatrix34.Build34(kyle.PitchYawRoll, kyle.Position).ToMatrix4x4();
                    Vector3 handWorldPos;
                    Vector3 bladeDir;
                    if (_thingRenderer?.TryGetThingPrimaryWeaponNodeTransform(renderPlayerId, out Matrix4x4 rightHandLocal) == true)
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
                    _saberRenderer.RenderThirdPersonBlade(view, projection, handWorldPos, bladeDir, saberColor, bladeLengthScale, localSideMat, localTipMat, localBladeLen);
                }

                // Render active NPC lightsaber blades (e.g. Yun's saberyellow1.mat/saberyellow0.mat)
                if (_saberRenderer != null && _worldSim != null && _level != null)
                {
                    foreach (var npcThing in _level.Things)
                    {
                        if (npcThing.ThingIndex == renderPlayerId ||
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

                        Vector3 npcColor = PlayerCustomizationCatalog.FindSaberBySideMaterial(_saberCatalog, npcSaber.SideMat)?.FallbackRgb ?? saberColor;
                        _saberRenderer.RenderThirdPersonBlade(
                            view,
                            projection,
                            npcHandPos,
                            npcBladeDir,
                            npcColor,
                            lengthScale: 1.0f,
                            sideMat: npcSaber.SideMat,
                            tipMat: npcSaber.TipMat,
                            bladeLength: npcSaber.Length);
                    }
                }

                // Render active 3D projectiles, wall impact sparks, and sprite/explosion VFX
                _thingRenderer?.RenderProjectiles(_shader, _worldSim!.Projectiles);
                _thingRenderer?.RenderSparks(_shader, _worldSim!.Sparks);
                _thingRenderer?.RenderVisualEffects(_shader, _camera, _worldSim!.VisualEffects);

                // Render first-person COG-driven weapon viewmodel when in 1st-person view
                if (!_thirdPerson && _thingRenderer != null && lpState != null && !_worldSim!.Inventory.IsDead)
                {
                    string weaponModel = string.IsNullOrWhiteSpace(lpState.PovModel) ? "bryv.3do" : lpState.PovModel;
                    string weaponKey = string.IsNullOrWhiteSpace(lpState.PovKey) ? "bryvpst1.key" : lpState.PovKey;
                    float animTime = lpState.PovKeyTime;
                    float bobX = _playerMoving ? MathF.Cos(_playerMoveBobTime * 8.0f) * 0.0012f : 0f;
                    float bobZ = _playerMoving ? MathF.Sin(_playerMoveBobTime * 16.0f) * 0.0012f : 0f;
                    Vector3 localOffset = GetViewmodelBaseOffset(isSaber) + new Vector3(bobX, 0f, bobZ) + lpState.PovShakePos;

                    // Authentic jkPlayer_DrawPov -eyeOffset placement (slightly right-of-center & low)
                    _thingRenderer.RenderViewmodel(
                        _shader,
                        _camera,
                        weaponModel,
                        weaponKey,
                        animTime,
                        aspect,
                        localOffset);

                    if (isSaberActive && _saberRenderer != null &&
                        _thingRenderer.TryGetViewmodelNodeTransform(weaponModel, weaponKey, animTime, nodeIndex: 5, out Matrix4x4 joint5Local))
                    {
                        float fovRad = MathF.PI / 180.0f * ThingMeshRenderer.ViewmodelVerticalFovDegrees;
                        Matrix4x4 bladeProjection = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspect, 0.005f, 10.0f);
                        Matrix4x4 viewmodelWorld = ThingMeshRenderer.BuildViewmodelWorldMatrix(_camera, localOffset);
                        Matrix4x4 joint5World = joint5Local * viewmodelWorld;
                        Vector3 bladeBaseWorldPos = Vector3.Transform(Vector3.Zero, joint5World);
                        Vector3 bladeForwardDir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, joint5World));
                        float swingProgress = lpState.SaberDamageActive
                            ? Math.Clamp(lpState.PovKeyTime / 0.45f, 0.0f, 1.0f)
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
                            bladeLengthScale,
                            localSideMat,
                            localTipMat,
                            localBladeLen);
                    }
                }

                // Render 2D HUD overlay
                if (_hudRenderer != null)
                {
                    var inv = _worldSim!.Inventory;
                    int displayAmmo = activeWeaponBin switch
                    {
                        PlayerInventory.BinThermDet => (int)inv.GetInv(PlayerInventory.BinThermDet),
                        PlayerInventory.BinBowcaster or PlayerInventory.BinRepeater or PlayerInventory.BinConcRifle => (int)inv.PowerAmmo,
                        PlayerInventory.BinRailDet => (int)inv.RailCharges,
                        PlayerInventory.BinSeqCharge => (int)inv.GetInv(PlayerInventory.BinSeqCharge),
                        _ => (int)inv.EnergyAmmo
                    };
                    _hudRenderer.Render(
                        _window.Size.X,
                        _window.Size.Y,
                        inv.Health,
                        inv.MaxHealth,
                        inv.Shields,
                        200.0f,
                        displayAmmo,
                        activeWeaponBin,
                        inv.IsDead,
                        _worldSim.StatusMessage ?? (inv.IsDead
                            ? "PRESS R TO RESPAWN"
                            : (_worldSim.IsInCutscene ? "INTRO - PRESS SPACE TO SKIP" : null)),
                        inv.ForceMana,
                        inv.MaxForceMana,
                        inv.Battery,
                        _worldSim.IsFieldLightActive(renderPlayerId),
                        BuildActiveForceHudLabel());
                }
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
        DisposeMultiplayerClient();
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
