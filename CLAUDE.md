# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

An experimental remake of Hearts of Iron IV in Unity (6000.3, URP), built to stress-test **Archon-Engine**, the user's own grand strategy engine. The engine lives at `Assets/Archon-Engine` as a git submodule (github.com/ForgottenHistory/Archon-Engine) and is owned by the user, so engine fixes and design changes are welcome, not off-limits. The goal is seeing how far it can get, not a shippable game. Prefer big visual wins over exact HOI4 fidelity.

Work so far is the map: terrain, borders, rivers, labels, atmosphere, day/night.

## Layout

- `Assets/Archon-Engine/`: the ENGINE (submodule). Its own docs are in `Assets/Archon-Engine/Docs/`. `Docs/Engine/ARCHITECTURE_OVERVIEW.md`, `engine-game-separation.md` and `map-system-architecture.md` are the ones that matter most for map work. `Docs/CURRENT_FEATURES.md` lists what the engine already provides.
- `Assets/Game/`: the GAME layer for HOI4 (namespace `Hoi4.*`, compiled into Assembly-CSharp because it has no asmdef). It holds the shaders, the map extension classes, `Settings/Hoi4Settings.asset` (points the engine at the `Hoi4Data` directory) and `VisualStyles/Hoi4VisualStyle.asset`.
- `Assets/Scenes/Hoi4Test.unity`: the working scene.
- `Tools/`: the Python data pipeline and the prototypes.
- `Hoi4Data/`: converted game data. It is **gitignored and derived from the user's HOI4 install, so never commit it or redistribute it.**

## ENGINE vs GAME

Archon's rule is "ENGINE provides mechanisms, GAME defines policy". Anything HOI4-specific (colors, border classes, label rules, terrain look) belongs in `Assets/Game`. Put it in the engine only if it is a generic mechanism that another game could use. If the engine lacks an extension point, add one to the engine rather than hacking around it in game code.

