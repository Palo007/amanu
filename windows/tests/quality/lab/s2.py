"""The delay tracker against the truth of the held-out manifest: latency and drift per call."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np, lab, heldout, solve
specs = {s["name"]: s for s in json.loads(heldout.MANIFEST.read_text(encoding="utf-8"))}
for n in sys.argv[1].split(","):
    s = specs[n]; folder = heldout.RECORDINGS / n
    mic, far = lab.Q.read_mono(folder / "mic.wav"), lab.Q.read_mono(folder / "system.wav")
    truth = json.loads((folder / "truth.json").read_text(encoding="utf-8"))
    secs = solve.track(mic, far)
    want = f"{s['room']['kind']} {s['room']['latency_ms']} ms" + (f" -> {s['room2']['latency_ms']} ms at {truth['change'] / 16000:.0f} s" if s["room2"] else "") + f", {s['drift_ppm']} ppm, ser {s['ser']} snr {s['snr']}" if s["room"] else "no echo"
    got = "; ".join(f"[{a:.0f}-{b:.0f} s] {i / 16:.2f} ms {sl * 1e6 / 16000:+.1f} ppm" for a, b, i, sl in secs)
    print(f"{n}: {want}\n      {got}", flush=True)
