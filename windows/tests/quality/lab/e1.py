"""E: combine the cached transcripts of several cleaned versions of the same call (overnight, track E).

No engine run. Reads the rows already cached by n1.py (setting a, c, e), n2c.py (n2-w50 cut) and
the lab's su/solve-gate rows. Each row has the joined text of the whole call only: no per-region
text and no word times. So the per-region rules (plausible rate, agreement per region, prefer the
non-denoised) cannot be computed from the cache; what can be done is word-level voting (ROVER-
style) over the whole call, and ceilings. The truth is used only to score and for the ceilings.

    py -P e1.py [out dir]

Voting rule (fixed, no per-call setting): the backbone is the first system; every other system is
aligned to it by edit distance; a backbone slot takes the word most systems give (a missing word
counts as a vote for deletion); a tie goes to the backbone. A word that other systems insert
between two backbone words is kept when more than half of ALL systems insert it there.
The agreement count is a PROXY for correctness, not a confidence score: systems that share
an input share its errors.
"""
import json, os, sys
from collections import Counter
from pathlib import Path
import numpy as np
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lab, asr
from rapidfuzz.distance import Levenshtein

Q = lab.Q
HOME = Path.home() / "Amanu Quality Set" / "lab"
N1, N2 = HOME / "overnight" / "N1", HOME / "overnight" / "N2"
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else HOME / "overnight" / "E"
OUT.mkdir(parents=True, exist_ok=True)

SOURCES = {"gate": (HOME, "su/solve-gate"), "c": (N1, "n1/c"), "a": (N1, "n1/a"), "e": (N1, "n1/e"),
           "n2c": (N2, "n2c/n2-w50")}


def load():
    return {k: asr.rows(w, t) for k, (w, t) in SOURCES.items()}


def truth_words(name):
    t = json.loads((Q.RECORDINGS / name / "truth.json").read_text(encoding="utf-8"))
    return t["me"], t


def score(name, words):
    me, t = truth_words(name)
    c = Q.error_counts(me, " ".join(words))
    far_only = set(Q.normalize_text(t["them"])) - set(Q.normalize_text(me))
    return {"errors": c["word_errors"], "words": c["ref_words"], "leak": sum(1 for w in c["inserted"] if w in far_only),
            "snr": t.get("snr")}


def vote(systems):
    """systems: list of word lists, the first is the backbone. Returns the voted word list."""
    n = len(systems)
    bb = systems[0]
    slot = [[w] for w in bb]                       # votes per backbone slot, backbone first
    gaps = [[] for _ in range(len(bb) + 1)]        # per gap: one inserted-word list per other system
    for s in systems[1:]:
        votes = [None] * len(bb)
        ins = [[] for _ in range(len(bb) + 1)]
        for tag, i1, i2, j1, j2 in Levenshtein.opcodes(bb, s).as_list():
            if tag == "equal":
                for k in range(i2 - i1):
                    votes[i1 + k] = s[j1 + k]
            elif tag == "replace":
                m = min(i2 - i1, j2 - j1)
                for k in range(m):
                    votes[i1 + k] = s[j1 + k]
                ins[i2].extend(s[j1 + m:j2])       # surplus words are insertions after the block
            elif tag == "insert":
                ins[i1].extend(s[j1:j2])
            # "delete": votes stay None
        for k in range(len(bb)):
            slot[k].append(votes[k])
        for g in range(len(bb) + 1):
            gaps[g].append(ins[g])
    out = []
    for g in range(len(bb) + 1):
        cnt = Counter(w for lst in gaps[g] for w in set(lst))
        out += [w for w in dict.fromkeys(w for lst in gaps[g] for w in lst) if cnt[w] > n / 2]
        if g < len(bb):
            c = Counter(slot[g])
            best = max(c.values())
            w = slot[g][0] if c[slot[g][0]] == best else next(x for x in slot[g] if c[x] == best)
            if w is not None:
                out.append(w)
    return out


