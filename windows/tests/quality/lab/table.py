"""The write-up's table from a quality run's results.json."""
import json, sys, numpy as np
from pathlib import Path
d = json.loads((Path(sys.argv[1]) / "results.json").read_text(encoding="utf-8"))
modes = sys.argv[2].split(",")
rows = [r for r in d["rows"] if not r.get("failed")]
def wer(rs): return 100 * sum(r["me"]["word_errors"] for r in rs) / sum(r["me"]["ref_words"] for r in rs)
print("| Echo remover | My WER % | My WER %, no noise | Echo words | ERLE synthetic (dB) | ERLE real (dB) | Double-talk voice (dB) | Double-talk SI-SDR gain (dB) | Echo time (s) |")
print("|---|---|---|---|---|---|---|---|---|")
for m in modes:
    e = [r for r in rows if r["kind"] == "echo" and r["mode"] == m]; cl = [r for r in e if r["snr"] is None]
    real = [r for r in rows if r["kind"] == "aec-real" and r["mode"] == m]
    g = lambda k: np.mean([r[k] for r in e if k in r])
    print(f"| {m} | {wer(e):.1f} | {wer(cl):.1f} | {sum(r['me'].get('echo_leak_words', 0) for r in e)} | {g('erle_db'):.1f} | "
          f"{real[0]['erle_db']:.1f} | {g('double_near_gain_db'):.1f} | {g('double_sisdr_out_db') - g('double_sisdr_in_db'):.1f} | {sum(r['echo_s'] or 0 for r in e):.0f} |".replace("-", "−").replace("| −−", "|---").replace(m.replace("-", "−"), m))
