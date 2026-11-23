# Layered war AI: plan

Status: **steps 1-3 and the debug toggle implemented, untested in play** (2026-11-23). Update the checkboxes and the "State" notes as
work lands, so a later session can pick up from here.

## Why

The AI's war code had three systems that each did part of the others' job, and that caused most of the bugs:

- **Holes, stray groups, back-and-forth.** The attack AI (`Hoi4EnemyAI`) picked its own attackers from up to 3-5 provinces away, and they were often the line's own holders. This was fixed by attacking only from the line, but the attack AI still owns targets that the plans should own.
- **Italy's whole army walking to Persia.** The strategy targets were set per enemy, not per area, so a beaten enemy's last 3 provinces claimed the whole army.
- **Stuck marching divisions.** No layer owned divisions that had left a line.

The player's battle plans (army front lines with a section of the border, offensives with a zone, the play button, `Advance`) already work the way the AI should fight: the line attacks across itself and moves forward as a whole. The AI never used them.

## Decisions (user, 2026-11-23)

| Question | Decision |
|---|---|
| Plan model | **Same as the player.** AI armies get the player's kind of front line (a section of the border), offensives, and the play button. The special AI lines (`IsAI` per-enemy whole-border plans, `EnsureAIPlans`, `AutoAssignAI`, `PickAILine`, per-line `Target`, `Rebalance`, `ReleaseExcess`, `homePlan`) go away. |
| Army size | **24 divisions**, HOI4's army limit. A long theatre gets several armies side by side, each holding its own stretch. |
| Show AI plans | **Debug toggle**, off by default. A HOI4 menu item draws every AI army's lines and arrows in its colour. |
| Scheduler | **Game side for now.** Fixed cadences, staggered by country id. Move it to the engine (`Core.AI.AISystem`, which has goals and distance tiers but no layers) later if it proves generic. |

The user also asked: "The AI also needs to build armies and use offensive lines for planning."

## The three layers

Each layer writes only its own outputs. It reads the layers below as reports and never reaches down to order units itself.

### 1. Country layer: theatres, postures, budgets, armies

- **When:** weekly per country (day % 7 == country id % 7), plus the next day after a change of event. The events are a war declared or ended, a capitulation, or the set of theatres changing (checked daily with a cheap signature: the bordered enemies plus the number of theatres).
- **Theatres:** all of the country's front provinces against every enemy it is at war with and borders, impassable excluded. They are grouped into connected stretches through land adjacency **across enemies**, so one theatre can face two enemies. A pocket of ours or an enemy pocket stays part of the theatre it touches; a separate stretch is a separate theatre.
- **Per theatre:**
  - **Holding need:** max(fronts, ⌈provinces/3⌉, ⌈0.8 × enemy divisions within 2 hops⌉). This is the current Strategy rule.
  - **Score:** (0.25 + the enemy's surrender progress) × 10 / (1 + route cost). The route cost is the cheapest route from the theatre on to the victory points of an enemy's main body (`RouteCosts`); a theatre facing several enemies takes the best one. No route gives 0: the theatre only holds.
  - **Posture:** the main effort is the best score. It is committed for 60 days and a switch needs 1.5× the score (current rules). The main effort's posture is **attack**. Every other theatre **holds**; it is later allowed to attack when its odds are good (a step-3 feedback item).
  - **Budget:** holding needs first. The surplus goes to the main effort up to 2 per front province, then the overflow to the next best scores the same way. What is left is **reserve** (no army). When short of divisions, each theatre gets its share of its need.
- **Armies:**
  - **Count:** a theatre gets ⌈budget / 24⌉ armies.
  - **Sections:** the theatre's front provinces are ordered along the front (end-to-end walk, as in `FrontsAgainst`) and split into that many contiguous chunks of near-equal province count. Each army's section is one chunk and its budget is in proportion to its chunk.
  - **Stable ids:** an existing army is matched to the chunk overlapping its old section most. New armies are created as needed and empty ones disbanded.
  - **Membership:** a division keeps its army while the army has room. Surplus divisions (the furthest from their section first) leave to reserve and stop where they are. Armies short of divisions take the nearest reserve or surplus division within MaxTransferHops (20, landed from the needy section, as `Rebalance` does), at most max(2, total/20) a day. New divisions join the army furthest under budget in reach; when there is none they are reserve.
- **Notes:** `ai_country` with kinds `theatres`, `main_effort` and `armies` (sizes and budgets), plus `transfer` and `reserve`.

#### Prototype results (`Tools/prototypes/ai_armies/theatres.py`, 2026-11-23)

The prototype ran on `Recordings/20261123_121736_CUB`: Italy and the Soviets, 24 days from 1938-01-20, stepping every 2–3 days. It changed the design in five ways:

- **Carry sections by nearness, not by exact province.** After 24 days only 16 of Italy's front provinces were still front provinces, so exact carrying lost the armies. Each new front province takes the army of the nearest previous front province, up to CarryHops (4) land hops through any passable land, ties going to the lower army id. Provinces still without an army join the nearest army along the front (the theatre's links).
- **Adjust instead of re-splitting.** When a theatre needs fewer armies, the smallest army is merged away. When it needs more, the army with the most provinces is split in half along the front order. Re-splitting evenly churned the army ids whenever theatres joined or split.
- **One stretch per army.** Each army keeps its largest connected run of provinces; islands go to the surrounding army. Carrying by nearness had left islands.
- **Whole armies move between theatres.** Armies merged away or left without a theatre go to a pool, and a theatre short of armies takes the nearest pooled army before creating one. The limit is TransferKm (1500 km between the army's old section centre and the theatre's centre). Beyond that the pooled army is disbanded where it stands and its divisions go to reserve. Without the limit, a main-effort switch marched three Soviet armies 12,000 km.
- **A pocket theatre** (not in the piece of our land with the capital) is out of the budget, and its budget is the divisions already inside it. Membership transfers only between provinces with the same walk label (`WalkLabels`), so nobody is sent into a pocket.

