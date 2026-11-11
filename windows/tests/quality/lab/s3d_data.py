"""Track S3 (overnight 2026-11-12): the training data of the distilled speech gate.

    py -P s3d_data.py <worker> <seconds of wall time> [n validation mixes]

Mixes of FLEURS Slovak *train* speech (the suite uses test, the held-out set dev, see n5_fetch.py) with
babble made from other train talkers, DEMAND PSTATION/OHALLWAY, coloured noise, stretches of silence and
far-end-like leftovers (other talkers through a reverb, 8 to 45 dB under the near talker). Each mix is 60 s.
It is labelled by the real gate's front end: DPDFNet baseline (float, 1 thread, the whole mix as one stream, as
the lab does) and, per STFT frame of pipeline.speech_gate (512 samples, hop 256), the level DPDFNet keeps:
kept = 10 log10(power of its output / power of the input). Audio is never written: only int8 log-mel
features, the frame level of the input re its 95th percentile (float16) and the int8 label go to disk.
Never suite or held-out audio. Output: %USERPROFILE%\Amanu Quality Set\lab\overnight\S3\data.
"""
import os, sys, time
HOME_ = os.path.join(os.path.expanduser("~"), "Amanu Quality Set", "lab", "overnight")
sys.path.insert(0, os.path.join(HOME_, "N5", "pylib"))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import numpy as np
from scipy.signal import fftconvolve

RATE, N, HOP, NMEL = 16000, 512, 256, 64
OUT = Path(HOME_) / "S3" / "data"


