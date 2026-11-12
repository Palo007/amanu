"""C1: cut the engine's input into speech regions WITHOUT the neural gate (overnight 2026-11-12, track C).

N1's setting c cuts solve-gate's output into the gate's speech regions (DPDFNet as a detector,
about 427 s for the suite's 40 calls). Here the regions come from a cheap detector run on the
output of solve-floor (the lab's solve-res15: solver plus the 0.15 residual suppressor), then
the same cutting as n1.py's setting c (merge 1.5 s, pad 1.0 s, 25 s cap, -20 dBFS per region,
one WAV per region through `transcribe-cli --batch`). Only mic and far go into the signal path;
the settings are fixed for all calls.

Detectors (on the cleaned mic, 16 ms frames):
    E   frame energy (dB) against a noise floor = 10th percentile over a sliding 10 s window,
        speech when more than `margin_db` above it, runs under 0.25 s dropped, hangover 0.3 s
    F   300-3400 Hz band energy against the same kind of floor AND spectral flatness of that band
        below its own window median (speech is peaky, broadband noise is flat), same hangover

    py -P c1.py <work dir> time [regex]        detector time per call
    py -P c1.py <work dir> check [regex]       against the gate's regions (N1 cache): coverage, extra time, count
    py -P c1.py <work dir> run:<A,B> [regex]   screening (engine!), rows under <work>/wer2/n1/<setting>
    py -P c1.py <work dir> show:<A,B>          table against the cached lab rows
    (C1_SET=ho: held-out set, work dir C/ho)

<work dir> is %USERPROFILE%\\Amanu Quality Set\\lab\\overnight\\C.
"""
import json, os, shutil, sys, time
from pathlib import Path
import numpy as np
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
if os.environ.get("C1_SET") == "ho":
    os.environ["N1_SET"] = "ho"
import n1, asr, pipeline

Q, RATE = n1.Q, n1.RATE
LAB = n1.LAB
N1_DIR = LAB / "overnight" / "N1"
N, HOP = 512, 256
CUT = dict(merge_s=1.5, pad_s=1.0, cap_s=25.0)        # n1.py's setting c
SETTINGS = {"A": "E", "B": "F"}                       # engine setting -> detector
MARGIN_DB = {"E": 3.0, "F": 3.0}
HELDOUT = n1.HELDOUT


def frames_of(x):
    n = 1 + (len(x) - N) // HOP
    idx = np.arange(N)[None, :] + HOP * np.arange(n)[:, None]
    return x[idx] * np.hanning(N)[None, :]


