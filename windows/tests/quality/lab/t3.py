import sys, time, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, sonora, localvqe
scenes = [lab.build_scene("A", -5), lab.build_scene("B", -5),
          lab.build_scene("A", -5, rir="direct", drr_db=6), lab.build_scene("B", -5, rir="direct", drr_db=6)]
for sc in scenes:
    for name, fn in (("fast", lambda s: localvqe.run(s["mic"], s["far"], "fast")[0]),
                     ("sonora", lambda s: sonora.run(s["mic"], s["far"], None, stats=False)[0]),
                     ("sonora-linear", lambda s: sonora.run(s["mic"], s["far"], "output=linear", stats=False)[0])):
        t0 = time.time(); out = fn(sc); dt = time.time() - t0
        r = lab.score(sc, out)
        print(f"{sc['name']:28s} {name:14s} ERLE {r['erle']:5.1f} settled {r['erle_settled']:5.1f} DT SI-SDR gain {r['dt_gain']:5.1f} voice {r['dt_voice']:6.1f}  {dt:5.1f}s")
