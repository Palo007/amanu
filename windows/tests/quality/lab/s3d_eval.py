"""Track S3: the distilled gate against the real one on the suite's 40 mixed echo calls (CPU, no engine).

    py -P s3d_eval.py check <onnx>               gate arithmetic check on one call (distilled with the TRUE kept == real gate)
    py -P s3d_eval.py eval <onnx> [out json]     frames that differ, regions of n1 setting c, per call
    py -P s3d_eval.py time <onnx>                the 5 timing calls, back to back, best of 2

Reads the solver output and DPDFNet's output from the N2 cache (read only); no audio is written.
"""
import os, sys, json, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import numpy as np
import lab, pipeline, denoise, n1, n2, s1q_lib as L
import s3d_data as D

RATE = 16000
HOP = 256
N2C = Path.home() / "Amanu Quality Set" / "lab" / "overnight" / "N2" / "cache"


def solver_outputs(name):
    r = lab.load_recording(name)
    f = N2C / f"{n2._key(r['mic'], r['far'])}.npz"
    if f.exists():
        z = np.load(f)
        return z["e"].astype(np.float64), z["c"].astype(np.float64), r["truth"].get("snr"), r
    e = pipeline.VARIANTS["solve"](r["mic"], r["far"])          # in memory only
    return e, denoise.dpdf(e), r["truth"].get("snr"), r