Measured: a section's centre moves a mean 47–64 km per step when carried, against 99–102 km for a fresh even split. Army ids hold for the whole run except where theatres really join or split.

**Cadence change:** the country layer runs daily at the country's hour, so sections follow the moving front. The main effort keeps its 60-day commitment and 1.5× switch rule, so a daily run doesn't make the posture flip-flop.

### 2. Strategic layer: front lines and offensives per army

- **When:** daily per country, at its own hour (country id % 24, as attacks are now), and when that army's line moved.
- **Front lines:** one plan per enemy that the army's section faces, the same kind of plan as the player's (`CreatePlan` with a Section). Section = the chunk's front provinces against that enemy; Outside = the enemy's other front provinces, so the hourly refresh follows the border without creeping into a neighbouring army's stretch. Sections are re-set from the chunk each day.
- **Offensives:** armies of an attacking theatre place one offensive each.
  - The target is reached by stepping from the section's best enemy-adjacent province along falling route cost (`RouteCosts`) for OffensiveDepth (about 4) hops; that province and its enemy neighbours on the route are the targets.
  - The offensive starts from the army's front facing that piece (`CreateOffensive`).
  - Once its targets are taken (`PruneOffensives` removes it), a new one is placed the next day.
  - The army's play button goes on (`SetExecuting`). Holding armies have no offensive and aren't executing.
- **Notes:** `ai_strategic` with kinds `line`, `offensive` and `execute`.

### 3. Tactical layer: per division, hourly

- `Hoi4EnemyAI` keeps the odds-based attack choice, gathering only divisions next to the target, plus break-off and breakout. Its targets become:
  - **inside an executing offensive's zone** (`OffensiveZone`): taken in route-cost order, with the bold fight allowed (today's "main effort" rules);
  - **elsewhere:** only good odds or undefended provinces. This is local line straightening; it doesn't advance the line.
