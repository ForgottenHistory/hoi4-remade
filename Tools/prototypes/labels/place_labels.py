"""
Map label placement prototype: fit each country's name along a curve through the body of its
territory, sized to fit (HOI4 / Paradox style country labels).

Per country (largest connected piece, plus big detached pieces):
  1. mask at 1/DS resolution, distance to the edge (scipy EDT)
  2. skeleton (Zhang-Suen), pruned to the thick trunk (dist >= TRUNK * max dist)
  3. longest path along the trunk (tree diameter, two BFS passes)
  4. smooth arc: quadratic fit in the frame of the path's end-to-end axis, weighted by thickness
  5. font size from the thickness along the arc and the arc length; letters spread to fill it

Usage: python place_labels.py <hoi4_dir> <hoi4data_dir> <out.png> [x0 y0 w h] [scale]
"""
import sys, os, re, glob, math, time
from collections import deque
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter
from scipy import ndimage

HOI4, DATA, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
CROP = tuple(map(int, sys.argv[4:8])) if len(sys.argv) >= 8 else (2400, 250, 1300, 800)
SCALE = float(sys.argv[8]) if len(sys.argv) > 8 else 1.0
sys.argv = [sys.argv[0], HOI4]  # trace_borders reads HOI4 from argv[1]
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'borders'))
import trace_borders as tb

DS = 4            # pieces are found at 1/DS resolution (then measured adaptively)
CUR_STEP = DS
TRUNK = 0.35      # skeleton pixels thinner than this fraction of max thickness are pruned
MIN_AREA = 40     # (downsampled px) pieces smaller than this get no label
DETACHED = 0.3    # other pieces labelled if area >= this fraction of the capital's piece
MAX_HEIGHT_PX = 88  # (full-res px) largest label height
ANGLES = range(-90, 90, 15)  # straight candidates: parallel lines swept across the shape
LEVEL_PREF = 0.35            # round shapes: score *= 1 - LEVEL_PREF * |sin(angle)|
ELONG_ROUND = 1.6            # elongation (long/short axis) at or below which the full preference applies
ELONG_LONG = 2.4             # at or above this the label freely follows the shape
DEBUG_ELONG = False
ARC_BONUS = 1.1              # skeleton arc follows the country's shape: prefer it over straight lines
FILL = 0.75                  # text is spread over the central FILL of the curve
CLEAR = 0.55                 # letters keep their centre line at least CLEAR * height from the edge
MAX_GAP = 0.8                # letter spacing at most this fraction of the font size
MIN_SCREEN = 11              # labels smaller than this on screen (px, font size) grow...
MAX_SCALE = 2.5              # ...by at most this much...
GROW_RULE = os.environ.get('GROW_RULE', 'room')  # 'global' = old rule; 'room' = also capped by the label's own room
FADE_RANGE = 1.3             # ...and fade out over need in [cap, cap * FADE_RANGE]
TARGET_PX = 160              # pieces are measured at a resolution giving ~this many px across (DS max)
SPACE_BELOW = 0.9            # letter-space only if the tight text is shorter than this fraction of the span
FONT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'fonts', 'Oswald.ttf')

def get_font(size):
    return ImageFont.truetype(FONT, size)

_ratio_font = get_font(100)
def width_ratio(text):
    """Text width / font size for the label font."""
    return max(_ratio_font.getlength(text) / 100.0, 0.3)

# ------------------------------------------------------------------ data
def load_names():
    names = {}
    for line in open(os.path.join(DATA, 'localisation/english/countries_l_english.yml'), encoding='utf-8-sig'):
        m = re.match(r'\s*([A-Z0-9]{3}):0\s+"(.*)"', line)
        if m: names[m.group(1)] = m.group(2)
    return names

def load_capitals():
    caps = {}
    for f in glob.glob(os.path.join(HOI4, 'history/countries/*.txt')):
        tag = os.path.basename(f)[:3]
        m = re.search(r'^capital\s*=\s*(\d+)', open(f, encoding='utf-8-sig', errors='replace').read(), re.M)
        if m: caps[tag] = int(m.group(1))
    return caps

