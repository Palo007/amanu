"""Track N4 (overnight 2026-11-12): noise reduction without a neural denoiser, by gain floors per band.

A classic spectral gain that cannot delete much speech. The noise spectrum comes from the
gate's CLOSED stretches only (time-weighted around each frame, so each speech region gets the
noise measured nearest to it); inside the open stretches a Wiener gain per bin and frame is
applied with a floor and smoothing in time and frequency. The filtered signal then goes
through solve-gate's gate and n1 setting c's region cutting.

    py -P n4.py check [call regex|-] [setting,...]    signal check (CPU only, no engine)
    py -P n4.py noise                                 the closed-stretch noise spectra per call
    py -P n4.py run <work dir> <setting>              20 calls: filter, gate, cut like n1 c, transcribe (NEEDS THE ENGINE)
    py -P n4.py show <work dir> <setting>             table against su/solve-gate and n1 setting c

Only the mic and far signals go into the remover (through the cached solver output `e` and
the gate `g` of N1's cache). near.wav is read only to MEASURE: the filter's gain is applied to
the speech part and to the rest separately ("shadow filtering"), so the speech level and the
SNR change are exact for the gain that the filter chose from the mixture. No per-call settings.
"""
import json, os, re, shutil, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import numpy as np
from scipy.ndimage import gaussian_filter1d, uniform_filter, binary_erosion

import lab

RATE = lab.RATE
HOME = Path.home() / "Amanu Quality Set" / "lab" / "overnight"
N1CACHE = HOME / "N1" / "cache"
N, HOP = 512, 128
WIN = np.sqrt(np.hanning(N + 1)[:-1])           # periodic sqrt-Hann: analysis and synthesis, hop N/4 gives 2.0
FREQ = np.fft.rfftfreq(N, 1 / RATE)

# Fixed settings, the same for every call.
SETTINGS = {
    "f6":  dict(floor="flat", floor_db=-6.0),
    "f12": dict(floor="flat", floor_db=-12.0),
    # band-dependent: -15 dB outside 250..4000 Hz where speech has little energy anyway; inside it
    # the floor goes from -15 dB (noise that does not look like speech) to -6 dB (babble), by the
    # share of the noise energy in 300..3000 Hz measured in the closed stretches only.
    "bd":  dict(floor="speechlike", lo_db=-15.0, hi_db=-6.0),
}
COMMON = dict(sigma_s=4.0, guard_s=0.15, smooth_t=3, smooth_f=3, beta=1.0)


# ---------------------------------------------------------------- STFT

