"""Ceiling study: what the engine makes of my voice when the echo, the noise or both are
removed perfectly, so the gap to each remover says how much of its error is echo, noise
or the engine. The oracles see near.wav, the echo and the noise; no remover may.

    py -P oracle.py <work dir>

Tags (under <work>/wer2): near = my voice alone; nn = my voice and the noise, no echo;
irm-echo = an ideal ratio mask on the mic that removes the echo only; irm-all = one that
removes echo and noise, also with gain floors 0.3 and 0.5; nn-irm = perfect echo removal
and an ideal noise mask, with the same floors.
"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from scipy import signal
import lab, asr
Q = lab.Q
WORK = sys.argv[1]
N, HOP = 512, 128


def stft(x):
    return signal.stft(x, lab.RATE, nperseg=N, noverlap=N - HOP)[2]


def istft(X, length):
    y = signal.istft(X, lab.RATE, nperseg=N, noverlap=N - HOP)[1]
    return np.pad(y, (0, max(0, length - len(y))))[:length]


def masked(mix, keep, drop, floor=0.0):
    """mix times the ideal ratio mask sqrt(|keep|^2 / (|keep|^2 + |drop|^2)), never below floor."""
    K, D = np.abs(stft(keep)) ** 2, np.abs(stft(drop)) ** 2
    g = np.maximum(np.sqrt(K / (K + D + 1e-20)), floor)
    return istft(stft(mix) * g, len(mix))


def scene(name):
    t = lab.load_recording(name)["truth"]
    return lab.build_scene(t["room"], t["ser"], t["snr"], t["noise"])


names = sorted(p.name for p in Q.RECORDINGS.iterdir() if p.name.startswith("echo-"))
distinct = [n for n in names if "ser-5" in n]          # one call per noise condition
jobs = []
for n in distinct:
    if "snrclean" in n:
        jobs.append(("near", n, lambda n=n: scene(n)["near"]))
    else:
        jobs.append(("nn", n, lambda n=n: (lambda s: s["near"] + s["noise"])(scene(n))))
        for floor in (0.0, 0.3, 0.5):
            tag = "nn-irm" + (f"-f{int(floor * 100)}" if floor else "")
            jobs.append((tag, n, lambda n=n, floor=floor: (lambda s: masked(s["near"] + s["noise"], s["near"], s["noise"], floor))(scene(n))))
for n in names:
    jobs.append(("irm-echo", n, lambda n=n: (lambda s: masked(s["mic"], s["near"] + s["noise"], s["echo"]))(scene(n))))
    for floor in (0.0, 0.3, 0.5):
        tag = "irm-all" + (f"-f{int(floor * 100)}" if floor else "")
        jobs.append((tag, n, lambda n=n, floor=floor: (lambda s: masked(s["mic"], s["near"], s["echo"] + s["noise"], floor))(scene(n))))
if len(sys.argv) > 2:
    jobs = [j for j in jobs if re.search(sys.argv[2], j[0])]
asr.run_jobs(WORK, jobs)
for tag in dict.fromkeys(j[0] for j in jobs):
    print(asr.summary(WORK, tag), flush=True)
