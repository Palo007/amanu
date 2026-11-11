"""Held-out set, WER: Amanu's own echo models (run inside Amanu.exe) and lab removers.

    py -P ho.py <work dir> amanu:<echo_model,...>      e.g. amanu:thorough,sonora-delay-floor,off
    py -P ho.py <work dir> lab:<variant,...>            variants from pipeline.VARIANTS
    py -P ho.py <work dir> show:<tag,...> [baseline tag]

Tags are ho/<name>. `show` prints WER per tag and, against the baseline, the difference
with a 95 % interval from a paired bootstrap over calls.
"""
import os, sys, time
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lab, asr, heldout
WORK = sys.argv[1]
kind, _, what = sys.argv[2].partition(":")
names = sorted(p.name for p in heldout.RECORDINGS.iterdir())
if kind == "amanu":
    for model in what.split(","):
        with ThreadPoolExecutor(3) as pool:
            list(pool.map(lambda n: asr.amanu(WORK, f"ho/{model}", n, heldout.RECORDINGS / n, model), names))
        print(asr.summary(WORK, f"ho/{model}"), flush=True)
elif kind == "lab":
    import pipeline
    for v in what.split(","):
        def make(n, v=v):
            mic, far = lab.Q.read_mono(heldout.RECORDINGS / n / "mic.wav"), lab.Q.read_mono(heldout.RECORDINGS / n / "system.wav")
            t0 = time.time(); out = pipeline.VARIANTS[v](mic, far)
            return out, time.time() - t0
        asr.run_jobs(WORK, [(f"ho/{v}", n, lambda n=n: make(n), heldout.RECORDINGS / n) for n in names])
        print(asr.summary(WORK, f"ho/{v}"), flush=True)
else:
    base = asr.rows(WORK, f"ho/{sys.argv[3]}") if len(sys.argv) > 3 else None
    for tag in what.split(","):
        rs = asr.rows(WORK, f"ho/{tag}")
        line = asr.summary(WORK, f"ho/{tag}") + f"  echo time {sum(r['secs'] for r in rs.values()):6.0f} s"
        if base is not None and tag != sys.argv[3]:
            d, lo, hi = asr.bootstrap(rs, base)
            line += f"  against {sys.argv[3]}: {d:+.1f} [{lo:+.1f}, {hi:+.1f}]"
        print(line)
