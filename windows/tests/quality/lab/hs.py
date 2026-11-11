"""Signal scores on the held-out set (no transcription): ERLE in far-only parts, and in
double talk the SI-SDR gain and my voice level, per variant, cached per call.

    py -P hs.py <work dir> <variant,...> [name regex]      variants from pipeline.VARIANTS or variants.VARIANTS
"""
import json, os, re, sys, time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np


def one(args):
    work, v, n = args
    c = Path(work) / "hs" / f"{n}__{v}.json"
    if c.exists():
        return json.loads(c.read_text())
    import lab, heldout, variants
    try:
        import pipeline
        table = {**variants.VARIANTS, **pipeline.VARIANTS}
    except ImportError:
        table = variants.VARIANTS
    folder = heldout.RECORDINGS / n
    sc = {"mic": lab.Q.read_mono(folder / "mic.wav"), "far": lab.Q.read_mono(folder / "system.wav"),
          "near": lab.Q.read_mono(folder / "near.wav"), "truth": json.loads((folder / "truth.json").read_text(encoding="utf-8"))}
    t0 = time.time(); out = table[v](sc["mic"], sc["far"]); dt = time.time() - t0
    r = lab.score(sc, out)
    t = sc["truth"]
    r.update(secs=dt, room=t["room"], ser=t["ser"], snr=t["snr"], drift=t["drift_ppm"], change=t["change"] is not None)
    c.parent.mkdir(exist_ok=True)
    c.write_text(json.dumps(r))
    return r


if __name__ == "__main__":
    import heldout
    work = sys.argv[1]
    pattern = sys.argv[3] if len(sys.argv) > 3 else r"^ho-\d"
    names = sorted(p.name for p in heldout.RECORDINGS.iterdir() if re.search(pattern, p.name))
    for v in sys.argv[2].split(","):
        with ProcessPoolExecutor(int(os.environ.get("LAB_PROCS", "5"))) as pool:
            rows = [r for r in pool.map(one, [(work, v, n) for n in names]) if r["room"] != "none"]
        m = lambda k, sel=lambda r: True: np.mean([r[k] for r in rows if sel(r)] or [np.nan])
        groups = {"all": lambda r: True, "steady": lambda r: not r["drift"] and not r["change"], "drift": lambda r: r["drift"] and not r["change"],
                  "change": lambda r: r["change"], "no noise": lambda r: r["snr"] is None}
        print(f"{v}  ({sum(r['secs'] for r in rows):.0f} s)")
        for g, sel in groups.items():
            print(f"   {g:9s} n={sum(1 for r in rows if sel(r)):2d}  ERLE {m('erle', sel):5.1f}  DT gain {m('dt_gain', sel):5.1f}  voice {m('dt_voice', sel):5.1f}", flush=True)
