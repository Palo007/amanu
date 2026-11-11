"""Held-out word errors by kind of call, and the paired difference of two removers in each.
    py -P hg.py <work dir> <tag,...> <tag a> <tag b>      (tags are under ho/)"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asr
W, tags, ta, tb = sys.argv[1], sys.argv[2].split(","), sys.argv[3], sys.argv[4]
man = {s["name"]: s for s in json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "heldout-manifest.json")))}


def group(s):
    if s["name"].startswith("ho-long"): return "long"
    if s.get("ser") is None or s.get("room") is None: return "no echo"
    if s.get("room2"): return "change"
    return "drift" if s.get("drift_ppm") else "steady"


rows = {t: {n: asr.cached(W, "ho/" + t, n) for n in man} for t in set(tags) | {ta, tb}}
sets = {g: [n for n, s in man.items() if group(s) == g] for g in ("steady", "drift", "change", "no echo", "long")}
sets["no noise"] = [n for n, s in man.items() if s.get("snr") is None]
sets["SNR 20..5"] = [n for n, s in man.items() if s.get("snr") is not None and s["snr"] >= 5]
sets["SNR 0, -5"] = [n for n, s in man.items() if s.get("snr") is not None and s["snr"] < 5]
sets["all"] = list(man)
for g, sel in sets.items():
    line = [f"{g:10} n={len(sel):2}"]
    for t in tags:
        rs = [rows[t][n] for n in sel]
        line.append(f"{t} {100 * sum(r['errors'] for r in rs) / sum(r['words'] for r in rs):5.1f}")
    d, lo, hi = asr.bootstrap({n: rows[ta][n] for n in sel}, {n: rows[tb][n] for n in sel})
    line.append(f"{ta} - {tb} {d:+.1f} [{lo:+.1f}, {hi:+.1f}]")
    print(" | ".join(line))
