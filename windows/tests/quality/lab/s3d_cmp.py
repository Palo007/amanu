import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import asr
O = Path.home() / "Amanu Quality Set" / "lab" / "overnight"
a = asr.rows(O / "S3" / "w", "n1/c"); b = asr.rows(O / "N1", "n1/c")
b = {k: v for k, v in b.items() if k in a}
print(len(a), len(b), "distilled", asr.wer(a.values()), "real c", asr.wer(b.values()), "engine real c", sum(r["secs"] for r in b.values()))
print("paired distilled vs c:", asr.bootstrap(a, b))
for g, f in (("loud", lambda r: r["snr"] in (0, -5)), ("0", lambda r: r["snr"] == 0), ("-5", lambda r: r["snr"] == -5), ("clean", lambda r: r["snr"] is None)):
    print(g, round(asr.wer(r for r in a.values() if f(r)), 1), round(asr.wer(r for r in b.values() if f(r)), 1))
