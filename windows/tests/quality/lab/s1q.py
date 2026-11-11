"""Track S1 (overnight 2026-11-12): is DPDFNet in int8 as good a speech gate as the float model?

    py -P s1q.py quant                  write the int8 models under overnight/S1/models
    py -P s1q.py cache                  the canceller's output of the 40 suite calls, cached
    py -P s1q.py gate <model,...>       DPDFNet's output and the gate per model, share of differing 10 ms frames
    py -P s1q.py time <model,...>       back-to-back timing on the same 5 calls
Models are names under overnight/S1/models, or 'float' for the shipped one.
"""
import os, sys, time, json, re
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pathlib import Path
import numpy as np
HOME = Path.home() / "Amanu Quality Set" / "lab" / "overnight" / "S1"
FLOAT = Path.home() / "Amanu Quality Set" / "lab" / "models" / "dpdfnet_baseline.onnx"
MODELS = HOME / "models"
CACHE = HOME / "cache"


def path_of(name): return FLOAT if name == "float" else MODELS / f"{name}.onnx"


def quant():
    import onnx
    from onnxruntime.quantization import quantize_dynamic, QuantType
    MODELS.mkdir(parents=True, exist_ok=True)
    src = onnx.load(str(FLOAT))
    meta = [(p.key, p.value) for p in src.metadata_props]
    for name, ops in (("int8", None), ("int8-gemm", ["MatMul", "Gemm"])):
        out = MODELS / f"{name}.onnx"
        kw = {"op_types_to_quantize": ops} if ops else {}
        quantize_dynamic(str(FLOAT), str(out), weight_type=QuantType.QInt8, **kw)
        m = onnx.load(str(out))
        have = {p.key for p in m.metadata_props}
        for k, v in meta:
            if k not in have:
                e = m.metadata_props.add(); e.key, e.value = k, v
        onnx.save(m, str(out))
        from collections import Counter
        print(name, out.stat().st_size, dict(Counter(n.op_type for n in m.graph.node)))


def suite():
    import lab
    return sorted(p.name for p in lab.Q.RECORDINGS.iterdir() if p.name.startswith("echo-"))


def canceller(name):
    f = CACHE / f"{name}.npy"
    if f.exists():
        return np.load(f).astype(np.float64)
    import lab, pipeline
    r = lab.load_recording(name)
    e = pipeline.VARIANTS["solve"](r["mic"], r["far"])
    CACHE.mkdir(parents=True, exist_ok=True)
    np.save(f, e.astype(np.float32))
    return e


def denoiser(model):
    import sherpa_onnx
    p = str(path_of(model))
    m = sherpa_onnx.OfflineSpeechDenoiserModelConfig(dpdfnet=sherpa_onnx.OfflineSpeechDenoiserDpdfNetModelConfig(model=p, attenuation_limit_db=0.0), num_threads=1)
    return sherpa_onnx.OfflineSpeechDenoiser(sherpa_onnx.OfflineSpeechDenoiserConfig(model=m))


def run(d, x):
    out = np.asarray(d.run(np.ascontiguousarray(x, dtype=np.float32), 16000).samples, dtype=np.float64)
    return np.pad(out, (0, max(0, len(x) - len(out))))[: len(x)]


def frames10(g):
    """Open (gain above 0.5) per 10 ms frame, from the per-sample gain."""
    k = len(g) // 160
    return g[: k * 160].reshape(k, 160).mean(axis=1) > 0.5


def snr_of(name):
    import lab
    return lab.load_recording(name)["truth"].get("snr")


def main():
    cmd = sys.argv[1]
    if cmd == "quant": return quant()
    names = suite()
    if cmd == "cache":
        for n in names:
            t = time.time(); canceller(n); print(n, f"{time.time()-t:.1f}s", flush=True)
        return
    import pipeline
    models = sys.argv[2].split(",")
    if cmd == "gate":
        ds = {m: denoiser(m) for m in models}
        res = {}
        CACHE.mkdir(parents=True, exist_ok=True)
        rows = []
        for n in names:
            e = canceller(n); snr = snr_of(n)
            g = {}
            for m in models:
                f = CACHE / f"{n}__{m}.npy"
                if f.exists(): c = np.load(f).astype(np.float64)
                else:
                    c = run(ds[m], e); np.save(f, c.astype(np.float32))
                g[m] = frames10(pipeline.speech_gate(e, c))
            rows.append((n, snr, g))
        ref = models[0]
        out = {}
        for m in models[1:]:
            print(f"--- {m} against {ref}")
            tot = dif = hi = hd = 0
            for n, snr, g in rows:
                a, b = g[ref], g[m]; k = min(len(a), len(b))
                d = int((a[:k] != b[:k]).sum())
                print(f"{n:34s} snr {str(snr):5s} frames {k:6d} open {a[:k].mean()*100:5.1f}% / {b[:k].mean()*100:5.1f}%  differ {d:5d} = {100*d/k:5.2f}%")
                tot += k; dif += d
                if snr in (0, -5): hi += k; hd += d
            print(f"ALL 40: {100*dif/tot:.3f}%   SNR 0 and -5: {100*hd/hi:.3f}%  (frames {tot}, {hi})")
    if cmd == "time":
        sel = names[:: max(1, len(names) // 5)][:5]
        ds = {m: denoiser(m) for m in models}
        xs = [canceller(n) for n in sel]
        print("calls", sel, "seconds of audio", sum(len(x) for x in xs) / 16000)
        tt = {m: [] for m in models}
        for rep in range(2):
            for m in models:
                t = time.time()
                for x in xs: run(ds[m], x)
                tt[m].append(time.time() - t); print(f"rep {rep} {m:10s} {tt[m][-1]:.2f}s", flush=True)
        for m in models: print(f"{m:10s} best {min(tt[m]):.2f}s  ratio to {models[0]} {min(tt[m])/min(tt[models[0]]):.3f}")


main()
