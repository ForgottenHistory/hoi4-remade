"""
River extraction prototype: trace 1-pixel river lines (HOI4 rivers.bmp) into smoothed polylines
with a width per point.

Pixels: palette 3..11 = width (narrow..wide), 0/1/2 = source / tributary join / split markers,
254 = land, 255 = sea. Markers take the width of their neighbours.

Connectivity: 4-neighbours always connect; a diagonal neighbour only when neither of the two
orthogonal pixels between them is a river (so L-bends are not junctions).
Nodes: degree != 2. Arcs run node to node through pixel centres; leftovers are loops.

Usage: python trace_rivers.py <hoi4_dir> <out.png> <x0> <y0> <w> <h> [scale]
"""
import sys, os
import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'borders'))

def load_rivers(hoi4):
    a = np.asarray(Image.open(os.path.join(hoi4, 'map', 'rivers.bmp'))).astype(np.int32)
    width = np.zeros(a.shape, np.float32)
    river = a <= 11
    width[(a >= 3) & (a <= 11)] = a[(a >= 3) & (a <= 11)] - 2   # 1..9
    # markers: take max neighbour width (repeat to fill marker chains)
    H, W = a.shape
    markers = np.argwhere(river & (width == 0))
    for _ in range(3):
        for y, x in markers:
            if width[y, x] > 0: continue
            nb = width[max(y-1,0):y+2, max(x-1,0):x+2]
            if nb.max() > 0: width[y, x] = nb.max()
    width[river & (width == 0)] = 1
    return river, width

NB4 = [(1,0),(-1,0),(0,1),(0,-1)]
NBD = [(1,1),(1,-1),(-1,1),(-1,-1)]

def neighbours(river, x, y):
    H, W = river.shape
    out = []
    for dx, dy in NB4:
        nx, ny = x+dx, y+dy
        if 0 <= nx < W and 0 <= ny < H and river[ny, nx]: out.append((nx, ny))
    for dx, dy in NBD:
        nx, ny = x+dx, y+dy
        if 0 <= nx < W and 0 <= ny < H and river[ny, nx] and not river[y, nx] and not river[ny, x]:
            out.append((nx, ny))
    return out

def trace(river):
    ys, xs = np.nonzero(river)
    pix = list(zip(xs.tolist(), ys.tolist()))
    nbs = {p: neighbours(river, *p) for p in pix}
    node = {p for p in pix if len(nbs[p]) != 2}
    visited = set()  # undirected edges
    def ekey(a, b): return (a, b) if a < b else (b, a)
    arcs = []
    for n in node:
        for m in nbs[n]:
            if ekey(n, m) in visited: continue
            path = [n]; prev, cur = n, m
            visited.add(ekey(prev, cur))
            while True:
                path.append(cur)
                if cur in node: break
                nxt = [q for q in nbs[cur] if q != prev]
                if not nxt: break
                prev, cur = cur, nxt[0]
                if ekey(prev, cur) in visited: path.append(cur); break
                visited.add(ekey(prev, cur))
            arcs.append((path, False))
    for p in pix:
        for m in nbs[p]:
            if ekey(p, m) in visited: continue
            path = [p]; prev, cur = p, m
            visited.add(ekey(prev, cur))
            while cur != p:
                path.append(cur)
                nxt = [q for q in nbs[cur] if q != prev and ekey(cur, q) not in visited]
                if not nxt: break
                prev, cur = cur, nxt[0]
                visited.add(ekey(prev, cur))
            path.append(p)
            arcs.append((path, True))
    return arcs, node, nbs

# --- smoothing with width as a third coordinate (endpoints fixed)
def rdp(p, eps):
    if len(p) < 3: return p
    keep = np.zeros(len(p), bool); keep[0] = keep[-1] = True
    st = [(0, len(p)-1)]
    while st:
        i, j = st.pop()
        if j <= i+1: continue
        a, d = p[i, :2], p[j, :2] - p[i, :2]
        n = np.hypot(*d); seg = p[i+1:j, :2] - a
        dist = np.abs(seg[:,0]*d[1] - seg[:,1]*d[0]) / n if n > 1e-9 else np.hypot(seg[:,0], seg[:,1])
        k = int(np.argmax(dist))
        if dist[k] > eps:
            keep[i+1+k] = True; st += [(i, i+1+k), (i+1+k, j)]
    return p[keep]

def chaikin(p, it):
    for _ in range(it):
        if len(p) < 3: return p
        a, b = p[:-1], p[1:]
        mid = np.stack([0.75*a+0.25*b, 0.25*a+0.75*b], 1).reshape(-1, p.shape[1])
        p = np.vstack([p[:1], mid[1:-1], p[-1:]])
    return p

def smooth(path, width):
    p = np.array([(x+0.5, y+0.5, width[y, x]) for x, y in path], float)
    p = rdp(p, 0.75)
    p = chaikin(p, 3)
    return p

if __name__ == '__main__':
    hoi4, out = sys.argv[1], sys.argv[2]
    x0, y0, cw, ch = map(int, sys.argv[3:7])
    S = int(sys.argv[7]) if len(sys.argv) > 7 else 4
    river, width = load_rivers(hoi4)
    import time; t = time.time()
    arcs, node, nbs = trace(river)
    print(f'{river.sum()} river px, {len(arcs)} arcs ({sum(1 for a in arcs if a[1])} loops), {len(node)} nodes, {time.time()-t:.1f}s')
    deg = {}
    for p in node: deg[len(nbs[p])] = deg.get(len(nbs[p]), 0) + 1
    print('node degrees', dict(sorted(deg.items())))

    a = np.asarray(Image.open(os.path.join(hoi4, 'map', 'rivers.bmp')))[y0:y0+ch, x0:x0+cw]
    bg = np.where((a == 255)[..., None], np.array([70,110,150], np.uint8), np.array([190,195,170], np.uint8))
    SS = 3
    def render(smoothed):
        img = Image.fromarray(bg.astype(np.uint8)).resize((cw*S*SS, ch*S*SS), Image.NEAREST)
        d = ImageDraw.Draw(img)
        for path, closed in arcs:
            if not any(x0 <= x < x0+cw and y0 <= y < y0+ch for x, y in path): continue
            if smoothed:
                p = smooth(path, width)
                for i in range(len(p)-1):
                    wpx = (0.35 + 0.18*(p[i,2]+p[i+1,2])/2) * S * SS
                    d.line([((p[i,0]-x0)*S*SS, (p[i,1]-y0)*S*SS), ((p[i+1,0]-x0)*S*SS, (p[i+1,1]-y0)*S*SS)],
                           fill=(40, 80, 160), width=max(1, int(wpx)), joint='curve')
            else:
                for x, y in path:
                    d.rectangle([(x-x0)*S*SS, (y-y0)*S*SS, (x-x0+1)*S*SS-1, (y-y0+1)*S*SS-1], fill=(40, 80, 160))
        for x, y in node:
            if x0 <= x < x0+cw and y0 <= y < y0+ch:
                cx, cy = (x+0.5-x0)*S*SS, (y+0.5-y0)*S*SS; r = 1.2*SS
                d.ellipse([cx-r, cy-r, cx+r, cy+r], fill=(220, 0, 0))
        return img.resize((cw*S, ch*S), Image.LANCZOS)
    raw, smo = render(False), render(True)
    both = Image.new('RGB', (raw.width*2+8, raw.height), 'white')
    both.paste(raw, (0, 0)); both.paste(smo, (raw.width+8, 0)); both.save(out)
    print('saved', out)
