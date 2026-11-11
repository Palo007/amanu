"""Track N2 (overnight 2026-11-12): an adaptive blend of the denoiser's output into the
canceller's output, weighted by the local SNR that DPDFNet itself reveals.

    py -P n2.py cache <work dir> [call regex]     solver + DPDFNet outputs of the calls, cached
    py -P n2.py check <work dir>                  signal check of the three settings
    py -P n2sw.py <work dir> run:<variants>       suite WER (n2sw.py registers the variants in sw.py)

Only the mic and far signals are used. The per-window SNR estimate is the share of the
solver output's energy that DPDFNet keeps: where it keeps nearly everything the call is
clean and the weight is 0 (the output is then solve-gate's, bit for bit); where it removes
most of it there is a lot of noise and the weight rises towards w_max. The gate of
pipeline.speech_gate stays on top, as in solve-gate.
"""
import hashlib, os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from pathlib import Path
import lab, solve, denoise, pipeline

HOME = Path.home() / "Amanu Quality Set" / "lab" / "overnight" / "N2"
N, HOP = 512, 256


def _key(mic, far):
    h = hashlib.md5(np.ascontiguousarray(mic[::37], dtype=np.float32).tobytes() + np.ascontiguousarray(far[::37], dtype=np.float32).tobytes())
    return f"{len(mic)}-{h.hexdigest()[:16]}"


def base(mic, far):
    """(solver output, DPDFNet output for it), cached under HOME/cache by the signals."""
    f = HOME / "cache" / f"{_key(mic, far)}.npz"
    if f.exists():
        z = np.load(f)
        return z["e"].astype(np.float64), z["c"].astype(np.float64)
    e = pipeline.VARIANTS["solve"](mic, far)
    c = denoise.dpdf(e)
    f.parent.mkdir(parents=True, exist_ok=True)
    np.savez(f, e=e.astype(np.float32), c=c.astype(np.float32))
    return e, c


def frame_power(x):
    frames = 1 + (len(x) - N) // HOP
    idx = np.arange(N)[None, :] + HOP * np.arange(frames)[:, None]
    return (x[idx] ** 2).mean(axis=1)


