"""Parity of the app's echo solver with the lab's: one recording through the app's echo
model, its kept mic against the lab pipeline's output.
    py -P p1.py <Amanu.exe> <recording folder> <app model> <lab variant>"""
import os, sys, subprocess, shutil, tempfile, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import lab, pipeline
Q = lab.Q
exe, src, model, variant = sys.argv[1:5]
target = os.path.join(tempfile.mkdtemp(prefix="parity-"), "run")
subprocess.run([exe, "--measure-transcription", src, target, "auto"], env=dict(os.environ, AMANU_MEASURE_ECHO=model, AMANU_MEASURE_KEEP_ECHO="1"), check=True)
app = Q.read_mono(os.path.join(target, ".echo-kept", "mic.wav"))
mic, far = Q.read_mono(os.path.join(src, "mic.wav")), Q.read_mono(os.path.join(src, "system.wav"))
ref = pipeline.VARIANTS[variant](mic, far[:len(mic)] if len(far) >= len(mic) else np.pad(far, (0, len(mic) - len(far))))
n = min(len(app), len(ref))
d = app[:n] - ref[:n]
db = lambda a, b: 10 * np.log10((np.sum(a ** 2) + 1e-20) / (np.sum(b ** 2) + 1e-20))
print(f"lengths {len(app)} {len(ref)}; difference against lab output {db(d, ref[:n]):.1f} dB; app output against mic {db(app[:n], mic[:n]):.1f} dB; lab {db(ref[:n], mic[:n]):.1f} dB")
for line in open(glob.glob(os.path.join(target, "*.log"))[0], encoding="utf-8", errors="replace") if glob.glob(os.path.join(target, "*.log")) else []:
    if "echo" in line:
        print(line.strip()[:400])
print(target)
