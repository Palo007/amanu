"""Level of a denoiser's output inside and outside my speech, to place a gate's threshold."""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import lab, pipeline, denoise
n, hop = 512, 256
def env(x):
    f = 1 + (len(x) - n) // hop
    idx = np.arange(n)[None, :] + hop * np.arange(f)[:, None]
    return 10 * np.log10((x[idx] ** 2).mean(axis=1) + 1e-12)
for name in sys.argv[1:]:
    r = lab.load_recording(name)
    t = r["truth"]; s = lab.build_scene(t["room"], t["ser"], t["snr"], t["noise"])
    e = pipeline.VARIANTS["solve-res15"](s["mic"], s["far"])
    near = env(s["near"])
    talk = near > np.percentile(near, 99) - 35                     # frames where I truly talk
    mine = np.zeros(len(near), bool)
    for g in t["regions"]:
        if g["kind"] in ("near", "double"):
            mine[g["start"] // hop: g["end"] // hop] = True
    for kind, f in (("dpdf", denoise.dpdf), ("gtcrn", denoise.gtcrn)):
        p = env(f(e)); top = np.percentile(p, 99)
        q = lambda m, a: np.percentile(p[m], a) - top
        print(f"{name[5:]:22} {kind:5} p99 {top:6.1f} | talk p5 {q(talk,5):6.1f} p25 {q(talk,25):6.1f} p50 {q(talk,50):6.1f} | other blocks p50 {q(~mine,50):6.1f} p90 {q(~mine,90):6.1f} p99 {q(~mine,99):6.1f} | pauses in my blocks p50 {q(mine & ~talk,50):6.1f} p90 {q(mine & ~talk,90):6.1f}", flush=True)
