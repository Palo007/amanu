import sys, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, sonora
def fb(b): return f"filter.refined.length_blocks={b};filter.coarse.length_blocks={b};filter.refined_initial.length_blocks={b};filter.coarse_initial.length_blocks={b}"
scenes = [lab.build_scene("A", -5), lab.build_scene("B", -5),
          lab.build_scene("A", -5, rir="direct", drr_db=6), lab.build_scene("B", -5, rir="direct", drr_db=6),
          lab.build_scene("A", -5, distortion=False), lab.build_scene("B", -5, distortion=False)]
for sc in scenes:
    print(sc["name"])
    for b in (13, 20, 40, 80):
        for out_kind in ("linear", "suppressed"):
            t = fb(b) + (";output=linear" if out_kind == "linear" else "")
            out, st = sonora.run(sc["mic"], sc["far"], t)
            r = lab.score(sc, out)
            print(f"  {b*4:4d} ms {out_kind:10s} ERLE {r['erle']:5.1f} settled {r['erle_settled']:5.1f}  DT SI-SDR gain {r['dt_gain']:5.1f} voice {r['dt_voice']:6.1f}  aec3-erle(end) {np.nanmax(st['erle'][-500:]):.1f}")
