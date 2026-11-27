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
9. [x] **Reserves by sea** (2026-11-27, compiles, not yet play-tested): `ShipReserves`, daily after the country layer. A landmass whose theatres need more divisions than it has (those already on their way counted) gets idle reserve divisions from landmasses with more than their theatres need, shipped by naval transfer to its port nearest the theatre with the largest need; at most max(2, a twentieth of the army) orders a day; note `ai_country` / `ship`. Budgets stay per landmass. Known gaps: divisions in armies are never released to go overseas (only reserves are donors, so mostly landmasses without a front give); a landed division joins an army only within MaxTransferHops of its section or when the army is below half its budget; no theatre is opened where the country has no front of its own (no naval invasions, no fighting from an ally's land). First recording (`Recordings/20261127_114535_CUB`, to 1936-06): 246 divisions ordered by 16 countries, 223 went to sea, 111 reached their port (median 22 days) and 86 of those joined an army. Found: armies took divisions on their march to the quay, released them at sea and they stood there for good (8 at the end, 6 of them Belgium's whole reinforcement for the Congo). Fixed the same day: divisions being shipped are out of the armies' reach until they land (`shipping`), and any division standing still at sea sails to the nearest port daily (`HaltUnitsCommand.RescueStranded`).
10. [x] **Secondary theatres attack** (2026-11-27, compiles, not yet play-tested): from `Recordings/20261127_122243_CUB`, 1938-06: Germany against Italy had its main effort on a 2-province front with 9 divisions while 51 and 40 divisions on its two other Italian fronts held for months, and empty provinces touching the line with one province were never entered. Now a theatre that isn't the main effort attacks once it has `OverwhelmRatio` (2) times the enemy divisions facing it, and a holding front walks into any undefended province (`Hoi4EnemyAI`), not only into bulges.
8. [x] **Supply awareness** (2026-11-24): theatre budgets capped by what the front's supply feeds (hub supply plus state pools over the average division need; HOI4's AI_FRONT_DIVISIONS_PER_SUPPLY_POINT 1.0, at least one per province), the excess to theatres with room, then reserve; starving reserve divisions walk daily to fed provinces off the front (`RestReserves`). Found in `Recordings/20261124_163531_CUB`: the Soviets put 33 divisions in Qandahar (0.3 supply) with no cap. hoi4-custom-ai learned each area's limit from its divisions' supply; here the game knows the supply, so the cap is computed.

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

#### Second play test (`Recordings/20261123_130713_CUB`, to 1942-02)

- Committed before it as a checkpoint: engine aa43f46, game 2fcf202.
- **Churn still high:** the Soviets had 5,813 joins, 3,565 releases and 167 disbands; China had 2,950, 1,963 and 66.
  Causes, and the fixes:
  - **Theatres re-cut daily** (one front in 3-4 changing pieces). Fix: stretches within MergeHops (4) are one
    theatre, slivers join one up to SliverHops (8) away.
  - **Budgets follow the front's length day to day.** Fix: release only when more than a quarter over budget (at
    least 2); fetch divisions from far only when an army is below half its budget.
  - **Landmasses flickered** because enemy divisions in our land split them. Fix: `Landmasses` splits only on
    enemy-controlled land.
  - **Big idle reserves:** a cap of 2 per front province left 103-134 of 235 Soviet divisions out of any army.
    Fix: the cap is 3.
- **Lag: plans cost 187 ms per game day** (44 before armies). Of that, line refresh was 121 ms (38 before) and the
  country layer about 50 ms. Cause: every army's line against an enemy traced and split the same border again. Fix:
  `FrontsAgainst` and `Stretches` are memoised within a refresh and within an AI run (`BeginFrontsMemo`).

#### User feedback after the second test (2026-11-23)

- "Our AI is definitely very aggressive. Fronts never really stabilize." Fix: posture. Only the main effort attacks,
  and only while its armies have AttackRatio (1.5) times the enemy divisions facing it. Holding fronts take only
  bulges into their line (target next to 2+ of our provinces) at 2:1 odds or empty (`Hoi4EnemyAI.StrongOdds`,
  `IsAttackFront`).
