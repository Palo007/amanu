"""N2 setting through n1.py's setting-c cutting: blend first (n2), then cut into the gate's
speech regions (merge 1.5 s, pad 1.0 s, each region to -20 dBFS, one WAV each, transcribe-cli --batch).

    py -P n2c.py <work dir> run:<n2 setting>      14 noisy calls, rows <work>/wer2/n2c/<setting>
    py -P n2c.py <work dir> show:<n2 setting>     against the cached su/solve-gate rows, and against n1's setting c
"""
import os, shutil, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
import re
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import numpy as np
import lab, asr, n1, n2, pipeline

WORK = sys.argv[1]
kind, _, tag = sys.argv[2].partition(":")
names = [n for n in n2.names() if re.search(r"snr(\+0|-5)$", n)]


def one(name):
    row = asr.cached(WORK, f"n2c/{tag}", name)
    if row is not None:
        return row
    sc = lab.load_recording(name)
    e, c = n2.base(sc["mic"], sc["far"])
    g = pipeline.speech_gate(e, c)
    x = n2.SETTINGS[tag](sc["mic"], sc["far"])           # = g * ((1 - w) e + w c)
    regs = n1.regions(g, x, **n1.SETTINGS["c"])
    tmp = Path(WORK) / "tmp" / f"{tag}-{name}"
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir(parents=True)
    wavs = []
    for i, (a, b) in enumerate(regs):
        p = tmp / f"r{i:03d}.wav"
        lab.Q.write_wav(p, n1.scaled(x, g, a, b)); wavs.append(p)
    texts, secs = n1.run_batch(wavs, tmp) if wavs else ([], 0.0)
    row = n1.score_row(name, " ".join(t for t in texts if t), secs, {"regions": len(regs)})
    f = Path(WORK) / "wer2" / "n2c" / tag / f"{name}.json"
    f.parent.mkdir(parents=True, exist_ok=True)
    import json
    f.write_text(json.dumps(row, ensure_ascii=False), encoding="utf-8")
    shutil.rmtree(tmp)
    return row


if kind == "run":
    with ThreadPoolExecutor(3) as pool:
        list(pool.map(one, names))
base = {k: r for k, r in asr.rows(n1.LAB, "su/solve-gate").items() if k in names}
rs = asr.rows(WORK, f"n2c/{tag}")
g = [("SNR 0", lambda r: r["snr"] == 0), ("SNR -5", lambda r: r["snr"] == -5)]
d, lo, hi = asr.bootstrap(rs, base)
print(f"n2c/{tag} n={len(rs)} WER {asr.wer(rs.values()):.1f} | " + " ".join(f"{a} {asr.wer(r for r in rs.values() if f(r)):.1f}" for a, f in g) + f" | echo words {sum(r['leak'] for r in rs.values())} | against solve-gate {d:+.1f} [{lo:+.1f}, {hi:+.1f}]")
c = asr.rows(os.path.join(os.path.expanduser("~"), "Amanu Quality Set", "lab", "overnight", "N1"), "n1/c")
c = {k: r for k, r in c.items() if k in names}
if c:
    d, lo, hi = asr.bootstrap(rs, c)
    print(f"n1 setting c alone: WER {asr.wer(c.values()):.1f}; this against it {d:+.1f} [{lo:+.1f}, {hi:+.1f}]")
