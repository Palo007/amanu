"""The stress set: 12 new calls for three cases the held-out set has too few of. Report only.

    py -P stress.py plan       # writes <stress>/manifest.json (never overwrites)
    py -P stress.py build      # writes the 12 recordings from the manifest (CPU)
    py -P stress.py cpu        # solver + gate per call, cached; CPU numbers (no engine)
    py -P stress.py engine     # THE ENGINE STEP: solve-gate (Amanu.exe) and setting c (transcribe-cli) on the 12 calls, then the table
    py -P stress.py show       # the table from the cached rows

Groups (4 calls each, 2 to 3 minutes):
    drift   the call track runs against the mic's clock at a ppm that changes during the call
            (slow sine, steps, a bounded random walk; all within +-40 ppm of 0 or 20)
    dev4    four devices: the echo path (room kind, reverberation, latency) changes three times,
            in the pauses between blocks nearest a quarter, a half and three quarters of the call
    one     the near talker (me) says one sentence of about 4 s in a call of 180 s (1 to 2 % of the
            time); the rest is the call track's echo and noise

Material: FLEURS Slovak *test* sentences that none of the suite's 40 echo calls uses (the
tuning set) and that are not in the held-out set (which is FLEURS dev); noise from the
held-out set's four DEMAND rooms (not the suite's). The mixer is heldout.py's (lay_out,
response, load_noise, PATTERNS); only the drift curve, the three device changes and the
one-sentence layout are new. Nothing here may be used to choose a setting.
"""
import json, math, os, sys, time
from pathlib import Path
import numpy as np
import soundfile as sf
from scipy import signal
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lab, heldout

Q = lab.Q
RATE = Q.RATE
ROOT = Q.ROOT / "lab" / "overnight" / "G" / "stress"
REC, WORK = ROOT / "recordings", ROOT / "work"
MANIFEST = ROOT / "manifest.json"
SEED = 20261112
GROUPS = ("drift", "dev4", "one")


# ---------------------------------------------------------------- plan

def free_rows():
    """FLEURS test rows the suite's 40 echo calls do not use, 4 to 14 s long."""
    blob = ""
    for p in Q.RECORDINGS.iterdir():
        if p.name.startswith("echo-") and (p / "truth.json").exists():
            t = json.loads((p / "truth.json").read_text(encoding="utf-8"))
            blob += " " + t.get("me", "") + " " + t.get("them", "")
    rows = [r for r in Q.load_fleurs() if r["text"] not in blob]
    for r in rows:
        r["seconds"] = sf.info(r["path"]).duration
    return sorted((r for r in rows if 4 <= r["seconds"] <= 14), key=lambda r: r["file"])


def speech_seconds(x, below_db=25.0, close_s=0.3):
    """Seconds of speech in a clean track: 20 ms frames within `below_db` of the loudest,
    gaps under `close_s` closed."""
    f = RATE // 50
    n = len(x) // f
    p = (x[:n * f].reshape(n, f) ** 2).mean(axis=1)
    on = p >= p.max() * 10 ** (-below_db / 10)
    edges = np.flatnonzero(np.diff(np.concatenate([[0], on.astype(int), [0]])))
    for a, b in zip(edges[1:-1:2], edges[2:-1:2]):       # gaps between runs
        if (b - a) * 0.02 < close_s:
            on[a:b] = True
    return float(on.sum() * 0.02)