- "Capping armies at 2 divisions per front is arbitrary... How many units to fill the line? How many troops does my
  enemy have?" Fix: a theatre's need is max(front provinces, enemy divisions facing it), the main effort's
  max(front provinces, 1.5 x facing). The budget is each its need, the spare shared in proportion, or when short
  the same fraction each. `PerProvinceCap`, `HoldProvinces` and `HoldRatio` are gone.
- German tanks bounced between a taken province and their old slot. Fix: in `DistributeInner` a division standing
  still on a front province of its line keeps that province instead of its previous slot.

#### Third test (`Recordings/20261123_133326_CUB`): Germany against the Soviets

- User: "most german troops are now going up to scandinavia while russians are in the heartland of Germany. Core
  territory should be valued more... I don't want the enemy to have a good path to my homeland."
- **Bug found first:** from 1943-02-07 to 03-02 Germany's central front (46-81 provinces) was "overseas" with
  3 divisions while Finland got 134. Fronts facing the same enemy province were joined across landmasses, and the
  theatre's landmass came from its first province: a cut-off piece took the main front with it. Fix: that join
  only within a landmass.
- **Defence weight** (prototype `Tools/prototypes/ai_armies/defence.py`, render on 1943-03-04): the mirror of the
  attack route cost, the enemy's cheapest route from a front to our core victory points over land we control.
  Germany: central front 3.6 (danger 0.74), East Prussia 4.7 (0.68), Alps 8.6 to 13.6 (0.54 to 0.42),
  Finland 51 (0.16), cut-off pieces none (0). `danger = HomeScale / (HomeScale + cost)`, HomeScale 10; a front's
  need is max(provinces, facing x (0.5 + danger)), so 0.5x far from home to 1.5x at the core; the attack score
  times 1 + danger.

#### Fourth test (`Recordings/20261123_135230_CUB`, to 1942-05)

- **Better:** holding (last 90 days) is Soviets 71% (was 20%), and China, Germany and Japan 100%; organisation is
  98-100%.
- **Churn worse:** the Soviets had 9,570 joins, 5,973 releases and 239 disbands. With about 6 divisions per front
  province, a section a province longer or shorter swung an army's budget by around 5: one army released and its
  neighbour on the same line took them back. Fix: release per theatre (only when the theatre is over its budget);
  joins fill a theatre only up to its budget.
- **Open, stacking:** with no cap, all spare divisions go to the existing fronts. China had 170 on a 4-province
  front against Spain (about 43 per province), the Soviets 143 on 27 provinces. Candidate rule: a front province
  takes only what its battles can use (HOI4 combat width by terrain over division width, plus a reserve line); the
  rest waits in reserve behind the most dangerous front.

#### Performance pass (2026-11-23)

- **Line drawing:** `BorderWith` scanned all ~43,000 border arcs with two controller lookups each, for every drawn
  line (about 90% of a line refresh). Arcs are now indexed per hour by controller pair.
- **Line mesh:** rebuilt every frame while the simulation ran (10 ms a frame with AI lines shown). Simulation changes
  now redraw at most every 0.25 s of real time; lines the simulation moved are traced at that redraw (`TraceDirty`),
  not in the simulation step.
- **Landmasses:** flood fill from the country's own land only.
- **Country layer, weekly** (user: "we shouldnt run more than we have to"): `RunCountry` decides. `PlanCountry` (the
  old daily run) runs weekly or early (first plan, other enemies, front +-25%, an army gone, over a tenth of the front
  beyond every section's CarryHops reach). On other days `FollowCountry` carries the sections onto today's front
  (`CarrySections`), then updates army lines, membership and posture with the plan's theatres and budgets. Route
  costs are cached for a plan's lifetime (7 days). The theatres note says why it planned, and report.py counts plans
  by reason.
- **Measured after** (`Recordings/20261123_170848_CUB`, 84 war days, ~47 wars, ~1,000 divisions): sim 110 ms a game
  day (was ~250), plans 56 (was ~200), line refresh 18 (was ~100), country layer 32, frame average 31 ms (was 62).
  Plans: 364 weekly, 236 front beyond every section, 194 other enemies, 121 front +-25%, 85 first. Then: between plans
  a front province goes to a section within FollowHops (8; the plan carries CarryHops 4), and the army lines are
  skipped when no section changed (the hourly refresh follows the border within a section).

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
