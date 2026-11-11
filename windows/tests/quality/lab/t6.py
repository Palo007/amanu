import sys, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, sonora, localvqe
from scipy import signal
Q = lab.Q
fl = Q.load_fleurs(); rng = np.random.default_rng(5); parts = []
for k in rng.permutation(len(fl))[:40]:
    a = Q.read_mono(fl[k]["path"]); a = a * 10 ** (-26 / 20) / np.sqrt(Q.active_power(a)); parts += [a, np.zeros(4000)]
far = np.concatenate(parts)[: 16000 * 150]
def fb(b): return f"filter.refined.length_blocks={b};filter.coarse.length_blocks={b};filter.refined_initial.length_blocks={b};filter.coarse_initial.length_blocks={b}"
for room in ("A", "B"):
    cfg = Q.ROOMS[room]
    h = Q.room_response(cfg["rt60"], cfg["latency_ms"], np.random.default_rng(cfg["seed"]))
    echo = signal.fftconvolve(far, h)[: len(far)] * 0.5
    mic = echo + 1e-5 * np.random.default_rng(1).standard_normal(len(far))
    lat = cfg["latency_ms"]
    variants = [("default 52ms", fb(13), None)]
    for b in (13, 40, 64):
        variants += [(f"ext {b*4}ms", fb(b) + ";delay.use_external_delay_estimator=true", lat)]
        variants += [(f"nopre+hyst {b*4}ms", fb(b) + ";delay.detect_pre_echo=false;delay.hysteresis_limit_blocks=25;delay.delay_selection_thresholds.converged=200", None)]
    for label, tun, sd in variants:
        out, st = sonora.run(mic, far, tun + ";output=linear", stream_delay_ms=sd)
        out = lab.align_whole(out, mic); w = 160000
        erle = [Q.db(np.mean(mic[i:i+w]**2) / np.mean(out[i:i+w]**2)) for i in range(0, len(mic) - w + 1, w)]
        print(f"room {room} {label:18s} " + " ".join(f"{e:5.1f}" for e in erle) + f" | changes {st['changes'][-1]} delay {st['delay'][-1]} filterdelay {st['filter_delay'][-1]} usable {st['usable'].mean():.2f} transparent {st['transparent'].mean():.2f}")
