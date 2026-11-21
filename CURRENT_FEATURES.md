# HOI4 Remade - Current Features

**Last Updated:** 2026-11-21

---

## TL;DR

**Map:** Clausewitz-style terrain, water, coasts, smooth mesh borders in HOI4 classes, rivers, country labels, haze, clouds, day/night with city lights, political / terrain / diplomatic map modes

**Units:** 1936 armies, on-map counters with action icons and battle bubbles, click and box selection, HOI4 movement rules, hourly movement, province-to-province combat with terrain, rivers and entrenchment, retreat and overrun, occupation, manpower pools, equipment stockpiles with the 1936 production lines, reinforcement

**Armies and battle plans:** Armies with bottom-of-screen cards, front lines that divisions fill and hold, offensive plans (drag over targets) that advance as a broad front when executed, planning bonus

**AI:** Enemy war AI with odds-based attacks off its own front lines

**Simulation:** Every decision is a command, one fixed-order hourly tick, fixed-point maths and adjacency-based fronts (deterministic, lockstep-ready), session recordings for debugging

**UI:** Top bar, state panel, division panel, army cards, battle plan window, diplomacy panel (UI Toolkit: UXML + USS + C#), all sprites loaded at runtime from the converted data

**Data:** One Python converter turns the HOI4 install into an Archon data directory

**Not Implemented:** production control (only the 1936 lines run), research, politics, supply, terrain and entrenchment in combat, generals, air and naval, saving/loading of HOI4-specific state, multiplayer in practice

The goal is to stress-test Archon-Engine, not to ship a game. Big visual wins are preferred over exact HOI4 fidelity.

---

## Map Rendering

- **HOI4 terrain surface** - 4x4 terrain atlas (diffuse+gloss, normal+spec) blended per map pixel from a terrain index map, tinted by the HOI4 colormap, lit by sun + ambient + Blinn-Phong specular
- **Heightmap-based coasts** - Smooth coast line from a height field instead of province pixels
- **Water** - Port of HOI4's water shader (LEAN waves, refracted sea floor, fresnel sky reflection, sun glint), calmer defaults
- **Political map mode** - Country colors with a soft gradient between countries; unowned land shows terrain
- **Terrain map mode** - Plain terrain
- **Diplomatic map mode** - Own country green, allies blue, enemies red, everyone else terrain (palette mode, opened by the diplomacy panel, refreshed on war and peace)
- **Mesh borders** - Smooth traced border meshes with HOI4 border textures
- **Border classes** - Sea, sea region, coast (no line, like HOI4), country, state, impassable, province
- **Rivers** - Traced and rendered with HOI4's river texture, width by river level, kept off water
- **Country labels** - Placed on the capital's territory piece, localised names, follow control
- **Atmosphere** - Haze and clouds
- **Day/night** - Sun cycle with city lights (HOI4 citylights texture, masked by the colormap)
- **Selection outline** - Smooth outline with glow for the selected province
- **State outline** - Second, lighter outline around the whole state of the selected province, drawn under the province outline
- **No hover highlight** - Disabled; HOI4 does not highlight provinces under the cursor
- **Visual style tuning** - `Tools/hoi4_visual_style.json5` is the master copy; hot reloaded from `Hoi4Data/map/visual_style.json5` in play mode

---

## Units

- **1936 armies** - Land divisions from HOI4's order of battle, with HOI4 division names; the game starts 1 January 1936
- **Division data** - Name, main type, organisation, strength, planning (all `FixedPoint64`) kept in the GAME layer, keyed by engine unit ID
- **Unit types** - One engine unit type per division main type
- **On-map counters** - One per country per province: plate in the country colour, type icon, flag, organisation and strength bars, division count, ideology marker, army colour strip. Composed into one atlas, drawn as one mesh, shrink and fade with zoom
- **Counter action icons** - Move arrow, defend shield or attack chevrons on the counter's right edge
- **Battle bubbles** - Marker on the border between the two provinces, coloured by whether the player is winning or losing
- **Selection** - Click a counter (Shift adds), left-drag box select for your own divisions, S selects half, click the map or press Escape to clear. Any country's counters can be inspected; only your own take orders
- **Move orders** - Right-click a province with own divisions selected: path and move
- **Halt** - H stops the selected divisions where they stand
- **Movement rules** - Land only, no water, no impassable states, owner must be own country, ally, enemy or military access
- **Move arrows** - HOI4's `move_thick` arrow ribbon along the route (centripetal Catmull-Rom), travelled part in a lighter colour, red for attacks
- **Hourly movement** - Divisions advance every game hour, so progress and arrows fill smoothly
- **Combat** - Province-to-province battles across a border, from converted HOI4 equipment stats (attack, defence, breakthrough, hardness, armour, piercing) and combat width with reserves. The attack starts when the move starts; the move into the province is held while an enemy is there. No counter-attack into the province you are being attacked from
- **Terrain in combat** - The target's terrain (HOI4 `00_terrain.txt`) sets the combat width (plains 70, forest 60, mountain 50, urban 80...), plus its support width for each extra attack direction (flanking), and the attacker's attack and breakthrough penalty (forest -15% up to mountain -50%)
- **River crossings** - Attacking across a river costs the divisions crossing it 30% (small) or 60% (large); crossings come from HOI4 `rivers.bmp` along province borders
- **Entrenchment** - A division standing still out of battle digs in 1 level a day, up to 5; each level gives a defender +2% attack and defence; lost on moving or attacking. Shown in the division panel
- **Manpower** - Each country's pool is the engine's `ResourceSystem` resource `manpower` (saved with the game): recruitable manpower (core population + 2% of non-core, times the mobilisation law) minus the men in its divisions, growing 1.5% of recruitable a year
- **Equipment** - Divisions track equipment per archetype (infantry, support, artillery, trucks, light tanks): what their sub-units `need` and what they have, starting at `start_equipment_factor`. A sub-unit is as strong as its scarcest equipment; strength is capped by equipment and by men. Battle losses take the men and 70% of the equipment (`EQUIPMENT_COMBAT_LOSS_FACTOR`)
- **Stockpiles and production** - One engine `ResourceSystem` resource per archetype per country, starting at the history's `add_equipment_to_stockpile` (nearly empty, as in real HOI4). Each country runs its 1936 production lines from its OOB (`add_equipment_production`): factories x 4.5 x efficiency / build cost a day, efficiency fixed. Weekly notes per country (made, delivered, stock)
- **Reinforcement** - Daily, every division short of equipment gets up to 10% of its need per archetype from the stockpile, and every division short of men up to 10% of its manpower from the pool; then strength rises to what equipment and men allow. Countries and divisions in id order. Readiness checks (AI, offensives) measure against that full strength
- **Combat factors** - Planning, terrain and river (attackers) and entrenchment (defenders) multiply per division, like HOI4; the AI's odds use the same factors
- **Retreat and overrun** - Broken defenders march to a friendly neighbouring province (a day); a retreater that arrives where an enemy stands is destroyed. Broken attackers drop their order
- **Occupation** - Entering a province held by an enemy flips its controller (not owner); occupied land is drawn in the occupier's colour, lakes follow the land around them, allies liberate land for its owner
- **Hover blocking** - No province hover or click through counters or panels

---

## Armies and Battle Plans

- **Armies** - Named groups of divisions with a fixed colour each. Made from the selection with the plus card, right-click a card to add the selection, cross to disband; an army with no divisions disappears. No general or size limit yet
- **Army cards** - Bottom-of-screen row (HOI4 leader groups): left-click selects the army's divisions, play / stop buttons above armies with a front line, animated chevrons while the plan runs
- **Battle plan window** - Shows while an army's division is selected: front line tool, offensive tool, delete plans
- **Front lines** - Hover a country to preview the border as a green dashed line with striped own provinces, click to place it in the army colour. A line covers only the stretch of border nearest the cursor: where a third country splits the border (Luxembourg between Belgium and Alsace) each side is its own line, and stretches that join as the border moves become one. Lines are split into separate fronts per enemy pocket and own pocket, traced again whenever provinces change hands, removed when the country no longer borders you
- **Holding the line** - Assigned divisions spread evenly over the front provinces and return to their spots; new army divisions join the least-loaded plan automatically; a manual order takes a division off its plan until it stops, then it goes back
- **Line labels** - "N Divs, army name" bent letter by letter along each front, scaled and hidden with the counters' zoom rules
- **Offensive plans** - With the offensive tool, enemy provinces facing the army's lines stripe yellow; right-drag picks targets (green, HOI4 magnet trail and dotted line), release places an arrow from the front into the targets plus an objective line on their far side. Click picks one province; Tab switches which front the arrow starts from; live arrow preview while placing; several offensives per army, unassigned ones drawn with HOI4's unassigned masks
- **Executing plans** - The play button sends the offensive's divisions across the line into the zone between the front and the targets, spreading the push over the zone's width. The line is retraced after each capture, so it advances as a front. Divisions attack only when rested enough and break off when worn down; the army stops once every target is taken
- **Planning bonus** - Divisions on the start line of an offensive build planning daily (blue bar in the division list), decaying while moving, fighting or on manual orders; it raises attack and breakthrough
- **Plans turn green under the cursor**
- **Simulation vs presentation** - Fronts come from the province adjacency graph with integer maths; the drawn lines and arrows (`Hoi4/PlanLine`, `Hoi4/PlanArrow`, `_Hoi4PlanTex` stripes) are presentation only, for the player's plans

---

## AI

- **Front lines** - Each AI country at war gets an (undrawn) front line per enemy it borders, holding all its idle divisions
- **Attacks** - Daily, each AI country picks its strongest bordering enemy and spreads idle, rested divisions over that enemy's border provinces. A division only attacks with good estimated odds (`Hoi4Combat.Odds`, counting only what fits the combat width), now and then an uneven fight, never a hopeless one; no more divisions go at a province than its width holds
- **Calling off** - Hourly, attacks that go badly are called off before the attackers are ground down; attackers rejoin their line afterwards
- **Deterministic dice** - Per country, province and day, so the same game makes the same choices
- **Same commands as the player** - AI orders go through the same commands, so combat, retreat and occupation apply unchanged

---

## Simulation

- **Commands** - Every player or AI decision (move, halt, leave plan, create army, add to army, place / delete battle plan, place offensive, execute plan, borrow for attack) goes through `gameState.TryExecuteCommand`: validated, logged, replayable, network-ready
- **Fixed-order tick** - `Hoi4Simulation` runs combat, then battle plans, then AI break-offs every hour, and plan checks plus new AI attacks every day
- **Determinism** - `FixedPoint64` maths, loops in id order, adjacency-based fronts, AI decides on the host only (Archon lockstep model)
- **Session recordings** - Every editor play session is recorded to `Recordings/<stamp>_<TAG>/` (newest 12 kept): divisions, plans, plan fronts, battles, control changes, commands, decision notes, log. Read with `Tools/recorder/report.py`, `unit.py` and the engine's `rows.py`
- **Scene guard** - Game registrations only run in HOI4 scenes (`Hoi4Scenes.Active`), so the engine's StarterKit scene stays clean

---

## UI

All panels follow one pattern: structure in `.uxml`, look in `*Style.uss` + `Hoi4Theme.uss`, display logic in a static Presenter, a thin View MonoBehaviour that wires events. Sprites are named in USS (`--sprite: "gfx/..."`) and loaded at runtime from the data directory, so no HOI4 art is in the project or a build.

- **Top bar** - Player flag, political power (+2/day), stability, war support, live manpower pool, date pill (click or Space to pause), speed down/up (-/+), five speed steps. Shown once a country is picked
- **State panel** - Click a land province to see its state: owner, cores, manpower, resources, victory points, buildings, categories. Appears only after a country is selected; hidden while divisions are selected; Escape hides it
- **Division panel** - Selected divisions (widened to the whole army) as cards with organisation, strength and planning bars, HOI4's entrenchment shovel and bar, icon, status, per-card close, select-half, scrollable list
- **Army cards and battle plan window** - See Armies and Battle Plans
- **Diplomacy panel** - Right-click a country's land (with no divisions selected): country, relation, Declare War / Make Peace. Opens the diplomatic map mode; X or Escape closes it and restores the previous mode. Declaring war ends guarantees and military access between the two
- **Scroll capture** - The mouse wheel scrolls panels instead of zooming the map while the pointer is over one
- **HOI4 font** - Oswald, applied to all panels

---

## Data Pipeline

`python Tools/hoi4_convert.py [--hoi4 "<install>"] [--out Hoi4Data]` (needs numpy, scipy, Pillow)

- **Provinces, definitions, terrain, heightmap, rivers** - Archon-format data directory
- **States and strategic regions** - `map/hoi4_regions.json5` (states, regions, impassable states, capitals, water)
- **Combat terrain** - Combat width, support width and attack penalty per terrain in `map/terrain.json5`; river crossings between provinces in `map/hoi4_river_crossings.json5`
- **State details** - `map/hoi4_state_data.json5`
- **Countries** - Colors, ideologies, stats (`common/hoi4_country_stats.json5`: stability, war support, core and non-core population, mobilisation law), history
- **Localisation** - Names for provinces, states, countries
- **Order of battle** - 1936 divisions
- **Terrain and water textures** - Atlases, colormaps, water cubemap, normals, border strips
- **Interface sprites** - Top bar, unit panel, state view, counters and action icons, move arrow, battle plan arrows and lines, army cards, flags
- **Own Paradox-script parser** - `parse_pdx`
- `Hoi4Data/` is gitignored and derived from the user's install; never commit or redistribute it

---

## Prototypes

Tricky map algorithms are prototyped in Python under `Tools/prototypes/<topic>/` and checked as preview PNGs before porting to C# or HLSL: borders, rivers, river crossings, labels, highlight outline, move arrows, counters, country fill, top bar, units, battle plans (front line, offensive arrow, drag).

---

## Known Gaps

- Combat ignores forts, generals, supply, night, stacking and per-unit terrain modifiers (only the terrain's base penalty applies); max entrenchment ignores the division's own entrenchment stat (engineers)
- Offensives are broad-front only; no spearhead or encirclement logic
- AI does not use offensives or armies, only odds-based border attacks
- Production is fixed to the 1936 lines: no production screen, no new lines, no efficiency growth, no factory construction, no resources; country production modifiers ignored (real Germany makes about 20% more)
- Equipment designs and variants are not modelled: an archetype is one kind of equipment, stats from the 1936 design
- No recruiting new divisions; manpower only reinforces
- Province selection does not persist after the state panel closes (HOI4 keeps it)
- Only land units exist; no air or naval
- Political power only counts up; nothing spends it
- Plan confidence indicator on army cards is always the tick