def stft(x):
    pad = np.concatenate([np.zeros(N // 2), x, np.zeros(N)])
    frames = 1 + (len(x) + N // 2) // HOP
    pad = np.concatenate([pad, np.zeros(max(0, (frames - 1) * HOP + N - len(pad)))])
    idx = np.arange(N)[None, :] + HOP * np.arange(frames)[:, None]
    return np.fft.rfft(pad[idx] * WIN, axis=1)


def istft(X, n):
    frames = X.shape[0]
    y = np.zeros((frames - 1) * HOP + N)
    seg = np.fft.irfft(X, N, axis=1) * WIN / 2.0
    q = N // HOP
    for k in range(q):                               # overlap-add without a Python loop over frames
        s = seg[k::q]
        flat = s.reshape(-1)
        start = k * HOP
        y[start:start + flat.size] += flat[: len(y) - start]
    return y[N // 2:N // 2 + n]


# ---------------------------------------------------------------- the filter

def closed_frames(g, nframes):
    """Frames fully inside a closed stretch of the gate, with a guard on both sides."""
    centre = np.minimum((np.arange(nframes) * HOP).astype(int), len(g) - 1)
    closed = g[centre] <= 0.5
    guard = int(COMMON["guard_s"] * RATE / HOP)
    return binary_erosion(closed, iterations=guard, border_value=1)


def noise_psd(P, closed, sigma_s):
    """Per frame and bin: the mean power of the closed frames, Gaussian-weighted in time around
    the frame (so a region gets the noise measured nearest to it), smoothed over 5 bins. Frames
    with too little closed weight around them fall back to the call's mean closed spectrum."""
    s = sigma_s * RATE / HOP
    m = closed.astype(float)
    num = gaussian_filter1d(P * m[:, None], s, axis=0, mode="nearest", truncate=4.0)
    den = gaussian_filter1d(m, s, mode="nearest", truncate=4.0)[:, None]
    glob = (P[closed].mean(axis=0) if closed.any() else np.zeros(P.shape[1]))[None, :]
    w = np.clip(den / 0.05, 0, 1)
    Nz = w * num / np.maximum(den, 1e-9) + (1 - w) * glob
    return uniform_filter(Nz, size=(1, 5), mode="nearest"), glob[0]


def speech_likeness(glob):
    """0..1 from the closed stretches' mean spectrum: the share of its energy in 300..3000 Hz
    out of 100..8000 Hz; 0.45 and below counts as 0 and 0.75 and above as 1."""
    band = (FREQ >= 300) & (FREQ <= 3000)
    whole = (FREQ >= 100) & (FREQ <= 8000)
    frac = glob[band].sum() / max(glob[whole].sum(), 1e-20)
    return float(np.clip((frac - 0.45) / 0.30, 0, 1)), float(frac)


def floor_curve(cfg, glob):
    """Gain floor (linear) per bin."""
    if cfg["floor"] == "flat":
        return np.full(len(FREQ), 10 ** (cfg["floor_db"] / 20))
    s, _ = speech_likeness(glob)
    inband = (FREQ >= 250) & (FREQ <= 4000)
    db = np.where(inband, cfg["lo_db"] + s * (cfg["hi_db"] - cfg["lo_db"]), cfg["lo_db"])
    return 10 ** (db / 20)


def gain(e, g, cfg, common=COMMON):
    """The Wiener gain, per frame and bin, of the mixture `e` given the gate `g`. Uses e and g only."""
    X = stft(e)
    P = np.abs(X) ** 2
    closed = closed_frames(g, X.shape[0])
    Nz, glob = noise_psd(P, closed, common["sigma_s"])
    Nz = Nz * common["beta"]
    Psm = uniform_filter(P, size=(common["smooth_t"], common["smooth_f"]), mode="nearest")
    xi = np.maximum(Psm / np.maximum(Nz, 1e-20) - 1.0, 0.0)
    G = xi / (1.0 + xi)
    fl = floor_curve(cfg, glob)[None, :]
    G = np.maximum(G, fl)
    G = uniform_filter(G, size=(common["smooth_t"], common["smooth_f"]), mode="nearest")
    G = np.maximum(G, fl)
    if not closed.any():                              # nothing to measure the noise from: leave the signal alone
        G = np.ones_like(G)
    return G, X, glob


def apply(e, G, X=None):
    X = stft(e) if X is None else X
    return istft(X * G, len(e))


def filtered(e, g, setting):
    G, X, _ = gain(e, g, SETTINGS[setting])
    return apply(e, G, X)


# ---------------------------------------------------------------- data

def names():
    return sorted(f.stem for f in N1CACHE.glob("echo-*.npz"))


def noisy():
    return [n for n in names() if re.search(r"snr(\+0|-5)$", n)]


def clean():
    return [n for n in names() if n.endswith("snrclean")]


def pieces(name):
    z = np.load(N1CACHE / f"{name}.npz")
    return z["e"].astype(np.float64), z["g"].astype(np.float64)


def regions_c(g, e):
    import n1
    return n1.regions(g, g * e, **n1.SETTINGS["c"])


# ---------------------------------------------------------------- the signal check

def frame_mask(near, n):
    """Samples of my speech: 16 ms frames of near.wav within 35 dB of its 99th percentile."""
    import n2
    pn = n2.frame_power(near)
    db = 10 * np.log10(pn + 1e-12)
    fr = db > np.percentile(db, 99) - 35
    m = np.repeat(fr, n2.HOP)
    return np.pad(m, (0, max(0, n - len(m))))[:n]


def measure(name, settings):
    """Per setting: (speech level change dB over my speech frames, SNR gain dB inside the
    speech regions, SNR gain over my speech frames, residual change dB in the regions).
    The truth only measures."""
    sc = lab.load_recording(name)
    near = sc["near"]
    e, g = pieces(name)
    n = len(e)
    near = near[:n]
    m = frame_mask(near, n)
    a = float(np.dot(e[m], near[m]) / np.dot(near[m], near[m]))
    s = a * near
    r = e - s
    R = np.zeros(n, bool)
    for a0, b0 in regions_c(g, e):
        R[a0:b0] = True
    snr = lambda ss, rr, mm: 10 * np.log10((ss[mm] ** 2).sum() / max((rr[mm] ** 2).sum(), 1e-20))
    out = {}
    for st in settings:
        G, X, glob = gain(e, g, SETTINGS[st])
        fs, fr_ = apply(s, G), apply(r, G)
        lvl = 10 * np.log10((fs[m] ** 2).sum() / (s[m] ** 2).sum())
        out[st] = (lvl, snr(fs, fr_, R) - snr(s, r, R), snr(fs, fr_, m) - snr(s, r, m),
                   10 * np.log10((fr_[R] ** 2).sum() / (r[R] ** 2).sum()))
    return out, speech_likeness(glob), snr(s, r, R)


def check(pattern=None, settings=None):
    settings = settings or list(SETTINGS)
    print("sp = speech level change over my speech frames; snr = SNR gain inside the c regions (on my speech frames in brackets); n = residual change in the regions; dB")
    print(f"{'call':22} {'snr in':>6} {'like':>5} | " + " | ".join(f"{s:^40}" for s in settings))
    agg = {}
    for nm in noisy() + clean():
        if pattern and not re.search(pattern, nm):
            continue
        o, (like, frac), sin = measure(nm, settings)
        cells = [f"sp {o[s][0]:+5.2f} snr {o[s][1]:+5.2f} ({o[s][2]:+5.2f}) n {o[s][3]:+5.1f}" for s in settings]
        print(f"{nm[5:]:22} {sin:6.1f} {like:5.2f} | " + " | ".join(cells), flush=True)
        key = nm.split("-")[1] + (" -5" if nm.endswith("-5") else " 0" if nm.endswith("+0") else " clean")
        for s in settings:
            agg.setdefault(key, {}).setdefault(s, []).append(o[s])
    print("\nmean per noise and SNR (speech dB, SNR gain in regions, (on speech frames), noise dB, worst speech dB):")
    for key in sorted(agg):
        print(f"{key:12} " + " | ".join(f"{s}: {np.mean([x[0] for x in agg[key][s]]):+5.2f} {np.mean([x[1] for x in agg[key][s]]):+5.2f} "
                                         f"({np.mean([x[2] for x in agg[key][s]]):+5.2f}) {np.mean([x[3] for x in agg[key][s]]):+5.1f} min {min(x[0] for x in agg[key][s]):+5.2f}"
                                         for s in settings))


def noise_report():
    print(f"{'call':22} closed s | share in 300..3000 Hz | likeness")
    for nm in noisy() + clean():
        e, g = pieces(nm)
        X = stft(e)
        P = np.abs(X) ** 2
        c = closed_frames(g, X.shape[0])
        glob = P[c].mean(axis=0) if c.any() else np.zeros(P.shape[1])
        like, frac = speech_likeness(glob)
        print(f"{nm[5:]:22} {c.sum() * HOP / RATE:7.1f} | {frac:5.2f} | {like:4.2f}")


# ---------------------------------------------------------------- the engine part (not run in step 1)

def run(work, tag):
    import asr, n1
    from concurrent.futures import ThreadPoolExecutor

    def one(name):
        row = asr.cached(work, f"n4/{tag}", name)
        if row is not None:
            return row
        e, g = pieces(name)
        x = g * filtered(e, g, tag)                          # filter, then solve-gate's gate
        regs = n1.regions(g, g * e, **n1.SETTINGS["c"])       # the regions are solve-gate's, as in n1 c
        tmp = Path(work) / "tmp" / f"n4-{tag}-{name}"
        if tmp.exists():
            shutil.rmtree(tmp)
        tmp.mkdir(parents=True)
        wavs = []
        for i, (a, b) in enumerate(regs):
            p = tmp / f"r{i:03d}.wav"
            lab.Q.write_wav(p, n1.scaled(x, g, a, b))
            wavs.append(p)
        texts, secs = n1.run_batch(wavs, tmp) if wavs else ([], 0.0)
        row = n1.score_row(name, " ".join(t for t in texts if t), secs, {"regions": len(regs)})
        f = Path(work) / "wer2" / "n4" / tag / f"{name}.json"
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(json.dumps(row, ensure_ascii=False), encoding="utf-8")
        shutil.rmtree(tmp)
        return row
    with ThreadPoolExecutor(3) as pool:
        list(pool.map(one, noisy() + clean()))


def show(work, tag):
    import asr, n1
    ns = noisy() + clean()
    base = {k: r for k, r in asr.rows(n1.LAB, "su/solve-gate").items() if k in ns}
    rs = asr.rows(work, f"n4/{tag}")
    groups = [("0", lambda r: r["snr"] == 0), ("-5", lambda r: r["snr"] == -5),
              ("14 noisy", lambda r: r["snr"] in (0, -5)), ("no noise", lambda r: r["snr"] is None)]
    print(f"n4/{tag} n={len(rs)} WER " + " ".join(f"{a} {asr.wer(r for r in rs.values() if f(r)):.1f}" for a, f in groups)
          + f" | echo words {sum(r['leak'] for r in rs.values())}")
    c = {k: r for k, r in asr.rows(HOME / "N1", "n1/c").items() if k in rs}
    for label, ref in (("solve-gate", base), ("n1 setting c", c)):
        ref = {k: v for k, v in ref.items() if k in rs}
        if ref:
            d, lo, hi = asr.bootstrap(rs, ref)
            print(f"  against {label}: {d:+.1f} [{lo:+.1f}, {hi:+.1f}] (reference " +
                  " ".join(f"{a} {asr.wer(r for r in ref.values() if f(r)):.1f}" for a, f in groups) + ")")


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "check":
        check(sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != "-" else None,
              sys.argv[3].split(",") if len(sys.argv) > 3 else None)
    elif cmd == "noise":
        noise_report()
    elif cmd == "run":
        run(sys.argv[2], sys.argv[3]); show(sys.argv[2], sys.argv[3])
    elif cmd == "show":
        show(sys.argv[2], sys.argv[3])
