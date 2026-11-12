import json, os, sys
import numpy as np
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
d = json.loads((Path.home()/"Amanu Quality Set/lab/overnight/N6/step1_all.json").read_text(encoding="utf-8"))
rows = []
for n, v in d.items():
    for r in v["regs"]: rows.append((n, r))
tot_my = sum(r["my_s"] for _, r in rows)
echo = [(n, r) for n, r in rows if r["my_s"] < 0.5]
print("regions", len(rows), "echo-only (my<0.5 s)", len(echo), "my speech s", round(tot_my, 1))
print("echo-only regions:")
for n, r in echo: print(f"  {n:28s} R={r['R']:6.1f} L={r['L']:6.1f} lvl={r['lvl']:6.1f} open={r['open_s']:5.1f} my={r['my_s']:.2f}")
print("R of regions with my speech: sorted lowest 15")
for n, r in sorted([x for x in rows if x[1]["my_s"] >= 0.5], key=lambda x: x[1]["R"])[:15]:
    print(f"  {n:28s} R={r['R']:6.1f} L={r['L']:6.1f} open={r['open_s']:5.1f} my={r['my_s']:.2f}")
print("T-sweep on R  (dropped echo-only / lost my-speech %)")
for T in range(-14, 1, 1):
    de = sum(1 for n, r in echo if r["R"] < T); lm = sum(r["my_s"] for n, r in rows if r["R"] < T and r["my_s"] >= 0.5) + sum(r["my_s"] for n,r in echo if r["R"]<T)
    print(f" T={T:3d}: echo dropped {de}/{len(echo)}  my lost {100*lm/tot_my:.2f}%")
print("T-sweep on L")
for T in range(-30, 1, 2):
    de = sum(1 for n, r in echo if r["L"] < T); lm = sum(r["my_s"] for n, r in rows if r["L"] < T)
    print(f" T2={T:3d}: echo dropped {de}/{len(echo)}  my lost {100*lm/tot_my:.2f}%")
