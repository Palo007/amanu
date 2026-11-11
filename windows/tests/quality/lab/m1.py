"""Why does turning the mic down while the far end talks help the engine in loud noise?
Oracle inputs: my voice and the noise, no echo, with the noise turned down in parts.

    py -P m1.py <work dir>

Tags: nn-duckfar = noise at 0.15 in the far-only blocks; nn-duckfar0 = noise at 0 there;
nn-ducknotme = noise at 0.15 everywhere outside the blocks where I talk.
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import lab, asr
Q = lab.Q
WORK = sys.argv[1]


def scene(name):
    t = lab.load_recording(name)["truth"]
    return lab.build_scene(t["room"], t["ser"], t["snr"], t["noise"])


def ducked(name, kinds, gain, inside):
    s = scene(name)
    g = np.full(len(s["near"]), 1.0 if inside else gain)
    for r in s["truth"]["regions"]:
        if r["kind"] in kinds:
            g[r["start"]:r["end"]] = gain if inside else 1.0
    return s["near"] + s["noise"] * g


names = sorted(p.name for p in Q.RECORDINGS.iterdir() if p.name.startswith("echo-") and "ser-5" in p.name
               and ("snr+0" in p.name or "snr-5" in p.name))
jobs = []
for n in names:
    jobs.append(("nn-duckfar", n, lambda n=n: ducked(n, ("far",), 0.15, True)))
    jobs.append(("nn-duckfar0", n, lambda n=n: ducked(n, ("far",), 0.0, True)))
    jobs.append(("nn-ducknotme", n, lambda n=n: ducked(n, ("near", "double"), 0.15, False)))
asr.run_jobs(WORK, jobs)
for tag in ("nn", "nn-duckfar", "nn-duckfar0", "nn-ducknotme", "nn-irm"):
    rs = {r["name"]: r for r in asr.rows(WORK, tag)} if False else None
    print(f"{tag:14}", "  ".join(f"{n[5:]}: {100 * c['errors'] / c['words']:5.1f}" for n in names for c in [asr.cached(WORK, tag, n)] if c))
