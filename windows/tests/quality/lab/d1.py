"""Denoisers on my voice with noise and no echo (the oracle's nn input) and on my clean
voice, by WER: does the engine like what a denoiser does?

    py -P d1.py <work dir> <denoiser,...>
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lab, asr, denoise
Q = lab.Q
WORK = sys.argv[1]
names = sorted(p.name for p in Q.RECORDINGS.iterdir() if p.name.startswith("echo-") and "ser-5" in p.name)


def scene(name):
    t = lab.load_recording(name)["truth"]
    return lab.build_scene(t["room"], t["ser"], t["snr"], t["noise"])


jobs = []
for d in sys.argv[2].split(","):
    f = denoise.NAMES[d]
    for n in names:
        jobs.append((f"nn+{d}", n, lambda n=n, f=f: (lambda s: f(s["near"] + s["noise"]))(scene(n))))
asr.run_jobs(WORK, jobs)
print(f"{'':12s}" + " ".join(f"{n[5:].replace('-ser-5-snr', ' '):>10s}" for n in names))
for tag in ["nn", "nn-irm"] + [f"nn+{d}" for d in sys.argv[2].split(",")]:
    rs = {**asr.rows(WORK, "near"), **asr.rows(WORK, tag)} if tag in ("nn", "nn-irm") else asr.rows(WORK, tag)
    print(f"{tag:12s}" + " ".join(f"{asr.wer([rs[n]]):10.1f}" if n in rs else f"{'-':>10s}" for n in names))
