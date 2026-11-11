"""ctypes harness for liblocalvqe.dll the way LocalVqeBackend drives it: noise gate off,
whole-signal delay prime first, then 256-sample hops (the output is one hop late)."""
from __future__ import annotations

import ctypes as C
import os
from pathlib import Path

import numpy as np

HERE = Path(os.environ.get("LOCALVQE_DIR", Path(__file__).resolve().parents[3] / "native" / "localvqe"))
MODELS = {"fast": "localvqe-v1.4-aec-2.7K-f32.gguf", "thorough": "localvqe-v1.4-aec-200K-f32.gguf"}
_lib = None


def lib():
    global _lib
    if _lib is None:
        d = C.CDLL(str(HERE / "liblocalvqe.dll"))
        d.localvqe_new.restype = C.c_void_p
        d.localvqe_new.argtypes = [C.c_char_p]
        d.localvqe_free.argtypes = [C.c_void_p]
        PF = C.POINTER(C.c_float)
        d.localvqe_process_frame_f32.argtypes = [C.c_void_p, PF, PF, C.c_int, PF]
        d.localvqe_set_noise_gate.argtypes = [C.c_void_p, C.c_int, C.c_float]
        for n in ("localvqe_prime_delay_begin", "localvqe_prime_delay_end"):
            getattr(d, n).argtypes = [C.c_void_p]
        d.localvqe_prime_delay_feed.argtypes = [C.c_void_p, PF, PF, C.c_int]
        _lib = d
    return _lib


def run(mic: np.ndarray, far: np.ndarray, model: str = "fast", prime: bool = True):
    d = lib()
    ctx = d.localvqe_new(str(HERE / MODELS[model]).encode())
    if not ctx:
        raise RuntimeError("localvqe_new failed")
    d.localvqe_set_noise_gate(ctx, 0, -45.0)
    PF = C.POINTER(C.c_float)
    m = np.ascontiguousarray(mic, dtype=np.float32)
    f = np.ascontiguousarray(far, dtype=np.float32)
    shift = None
    if prime:
        d.localvqe_prime_delay_begin(ctx)
        n = len(m) - len(m) % 128
        d.localvqe_prime_delay_feed(ctx, m.ctypes.data_as(PF), f.ctypes.data_as(PF), n)
        shift = d.localvqe_prime_delay_end(ctx)
    hop = 256
    n = len(m) // hop
    out = np.zeros(len(m), dtype=np.float32)
    buf = np.zeros(hop, dtype=np.float32)
    for k in range(n + 1):
        if k < n:
            mi, fi = m[k * hop:(k + 1) * hop], f[k * hop:(k + 1) * hop]
        else:
            mi = fi = np.zeros(hop, dtype=np.float32)
        r = d.localvqe_process_frame_f32(ctx, mi.ctypes.data_as(PF), fi.ctypes.data_as(PF), hop, buf.ctypes.data_as(PF))
        if r != 0:
            raise RuntimeError(f"process failed {r}")
        if k > 0:
            out[(k - 1) * hop:k * hop] = buf
    d.localvqe_free(ctx)
    return out.astype(np.float64), shift


def prime(mic: np.ndarray, far: np.ndarray, model: str = "fast") -> int:
    """LocalVQE's whole-signal delay prime alone: the shift in samples, or a negative code."""
    d = lib()
    ctx = d.localvqe_new(str(HERE / MODELS[model]).encode())
    PF = C.POINTER(C.c_float)
    m = np.ascontiguousarray(mic, dtype=np.float32)
    f = np.ascontiguousarray(far, dtype=np.float32)
    d.localvqe_prime_delay_begin(ctx)
    n = len(m) - len(m) % 128
    d.localvqe_prime_delay_feed(ctx, m.ctypes.data_as(PF), f.ctypes.data_as(PF), n)
    shift = d.localvqe_prime_delay_end(ctx)
    d.localvqe_free(ctx)
    return shift