def plan():
    rows = free_rows()
    rng = np.random.default_rng(SEED)
    used = np.zeros(len(rows))

    def pick(n_near, n_far, max_s=11.0, near_pool=None):
        order = np.lexsort((rng.random(len(rows)), used))
        near, far, seen = [], [], set()
        for k in order:
            r = rows[k]
            if r["sentence"] in seen or r["seconds"] > max_s:
                continue
            if near_pool is not None and len(near) < n_near:
                if k not in near_pool:
                    continue
            if len(near) < n_near:
                near.append(int(k))
            elif len(far) < n_far:
                far.append(int(k))
            else:
                break
            seen.add(r["sentence"]); used[k] += 1
        return [rows[k]["file"] for k in near], [rows[k]["file"] for k in far]

    def room(kind, latency=None):
        lat = latency or (int(rng.integers(40, 221)) if kind == "rev" else int(rng.integers(20, 161)))
        return {"kind": kind, "rt60": round(float(rng.uniform(0.25, 0.6) if kind == "rev" else rng.uniform(0.2, 0.5)), 3),
                "latency_ms": lat, "seed": int(rng.integers(1000, 10 ** 6))}

    noises = list(heldout.NOISES)
    calls = []
    # wandering drift: four curves, one room each
    curves = [{"kind": "sine", "base": 0, "amp": 40, "period_s": 70.0, "phase": 0.0},
              {"kind": "sine", "base": 20, "amp": 40, "period_s": 45.0, "phase": 1.3},
              {"kind": "steps", "levels": [40, -40, 15, -30, 40], "step_s": 28.0},
              {"kind": "walk", "peak": 40, "smooth_s": 12.0, "seed": int(rng.integers(0, 10 ** 6))}]
    for i, (c, kind, ser, snr) in enumerate(zip(curves, ["rev", "direct", "mid", "rev"], [-5, -10, 0, -10], [20, None, 10, 5])):
        near, far = pick(10, 8)
        calls.append({"name": f"st-drift-{i}", "group": "drift", "pattern": i, "near": near, "far": far, "rooms": [room(kind)],
                      "ser": ser, "snr": snr, "noise": noises[i], "drive": 1.5, "drift": c, "noise_seed": int(rng.integers(0, 10 ** 6))})
    # four devices: three changes
    kinds4 = [["rev", "direct", "mid", "rev"], ["direct", "rev", "rev", "mid"], ["mid", "rev", "direct", "direct"], ["rev", "mid", "rev", "direct"]]
    for i, (ks, ser, snr) in enumerate(zip(kinds4, [-5, 0, -10, -5], [10, 20, None, 0])):
        near, far = pick(10, 8)
        calls.append({"name": f"st-dev-{i}", "group": "dev4", "pattern": (i + 1) % 4, "near": near, "far": far, "rooms": [room(k, int(l)) for k, l in zip(ks, rng.permutation([35, 80, 130, 190]))],
                      "ser": ser, "snr": snr, "noise": noises[(i + 1) % 4], "drive": 1.5, "drift": None, "noise_seed": int(rng.integers(0, 10 ** 6))})
    # one sentence of mine: the four shortest-speech sentences are the pool
    short = sorted(range(len(rows)), key=lambda k: speech_seconds(Q.read_mono(rows[k]["path"])))[:24]
    for i, (ser, snr, double) in enumerate(zip([-5, -10, 0, -10], [5, 0, 10, 0], [True, False, True, False])):
        near, far = pick(1, 24, max_s=14.0, near_pool=set(short))
        calls.append({"name": f"st-one-{i}", "group": "one", "pattern": None, "near": near, "far": far, "rooms": [room(["rev", "mid", "direct", "rev"][i])],
                      "ser": ser, "snr": snr, "noise": noises[(i + 2) % 4], "drive": 1.5, "drift": None, "length_s": 180, "near_at_s": [52, 96, 128, 70][i],
                      "near_in_far_pause": not double, "noise_seed": int(rng.integers(0, 10 ** 6))})
    return calls


# ---------------------------------------------------------------- the mixer (heldout's, plus three new pieces)

def smooth(x, k):
    """Moving average over k samples, edge-padded, in O(n) (np.convolve with a 12 s kernel takes minutes)."""
    from scipy.ndimage import uniform_filter1d
    return uniform_filter1d(x, size=k, mode="nearest")


