"""N6 step 1 (CPU only): per setting-c region, R = energy(solver out)/energy(raw mic) over the gate-open
samples in dB, L = region level before scaling minus the call's median region level, and whether the
region holds my speech (near.wav activity, used for SCORING/labelling only)."""
import json, os, sys
from pathlib import Path
import numpy as np
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import n1
Q = n1.Q; RATE = n1.RATE
WORK = n1.LAB / "overnight" / "N1"
OUT = n1.LAB / "overnight" / "N6"

def my_speech_mask(near, frame=RATE // 100):
    n = len(near) // frame
    p = (near[:n * frame].reshape(n, frame) ** 2).mean(1)
    thr = p.max() * 10 ** (-25 / 10)
    act = p > thr
    # close gaps under 0.3 s
    idx = np.flatnonzero(act)
    for a, b in zip(idx[:-1], idx[1:]):
        if 1 < b - a <= 30: act[a:b] = True
    return np.repeat(act, frame)

def features(name, work=WORK, with_near=True):
    e, g = n1.gate_pieces(work, name)
    mic = Q.read_mono(n1.REC / name / "mic.wav")[:len(e)]
    x = g * e
    regs = n1.regions(g, x, **n1.SETTINGS["c"])
    my = None
    if with_near:
        near = Q.read_mono(n1.REC / name / "near.wav")
        my = my_speech_mask(near)
        my = np.pad(my, (0, max(0, len(e) - len(my))))[:len(e)]
    out = []
    for a, b in regs:
        op = g[a:b] > 0.5
        if op.sum() < RATE // 20: op = np.ones(b - a, bool)
        ee = float(np.sum(e[a:b][op] ** 2)); mm = float(np.sum(mic[a:b][op] ** 2)) + 1e-20
        lvl = 10 * np.log10(np.mean(e[a:b][op] ** 2) + 1e-20)
        out.append(dict(a=a, b=b, R=10 * np.log10(ee / mm + 1e-20), lvl=lvl,
                        my_s=(float(my[a:b].sum()) / RATE if my is not None else None), open_s=float(op.sum()) / RATE))
    med = float(np.median([r["lvl"] for r in out])) if out else 0.0
    for r in out: r["L"] = r["lvl"] - med
    return out

if __name__ == "__main__":
    res = {}
    W = OUT / "all"
    for name in n1.call_names():
        res[name] = features(name, work=(WORK if (WORK/"cache"/f"{name}.npz").exists() else W))
        t = json.loads((n1.REC / name / "truth.json").read_text(encoding="utf-8"))
        res[name] = dict(regs=res[name], words=len(Q.normalize_text(t["me"])), my_total=None)
        print(name, len(res[name]["regs"]), flush=True)
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "step1_all.json").write_text(json.dumps(res), encoding="utf-8")
