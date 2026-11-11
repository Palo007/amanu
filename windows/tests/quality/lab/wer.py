"""WER for lab echo removers: the lab cleans each scene's mic, writes it as a recording,
and Amanu transcribes it with its own echo removal off, as the suite does.

    py -P wer.py <work dir> <exe> <variant,...> [scene regex]
"""
import json, os, re, shutil, subprocess, sys, time
from pathlib import Path
import numpy as np
sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import lab, variants
Q = lab.Q
root = Path(sys.argv[1]) / "wer"
exe = str(Path(sys.argv[2]).resolve())
pattern = sys.argv[4] if len(sys.argv) > 4 else "^echo-"
names = sorted(p.name for p in Q.RECORDINGS.iterdir() if re.search(pattern, p.name))
for v in sys.argv[3].split(","):
    rows = []
    for n in names:
        res = root / v / n / "score.json"
        if not res.exists():
            src = Q.RECORDINGS / n
            sc = lab.load_recording(n)
            t0 = time.time(); out = variants.VARIANTS[v](sc["mic"], sc["far"]); secs = time.time() - t0
            out = lab.align_near(out, sc["mic"], sc["truth"]["regions"])
            folder = root / v / n / "in"; target = root / v / n / "run"
            for d in (folder, target):
                if d.exists(): shutil.rmtree(d)
            folder.mkdir(parents=True)
            Q.write_wav(folder / "mic.wav", out)
            for f in ("system.wav", "meta.json", "truth.json"):
                shutil.copy(src / f, folder / f)
            env = dict(os.environ, AMANU_MEASURE_ECHO="off")
            subprocess.run([exe, "--measure-transcription", str(folder), str(target), "auto"], env=env, check=True)
            truth = json.loads((src / "truth.json").read_text(encoding="utf-8"))
            me = Q.error_counts(truth["me"], Q.transcript_text(target, "me"))
            far_only = set(Q.normalize_text(truth["them"])) - set(Q.normalize_text(truth["me"]))
            r = {"errors": me["word_errors"], "words": me["ref_words"], "leak": sum(1 for w in me["inserted"] if w in far_only),
                 "clean": truth["snr"] is None, "secs": secs}
            res.write_text(json.dumps(r))
            shutil.rmtree(folder); shutil.rmtree(target)
        rows.append(json.loads(res.read_text()))
    w = lambda rs: 100 * sum(r["errors"] for r in rs) / max(1, sum(r["words"] for r in rs))
    print(f"{v:32s} n={len(rows)} WER {w(rows):5.1f} no noise {w([r for r in rows if r['clean']]):5.1f} echo words {sum(r['leak'] for r in rows):3d}", flush=True)