def window_sums(p, win_s=0.5):
    k = max(1, int(round(win_s * lab.RATE / HOP)))
    return np.convolve(np.pad(p, (k // 2, k - 1 - k // 2), mode="edge"), np.ones(k), "valid")[:len(p)]


def snr_estimate(e, c, win_s=0.5):
    """Per frame, over a 0.5 s window: (SNR in dB, noise level in dB re full scale).
    Speech is what DPDFNet keeps, noise what it removes; the energy removed is the noise."""
    se, sc = window_sums(frame_power(e), win_s), window_sums(frame_power(c), win_s)
    sc = np.minimum(sc, se)
    noise = np.maximum(se - sc, 1e-14)
    return 10 * np.log10(np.maximum(sc, 1e-14) / noise), 10 * np.log10(noise / int(round(win_s * lab.RATE / HOP)) + 1e-14)


def weight_curve(snr, noise_db, w_max, hi_db=10.0, lo_db=-5.0, dead_hi=-17.0, dead_lo=-27.0, floor_db=-50.0):
    """A trapezoid in the estimated SNR. 0 at `hi_db` dB and above, w_max from `lo_db` down to
    `dead_hi`, then back to 0 at `dead_lo`: where DPDFNet keeps almost nothing the window is
    silence or speech that it removed, and mixing its output in would only turn my words down
    (the gate decides there). Also 0 where the noise floor (`noise_floor`) is under `floor_db` dBFS, with a
    6 dB ramp: a quiet call has nothing to blend."""
    w = w_max * np.clip((hi_db - snr) / (hi_db - lo_db), 0, 1) * np.clip((snr - dead_lo) / (dead_hi - dead_lo), 0, 1)
    return w * np.clip((noise_floor(noise_db) - floor_db) / 6.0, 0, 1)


def noise_floor(noise_db, span_s=6.0, q=10):
    """The 10th percentile of the removed-noise level over +-6 s. Babble never goes quiet, so
    its floor stays high; the leftovers of the far end in a quiet call come in bursts, and
    between them the level falls under -55 dBFS."""
    from scipy import ndimage
    step = 8
    k = 2 * int(span_s * lab.RATE / HOP / step) + 1
    small = ndimage.percentile_filter(noise_db[::step], q, size=k, mode="nearest")
    return np.interp(np.arange(len(noise_db)), np.arange(len(small)) * step, small)


def weight_samples(w, length, smooth_s=0.5):
    """The frame weights as a smooth per-sample curve (a moving average of `smooth_s`)."""
    centres = HOP * np.arange(len(w)) + N / 2
    g = np.interp(np.arange(length), centres, w)
    k = int(smooth_s * lab.RATE)
    return np.convolve(np.pad(g, k // 2, mode="edge"), np.ones(k) / k, "valid")[:length]


def blended(w_max, **curve):
    def run(mic, far):
        e, c = base(mic, far)
        snr, nl = snr_estimate(e, c)
        w = weight_samples(weight_curve(snr, nl, w_max, **curve), len(e))
        return pipeline.speech_gate(e, c) * ((1 - w) * e + w * c)
    return run


SETTINGS_W = {"n2-w50": 0.5, "n2-w80": 0.8, "n2-w65-guard": 0.65}
SETTINGS = {
    "n2-w50": blended(0.5),
    "n2-w80": blended(0.8),
    "n2-w65-guard": blended(0.65, dead_hi=-8.0, dead_lo=-16.0),   # stays out of windows where DPDFNet removed most of the signal
}


def names(pattern=None):
    n = sorted(p.name for p in lab.Q.RECORDINGS.iterdir() if p.name.startswith("echo-"))
    return [x for x in n if re.search(pattern, x)] if pattern else n


def check(pattern=None):
    """Signal check; the truth only places the scores. Over my speech frames (near.wav within
    35 dB of its 99th percentile) the speech gain is the projection of the output on near.wav,
    over solve-gate's; the noise is the output minus that projection. Cells, per setting
    against solve-gate: speech gain dB / noise change dB (all frames) ; d = the largest
    sample difference in dB re the peak. `wmean`, `wmax` are for w_max = 1 of the first curve."""
    print(f"{'call':24} {'w50 mean':>8} {'max':>4} | " + " | ".join(f"{k:^30}" for k in SETTINGS))
    for nm in names(pattern):
        sc = lab.load_recording(nm)
        e, c = base(sc["mic"], sc["far"])
        ref = pipeline.speech_gate(e, c) * e
        near = sc["near"]
        pn = frame_power(near)
        talk = np.repeat(10 * np.log10(pn + 1e-12) > np.percentile(10 * np.log10(pn + 1e-12), 99) - 35, HOP)
        talk = np.pad(talk, (0, max(0, len(e) - len(talk))))[:len(e)]
        m = talk[:len(e)]
        proj = lambda x: float(np.dot(x[m], near[:len(e)][m]) / np.dot(near[:len(e)][m], near[:len(e)][m]))
        resid = lambda x: 10 * np.log10(np.mean((x - proj(x) * near[:len(e)]) ** 2) + 1e-14)
        snr, nl = snr_estimate(e, c)
        w = weight_curve(snr, nl, SETTINGS_W["n2-w50"])
        cells = []
        for k, f in SETTINGS.items():
            out = f(sc["mic"], sc["far"])
            cells.append(f"{20 * np.log10(proj(out) / proj(ref)):+5.2f} / {resid(out) - resid(ref):+6.2f} d{20 * np.log10(np.abs(out - ref).max() / np.abs(ref).max() + 1e-12):4.0f}")
        print(f"{nm[5:]:24} {w.mean():8.3f} {w.max():4.2f} | " + " | ".join(cells), flush=True)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    cmd = sys.argv[1]
    if cmd == "cache":
        for nm in names(sys.argv[3] if len(sys.argv) > 3 else None):
            sc = lab.load_recording(nm); base(sc["mic"], sc["far"]); print(nm, flush=True)
    elif cmd == "check":
        check(sys.argv[3] if len(sys.argv) > 3 else None)
