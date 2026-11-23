# HOI4 Remade - Current Features

**Last Updated:** 2026-11-22

---

## TL;DR

**Map:** Clausewitz-style terrain, water, coasts, smooth mesh borders in HOI4 classes, rivers, country labels, haze, clouds, day/night with city lights, political / terrain / diplomatic map modes

**Units:** 1936 armies, on-map counters with action icons and battle bubbles, click and box selection, HOI4 movement rules, hourly movement, province-to-province combat with terrain, rivers and entrenchment, retreat and overrun, occupation, manpower pools, equipment stockpiles, reinforcement

**Armies and battle plans:** Armies with bottom-of-screen cards, front lines that divisions fill and hold, offensive plans (drag over targets) that advance as a broad front when executed, planning bonus

**Production:** Military factories from owned states, production lines in priority order with per-factory efficiency growth, commands for adding, sizing, reordering and deleting lines, AI that puts free factories to work, HOI4 production screen

**Construction:** Civilian factories and consumer goods from economy laws, a construction queue (civilian and military factories, infrastructure) with infrastructure speed-up and shared slots, construction AI and the construction screen

**Recruit & deploy:** Training lines per deployment order take manpower up front, get equipment after the field, train up to their equipment level and deploy at the capital or a chosen province; recruitment AI and the recruit & deploy screen

**AI:** Enemy war AI with odds-based attacks off its own front lines; production, construction and recruitment AI

**Simulation:** Every decision is a command, one fixed-order hourly tick, fixed-point maths and adjacency-based fronts (deterministic, lockstep-ready), session recordings for debugging

