"""Suite WER of the N2 settings on the 14 calls at SNR 0 and -5 dB, against solve-gate's cached rows
(tag su/solve-gate, never measured again). Three transcriptions at once (asr.run_jobs workers=3).

    py -P n2sw.py <work dir> run:n2-w50,n2-w80,n2-w65-guard     transcribe (cached per call), then show
    py -P n2sw.py <work dir> show:n2-w50,n2-w80,n2-w65-guard    show only
"""
import os, re, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
import lab, asr, n2
WORK = sys.argv[1]
kind, _, what = sys.argv[2].partition(":")
BASE = "solve-gate"
names = [n for n in n2.names() if re.search(r"snr(\+0|-5)$", n)]
assert len(names) == 14, names
if kind == "run":
    for v in what.split(","):
        def make(n, v=v):
            sc = lab.load_recording(n)
            t0 = time.time(); out = n2.SETTINGS[v](sc["mic"], sc["far"]); dt = time.time() - t0
            return lab.align_near(out, sc["mic"], sc["truth"]["regions"]), dt
        asr.run_jobs(WORK, [(f"su/{v}", n, lambda n=n: make(n)) for n in names], workers=3)
base = {k: r for k, r in asr.rows(WORK, f"su/{BASE}").items() if k in names}
groups = [("SNR 0", lambda r: r["snr"] == 0), ("SNR -5", lambda r: r["snr"] == -5)]
line = lambda tag, rs: f"{tag:16s} n={len(rs):2d} WER {asr.wer(rs.values()):5.1f} | " + " ".join(f"{g} {asr.wer(r for r in rs.values() if f(r)):5.1f}" for g, f in groups) + f" | echo words {sum(r['leak'] for r in rs.values()):2d}"
print(line(BASE, base))
for tag in what.split(","):
    rs = {k: r for k, r in asr.rows(WORK, f"su/{tag}").items() if k in names}
    d, lo, hi = asr.bootstrap(rs, base)
    print(line(tag, rs) + f" | against {BASE}: {d:+.1f} [{lo:+.1f}, {hi:+.1f}]")
