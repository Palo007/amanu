"""Track S3: n1's gate cache filled with the DISTILLED gate, so that n1.py runs setting c on it.

    py -P s3d_cache.py <work dir> <onnx> [call regex]     writes <work dir>/cache/<call>.npz (e, g), 21 MB a call
    py -P n1.py <work dir> run:c                          (NEEDS THE ENGINE) the 14 calls at SNR 0/-5 and the 6 without noise
"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import numpy as np
import n1, s3d_eval as V

work, onnx = Path(sys.argv[1]), sys.argv[2]
rx = sys.argv[3] if len(sys.argv) > 3 else None
d = V.Distilled(onnx)
for name in n1.screen_names(rx):
    f = work / "cache" / f"{name}.npz"
    if f.exists():
        continue
    e, c, snr, _ = V.solver_outputs(name)
    g = d.gate(e)
    f.parent.mkdir(parents=True, exist_ok=True)
    np.savez(f, e=e.astype(np.float32), g=g.astype(np.float32))
    print(name, f.stat().st_size // 10 ** 6, "MB", flush=True)