def load_colors():
    cols = {}
    for f in glob.glob(os.path.join(DATA, 'common/countries/*.json5')):
        s = open(f, encoding='utf-8').read()
        t = re.search(r'tag:\s*"(\w+)"', s); c = re.search(r'color:\s*\[(\d+),\s*(\d+),\s*(\d+)\]', s)
        if t and c: cols[t.group(1)] = tuple(int(v) for v in c.groups())
    return cols

# ------------------------------------------------------------------ geometry
def zhang_suen(img):
    img = img.copy().astype(np.uint8)
    img[0, :] = img[-1, :] = 0; img[:, 0] = img[:, -1] = 0
    while True:
        changed = False
        for step in (0, 1):
            P = np.pad(img, 1)
            p2, p3, p4 = P[:-2, 1:-1], P[:-2, 2:], P[1:-1, 2:]
            p5, p6, p7 = P[2:, 2:], P[2:, 1:-1], P[2:, :-2]
            p8, p9 = P[1:-1, :-2], P[:-2, :-2]
            nb = [p2, p3, p4, p5, p6, p7, p8, p9]
            B = sum(n.astype(np.int32) for n in nb)
            A = sum(((nb[i] == 0) & (nb[(i + 1) % 8] == 1)).astype(np.int32) for i in range(8))
            if step == 0:
                c = (p2 * p4 * p6 == 0) & (p4 * p6 * p8 == 0)
            else:
                c = (p2 * p4 * p8 == 0) & (p2 * p6 * p8 == 0)
            rm = (img == 1) & (B >= 2) & (B <= 6) & (A == 1) & c
            if rm.any():
                img[rm] = 0; changed = True
        if not changed:
            return img.astype(bool)

def farthest(start, adj):
    dist = {start: 0.0}; prev = {start: None}; q = deque([start])
    # skeleton is nearly a tree: BFS with geometric step lengths is close enough
    while q:
        u = q.popleft()
        for v, w in adj[u]:
            if v not in dist:
                dist[v] = dist[u] + w; prev[v] = u; q.append(v)
    end = max(dist, key=dist.get)
    path = [end]
    while prev[path[-1]] is not None: path.append(prev[path[-1]])
    return end, path

def trunk_path(mask, dist):
    skel = zhang_suen(mask)
    trunk = skel & (dist >= TRUNK * dist.max())
    # keep the trunk component containing the thickest skeleton pixel
    lab, n = ndimage.label(trunk, structure=np.ones((3, 3)))
    if n == 0:
        return None
    sizes = ndimage.sum(trunk, lab, range(1, n + 1))
    best = 1 + int(np.argmax(sizes * 0 + ndimage.maximum(dist, lab, range(1, n + 1)) * np.sqrt(sizes)))
    pts = set(map(tuple, np.argwhere(lab == best)))
    adj = {p: [] for p in pts}
    for (y, x) in pts:
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if (dy or dx) and (y + dy, x + dx) in pts:
                    adj[(y, x)].append(((y + dy, x + dx), math.hypot(dx, dy)))
    a, _ = farthest(next(iter(pts)), adj)
    _, path = farthest(a, adj)
    return np.array([(x, y) for y, x in path], float)  # (x, y)

def fit_arc(path, dist):
    """Quadratic y' = a x'^2 + b x' + c in the frame of the end-to-end axis, thickness weighted."""
    p0, p1 = path[0], path[-1]
    axis = p1 - p0; L = np.hypot(*axis)
    if L < 1e-6:
        return None
    u = axis / L
    # read left to right; steep labels read bottom-to-top (HOI4 style)
    if u[0] < -1e-6 or (abs(u[0]) <= 1e-6 and u[1] > 0):
        u = -u; p0, p1 = p1, p0
    v = np.array([-u[1], u[0]])
    rel = path - p0
    xs, ys = rel @ u, rel @ v
    w = np.array([dist[int(y), int(x)] for x, y in path]) + 1e-3
    A = np.stack([xs ** 2, xs, np.ones_like(xs)], 1)
    coef, *_ = np.linalg.lstsq(A * w[:, None], ys * w, rcond=None)
    # limit bending: sagitta <= 18% of the chord
    sag = abs(coef[0]) * (L / 2) ** 2
    if sag > 0.18 * L:
        coef[0] *= 0.18 * L / sag
    t = np.linspace(xs.min(), xs.max(), 64)
    yv = coef[0] * t ** 2 + coef[1] * t + coef[2]
    return p0 + np.outer(t, u) + np.outer(yv, v)

