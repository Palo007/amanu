"""Scores the C++ reference outputs and the lab's echo removers on the inputs the
aec3-upstream workflow built (AEC Challenge clips, cold and warm, and the suite's rooms).

    py -P cmp.py <work dir> <artifact dir> <variant,variant,...>
"""
import json
import sys
import time
from collections import defaultdict
from pathlib import Path

import numpy as np

sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab  # noqa: E402
import variants  # noqa: E402

Q = lab.Q
art = Path(sys.argv[2])
want = [v for v in sys.argv[3].split(",") if v]
meta = json.loads((art / "inputs" / "meta.json").read_text())
cache = Path(sys.argv[1]) / "cache-cmp"
cache.mkdir(exist_ok=True)


def f32(path):
    return np.fromfile(path, dtype=np.float32).astype(np.float64)


def lag_of(variant):
    """Each remover's constant latency in this harness, measured in a near-only region where
    the output equals the mic: AEC3 suppressed 128 samples, its linear output 64, LocalVQE -256."""
    return -256 if variant == "fast" else 64 if "linear" in variant else 128


def score(mic, near, out, regions, variant):
    n = min(len(mic), len(out))
    out = np.roll(np.pad(out[:n], (0, len(mic) - n)), -lag_of(variant))
    row = {}
    far = [r for r in regions if r["kind"] == "far"]
    if far:
        row["erle"] = float(np.mean([Q.db(Q.region_power(mic, r) / Q.region_power(out, r)) for r in far]))
    dbl = [r for r in regions if r["kind"] == "double"]
    cut = lambda x, r: x[r["start"]:r["end"]]
    sin = np.mean([Q.si_sdr(cut(mic, r), cut(near, r)) for r in dbl])
    aligned = [Q.cut_aligned(out, near, r, span=32) for r in dbl]
    row["dt_gain"] = float(np.mean([Q.si_sdr(o, t) for o, t in aligned]) - sin)
    row["dt_voice"] = float(np.mean([Q.near_gain(o, t) for o, t in aligned]))
    return row


table = defaultdict(lambda: defaultdict(list))
for name, m in sorted(meta.items()):
    group = "room" if name.startswith("room") else name.rsplit("-", 1)[1]
    mic, far, near = (f32(art / "inputs" / f"{name}.{k}.f32") for k in ("mic", "far", "near"))
    for kind in ("default", "ext256"):
        p = art / "outputs" / f"{name}.{kind}.f32"
        if p.exists():
            r = score(mic, near, f32(p), m["regions"], kind)
            table[group if group != "room" else name][f"c++ {kind}"].append(r)
    for v in want:
        c = cache / f"{name}__{v}.json"
        if c.exists():
            r = json.loads(c.read_text())
        else:
            t0 = time.time()
            out = variants.VARIANTS[v](mic, far)
            r = score(mic, near, out, m["regions"], v)
            r["secs"] = time.time() - t0
            c.write_text(json.dumps(r))
        table[group if group != "room" else name][v].append(r)

for group, rows in table.items():
    print(f"== {group}")
    for v, rs in rows.items():
        mean = lambda k: np.mean([r[k] for r in rs if k in r]) if any(k in r for r in rs) else float("nan")
        print(f"  {v:26s} n={len(rs):2d} ERLE {mean('erle'):5.1f}  DT SI-SDR gain {mean('dt_gain'):5.1f}  voice {mean('dt_voice'):6.1f}")