def mel_bank():
    def mel(f): return 2595 * np.log10(1 + f / 700)
    def imel(m): return 700 * (10 ** (m / 2595) - 1)
    pts = imel(np.linspace(mel(50), mel(8000), NMEL + 2))
    freqs = np.fft.rfftfreq(N, 1 / RATE)
    fb = np.zeros((NMEL, N // 2 + 1))
    for i in range(NMEL):
        lo, c, hi = pts[i], pts[i + 1], pts[i + 2]
        fb[i] = np.maximum(0, np.minimum((freqs - lo) / (c - lo), (hi - freqs) / (hi - c)))
    return fb / np.maximum(fb.sum(1, keepdims=True), 1e-9)


FB = mel_bank()
WIN = np.hanning(N)


def frame_index(n):
    frames = 1 + (n - N) // HOP
    return np.arange(N)[None, :] + HOP * np.arange(frames)[:, None]


def frame_db(x):
    """10 log10 of the mean power per frame, exactly the p of pipeline.speech_gate before smoothing."""
    return 10 * np.log10((x[frame_index(len(x))] ** 2).mean(axis=1) + 1e-12)


def features(x):
    """(log-mel in dB re the 95th percentile of the frame level, shaped to int8-friendly [-4, 3]; frame level re the 95th percentile in dB)."""
    P = np.abs(np.fft.rfft(x[frame_index(len(x))] * WIN, axis=1)) ** 2
    E = frame_db(x)
    ref = np.percentile(E, 95)
    mel = 10 * np.log10(P @ FB.T + 1e-9)
    f = np.clip((mel - ref + 30.0) / 20.0, -4, 3)
    return f.astype(np.float32), (E - ref).astype(np.float32)


class Mixer:
    def __init__(self, split, seed):
        import n5
        self.n5 = n5
        self.p = n5.Pool(split, seed)
        self.rng = self.p.rng

    def mix(self, n):
        rng, p, n5 = self.rng, self.p, self.n5
        sparse = rng.random() < 0.12
        s = p.near(n)
        # long stretches where the near talker is silent (the far end talks, or nobody)
        quiet = np.zeros(n, bool)
        for _ in range(int(rng.integers(0, 4)) + (3 if sparse else 0)):
            a = int(rng.uniform(0, n)); quiet[a:a + int(rng.uniform(5, 22) * RATE)] = True
        s = np.where(quiet, 0, s).astype(np.float32)
        fr = s[:n // 256 * 256].reshape(-1, 256)
        pw = (fr ** 2).mean(1)
        act = pw > max(pw.max(), 1e-12) * 10 ** (-3.5)
        p_s = float(pw[act].mean()) if act.any() else 1e-6
        x = s.astype(np.float64)
        # far-end-like leftovers: another talker, reverberant, in random stretches, 8..45 dB under my speech
        if rng.random() < 0.75:
            f = n5.talker_stream(rng, p.speech, n).astype(np.float64)
            f = fftconvolve(f, n5.rir(rng, rng.uniform(0.2, 0.9), rng.uniform(-10, 0)))[:n]
            f *= np.sqrt(p_s / max(np.mean(f ** 2), 1e-12)) * 10 ** (-rng.uniform(8, 45) / 20)
            on = np.zeros(n, bool)
            for _ in range(int(rng.integers(1, 5))):
                a = int(rng.uniform(0, n)); on[a:a + int(rng.uniform(4, 25) * RATE)] = True
            ramp = np.convolve(on.astype(float), np.ones(800) / 800, "same")
            x = x + f * ramp
        v = rng.random()
        snr = rng.uniform(-8, 3) if v < 0.38 else rng.uniform(3, 12) if v < 0.58 else rng.uniform(12, 30) if v < 0.85 else rng.uniform(30, 55)
        z = np.concatenate([p.noise(20 * RATE) for _ in range(-(-n // (20 * RATE)))])[:n].astype(np.float64)   # Pool.noise makes 20 s at most
        if not np.mean(z ** 2) > 1e-10:
            z = p.colored(n).astype(np.float64)
        z *= np.sqrt(p_s / np.mean(z ** 2) * 10 ** (-snr / 10))
        if rng.random() < 0.3:                      # noise that changes level during the call, up to 12 dB in steps
            k = int(rng.integers(3, 8)); g = 10 ** (rng.uniform(-12, 3, size=k) / 20)
            env = np.repeat(g, n // k + 1)[:n]
            z *= np.convolve(env, np.ones(4000) / 4000, "same")
        y = x + z
        y *= 10 ** (rng.uniform(-38, -14) / 20) / max(np.sqrt(np.mean(y ** 2)), 1e-9)
        pk = np.max(np.abs(y))
        if pk > 0.98:
            y *= 0.98 / pk
        return y.astype(np.float32), snr


def main():
    wid, budget = sys.argv[1], float(sys.argv[2])
    nval = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    import s1q_lib
    d = s1q_lib.denoiser("float")                    # 1 thread, the app's path
    OUT.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    jobs = [("val", 5000 + i) for i in range(nval)]
    k = 0
    mixers = {}
    while True:
        if jobs:
            split, idx = jobs.pop(0)
        else:
            if time.time() - t0 > budget:
                break
            split, idx = "train", 1000 * (1 + ord(wid[0])) + k; k += 1
        f = OUT / f"{split}-{idx}.npz"
        if f.exists():
            continue
        if split not in mixers:
            mixers[split] = Mixer(split, 7919 * (ord(wid[0]) + 1) + (13 if split == "val" else 0))
        y, snr = mixers[split].mix(60 * RATE)
        c = s1q_lib.run(d, y)
        feat, erel = features(y)
        kept = np.clip(frame_db(c) - frame_db(y), -100, 20)
        np.savez(f.with_suffix(".tmp.npz"), f=np.round(feat * 30).astype(np.int8), e=erel.astype(np.float16),
                 k=np.round(kept).astype(np.int8), snr=np.float32(snr))
        f.with_suffix(".tmp.npz").replace(f)
        print(f"{wid} {f.name} snr {snr:.1f} open-ish {np.mean(kept > -20) * 100:.0f} % at {time.time() - t0:.0f} s", flush=True)


if __name__ == "__main__":
    main()
