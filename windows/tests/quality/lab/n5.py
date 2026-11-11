"""Track N5 (overnight 2026-11-12): a tiny mask trained for the speech engine.

A bidirectional GRU on log-mel features, level-normalized per window, with the window's own long-term
loud-frame spectrum as an extra input, predicts the ideal ratio mask with a floor of 0.3 (so it cannot
delete speech). It is applied to the solver's output `e` (N1's cache) and then goes through solve-gate's
gate and n1 setting c's region cutting.

    py -P n5.py data                          the training pool: counts, hashes (no training)
    py -P n5.py train [seconds]               train on CPU, 2 threads, checkpoint every 10 min (resumes)
    py -P n5.py check [call regex|-] [ckpt]   signal check on the 14 noisy + 6 clean calls (CPU only)
    py -P n5.py run <work dir> <tag> [ckpt]   mask, gate, cut like n1 c, transcribe (NEEDS THE ENGINE)
    py -P n5.py show <work dir> <tag>         table against su/solve-gate and n1 setting c

Training data (all under %USERPROFILE%\\Amanu Quality Set\\lab\\overnight\\N5\\data, see n5_fetch.py): FLEURS
Slovak *train* speech only (the suite uses test, the held-out set dev) and DEMAND PSTATION and OHALLWAY
(neither the suite nor the held-out set uses them), plus babble made from sums of 2 to 8 FLEURS train
talkers through diffuse reverb. Never the suite's or the held-out set's audio, never near.wav, the noise
track or truth inside the remover: at run time the mask comes from `e` alone. Truth only measures.
"""
import hashlib, io, json, os, re, shutil, sys, threading, time
HOME_ = os.path.join(os.path.expanduser("~"), "Amanu Quality Set", "lab", "overnight")
sys.path.insert(0, os.path.join(HOME_, "N5", "pylib"))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path
import numpy as np
import soundfile as sf
from scipy.signal import fftconvolve, lfilter
import torch
from torch import nn

RATE = 16000
N, HOP, NF = 512, 128, 257
NMEL = 64
FLOOR = 0.3
ASYM = 2.0                         # extra weight on a mask below its target (deleting speech); the second run uses 6
POOL = 3                           # STFT frames per model frame (24 ms)
CH = 1250                          # frames per window (10 s at 8 ms); training windows are this long
HOME = Path(HOME_)
N5 = HOME / "N5"
DATA = N5 / "data"
CKPT = N5 / "ckpt"
WIN_T = torch.sqrt(torch.hann_window(N, periodic=True))


# ---------------------------------------------------------------- features and model

def mel_bank():
    def mel(f): return 2595 * np.log10(1 + f / 700)
    def imel(m): return 700 * (10 ** (m / 2595) - 1)
    pts = imel(np.linspace(mel(50), mel(8000), NMEL + 2))
    freqs = np.fft.rfftfreq(N, 1 / RATE)
    fb = np.zeros((NMEL, NF), np.float32)
    for i in range(NMEL):
        lo, c, hi = pts[i], pts[i + 1], pts[i + 2]
        fb[i] = np.maximum(0, np.minimum((freqs - lo) / (c - lo), (hi - freqs) / (hi - c)))
    return torch.from_numpy(fb / np.maximum(fb.sum(1, keepdims=True), 1e-9) * 1.0)


FB = mel_bank()


def features(X):
    """X: complex [B, 257, T] STFT of the mixture. Returns [B, T//POOL, 2*NMEL]: log-mel (dB) minus the window's
    95th percentile of frame energy (the level normalization), pooled to 16 ms, and the window's mean over
    its loud frames (within 12 dB of that percentile) repeated on every frame."""
    P = X.real ** 2 + X.imag ** 2
    T2 = P.shape[2] // POOL
    P = (P[:, :, :POOL * T2].reshape(P.shape[0], NF, T2, POOL)).mean(3)
    E = 10 * torch.log10(P.sum(1) + 1e-9)                                  # [B, T2]
    ref = torch.quantile(E, 0.95, dim=1, keepdim=True)
    mel = 10 * torch.log10(torch.einsum("mf,bft->bmt", FB, P) + 1e-9)    # [B, NMEL, T2]
    f = ((mel - ref[:, None, :] + 30.0) / 20.0).clamp(-4, 3)
    loud = (E > ref - 12.0).float()
    ctx = (f * loud[:, None, :]).sum(2) / loud.sum(1, keepdim=True).clamp(min=1)
    return torch.cat([f, ctx[:, :, None].expand(-1, -1, T2)], 1).transpose(1, 2)


