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
        if v == "solve" or v.startswith("solve:"):                      # solve or solve:hop=512;pre=1
            kw = {k: (float(x) if "." in x else int(x)) for k, x in (p.split("=") for p in v.partition(":")[2].split(";") if p)}
            out = solve.cancel(sc["mic"], sc["far"], **kw)[0]
        else:
            import pipeline
            out = {**variants.VARIANTS, **pipeline.VARIANTS}[v](sc["mic"], sc["far"])
        r = lab.score(sc, out)
        print(f"{n:28s} {v:34s} ERLE {r['erle']:5.1f} settled {r['erle_settled']:5.1f} DT gain {r['dt_gain']:5.1f} voice {r['dt_voice']:5.1f}  {time.time()-t0:5.1f}s", flush=True)