GAME code plugs into the engine through registries, set before the scene loads:

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
static void Register() => MapRendererRegistry.BorderClassifier = new Hoi4BorderClassifier();
```

Unity runs these methods in every scene, the engine's own StarterKit scene included (the user runs it to check the engine). So every game `RuntimeInitializeOnLoadMethod` that registers or spawns something must start with `if (!Hoi4.Hoi4Scenes.Active) return;`. `Hoi4Scenes` lists the HOI4 scenes. Cache resets at `SubsystemRegistration` don't need the check.

The pattern is shown in `Hoi4BorderClassifier` (`IBorderClassifier`) and `Hoi4MapLabelSource` (which extends `DefaultMapLabelSource`). Both read the extra HOI4 data (states, strategic regions, impassable states, capitals) from `map/hoi4_regions.json5` through `DataFileResolver`, and they fall back to engine defaults when that file is missing.

### Shaders

`Assets/Game/Shaders/Hoi4TerrainMap.shader` is a thin wrapper. It includes the engine's `MapShaderSetup.hlsl`, then the GAME policy (`MapModes/Hoi4MapModes.hlsl` and `Hoi4Terrain.hlsl`), then the engine pass `MapPassTerrain.hlsl`.

- Engine-owned values (map textures, map mode, borders, fog and so on) are shader **globals** set by `MapShaderGlobals`. Do not declare them as material properties, because a material property shadows the global.
- Per-material properties must match the `UnityPerMaterial` CBUFFER in the engine's `DefaultCommon.hlsl`.
- Textures that hold data rather than colour (the heightmap, IDs, indices) must be created **linear** (`new Texture2D(..., linear: true)`, or `linear: true` in the visual style). Otherwise the GPU colour-decodes the values while CPU reads return the raw ones. See "sRGB Data Texture Trap" in `Assets/Archon-Engine/Docs/Log/learnings/unity-gpu-debugging-guide.md`.

Engine-level findings like this belong in the Archon docs (`Docs/Log/learnings/`, `Docs/Log/decisions/`, `Docs/Engine/`), not only here.
- The HOI4-specific textures and tuning values (`_Hoi4*`) come from the visual style file, not from the material (see below).

## Data pipeline

```
python Tools/hoi4_convert.py [--hoi4 "<HOI4 install>"] [--out Hoi4Data]
```

The default `--hoi4` path is the Steam install under `C:\Program Files (x86)`. The script needs `numpy`, `scipy` and `Pillow`. It reads HOI4 map, state, country, localisation and gfx files and writes an Archon-format data directory (the same layout as `Assets/Archon-Engine/Template-Data`). That covers provinces, definitions, terrain (with HOI4 combat width and attack penalty), heightmap, rivers, `hoi4_regions.json5`, `hoi4_river_crossings.json5` and the converted terrain atlases and colormaps under `gfx/map/`. It contains its own small Paradox-script parser (`parse_pdx`).

Rerun the converter after changing it. The engine caches decoded images next to them as `*.pixels` and `*.adjacency` files.

### Visual style tuning

- `Tools/hoi4_visual_style.json5` is the **master** copy of the shader parameters. The converter copies it to `Hoi4Data/map/visual_style.json5`.
- In play mode the copy in `Hoi4Data` is **hot reloaded**. Tweak values there live, then copy the good ones back to `Tools/hoi4_visual_style.json5`, because the next converter run overwrites the `Hoi4Data` copy.

## Prototyping workflow

For tricky map algorithms (border tracing and smoothing, river tracing, label placement), first prototype in Python under `Tools/prototypes/<topic>/` and render preview PNGs to check the result visually. Port to C# or HLSL only after that. Existing prototypes include `borders/trace_borders.py` with `render_borders.py`, `rivers/trace_rivers.py`, `labels/place_labels.py`, `peace/peace_conference.py` (peace conference costs and score, checked against a live HOI4 conference), `peace/peace_stripes.py` (the conference's map stripes) and `supply/rail_network.py` (railways, supply hubs and their connection to the capital).

## Work in progress

- The war AI is being rebuilt in three layers (country: theatres and armies; strategic: army front lines and offensives; tactical: per-division attacks). The plan, decisions and progress are in `Docs/ai-layers.md`. Read it before touching `Hoi4BattlePlans*`, `Hoi4EnemyAI` or `Hoi4Armies`, and update its checkboxes as work lands.

## Session recordings (how to read the game)

The engine provides the mechanism (`Core.Recording.SessionRecorder`, see `Assets/Archon-Engine/Docs/Engine/session-recording.md`). The game decides when to record and which HOI4 tables to add. In the editor, `Hoi4Recorder` (`Assets/Game/Scripts/Units/Hoi4Recorder.cs`) records every play session to `Recordings/<stamp>_<TAG>/` (gitignored; the newest 12 are kept). The menu toggle **HOI4 > Record Sessions** (remembered per machine) turns it off, for example for clean profiling: recording costs about 15 ms per game day in a large war. With it off there is no recording to read, `perf.tsv` included. The files are TSVs that start with `tick` (game hours) and `date`. They cover:

- division changes, plus weekly snapshots;
- plan figures and fronts;
- battles every hour;
- control changes;
- every executed command;
- `notes.tsv`, the decisions and why they were made (AI odds, targets and break-offs, plan distribution, retreats);
- warnings and errors;
- `perf.tsv`, one row per game day: milliseconds per simulation system, frame times, wars and divisions.

When the user reports odd behaviour, read the newest recording before guessing:

```
python Tools/recorder/report.py [REC]            # overview and anomalies: divisions off the line, stuck, thrashing, AI outcomes, activity (holding/marching/attacking shares), strategy
python Tools/recorder/unit.py [REC] ID           # one division's whole story
python Tools/recorder/battles.py [REC]           # whole battles: outcome by terrain/river and attacker, entrenchment at the start
python Tools/recorder/rows.py [REC] FILE col=val --cols a,b   # filter by column name; --schema, --count COL
```

`rows.py` is the engine's tool (`Assets/Archon-Engine/Tools~/recording/`), pointed at `Recordings/`. `report.py`, `unit.py` and `battles.py` are HOI4-specific. New systems should call `SessionRecorder.Note(system, kind, country, unit, province, target, text)` at their decision points.

## Build and test

Work happens in the Unity editor: open `Hoi4Test.unity` and press Play. To catch C# errors without the editor, `python Tools/check_build.py` compiles the game layer with `dotnet build` against the engine DLLs Unity last built (so engine changes need a Unity compile first). `python Tools/check_build.py MapAssembly` (or `Core`, `StarterKit`...) compiles one engine project the same way, for engine changes.

### Checking against real HOI4

Mechanics should match real HOI4 data, not only the defines. `Tools/reference/` measures the real game: `hoi4_reinforcement.py` (refill rates from the reference workspace's daily division snapshots), `save_dig_in.py` (entrenchment in battles from a plain-text save), `wiki_text.py` (text of a wiki page saved as HTML). The reference workspace (`D:\Stuff\Coding\hoi4`, read its `CLAUDE.md`) has recordings of real games: daily division snapshots, battles, country figures, production output. `Tools/manpower_pools.py` prints our starting manpower pools, `Tools/equipment_outlook.py` each country's equipment shortfall, stockpile and production.

### Tests

The engine's tests use the Unity Test Framework (`Assets/Archon-Engine/Scripts/Tests`, `Scripts/Map/Tests`) and run from the Test Runner window. The game layer has no tests. The `.csproj` and `.slnx` files are Unity-generated and gitignored.

## Git

- If the engine changed, commit inside `Assets/Archon-Engine` first, then commit the outer repo (which includes the submodule pointer bump).
- Never push without the user's explicit OK, and ask again each time.
- Keep the local fullscreen editor addon (`Packages/com.mibdev.fullscreen-editor/`, already gitignored) and its entry in `Packages/packages-lock.json` out of commits.
- Commit messages are short imperative summaries such as "HOI4 rivers" or "HOI4 day/night with city lights".
