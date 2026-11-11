"""Functions of s1q.py without its command line, shared with s2q.py."""
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


