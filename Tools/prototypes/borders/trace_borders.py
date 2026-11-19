"""
Border extraction prototype: topological tracing on the pixel-corner lattice.

Every boundary edge lies between two pixels with different province IDs. Corners are graph
vertices. A corner touching 3+ boundary edges is a junction node. Each arc runs node -> node
and separates exactly one province pair, so neighbouring arcs share exact endpoints by
construction (no chaining heuristics, no snapping). Arcs with no node are closed loops.

Usage: python trace_borders.py <hoi4_dir> <out_dir> [x0 y0 w h]
"""
import sys, os, re, glob, time
import numpy as np
from PIL import Image, ImageDraw

HOI4 = sys.argv[1]

# ---------------------------------------------------------------- data
def load_ids():
    img = np.asarray(Image.open(os.path.join(HOI4, 'map/provinces.bmp')).convert('RGB')).astype(np.int64)
    key = (img[..., 0] << 16) | (img[..., 1] << 8) | img[..., 2]
    lut, kinds = {}, {}
    for line in open(os.path.join(HOI4, 'map/definition.csv'), encoding='latin-1'):
        p = line.strip().split(';')
        if len(p) < 5 or not p[0].isdigit():
            continue
        pid = int(p[0])
        lut[(int(p[1]) << 16) | (int(p[2]) << 8) | int(p[3])] = pid
        kinds[pid] = p[4]
    uniq, inv = np.unique(key, return_inverse=True)
    ids = np.array([lut.get(int(k), 0) for k in uniq], dtype=np.int32)[inv].reshape(key.shape)
    return ids, kinds

def load_states():
    prov_state, state_owner = {}, {}
    for f in glob.glob(os.path.join(HOI4, 'history/states/*.txt')):
        s = open(f, encoding='utf-8-sig', errors='replace').read()
        s = re.sub(r'#.*', '', s)
        sid = re.search(r'\bid\s*=\s*(\d+)', s)
        own = re.search(r'\bowner\s*=\s*([A-Z0-9]{3})', s)
        pv = re.search(r'\bprovinces\s*=\s*\{([^}]*)\}', s)
        if not (sid and pv):
            continue
        sid = int(sid.group(1))
        state_owner[sid] = own.group(1) if own else None
        for p in pv.group(1).split():
            if p.isdigit():
                prov_state[int(p)] = sid
    return prov_state, state_owner

# ---------------------------------------------------------------- tracing
def trace(ids):
    """Returns list of arcs: (points Nx2 float corner coords, idA, idB, closed)."""
    H, W = ids.shape
    # V[y, x]: vertical edge from corner (x,y) to (x,y+1), between pixel (x-1,y) and (x,y)
    V = np.zeros((H, W + 1), bool); V[:, 1:W] = ids[:, :-1] != ids[:, 1:]
    # Hz[y, x]: horizontal edge from corner (x,y) to (x+1,y), between pixel (x,y-1) and (x,y)
    Hz = np.zeros((H + 1, W), bool); Hz[1:H, :] = ids[:-1, :] != ids[1:, :]

    deg = np.zeros((H + 1, W + 1), np.int8)
    deg[:-1, :] += V; deg[1:, :] += V          # down / up
    deg[:, :-1] += Hz; deg[:, 1:] += Hz        # right / left
    node = (deg >= 3) | (deg == 1)

    visV = np.zeros_like(V); visH = np.zeros_like(Hz)

    # edge directions from a corner: (dx, dy)
    def edges_at(cx, cy):
        out = []
        if cy > 0 and V[cy - 1, cx] and not visV[cy - 1, cx]: out.append((0, -1))
        if cy < H and V[cy, cx] and not visV[cy, cx]: out.append((0, 1))
        if cx > 0 and Hz[cy, cx - 1] and not visH[cy, cx - 1]: out.append((-1, 0))
        if cx < W and Hz[cy, cx] and not visH[cy, cx]: out.append((1, 0))
        return out

    def mark(cx, cy, dx, dy):
        """Mark edge from corner (cx,cy) in direction; return the pixel pair it separates."""
        if dx == 0:
            y = cy if dy > 0 else cy - 1
            visV[y, cx] = True
            return ids[y, cx - 1], ids[y, cx]
        x = cx if dx > 0 else cx - 1
        visH[cy, x] = True
        return ids[cy - 1, x], ids[cy, x]

    def walk(cx, cy, dx, dy, closed_start=None):
        a, b = mark(cx, cy, dx, dy)
        pts = [(cx, cy)]
        cx += dx; cy += dy
        while True:
            pts.append((cx, cy))
            if closed_start is None and node[cy, cx]:
                break
            if closed_start is not None and (cx, cy) == closed_start:
                break
            nxt = edges_at(cx, cy)
            if not nxt:
                break
            dx, dy = nxt[0]
            mark(cx, cy, dx, dy)
            cx += dx; cy += dy
        return np.array(pts, float), int(min(a, b)), int(max(a, b))

    arcs = []
    ny, nx = np.nonzero(node)
    for cy, cx in zip(ny, nx):
        for dx, dy in edges_at(cx, cy):
            # edges_at is re-evaluated per arc since walks mark edges
            if dx == 0 and visV[cy if dy > 0 else cy - 1, cx]: continue
            if dy == 0 and visH[cy, cx if dx > 0 else cx - 1]: continue
            p, a, b = walk(cx, cy, dx, dy)
            arcs.append((p, a, b, False))
    # remaining edges form closed loops through degree-2 corners
    for arr, vis, isV in ((V, visV, True), (Hz, visH, False)):
        ys, xs = np.nonzero(arr & ~vis)
        for y, x in zip(ys, xs):
            if vis[y, x]:
                continue
            cx, cy = x, y
            p, a, b = walk(cx, cy, 0 if isV else 1, 1 if isV else 0, closed_start=(cx, cy))
            arcs.append((p, a, b, True))
    return arcs, node