def ppm_curve(c, n, seed=0):
    t = np.arange(n) / RATE
    if c["kind"] == "sine":
        p = c["base"] + c["amp"] * np.sin(2 * np.pi * t / c["period_s"] + c["phase"])
    elif c["kind"] == "steps":
        p = np.array(c["levels"], float)[np.minimum((t // c["step_s"]).astype(int), len(c["levels"]) - 1)]
    else:
        rng = np.random.default_rng(c["seed"])
        k = int(c["smooth_s"] * RATE)
        w = np.cumsum(rng.standard_normal(n // 1000 + 2))              # one step per 1000 samples
        w = np.interp(np.arange(n), np.arange(len(w)) * 1000, w)
        w = smooth(w, k)
        p = c["peak"] * w / np.max(np.abs(w))
    ramp = RATE * 2                                                    # steps are ramped over 2 s, as a clock would slew
    return smooth(p, ramp)


def drifted_curve(x, ppm, taps=32):
    """heldout.drifted with a drift that changes: y[n] = x(pos[n]), pos[n] = n + sum(ppm) * 1e-6, windowed-sinc."""
    pos_all = np.arange(len(x)) + np.cumsum(ppm) * 1e-6
    y = np.zeros(len(x))
    k = np.arange(-taps // 2 + 1, taps // 2 + 1)
    xp = np.pad(x, (taps, taps))
    for s in range(0, len(x), 1 << 18):
        pos = pos_all[s:s + (1 << 18)]
        i = np.floor(pos).astype(np.int64); f = pos - i
        ok = (i >= 0) & (i < len(x))
        i, f = i[ok], f[ok]
        arg = k[None, :] - f[:, None]
        w = np.sinc(arg) * (0.5 + 0.5 * np.cos(np.pi * arg / (taps / 2 + 1)))
        y[s:s + len(i)] = np.einsum("ij,ij->i", xp[i[:, None] + k[None, :] + taps], w)
    return y


def lay_one(spec, audio):
    """A call of `length_s` seconds: the far track says sentences one after the other (a quarter
    of a second between them; 8 s of pause around my sentence when it is placed in a pause) and I say
    my one sentence at near_at_s. Returns near, far, regions."""
    level = lambda a: a * 10 ** (-26 / 20) / math.sqrt(Q.active_power(a))
    n_total = spec["length_s"] * RATE
    me = level(audio[spec["near"][0]])
    t0 = int(spec["near_at_s"] * RATE)
    far = np.zeros(n_total)
    placed, t = [], RATE
    for f in spec["far"]:
        a = level(audio[f])
        if spec["near_in_far_pause"] and t0 - 2 * RATE < t < t0 + len(me) + 2 * RATE:
            t = t0 + len(me) + 2 * RATE                                # my sentence has a pause in the far track
        if t + len(a) > n_total - RATE:
            break
        far[t:t + len(a)] += a; placed.append(f); t += len(a) + RATE // 4
    near = np.zeros(n_total)
    near[t0:t0 + len(me)] = me
    spec["far_placed"] = placed
    return near, far, [{"kind": "near", "start": t0, "end": t0 + len(me)}]


def build_one(spec):
    audio = {f: Q.read_mono(Q.SETS / "fleurs" / f) for f in spec["near"] + spec["far"]}
    if spec["group"] == "one":
        near, far, regions = lay_one(spec, audio)
    else:
        near, far, regions = heldout.lay_out({"near": spec["near"], "far": spec["far"], "cycles": 1, "pattern": spec["pattern"]}, audio)
    p_near = Q.active_power(near)
    peak = np.max(np.abs(far)) + 1e-12
    src = np.tanh(spec["drive"] * far / peak) / np.tanh(spec["drive"]) * peak
    rooms = spec["rooms"]
    echo = signal.fftconvolve(src, heldout.response(rooms[0]))[:len(far)]
    changes = []
    if len(rooms) > 1:
        gaps = [r["end"] + RATE // 2 for r in regions[:-1]]
        for target in (0.25, 0.5, 0.75):
            free = [g for g in gaps if g not in changes]
            changes.append(min(free, key=lambda g: abs(g - target * len(far))))
        changes.sort()
        for g, room in zip(changes, rooms[1:]):
            echo[g:] = signal.fftconvolve(src, heldout.response(room))[:len(far)][g:]
    ppm = None
    if spec["drift"]:
        ppm = ppm_curve(spec["drift"], len(far))
        echo = drifted_curve(echo, ppm)
    echo *= math.sqrt(p_near / Q.active_power(echo) * 10 ** (-spec["ser"] / 10))
    noise = np.zeros(len(near))
    if spec["snr"] is not None:
        source = heldout.load_noise(heldout.NOISES[spec["noise"]])
        if len(source) <= len(near):
            source = np.tile(source, len(near) // len(source) + 2)
        offset = int(np.random.default_rng(spec["noise_seed"]).integers(0, len(source) - len(near)))
        noise = source[offset:offset + len(near)].copy()
        noise *= math.sqrt(p_near / np.mean(noise ** 2) * 10 ** (-spec["snr"] / 10))
    scale = min(1.0, 0.95 / np.max(np.abs(near + echo + noise)))
    return {"near": near * scale, "far": far, "echo": echo * scale, "noise": noise * scale, "regions": regions, "changes": changes, "ppm": ppm}


def components(spec):
    return build_one(spec)


def build():
    texts = {r["file"]: r["text"] for r in free_rows()}
    specs = json.loads(MANIFEST.read_text(encoding="utf-8"))
    for spec in specs:
        folder = REC / spec["name"]
        if (folder / "truth.json").exists():
            continue
        c = build_one(spec)
        mic = c["near"] + c["echo"] + c["noise"]
        Q.write_wav(folder / "mic.wav", mic)
        Q.write_wav(folder / "system.wav", c["far"])
        Q.write_wav(folder / "near.wav", c["near"])
        Q.write_meta(folder, len(mic) / RATE, {"mic": "mic.wav", "system": "system.wav"})
        far_files = spec.get("far_placed", spec["far"])
        ppm = c["ppm"]
        truth = {"kind": "echo", "group": spec["group"], "noise": spec["noise"] if spec["snr"] is not None else None, "room": "+".join(r["kind"] for r in spec["rooms"]),
                 "ser": spec["ser"], "snr": spec["snr"], "drift_ppm": None if ppm is None else [round(float(ppm.min()), 1), round(float(ppm.max()), 1)],
                 "change": c["changes"] or None, "drive": spec["drive"],
                 "me": " ".join(texts[f] for f in spec["near"]), "them": " ".join(texts[f] for f in far_files), "regions": c["regions"],
                 "true_speech_s": round(speech_seconds(c["near"]), 2)}
        (folder / "truth.json").write_text(json.dumps(truth, ensure_ascii=False), encoding="utf-8")
        print(spec["name"], f"{len(mic) / RATE:.0f} s", flush=True)
    MANIFEST.write_text(json.dumps(specs, ensure_ascii=False, indent=1), encoding="utf-8")   # keeps far_placed


# ---------------------------------------------------------------- CPU stage

def names(regex=None):
    import re
    return [p.name for p in sorted(REC.iterdir()) if re.search(regex or "", p.name)]


def gate_pieces(work, name):
    """(e, g) from the cache written by `cpu`: solve's output and the speech gate (solve-gate = g * e)."""
    z = np.load(Path(work) / "cache" / f"{name}.npz")
    return z["e"].astype(np.float64), z["g"].astype(np.float64)


def dilate(mask, n):
    from scipy.ndimage import maximum_filter1d
    return maximum_filter1d(mask.astype(np.uint8), size=2 * n + 1) > 0


def db(a, b):
    return 10 * math.log10((a + 1e-20) / (b + 1e-20))


def cpu(regex=None):
    import pipeline, denoise, solve, variants, n1
    stats = {}
    f_stats = ROOT / "cpu_stats.json"
    if f_stats.exists():
        stats = json.loads(f_stats.read_text(encoding="utf-8"))
    t0 = time.time()
    for name in names(regex):
        folder = REC / name
        spec = json.loads((folder / "truth.json").read_text(encoding="utf-8"))
        cache = WORK / "cache" / f"{name}.npz"
        mic, far = Q.read_mono(folder / "mic.wav"), Q.read_mono(folder / "system.wav")
        if not cache.exists():
            out, info = solve.cancel(mic, far)
            many = bool(info["many_paths"])
            e = variants.VARIANTS["sonora"](mic, far) if many else out
            g = pipeline.speech_gate(e, denoise.dpdf(e))
            cache.parent.mkdir(parents=True, exist_ok=True)
            np.savez(cache.with_suffix(".tmp.npz"), e=e.astype(np.float32), g=g.astype(np.float32))
            cache.with_suffix(".tmp.npz").replace(cache)
            stats.setdefault(name, {})["many_paths"] = many
            stats[name]["sections"] = len(info["sections"])
        # components are rebuilt from the manifest (deterministic) for the true echo, noise and speech
        spec_m = next(s for s in json.loads(MANIFEST.read_text(encoding="utf-8")) if s["name"] == name)
        c = build_one(spec_m)
        e, g = gate_pieces(WORK, name)
        n = min(len(e), len(c["echo"]))
        near_on = dilate(np.abs(c["near"][:n]) > 1e-4, RATE)             # my speech and one second around it
        far_only = ~near_on
        P = lambda x, m: float(np.sum(x[m] ** 2))
        res = e[:n] - c["near"][:n] - c["noise"][:n]                      # what is left of the echo (and the solver's own error)
        x = g * e
        s = stats.setdefault(name, {})
        s.update({"group": spec["group"], "seconds": n / RATE, "far_only_s": float(far_only.sum() / RATE),
                  "P_echo": P(c["echo"][:n], far_only), "P_res": P(res, far_only), "P_mic": P(mic[:n], far_only), "P_out": P(e[:n], far_only),
                  "P_gated": P(x[:n], far_only), "P_noise": P(c["noise"][:n], far_only)})
        s["erle_db"] = db(s["P_echo"], s["P_res"])
        truth_s = spec["true_speech_s"]
        s["true_speech_s"] = truth_s
        s["open_s"] = float((g > 0.5).sum() / RATE)
        speech = np.abs(c["near"]) > 1e-4
        for k in "ac":
            regs = n1.regions(g, x, **n1.SETTINGS[k])
            cov = np.zeros(len(g), bool)
            for a, b in regs:
                cov[a:b] = True
            sp = speech[:len(cov)] & (np.abs(c["near"][:len(cov)]) > 0)
            s[f"{k}_regions"] = len(regs)
            s[f"{k}_region_s"] = sum(b - a for a, b in regs) / RATE
            s[f"{k}_speech_covered"] = float((cov & sp).sum() / max(1, sp.sum()))
        print(f"{name} {s['group']} erle {s['erle_db']:.1f} dB, gate open {s['open_s']:.0f} s, c sends {s['c_region_s']:.0f} s in {s['c_regions']} regions, "
              f"many_paths {s.get('many_paths')} ({time.time() - t0:.0f} s)", flush=True)
        f_stats.write_text(json.dumps(stats, indent=1), encoding="utf-8")
    cpu_report(stats)


def cpu_report(stats):
    print("\nGroup attenuation on the stretches without my speech (power-weighted over the group):")
    for grp in GROUPS:
        ss = [s for s in stats.values() if s.get("group") == grp and "P_echo" in s]
        if not ss:
            continue
        tot = lambda k: sum(s[k] for s in ss)
        print(f"  {grp:6s} n={len(ss)} ERLE of the solver {db(tot('P_echo'), tot('P_res')):5.1f} dB (per call "
              f"{', '.join('%.1f' % s['erle_db'] for s in ss)}) | echo to the gated output {db(tot('P_echo'), tot('P_gated')):5.1f} dB | "
              f"mic to solver output {db(tot('P_mic'), tot('P_out')):5.1f} dB | many_paths {sum(bool(s.get('many_paths')) for s in ss)}")
    ss = [(n, s) for n, s in stats.items() if s.get("group") == "one" and "c_region_s" in s]
    if ss:
        print("\nOne-sentence calls, seconds the engine would get (setting c) against my true speech seconds:")
        for n, s in ss:
            print(f"  {n}: c {s['c_region_s']:6.1f} s in {s['c_regions']:2d} regions, a {s['a_region_s']:6.1f} s | my speech {s['true_speech_s']:.1f} s of {s['seconds']:.0f} s "
                  f"| gate open {s['open_s']:.1f} s | my speech inside c's regions {100 * s['c_speech_covered']:.0f} %")
        print(f"  total: c {sum(s['c_region_s'] for _, s in ss):.0f} s, a {sum(s['a_region_s'] for _, s in ss):.0f} s, true speech {sum(s['true_speech_s'] for _, s in ss):.1f} s")
    other = [s for s in stats.values() if s.get("group") in ("drift", "dev4") and "c_region_s" in s]
    if other:
        print(f"\nOther 8 calls: c sends {sum(s['c_region_s'] for s in other):.0f} s of {sum(s['seconds'] for s in other):.0f} s; my speech {sum(s['true_speech_s'] for s in other):.0f} s")


# ---------------------------------------------------------------- engine stage

def engine():
    """THE one command for the engine. solve-gate as the app's normal path (Amanu.exe, echo removal off,
    the audio is g * e from the cache), then setting c (transcribe-cli --batch per call). Rows under WORK/wer2."""
    os.environ["N1_SET"] = "ho"
    import asr, n1
    from concurrent.futures import ThreadPoolExecutor
    n1.REC = REC
    n1.gate_pieces = gate_pieces
    ns = names()
    t0 = time.time()
    def make(n):
        e, g = gate_pieces(WORK, n)
        return g * e, 0.0
    asr.run_jobs(str(WORK), [("stress/solve-gate", n, lambda n=n: make(n), REC / n) for n in ns], workers=1)     # one Amanu.exe at a time (it starts two transcribe-cli)
    print(asr.summary(str(WORK), "stress/solve-gate"), f"({time.time() - t0:.0f} s)", flush=True)
    with ThreadPoolExecutor(3) as pool:
        list(pool.map(lambda n: n1.transcribe_regions(str(WORK), "c", n), ns))
    print(f"setting c done ({time.time() - t0:.0f} s)", flush=True)
    show()


def show():
    import asr
    base, cut = asr.rows(str(WORK), "stress/solve-gate"), asr.rows(str(WORK), "n1/c")
    truths = {n: json.loads((REC / n / "truth.json").read_text(encoding="utf-8")) for n in names()}
    print(f"{'group':6s} {'n':>2s} {'solve-gate WER':>15s} {'echo w':>6s} | {'c WER':>7s} {'echo w':>6s} | words")
    for grp in GROUPS + ("all",):
        ks = [n for n in names() if grp == "all" or truths[n]["group"] == grp]
        b = [base[n] for n in ks if n in base]; c = [cut[n] for n in ks if n in cut]
        if b or c:
            print(f"{grp:6s} {len(ks):2d} {asr.wer(b) if b else float('nan'):15.1f} {sum(r['leak'] for r in b):6d} | "
                  f"{asr.wer(c) if c else float('nan'):7.1f} {sum(r['leak'] for r in c):6d} | {sum(r['words'] for r in (b or c))}")
    for n in names():
        if n in base and n in cut:
            print(f"  {n:11s} solve-gate {100 * base[n]['errors'] / base[n]['words']:5.1f} ({base[n]['leak']} echo)  c {100 * cut[n]['errors'] / cut[n]['words']:5.1f} ({cut[n]['leak']} echo)")


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "plan":
        if MANIFEST.exists():
            sys.exit("the manifest exists; the stress set is frozen")
        MANIFEST.parent.mkdir(parents=True, exist_ok=True)
        MANIFEST.write_text(json.dumps(plan(), ensure_ascii=False, indent=1), encoding="utf-8")
    elif cmd == "build":
        build()
    elif cmd == "cpu":
        cpu(sys.argv[2] if len(sys.argv) > 2 else None)
    elif cmd == "engine":
        engine()
    elif cmd == "show":
        show()
