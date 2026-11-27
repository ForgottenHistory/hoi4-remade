# Data formats

This is a remake, not a port: we do not read HOI4's files at runtime. `Tools/hoi4_convert.py` reads the HOI4 install once and writes everything the game needs into `Hoi4Data/`, in formats that suit this project. The game and the engine only ever read `Hoi4Data/`.

`Hoi4Data/` is derived from the HOI4 install: it is gitignored and must never be committed or shared.

## Why not Paradox formats

- **Paradox script** (`.txt`: `key = { ... }`) has no standard parser, no (official) tooling and several dialects. One small parser for it lives in the converter (`parse_pdx`) and nowhere else.
- **DDS / TGA** images need decoders; the game loads PNG.
- **Scripted logic** in Paradox script (triggers, effects) is dated and awkward to write. Ours is Lua, which is common, well tooled, and written well by AI assistants, which is how most of this project's code and content is written.

The rule for a new kind of data: pick the format below that fits, convert to it in the converter, and add a row here.

## The formats

| Format | Used for | Read by |
|---|---|---|
| **JSON5** (`.json5`) | All structured data: definitions, start values, UI layout numbers | `Json5Loader` (engine), into `JObject` |
| **Lua** (`.lua`) | Scripted content: logic the game calls (focus trees; later events, decisions) | The engine's `ScriptEngine`, through the game's `Hoi4Script` |
| **PNG** (`.png`) | Every image: map data, terrain, UI sprites, flags, icons | Engine map loaders, `Hoi4Sprites` |
| **CSV** (`.csv`) | Flat tables keyed by province: definitions, adjacencies, centres | Engine loaders and small game readers |
| **YAML** (`.yml`) | Localisation text, in the engine's layout | Engine `LocalizationManager` |
| **TSV** (`.tsv`) | Session recordings (output, not input) | `Tools/recorder/*.py` |
| **TTF** (`.ttf`) | Fonts (OFL, from `Tools/fonts/`) | Map labels, UI |

### JSON5

Data, never logic. Comments allowed, so every generated file starts with a line saying what made it and from what. Files the engine defines keep the engine's names and layout (`Assets/Archon-Engine/Template-Data` is the reference). Files only the game reads are prefixed `hoi4_`.

| Where | What |
|---|---|
| `map/default.json5`, `terrain.json5` | Engine map setup and terrain types (with HOI4 combat width and attack penalty) |
| `map/hoi4_regions.json5` | States, strategic regions, impassable states, capitals |
| `map/hoi4_state_data.json5` | Building definitions and state categories; per state: category, population, buildings, resources, victory points, cores |
| `map/hoi4_railways.json5`, `hoi4_ports.json5`, `hoi4_river_crossings.json5`, `hoi4_weather.json5` | Supply network, naval bases, river crossings, weather zones |
| `map/visual_style.json5` | Shader parameters. Master copy: `Tools/hoi4_visual_style.json5`; this copy is hot reloaded in play mode |
| `common/countries/*.json5`, `history/provinces/*.json5` | Engine country and province files |
| `common/hoi4_technologies.json5` | Tech tree: costs, positions, links, modifiers, AI weights |
| `common/hoi4_ideas.json5` | Ideas and laws with their modifiers (the starting ones, every law, every idea a focus names) |
| `common/hoi4_country_economy.json5`, `hoi4_country_stats.json5` | Per country: research slots, laws, starting ideas; stability, war support, population, ideology |
| `common/hoi4_politics.json5` | Per country: the ruling ideology and each ideology's popularity (shares adding up to 1) |
| `common/hoi4_equipment.json5`, `hoi4_tank_designs.json5` | Equipment catalogue and tank designs |
| `common/hoi4_static_modifiers.json5`, `hoi4_landmarks.json5` | Stability modifiers, landmark buildings |
| `units/*.json5`, `history/units/*.json5` | Sub-unit types; the 1936 armies |
| `gfx/**/**.json5` | Sprite sheets' frame tables and UI layout numbers taken from HOI4's `.gui` files |

