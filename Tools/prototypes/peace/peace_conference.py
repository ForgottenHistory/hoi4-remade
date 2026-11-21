"""Prototype: HOI4 peace conference numbers, before porting them to C# (Hoi4PeaceConference).

Rules (HOI4 wiki "Peace conference" + common/defines, checked against a live single-winner conference,
France over Belgium: Wallonie 102, Ardennes 31, Vlaanderen 98, Antwerpen 104, score 550):

- State value (converter, hoi4_state_data.json5 `value`): 10 + population / 100,000 + base resources
  + 4 x building slots of its category + the `value` of its state buildings (provincial buildings don't count).
- Conference score: 1.35 (PEACE_SCORE_SCALE_FACTOR) x the value of the loser's states the winners control,
  shared by war participation. One winner: all of it on the first turn. (Live: 1.35 x 407.3 = 550.)
- Take-state cost: value x ideology (democratic 1.3, fascist 0.9, communist 1.0) x occupation (0.8 when the
  winner controls the state; HOI4 also needs compliance > 0, which a just-taken state lacks: Antwerpen)
  x influence, or x 10 (BASE_PEACE_TAKE_UNCONTROLLED_STATE_FACTOR) when no winner controls it.
- Influence: 0.05 x f(capital) + 0.45 x f(nearest core) + 0.5 x f(nearest controlled state other than this one),
  f(d) = 0.7 + 0.3 x min(d / neutral, 1), d = state-centre pixel distance / 22, neutral 30 / 6 / 14
  (INFLUENCE_* defines), minus 0.05 per adjacent state already taken in the conference.

    python Tools/prototypes/peace/peace_conference.py [--winner FRA] [--loser BEL] [--controlled 6,34,977,980]

Prints each of the loser's states with value, influence and cost (and the live cost where known), the score,
and writes peace_preview.png next to this script: the loser's states, controlled ones green with their cost.
"""
import argparse
import json
import math
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Tools"))
from manpower_pools import json5  # noqa: E402

SCALE = 1.35
UNCONTROLLED = 10.0
OCCUPATION = 0.8
IDEOLOGY = {"democratic": 1.3, "fascism": 0.9, "communism": 1.0}
DIVISOR = 22.0
NEUTRAL = {"capital": 30.0, "core": 6.0, "controlled": 14.0}
RATIO = {"capital": 0.05, "core": 0.45, "controlled": 0.5}
ADJACENT_TAKEN = 0.05
LIVE = {"BEL": {34: 102, 980: 31, 6: 98, 977: 104}}  # France's live conference, turn 1


def load(data: Path):
    states = {int(k): v for k, v in json5(data / "map" / "hoi4_state_data.json5")["states"].items()}
    regions = json5(data / "map" / "hoi4_regions.json5")
    province_state = {p: int(s) for s, ps in regions["states"].items() for p in ps}
    oob = json5(data / "history" / "units" / "oob_1936.json5")["countries"]
    ideology = {t: c.get("ideology", "neutrality") for t, c in oob.items()}
    return states, regions, province_state, ideology


def dist(states, a, b):
    ca, cb = states[a].get("center"), states[b].get("center")
    if not ca or not cb:
        return 1e9
    return math.hypot(ca[0] - cb[0], ca[1] - cb[1]) / DIVISOR


def f(d, kind):
    return 0.7 + 0.3 * min(d / NEUTRAL[kind], 1.0)


