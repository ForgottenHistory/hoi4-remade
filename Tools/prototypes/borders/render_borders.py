"""
Render traced borders over a crop: raw staircase vs smoothed, styled by type.
Usage: python render_borders.py <hoi4_dir> <arcs.pkl> <out.png> <tag> [scale] [pad]
"""
import sys, os, pickle
import numpy as np
from PIL import Image, ImageDraw
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import trace_borders as tb

HOI4, PKL, OUTPNG, TAG = sys.argv[1:5]
SCALE = int(sys.argv[5]) if len(sys.argv) > 5 else 4
PAD = int(sys.argv[6]) if len(sys.argv) > 6 else 30
SS = 3  # supersampling

ids, kinds = tb.load_ids()
prov_state, state_owner = tb.load_states()
data = pickle.load(open(PKL, 'rb'))
arcs = data['arcs']

# --- integrity: every boundary edge walked exactly once, open arcs end on nodes
H, W = ids.shape
n_edges = int((ids[:, :-1] != ids[:, 1:]).sum() + (ids[:-1, :] != ids[1:, :]).sum())
walked = sum(len(a[0]) - 1 for a in arcs)
print(f'boundary edges {n_edges}, walked {walked}')

# --- crop = bbox of the tag's provinces
provs = [p for p, s in prov_state.items() if state_owner.get(s) == TAG]
mask = np.isin(ids, provs)
ys, xs = np.nonzero(mask)
x0, x1 = max(xs.min() - PAD, 0), min(xs.max() + PAD, W)
y0, y1 = max(ys.min() - PAD, 0), min(ys.max() + PAD, H)
cw, ch = x1 - x0, y1 - y0
print(f'{TAG} crop x{x0}-{x1} y{y0}-{y1} ({cw}x{ch})')

# --- background: country colors (hashed) for land, blue for water
owner_of = {}
for p, s in prov_state.items():
    owner_of[p] = state_owner.get(s)
crop = ids[y0:y1, x0:x1]
uniq, inv = np.unique(crop, return_inverse=True)
cols = []
for p in uniq:
    if kinds.get(int(p), 'sea') != 'land':
        cols.append((70, 110, 150))
    else:
        o = owner_of.get(int(p)) or '???'
        h = abs(hash(o)) % 360
        rng = np.random.default_rng(sum(ord(c) * 31 ** i for i, c in enumerate(o)))
        c = rng.integers(110, 210, 3)
        cols.append(tuple(int(v) for v in c))
bg = np.array(cols, np.uint8)[inv].reshape(ch, cw, 3)

STYLE = {  # width in output px, color
    'country':  (3.0, (20, 20, 20)),
    'state':    (1.6, (60, 60, 60)),
    'province': (0.8, (90, 90, 90)),
    'sea':      (0.8, (150, 180, 210)),
    'coast':    (1.2, (30, 40, 50)),
}
ORDER = ['sea', 'province', 'state', 'coast', 'country']

def bbox_hit(p):
    return p[:, 0].max() >= x0 and p[:, 0].min() <= x1 and p[:, 1].max() >= y0 and p[:, 1].min() <= y1

def render(smoothing, show_nodes=False):
    kw = smoothing if isinstance(smoothing, dict) else {}
    S = SCALE * SS
    img = Image.fromarray(bg).resize((cw * S, ch * S), Image.NEAREST)
    d = ImageDraw.Draw(img)
    groups = {k: [] for k in ORDER}
    for p, a, b, closed in arcs:
        if not bbox_hit(p):
            continue
        t = tb.classify(a, b, kinds, prov_state, state_owner)
        if t is None:
            continue
        q = tb.smooth(p, closed, **kw) if smoothing else p
        groups[t].append(q)
    for t in ORDER:
        wpx, col = STYLE[t]
        for q in groups[t]:
            pts = [((x - x0) * S, (y - y0) * S) for x, y in q]
            d.line(pts, fill=col, width=max(1, int(round(wpx * SS))), joint='curve')
    if show_nodes:
        for p, a, b, closed in arcs:
            if closed or not bbox_hit(p):
                continue
            for x, y in (p[0], p[-1]):
                cx, cy = (x - x0) * S, (y - y0) * S
                r = 1.5 * SS
                d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 0, 0))
    return img.resize((cw * SCALE, ch * SCALE), Image.LANCZOS)

STRONG = dict(pre_eps=0.75, chaikin_it=3, eps=0.15)
panels = [render(False), render(True), render(STRONG)]
both = Image.new('RGB', (panels[0].width * 3 + 16, panels[0].height), (255, 255, 255))
for i, im in enumerate(panels):
    both.paste(im, (i * (im.width + 8), 0))
both.save(OUTPNG)
render(STRONG, show_nodes=True).save(OUTPNG.replace('.png', '_nodes.png'))
spts = sum(len(tb.smooth(p, c, **STRONG)) for p, a, b, c in arcs[:5000])
print(f'strong points per arc: {spts/5000:.1f}')
pts = sum(len(tb.smooth(p, c)) for p, a, b, c in arcs[:5000])
raw_pts = sum(len(p) for p, a, b, c in arcs[:5000])
print(f'points per arc (first 5000): raw {raw_pts/5000:.1f} -> smoothed {pts/5000:.1f}')
print('saved', OUTPNG)
