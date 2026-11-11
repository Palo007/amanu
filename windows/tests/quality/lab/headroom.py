"""The headroom table: my WER per condition for the oracles of oracle.py beside the suite's
removers (from a suite run's results.json), and what share of the error each part holds.

    py -P headroom.py <work dir> [suite run name]
"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lab, asr
Q = lab.Q
WORK = sys.argv[1]
run = Q.RUNS / (sys.argv[2] if len(sys.argv) > 2 else "sonora-tuning")
suite = {}
for r in json.loads((run / "results.json").read_text(encoding="utf-8"))["rows"] if (run / "results.json").exists() else []:
    if r.get("kind") == "echo" and "me" in r:
        suite.setdefault(r["mode"], {})[r["recording"]] = {"errors": r["me"]["word_errors"], "words": r["me"]["ref_words"]}
names = sorted(p.name for p in Q.RECORDINGS.iterdir() if p.name.startswith("echo-"))
by_cond = lambda tag: {n.replace("ser+5", "ser-5").replace("ser-15", "ser-5"): r for n, r in asr.rows(WORK, tag).items()}
near, nn, nnirm = by_cond("near"), by_cond("nn"), by_cond("nn-irm")
cond = lambda n: n.replace("ser+5", "ser-5").replace("ser-15", "ser-5")
noecho = lambda n: nn.get(cond(n)) or near[cond(n)]                    # perfect echo removal, noise left in
ideal = lambda n: nnirm.get(cond(n)) or near[cond(n)]                   # and the noise removed by an ideal mask
floor = lambda n: near[cond(n).rsplit("-snr", 1)[0] + "-snrclean"] if "office" in n or "cafe" in n else near["echo-" + ("office" if "street" in n else "cafe") + "-ser-5-snrclean"]
cols = {"engine floor": floor, "no echo, ideal noise mask": ideal, "no echo, noise left": noecho,
        "ideal echo mask": lambda n: asr.rows(WORK, "irm-echo")[n], "ideal echo+noise mask": lambda n: asr.rows(WORK, "irm-all")[n]}
for mode in ("sonora-delay-floor", "sonora-delay-nearend-floor", "thorough", "fast"):
    if mode in suite:
        cols[mode] = lambda n, mode=mode: suite[mode][n]
w = lambda rs: asr.wer(rs)
print("| Input | All 40 | No noise | SNR 20..5 | SNR 0 | SNR -5 |\n|---|---|---|---|---|---|")
groups = [("all", lambda n: True), ("clean", lambda n: "snrclean" in n), ("mid", lambda n: any(s in n for s in ("snr+20", "snr+10", "snr+5"))),
          ("0", lambda n: "snr+0" in n), ("-5", lambda n: "snr-5" in n)]
for label, f in cols.items():
    print(f"| {label} | " + " | ".join(f"{w(f(n) for n in names if g(n)):.1f}" for _, g in groups) + " |")
print()
print("| Noise | SNR | engine floor | no echo, ideal noise mask | no echo, noise left | " + " | ".join(m for m in cols if m in suite) + " |\n|---|---|---|---|---|" + "---|" * sum(1 for m in cols if m in suite))
seen = []
for n in names:
    key = (n.split("-")[1], n.rsplit("-snr", 1)[1])
    if key in seen:
        continue
    seen.append(key)
    same = [m for m in names if (m.split("-")[1], m.rsplit("-snr", 1)[1]) == key]
    print(f"| {key[0]} | {key[1]} | " + " | ".join(f"{w(cols[c](m) for m in same):.1f}" for c in ["engine floor", "no echo, ideal noise mask", "no echo, noise left"] + [m for m in cols if m in suite]) + " |")