class Mask(nn.Module):
    def __init__(self, h=128, p=96):
        super().__init__()
        self.inp = nn.Sequential(nn.Linear(2 * NMEL, p), nn.ReLU())
        self.gru = nn.GRU(p, h, num_layers=2, batch_first=True, bidirectional=True)
        self.out = nn.Linear(2 * h, NF)

    def forward(self, feat, frames):
        y, _ = self.gru(self.inp(feat))
        m = FLOOR + (1 - FLOOR) * torch.sigmoid(self.out(y))               # [B, T2, 257]
        m = m.repeat_interleave(POOL, dim=1)
        if m.shape[1] < frames:
            m = torch.cat([m, m[:, -1:].expand(-1, frames - m.shape[1], -1)], 1)
        return m[:, :frames]


def nparams(m):
    return sum(p.numel() for p in m.parameters())


# ---------------------------------------------------------------- the data pool

def speech_files():
    rows = (DATA / "speech-files.tsv").read_text(encoding="utf-8").splitlines()[1:]
    return [r.split("\t") for r in rows]


def load_speech():
    """(train list, validation list) of int16 arrays. Every 20th file, by sorted name, validates."""
    rows = sorted(speech_files())
    tr, va = [], []
    for i, r in enumerate(rows):
        x, sr = sf.read(DATA / "speech" / (r[0][:-4] + ".flac"), dtype="int16")
        (va if i % 20 == 0 else tr).append(x)
    return tr, va


def load_noises():
    out = {}
    for f in sorted((DATA / "noise").glob("*.flac")):
        x, _ = sf.read(f, dtype="float32")
        k = int(len(x) * 0.8)                                               # first 80 % trains, last 20 % validates
        out[f.stem] = (x[:k], x[k:])
    return out


def rir(rng, rt60, drr_db, direct_at=0):
    n = int(rt60 * RATE * 1.0) + 1
    t = np.arange(n) / RATE
    tail = rng.standard_normal(n) * np.exp(-6.9 * t / rt60)
    tail[:int(0.004 * RATE)] = 0
    tail *= np.sqrt(10 ** (-drr_db / 10) / max(np.sum(tail ** 2), 1e-9))
    h = tail
    h[direct_at] += 1.0
    return h.astype(np.float32)


def speed(rng, x):
    """Half of the utterances are played 0.88..1.12 times as fast (linear interpolation): the pool is only 0.9 h."""
    if rng.random() < 0.5:
        return x
    f = rng.uniform(0.88, 1.12)
    t = np.arange(0, len(x) - 1, f)
    return np.interp(t, np.arange(len(x)), x).astype(np.float32)


def talker_stream(rng, pool, n):
    """One continuous talker: random utterances with short gaps, n samples."""
    out = np.zeros(n + 400000, np.float32)
    p = 0
    while p < n:
        x = speed(rng, pool[int(rng.integers(len(pool)))].astype(np.float32) / 32768)
        g =10 ** (rng.uniform(-4, 4) / 20)
        out[p:p + len(x)] = x[:len(out) - p] * g
        p += len(x) + int(rng.uniform(0.1, 1.2) * RATE)
    return out[:n]


def build_bank(pool, rng, segs, seg_s):
    """Babble: each segment sums 2 to 8 talkers, each through its own diffuse reverb (RT60 0.4..1.1 s, DRR -6..+4 dB)."""
    n = int(seg_s * RATE)
    bank = np.zeros((segs, n), np.float32)
    for i in range(segs):
        k = int(rng.integers(2, 9))
        acc = np.zeros(n)
        for _ in range(k):
            s = talker_stream(rng, pool, n)
            h = rir(rng, rng.uniform(0.4, 1.1), rng.uniform(-6, 4))
            acc += fftconvolve(s, h)[:n] * 10 ** (rng.uniform(-4, 4) / 20)
        bank[i] = (acc / max(np.sqrt(np.mean(acc ** 2)), 1e-6)).astype(np.float32)
    return bank