def sample(dist, pts):
    h, w = dist.shape
    xi = np.clip(pts[:, 0].astype(int), 0, w - 1); yi = np.clip(pts[:, 1].astype(int), 0, h - 1)
    return dist[yi, xi]

def resample(curve, step=0.5):
    seg = np.hypot(*np.diff(curve, axis=0).T)
    cum = np.concatenate([[0], np.cumsum(seg)])
    if cum[-1] <= 0:
        return curve[:1], cum[:1]
    t = np.arange(0, cum[-1], step)
    pts = np.stack([np.interp(t, cum, curve[:, 0]), np.interp(t, cum, curve[:, 1])], 1)
    return pts, t

def longest_run(ok):
    edges = np.diff(np.concatenate([[0], ok.astype(int), [0]]))
    st, en = np.nonzero(edges == 1)[0], np.nonzero(edges == -1)[0]
    if len(st) == 0:
        return None
    k = int(np.argmax(en - st))
    return st[k], en[k]

def layout(curve, dist, name):
    """Centred layout: trim the curve to the territory, keep the central FILL of it, and size the
    text by the thickness along that whole span (letters are spread over it when rendering).
    Returns (span curve, span length, h); h 0 if nothing fits."""
    pts, t = resample(curve)
    if len(pts) < 4:
        return curve, 0.0, 0.0
    d = sample(dist, pts)
    run = longest_run(d >= 1.0)
    if run is None or run[1] - run[0] < 4:
        return curve, 0.0, 0.0
    pts, d = pts[run[0]:run[1]], d[run[0]:run[1]]
    n = len(pts)
    cut = int(n * (1 - FILL) / 2)
    span, ds = pts[cut:n - cut], d[cut:n - cut]
    if len(span) < 4:
        return curve, 0.0, 0.0
    L = (len(span) - 1) * 0.5
    h = min(np.percentile(ds, 25) / CLEAR, L / width_ratio(name), MAX_HEIGHT_PX / CUR_STEP)
    global LAST_RUN
    LAST_RUN = (run[1] - run[0] - 1) * 0.5   # whole inside run, for the growth limit
    return span, L, max(h, 0.0)

def center_lines(dist, cx, cy):
    """Straight lines through (cx, cy) at every angle, long enough to cross the territory."""
    h, w = dist.shape
    R = math.hypot(w, h)
    out = []
    for deg in ANGLES:
        a = math.radians(deg); u = np.array([math.cos(a), math.sin(a)])
        out.append(np.array([[cx, cy] - R * u, [cx, cy] + R * u]))
    return out

def sweep_lines(dist, deg):
    """Parallel lines at deg across the whole shape; each yields its longest inside run."""
    h, w = dist.shape
    a = math.radians(deg); u = np.array([math.cos(a), math.sin(a)]); nrm = np.array([-u[1], u[0]])
    c = np.array([w / 2, h / 2]); R = math.hypot(w, h) / 2
    t = np.arange(-R, R, 0.5)
    out = []
    for o in np.arange(-R, R, max(1.0, R / 16)):
        pts = c + o * nrm + np.outer(t, u)
        xi, yi = pts[:, 0].astype(int), pts[:, 1].astype(int)
        ok = (xi >= 0) & (yi >= 0) & (xi < w) & (yi < h)
        inside = np.zeros(len(t), bool)
        inside[ok] = dist[yi[ok], xi[ok]] > 0
        if not inside.any(): continue
        # longest contiguous inside run
        edges = np.diff(np.concatenate([[0], inside.astype(int), [0]]))
        starts, ends = np.nonzero(edges == 1)[0], np.nonzero(edges == -1)[0]
        k = int(np.argmax(ends - starts))
        if ends[k] - starts[k] < 4: continue
        run = pts[starts[k]:ends[k]]
        out.append(run[np.linspace(0, len(run) - 1, 32).astype(int)])
    return out

