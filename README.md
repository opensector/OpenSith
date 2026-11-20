# OpenSith

A pure managed C# (.NET 10) reimplementation of the 1997 **Sith Engine** for *Star Wars Jedi Knight: Dark Forces II*, built against retail game assets (`.gob`, `.jkl`, `.3do`, `.key`, `.pup`, `.cog`, `.mat`, `.bm`, `.sft`, `.cmp`, `.snd`, `.wav`) using Silk.NET (OpenGL, OpenAL, Windowing, and Input).

## Solution Architecture

The solution (`OpenSith.slnx`) is split into three projects:

```text
OpenSith.slnx
├── OpenSith.Core     # Pure managed .NET 10 engine library (no native/GPU dependencies)
├── OpenSith.Client   # Silk.NET OpenGL / OpenAL runtime, simulation loop, and 1997 UI
└── OpenSith.Tests    # xUnit regression and asset-verification test suite
```

### `OpenSith.Core`
Zero external dependencies (`net10.0`). Contains all file-format parsers, virtual filesystem, scripting VM, AI, physics, and world state models:

- **`Vfs/`** (`GobArchive`, `GobEntry`, `IVirtualFileSystem`, `VirtualFileSystem`): Reads LucasArts `.gob` archives and overlays loose/resource files (`JK1.GOB`, `Res1hi.gob`, `Res2.gob`).
- **`World/`** (`EpisodeDefinition`, `JklReader`, `JklWriter`, `JklLevel`, `JklSector`, `JklSurface`, `JklAdjoin`, `JklThing`, `JklTemplate`, `JklCogPlacement`): Parses `episode.jk` campaigns and `.jkl` level geometry, portals/adjoins, templates, things, and COG placements.
- **`Cog/`** (`CogLexer`, `CogParser`, `CogAst`, `CogScript`, `CogInstance`, `CogSymbol`, `CogValue`, `CogVm`, `ICogWorldContext`, `PlayerInventory`): Lexer, AST parser, and bytecode/AST virtual machine for JK's `.cog` scripting language, including player inventory and Force power bins.
- **`Models/`** (`ThreeDoReader`, `ThreeDoModel`, `ThreeDoMesh`, `ThreeDoFace`, `ThreeDoHierarchy`, `ThreeDoHierarchyNode`): `.3do` hierarchical 3D model and bone-pivot reader.
- **`Animation/`** (`KeyframeReader`, `KeyframeTrack`, `PuppetDefinition`): `.key` keyframe animation and `.pup` puppet state definitions.
- **`AI/`** (`AiClassReader`, `AiClass`, `AiBrain`, `AiInstinct`, `AiRuntimeTypes`): `.ai` instinct/brain parser and runtime AI state machine.
- **`Physics/`** (`PlayerPhysics`): Sector-portal traversal, convex sector containment, ramp/step-up, wall sliding, and underwater swimming physics.
- **`Materials/`** (`CmpPalette`, `MatMaterial`, `BmBitmap`, `SftFont`, `Rgba32`): 8-bit/16-bit palette, `.mat` texture, `.bm` menu bitmap, and `.sft` bitmap font decoders.
- **`Audio/`** (`SndReader`, `SoundClass`, `SoundClassEntry`, `SoundEvent`, `SoundFlags`, `WavReader`, `WavInfo`): `.snd` sound class and `.wav` audio decoders.

### `OpenSith.Client`
Depends on `OpenSith.Core` and `Silk.NET` (`2.23.0` for Windowing, Input, OpenGL, OpenAL + `Silk.NET.OpenAL.Soft.Native` `1.23.1`):

- **`Program.cs`**: Window lifecycle, CLI `--level` loading, input handling, and render/update loop.
- **`WorldSimulation.cs`**: Runtime world simulation bridging `JklLevel`, `CogVm` (`ICogWorldContext`), `PlayerPhysics`, `AiBrain`, combat, doors/elevators, pickups, and Force powers.
- **`LevelMeshRenderer.cs`**: OpenGL sector geometry renderer (world surfaces, sky passes, alpha-tested grates, translucent surfaces, vertex lighting).
- **`ThingMeshRenderer.cs`**: Hierarchical `.3do` renderer with keyframe interpolation (`PlayKey`) and weapon/joint attachment transforms.
- **`SaberRenderer.cs`**: Lightsaber blade rendering and ignition scaling.
- **`MenuRenderer.cs` & `HudRenderer.cs`**: Authentic 1997 `.bm`/`.sft` menus (main, single-player, pause, tally, loading) and in-game HUD.
- **`TextureManager.cs`, `ShaderProgram.cs`, `Camera.cs`, `AudioEngine.cs`**: GPU texture/shader management, first-person camera, and 3D OpenAL audio.

### `OpenSith.Tests`
xUnit (`2.9.3`) test suite covering format parsers, COG VM verbs, sector/portal collision, keyframe hierarchies, AI instincts, menu/HUD assets, and level/boss-battle regressions.

## Prerequisites & Retail Assets

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- A retail installation of *Star Wars Jedi Knight: Dark Forces II* (providing `Episode\JK1.GOB`, `Resource\Res1hi.gob`, and `Resource\Res2.gob`)

## Building, Running, and Testing

```bash
# Build the full solution
dotnet build OpenSith.slnx

# Run the client (loads default campaign/menu)
dotnet run --project OpenSith.Client

# Run a specific level directly
dotnet run --project OpenSith.Client -- --level 01narshadda.jkl

# Run the test suite
dotnet test OpenSith.slnx
```