**UI:** Top bar, production screen, state panel, division panel, army cards, battle plan window, diplomacy panel (UI Toolkit: UXML + USS + C#), all sprites loaded at runtime from the converted data

**Data:** One Python converter turns the HOI4 install into an Archon data directory

**War and peace:** War participation, surrender progress from victory points, capitulation, a basic peace conference (take states)

**Not Implemented:** strategic resources and trade, research, politics, supply, generals, air and naval, factions, conditional surrender, saving/loading of HOI4-specific state, multiplayer in practice

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
- **Stockpiles** - One engine `ResourceSystem` resource per archetype per country, starting at the history's `add_equipment_to_stockpile` (nearly empty, as in real HOI4). Production adds to them, reinforcement takes from them. Weekly notes per country (made, delivered, stock)
- **Reinforcement** - Daily, every division short of equipment wants up to 10% of its need per archetype from the stockpile, shared with the training lines by priority (equal shares within one priority; see Recruit and Deploy), and every division short of men up to 10% of its manpower from the pool; then strength rises to what equipment and men allow. Countries and divisions in id order. Readiness checks (AI, offensives) measure against that full strength
- **Combat factors** - Planning, terrain and river (attackers) and entrenchment (defenders) multiply per division, like HOI4; the AI's odds use the same factors
- **Retreat and overrun** - Broken defenders march to a friendly neighbouring province (a day); a retreater that arrives where an enemy stands is destroyed. Broken attackers drop their order
- **Occupation** - Entering a province held by an enemy flips its controller (not owner); occupied land is drawn in the occupier's colour, lakes follow the land around them, allies liberate land for its owner
- **Hover blocking** - No province hover or click through counters or panels

---

## Production

`Assets/Game/Scripts/Production/` (wiki "Production", `NDefines.NProduction`):

- **Military factories** - A state's `arms_factory` buildings count for its owner (the country owning most of its provinces) while the owner controls more than half of them; recounted when ownership or control changes, so conquest and peace deals move factories. Day one matches real HOI4 (Germany 28)
- **Production lines** - Per country, in priority order, starting from the 1936 lines in the OOB. Each day the lines take factories top-down (requested, at most 150 per line) until the country's factories run out; a lost factory comes off the bottom and loses its efficiency, an added one starts at 10%
- **Output** - 4.5 IC per active factory x its efficiency a day; whole units come out when the line's progress reaches the equipment's `build_cost_ic`, the rest carries over, and go into the stockpile
- **Efficiency** - Per factory slot, growing by 0.001 x cap² / current a day up to the 50% cap (about 500 days from nothing)
- **First day** - Until the first game day has passed, factories move freely between the 1936 lines without losing efficiency (added ones start at the line's starting efficiency); a line made that day starts at 10% like any new line
- **Buildable equipment** - Per country and archetype, the newest equipment its 1936 technologies unlock (a tank design needs its chassis technology), written by the converter; a 1936 line's equipment always counts
- **Commands** - Add a line (bottom of the queue), set its factories, move it in the queue, delete it, all through `TryExecuteCommand`
- **Production AI** - Weekly, each country but the player's on its own weekday: free factories go one at a time to the archetype with the most need per factory already making it (what its divisions lack beyond the stockpile, plus a 10% reserve of their total use); never takes factories off a line. Host only, through the same commands
- **Production screen** - HOI4's `countryproductionlineview.gui` layout and art, opened by the top bar's production button (glows while factories are free) or Y. Resource strip (the country's own states), efficiency cap, military factories used/total, add-line buttons for infantry/artillery and armour opening the equipment picker (design name, general name, cost, picture), category filters with factory counts. Each line: priority, design name, efficiency bar relative to the cap, +/- factories (Shift x5, Ctrl x10, or the x5/x10 buttons), delete, priority arrows, picture, units a day or week with progress to the next one, what the divisions lack (reinforcing) or the stock (stockpiling), the 5x3 factory grid (x5/x10 icons past 15, grey where the line wants more than it gets) glowing while factories are free. A new line asks for the free factories, 1 to 15
- **Recording** - Weekly `production` notes (factories, free, each line's factories, efficiency, IC), `ai_assign` notes, a Production section in `report.py`, `production` and `ai_production` columns in `perf.tsv`

---

## Construction

`Assets/Game/Scripts/Construction/` (wiki "Construction", `NDefines.NProduction` / `NBuildings`):

- **Buildings** - `Hoi4Buildings`: state building levels as they are now (the 1936 history plus what was built), and the country each state's buildings work for (the owner holding most provinces while it controls more than half). Production counts military factories from here; the state panel shows it
- **Civilian factories and consumer goods** - Consumer goods take floor((civilian + military) x economy law share x (1 + consumer goods factor)), at least 10% and 1; the rest are available. Laws, factors and construction speed (overall and per building) per country from its 1936 ideas (default laws filled in) and technologies (`common/hoi4_country_economy.json5`). Matches real HOI4 Germany (15 of 63, 20 available)
- **Queue** - Per country, in order; an order holds one or more levels of a building in a state (queueing it there again adds a level) and builds them one at a time; each order takes up to 15 available civilian factories at 5 IC a day x (1 + construction speed + the building's speed) x (1 + infrastructure / 5). Civilian factory 10800, military factory 7200, infrastructure 6000 (and per-level costs where HOI4 has them). Finished levels go to the state; new military factories reach production the same day
- **Buildings** - Civilian and military factories, infrastructure, air bases, anti-air, radar, dockyards (coastal states only), synthetic refineries and fuel silos (no effect yet beyond their levels). Technology-gated ones (anti-air, radar, refinery, fuel silo) need the country's 1936 technologies, up to the level they unlock; locked ones are greyed out on the construction screen
- **Limits** - Factories need a free shared slot (state category plus extra slots from state history, at most 25, counting the queue); state buildings up to their cap (infrastructure 5, air base 10, anti-air 5, radar 6), air bases lower on islands (6/4/2); only states whose buildings work for the country; items in a state the country loses are dropped
- **Commands** - Queue levels, set an order's levels, remove, move in the queue
- **Construction screen** - Top bar button or T (shares the left side with production): civilian factories in use, owned, construction speed, consumer goods, one row per order (levels +/-, factories out of 15 over a box that animates faster with more factories, progress, priority arrows, delete), building buttons per section in HOI4's order; with one selected a province click queues it in that state, and the map shows where it fits: plain terrain, the player's states green with room and blue when full, yellow bands over states with an order that leaves room and blue bands where the orders fill the state, a soft green glow around the state under the cursor, state names when zoomed in to unit level (Archon region labels), no borders between a state's provinces while placing state-wide buildings, a box per state with the building's levels (built+queued/most), infrastructure speed bonus and shared slots in use (prototype `Tools/prototypes/construction/construction_map.py`)
- **Construction AI** - Weekly per country but the player's: keeps enough items queued for all available civilian factories (15 each, plus one): civilian factories until 1938, military factories from then or at war, in the state with a free slot and the best infrastructure; infrastructure when no slot is left
- **Recording** - Weekly `construction` notes (civilian, military, consumer goods, free, queue, built), `done`, `dropped` and `ai_queue` notes, a Construction section in `report.py`, `construction` and `ai_construction` columns in `perf.tsv`

---

## Recruit and Deploy

`Assets/Game/Scripts/Deployment/` (`NDefines.NDeployment` / `NMilitary`, `common/units` training_time; no wiki page, real 1936 German training lines in the reference workspace's `deployment.tsv` and country blocks):

- **Orders and lines** - Per country: deployment orders of a template (deploy province: none at first, HOI4's red "No location set", deploying at the capital then; the player picks a state land-connected to the capital, shown in green bands; priority low/normal/high, series amount) with training lines (Add Unit adds one). Each line trains one division at a time and, once it deploys, the next, until it has made the order's amount; infinite (HOI4's default) keeps training. Commands for new orders, more lines, the amount, cancelling a line or order, priority, location, order up/down and deploying early
- **Manpower** - A line takes the template's whole manpower when it starts (as HOI4: 9,600 men from day one); it waits while the pool is short or the men in training would pass max(100,000, 75% of the men in the field). Fewer divisions or a cancelled order give men and equipment back
- **Equipment** - Field divisions (reinforcing) and started lines share the stockpile each day: each wants up to 10% of its need of an archetype; higher priority first (the country's reinforcement priority for the field, each order's for its lines, all normal by default), within that field divisions by their template's priority (elite, normal, reserve: 1936 history has 27 reserve and 8 elite templates), equal shares of what they want within one tier, as HOI4 splits equipment between reinforcing and training
- **Training** - One day a day of the template's training time (its slowest sub-unit: infantry 90 days, support 120, armour 180), never above the line's equipment level (HOI4's max_training follows equipment), so lines wait for the factories
- **Deploying** - Divisions deploy with experience in proportion to their training, up to level 1 (DEPLOY_TRAINING_MAX_LEVEL): fully trained Trained, early Green; the division list and the recruit rows show the level (green, trained, regular, seasoned, veteran from UNIT_EXP_LEVELS; 1936 divisions from their history). Fully trained lines deploy on their own and start the next division of their series (new men and equipment, no training), or go when the series is done; early from 20% training (MIN_DIVISION_DEPLOYMENT_TRAINING) with the equipment they have. New divisions are named from the template's names group, numbered on from the 1936 divisions (Germany's next is 26, as in HOI4), at the order's province, or the capital when the country lost it
- **Recruitment AI** - Weekly per country but the player's: one line per 4 military factories (at least one) of its most common template at the capital, at most 5 new a week, keeping two divisions' men in the pool
- **Recruit & deploy screen** - Top bar button right of production, or U (shares the left side with production and construction). Equipment priority rows with red/yellow/green low/normal/high dots: reinforcements is real (field against training) and shows the field's equipment, the others are shown, not changeable, then one block per order: template icon, name, -/+ the series amount (infinity icon by default, - off there; + sets 1, then counts up, Shift: 5; - from 1 back to infinite), low/normal/high priority, deploy location (click, then a province you hold), Add Unit, deploy all, cancel; a summary row (move the order up or down, mean equipment, divisions, collapse) and a row per line (its division's name, equipment and training, yellow while stalled, its place in the series n/N or n/infinite, deploy early from 20%, cancel). Division templates window beside it: special forces cap (max(24, 5% of battalions)), templates with icons, reserve/elite chevrons (click to cycle; also on the order icons and in the division list), Train (an order of one), Edit (the division designer), Delete (with HOI4's confirmation, only while no division uses the template, in the field or in training) and the Division Designer button (a new template); decommissioning greyed out (prototype `Tools/prototypes/deployment/render_deployment.py`)
- **Recording** - Weekly `deployment` notes (lines, started, mean training and equipment, waiting for manpower, pool and cap), `order`, `deployed`, `cancel` and `ai_train` notes, a Deployment section in `report.py`, `deployment` and `ai_deployment` columns in `perf.tsv`

## Division Designer

`Assets/Game/Scripts/UI/Hoi4DivisionDesigner.cs`, `Units/Hoi4TemplateDesign.cs` (`interface/divisiondesignerview.gui`, `NDefines.NMilitary` designer defines, `common/units` sub-units, `common/technologies` enable_subunits; prototype `Tools/prototypes/designer/render_designer.py`):

- **Sub-units** - Every land battalion and support company with the designer's stats (recovery, recon, suppression, weight, supply use, reliability, air attack, entrenchment, initiative) and display name. Per country the ones it can use in 1936: unlocked by a technology it has (or not gated by any and active), and every equipment archetype it needs buildable or stockpiled (France 24, Ethiopia 9)
- **Grid** - Templates keep HOI4's brigade positions. 5 columns of up to 4 battalions (the fifth row needs a doctrine, MIN_DIVISION_BRIGADE_HEIGHT), filled in order (one open slot under a column's last battalion and in the next column, then locks), one sub-unit group per brigade; 5 divisional support companies, one of each type (not `divisional = no`); regimental support: one company under each brigade of 3 battalions (REGIMENTAL_SUPPORT_REQUIRED_BATTALIONS) whose `allowed_battalion_groups` take the brigade's group (infantry guns, fire support, AA/AT batteries, SP AA and tank destroyer support), half a company's cost; 1936's one regimental company (CZE) is read from the history. Removing closes the gaps
- **Stats** - `Hoi4Divisions.Stats`, shared with the divisions: sums (HP, attacks, defence, breakthrough, suppression, weight, supply, manpower, equipment), means over every sub-unit (organisation, recovery, reliability, initiative, hardness), slowest speed of the sub-units that affect it (not `affects_speed = no`, never regimental support: engineers no longer slow a motorized division), battalion combat width; regimental support counts in full otherwise (HOI4's scaling with brigade size isn't in the data), armour/piercing 30/40% best plus 70/60% mean, cut to one decimal as HOI4 shows them; matches HOI4's designer for France's Division Coloniale
- **Saving** - `SaveTemplateCommand`: a new template or over one (renaming it). Its divisions in the field and in training take the new layout; equipment and men past the new need go back to the stockpile and the pool, strength and organisation keep their share of the new maximum; orders follow a rename. Main type (icon, counter, unit type) from the highest-priority battalion
- **Screen** - HOI4's window over the map: template icon, name (typed), name list, reserve/normal/elite, the composition summary (hardness, organisation, HP, speed, combat width), the grid and support column (+ on an empty brigade opens the categories, HOI4's sub-unit groups with "New group" for one the template lacks, then that group's battalions; a used brigade goes straight to its group; "Cost N to add/replace" green, or red without the experience; right click removes), the regimental support row (locked), base/combat/equipment sheets, Reset, Duplicate (an unsaved copy under a free name: closing drops it), Save, the experience the changes would cost (5 a battalion, 10 a company, 20 a new brigade group; shown, not charged)

## Armies and Battle Plans

- **Armies** - Named groups of divisions with a fixed colour each. Made from the selection with the plus card, right-click a card to add the selection, cross to disband; an army with no divisions disappears. No general or size limit yet
- **Army cards** - Bottom-of-screen row (HOI4 leader groups): left-click selects the army's divisions, play / stop buttons above armies with a front line, animated chevrons while the plan runs
- **Battle plan window** - Shows while at least one selected division is in an army: front line tool, offensive tool, assign, unassign, delete tool. Assign: click a front line and the selected divisions join it, and its army if it is another one's; click an offensive and they join its line and stay on the front the offensive belongs to. Unassign: the selected divisions leave their lines at once, stop, and stay off lines until assigned again. The bin turns the delete tool on: unit counters hide, the line or offensive under the cursor turns red and a click removes it; Escape, right click or the bin again turns it off. Shift + right click on the bin removes every order of the selected army at once (all its front lines and offensives)
- **Front lines** - Hover a country to preview the border as a green dashed line with striped own provinces, click to place it in the army colour. A line covers only the stretch of border nearest the cursor: where a third country splits the border (Luxembourg between Belgium and Alsace) each side is its own line, and stretches that join as the border moves become one. Hold the right button and drag along the border to draw only part of a stretch (HOI4: the line in the army colour under the dashed preview line, a ring at each end); the partial line follows the border without creeping along it. Lines are split into separate fronts per enemy pocket and own pocket, traced again whenever provinces change hands, removed when the country no longer borders you
- **Holding the line** - Assigned divisions spread evenly over the front provinces and return to their spots; new army divisions join the least-loaded plan automatically; a manual order takes a division off its plan until it stops, then it goes back
- **Line labels** - "N Divs, army name" bent letter by letter along each front, scaled and hidden with the counters' zoom rules
- **Offensive plans** - With the offensive tool, enemy provinces facing the army's lines stripe yellow; right-drag picks targets (green, HOI4 magnet trail and dotted line), release places an arrow from the front into the targets plus an objective line on their far side. Click picks one province; Tab switches which front the arrow starts from; live arrow preview while placing; several offensives per army, unassigned ones drawn with HOI4's unassigned masks
- **Executing plans** - The play button sends the offensive's divisions across the line into the zone between the front and the targets, spreading the push over the zone's width. The line is retraced after each capture, so it advances as a front. Divisions attack only when rested enough and break off when worn down; the army stops once every target is taken
- **Planning bonus** - Divisions on the start line of an offensive build planning daily (blue bar in the division list), decaying while moving, fighting or on manual orders; it raises attack and breakthrough
- **Plans turn green under the cursor**
- **Simulation vs presentation** - Fronts come from the province adjacency graph with integer maths; the drawn lines and arrows (`Hoi4/PlanLine`, `Hoi4/PlanArrow`, `_Hoi4PlanTex` stripes) are presentation only, for the player's plans

---

## War and Peace

- **War participation** - Per country against each enemy (HOI4 defines): 0.1 per strength point of damage dealt, 0.5 per 1,000 casualties inflicted, and per province taken 0.2 per victory point plus 0.2 x its share of the state's value. A winner's participation is its share of its side's points
- **Surrender progress** - The share of a country's victory points its enemies hold, over the 80% surrender limit (provinces when it has no victory points). Shown in the diplomacy panel for both sides of a war
- **Capitulation** - At 100%, once the war is 7 days old: the loser's divisions disband, its land not yet held by a winner goes to the winner it capitulates to (the one holding its capital, else the one with the most participation), and a peace conference opens among everyone at war with it. The conference hides the rest of the UI and the unit counters until it ends
- **After the peace** - Divisions left on land their country may not enter (no war, alliance or military access) go to their capital
- **Peace conference** - Basic HOI4 version, checked against a live conference (`Tools/prototypes/peace/`): score = 1.35 x the value of the loser's states the winners control, shared by participation. Take State only, one turn, no contests. Cost = state value (10 + population / 100,000 + resources + 4 x category slots + building values) x ideology (democratic 1.3, fascist 0.9) x 0.8 when the taker controls it (x 10 when no winner does) x influence (0.7 to 1.0 by distance to the taker's capital, cores and controlled states, cheaper next to states already demanded). With the player among the winners the game pauses on the conference screen (HOI4 peaceconferencewindow.gui layout and art): Make Demands on the left (Select All, Reset), the victors on the right, cost cards on the map (click a state to demand it), the loser's states in its own colour (occupation hidden) with HOI4's broad green stripes, blue once demanded, Confirm and Exit. The AI winners then take the states they control, best value for the cost first. Taken states change owner, the rest of the occupation ends, the wars end
- **Command** - `PeaceDealCommand` carries the player's demands

## AI

- **War declarations (stress test)** - Off by default, F8 toggles it. Every 3 days each AI country with at least 2 divisions and fewer than 1 war declares war on its weakest land neighbour, if that neighbour has at most 70% of its division strength (not allies, not the player unless set in the inspector). At most 3 new wars per check. Settings on the Hoi4AiWars object

- **Front lines** - Each AI country at war gets an (undrawn) front line per enemy it borders, holding all its idle divisions
- **Attacks** - Once a day at its own hour (country id mod 24, so the work is spread over the day), each AI country picks its strongest bordering enemy and attacks its border provinces target by target (least loaded, least defended first). Each target gathers the idle, rested divisions within 3 land hops, nearest first, until the estimated odds are good (`Hoi4Combat.Odds`, counting only what fits the combat width), and the group attacks together; a group short of good odds takes the uneven fight now and then, never a hopeless one, and otherwise stays free for the next target. No more divisions go at a province than its width holds. A division next to its target goes straight in; at most 12 path searches per country per day for those further back
- **Calling off** - Hourly, attacks that go badly are called off before the attackers are ground down; attackers rejoin their line afterwards
- **Deterministic dice** - Per country, province and day, so the same game makes the same choices
- **Same commands as the player** - AI orders go through the same commands, so combat, retreat and occupation apply unchanged

---

## Simulation

- **Commands** - Every player or AI decision (move, halt, leave plan, create army, add to army, place / delete battle plan, place offensive, execute plan, borrow for attack) goes through `gameState.TryExecuteCommand`: validated, logged, replayable, network-ready
- **Fixed-order tick** - `Hoi4Simulation` runs combat, then battle plans, then AI break-offs every hour, and plan checks plus new AI attacks every day
- **Determinism** - `FixedPoint64` maths, loops in id order, adjacency-based fronts, AI decides on the host only (Archon lockstep model)
- **Session recordings** - Every editor play session is recorded to `Recordings/<stamp>_<TAG>/` (newest 12 kept): divisions, plans, plan fronts, battles, control changes, commands, decision notes, log, and `perf.tsv` (milliseconds per simulation system per game day, frame times, wars, divisions). Read with `Tools/recorder/report.py`, `unit.py`, `battles.py` and the engine's `rows.py`. Menu **HOI4 > Record Sessions** turns recording off (remembered per machine), for clean profiling: it costs about 15 ms per game day in a large war
- **Performance for large wars** - Built on the AI war stress test: one shared controller / owner snapshot per change of control (not one read per system per hour), flat state and region lookups, target-first AI attacks with a path-search budget, cheaper movement rules and counters. Each simulation step has a Unity profiler marker named like its `perf.tsv` column (`Hoi4.<step>`), visible in a normal capture
- **Scene guard** - Game registrations only run in HOI4 scenes (`Hoi4Scenes.Active`), so the engine's StarterKit scene stays clean. The StarterKit demo systems (economy, buildings, AI, province history) are switched off in `Hoi4Test`

---

## UI

All panels follow one pattern: structure in `.uxml`, look in `*Style.uss` + `Hoi4Theme.uss`, display logic in a static Presenter, a thin View MonoBehaviour that wires events. Sprites are named in USS (`--sprite: "gfx/..."`) and loaded at runtime from the data directory, so no HOI4 art is in the project or a build.

- **Top bar** - Player flag, political power (+2/day), stability, war support, live manpower pool, date pill (click or Space to pause), speed down/up (-/+), five speed steps. Shown once a country is picked
- **State panel** - Click a land province to see its state: owner, cores, manpower, resources, victory points, buildings, categories. Appears only after a country is selected; hidden while divisions are selected; Escape hides it
- **Division panel** - Selected divisions (widened to the whole army) as cards with organisation, strength and planning bars, HOI4's entrenchment shovel and bar, icon, status, per-card close, select-half, scrollable list
- **Army cards and battle plan window** - See Armies and Battle Plans
- **Diplomacy panel** - Right-click a country's land (with no divisions selected): country, relation, Declare War / Make Peace. Opens the diplomatic map mode; X or Escape closes it and restores the previous mode. Declaring war ends guarantees and military access between the two
- **Scroll capture** - The mouse wheel scrolls panels instead of zooming the map while the pointer is over one
- **Production screen** - See Production
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

Tricky map algorithms are prototyped in Python under `Tools/prototypes/<topic>/` and checked as preview PNGs before porting to C# or HLSL: borders, rivers, river crossings, labels, highlight outline, move arrows, counters, country fill, top bar, units, battle plans (front line, offensive arrow, drag), peace conference (costs and score checked against a live HOI4 conference, map stripes), production screen, recruit and deploy screen, division designer (stats checked against HOI4's).

## Analysis and Reference Tools

- **Build check** - `Tools/check_build.py` compiles the game layer (or one engine project) with `dotnet build`, without the editor
- **Our numbers** - `Tools/manpower_pools.py` (starting manpower pools), `Tools/equipment_outlook.py` (equipment shortfall, stockpile and production per country)
- **Real HOI4** - `Tools/reference/`: refill rates from real-game division snapshots, entrenchment from a plain-text save, wiki page text

---

## Known Gaps

- Combat ignores forts, generals, supply, night, stacking and per-unit terrain modifiers (only the terrain's base penalty applies); max entrenchment ignores the division's own entrenchment stat (engineers)
- Offensives are broad-front only; no spearhead or encirclement logic
- AI does not use offensives or armies, only odds-based border attacks
- The division designer has no division equipment window, name list choice, 3D model, terrain adjusters or army experience (costs are shown, not paid); sub-unit availability is 1936's (no research)
- Recruit and deploy has no equipment priorities, no training experience or exercises, no special forces cap, and deployment orders aren't saved
- Construction has no trade, repairs, conversion, province buildings (forts, naval bases, railways), slot bonuses from technology or law changes
- Production has no strategic resources or trade, no factory construction, no factory output modifiers (real Germany makes about 17% more per factory), no civilian factories; air and naval lines are not converted, so their factories end up free (the AI puts them on land equipment)
- Equipment designs and variants are not modelled: an archetype is one kind of equipment, stats from the 1936 design
- No recruiting new divisions; manpower only reinforces
- Province selection does not persist after the state panel closes (HOI4 keeps it)
- Only land units exist; no air or naval
- Political power only counts up; nothing spends it
- Peace conferences are one turn with Take State only: no puppets, liberation, government change, contests, compliance or war goals; war participation ignores air and naval and the attacker-and-winner bonus
- Plan confidence indicator on army cards is always the tick
