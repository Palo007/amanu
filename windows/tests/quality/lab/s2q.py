"""Track S2 (overnight 2026-11-12): a cheaper speech gate with the float DPDFNet, same decisions.

    py -P s2q.py time <procs>        the 5 timing calls, every configuration back to back
    py -P s2q.py dec <config,...>    decisions of the configurations on the 40 calls against the float whole-call run
Configurations: p<ctx>  pieces of 30 s with <ctx> s of sound each side (p2 is the app today);
s<ctx>  the same, but the model is skipped where the canceller is 30 dB under its speech level.
Caches under overnight/S1/cache2.
"""
import os, sys, time
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pathlib import Path
import numpy as np
import s1q_lib as L

CORE = 30 * 16000
HOP, N = 256, 512


def plan(e, ctx_s, skip_db=None, min_skip_s=1.0):
    """[(from, to, start, end)]: the model runs on e[from:to] and its output counts for [start:end]."""
    ctx = int(ctx_s * 16000)
    n = len(e)
    ranges = [(0, n)]
    if skip_db is not None:
        frames = 1 + (n - N) // HOP
        idx = np.arange(N)[None, :] + HOP * np.arange(frames)[:, None]
        p = (e[idx] ** 2).mean(axis=1)
        k = int(round(0.3 * 16000 / HOP))
        p = np.convolve(np.pad(p, k // 2, mode="edge"), np.ones(k) / k, "valid")[:frames]
        quiet = p < np.percentile(p, 99) * 10 ** (-skip_db / 10)
        edges = np.flatnonzero(np.diff(np.concatenate([[0], quiet.astype(int), [0]])))
        ranges, pos = [], 0
        for a, b in zip(edges[::2], edges[1::2]):
            if (b - a) * HOP < min_skip_s * 16000: continue
            s, t = a * HOP + N // 2, b * HOP + N // 2           # the samples these frames are centred on
            if s > pos: ranges.append((pos, s))
            pos = max(pos, t)
        if pos < n: ranges.append((pos, n))
    out = []
    for a, b in ranges:
        for s in range(a, b, CORE):
            t = min(b, s + CORE)
            out.append((max(0, s - ctx), min(n, t + ctx), s, t))
    return out


_d = None
def _work(job):
    global _d
    if _d is None: _d = L.denoiser("float")
    x, = job
    return L.run(_d, x)


def assemble(e, pl, outs):
    c = np.zeros(len(e))
    for (f, t, s, en), o in zip(pl, outs):
        c[s:en] = o[s - f: en - f]
    return c


def compute(e, ctx_s, skip_db, pool=None):
    pl = plan(e, ctx_s, skip_db)
    jobs = [(np.ascontiguousarray(e[f:t], dtype=np.float32),) for f, t, s, en in pl]
    outs = pool.map(_work, jobs, chunksize=1) if pool else [_work(j) for j in jobs]
    return assemble(e, pl, outs), pl


def cfg(name):
    return float(name[1:]), (30.0 if name[0] == "s" else None)


def main():
    import multiprocessing as mp, pipeline
    names = L.suite()
    if sys.argv[1] == "time":
        procs = [int(x) for x in sys.argv[2].split(",")]
        sel = names[:: max(1, len(names) // 5)][:5]
        es = [L.canceller(n) for n in sel]
        audio = sum(len(x) for x in es) / 16000
        pools = {k: mp.Pool(k) for k in procs}
        for p in pools.values(): p.map(_work, [(np.zeros(16000, np.float32),)] * 8)      # start the workers
        runs = [("whole", None, "p2", None)]
        runs += [(f"p2 x{k}", k, "p2", k) for k in procs] + [("p1 x1", 1, "p1", None), ("p0.5 x1", 1, "p0.5", None),
                 ("s2 x1", 1, "s2", None)] + [(f"s2 x{k}", k, "s2", k) for k in procs if k > 1]
        res = {}
        for label, _, c, k in runs:
            t = time.time()
            for e in es:
                if label == "whole":
                    global _d
                    if _d is None: _d = L.denoiser("float")
                    L.run(_d, e)
                else:
                    ctx, skip = cfg(c)
                    _, pl = compute(e, ctx, skip, pools[k] if k else None)
            res[label] = time.time() - t
            print(f"{label:10s} {res[label]:7.1f} s", flush=True)
        print("audio seconds", audio)
        base = res["whole"]
        for l, v in res.items(): print(f"{l:10s} {v:7.1f} s  ratio to whole-call {v/base:.3f}")
    else:
        confs = sys.argv[2].split(",")
        cache = L.HOME / "cache2"; cache.mkdir(exist_ok=True)
        pool = mp.Pool(int(os.environ.get("PROCS", "4")))
        rows = {c: [] for c in confs}
        for n in names:
            e = L.canceller(n); snr = L.snr_of(n)
            ref = L.frames10(pipeline.speech_gate(e, np.load(L.CACHE / f"{n}__float.npy").astype(np.float64)))
            for c in confs:
                f = cache / f"{n}__{c}.npy"
                if f.exists(): cc = np.load(f).astype(np.float64); skipped = None
                else:
                    ctx, skip = cfg(c); cc, pl = compute(e, ctx, skip, pool)
                    np.save(f, cc.astype(np.float32))
                g = L.frames10(pipeline.speech_gate(e, cc))
                k = min(len(g), len(ref)); d = int((g[:k] != ref[:k]).sum())
                ran = sum(t - f_ for f_, t, s, en in plan(e, *cfg(c))) / len(e)
                rows[c].append((n, snr, k, d, ran))
                print(f"{c:6s} {n:34s} snr {str(snr):5s} differ {d:5d} = {100*d/k:5.2f}%  model ran on {100*ran:5.1f}% of audio", flush=True)
        for c in confs:
            tot = sum(r[2] for r in rows[c]); dif = sum(r[3] for r in rows[c])
            nz = [r for r in rows[c] if r[1] in (0, -5)]
            print(f"SUMMARY {c}: all 40 {100*dif/tot:.3f}%  noisy 14 {100*sum(r[3] for r in nz)/sum(r[2] for r in nz):.3f}%  model on {100*np.mean([r[4] for r in rows[c]]):.1f}% of audio (mean)")


if __name__ == "__main__":
    main()
