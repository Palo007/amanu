"""Track N3 (overnight 2026-11-12): a larger DPDFNet (dpdfnet2, dpdfnet4) blended in where the noise is loud.

    py -P n3.py cache                       dpdfnet2/dpdfnet4 outputs on N2's cached solver outputs (14 noisy + 6 clean calls), with timings
    py -P n3.py speed                       seconds per minute of audio, baseline beside the larger models
    py -P n3.py check [call regex]          signal check: speech kept, noise, identity on the clean calls
    py -P n3.py run <work dir> <setting>    14 noisy calls: blend, cut like n1 setting c, transcribe (NEEDS THE ENGINE)
    py -P n3.py show <work dir> <setting>   table against the cached su/solve-gate rows and against n1's setting c

The scheme is n2's, with one change: the weight curve and the gate still come from the BASELINE
model's output (cached by N2), so a setting differs from n2-w50 only in the sound that is blended
in. Only the mic and far signals are used; the truth only places the scores in `check`.
"""
import os, re, sys, time, json, shutil
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import numpy as np
import lab, denoise, pipeline, n2

N3 = Path.home() / "Amanu Quality Set" / "lab" / "overnight" / "N3"
MODELS = {"d2": "dpdfnet2.onnx", "d4": "dpdfnet4.onnx"}
RATE = lab.RATE


def _big(model, e):
    """The larger model's output for the solver output `e`, from N3's own model files."""
    old = denoise.MODELS
    denoise.MODELS = N3
    try:
        return denoise.run(e, "dpdfnet", MODELS[model])
    finally:
        denoise.MODELS = old


def big(model, mic, far):
    """(e, baseline c, larger c), the last cached under N3/cache."""
    e, c = n2.base(mic, far)
    f = N3 / "cache" / f"{n2._key(mic, far)}-{model}.npy"
    if f.exists():
        return e, c, np.load(f).astype(np.float64)
    cb = _big(model, e)
    f.parent.mkdir(parents=True, exist_ok=True)
    np.save(f, cb.astype(np.float32))
    return e, c, cb


def blended(model, w_max):
    """solve-gate's gate times ((1 - w) e + w c_big), with w and the gate from the baseline's c."""
    def run(mic, far):
        e, c, cb = big(model, mic, far)
        snr, nl = n2.snr_estimate(e, c)
        w = n2.weight_samples(n2.weight_curve(snr, nl, w_max), len(e))
        return pipeline.speech_gate(e, c) * ((1 - w) * e + w * cb)
    return run


SETTINGS_W = {"n3-d2-w50": ("d2", 0.5), "n3-d4-w50": ("d4", 0.5), "n3-d4-w80": ("d4", 0.8)}
SETTINGS = {k: blended(m, w) for k, (m, w) in SETTINGS_W.items()}


def noisy():
    return [n for n in n2.names() if re.search(r"snr(\+0|-5)$", n)]


def clean():
    return [n for n in n2.names() if n.endswith("snrclean")]


def cache():
    rows = []
    for nm in noisy() + clean():
        sc = lab.load_recording(nm)
        e, c = n2.base(sc["mic"], sc["far"])
        mins = len(e) / RATE / 60
        t = {}
        for m in MODELS:
            t0 = time.time(); big(m, sc["mic"], sc["far"]); t[m] = (time.time() - t0) / mins
        print(nm, f"{mins:.2f} min", " ".join(f"{m} {s:.1f} s/min" for m, s in t.items()), flush=True)


def speed():
    """Seconds of CPU wall time per minute of audio, each model on the same three calls, 2 threads, cold start excluded."""
    sc = [lab.load_recording(n) for n in noisy()[:3]]
    xs = [n2.base(s["mic"], s["far"])[0] for s in sc]
    mins = sum(len(x) for x in xs) / RATE / 60
    out = {}
    for label, model in [("baseline", "dpdfnet_baseline.onnx"), ("dpdfnet2", "dpdfnet2.onnx"), ("dpdfnet4", "dpdfnet4.onnx")]:
        old = denoise.MODELS; denoise.MODELS = N3 if label != "baseline" else old
        if label == "baseline":
            denoise.MODELS = Path.home() / "Amanu Quality Set" / "lab" / "models"
        denoise.run(xs[0][:RATE * 5], "dpdfnet", model)                       # warm-up
        t0 = time.time()
        for x in xs:
            denoise.run(x, "dpdfnet", model)
        out[label] = (time.time() - t0) / mins
        denoise.MODELS = old
        print(f"{label:9s} {out[label]:.2f} s per minute of audio ({mins:.2f} min)", flush=True)
    return out


