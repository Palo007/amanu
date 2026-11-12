"""N1: take the segmentation away from the engine (overnight 2026-11-12, track N1).

transcribe-cli has no voice-activity detector and no gain normalization, and the app cuts
the track into fixed 60 s chunks. Here the engine is fed one WAV per speech region of
solve-gate's output instead: the gate's open regions, merged across short gaps, padded,
capped in length and scaled to one RMS, through `transcribe-cli --batch`, with the texts
joined in time order. Scored like sw.py's "my WER" (Q.error_counts on the truth's "me").

Only the mic and the far track go into the signal path (solve, DPDFNet gate, regions).
The truth is read for scoring, nothing else; there are no per-call settings.

Settings (fixed, three):
    a   regions cut out; merge gaps under 0.6 s, pad 0.3 s, cap 25 s, one RMS (-20 dBFS)
    b   the same regions (a's) stay in one file, the gaps replaced by comfort noise at
        -60 dBFS, no rescaling, and the app's normal path transcribes it (Amanu.exe)
    c   as a, with merge gap 1.5 s and pad 1.0 s

    py -P n1.py <work dir> cache [regex]        solve-gate pieces per call (CPU, no engine)
    py -P n1.py <work dir> check [regex]        regions per setting a and c, share of gate speech kept
    py -P n1.py <work dir> plumb <call> [a|c]   ONE transcription of one call, printed with its WER
    py -P n1.py <work dir> run:<a,b,c> [regex]  screening (engine!), rows under <work>/wer2/n1/<setting>
    py -P n1.py <work dir> show:<a,b,c>         (N1_SET=ho: held-out set, work dir N1/ho)  table against the cached su/solve-gate rows

<work dir> is %USERPROFILE%\\Amanu Quality Set\\lab\\overnight\\N1. The baseline rows are read
from the lab's own folder (%USERPROFILE%\\Amanu Quality Set\\lab, tag su/solve-gate), never run.
"""
import json, os, re, shutil, subprocess, sys, time
from pathlib import Path
import numpy as np
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lab, asr, pipeline, denoise, variants

Q = lab.Q
RATE = Q.RATE
LAB = Path.home() / "Amanu Quality Set" / "lab"
CLI = Path(__file__).resolve().parents[3] / "artifacts" / "publish" / "local-runtime" / "transcribe-cli.exe"
DATA = Path(os.environ.get("LOCALAPPDATA", "")) / "Amanu Data"
TARGET_DBFS = -20.0                      # the app's own SpeechLevels target
COMFORT_DBFS = -60.0
SETTINGS = {
    "a": dict(merge_s=0.6, pad_s=0.3, cap_s=25.0),
    "c": dict(merge_s=1.5, pad_s=1.0, cap_s=25.0),
}
SETTINGS["d"] = SETTINGS["c"]            # c's regions in ONE file, 0.3 s of digital silence between them
SETTINGS["e"] = dict(merge_s=2.5, pad_s=1.5, cap_s=25.0)
SETTINGS["b"] = SETTINGS["a"]            # b uses a's regions, only the packaging differs


def model_file():
    """The model the app's "auto" engine uses on this PC: config.json's local_engine."""
    names = {"parakeet": "parakeet-tdt-0.6b-v3-Q8_0.gguf", "parakeet-ultra": "parakeet-ultra-Q8_0.gguf",
             "parakeet-sk": "parakeet-tdt-0.6b-v3-sk-Q8_0.gguf"}
    try:
        engine = json.loads((DATA / "config.json").read_text(encoding="utf-8-sig"))["transcription"]["local_engine"]
    except Exception:
        engine = "parakeet"
    return os.environ.get("AMANU_N1_MODEL") or str(DATA / "models" / names.get(engine, names["parakeet"]))


# ---------------------------------------------------------------- the gate, cached per call

def call_names(regex=None):
    names = sorted(p.name for p in REC.iterdir() if HELDOUT or p.name.startswith("echo-"))
    return [n for n in names if re.search(regex or "", n)]


def screen_names(regex=None, snrs=(None, 0, -5)):
    if HELDOUT:
        return call_names(regex)
    """The calls to screen: SNR 0 and -5 (14 calls) and the calls without noise (6)."""
    return [n for n in call_names(regex)
            if json.loads((REC / n / "truth.json").read_text(encoding="utf-8")).get("snr") in snrs]


HELDOUT = os.environ.get("N1_SET") == "ho"
if HELDOUT:
    import heldout
