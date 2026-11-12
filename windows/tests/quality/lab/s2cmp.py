"""S2: decisions of cached configurations against p2 (the app today: pieces of 30 s, 2 s of sound each side)."""
import os, sys
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np, pipeline, s1q_lib as L, s2q
rows = {}
for n in L.suite():
    e = L.canceller(n); snr = L.snr_of(n)
    g = {c: L.frames10(pipeline.speech_gate(e, np.load(L.HOME / "cache2" / f"{n}__{c}.npy").astype(np.float64))) for c in ("p2", "p1", "p0.5", "s2")}
    pl = s2q.plan(e, 2.0, 30.0); skipped = 1 - sum(en - s for f, t, s, en in pl) / len(e)
    for c in ("p1", "p0.5", "s2"):
        k = min(len(g[c]), len(g["p2"])); rows.setdefault(c, []).append((n, snr, k, int((g[c][:k] != g["p2"][:k]).sum()), skipped))
for c, r in rows.items():
    nz = [x for x in r if x[1] in (0, -5)]
    print(f"{c:5s} vs p2: all 40 {100*sum(x[3] for x in r)/sum(x[2] for x in r):.3f}%  noisy 14 {100*sum(x[3] for x in nz)/sum(x[2] for x in nz):.3f}%  worst call {max(100*x[3]/x[2] for x in r):.2f}%  audio skipped (s2) mean {100*np.mean([x[4] for x in r]):.1f}% max {100*max(x[4] for x in r):.1f}%")
