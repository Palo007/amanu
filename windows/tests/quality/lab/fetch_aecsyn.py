"""Downloads 40 clips of the AEC Challenge synthetic set (test split, clean near end):
20 with the far end's nonlinear distortion and 20 without."""
import csv, random, sys, urllib.request
from pathlib import Path
out = Path(sys.argv[1]); rows = list(csv.DictReader(open(out / "meta.csv")))
pick = lambda nl: [r for r in rows if r["split"] == "test" and r["is_nearend_noisy"] == "0" and r["is_farend_nonlinear"] == nl]
rng = random.Random(7)
chosen = rng.sample(pick("1"), 20) + rng.sample(pick("0"), 20)
base = "https://media.githubusercontent.com/media/microsoft/AEC-Challenge/main/datasets/synthetic"
for r in chosen:
    i = r["fileid"]
    for folder, name in (("farend_speech", f"farend_speech_fileid_{i}.wav"), ("echo_signal", f"echo_fileid_{i}.wav"),
                         ("nearend_speech", f"nearend_speech_fileid_{i}.wav"), ("nearend_mic_signal", f"nearend_mic_fileid_{i}.wav")):
        target = out / folder / name
        if target.exists() and target.stat().st_size > 1000:
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        req = urllib.request.Request(f"{base}/{folder}/{name}", headers={"User-Agent": "amanu-quality-suite"})
        target.write_bytes(urllib.request.urlopen(req, timeout=120).read())
(out / "chosen.csv").write_text("\n".join(",".join([r["fileid"], r["ser"], r["is_farend_nonlinear"], r["is_farend_noisy"], r["nearend_scale"]]) for r in chosen))
print("done", len(chosen))
