"""Suite, lab WER of removers from pipeline.VARIANTS or variants.VARIANTS.

    py -P sw.py <work dir> run:<variant,...>
    py -P sw.py <work dir> show:<tag,...> [baseline tag]     tags are su/<variant>
"""
import os, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lab, asr
Q = lab.Q
WORK = sys.argv[1]
kind, _, what = sys.argv[2].partition(":")
names = sorted(p.name for p in Q.RECORDINGS.iterdir() if p.name.startswith("echo-"))
if kind == "run":
    import variants, pipeline
    table = {**variants.VARIANTS, **pipeline.VARIANTS}
    for v in what.split(","):
        def make(n, v=v):
            sc = lab.load_recording(n)
            t0 = time.time(); out = table[v](sc["mic"], sc["far"]); dt = time.time() - t0
            return lab.align_near(out, sc["mic"], sc["truth"]["regions"]), dt
        asr.run_jobs(WORK, [(f"su/{v}", n, lambda n=n: make(n)) for n in names], workers=3)
groups = [("no noise", lambda r: r["snr"] is None), ("20..5", lambda r: r["snr"] in (20, 10, 5)), ("0", lambda r: r["snr"] == 0), ("-5", lambda r: r["snr"] == -5)]
base = asr.rows(WORK, f"su/{sys.argv[3]}") if len(sys.argv) > 3 else None
for tag in what.split(","):
    rs = asr.rows(WORK, f"su/{tag}")
    line = f"{tag:26s} n={len(rs):2d} WER {asr.wer(rs.values()):5.1f} | " + " ".join(f"{g} {asr.wer(r for r in rs.values() if f(r)):5.1f}" for g, f in groups)
    line += f" | echo words {sum(r['leak'] for r in rs.values()):2d} | time {sum(r['secs'] for r in rs.values()):5.0f} s"
    if base is not None and tag != sys.argv[3]:
        d, lo, hi = asr.bootstrap(rs, base)
        line += f" | against {sys.argv[3]}: {d:+.1f} [{lo:+.1f}, {hi:+.1f}]"
    print(line, flush=True)