Conditions that are simple data (a technology's AI weight modifiers) stay in JSON5 as nested lists the game compiles into its own trigger nodes (`Hoi4ResearchAI`). Anything with effects, scopes or loops goes to Lua instead.

### Lua

Logic. `Tools/hoi4_focus.py` translates `common/national_focus/*.txt` into `common/focus/*.lua`, one file per HOI4 file; its docstring has the full translation rules. A file is a list of calls that register content:

```lua
focus {
  id = "political_effort",
  icon = "GFX_goal_generic_demand_territory",
  x = 19,
  y = 0,
  cost = 10,
  filters = { "FOCUS_FILTER_POLITICAL" },
  available = function(c) return c:has_war(false) end,
  reward = function(c)
    c:add_political_power(120)
  end,
}
```

Conventions:

- **Fields are data or functions.** Plain values (`id`, `cost`, `x`) are read once. Functions take the country `c` and are kept and called later: triggers return whether they hold (`available`, `bypass`), effects do things (`reward`), `ai` returns a weight.
- **Scopes are objects**, triggers and effects are their methods, named as in HOI4 with a few renames: `c:has_government("fascism")`, `s:owner()`, `country("SOV")`, `state(64)`.
- **Arguments:** `no` is `false` (`c:has_war(false)`), a comparison is the operator then the value (`c:num_of_factories(">", 50)`), a block is a table.
- **Shown triggers** use `all(...)`, `any(...)`, `none(...)`, so every line is evaluated and a tooltip can tick each one. Inside effects it is Lua's own `and`, `or`, `not`.
- **Iterators:** `c:any_owned_state(function(s) ... end)`; `for _, s in ipairs(c:owned_states()) do`; `local s = c:random_owned_state(function(s) ... end)` then `if s then`.
- **No state in Lua.** Flags, variables and anything else that must be saved go through methods into C# state. Scripts do no simulation arithmetic: they pass numbers to methods.
- **Everything is translated, by shape.** The translator does not know what the game implements. A method the game lacks returns false and is noted once in the recording (`script` / `missing` in `notes.tsv`); that list is the work still owed in `Assets/Game/Scripts/Scripting/Hoi4Script*.cs`.

The generated files are meant to be readable and hand-editable, but the converter overwrites them: change the translator, or (once we write our own content) keep hand-written scripts in files the converter does not produce.

### PNG

All images are converted from DDS/TGA to PNG. The engine caches decoded map images next to them (`*.pixels`, `*.adjacency`, `*.borders`); delete those or rerun the converter after changing a map image.

- `map/provinces.png`, `terrain.png`, `heightmap.png`, `rivers.png`, `world_normal.png`: map data. Data textures must be loaded **linear**, not sRGB.
- `gfx/map/`: terrain atlases, colormaps, snow, mud, ice, water, borders, rails, ports.
- `gfx/interface/<screen>/`: UI sprites per screen, with a `.json5` beside them where a sheet has frames.
- `gfx/counters/`, `gfx/maparrows/`: unit counters and movement arrows.

### CSV

`map/definition.csv` (province id to colour, the engine's format), `adjacencies.csv` (straits and special links), `hoi4_province_centres.csv`, `hoi4_impassable_borders.csv`. Use CSV only for wide flat tables with one row per province; anything nested is JSON5.

### Recordings (TSV)

Not input data: what the game writes while it runs, to `Recordings/<stamp>_<TAG>/` (gitignored). One file per table, tab separated, starting with `tick` and `date`. See "Session recordings" in `CLAUDE.md`.

## Where each HOI4 source goes

| HOI4 | Ours |
|---|---|
| `map/*.bmp`, `definition.csv`, `map/strategicregions`, `history/states` | `map/*.png`, `map/*.csv`, `map/hoi4_*.json5`, `history/provinces/` |
| `common/countries`, `history/countries` | `common/countries/`, `common/hoi4_country_*.json5` |
| `common/technologies`, `common/ideas`, `common/units` | `common/hoi4_technologies.json5`, `hoi4_ideas.json5`, `units/` |
| `common/buildings`, `common/state_category` | inside `map/hoi4_state_data.json5` |
| `history/units` (orders of battle) | `history/units/` |
| `common/national_focus` | `common/focus/*.lua` |
| `interface/*.gui`, `*.gfx`, `gfx/**/*.dds` | `gfx/**/*.png` and layout `.json5` |
| `localisation/english/*.yml` | `localisation/english/*.yml` (country, state and province names); other names are written into the JSON5 that uses them |