# ---------------------------------------------------------------- smoothing
def midpoints(p, closed):
    """Replace staircase corners with edge midpoints; open arcs keep their junction endpoints."""
    m = (p[:-1] + p[1:]) * 0.5
    if closed:
        return m
    return np.vstack([p[:1], m, p[-1:]])

def chaikin(p, closed, it):
    for _ in range(it):
        if len(p) < 3:
            return p
        if closed:
            q = np.roll(p, -1, 0)
            p = np.vstack([np.column_stack([0.75 * p + 0.25 * q, 0.25 * p + 0.75 * q]).reshape(-1, 2)])
        else:
            a, b = p[:-1], p[1:]
            mid = np.column_stack([0.75 * a + 0.25 * b, 0.25 * a + 0.75 * b]).reshape(-1, 2)
            p = np.vstack([p[:1], mid[1:-1], p[-1:]]) if len(p) > 2 else p
    return p

def rdp(p, eps):
    if len(p) < 3:
        return p
    keep = np.zeros(len(p), bool); keep[0] = keep[-1] = True
    stack = [(0, len(p) - 1)]
    while stack:
        i, j = stack.pop()
        if j <= i + 1:
            continue
        a, b = p[i], p[j]
        d = b - a
        n = np.hypot(*d)
        seg = p[i + 1:j] - a
        dist = np.abs(seg[:, 0] * d[1] - seg[:, 1] * d[0]) / n if n > 1e-9 else np.hypot(seg[:, 0], seg[:, 1])
        k = int(np.argmax(dist))
        if dist[k] > eps:
            keep[i + 1 + k] = True
            stack += [(i, i + 1 + k), (i + 1 + k, j)]
    return p[keep]

def smooth(p, closed, chaikin_it=2, eps=0.2, pre_eps=0.0):
    q = midpoints(p, closed)
    if pre_eps > 0:
        q = rdp(np.vstack([q, q[:1]]), pre_eps)[:-1] if closed else rdp(q, pre_eps)
    q = chaikin(q, closed, chaikin_it)
    if closed:
        q = np.vstack([q, q[:1]])
    return rdp(q, eps)

# ---------------------------------------------------------------- classification
def classify(a, b, kinds, prov_state, state_owner):
    ka, kb = kinds.get(a, 'sea'), kinds.get(b, 'sea')
    land_a, land_b = ka == 'land', kb == 'land'
    if a == 0 or b == 0:
        return None
    if not land_a and not land_b:
        return 'sea'
    if land_a != land_b:
        return 'coast'
    sa, sb = prov_state.get(a), prov_state.get(b)
    oa, ob = state_owner.get(sa), state_owner.get(sb)
    if oa != ob:
        return 'country'
    if sa != sb:
        return 'state'
    return 'province'

# ---------------------------------------------------------------- main
if __name__ == '__main__':
    OUT = sys.argv[2]
    os.makedirs(OUT, exist_ok=True)
    t0 = time.time()
    ids_full, kinds = load_ids()
    prov_state, state_owner = load_states()
    print(f'loaded {ids_full.shape} in {time.time()-t0:.1f}s, {len(prov_state)} provinces in states')

    if len(sys.argv) >= 7:
        x0, y0, w, h = map(int, sys.argv[3:7])
        ids = ids_full[y0:y0 + h, x0:x0 + w]
    else:
        x0, y0 = 0, 0
        ids = ids_full
    t0 = time.time()
    arcs, node = trace(ids)
    t_trace = time.time() - t0
    closed = sum(1 for a in arcs if a[3])
    nodes = int(node.sum())
    raw_pts = sum(len(a[0]) for a in arcs)
    print(f'traced {len(arcs)} arcs ({closed} closed loops), {nodes} nodes, {raw_pts} raw points in {t_trace:.1f}s')

    # topology checks
    pairs_px = set()
    H, W = ids.shape
    hv = ids[:, :-1] != ids[:, 1:]
    for a, b in zip(ids[:, :-1][hv], ids[:, 1:][hv]): pairs_px.add((min(a, b), max(a, b)))
    vv = ids[:-1, :] != ids[1:, :]
    for a, b in zip(ids[:-1, :][vv], ids[1:, :][vv]): pairs_px.add((min(a, b), max(a, b)))
    pairs_arcs = {(a[1], a[2]) for a in arcs}
    print(f'adjacent pairs from pixels: {len(pairs_px)}, from arcs: {len(pairs_arcs)}, missing: {len(pairs_px - pairs_arcs)}')

    np.save(os.path.join(OUT, 'arcs_meta.npy'), np.array([(a[1], a[2], a[3]) for a in arcs]))
    import pickle
    pickle.dump({'arcs': arcs, 'origin': (x0, y0)}, open(os.path.join(OUT, 'arcs.pkl'), 'wb'))