def check(pattern=None):
    """Per call: speech level of each model's own output against the solver output `e`
    (projection on near.wav over my speech frames), the share of my speech frames it turns
    below half the level (-6 dB) frame by frame, and for the blended settings the speech
    gain and noise change against solve-gate, and the largest difference on clean calls."""
    H = n2.HOP
    print(f"{'call':20} | raw model output: speech dB / share of my frames under -6 dB  (base, d2, d4) | blended vs solve-gate: speech dB / noise dB")
    tot = {}
    for nm in (noisy() + clean()):
        if pattern and not re.search(pattern, nm):
            continue
        sc = lab.load_recording(nm)
        mic, far, near = sc["mic"], sc["far"], sc["near"]
        e, c, c2 = big("d2", mic, far)
        c4 = big("d4", mic, far)[2]
        n = len(e); near = near[:n]
        pn = n2.frame_power(near)
        db = 10 * np.log10(pn + 1e-12)
        fr = db > np.percentile(db, 99) - 35
        m = np.repeat(fr, H)
        m = np.pad(m, (0, max(0, n - len(m))))[:n]
        proj = lambda x, mm=m: float(np.dot(x[mm], near[mm]) / np.dot(near[mm], near[mm]))
        resid = lambda x: 10 * np.log10(np.mean((x - proj(x) * near) ** 2) + 1e-14)
        idx = np.arange(n2.N)[None, :] + H * np.arange(len(pn))[:, None]
        nf = near[idx]
        def share(x):
            xf = x[idx]
            g = (xf * nf).sum(1) / ((nf * nf).sum(1) + 1e-14)
            return float((g[fr] < 0.5).mean()) * 100
        cells = [f"{20 * np.log10(proj(x) / proj(e)):+5.2f}/{share(x):4.1f}%" for x in (c, c2, c4)]
        ref = pipeline.speech_gate(e, c) * e
        bl = []
        for k, f in SETTINGS.items():
            out = f(mic, far)
            d = 20 * np.log10(np.abs(out - ref).max() / np.abs(ref).max() + 1e-12)
            bl.append(f"{20 * np.log10(proj(out) / proj(ref)):+5.2f}/{resid(out) - resid(ref):+5.2f}" + (f" d{d:.0f}" if nm.endswith("clean") else ""))
        print(f"{nm[5:]:20} | " + "  ".join(cells) + " | " + "  ".join(bl), flush=True)
        key = ("-5" if nm.endswith("-5") else "0" if nm.endswith("+0") else "clean")
        tot.setdefault(key, []).append([float(x.split("/")[0]) for x in cells])
    for k, v in tot.items():
        print(f"mean speech change of the raw output, SNR {k}: base/d2/d4 = " + " ".join(f"{x:+.2f}" for x in np.mean(v, axis=0)))


def run(work, tag):
    import asr, n1
    from concurrent.futures import ThreadPoolExecutor
    names = noisy()

    def one(name):
        row = asr.cached(work, f"n3/{tag}", name)
        if row is not None:
            return row
        sc = lab.load_recording(name)
        e, c = n2.base(sc["mic"], sc["far"])
        g = pipeline.speech_gate(e, c)
        x = SETTINGS[tag](sc["mic"], sc["far"])
        regs = n1.regions(g, x, **n1.SETTINGS["c"])
        tmp = Path(work) / "tmp" / f"{tag}-{name}"
        if tmp.exists():
            shutil.rmtree(tmp)
        tmp.mkdir(parents=True)
        wavs = []
        for i, (a, b) in enumerate(regs):
            p = tmp / f"r{i:03d}.wav"
            lab.Q.write_wav(p, n1.scaled(x, g, a, b)); wavs.append(p)
        texts, secs = n1.run_batch(wavs, tmp) if wavs else ([], 0.0)
        row = n1.score_row(name, " ".join(t for t in texts if t), secs, {"regions": len(regs)})
        f = Path(work) / "wer2" / "n3" / tag / f"{name}.json"
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(json.dumps(row, ensure_ascii=False), encoding="utf-8")
        shutil.rmtree(tmp)
        return row
    with ThreadPoolExecutor(3) as pool:
        list(pool.map(one, names))


def show(work, tag):
    import asr, n1
    names = noisy()
    base = {k: r for k, r in asr.rows(n1.LAB, "su/solve-gate").items() if k in names}
    rs = asr.rows(work, f"n3/{tag}")
    g = [("SNR 0", lambda r: r["snr"] == 0), ("SNR -5", lambda r: r["snr"] == -5)]
    d, lo, hi = asr.bootstrap(rs, base)
    print(f"n3/{tag} n={len(rs)} WER {asr.wer(rs.values()):.1f} | " + " ".join(f"{a} {asr.wer(r for r in rs.values() if f(r)):.1f}" for a, f in g) + f" | echo words {sum(r['leak'] for r in rs.values())} | against solve-gate {d:+.1f} [{lo:+.1f}, {hi:+.1f}]")
    c = asr.rows(os.path.join(os.path.expanduser("~"), "Amanu Quality Set", "lab", "overnight", "N1"), "n1/c")
    c = {k: r for k, r in c.items() if k in names}
    if c:
        d, lo, hi = asr.bootstrap(rs, c)
        print(f"n1 setting c alone: WER {asr.wer(c.values()):.1f}; this against it {d:+.1f} [{lo:+.1f}, {hi:+.1f}]")


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "cache": cache()
    elif cmd == "speed": speed()
    elif cmd == "check": check(sys.argv[2] if len(sys.argv) > 2 else None)
    elif cmd == "run": run(sys.argv[2], sys.argv[3]); show(sys.argv[2], sys.argv[3])
    elif cmd == "show": show(sys.argv[2], sys.argv[3])