def orient(curve):
    d = curve[-1] - curve[0]
    if d[0] < -1e-3 * max(abs(d[1]), 1) or (abs(d[0]) <= 1e-3 * max(abs(d[1]), 1) and d[1] > 0):
        return curve[::-1].copy()
    return curve

def angle_of(curve):
    d = curve[-1] - curve[0]
    return math.atan2(d[1], d[0])

def best_layout(crop, dist, name):
    """Candidates: the skeleton arc and straight lines through the thickest point.
    Keep the one fitting the largest text, preferring horizontal."""
    cands = []
    path = trunk_path(crop, dist)
    if path is not None and len(path) >= 2:
        arc = fit_arc(path, dist)
        if arc is not None:
            cands.append((arc, ARC_BONUS))
    # territory centre, weighted towards the thick parts (snapped inside if needed)
    yy, xx = np.nonzero(dist > 0)
    wgt = dist[yy, xx] ** 2
    cx, cy = (xx * wgt).sum() / wgt.sum() + 0.5, (yy * wgt).sum() / wgt.sum() + 0.5
    if dist[int(cy), int(cx)] < 1:
        k = int(np.argmax(wgt)); cx, cy = xx[k] + 0.5, yy[k] + 0.5
    cands.extend((line, 1.0) for line in center_lines(dist, cx, cy))
    best, best_score = None, -1
    # elongation from the shape's second moments: round shapes read level, long ones follow their axis
    ev = np.linalg.eigvalsh(np.cov(np.stack([xx, yy]).astype(float)))
    elong = math.sqrt(ev[1] / max(ev[0], 1e-6))
    pref = LEVEL_PREF * float(np.clip((ELONG_LONG - elong) / (ELONG_LONG - ELONG_ROUND), 0, 1))
    if DEBUG_ELONG: print(name, round(elong, 2), round(pref, 2))
    for c, bonus in cands:
        c2, length, height = layout(orient(c), dist, name)
        if height <= 0: continue
        # ties go to horizontal (round countries); long thin countries still follow their axis
        score = bonus * height * (1 - pref * abs(math.sin(angle_of(c2))))
        if score > best_score:
            best, best_score = (c2, length, height, LAST_RUN), score
    return best

# ------------------------------------------------------------------ main
t0 = time.time()
ids, kinds = tb.load_ids()
prov_state, state_owner = tb.load_states()
names, colors, capitals = load_names(), load_colors(), load_capitals()
H, W = ids.shape
tags = sorted({t for t in state_owner.values() if t})
tag_index = {t: i + 1 for i, t in enumerate(tags)}
lut = np.zeros(ids.max() + 1, np.int32)
for pid, sid in prov_state.items():
    t = state_owner.get(sid)
    if t and pid < len(lut): lut[pid] = tag_index[t]
