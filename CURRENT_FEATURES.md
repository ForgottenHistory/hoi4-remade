# HOI4 Remade - Current Features

**Last Updated:** 2026-11-21

---

## TL;DR

**Map:** Clausewitz-style terrain, water, coasts, smooth mesh borders in HOI4 classes, rivers, country labels, haze, clouds, day/night with city lights

**Units:** 1936 armies, on-map counters, click and box selection, HOI4 movement rules, move arrows (red when attacking), hourly movement, province-to-province combat with retreat and overrun, occupation (controller flips, drawn in the occupier's colour)

**UI:** Top bar, state panel and division panel (UI Toolkit: UXML + USS + C#), all sprites loaded at runtime from the converted data

**Data:** One Python converter turns the HOI4 install into an Archon data directory

**Not Implemented:** production, research, politics, AI (including enemy armies), saving/loading of HOI4-specific state, multiplayer

The goal is to stress-test Archon-Engine, not to ship a game. Big visual wins are preferred over exact HOI4 fidelity.

---

## Map Rendering

- **HOI4 terrain surface** - 4x4 terrain atlas (diffuse+gloss, normal+spec) blended per map pixel from a terrain index map, tinted by the HOI4 colormap, lit by sun + ambient + Blinn-Phong specular
- **Heightmap-based coasts** - Smooth coast line from a height field instead of province pixels
- **Water** - Port of HOI4's water shader (LEAN waves, refracted sea floor, fresnel sky reflection, sun glint), calmer defaults
- **Political map mode** - Country colors with a soft gradient between countries; unowned land shows terrain
- **Terrain map mode** - Plain terrain
- **Mesh borders** - Smooth traced border meshes with HOI4 border textures
- **Border classes** - Sea, sea region, coast (no line, like HOI4), country, state, impassable, province
- **Rivers** - Traced and rendered with HOI4's river texture, width by river level, kept off water
- **Country labels** - Placed on the capital's territory piece, localised names
- **Atmosphere** - Haze and clouds
- **Day/night** - Sun cycle with city lights (HOI4 citylights texture, masked by the colormap)
- **Selection outline** - Smooth outline with glow for the selected province
- **State outline** - Second, lighter outline around the whole state of the selected province, drawn under the province outline
- **No hover highlight** - Disabled; HOI4 does not highlight provinces under the cursor
- **Visual style tuning** - `Tools/hoi4_visual_style.json5` is the master copy; hot reloaded from `Hoi4Data/map/visual_style.json5` in play mode

---

## Units

- **1936 armies** - Land divisions from HOI4's order of battle, with HOI4 division names
- **Division data** - Name, main type, organisation, strength kept in the GAME layer, keyed by engine unit ID
- **Unit types** - One engine unit type per division main type
- **On-map counters** - One per country per province: plate, type icon, flag, organisation and strength bars, division count, ideology marker. Composed into one atlas, drawn as one mesh, shrink and fade with zoom
- **Selection** - Click a counter (Shift adds), left-drag box select for your own divisions, click the map or press Escape to clear
- **Move orders** - Right-click a province with own divisions selected: path and move
- **Movement rules** - Land only, no water, no impassable states, owner must be own country, ally, enemy or military access
- **Move arrows** - HOI4's `move_thick` arrow ribbon along the route, travelled part in a lighter colour
- **Hourly movement** - Divisions advance every game hour, so progress and arrows fill smoothly
- **Combat** - Province-to-province battles from converted HOI4 equipment stats (attack, defence, breakthrough, hardness, armour, piercing). The attack starts when the move starts; the move into the province is held while an enemy is there. Battle markers sit on the border between the two provinces
- **Retreat and overrun** - Broken defenders march to a friendly neighbouring province (a day); a retreater that arrives where an enemy stands is destroyed
- **Attack arrows** - Move arrows turn red for attacks, with a lighter red for the travelled part
- **Occupation** - Entering a province held by an enemy flips its controller (not owner); occupied land is drawn in the occupier's colour, lakes follow the land around them, and country labels follow control
- **Hover blocking** - No province hover or click through counters

---

## UI

All panels follow one pattern: structure in `.uxml`, look in `*Style.uss` + `Hoi4Theme.uss`, display logic in a static Presenter, a thin View MonoBehaviour that wires events. Sprites are named in USS (`--sprite: "gfx/..."`) and loaded at runtime from the data directory, so no HOI4 art is in the project or a build.

- **Top bar** - Player flag, political power (+2/day), stability, war support, manpower, date pill (click or Space to pause), speed down/up, five speed steps. Shown once a country is picked
- **State panel** - Click a land province to see its state: owner, cores, manpower, resources, victory points, buildings, categories. Appears only after a country is selected; Escape hides it
- **Division panel** - Selected divisions as cards with bars, icon, status (moving / in province), per-card close, select-half, scrollable list
- **Scroll capture** - The mouse wheel scrolls panels instead of zooming the map while the pointer is over one
- **HOI4 font** - Oswald, applied to all panels

---

## Data Pipeline

`python Tools/hoi4_convert.py [--hoi4 "<install>"] [--out Hoi4Data]` (needs numpy, scipy, Pillow)

- **Provinces, definitions, terrain, heightmap, rivers** - Archon-format data directory
- **States and strategic regions** - `map/hoi4_regions.json5` (states, regions, impassable states, capitals, water)
- **State details** - `map/hoi4_state_data.json5`
- **Countries** - Colors, ideologies, stats (`common/hoi4_country_stats.json5`), history
- **Localisation** - Names for provinces, states, countries
- **Order of battle** - 1936 divisions
- **Terrain and water textures** - Atlases, colormaps, water cubemap, normals, border strips
- **Interface sprites** - Top bar, unit panel, state view, counters, move arrow, flags
- **Own Paradox-script parser** - `parse_pdx`
- `Hoi4Data/` is gitignored and derived from the user's install; never commit or redistribute it

---

## Prototypes

Tricky map algorithms are prototyped in Python under `Tools/prototypes/<topic>/` and checked as preview PNGs before porting to C# or HLSL: borders, rivers, labels, highlight outline, move arrows, counters, country fill, top bar.

---

## Known Gaps

- `icons/light_armor.png` sprite is missing from the converted data (infantry icon used instead)
- Province selection does not persist after the state panel closes (HOI4 keeps it)
- Only land units exist; no air or naval
- Political power only counts up; nothing spends it
