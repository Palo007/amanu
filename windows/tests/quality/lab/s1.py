"""First look at solve.cancel against the delay-primed AEC3 filter and LocalVQE fast."""
import os, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np, lab, solve, variants
names = sys.argv[1].split(",")
which = sys.argv[2].split(",") if len(sys.argv) > 2 else ["solve"]
for n in names:
    sc = lab.load_recording(n)
    for v in which:
        t0 = time.time()
        out = solve.cancel(sc["mic"], sc["far"])[0] if v == "solve" else variants.VARIANTS[v](sc["mic"], sc["far"])
        r = lab.score(sc, out)
        print(f"{n:28s} {v:14s} ERLE {r['erle']:5.1f} settled {r['erle_settled']:5.1f} DT gain {r['dt_gain']:5.1f} voice {r['dt_voice']:5.1f}  {time.time()-t0:5.1f}s", flush=True)