owner = lut[ids]
small = owner[DS // 2::DS, DS // 2::DS]
# capital state -> downsampled pixels of its provinces

sid_of = np.zeros(ids.max() + 1, np.int32)
for pid, sid in prov_state.items():
    if pid < len(sid_of): sid_of[pid] = sid
small_state = sid_of[ids[DS // 2::DS, DS // 2::DS]]
print(f'loaded in {time.time() - t0:.1f}s; {len(tags)} countries')

labels = []  # (tag, curve full-res px, height full-res px, name)
t1 = time.time()
# Engine port check: DUMP_DIR writes the inputs for the C# LabelPlacer harness;
# LABELS_JSON draws the labels it placed instead of placing them here.
if os.environ.get('DUMP_DIR'):
    import json
    dd = os.environ['DUMP_DIR']
    ids.astype(np.uint16).tofile(os.path.join(dd, 'ids.bin'))
    olut = np.zeros(ids.max() + 1, np.uint16); olut[:len(lut)] = lut
    olut.tofile(os.path.join(dd, 'owners.bin'))
    reqs = []
    for tag in tags:
        cap_state = capitals.get(tag, -1)
        provs = [pid for pid, sid in prov_state.items() if sid == cap_state and lut[pid] == tag_index[tag]]
        name = names.get(tag, tag).upper()
        reqs.append({'country': tag_index[tag], 'tag': tag, 'text': name,
                     'capital': int(min(provs)) if provs else 0, 'ratio': width_ratio(name)})
    json.dump({'width': int(W), 'height': int(H), 'requests': reqs}, open(os.path.join(dd, 'input.json'), 'w'))
    print('dumped to', dd); sys.exit(0)
if os.environ.get('LABELS_JSON'):
    import json
    by_index = {i: t for t, i in tag_index.items()}
    for L in json.load(open(os.environ['LABELS_JSON'])):
        labels.append((by_index[L['country']], np.array(L['curve'], float), L['height'], L['text'], L.get('room', 0.0)))
    tags_to_place = []
else:
    tags_to_place = tags
for tag in tags_to_place:
    m = small == tag_index[tag]
    if not m.any(): continue
    lab, n = ndimage.label(m)
    sizes = ndimage.sum(m, lab, range(1, n + 1))
    # main piece: the one holding the capital (HOI4 labels the homeland, not the biggest colony)
    cap_px = lab[(small_state == capitals.get(tag, -1)) & m]
    main = int(np.bincount(cap_px).argmax()) - 1 if cap_px.size else int(np.argmax(sizes))
    order = [main] + [k for k in np.argsort(-sizes) if k != main]
    for k in order:
        if k != main and (sizes[k] < MIN_AREA or sizes[k] < DETACHED * sizes[main]): continue
        if sizes[k] < MIN_AREA / 4: continue
        piece = lab == k + 1
        ys, xs = np.nonzero(piece)
        # full-res bbox of the piece; pick a step so the piece is ~TARGET_PX across
        fy0, fy1 = ys.min() * DS, (ys.max() + 1) * DS
        fx0, fx1 = xs.min() * DS, (xs.max() + 1) * DS
        step = int(np.clip(math.ceil(max(fy1 - fy0, fx1 - fx0) / TARGET_PX), 1, DS))
        pad = 2 * step
        y0, x0 = max(fy0 - pad, 0), max(fx0 - pad, 0)
        y1, x1 = min(fy1 + pad, H), min(fx1 + pad, W)
        fine = owner[y0 + step // 2:y1:step, x0 + step // 2:x1:step] == tag_index[tag]
        # keep only this piece: the fine component overlapping the coarse piece most
        flab, fn = ndimage.label(fine)
        cy_, cx_ = ((ys * DS + DS // 2 - y0 - step // 2) // step).clip(0, fine.shape[0] - 1),                    ((xs * DS + DS // 2 - x0 - step // 2) // step).clip(0, fine.shape[1] - 1)
        hits = flab[cy_, cx_]; hits = hits[hits > 0]
        if hits.size == 0: continue
        crop = np.pad(flab == np.bincount(hits).argmax(), 1)
        y0 -= step; x0 -= step
        dist = ndimage.distance_transform_edt(crop)
        CUR_STEP = step
        name = names.get(tag, tag).upper()
        best = best_layout(crop, dist, name)
        if best is None: continue
        curve, length, height, run_len = best
        labels.append((tag, (curve + 0.5) * step + [x0, y0], height * step, name, run_len * step))
print(f'placed {len(labels)} labels in {time.time() - t1:.1f}s')

# ------------------------------------------------------------------ render
cx0, cy0, cw, ch = CROP
S = SCALE
own = owner[cy0:cy0 + ch, cx0:cx0 + cw]
pal = np.zeros((len(tags) + 1, 3), np.uint8)
for t, i in tag_index.items():
    pal[i] = colors.get(t, (128, 128, 128))
water = np.array([[k in ('sea', 'lake') for k in [None]]])
idc = ids[cy0:cy0 + ch, cx0:cx0 + cw]
iswater = np.vectorize(lambda p: kinds.get(p) in ('sea', 'lake'))(np.unique(idc))
wl = dict(zip(np.unique(idc), iswater))
wmask = np.vectorize(wl.get)(idc)
img = pal[own]
img[own == 0] = (150, 150, 140)
img[wmask] = (60, 80, 110)
# country borders
edge = np.zeros(own.shape, bool)
edge[:, 1:] |= own[:, 1:] != own[:, :-1]; edge[1:, :] |= own[1:, :] != own[:-1, :]
img = (img * 0.85).astype(np.uint8); img[edge & ~wmask] = (30, 30, 30)
canvas = Image.fromarray(img).resize((int(cw * S), int(ch * S)), Image.NEAREST).convert('RGBA')
draw = ImageDraw.Draw(canvas)

def draw_label(curve, height, text, run_len):
    pts = (curve - [cx0, cy0]) * S
    if not ((pts[:, 0] > -200).any() and (pts[:, 0] < cw * S + 200).any() and
            (pts[:, 1] > -200).any() and (pts[:, 1] < ch * S + 200).any()):
        return
    seg = np.hypot(*np.diff(pts, axis=0).T); cum = np.concatenate([[0], np.cumsum(seg)]); L = cum[-1]
    base = height * S                      # font size on screen at native size
    ratio_font = get_font(100)
    widths = np.array([ratio_font.getlength(c) / 100.0 for c in text]) * base
    natural = widths.sum()
    if natural > L:
        widths *= L / natural; base *= L / natural; natural = L
    gap = 0.0
    if len(text) > 1 and natural < SPACE_BELOW * L:
        gap = min((L - natural) / (len(text) - 1), base * MAX_GAP)
    total = natural + gap * (len(text) - 1)

    need = MIN_SCREEN / max(base, 1e-4)
    cap = MAX_SCALE
    if GROW_RULE == 'room':
        # the grown label may span the whole inside run, not beyond
        cap = min(cap, max(1.0, run_len * S / max(total, 1e-4)))
    scale = min(max(need, 1.0), cap)
    alpha = 1.0 if need <= cap else max(0.0, 1 - (need - cap) / (cap * (FADE_RANGE - 1)))
    if alpha <= 0.02:
        return
    size = max(int(round(base * scale)), 4)
    font = get_font(size)

    # letter centres at native size, then scaled about the label centre
    s0 = (L - total) / 2
    mids = []
    for wc in widths:
        mids.append(s0 + wc / 2); s0 += wc + gap
    def at(t):
        i = int(np.clip(np.searchsorted(cum, t) - 1, 0, len(pts) - 2))
        f = (t - cum[i]) / max(seg[i], 1e-6)
        d = pts[i + 1] - pts[i]
        return pts[i] + f * d, math.degrees(math.atan2(d[1], d[0]))
    centre, _ = at(L / 2)
    for c, mid in zip(text, mids):
        pos, ang = at(mid)
        pos = centre + (pos - centre) * scale
        g = Image.new('RGBA', (int(size * 2), int(size * 2)), (0, 0, 0, 0))
        sh = Image.new('RGBA', g.size, (0, 0, 0, 0))
        off = max(1, size // 18)
        ImageDraw.Draw(sh).text((size + off, size + off), c, font=font, anchor='mm', fill=(10, 10, 20, int(170 * alpha)))
        g.alpha_composite(sh.filter(ImageFilter.GaussianBlur(max(1, size / 20))))
        ImageDraw.Draw(g).text((size, size), c, font=font, anchor='mm', fill=(232, 230, 242, int(255 * alpha)))
        g = g.rotate(-ang, resample=Image.BICUBIC)
        canvas.alpha_composite(g, (int(pos[0] - g.width / 2), int(pos[1] - g.height / 2)))

SHOW_CURVES = False
for tag, curve, height, name, run_len in labels:
    draw_label(curve, height, name, run_len)
    if SHOW_CURVES:
        pts = (curve - [cx0, cy0]) * S
        draw.line([tuple(p) for p in pts], fill=(255, 0, 0, 90), width=1)
canvas.convert('RGB').save(OUT)
print('saved', OUT)