def floor_db(db, win_s=10.0, q=10, dec=8):
    """Noise floor per frame: q-th percentile of db over a sliding window of win_s, computed on
    every dec-th frame and interpolated back."""
    d = db[::dec]
    k = max(3, int(win_s * RATE / HOP / dec) | 1)
    pad = np.pad(d, (k // 2, k // 2), mode="edge")
    w = np.lib.stride_tricks.sliding_window_view(pad, k)[:len(d)]
    f = np.percentile(w, q, axis=1)
    return np.interp(np.arange(len(db)), np.arange(len(d)) * dec, f)


def runs_to_mask(act, n_samples, min_s=0.25, hang_s=0.3):
    edges = np.flatnonzero(np.diff(np.concatenate([[0], act.astype(int), [0]])))
    for a, b in zip(edges[::2], edges[1::2]):
        if (b - a) * HOP < min_s * RATE:
            act[a:b] = False
    h = int(round(hang_s * RATE / HOP))
    act = np.convolve(act.astype(float), np.ones(2 * h + 1), "same") > 0
    m = np.repeat(act, HOP)
    m = np.concatenate([np.zeros(N // 2 - HOP // 2, bool), m])       # frame i is centred on i*HOP + N/2
    out = np.zeros(n_samples, bool)
    out[:min(n_samples, len(m))] = m[:n_samples]
    return out


def detect_e(x, margin_db=MARGIN_DB["E"]):
    p = (frames_of(x) ** 2).mean(axis=1)
    db = 10 * np.log10(p + 1e-12)
    return runs_to_mask(db > floor_db(db) + margin_db, len(x))


def detect_f(x, margin_db=MARGIN_DB["F"]):
    s = np.abs(np.fft.rfft(frames_of(x), axis=1)) ** 2
    lo, hi = int(300 * N / RATE), int(3400 * N / RATE)
    b = s[:, lo:hi] + 1e-14
    db = 10 * np.log10(b.mean(axis=1) + 1e-12)
    flat = np.exp(np.log(b).mean(axis=1)) / b.mean(axis=1)            # 1 = noise-like, small = peaky
    flat_ref = floor_db(-flat * 100, q=50) * -1 / 100                 # sliding median of the flatness
    return runs_to_mask((db > floor_db(db) + margin_db) & (flat < 1.5 * flat_ref), len(x))


DETECTORS = {"E": detect_e, "F": detect_f}


# ---------------------------------------------------------------- solve-floor's output, cached per call

def floor_output(work, name):
    f = Path(work) / "cache" / f"{name}.npy"
    if f.exists() and not HELDOUT:
        return np.load(f).astype(np.float64)
    folder = n1.REC / name
    mic, far = Q.read_mono(folder / "mic.wav"), Q.read_mono(folder / "system.wav")
    x = pipeline.VARIANTS["solve-res15"](mic, far)
    if not HELDOUT:
        f.parent.mkdir(parents=True, exist_ok=True)
        np.save(f, x.astype(np.float32))
    return x


# ---------------------------------------------------------------- commands

def to_mask_regions(mask, x):
    g = mask.astype(np.float64)
    return g, n1.regions(g, x, **CUT)


def transcribe(work, setting, name):
    row = asr.cached(work, f"n1/{setting}", name)
    if row is not None:
        return row
    x = floor_output(work, name)
    t0 = time.time()
    g, regs = to_mask_regions(DETECTORS[SETTINGS[setting]](x), x)
    det_s = time.time() - t0
    tmp = Path(work) / "tmp" / f"{setting}-{name}"
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir(parents=True)
    wavs = []
    for i, (a, b) in enumerate(regs):
        p = tmp / f"r{i:03d}.wav"
        Q.write_wav(p, n1.scaled(x, g, a, b))
        wavs.append(p)
    texts, secs = n1.run_batch(wavs, tmp) if wavs else ([], 0.0)
    row = n1.score_row(name, " ".join(t for t in texts if t), secs,
                       {"regions": len(regs), "region_s": sum(b - a for a, b in regs) / RATE, "detector_s": det_s})
    n1.save_row(work, setting, name, row)
    shutil.rmtree(tmp)
    return row


def check(work, regex):
    print(f"{'call':26s} {'snr':>5s} {'gate s':>7s} | " + " | ".join(f"{d}: n(gate n) cover% extra s" for d in "EF"))
    tot = {d: [0, 0, 0.0, 0.0, 0.0] for d in "EF"}
    for name in n1.screen_names(regex):
        truth = json.loads((n1.REC / name / "truth.json").read_text(encoding="utf-8"))
        e, g = n1.gate_pieces(N1_DIR, name)
        gx = g * e
        gregs = n1.regions(g, gx, **CUT)
        gcov = np.zeros(len(g), bool)
        for a, b in gregs:
            gcov[a:b] = True
        x = floor_output(work, name)
        cells = []
        for d in "EF":
            m = DETECTORS[d](x)
            regs = n1.regions(m.astype(float), x, **CUT)
            cov = np.zeros(len(m), bool)
            for a, b in regs:
                cov[a:b] = True
            share = 100 * (gcov & cov).sum() / max(1, gcov.sum())
            extra = (cov & ~gcov).sum() / RATE
            cells.append(f"{d}: {len(regs):2d}({len(gregs):2d}) {share:6.1f} {extra:6.1f}")
            t = tot[d]; t[0] += len(regs); t[1] += len(gregs); t[2] += (gcov & cov).sum(); t[3] += gcov.sum(); t[4] += extra
        print(f"{name:26s} {str(truth.get('snr')):>5s} {gcov.sum() / RATE:7.1f} | " + " | ".join(cells), flush=True)
    for d in "EF":
        t = tot[d]
        print(f"detector {d}: {t[0]} regions (gate {t[1]}), covers {100 * t[2] / t[3]:.1f} % of the gate's region time, extra {t[4]:.0f} s over {t[3] / RATE:.0f} s")


def timing(work, regex):
    for d in "EF":
        ts = []
        for name in n1.screen_names(regex)[:6]:
            x = floor_output(work, name)
            t0 = time.time(); DETECTORS[d](x); ts.append(time.time() - t0)
        print(f"detector {d}: {np.mean(ts):.3f} s per call (mean of {len(ts)}, max {max(ts):.3f})")


def show(work, settings):
    base = {"floor": asr.rows(LAB, "ho/solve-floor" if HELDOUT else "su/solve-res15"),
            "gate": asr.rows(LAB, "ho/solve-gate" if HELDOUT else "su/solve-gate")}
    groups = [("0", lambda r: r["snr"] == 0), ("-5", lambda r: r["snr"] == -5),
              ("14 noisy", lambda r: r["snr"] in (0, -5)), ("no noise", lambda r: r["snr"] is None)]
    for s in settings:
        rs = asr.rows(work, f"n1/{s}")
        if not rs:
            print(f"{s}: no rows"); continue
        line = f"{s} n={len(rs):2d} WER {asr.wer(rs.values()):5.1f} | " + " ".join(f"{g} {asr.wer(r for r in rs.values() if f(r)):5.1f}" for g, f in groups)
        for bn, b in base.items():
            b = {k: v for k, v in b.items() if k in rs}
            d, lo, hi = asr.bootstrap(rs, b)
            line += f" | vs {bn} ({asr.wer(b.values()):.1f}; noisy {asr.wer(r for r in b.values() if r['snr'] in (0, -5)):.1f}; clean {asr.wer(r for r in b.values() if r['snr'] is None):.1f}): {d:+.1f} [{lo:+.1f}, {hi:+.1f}]"
        print(line + f" | engine {sum(r['secs'] for r in rs.values()):.0f} s, regions/call {np.mean([r['regions'] for r in rs.values()]):.1f}, detector {np.mean([r['detector_s'] for r in rs.values()]):.2f} s/call")


def main():
    work, cmd = sys.argv[1], sys.argv[2]
    rest = sys.argv[3:]
    kind, _, what = cmd.partition(":")
    regex = rest[0] if rest else None
    if kind == "check":
        check(work, regex)
    elif kind == "time":
        timing(work, regex)
    elif kind == "run":
        from concurrent.futures import ThreadPoolExecutor
        for s in what.split(","):
            with ThreadPoolExecutor(3) as pool:
                list(pool.map(lambda n, s=s: transcribe(work, s, n), n1.screen_names(regex)))
            show(work, [s])
    elif kind == "show":
        show(work, what.split(","))


if __name__ == "__main__":
    main()
