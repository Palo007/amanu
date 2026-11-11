"""Long held-out calls: true echo removal of solve.cancel with the fit in blocks against
one fit over the whole call.  py -P s3.py <block_s,...>   (inf = whole call)"""
import os, sys, json, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import heldout, solve
specs = [s for s in json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "heldout-manifest.json"))) if s["name"].startswith("ho-long")]
for spec in specs:
    c = heldout.components(spec)
    mic = c["near"] + c["echo"] + c["noise"]
    act = np.abs(c["echo"]) > 0
    line = []
    for b in sys.argv[1].split(","):
        t0 = time.time()
        out, info = solve.cancel(mic, c["far"], block_s=float(b))
        left = c["echo"] - info["echo"]
        line.append(f"block {b}: {10 * np.log10(np.sum(c['echo'][act] ** 2) / np.sum(left[act] ** 2)):5.1f} dB {time.time() - t0:4.0f} s")
    print(spec["name"], spec.get("room"), spec.get("snr"), spec.get("drift_ppm"), len(info["sections"]), " | ".join(line), flush=True)
