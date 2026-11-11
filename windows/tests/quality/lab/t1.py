import sys, time, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, sonora
Q = lab.Q
# Analytic ceiling for a linear filter of length L after the delay, on the suite's rooms.
for room, cfg in Q.ROOMS.items():
    h = Q.room_response(cfg["rt60"], cfg["latency_ms"], np.random.default_rng(cfg["seed"]))
    d = int(cfg["latency_ms"] * 16)
    e = h ** 2
    direct = e[d] / e.sum()
    out = []
    for ms in (52, 160, 320, 640):
        L = ms * 16
        inside = e[d - 64:d + L].sum() / e.sum()
        out.append(f"{ms}ms {-10*np.log10(1-inside):.1f}dB")
    print(f"room {room}: direct share {100*direct:.1f}% (DRR {10*np.log10(direct/(1-direct)):.1f} dB); ceiling", ", ".join(out))
# Speaker distortion ceiling: best scalar fit of tanh(far) by far.
sc = lab.load_recording("echo-office-ser-5-snrclean")
f = sc["far"]; g = Q.speaker_distortion(f)
a = np.dot(g, f) / np.dot(f, f)
print(f"speaker distortion: nonlinear part {10*np.log10(np.sum((g-a*f)**2)/np.sum(g**2)):.1f} dB below the echo")
for name in ("echo-office-ser-5-snrclean", "echo-cafe-ser-5-snrclean", "echo-office-ser-15-snrclean", "echo-cafe-ser-15-snrclean"):
    sc = lab.load_recording(name)
    for label, tuning in (("default", None), ("linear", "output=linear")):
        t0 = time.time()
        out, st = sonora.run(sc["mic"], sc["far"], tuning)
        r = lab.score(sc, out)
        regs = sc["truth"]["regions"]
        def at(reg):  # stats at the end of a region
            k = min(reg["end"] // 160, len(st["erle"]) - 1)
            return f"erle {st['erle'][k]:.1f} delay {st['delay'][k]}"
        print(f"{name} {label}: ERLE {r['erle']:.1f} settled {r['erle_settled']:.1f} DT SI-SDR {r['dt_sisdr_in']:.1f}->{r['dt_sisdr_out']:.1f} (+{r['dt_gain']:.1f}) voice {r['dt_voice']:.1f} | "
              + " ; ".join(f"{g['kind']}@{g['end']/16000:.0f}s {at(g)}" for g in regs) + f" | {time.time()-t0:.1f}s")