REC = heldout.RECORDINGS if HELDOUT else Q.RECORDINGS


def gate_pieces(work, name):
    """(e, g): solve's output and the speech gate's gain for it, both from mic and far only.
    solve-gate's output is g * e (pipeline.gated(solve))."""
    f = Path(work) / "cache" / f"{name}.npz"
    if f.exists() and not HELDOUT:
        z = np.load(f)
        return z["e"].astype(np.float64), z["g"].astype(np.float64)
    f.parent.mkdir(parents=True, exist_ok=True)
    folder = REC / name
    mic, far = Q.read_mono(folder / "mic.wav"), Q.read_mono(folder / "system.wav")
    e = pipeline.VARIANTS["solve"](mic, far)
    g = pipeline.speech_gate(e, denoise.dpdf(e))
    if HELDOUT:                      # 1.2 GB for 52 calls: recompute on a restart, rows are cached
        return e, g
    np.savez(f.with_suffix(".tmp.npz"), e=e.astype(np.float32), g=g.astype(np.float32))
    f.with_suffix(".tmp.npz").replace(f)
    return e, g


# ---------------------------------------------------------------- regions

def open_runs(g, level=0.5):
    """[start, end) sample runs where the gate is open (gain above `level`)."""
    o = np.concatenate([[0], (g > level).astype(np.int8), [0]])
    d = np.flatnonzero(np.diff(o))
    return list(zip(d[::2].tolist(), d[1::2].tolist()))


def merge(runs, gap):
    out = []
    for a, b in runs:
        if out and a - out[-1][1] < gap:
            out[-1] = (out[-1][0], b)
        else:
            out.append((a, b))
    return out


