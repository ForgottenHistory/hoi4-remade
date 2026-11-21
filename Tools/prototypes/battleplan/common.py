"""Shared loading for the battle plan prototypes: HOI4 province ids, owners, traced + smoothed border arcs
(same tracing and smoothing settings as the engine's BorderGraph: pre RDP 0.75, Chaikin 3, post RDP 0.15)."""
import os, sys, pickle
import numpy as np
from PIL import Image

HOI4 = r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV"
HERE = os.path.dirname(os.path.abspath(__file__))
_argv = sys.argv
sys.argv = [sys.argv[0], HOI4]
sys.path.insert(0, os.path.join(HERE, '..', 'borders'))
import trace_borders as tb  # noqa: E402
sys.argv = _argv

CACHE = os.path.join(HERE, '_cache.pkl')


def load(crop):
    """crop = (x0, y0, w, h) in map pixels. Returns ids crop, owner dict, arcs [(pts, a, b, closed)] in crop coords."""
    if os.path.exists(CACHE):
        with open(CACHE, 'rb') as f:
            c = pickle.load(f)
        if c['crop'] == crop:
            return c['ids'], c['owner'], c['arcs']
    ids, kinds = tb.load_ids()
    prov_state, state_owner = tb.load_states()
    owner = {p: state_owner.get(s) for p, s in prov_state.items()}
    x0, y0, w, h = crop
    sub = ids[y0:y0 + h, x0:x0 + w].copy()
    raw, _ = tb.trace(sub)
    arcs = [(tb.smooth(p, closed, 3, 0.15, 0.75), a, b, closed) for p, a, b, closed in raw]
    with open(CACHE, 'wb') as f:
        pickle.dump({'crop': crop, 'ids': sub, 'owner': owner, 'arcs': arcs}, f)
    return sub, owner, arcs


def centroid(ids, pid):
    ys, xs = np.nonzero(ids == pid)
    return np.array([xs.mean(), ys.mean()]) if len(xs) else None
