"""Read a recording and point at what looks wrong. Start here; dig in with rows.py and unit.py.

    python report.py [REC] [--country TAG] [--stuck HOURS] [--top N]

Sections: overview, log (warnings and errors), plans (divisions not on the line), stuck and thrashing divisions,
player armies with divisions outside their plan, AI decisions and how the attacks ended, combat losses, territory,
commands. Every finding prints division / plan ids so it can be followed up with unit.py ID.
"""
import argparse
import re
from collections import Counter, defaultdict

import _engine  # noqa: F401  (engine tools on the path, Recordings/ as the root)
import rec


def hours(n):
    return f"{n / 24:.1f}d" if n >= 48 else f"{n}h"


def main():
    rec.setup_stdout()
    ap = argparse.ArgumentParser()
    ap.add_argument("rec", nargs="?")
    ap.add_argument("--country", help="only this country's divisions, plans and AI")
    ap.add_argument("--stuck", type=int, default=24, help="hours idle off its slot before a division counts as stuck")
    ap.add_argument("--top", type=int, default=12)
    a = ap.parse_args()
    path = rec.find(a.rec)
    m = rec.meta(path)
    player = m.get("player", "")
    only = a.country

    _, divs = rec.load(path, "divisions")
    _, plans = rec.load(path, "plans")
    _, notes = rec.load(path, "notes")
    _, battles = rec.load(path, "battles")
    _, control = rec.load(path, "control")
    _, cmds = rec.load(path, "commands")
    _, log = rec.load(path, "log")
    # a session stopped mid-write can leave a cut-off last row: keep only complete ones
    divs, plans, notes, battles, control, cmds, log = (
        [r for r in f if r.get("tick", "").isdigit() and all(v != "" for k, v in list(r.items())[:3])]
        for f in (divs, plans, notes, battles, control, cmds, log))
    last_tick = max([int(r["tick"]) for f in (divs, plans, notes, battles, control, cmds, log) for r in f[-1:]] or [0])
    first_tick = int(m.get("start_tick", 0))
    end_date = next((f[-1]["date"] for f in (divs, notes, battles, plans) if f), "?")

    print(f"# {path}")
    print(f"player {player or '(none)'}, {m.get('start', '?')} -> {end_date} ({hours(last_tick - first_tick)} of game time)")
    print(f"rows: divisions {len(divs)}, plans {len(plans)}, battles {len(battles)}, notes {len(notes)}, "
          f"control {len(control)}, commands {len(cmds)}, log {len(log)}")

    # --- log ---------------------------------------------------------------------------------------
    print("\n## Log")
    if not log:
        print("no warnings or errors")
    else:
        groups = Counter()
        first = {}
        for r in log:
            key = (r["level"], re.sub(r"\d+", "#", r["message"])[:160])
            groups[key] += 1
            first.setdefault(key, r)
        for (level, msg), n in groups.most_common(a.top):
            f = first[(level, msg)]
            print(f"{n:6}x {level:9} first {f['date']}: {f['message'][:200]}")

    # --- division timelines --------------------------------------------------------------------------
    cur = {}           # id -> row
    since = {}         # id -> tick the current (province, slot, state) began
    stuck = []         # (hours, id, row) idle off its slot
    dest_changes = defaultdict(Counter)  # id -> day -> redirects
    player_army_plans = defaultdict(set)  # army -> plan ids alive (from plans rows)
    gone = {}

    def close(uid, now):
        r = cur.get(uid)
        if r and r["plan"] != "0" and r["slot"] != "0" and r["province"] != r["slot"] and r["state"] == "idle":
            dur = now - since[uid]
            if dur >= a.stuck:
                stuck.append((dur, uid, r, since[uid]))

    for r in divs:
        uid, t = int(r["id"]), int(r["tick"])
        if only and r["tag"] != only:
            continue
        if r["why"] == "gone":
            close(uid, t)
            gone[uid] = r
            cur.pop(uid, None)
            continue
        old = cur.get(uid)
        if old is None or (old["province"], old["slot"], old["state"], old["plan"]) != (r["province"], r["slot"], r["state"], r["plan"]):
            if old is not None:
                close(uid, t)
            since[uid] = t
        if old is not None and r["dest"] != "0" and r["dest"] != old["dest"] and old["dest"] != "0":
            dest_changes[uid][t // 24] += 1
        cur[uid] = r
    for uid in list(cur):
        close(uid, last_tick)

    # --- plans ---------------------------------------------------------------------------------------
    print("\n## Plans")
    by_plan = defaultdict(list)
    for r in plans:
        by_plan[int(r["plan"])].append(r)
    shown = 0
    problems = []
    for pid, rows in sorted(by_plan.items()):
        live = [r for r in rows if r["owner"] != "removed" and r["owner"] != ""]
        if not live:
            continue
        head = live[0]
        if only and head["owner"] != only:
            continue
        if head["ai"] == "0":
            player_army_plans[head["army"]].add(pid)
        # Hours with divisions away (idle, not on their slot) or unslotted, from the change rows
        away_h = unslot_h = 0
        worst_away = (0, None)
        for i, r in enumerate(rows):
            if r["owner"] in ("removed", ""):
                continue
            nxt = int(rows[i + 1]["tick"]) if i + 1 < len(rows) else last_tick
            dur = nxt - int(r["tick"])
            if int(r["away"] or 0) > 0:
                away_h += dur
                if dur > worst_away[0]:
                    worst_away = (dur, r)
            if int(r["unslotted"] or 0) > 0:
                unslot_h += dur
        end = rows[-1]
        removed = end["owner"] == "removed"
        lifetime = (int(end["tick"]) if removed else last_tick) - int(head["tick"])
        peak = max(int(r["divisions"] or 0) for r in live)
        last = live[-1]
        line = (f"plan {pid:4} {'AI ' if head['ai'] == '1' else 'you'} {head['owner']} vs {head['country']}"
                f"{' army ' + head['army'] if head['ai'] == '0' else ''}: {hours(lifetime)}{' (removed)' if removed else ''}, "
                f"divisions {last['divisions']} (peak {peak}), last: {last['holding']} holding {last['marching']} marching "
                f"{last['away']} away {last['unslotted']} unslotted, fronts {last['fronts']}, {len(live)} changes")
        bad = away_h >= a.stuck or unslot_h >= a.stuck
        if bad:
            problems.append(line + f"\n      ! divisions away from the line for {hours(away_h)}"
                            + (f" (longest {hours(worst_away[0])} from {worst_away[1]['date']}: {worst_away[1]['away']} away)" if worst_away[1] else "")
                            + (f", unslotted for {hours(unslot_h)}" if unslot_h else ""))
        if head["ai"] == "0" or bad:
            if shown < a.top or bad:
                print(line if not bad else problems[-1])
                shown += 1
    ai_plans = sum(1 for rows in by_plan.values() if rows and rows[0]["ai"] == "1")
    print(f"{len(by_plan)} plans ({ai_plans} AI); {len(problems)} with divisions off the line for {a.stuck}h or more")

    # --- stuck and thrashing --------------------------------------------------------------------------
    print(f"\n## Divisions idle off their slot for {a.stuck}h or more")
    stuck.sort(key=lambda s: -s[0])
    if not stuck:
        print("none")
    for dur, uid, r, start in stuck[: a.top]:
        print(f"{uid:6} {r['tag']} plan {r['plan']}: idle at {r['province']} for {hours(dur)} from tick {start}, its slot is {r['slot']}")
    if len(stuck) > a.top:
        print(f"... {len(stuck) - a.top} more")

    print("\n## Divisions redirected many times in one day (4+)")
    thrash = sorted(((n, uid, day) for uid, days in dest_changes.items() for day, n in days.items() if n >= 4), reverse=True)
    if not thrash:
        print("none")
    for n, uid, day in thrash[: a.top]:
        tag = (cur.get(uid) or gone.get(uid) or {}).get("tag", "?")
        print(f"{uid:6} {tag}: {n} new destinations on day {day}")

    # --- player armies outside their plan ----------------------------------------------------------------
    print("\n## Your army divisions without a plan while their army has one")
    found = 0
    state = {}
    for r in divs:
        if player and r["tag"] != player or r["why"] == "gone":
            continue
        state[int(r["id"])] = (int(r["tick"]), r)
    for uid, (t, r) in sorted(state.items()):
        if r["army"] != "0" and r["plan"] == "0" and player_army_plans.get(r["army"]) and not r["state"].startswith("manual"):
            print(f"{uid:6} army {r['army']} at {r['province']} ({r['state']}) since tick {t}: not in plan(s) {sorted(player_army_plans[r['army']])}")
            found += 1
            if found >= a.top:
                break
    if not found:
        print("none at the end of the recording")

    # --- AI ------------------------------------------------------------------------------------------------
    print("\n## AI")
    ai = [r for r in notes if r["system"] == "ai" and (not only or r["country"] == only)]
    kinds = defaultdict(Counter)
    for r in ai:
        kinds[r["country"]][r["kind"]] += 1
    if not ai:
        print("no AI decisions recorded (no AI country at war?)")
    else:
        print(f"{'country':8} {'attack':>7} {'bold':>6} {'hold':>7} {'brkoff':>7} {'nofront':>8}")
        for c, k in sorted(kinds.items(), key=lambda kv: -(kv[1]["attack"] + kv[1]["attack_bold"]))[: a.top]:
            print(f"{c:8} {k['attack']:7} {k['attack_bold']:6} {k['hold']:7} {k['break_off']:7} {k['no_front']:8}")

        # How each attack ended: the target taken, the division broke off, retreated, died, or nothing yet
        taken = defaultdict(list)  # province -> ticks it changed to tag
        for r in control:
            taken[(r["province"], r["to"])].append(int(r["tick"]))
        unit_notes = defaultdict(list)
        for r in notes:
            if r["unit"] != "0":
                unit_notes[r["unit"]].append(r)
        died = {str(uid): int(r["tick"]) for uid, r in gone.items()}
        outcome = Counter()
        bad_odds = []
        for r in ai:
            if r["kind"] not in ("attack", "attack_bold"):
                continue
            t = int(r["tick"])
            window = t + 24 * 10
            res = "pending" if window > last_tick else "nothing in 10 days"
            if any(t <= x <= window for x in taken.get((r["target"], r["country"]), [])):
                res = "target taken"
            else:
                for n in unit_notes.get(r["unit"], []):
                    nt = int(n["tick"])
                    if t < nt <= window and n["kind"] in ("break_off", "retreat", "destroyed", "trapped", "overrun"):
                        res = n["kind"]
                        break
            if res == "nothing in 10 days" and r["unit"] in died and t < died[r["unit"]] <= window:
                res = "died"
            outcome[(r["kind"], res)] += 1
            mo = re.search(r"odds ([\d.]+)", r["text"])
            if mo and float(mo.group(1)) < 0.6:
                bad_odds.append(r)
        print("attack outcomes (within 10 days):")
        for (kind, res), n in sorted(outcome.items(), key=lambda kv: (kv[0][0], -kv[1])):
            print(f"  {kind:12} {res:20} {n}")
        if bad_odds:
            print(f"  ! {len(bad_odds)} attacks below the 0.6 floor, e.g. {bad_odds[0]['date']} unit {bad_odds[0]['unit']}: {bad_odds[0]['text']}")
        pools = [r for r in ai if r["kind"] == "pool"]
        if pools:
            print("last pool per country:", "; ".join(f"{r['country']} {r['text']}" for r in
                                                       {r["country"]: r for r in pools}.values()
                                                       if not only or r["country"] == only)[:900])

    # --- combat -------------------------------------------------------------------------------------------
    print("\n## Combat")
    combat = [r for r in notes if r["system"] == "combat" and (not only or r["country"] == only)]
    lost = defaultdict(Counter)
    for r in combat:
        lost[r["country"]][r["kind"]] += 1
    if not combat:
        print("no retreats or losses")
    for c, k in sorted(lost.items(), key=lambda kv: -kv[1]["destroyed"])[: a.top]:
        print(f"{c:8} destroyed {k['destroyed']:4}  retreats {k['retreat']:4}  trapped {k['trapped']:3}  overrun {k['overrun']:3}")
    runs = {}
    longest = []
    for r in battles:
        t, key = int(r["tick"]), r["to"]
        run = runs.get(key)
        if run and t - run[1] <= 1:
            run[1] = t
            run[3] = r
        else:
            if run:
                longest.append(run)
            runs[key] = [t, t, r, r]
    longest.extend(runs.values())
    longest.sort(key=lambda x: -(x[1] - x[0]))
    if longest:
        print("longest battles:")
        for s, e, r0, r1 in longest[:5]:
            print(f"  at {r0['to']} from {r0['from']}: {hours(e - s + 1)} from {r0['date']}, bar {r0['percent']}% -> {r1['percent']}%, "
                  f"{len(rec.ids(r1['attackers']))} vs {len(rec.ids(r1['defenders']))}")

    # --- territory and commands -----------------------------------------------------------------------------
    print("\n## Territory")
    gain, loss = Counter(), Counter()
    for r in control:
        gain[r["to"]] += 1
        loss[r["from"]] += 1
    if not control:
        print("no province changed hands")
    for c in sorted(set(gain) | set(loss), key=lambda c: -(gain[c] - loss[c]))[: a.top]:
        print(f"{c:8} +{gain[c]:4} -{loss[c]:4}")

    # --- manpower: reinforcement notes (Hoi4Manpower), "N divisions, M men, pool P" ------------------------
    print("\n## Manpower (reinforcement)")
    spent, days, pool_first, pool_last = Counter(), Counter(), {}, {}
    for r in notes:
        if r["system"] != "manpower" or r["kind"] != "reinforce":
            continue
        m_ = re.match(r"(\d+) divisions, (\d+) men, pool (-?\d+)", r["text"])
        if not m_:
            continue
        c = r["country"]
        spent[c] += int(m_.group(2))
        days[c] += 1
        pool_first.setdefault(c, int(m_.group(3)) + int(m_.group(2)))
        pool_last[c] = int(m_.group(3))
    if not spent:
        print("no reinforcement")
    for c, n in spent.most_common(a.top):
        dry = "  POOL EMPTY" if pool_last[c] <= 0 else ""
        print(f"{c:8} {n:9,} men over {days[c]:4} days, pool {pool_first[c]:,} -> {pool_last[c]:,}{dry}")

    # --- equipment: weekly notes (Hoi4Equipment), "made A=n ... | delivered A=n ... | stock A=n ..." --------------
    print("\n## Equipment (made / delivered to divisions over the recording, last stockpile)")
    eq_made, eq_given, eq_stock = defaultdict(Counter), defaultdict(Counter), {}
    for r in notes:
        if r["system"] != "equipment" or r["kind"] != "week":
            continue
        parts = r["text"].split(" | ")
        if len(parts) != 3:
            continue
        pairs = lambda s: {k: int(v) for k, v in re.findall(r"(\w+)=(-?\d+)", s)}
        eq_made[r["country"]].update(pairs(parts[0]))
        eq_given[r["country"]].update(pairs(parts[1]))
        eq_stock[r["country"]] = pairs(parts[2])
    if not eq_made and not eq_given:
        print("no equipment notes")
    shown = [player] if player in eq_made or player in eq_given else []
    shown += [c for c, _ in Counter({c: sum(v.values()) for c, v in eq_made.items()}).most_common(a.top) if c != player]
    for c in shown[: a.top]:
        arch = sorted(set(eq_made[c]) | set(eq_given[c]) | set(eq_stock.get(c, {})))
        cells = ", ".join(f"{x.replace('_equipment', '').replace('_chassis', '').replace('_', ' ')} "
                          f"{eq_made[c][x]:,}/{eq_given[c][x]:,}/{eq_stock.get(c, {}).get(x, 0):,}" for x in arch)
        print(f"{c:8} {cells}")

    # --- equipment versions: weekly notes (Hoi4Equipment), "upgraded A=n ... | field A: v=n v=n; A: ..." -------
    ups, field = defaultdict(Counter), {}
    for r in notes:
        if r["system"] != "equipment" or r["kind"] != "versions":
            continue
        up, _, f = r["text"].partition(" | field")
        ups[r["country"]].update({k: int(v) for k, v in re.findall(r"(\w+)=(-?\d+)", up)})
        field[r["country"]] = f.strip()
    if ups or field:
        print("\n## Equipment versions (upgraded in the field over the recording; what the field held last)")
        shown = [player] if player in field else []
        shown += [c for c, _ in Counter({c: sum(v.values()) for c, v in ups.items()}).most_common(a.top) if c != player]
        for c in shown[: a.top]:
            got = ", ".join(f"{k} {v:,}" for k, v in ups[c].most_common()) or "none"
            print(f"{c:8} upgraded {got}")
            if field.get(c):
                print(f"{'':8} field {field[c]}")

    # --- production: weekly notes (Hoi4Production), "factories F free N | archetype active/requested eff% ic" ---
    print("\n## Production (last week's factories and lines; AI assignments over the recording)")
    prod_last, ai_assign = {}, Counter()
    for r in notes:
        if r["system"] != "production":
            continue
        if r["kind"] == "week":
            prod_last[r["country"]] = (r["date"], r["text"])
        elif r["kind"] == "ai_assign":
            m_ = re.match(r"\+(\d+)", r["text"])
            if m_:
                ai_assign[r["country"]] += int(m_.group(1))
    if not prod_last:
        print("no production notes")
    shown = [player] if player in prod_last else []
    by_factories = Counter({c: int(re.match(r"factories (\d+)", t).group(1)) for c, (_, t) in prod_last.items()})
    shown += [c for c, _ in by_factories.most_common(a.top) if c != player]
    for c in shown[: a.top]:
        d, t = prod_last[c]
        extra = f"  (AI assigned +{ai_assign[c]})" if ai_assign[c] else ""
        print(f"{c:8} {d}  {t.replace('_equipment', '').replace('_chassis', '')}{extra}")

    # --- construction: weekly notes (Hoi4Construction), "civ C mil M cg G free F | queue Q | built B | ..." ------
    print("\n## Construction (last week's civilian factories and queue; levels built over the recording)")
    con_last, built_by, ai_queued = {}, defaultdict(Counter), Counter()
    for r in notes:
        if r["system"] != "construction":
            continue
        if r["kind"] == "week":
            con_last[r["country"]] = (r["date"], r["text"])
        elif r["kind"] == "done":
            built_by[r["country"]][r["text"].split(" ")[0]] += 1
        elif r["kind"] == "ai_queue":
            ai_queued[r["country"]] += 1
    if not con_last:
        print("no construction notes")
    shown = [player] if player in con_last else []
    by_civ = Counter({c: int(re.match(r"civ (\d+)", t).group(1)) for c, (_, t) in con_last.items()})
    shown += [c for c, _ in by_civ.most_common(a.top) if c != player]
    for c in shown[: a.top]:
        d, t = con_last[c]
        parts = t.split(" | ")
        built = ", ".join(f"{k.replace('industrial_complex', 'civ').replace('arms_factory', 'mil')} +{n}" for k, n in sorted(built_by[c].items()))
        print(f"{c:8} {d}  {' | '.join(parts[:3])}  built: {built or '-'}  (AI queued {ai_queued[c]})")

    # --- deployment: weekly notes (Hoi4Deployment), "lines L started S training T equipment E | waiting W | ..." ---
    print("\n## Deployment (last week's training lines; divisions deployed over the recording)")
    dep_last, deployed, early, ai_lines = {}, Counter(), Counter(), Counter()
    for r in notes:
        if r["system"] != "deployment":
            continue
        if r["kind"] == "week":
            dep_last[r["country"]] = (r["date"], r["text"])
        elif r["kind"] == "deployed":
            deployed[r["country"]] += 1
            m = re.search(r"trained ([\d.]+)", r["text"])
            if m and float(m.group(1)) < 0.995:
                early[r["country"]] += 1
        elif r["kind"] == "ai_train":
            ai_lines[r["country"]] += int(r["text"].split(" ")[0])
    if not dep_last and not deployed:
        print("no deployment notes")
    countries_ = set(dep_last) | set(deployed)
    shown = [player] if player in countries_ else []
    shown += [c for c, _ in (deployed + Counter({c: 0 for c in countries_})).most_common(a.top) if c != player]
    for c in shown[: a.top]:
        d, t_ = dep_last.get(c, ("-", "no lines"))
        print(f"{c:8} {d}  {t_}  deployed {deployed[c]} (early {early[c]})  (AI ordered {ai_lines[c]})")

    # --- war: capitulations and peace deals (Hoi4War / Hoi4PeaceConference) ---------------------------
    print("\n## War (AI declarations, capitulations, peace deals)")
    war_notes = [r for r in notes if r["system"] in ("war", "ai_war") and r["kind"] != "sent_home"]
    if not war_notes:
        print("none")
    for r in war_notes[:80]:
        print(f"{r['date']}  {r['country']:6} {r['kind']:12} {r['text']}")
    if len(war_notes) > 80:
        print(f"... {len(war_notes) - 80} more")
    kinds = Counter(r["kind"] for r in war_notes)
    sent = sum(1 for r in notes if r["system"] == "war" and r["kind"] == "sent_home")
    if war_notes:
        print(f"totals: {dict(kinds)}, divisions sent home after peace: {sent}")

    # --- activity: how divisions spend their time (state per division-hour) and how often they move --------
    window = 90 * 24
    since = max(first_tick, last_tick - window)
    print(f"\n## Activity (last {hours(last_tick - since)}: share of division-hours by state, province moves per division per day, mean organisation at the weekly snapshots)")
    spent = defaultdict(Counter)   # tag -> state -> hours
    moves = Counter()              # tag -> province changes
    orgs = defaultdict(list)       # tag -> organisation % at weekly snapshots
    cur = {}                       # unit -> (tick, tag, state, province)
    def close(u, until):
        t0, tag, st, _ = cur[u]
        lo = max(t0, since)
        if until > lo:
            spent[tag][st] += until - lo
    for r in divs:
        t = int(r["tick"])
        u = r["id"]
        if u in cur:
            close(u, t)
            if t >= since and r["province"] != cur[u][3] and r.get("why") != "gone":
                moves[cur[u][1]] += 1
        if r.get("why") == "gone":
            cur.pop(u, None)
            continue
        if t >= since and r.get("why") == "snap" and r.get("org", "").replace(".", "", 1).isdigit():
            orgs[r["tag"]].append(float(r["org"]))
        cur[u] = (t, r["tag"], r.get("state", "") or "?", r["province"])
    for u in list(cur):
        close(u, last_tick)
    groups = {"holding": ("idle", "defend"), "marching": ("march",), "attacking": ("ai_attack", "attack"),
              "retreating": ("retreat",)}
    rows = []
    for tag, c in spent.items():
        total = sum(c.values())
        if total < 24 * 10 or (only and tag != only):
            continue
        shares = {g: sum(c[s] for s in ss) / total for g, ss in groups.items()}
        other = 1 - sum(shares.values())
        org = sum(orgs[tag]) / len(orgs[tag]) if orgs[tag] else float("nan")
        rows.append((total, tag, shares, other, moves[tag] * 24 / total, org))
    rows.sort(reverse=True)
    print(f"{'country':8} {'div-days':>8} {'holding':>8} {'marching':>9} {'attacking':>10} {'retreat':>8} {'other':>6} {'moves/div/day':>14} {'org':>5}")
    for total, tag, s, other, mpd, org in rows[:a.top + 8]:
        print(f"{tag:8} {total / 24:8.0f} {s['holding']:8.0%} {s['marching']:9.0%} {s['attacking']:10.0%} "
              f"{s['retreating']:8.0%} {other:6.0%} {mpd:14.2f} {org:4.0f}%")

    # --- war AI country layer: theatres, main efforts, armies (Hoi4BattlePlans.AICountry, Docs/ai-layers.md) ---
    print("\n## War AI (main efforts; theatres and armies at each country's last plan, have/budget; plans and why)")
    aic = [r for r in notes if r["system"] == "ai_country" and (not only or r["country"] == only)]
    mains = [r for r in aic if r["kind"] == "main_effort"]
    if not aic:
        print("none")
    for r in mains[:40]:
        print(f"{r['date']}  {r['country']:6} {r['text'][:400]}")
    if len(mains) > 40:
        print(f"... {len(mains) - 40} more main effort changes")
    last, joined, reserved, disbanded, why = {}, Counter(), Counter(), Counter(), {}
    for r in aic:
        c = r["country"]
        if r["kind"] == "theatres":
            last[c] = r
            m = re.match(r"plan \((.*?)\):", r["text"])
            why.setdefault(c, Counter())[re.sub(r"\d+", "N", m.group(1)) if m else "daily"] += 1
        elif r["kind"] == "join":
            joined[c] += sum(int(x) for x in re.findall(r"\+(\d+)", r["text"]))
        elif r["kind"] == "reserve":
            reserved[c] += int(r["text"].split()[0])
        elif r["kind"] == "disband":
            disbanded[c] += 1
    for c, r in sorted(last.items(), key=lambda kv: -(joined[kv[0]] + reserved[kv[0]]))[:a.top + 8]:
        plans = ", ".join(f"{k} {n}" for k, n in why.get(c, Counter()).most_common(4))
        print(f"{c:6} plans {sum(why.get(c, Counter()).values())} ({plans}); joined {joined[c]}, to reserve {reserved[c]}, armies disbanded {disbanded[c]}; last {r['date']}: {r['text'][:600]}")

    # --- timing (Hoi4Simulation perf.tsv, one row per day) -----------------------------------------
    _, perf = rec.load(path, "perf")
    print("\n## Timing (per game day, ms)")
    if not perf:
        print("no perf.tsv")
    else:
        def f(r, k):
            try:
                return float(r[k])
            except ValueError:
                return 0.0
        systems = [k for k in perf[0] if k not in ("tick", "date", "sim_ms", "frames", "frame_ms_avg", "frame_ms_max", "wars", "divisions")
                   and not k.endswith("_max") and k not in ("plans_refresh", "plans_distribute", "plans_ai")]
        step = max(1, len(perf) // 12)
        print(f"{'date':14} {'wars':>4} {'divs':>5} {'sim_ms':>8} {'frame avg':>9} {'frame max':>9}  top systems")
        for i in range(0, len(perf), step):
            chunk = perf[i:i + step]
            top = sorted(systems, key=lambda k: -sum(f(r, k) for r in chunk))[:3]
            tops = ", ".join(f"{k} {sum(f(r, k) for r in chunk) / len(chunk):.1f}" for k in top)
            print(f"{chunk[0]['date'][:10]:14} {chunk[-1]['wars']:>4} {chunk[-1]['divisions']:>5} "
                  f"{sum(f(r, 'sim_ms') for r in chunk) / len(chunk):8.1f} "
                  f"{sum(f(r, 'frame_ms_avg') for r in chunk) / len(chunk):9.1f} "
                  f"{max(f(r, 'frame_ms_max') for r in chunk):9.1f}  {tops}")
        worst = max(perf, key=lambda r: f(r, "sim_ms"))
        print(f"slowest day {worst['date'][:10]}: {f(worst, 'sim_ms'):.1f} ms, {worst['wars']} wars, {worst['divisions']} divisions")
        for k in ("combat_h_max", "plans_h_max", "ai_h_max", "plans_refresh", "plans_distribute"):
            if k in perf[0]:
                w = max(perf, key=lambda r: f(r, k))
                print(f"worst {k}: {f(w, k):.1f} ms on {w['date'][:10]}")

    print("\n## Commands")
    for k, n in Counter(r["command"] for r in cmds).most_common():
        print(f"{n:7}  {k}")
    if not cmds:
        print("none")


if __name__ == "__main__":
    main()
