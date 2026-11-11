import sys, json, numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, variants, localvqe
from pathlib import Path
Q = lab.Q
sc = lab.load_recording("aec-real")
print("whole-file prime", localvqe.prime(sc["mic"], sc["far"]))
for v in sys.argv[2].split(","):
    c = Path(sys.argv[1]) / "cache" / f"aec-real__{v}.json"
    if c.exists():
        r = json.loads(c.read_text())
    else:
        out = lab.align_whole(variants.VARIANTS[v](sc["mic"], sc["far"]), sc["mic"])
        per = [Q.db(Q.region_power(sc["mic"], g) / Q.region_power(out, g)) for g in sc["truth"]["regions"]]
        r = {"erle": float(np.mean(per)), "per": per}; c.write_text(json.dumps(r))
    print(f"{v:24s} aec-real ERLE {r['erle']:5.1f}  worst clips {sorted(r['per'])[:3]}")
