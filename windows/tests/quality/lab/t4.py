import sys, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, sonora, localvqe
from scipy import signal
Q = lab.Q
fl = Q.load_fleurs()
rng = np.random.default_rng(5)
parts = []
for k in rng.permutation(len(fl))[:40]:
    a = Q.read_mono(fl[k]["path"]); a = a * 10 ** (-26 / 20) / np.sqrt(Q.active_power(a)); parts += [a, np.zeros(4000)]
far = np.concatenate(parts)[: 16000 * 150]
def fb(b): return f"filter.refined.length_blocks={b};filter.coarse.length_blocks={b};filter.refined_initial.length_blocks={b};filter.coarse_initial.length_blocks={b}"
for room in ("A", "B"):
    cfg = Q.ROOMS[room]
    for rir in ("suite", "direct"):
        h = Q.room_response(cfg["rt60"], cfg["latency_ms"], np.random.default_rng(cfg["seed"])) if rir == "suite" else lab.direct_room(cfg["latency_ms"], cfg["rt60"], 6, np.random.default_rng(cfg["seed"]))
        echo = signal.fftconvolve(far, h)[: len(far)] * 0.5
        mic = echo + 1e-5 * np.random.default_rng(1).standard_normal(len(far))
        rows = {}
        for label, fn in [(f"aec3 {b*4}ms lin", (lambda b: lambda: sonora.run(mic, far, fb(b) + ";output=linear"))(b)) for b in (13, 40, 80)] + [("fast", lambda: (localvqe.run(mic, far, "fast")[0], None))]:
            out, st = fn()
            out = lab.align_whole(out, mic)
            w = 16000 * 10
            erle = [Q.db(np.mean(mic[i:i+w]**2) / np.mean(out[i:i+w]**2)) for i in range(0, len(mic) - w + 1, w)]
            extra = ""
            if st is not None:
                d = st["delay"]; ch = int(np.sum(d[1:] != d[:-1]))
                extra = f" delay {d[-1]} changes {ch}"
            print(f"room {room} {rir:6s} {label:16s} ERLE per 10 s: " + " ".join(f"{e:5.1f}" for e in erle) + extra)