def influence(states, state, capital_state, cores, controlled, adjacent_taken=0):
    dcap = dist(states, state, capital_state)
    dcore = min((dist(states, state, c) for c in cores if c != state), default=1e9)
    dctrl = min((dist(states, state, c) for c in controlled if c != state), default=1e9)
    v = RATIO["capital"] * f(dcap, "capital") + RATIO["core"] * f(dcore, "core") + RATIO["controlled"] * f(dctrl, "controlled")
    return v - ADJACENT_TAKEN * adjacent_taken, (dcap, dcore, dctrl)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--winner", default="FRA")
    ap.add_argument("--loser", default="BEL")
    ap.add_argument("--controlled", default="6,34,977,980", help="loser states the winner controls")
    ap.add_argument("--data", default=str(ROOT / "Hoi4Data"))
    a = ap.parse_args()
    data = Path(a.data)
    states, regions, province_state, ideology = load(data)
    loser_states = sorted(s for s, e in states.items() if e.get("owner") == a.loser)
    winner_states = [s for s, e in states.items() if e.get("owner") == a.winner]
    cores = [s for s in winner_states if a.winner in states[s].get("cores", [])]
    capital_state = province_state[regions["capitals"][a.winner]]
    taken = {int(x) for x in a.controlled.split(",") if x}
    controlled = set(winner_states) | taken
    ideo = IDEOLOGY.get(ideology.get(a.winner, ""), 1.0)

    score = SCALE * sum(states[s]["value"] for s in loser_states if s in taken)
    print(f"{a.winner} ({ideology.get(a.winner)}, ideology x{ideo}) over {a.loser}: score {score:.1f} "
          f"= {SCALE} x {score / SCALE:.1f} (value of the {len(taken)} states it controls)")
    live = LIVE.get(a.loser, {})
    rows = []
    for s in loser_states:
        v = states[s]["value"]
        inf, (dcap, dcore, dctrl) = influence(states, s, capital_state, cores, controlled)
        cost = v * ideo * (OCCUPATION if s in taken else UNCONTROLLED) * inf
        rows.append((s, cost))
        ref = f"  live {live[s]} ({cost / live[s] - 1:+.0%})" if s in live else ""
        print(f"  state {s:4} value {v:7.2f}  dist cap {dcap:5.1f} core {dcore:5.1f} ctrl {dctrl:5.1f}  "
              f"influence {inf:.3f}  {'controlled  ' if s in taken else 'uncontrolled'}  cost {cost:7.1f}{ref}")
    print(f"  take all controlled: {sum(c for s, c in rows if s in taken):.1f} of {score:.1f}")
    render(data, states, province_state, loser_states, taken, dict(rows), Path(__file__).with_name("peace_preview.png"))


def render(data, states, province_state, loser_states, taken, cost, out):
    px = np.asarray(Image.open(data / "map" / "provinces.png").convert("RGB"), dtype=np.uint32)
    packed = (px[..., 0] << 16) | (px[..., 1] << 8) | px[..., 2]
    key_state = {}
    for line in (data / "map" / "definition.csv").read_text(encoding="utf-8").splitlines()[1:]:
        f_ = line.split(";")
        if len(f_) >= 4 and f_[0].isdigit():
            key_state[(int(f_[1]) << 16) | (int(f_[2]) << 8) | int(f_[3])] = province_state.get(int(f_[0]), 0)
    keys = np.array(sorted(key_state), dtype=np.uint32)
    vals = np.array([key_state[k] for k in keys], dtype=np.int64)
    idx = np.searchsorted(keys, packed).clip(0, len(keys) - 1)
    st = np.where(keys[idx] == packed, vals[idx], 0)
    centers = [states[s]["center"] for s in taken if states[s].get("center")]
    cx = int(np.mean([c[0] for c in centers])) if centers else 2880
    cy = int(np.mean([c[1] for c in centers])) if centers else 520
    x0, y0, x1, y1 = cx - 150, cy - 100, cx + 150, cy + 100
    crop = st[y0:y1, x0:x1]
    img = np.full(crop.shape + (3,), 70, dtype=np.uint8)
    img[crop != 0] = (110, 110, 120)
    for s in loser_states:
        img[crop == s] = (80, 190, 80) if s in taken else (190, 160, 60)
    edge = np.zeros(crop.shape, bool)
    edge[:, 1:] |= crop[:, 1:] != crop[:, :-1]
    edge[1:, :] |= crop[1:, :] != crop[:-1, :]
    img[edge] = (30, 30, 30)
    im = Image.fromarray(img).resize((crop.shape[1] * 3, crop.shape[0] * 3), Image.NEAREST)
    d = ImageDraw.Draw(im)
    for s in loser_states:
        c = states[s].get("center")
        if c and x0 <= c[0] < x1 and y0 <= c[1] < y1:
            d.text(((c[0] - x0) * 3 - 12, (c[1] - y0) * 3 - 6), f"{cost[s]:.0f}", fill=(255, 255, 255))
    im.save(out)
    print(f"preview: {out}")


if __name__ == "__main__":
    main()
