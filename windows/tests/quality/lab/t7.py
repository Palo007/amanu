import sys, json, time, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, variants
from pathlib import Path
cache = Path(sys.argv[1]) / "cache2"; cache.mkdir(exist_ok=True)
names = sorted(p.name for p in lab.Q.RECORDINGS.iterdir() if p.name.startswith("echo-"))
want = sys.argv[2].split(",")
res = {}
for v in want:
    rows, secs = [], 0
    for n in names:
        c = cache / f"{n}__{v}.json"
        if c.exists():
            rows.append(json.loads(c.read_text())); continue
        sc = lab.load_recording(n)
        t0 = time.time(); out = variants.VARIANTS[v](sc["mic"], sc["far"]); dt = time.time() - t0
        r = lab.score(sc, out); r["secs"] = dt; r["clean"] = sc["truth"]["snr"] is None
        c.write_text(json.dumps(r)); rows.append(r)
    m = lambda k, sel=lambda r: True: np.mean([r[k] for r in rows if sel(r)])
    print(f"{v:22s} ERLE {m('erle'):5.1f}  DT SI-SDR gain {m('dt_gain'):5.1f} (clean {m('dt_gain', lambda r: r['clean']):5.1f})  voice {m('dt_voice'):6.1f}  time {sum(r['secs'] for r in rows):6.1f}s", flush=True)
