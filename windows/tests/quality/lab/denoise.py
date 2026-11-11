"""Speech denoisers under test, each a function (audio at 16 kHz) -> audio of the same length.
The models are the ones sherpa-onnx publishes (release speech-enhancement-models):
GTCRN (MIT, 48K parameters) and DPDFNet (Apache-2.0, 2.3M and more)."""
import threading
from pathlib import Path
import numpy as np
import sherpa_onnx

MODELS = Path.home() / "Amanu Quality Set" / "lab" / "models"
_cache, _lock = {}, threading.Lock()


def _denoiser(kind, name, limit_db=0.0, threads=2):
    key = (kind, name, limit_db)
    with _lock:
        if key not in _cache:
            path = str(MODELS / name)
            if kind == "gtcrn":
                model = sherpa_onnx.OfflineSpeechDenoiserModelConfig(gtcrn=sherpa_onnx.OfflineSpeechDenoiserGtcrnModelConfig(model=path), num_threads=threads)
            else:
                model = sherpa_onnx.OfflineSpeechDenoiserModelConfig(dpdfnet=sherpa_onnx.OfflineSpeechDenoiserDpdfNetModelConfig(model=path, attenuation_limit_db=limit_db), num_threads=threads)
            _cache[key] = sherpa_onnx.OfflineSpeechDenoiser(sherpa_onnx.OfflineSpeechDenoiserConfig(model=model))
        return _cache[key]


def run(x, kind="gtcrn", name="gtcrn_simple.onnx", limit_db=0.0):
    d = _denoiser(kind, name, limit_db)
    out = np.asarray(d.run(np.ascontiguousarray(x, dtype=np.float32), 16000).samples, dtype=np.float64)
    return np.pad(out, (0, max(0, len(x) - len(out))))[: len(x)]


def gtcrn(x): return run(x)
def dpdf(x, name="dpdfnet_baseline.onnx", limit_db=0.0): return run(x, "dpdfnet", name, limit_db)


def lag_of(out, ref, span=1024):
    """Samples by which `out` lags `ref`."""
    from scipy import signal
    n = min(len(out), len(ref), 16000 * 30)
    c = signal.correlate(out[:n], ref[:n], mode="full", method="fft")
    return int(np.argmax(c[n - 1 - span: n + span])) - span


def mix(f, keep):
    """The denoiser's output with `keep` of the input mixed back in: a floor under its cut."""
    return lambda x: (1 - keep) * f(x) + keep * x


NAMES = {
    "gtcrn": gtcrn,
    "dpdfb": dpdf,
    "dpdf2": lambda x: dpdf(x, "dpdfnet2.onnx"),
    "dpdf4": lambda x: dpdf(x, "dpdfnet4.onnx"),
    "dpdfb-l12": lambda x: dpdf(x, limit_db=12.0),
    "dpdfb-l6": lambda x: dpdf(x, limit_db=6.0),
    "dpdfb-m30": mix(dpdf, 0.3),
    "dpdfb-m15": mix(dpdf, 0.15),
    "gtcrn-m30": mix(gtcrn, 0.3),
}