def matched(ref, hyp):
    """Boolean per reference word: aligned as an exact match; and the number of hyp insertions."""
    ok = np.zeros(len(ref), bool)
    ins = 0
    for tag, i1, i2, j1, j2 in Levenshtein.opcodes(ref, hyp).as_list():
        if tag == "equal":
            ok[i1:i2] = True
        elif tag == "insert":
            ins += j2 - j1
        elif tag == "replace":
            ins += max(0, (j2 - j1) - (i2 - i1))
    return ok, ins


def table(label, rows):
    f = lambda sel: asr.wer(r for r in rows.values() if sel(r))
    return (f"{label:30s} n={len(rows):2d} 14 noisy {f(lambda r: r['snr'] in (0, -5)):5.1f}  SNR 0 {f(lambda r: r['snr'] == 0):5.1f}"
            f"  SNR -5 {f(lambda r: r['snr'] == -5):5.1f}  no noise {f(lambda r: r['snr'] is None):5.1f}"
            f"  echo words {sum(r['leak'] for r in rows.values())}")


def main():
    R = load()
    noisy = sorted(k for k in R["n2c"])
    allc = sorted(R["c"])
    print("calls:", {k: len(v) for k, v in R.items()})
    same = sum(1 for k in noisy if R["c"][k].get("regions") == R["n2c"][k].get("regions"))
    print(f"calls where n1-c and n2c have the same number of regions: {same} of {len(noisy)}")

    W = lambda src, k: Q.normalize_text(R[src][k]["hyp"])
    results = {}

    def run(label, names, build):
        results[label] = {k: score(k, build(k)) for k in names}

    for s in ("c", "n2c", "gate", "e"):
        run(f"single {s}", noisy, lambda k, s=s: W(s, k))
    combos = {"vote c+n2c+gate": ("c", "n2c", "gate"), "vote c+n2c+e": ("c", "n2c", "e"),
              "vote n2c+c+gate": ("n2c", "c", "gate"), "vote c+n2c+e+gate": ("c", "n2c", "e", "gate"),
              "vote c+e+a": ("c", "e", "a")}
    for label, srcs in combos.items():
        run(label, noisy, lambda k, srcs=srcs: vote([W(s, k) for s in srcs]))
    run("single c (20 calls)", allc, lambda k: W("c", k))
    run("single gate (20 calls)", allc, lambda k: W("gate", k))
    run("vote c+e+gate (20 calls)", allc, lambda k: vote([W(s, k) for s in ("c", "e", "gate")]))

    base14 = results["single c"]
    print()
    for label, rows in results.items():
        ref = results["single c (20 calls)"] if "20 calls" in label else base14
        d, lo, hi = asr.bootstrap(rows, ref)
        print(table(label, rows) + f"  | minus n1-c {d:+.1f} [{lo:+.1f}, {hi:+.1f}]")

    print("\nCEILINGS (use the truth; no rule can be built from them)")
    cl_call, cl_word = {}, {}
    for k in noisy:
        me, _ = truth_words(k)
        ref = Q.normalize_text(me)
        a, b = results["single c"][k], results["single n2c"][k]
        cl_call[k] = min(a, b, key=lambda r: r["errors"])
        oka, ia = matched(ref, W("c", k))
        okb, ib = matched(ref, W("n2c", k))
        cl_word[k] = {"errors": int((~(oka | okb)).sum()) + min(ia, ib), "words": len(ref), "leak": 0, "snr": a["snr"]}
    print(table("oracle per call (c or n2c)", cl_call))
    print(table("oracle per word (c or n2c)", cl_word))
    print(f"per-word ceiling minus n2c-w50: {asr.wer(cl_word.values()) - asr.wer(results['single n2c'].values()):+.1f}"
          f"; per-call ceiling minus n2c-w50: {asr.wer(cl_call.values()) - asr.wer(results['single n2c'].values()):+.1f}")
    (OUT / "e1-results.json").write_text(json.dumps({**results, "oracle call": cl_call, "oracle word": cl_word}), encoding="utf-8")


if __name__ == "__main__":
    main()