class Pool:
    def __init__(self, split, seed):
        tr, va = load_speech()
        self.speech = tr if split == "train" else va
        self.rng = np.random.default_rng(seed)
        self.demand = {k: (v[0] if split == "train" else v[1]) for k, v in load_noises().items()}
        t0 = time.time()
        self.bank = build_bank(self.speech, self.rng, 80 if split == "train" else 10, 20)
        print(f"[{split}] {len(self.speech)} utterances, {len(self.demand)} DEMAND channels, babble bank {self.bank.shape} in {time.time() - t0:.0f} s", flush=True)

    def near(self, n):
        rng = self.rng
        out = np.zeros(n + 600000, np.float32)
        p = int(rng.uniform(0, 1.5) * RATE)
        while p < n:
            x = speed(rng, self.speech[int(rng.integers(len(self.speech)))].astype(np.float32) / 32768)
            out[p:p + len(x)] = x[:len(out) - p]
            p += len(x) + int(rng.uniform(0.4, 3.0) * RATE)
        x = out[:n]
        if rng.random() < 0.5:                                              # the user is near: a light room, direct path dominates
            x = fftconvolve(x, rir(rng, rng.uniform(0.1, 0.35), rng.uniform(6, 20)))[:n].astype(np.float32)
        return x

    def crop(self, arr, n):
        o = int(self.rng.integers(0, len(arr) - n))
        return arr[o:o + n]

    def colored(self, n):
        rng = self.rng
        a = rng.uniform(0.0, 0.98)
        x = lfilter([1.0], [1.0, -a], rng.standard_normal(n + 2000))[2000:]
        env = 1 + 0.5 * np.sin(2 * np.pi * rng.uniform(0.1, 3) * np.arange(n) / RATE + rng.uniform(0, 6))
        return (x * env).astype(np.float32)

    def noise(self, n):
        rng = self.rng
        r = rng.random()
        keys = sorted(self.demand)
        dem = lambda: self.crop(self.demand[keys[int(rng.integers(len(keys)))]], n)
        bab = lambda: self.crop(self.bank[int(rng.integers(len(self.bank)))], n) if len(self.bank[0]) > n else \
            np.concatenate([self.bank[int(rng.integers(len(self.bank)))] for _ in range(2)])[:n]
        unit = lambda x: x / max(np.sqrt(np.mean(x.astype(np.float64) ** 2)), 1e-9)
        if r < 0.45:
            x = unit(bab())
        elif r < 0.70:
            x = unit(dem())
        elif r < 0.85:
            x = unit(bab()) + unit(dem()) * 10 ** (rng.uniform(-8, 4) / 20)
        elif r < 0.92:
            x = unit(self.colored(n))
        else:
            x = unit(bab()) + unit(self.colored(n)) * 10 ** (rng.uniform(-10, 0) / 20)
        return x.astype(np.float32)

    def mix(self, n):
        """(mixture, near speech, noise) as float32, the noise scaled to a random SNR against my active speech power."""
        rng = self.rng
        s = self.near(n)
        fr = s[:len(s) // 256 * 256].reshape(-1, 256)
        pw = (fr ** 2).mean(1)
        act = pw > pw.max() * 10 ** (-3.5)
        p_s = float(pw[act].mean()) if act.any() else 1e-6
        v = rng.random()
        snr = rng.uniform(-8, 3) if v < 0.6 else rng.uniform(3, 15) if v < 0.85 else rng.uniform(15, 35)
        z = self.noise(n)
        if not np.mean(z ** 2) > 1e-10:                                     # a digitally silent crop of a DEMAND channel
            z = self.colored(n)
        z = z *np.sqrt(p_s / np.mean(z ** 2) * 10 ** (-snr / 10))
        return (s + z).astype(np.float32), s, z


def stft_t(x):
    """x: float tensor [B, n] -> complex [B, 257, T] with zero padding of N/2, the same frames as n4.stft."""
    return torch.stft(x, N, HOP, N, WIN_T, center=True, pad_mode="constant", return_complex=True)


def batch(pool, bsz):
    n = CH * HOP - HOP
    ms, ss, zs = zip(*(pool.mix(n) for _ in range(bsz)))
    X = stft_t(torch.from_numpy(np.stack(ms)))[:, :, :CH]
    S = stft_t(torch.from_numpy(np.stack(ss)))[:, :, :CH]
    Z = stft_t(torch.from_numpy(np.stack(zs)))[:, :, :CH]
    return X, S, Z


def targets(X, S, Z):
    ps, pz = S.abs() ** 2, Z.abs() ** 2
    t = torch.sqrt(ps / (ps + pz + 1e-12)).clamp(min=FLOOR)                  # [B, 257, T]
    pm = X.abs() ** 2
    w = (pm / pm.mean(dim=(1, 2), keepdim=True).clamp(min=1e-12)).pow(0.3).clamp(0.1, 5.0)
    return t.transpose(1, 2), w.transpose(1, 2), ps, pz


def loss_fn(m, t, w):
    d = m - t
    asym = torch.where(d < 0, ASYM, 1.0)                                      # deleting speech costs twice
    return (w * asym * d ** 2).mean()


def proxy(m, ps, pz):
    """Speech level change and SNR gain (dB) of mask m [B, T, 257] over the batch, from the true parts."""
    mm = m.transpose(1, 2) ** 2
    sp = 10 * torch.log10((mm * ps).sum() / ps.sum())
    nz = 10 * torch.log10((mm * pz).sum() / pz.sum())
    return float(sp), float(sp - nz)


# ---------------------------------------------------------------- training

def train(total_s=6900.0, bsz=12, lr0=2e-3, init=None, tag=""):
    """tag != '': a fine-tune from ckpt/<init>.pt that writes ckpt/<tag>-*.pt (the one stated change of the stop rule)."""
    torch.set_num_threads(2)
    CKPT.mkdir(parents=True, exist_ok=True)
    model = Mask()
    if init:
        model.load_state_dict(torch.load(CKPT / f"{init}.pt", weights_only=False)["model"])
    opt = torch.optim.AdamW(model.parameters(), lr=lr0, weight_decay=1e-4)
    step, elapsed, best = 0, 0.0, 1e9
    last = CKPT / (tag + "latest.pt")
    if last.exists():
        ck = torch.load(last, weights_only=False)
        model.load_state_dict(ck["model"]); opt.load_state_dict(ck["opt"])
        step, elapsed, best = ck["step"], ck["elapsed"], ck["best"]
        print(f"resumed at step {step}, {elapsed:.0f} s", flush=True)
    print(f"parameters {nparams(model)}", flush=True)
    tr = Pool("train", 1000 + step)
    va = Pool("val", 7)
    vb = [batch(va, 6) for _ in range(4)]                                    # 24 fixed validation windows
    t_last_ck, t_last_val = elapsed, elapsed
    t0 = time.time() - elapsed
    ema = None

    def validate():
        model.eval()
        with torch.no_grad():
            L, sp, sg = [], [], []
            for X, S, Z in vb:
                t, w, ps, pz = targets(X, S, Z)
                m = model(features(X), X.shape[2])
                L.append(float(loss_fn(m, t, w)))
                a, b = proxy(m, ps, pz); sp.append(a); sg.append(b)
        model.train()
        return float(np.mean(L)), float(np.mean(sp)), float(np.mean(sg))

    while True:
        elapsed = time.time() - t0
        if elapsed >= total_s:
            break
        frac = elapsed / total_s
        lr = lr0 * min(1.0, (step + 1) / (20 if init else 100)) * (0.03 + 0.97 * 0.5 * (1 + np.cos(np.pi * frac)))
        for g in opt.param_groups:
            g["lr"] = lr
        X, S, Z = batch(tr, bsz)
        t, w, ps, pz = targets(X, S, Z)
        m = model(features(X), X.shape[2])
        loss = loss_fn(m, t, w)
        opt.zero_grad()
        loss.backward()
        nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        opt.step()
        step += 1
        ema = float(loss) if ema is None else 0.98 * ema + 0.02 * float(loss)
        if step % 50 == 0:
            print(f"step {step} t {elapsed:6.0f} s lr {lr:.2e} loss {ema:.5f}", flush=True)
        if elapsed - t_last_val >= 300:
            t_last_val = elapsed
            vl, sp, sg = validate()
            print(f"  VAL step {step} loss {vl:.5f} speech {sp:+.2f} dB snr gain {sg:+.2f} dB", flush=True)
            if vl < best:
                best = vl
                torch.save({"model": model.state_dict()}, CKPT / (tag + "best.pt"))
        if elapsed - t_last_ck >= 600:
            t_last_ck = elapsed
            torch.save({"model": model.state_dict(), "opt": opt.state_dict(), "step": step, "elapsed": elapsed, "best": best}, last)
            print(f"  checkpoint step {step}", flush=True)
    vl, sp, sg = validate()
    print(f"FINAL step {step} t {elapsed:.0f} s loss {vl:.5f} speech {sp:+.2f} dB snr gain {sg:+.2f} dB", flush=True)
    torch.save({"model": model.state_dict(), "opt": opt.state_dict(), "step": step, "elapsed": elapsed, "best": best}, last)
    torch.save({"model": model.state_dict()}, CKPT / (tag + "final.pt"))


# ---------------------------------------------------------------- applying the mask to a call

_model = {}


def load_model(name="final"):
    if name not in _model:
        torch.set_num_threads(2)
        m = Mask()
        m.load_state_dict(torch.load(CKPT / f"{name}.pt", weights_only=False)["model"])
        m.eval()
        _model[name] = m
    return _model[name]


def mask_of(e, ckpt="final"):
    """The mask [frames, 257] for signal e (n4.stft framing), from e alone: windows of 10 s, hop 8 s, cross-faded."""
    import n4
    model = load_model(ckpt)
    X = n4.stft(e)
    T = X.shape[0]
    Xt = torch.from_numpy(np.ascontiguousarray(X.T)).to(torch.complex64)
    out = np.zeros((T, NF), np.float32)
    wsum = np.zeros(T, np.float32)
    starts = list(range(0, max(T - CH, 0) + 1, 1000))
    if starts[-1] + CH < T:
        starts.append(T - CH)
    with torch.no_grad():
        for s in starts:
            seg = Xt[:, s:s + CH][None]
            m = model(features(seg), seg.shape[2])[0].numpy()
            L = m.shape[0]
            w = np.ones(L, np.float32)
            ramp = np.sin(np.linspace(0, np.pi / 2, 250, dtype=np.float32)) ** 2
            if s > 0:
                w[:250] = ramp[:min(250, L)]
            if s + CH < T:
                w[-250:] = np.minimum(w[-250:], ramp[::-1][:min(250, L)])
            out[s:s + L] += m * w[:, None]
            wsum[s:s + L] += w
    return out / np.maximum(wsum, 1e-6)[:, None]


def filtered(e, ckpt="final"):
    import n4
    return n4.apply(e, mask_of(e, ckpt))


# ---------------------------------------------------------------- the signal check

def check(pattern=None, ckpt="final", ref=True):
    import n4, n2, lab
    print("sp = speech level change over my speech frames (dB); snr = SNR gain inside the setting-c regions (dB); n = residual change in the regions (dB)")
    print("dp = DPDFNet baseline raw output on the same solver output (projection on near): speech dB / SNR gain dB")
    agg = {}
    for nm in n4.noisy() + n4.clean():
        if pattern and not re.search(pattern, nm):
            continue
        sc = lab.load_recording(nm)
        e, g = n4.pieces(nm)
        n = len(e)
        near = sc["near"][:n]
        m = n4.frame_mask(near, n)
        a = float(np.dot(e[m], near[m]) / np.dot(near[m], near[m]))
        s = a * near
        r = e - s
        R = np.zeros(n, bool)
        for a0, b0 in n4.regions_c(g, e):
            R[a0:b0] = True
        snr = lambda ss, rr, mm: 10 * np.log10((ss[mm] ** 2).sum() / max((rr[mm] ** 2).sum(), 1e-20))
        M = mask_of(e, ckpt)
        fs, fr = n4.apply(s, M), n4.apply(r, M)
        sp = 10 * np.log10((fs[m] ** 2).sum() / (s[m] ** 2).sum())
        sg = snr(fs, fr, R) - snr(s, r, R)
        nn_ = 10 * np.log10((fr[R] ** 2).sum() / (r[R] ** 2).sum())
        dp = ""
        if ref:
            f = n2.HOME / "cache" / f"{n2._key(sc['mic'], sc['far'])}.npz"
            if f.exists():
                c = np.load(f)["c"].astype(np.float64)[:n]
                b = float(np.dot(c[m], near[m]) / np.dot(near[m], near[m]))
                sh = b * near
                rh = c - sh
                dp = f"dp {10 * np.log10(b * b * np.dot(near[m], near[m]) / (a * a * np.dot(near[m], near[m]))):+5.2f} / {snr(sh, rh, R) - snr(s, r, R):+5.2f}"
        print(f"{nm[5:]:22} snr in {snr(s, r, R):5.1f} | sp {sp:+5.2f} snr {sg:+5.2f} n {nn_:+5.1f} | {dp}", flush=True)
        key = nm.split("-")[1] + (" -5" if nm.endswith("-5") else " 0" if nm.endswith("+0") else " clean")
        agg.setdefault(key, []).append((sp, sg, nn_))
    print("\nmean per noise and SNR (speech dB, SNR gain dB, noise dB, worst speech dB):")
    for key in sorted(agg):
        v = np.array(agg[key])
        print(f"{key:12} {v[:, 0].mean():+5.2f} {v[:, 1].mean():+5.2f} {v[:, 2].mean():+5.1f}  min {v[:, 0].min():+5.2f}  (n={len(v)})")


# ---------------------------------------------------------------- the engine part (not run in step 1)

def run(work, tag, ckpt="final"):
    import asr, n1, n4, lab
    from concurrent.futures import ThreadPoolExecutor

    def one(name):
        row = asr.cached(work, f"n5/{tag}", name)
        if row is not None:
            return row
        e, g = n4.pieces(name)
        x = g * filtered(e, ckpt)                            # mask, then solve-gate's gate
        regs = n1.regions(g, g * e, **n1.SETTINGS["c"])       # the regions are solve-gate's, as in n1 c
        tmp = Path(work) / "tmp" / f"n5-{tag}-{name}"
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
        f = Path(work) / "wer2" / "n5" / tag / f"{name}.json"
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(json.dumps(row, ensure_ascii=False), encoding="utf-8")
        shutil.rmtree(tmp)
        return row
    names = n4.noisy() + n4.clean()
    for nm in names:                                        # the masks first, one thread, so the engine slots are not starved
        pass
    with ThreadPoolExecutor(3) as pool:
        list(pool.map(one, names))


def show(work, tag):
    import asr, n1, n4
    ns = n4.noisy() + n4.clean()
    base = {k: r for k, r in asr.rows(n1.LAB, "su/solve-gate").items() if k in ns}
    rs = asr.rows(work, f"n5/{tag}")
    groups = [("0", lambda r: r["snr"] == 0), ("-5", lambda r: r["snr"] == -5),
              ("14 noisy", lambda r: r["snr"] in (0, -5)), ("no noise", lambda r: r["snr"] is None)]
    print(f"n5/{tag} n={len(rs)} WER " + " ".join(f"{a} {asr.wer(r for r in rs.values() if f(r)):.1f}" for a, f in groups)
          + f" | echo words {sum(r['leak'] for r in rs.values())}")
    c = {k: r for k, r in asr.rows(HOME / "N1", "n1/c").items() if k in rs}
    for label, ref in (("solve-gate", base), ("n1 setting c", c)):
        ref = {k: v for k, v in ref.items() if k in rs}
        if ref:
            d, lo, hi = asr.bootstrap(rs, ref)
            print(f"  against {label}: {d:+.1f} [{lo:+.1f}, {hi:+.1f}] (reference " +
                  " ".join(f"{a} {asr.wer(r for r in ref.values() if f(r)):.1f}" for a, f in groups) + ")")


def data_report():
    rows = speech_files()
    print(f"speech: {len(rows)} FLEURS train files, {sum(float(r[3]) for r in rows) / 3600:.2f} h; validation = every 20th by sorted name")
    h = hashlib.sha256("".join(sorted(r[0] + r[4] for r in rows)).encode()).hexdigest()
    print("sha256 over file names + flac hashes:", h)
    nr = [l.split("\t") for l in (DATA / "noise-files.tsv").read_text(encoding="utf-8").splitlines()[1:]]
    print("noise:", ", ".join(f"{r[0]} {r[2]} s" for r in nr))
    m = Mask()
    print("parameters", nparams(m))


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "data":
        data_report()
    elif cmd == "train":
        if len(sys.argv) > 3:                                   # train <seconds> <asym> : fine-tune final.pt as "a6-"
            ASYM = float(sys.argv[3])
            train(float(sys.argv[2]), lr0=5e-4, init="final", tag="a6-")
        else:
            train(float(sys.argv[2]) if len(sys.argv) > 2 else 6900.0)
    elif cmd == "check":
        check(sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != "-" else None, sys.argv[3] if len(sys.argv) > 3 else "final")
    elif cmd == "run":
        run(sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else "final"); show(sys.argv[2], sys.argv[3])
    elif cmd == "show":
        show(sys.argv[2], sys.argv[3])