def split_long(a, b, x, cap):
    """Cut [a, b) into pieces of at most `cap` samples, each cut at the quietest 100 ms of the
    last 40 % of what the piece may still take."""
    out = []
    w = RATE // 10
    while b - a > cap:
        lo, hi = a + int(cap * 0.6), a + cap
        seg = x[lo:hi + w]
        n = (len(seg) - w) // (w // 2)
        p = [float(np.mean(seg[i * (w // 2): i * (w // 2) + w] ** 2)) for i in range(max(n, 1))]
        cut = lo + (int(np.argmin(p)) * (w // 2) + w // 2)
        out.append((a, cut))
        a = cut
    out.append((a, b))
    return out


def regions(g, x, merge_s, pad_s, cap_s):
    """Speech regions of the gate `g` (samples): open runs merged across gaps under merge_s,
    padded by pad_s, merged again where the padding touches, split at cap_s."""
    n = len(g)
    runs = merge(open_runs(g), int(merge_s * RATE))
    p = int(pad_s * RATE)
    runs = merge([(max(0, a - p), min(n, b + p)) for a, b in runs], 1)
    out = []
    for a, b in runs:
        out += split_long(a, b, x, int(cap_s * RATE))
    return out


def scaled(x, g, a, b, target_dbfs=TARGET_DBFS):
    """x[a:b] scaled so that the RMS over its open samples is target_dbfs (peak kept under 0.97)."""
    seg = x[a:b]
    open_ = g[a:b] > 0.5
    ref = seg[open_] if open_.sum() > RATE // 20 else seg
    rms = float(np.sqrt(np.mean(ref ** 2)) + 1e-12)
    gain = min(10 ** (target_dbfs / 20) / rms, 10 ** (30 / 20))
    peak = float(np.max(np.abs(seg)) * gain)
    if peak > 0.97:
        gain *= 0.97 / peak
    return seg * gain


def comfort_noise(n, rng, dbfs=COMFORT_DBFS):
    """Pink-ish noise (1/f by an FFT shape) at `dbfs` RMS."""
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.maximum(np.arange(len(spec)), 1)
    y = np.fft.irfft(spec / np.sqrt(f), n)
    return y * 10 ** (dbfs / 20) / (np.sqrt(np.mean(y ** 2)) + 1e-20)


def with_comfort_noise(x, regs, seed=1):
    """x kept inside `regs`; the gaps between them replaced by comfort noise at -60 dBFS,
    30 ms linear crossfades at the edges. The signal inside the regions is untouched."""
    out = comfort_noise(len(x), np.random.default_rng(seed))
    keep = np.zeros(len(x))
    for a, b in regs:
        keep[a:b] = 1.0
    r = int(0.03 * RATE)
    keep = np.convolve(np.pad(keep, r // 2, mode="edge"), np.ones(r) / r, "valid")[:len(x)]
    return keep * x + (1 - keep) * out


# ---------------------------------------------------------------- the engine

_cli_lock = None


def run_batch(wavs, workdir, threads=None):
    """transcribe-cli --batch over `wavs` in order; returns ([text per wav], seconds).
    Same model, backend and thread share as the app's track (4 of the 8 threads: the app runs
    mic and system side by side) and --timestamps auto as the app's batch form."""
    workdir = Path(workdir)
    lst = workdir / "batch.txt"
    lst.write_text("\n".join(str(w) for w in wavs) + "\n", encoding="utf-8")
    backend = os.environ.get("AMANU_ASR_BACKEND") or ("vulkan" if (CLI.parent / "ggml-vulkan.dll").exists() else "cpu")
    cmd = [str(CLI), "--backend", backend, "-m", model_file(), "--threads", str(threads or os.environ.get("N1_THREADS", 4)),
           "--batch", str(lst), "--batch-jsonl", "--timestamps", "auto"]
    t0 = time.time()
    with asr._gpu:
        p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    secs = time.time() - t0
    if p.returncode != 0:
        raise RuntimeError(f"transcribe-cli failed ({p.returncode}): {p.stderr[-500:]}")
    texts = {}
    for raw in p.stdout.splitlines():
        line = raw.strip()
        m = re.search(r'"file"\s*:\s*"([^"]*)"', line)
        if not line.startswith("{") or not m:
            continue                                    # the batch header
        file = re.split(r"[\\/]", m.group(1))[-1]
        texts[file] = (json.loads(line.replace(m.group(0), '"file":""')).get("text") or "").strip()
    missing = [w.name for w in map(Path, wavs) if w.name not in texts]
    if missing:
        raise RuntimeError(f"no answer for {len(missing)} of {len(wavs)} files: {missing[:3]}")
    return [texts[Path(w).name] for w in wavs], secs


def score_row(name, hyp, secs, extra=None):
    truth = json.loads((REC / name / "truth.json").read_text(encoding="utf-8"))
    me = Q.error_counts(truth["me"], hyp)
    far_only = set(Q.normalize_text(truth["them"])) - set(Q.normalize_text(truth["me"]))
    return {"errors": me["word_errors"], "words": me["ref_words"], "sub": me["sub"], "del": me["del"], "ins": me["ins"],
            "leak": sum(1 for w in me["inserted"] if w in far_only), "secs": secs, "hyp": hyp,
            **{k: truth.get(k) for k in ("noise", "room", "ser", "snr")}, **(extra or {})}


def save_row(work, setting, name, row):
    f = Path(work) / "wer2" / "n1" / setting / f"{name}.json"
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_text(json.dumps(row, ensure_ascii=False), encoding="utf-8")


def transcribe_regions(work, setting, name):
    """Setting a or c for one call: cached row, else regions -> WAVs -> batch -> text -> score."""
    row = asr.cached(work, f"n1/{setting}", name)
    if row is not None:
        return row
    e, g = gate_pieces(work, name)
    x = g * e
    regs = regions(g, x, **SETTINGS[setting])
    tmp = Path(work) / "tmp" / f"{setting}-{name}"
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir(parents=True)
    wavs = []
    if setting == "d":
        gap = np.zeros(int(0.3 * RATE))
        joined = np.concatenate([np.concatenate([scaled(x, g, a, b), gap]) for a, b in regs])
        regs_for_files = [(0, len(joined))]
        Q.write_wav(tmp / "r000.wav", joined)
        wavs.append(tmp / "r000.wav")
    for i, (a, b) in enumerate([] if setting == "d" else regs):
        p = tmp / f"r{i:03d}.wav"
        Q.write_wav(p, scaled(x, g, a, b))
        wavs.append(p)
    if not wavs:
        texts, secs = [], 0.0
    else:
        texts, secs = run_batch(wavs, tmp)
    hyp = " ".join(t for t in texts if t)
    row = score_row(name, hyp, secs, {"regions": len(regs), "region_s": sum(b - a for a, b in regs) / RATE})
    save_row(work, f"{setting}", name, row)
    shutil.rmtree(tmp)
    return row


def transcribe_comfort(work, name):
    """Setting b: one file through the app's normal path, gaps filled with comfort noise."""
    e, g = gate_pieces(work, name)
    x = g * e
    regs = regions(g, x, **SETTINGS["b"])
    audio = with_comfort_noise(x, regs)
    return asr.transcribe(work, "n1/b", name, audio, secs=0.0)


# ---------------------------------------------------------------- commands

def check(work, regex):
    """Signal check, no engine: regions per setting and the share of the gate's speech kept."""
    print(f"{'call':26s} {'snr':>5s} {'open s':>7s} | " + " | ".join(f"{s}: n  mean s  max s  total s  kept %" for s in "ac"))
    tot = {s: [0, 0.0, 0.0] for s in "ac"}
    for name in screen_names(regex, (0, -5)):
        truth = json.loads((REC / name / "truth.json").read_text(encoding="utf-8"))
        e, g = gate_pieces(work, name)
        x = g * e
        op = g > 0.5
        cells = []
        for s in "ac":
            regs = regions(g, x, **SETTINGS[s])
            cov = np.zeros(len(g), bool)
            for a, b in regs:
                cov[a:b] = True
            kept = 100 * (op & cov).sum() / max(1, op.sum())
            lens = [(b - a) / RATE for a, b in regs]
            cells.append(f"{s}: {len(regs):2d} {np.mean(lens):6.1f} {max(lens):6.1f} {sum(lens):7.1f} {kept:6.1f}")
            tot[s][0] += len(regs); tot[s][1] += sum(lens); tot[s][2] += len(g) / RATE
        print(f"{name:26s} {str(truth.get('snr')):>5s} {op.sum() / RATE:7.1f} | " + " | ".join(cells), flush=True)
    for s in "ac":
        print(f"setting {s}: {tot[s][0]} regions, {tot[s][1]:.0f} s of {tot[s][2]:.0f} s sent to the engine ({100 * tot[s][1] / tot[s][2]:.0f} %)")


def show(work, settings):
    base = asr.rows(LAB, "ho/solve-gate" if HELDOUT else "su/solve-gate")
    groups = [("0", lambda r: r["snr"] == 0), ("-5", lambda r: r["snr"] == -5),
              ("14 noisy", lambda r: r["snr"] in (0, -5)), ("no noise", lambda r: r["snr"] is None)]
    for s in settings:
        rs = asr.rows(work, f"n1/{s}")
        if not rs:
            print(f"{s}: no rows"); continue
        b = {k: v for k, v in base.items() if k in rs}
        line = f"n1/{s:3s} n={len(rs):2d} WER {asr.wer(rs.values()):5.1f} (solve-gate on the same calls {asr.wer(b.values()):5.1f}) | "
        line += " ".join(f"{g} {asr.wer(r for r in rs.values() if f(r)):5.1f}/{asr.wer(r for r in b.values() if f(r)):5.1f}" for g, f in groups)
        d, lo, hi = asr.bootstrap(rs, b)
        print(line + f" | against solve-gate: {d:+.1f} [{lo:+.1f}, {hi:+.1f}] | engine {sum(r['secs'] for r in rs.values()):.0f} s")


def main():
    work, cmd = sys.argv[1], sys.argv[2]
    rest = sys.argv[3:]
    kind, _, what = cmd.partition(":")
    if kind == "cache":
        t0 = time.time()
        for n in screen_names(rest[0] if rest else None):
            gate_pieces(work, n)
            print(f"{n} cached ({time.time() - t0:.0f} s)", flush=True)
    elif kind == "check":
        check(work, rest[0] if rest else None)
    elif kind == "plumb":
        name, setting = rest[0], (rest[1] if len(rest) > 1 else "a")
        row = transcribe_regions(work, setting, name)
        print(json.dumps({k: v for k, v in row.items() if k != "hyp"}))
        print("WER %.1f" % (100 * row["errors"] / row["words"]), "| solve-gate cached:",
              (lambda r: "%.1f" % (100 * r["errors"] / r["words"]))(asr.cached(LAB, "su/solve-gate", name)))
        print("hyp:", row["hyp"][:400])
    elif kind == "run":
        regex = rest[0] if rest else None
        from concurrent.futures import ThreadPoolExecutor
        names = screen_names(regex)
        for s in what.split(","):
            fn = (lambda n: transcribe_comfort(work, n)) if s == "b" else (lambda n, s=s: transcribe_regions(work, s, n))
            with ThreadPoolExecutor(1 if s == 'b' else 3) as pool:   # b starts 2 processes per call (mic, system)
                list(pool.map(fn, names))
            show(work, [s])
    elif kind == "show":
        show(work, what.split(","))


if __name__ == "__main__":
    main()