- `Advance` (the player's offensive mover, which attacks without odds) **skips AI owners**, so there is one attack decider per division.
- Retreat, pockets and breakout stay as they are.

## Code changes (file by file)

- [x] `Hoi4Armies.cs`: `Hoi4Army.Owner`; `Create(owner, units, name)`. The player's UI (`Hoi4ArmyPanel`, selection, commands) lists only the player's armies. AI armies are named "1st Army" and so on per country (numbering per owner).
- [x] `Hoi4BattlePlans.cs`:
  - remove `EnsureAIPlans`, `AutoAssignAI` and the IsAI keep-rule (every plan keeps divisions of its army);
  - `IsAI` now means "owned by an AI country": not drawn unless debug, and not usable by the player's tools or commands;
  - add `IsDrawn(plan)` = Owner == Player || showAIPlans, and use it for TraceLine, the mesh, labels and `dirtyMesh`;
  - `Changed` fires only for drawn plans (perf: the UI shouldn't rebuild for AI churn).
- [ ] `Hoi4BattlePlans.Offensives.cs`: drop the `!IsAI` guards in IsOffensiveTarget, HasFrontLine, DirtyArmy, IsAdvancing, OnLineOf, OffensiveFrontList, AssignOffensives and RemoveOrphanedOffensives. `Advance` skips AI owners. Expose `OffensiveZones(country)` (the union of zones of its executing offensives) for the tactical layer. Draw AI arrows only when IsDrawn.
- [x] `Hoi4BattlePlans.Strategy.cs`: replaced by `Hoi4BattlePlans.AICountry.cs` (theatres, budgets, armies, transfers) and `Hoi4BattlePlans.AIStrategic.cs` (lines, offensives, execute). Keep `RouteCosts`, `TryRouteCost` and `FacingDivisions`, generalised from a plan to a province set plus enemies, along with the main-effort hysteresis. `MainEffort(country)` returns the main theatre's main enemy (used by `Hoi4EnemyAI` until it reads zones).
- [ ] `Hoi4EnemyAI.cs`: zone targets instead of `main`; no `BorrowForAttackCommand` needed (an attacking division keeps its slot; check that `Hold` leaves AI attackers alone, using `borrowed` or an equivalent).
- [x] Debug toggle: menu **HOI4 > Show AI Plans** (an EditorPrefs-backed static, like Record Sessions). It sets `showAIPlans` and re-traces.
- [ ] `Hoi4Recorder.cs`: plans.tsv gets the army column for AI plans (already has `army`); add an `armies.tsv` weekly (owner, army, theatre, budget, divisions, section size, posture)? Decide when building.
- [ ] `Tools/recorder/report.py`: the Strategy section reads `ai_country` notes; add theatres and armies per country. Activity stays.
- [ ] Docs: `CURRENT_FEATURES.md` (replace the Strategy, Attacks and Encirclement entries), the class doc comments, and `CLAUDE.md` (mention this doc while it's in progress).

## Order of work

1. [x] **Armies with owners**, and the player's UI filtered to the player's armies. Compile.
2. [x] **Plans:** remove the IsAI-special lines, add IsDrawn, and make offensives AI-capable. At this point AI countries have no lines, so this step must land together with step 3.
3. [x] **Country layer:** theatres, budgets, armies and transfers. Strategic layer: lines per army with sections. Compile. Check in play that AI countries still hold fronts (a recording: plans per army, Activity).
4. [ ] **Offensives:** the strategic layer places them and executes; the tactical layer reads zones and `Advance` skips AI. Check in a recording that main-effort armies push with a coherent line.
5. [x] **Debug drawing toggle.**
6. [ ] **Report and docs.**
7. Later: feedback upward (stalled offensives and front pressure change budgets), a fast-army breakthrough role, strategic redeployment by rail for long transfers, and moving the cadence scheduler to the engine.

## Invariants to keep

- **Determinism:** game time only, id-ordered loops, integer or FixedPoint maths, no float positions in decisions (the drawn path is presentation and must never feed the simulation; see the note in `Hoi4Offensive`).
- **No AI churn in the UI:** AI plan changes must not fire `Changed` or rebuild the mesh unless they're drawn.
- **One owner per division decision:** the country layer owns army membership, the strategic layer owns slots (through plans) and offensives, and the tactical layer owns attack and break-off orders.
- **Scene guard:** `RuntimeInitializeOnLoadMethod` spawners start with `if (!Hoi4.Hoi4Scenes.Active) return;`.
- **Performance:** country layer weekly, strategic daily per country at its own hour, tactical hourly but only neighbours. Watch `perf.tsv` ("plans" and "ai" columns) against the pre-change recordings.

## Findings so far (from recordings, 2026-11-23)

- 70% of AI attacks started 2+ provinces from their target, 20% at 4+ (`Recordings/20261123_120213_CUB`), which led to the line-only attacks.
- Real HOI4: a full division that moved a province has a median 97-98% organisation the next day, so recovery continues while moving. This is implemented.
- Italy: a dissolved line's 43 divisions kept marching to Persia, and a single line's target was the whole army. Both fixed with the caps, reserves and stale-march stop, which this plan supersedes with army budgets.
- Italy's Greek land counted as "encircled" because it is cut off from Italy by land. Pockets now need no coast and no neutral or allied neighbour.

#### First play test (`Recordings/20261123_125606_CUB`, 407 days)

- **Good:** organisation stayed at 89-100% (was 2-32%), the Soviets held 53% of the time (were marching 94%), and the
  big armies filled to their budgets.
- **Bug, pockets:** any theatre not walkable from the capital was a "pocket" budgeted with the divisions in its
  piece of land, and two theatres on the same piece counted them twice. Japan's whole China front and Italy's
  African fronts were "pockets", and Italy's main effort against Poland (93 provinces) got a budget of 3.
  **Fix:** budgets are per walkable landmass (its own divisions, its own main effort; the main armies and the
  day they became main are kept per army, `MainSince`).
- **Churn:** Poland (30 divisions) had 1,066 joins, 644 releases and 46 armies disbanded in 400 days. **Fixes:** a
  release only when an army is over its budget by more than a tenth (at least 2); fronts under 4 provinces join the
  nearest theatre on their landmass within 6 hops (`MinTheatre`, `MergeHops`), instead of a one-division army each.
- **Duplicate army names** (two "5th Army"): fixed, the lowest free number is used.

## State

- Done (compiles, untested in play): armies have an owner (the player's UI lists only the player's); the IsAI-special
  lines, `EnsureAIPlans`, `AutoAssignAI` and the old Strategy file are gone; `Hoi4BattlePlans.AICountry.cs` holds the
  country layer and the army lines (per army and enemy, Section/Outside); `UpdateAI` runs hourly for the countries
  whose hour it is, before the no-plans return in `SimulateHour`; membership releases over-budget divisions and fills
  from reserve (near first, then any walkable distance); player commands check army ownership; menu HOI4 > Show AI
  Plans; report.py "War AI" section reads `ai_country` notes.
- Still to do: step 4 (AI offensives, tactical layer on zones, `Advance` skipping AI), the offensive drawing for AI,
  `armies.tsv` (not added: the `ai_country/theatres` note carries the same per day), docs pass after play-testing.
- Everything since 0d1c1fa is uncommitted (engine adjacency overrides and LegTicks included).