def gain_from_active(active, nsamp, floor=0.15, n=512, hop=256):
    frames = len(active)
    g = np.interp(np.arange(nsamp), hop * np.arange(frames) + n / 2, np.where(active, 1.0, floor))
    ramp = int(0.03 * RATE)
    return np.convolve(np.pad(g, ramp // 2, mode="edge"), np.ones(ramp) / ramp, "valid")[:nsamp]


class Distilled:
    def __init__(self, onnx):
        import onnxruntime as ort
        so = ort.SessionOptions(); so.intra_op_num_threads = 1; so.inter_op_num_threads = 1
        self.s = ort.InferenceSession(str(onnx), so, providers=["CPUExecutionProvider"])

    def kept(self, f, e, piece=1875, ctx=125):
        out = np.zeros(len(e), np.float32)
        for a in range(0, len(e), piece):
            lo, hi = max(0, a - ctx), min(len(e), a + piece + ctx)
            y = self.s.run(None, {"mel": f[None, lo:hi], "level": e[None, lo:hi]})[0][0]
            out[a:a + piece] = y[a - lo: a - lo + min(piece, len(e) - a)]
        return out

    def gate(self, x, timing=None):
        t0 = time.perf_counter()
        f, erel = D.features(x)
        f = f.astype(np.float32); erel = erel.astype(np.float32)
        t1 = time.perf_counter()
        k = np.clip(self.kept(f, erel), -70.0, 5.0)
        t2 = time.perf_counter()
        a = D.frame_db(x) if False else None
        active = __import__("s3d_train").gate_active(erel + k)
        g = gain_from_active(active, len(x))
        t3 = time.perf_counter()
        if timing is not None:
            timing[:] = [t1 - t0, t2 - t1, t3 - t2]
        return g


def frames10(g): return L.frames10(g)


def cover(regs, n):
    m = np.zeros(n, bool)
    for a, b in regs: m[a:b] = True
    return m


def evaluate(onnx, outj=None):
    d = Distilled(onnx)
    names = L.suite()
    rows, tot = [], {"all": [0, 0], "loud": [0, 0]}
    agg = {k: dict(nr=0, nd=0, tr=0.0, td=0.0, cov=0.0, extra=0.0) for k in ("all", "loud")}
    for name in names:
        e, c, snr, _ = solver_outputs(name)
        gr = pipeline.speech_gate(e, c)
        gd = d.gate(e)
        fr, fd = frames10(gr), frames10(gd)
        diff = int((fr != fd).sum())
        rr = n1.regions(gr, gr * e, **n1.SETTINGS["c"])
        rd = n1.regions(gd, gd * e, **n1.SETTINGS["c"])
        mr, md = cover(rr, len(e)), cover(rd, len(e))
        row = dict(call=name, snr=snr, frames=len(fr), diff=diff, open_real=float(fr.mean()), open_dist=float(fd.mean()),
                   n_real=len(rr), n_dist=len(rd), s_real=float(mr.sum() / RATE), s_dist=float(md.sum() / RATE),
                   covered=float((mr & md).sum() / RATE), extra=float((md & ~mr).sum() / RATE))
        rows.append(row)
        print(f"{name:26s} snr {str(snr):>5s} diff {100 * diff / len(fr):5.2f} %  open {100 * row['open_real']:4.1f}/{100 * row['open_dist']:4.1f}  "
              f"regions {row['n_real']:2d}/{row['n_dist']:2d}  s {row['s_real']:6.1f}/{row['s_dist']:6.1f} covered {row['covered']:6.1f} extra {row['extra']:5.1f}", flush=True)
    for key, sel in (("all", rows), ("loud", [r for r in rows if r["snr"] in (0, -5)])):
        F = sum(r["frames"] for r in sel); Dd = sum(r["diff"] for r in sel)
        sr = sum(r["s_real"] for r in sel)
        print(f"{key}: {len(sel)} calls, {100 * Dd / F:.2f} % frames differ; regions real {sum(r['n_real'] for r in sel)} / dist {sum(r['n_dist'] for r in sel)}; "
              f"seconds real {sr:.0f} / dist {sum(r['s_dist'] for r in sel):.0f}; real region time covered {100 * sum(r['covered'] for r in sel) / sr:.2f} %; extra {sum(r['extra'] for r in sel):.0f} s "
              f"({100 * sum(r['extra'] for r in sel) / sr:.1f} % of real); worst call {max(100 * r['diff'] / r['frames'] for r in sel):.2f} %")
    if outj: Path(outj).write_text(json.dumps(rows), encoding="utf-8")


def check():
    name = [n for n in L.suite() if "cafe-ser-5-snr-5" in n][0]
    e, c, snr, _ = solver_outputs(name)
    gr = pipeline.speech_gate(e, c)
    kt = D.frame_db(c) - D.frame_db(e)
    erel = D.frame_db(e) - np.percentile(D.frame_db(e), 95)
    import s3d_train as T
    g2 = gain_from_active(T.gate_active(erel + kt), len(e))
    print("max gain difference with the true kept:", float(np.abs(g2 - gr).max()), " frames differ:", int((frames10(g2) != frames10(gr)).sum()))


TIMING = ["echo-cafe-ser+5-snr+0", "echo-cafe-ser-15-snr+20", "echo-cafe-ser-5-snr-5", "echo-office-ser+5-snr-5", "echo-office-ser-5-snr+0"]


def timing(onnx):
    import s2q
    d = Distilled(onnx)
    es = []
    for n in TIMING:
        if n not in L.suite():
            print("missing", n); continue
        es.append(solver_outputs(n)[0])
    secs = sum(len(e) for e in es) / RATE
    best_r, best_d, parts = 1e9, 1e9, None
    for rep in range(2):
        t = time.perf_counter()
        for e in es:
            c, _ = s2q.compute(e, 2.0, None)
            pipeline.speech_gate(e, c)
        tr = time.perf_counter() - t
        t = time.perf_counter(); acc = np.zeros(3)
        for e in es:
            tm = [0, 0, 0]; d.gate(e, tm); acc += tm
        td = time.perf_counter() - t
        print(f"rep {rep}: real gate {tr:.1f} s, distilled {td:.2f} s (features {acc[0]:.2f}, model {acc[1]:.2f}, gate {acc[2]:.2f}) for {secs:.0f} s of audio", flush=True)
        if tr < best_r: best_r = tr
        if td < best_d: best_d, parts = td, acc.copy()
    print(f"best: real {best_r:.1f} s, distilled {best_d:.2f} s, ratio {best_d / best_r:.3f}; model alone ratio {parts[1] / best_r:.3f}")


if __name__ == "__main__":
    c = sys.argv[1]
    if c == "check": check()
    elif c == "eval": evaluate(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
    elif c == "time": timing(sys.argv[2])
