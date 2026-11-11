import sys, json, numpy as np
from pathlib import Path
from collections import defaultdict
cache = Path(sys.argv[1]) / (sys.argv[3] if len(sys.argv) > 3 else "cache2"); prefix = sys.argv[2] if len(sys.argv) > 2 else "echo-"
rows = defaultdict(list)
for f in sorted(cache.glob(prefix + "*.json")):
    rows[f.stem.split("__")[1]].append(json.loads(f.read_text()))
for v, rs in rows.items():
    m = lambda k, sel=lambda r: True: np.mean([r[k] for r in rs if sel(r) and k in r])
    print(f"{v:24s} n={len(rs):2d} ERLE {m('erle'):5.1f}  DT SI-SDR gain {m('dt_gain'):5.1f} (clean {m('dt_gain', lambda r: r.get('clean')):5.1f})  voice {m('dt_voice'):6.1f}  time {sum(r['secs'] for r in rs):6.1f}s")
