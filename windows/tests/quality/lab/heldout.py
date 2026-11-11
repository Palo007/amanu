"""The held-out set: calls no remover was tuned on. Built once from a frozen manifest.

    py -P heldout.py plan      # writes heldout-manifest.json (only when it does not exist)
    py -P heldout.py build     # downloads, then writes the recordings from the manifest
    py -P heldout.py hash      # prints the SHA-256 of every mic.wav

It differs from the suite's 40 calls in everything a tuning could have learned:
- speech from FLEURS Slovak dev (the suite uses test), other sentences in every call;
- noise from four other DEMAND rooms (meeting room, restaurant, kitchen, public square);
- rooms with other seeds, reverberation times and latencies, in three kinds: the suite's
  reverberant recipe ("rev"), a direct sound 6 dB above the tail ("direct", a laptop) and
  one level with it ("mid"); four calls have no echo at all (headphones);
- other block orders, speaker drive (none, the suite's, harder), SER 5 to -15 dB;
- clock drift of 50 to 200 ppm between the call track and the mic in half the calls;
- a device change (another path and latency) in the middle of six calls;
- four long calls of about 12 minutes.

Recordings go to <quality set>/heldout/recordings in the suite's format, so asr.py and
quality.py's scores read them. Nothing here may be used to choose a setting.
"""
import hashlib, io, json, math, os, sys, tarfile, zipfile
from pathlib import Path
import numpy as np
import soundfile as sf
from scipy import signal
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lab
Q = lab.Q
RATE = lab.RATE
HO = Q.ROOT / "heldout"
DOWN, RECORDINGS = HO / "downloads", HO / "recordings"
MANIFEST = Path(__file__).with_name("heldout-manifest.json")
NOISES = {"meeting": "OMEETING", "restaurant": "PRESTO", "kitchen": "DKITCHEN", "square": "SPSQUARE"}
FILES = {
    "fleurs-sk-dev.tsv": "https://huggingface.co/datasets/google/fleurs/resolve/main/data/sk_sk/dev.tsv",
    "fleurs-sk-dev.tar.gz": "https://huggingface.co/datasets/google/fleurs/resolve/main/data/sk_sk/audio/dev.tar.gz",
    **{f"demand-{n}_16k.zip": f"https://zenodo.org/records/1227121/files/{n}_16k.zip" for n in NOISES.values()},
}
# Block orders of a short call: (kind, near utterances, far utterances). Ten near and eight far each.
PATTERNS = [
    [("far", 0, 2), ("near", 3, 0), ("double", 4, 4), ("far", 0, 2), ("near", 3, 0)],     # the suite's
    [("near", 3, 0), ("far", 0, 2), ("double", 4, 4), ("near", 3, 0), ("far", 0, 2)],
    [("far", 0, 2), ("double", 3, 3), ("near", 4, 0), ("double", 3, 3)],
    [("near", 2, 0), ("double", 4, 4), ("far", 0, 2), ("near", 4, 0), ("far", 0, 2)],
]


def dev_rows():
    rows = []
    for line in (DOWN / "fleurs-sk-dev.tsv").read_text(encoding="utf-8").splitlines():
        c = line.split("\t")
        if len(c) >= 4:
            rows.append({"sentence": c[0], "file": c[1], "text": c[2]})
    folder = HO / "sets" / "fleurs-dev"
    if not (folder / "done").exists():
        folder.mkdir(parents=True, exist_ok=True)
        with tarfile.open(DOWN / "fleurs-sk-dev.tar.gz") as tar:
            for m in tar:
                if m.isfile() and m.name.endswith(".wav"):
                    (folder / Path(m.name).name).write_bytes(tar.extractfile(m).read())
        (folder / "done").write_text("ok")
    rows = [dict(r, path=folder / r["file"]) for r in rows if (folder / r["file"]).exists()]
    rows = [r for r in rows if 4 <= sf.info(r["path"]).duration <= 14]
    return sorted(rows, key=lambda r: r["file"])


def load_noise(name):
    path = HO / "sets" / "noise" / f"{name}.wav"
    if not path.exists():
        with zipfile.ZipFile(DOWN / f"demand-{name}_16k.zip") as z:
            member = next(n for n in z.namelist() if n.endswith("ch01.wav"))
            Q.write_wav(path, Q.read_mono(io.BytesIO(z.read(member))))
    return Q.read_mono(path)


def plan():
    """The frozen recipe of every call. The seed is fixed; the manifest is committed."""
    rows = dev_rows()
    rng = np.random.default_rng(20261111)
    used = np.zeros(len(rows))

    def pick(n_near, n_far):
        # Least-used utterances first, so every utterance serves about equally often; near and
        # far never share a sentence, and no sentence comes twice on one side.
        order = np.lexsort((rng.random(len(rows)), used))
        near, far, seen = [], [], set()
        for k in order:
            s = rows[k]["sentence"]
            if s in seen:
                continue
            if len(near) < n_near:
                near.append(int(k))
            elif len(far) < n_far:
                far.append(int(k))
            else:
                break
            seen.add(s); used[k] += 1
        return [rows[k]["file"] for k in near], [rows[k]["file"] for k in far]

    def shuffled(values, n):
        v = [values[i % len(values)] for i in range(n)]
        rng.shuffle(v)
        return v

    def room(kind):
        if kind == "rev":
            return {"kind": kind, "rt60": round(float(rng.uniform(0.25, 0.6)), 3), "latency_ms": int(rng.integers(40, 221)), "seed": int(rng.integers(1000, 10 ** 6))}
        return {"kind": kind, "rt60": round(float(rng.uniform(0.2, 0.5)), 3), "latency_ms": int(rng.integers(20, 161)), "seed": int(rng.integers(1000, 10 ** 6))}

    n = 44
    kinds = shuffled(["rev", "direct", "mid", "rev", "direct", "mid", "rev", "direct", "mid", "rev", "direct"], n)
    sers = shuffled([5, -5, -15, 0, -10], n)
    snrs = shuffled([None] * 9 + [20] * 9 + [10] * 9 + [5] * 8 + [0] * 6 + [-5] * 3, n)
    noises = shuffled(list(NOISES), n)
    drives = shuffled([1.5, 1.5, 0.0, 2.5], n)
    drifts = shuffled([0] * 20 + [50, -50, 100, -100, 200, -200] * 4, n)
    changes = shuffled([True] * 6 + [False] * (n - 6), n)
    patterns = shuffled([0, 1, 2, 3], n)
    calls = []
    for i in range(n):
        near, far = pick(10, 8)
        calls.append({"name": f"ho-{i:02d}", "pattern": patterns[i], "cycles": 1, "near": near, "far": far, "room": room(kinds[i]),
                      "room2": room(kinds[i]) if changes[i] else None, "ser": sers[i], "snr": snrs[i], "noise": noises[i],
                      "drive": drives[i], "drift_ppm": drifts[i], "noise_seed": int(rng.integers(0, 10 ** 6))})
    for i, snr in enumerate([None, 20, 10, 0]):     # headphones: no echo
        near, far = pick(10, 8)
        calls.append({"name": f"ho-{n + i:02d}", "pattern": i, "cycles": 1, "near": near, "far": far, "room": None, "room2": None, "ser": None,
                      "snr": snr, "noise": list(NOISES)[i], "drive": 0.0, "drift_ppm": 0, "noise_seed": int(rng.integers(0, 10 ** 6))})
    long = [("rev", None, -5, 20, "meeting", 100), ("direct", "direct", -10, None, "kitchen", 0),
            ("mid", None, 0, 10, "restaurant", -50), ("rev", None, -5, 5, "square", 0)]
    for i, (kind, kind2, ser, snr, noise, ppm) in enumerate(long):
        near, far = pick(50, 40)
        calls.append({"name": f"ho-long-{i}", "pattern": i, "cycles": 5, "near": near, "far": far, "room": room(kind),
                      "room2": room(kind2) if kind2 else None, "ser": ser, "snr": snr, "noise": noise, "drive": 1.5,
                      "drift_ppm": ppm, "noise_seed": int(rng.integers(0, 10 ** 6))})
    return calls


def lay_out(spec, audio):
    """The suite's timeline rules (one second between blocks, a quarter between utterances)
    for any block order, repeated `cycles` times."""
    near_it, far_it = iter(spec["near"]), iter(spec["far"])
    level = lambda a: a * 10 ** (-26 / 20) / math.sqrt(Q.active_power(a))
    near_track, far_track, regions = [], [], []
    t = RATE
    for _ in range(spec["cycles"]):
        for kind, n_near, n_far in PATTERNS[spec["pattern"]]:
            start = end_n = end_f = t
            for _ in range(n_near):
                a = level(audio[next(near_it)]); near_track.append((end_n, a)); end_n += len(a) + RATE // 4
            for _ in range(n_far):
                a = level(audio[next(far_it)]); far_track.append((end_f, a)); end_f += len(a) + RATE // 4
            t = max(end_n, end_f)
            regions.append({"kind": kind, "start": start, "end": t})
            t += RATE
    near, far = np.zeros(t + RATE), np.zeros(t + RATE)
    for track, out in ((near_track, near), (far_track, far)):
        for start, a in track:
            out[start:start + len(a)] += a
    return near, far, regions


def response(room):
    rng = np.random.default_rng(room["seed"])
    if room["kind"] == "rev":
        return Q.room_response(room["rt60"], room["latency_ms"], rng)
    return lab.direct_room(room["latency_ms"], room["rt60"], 6.0 if room["kind"] == "direct" else 0.0, rng)


def drifted(x, ppm, taps=32):
    """x read at a clock that runs ppm faster: y[n] = x(n * (1 + ppm / 1e6)), by windowed-sinc interpolation."""
    if not ppm:
        return x
    y = np.zeros(len(x))
    k = np.arange(-taps // 2 + 1, taps // 2 + 1)
    xp = np.pad(x, (taps, taps))
    for s in range(0, len(x), 1 << 18):
        pos = np.arange(s, min(len(x), s + (1 << 18))) * (1 + ppm * 1e-6)
        i = np.floor(pos).astype(np.int64); f = pos - i
        ok = i < len(x)
        i, f = i[ok], f[ok]
        arg = k[None, :] - f[:, None]
        w = np.sinc(arg) * (0.5 + 0.5 * np.cos(np.pi * arg / (taps / 2 + 1)))
        y[s:s + len(i)] = np.einsum("ij,ij->i", xp[i[:, None] + k[None, :] + taps], w)
    return y


def components(spec, audio=None):
    """near, far, echo, noise and regions of a call, before quantization; mic = near + echo + noise."""
    if audio is None:
        folder = HO / "sets" / "fleurs-dev"
        audio = {f: Q.read_mono(folder / f) for f in spec["near"] + spec["far"]}
    near, far, regions = lay_out(spec, audio)
    p_near = Q.active_power(near)
    echo = np.zeros(len(near))
    change = None
    if spec["room"] is not None:
        if spec["drive"]:
            peak = np.max(np.abs(far)) + 1e-12
            src = np.tanh(spec["drive"] * far / peak) / np.tanh(spec["drive"]) * peak
        else:
            src = far
        echo = signal.fftconvolve(src, response(spec["room"]))[: len(far)]
        if spec["room2"] is not None:
            # The device changes in the pause between two blocks nearest the middle of the call.
            gaps = [r["end"] + RATE // 2 for r in regions[:-1]]
            change = min(gaps, key=lambda g: abs(g - len(far) // 2))
            second = signal.fftconvolve(src, response(spec["room2"]))[: len(far)]
            echo[change:] = second[change:]
        echo = drifted(echo, spec["drift_ppm"])
        echo *= math.sqrt(p_near / Q.active_power(echo) * 10 ** (-spec["ser"] / 10))
    noise = np.zeros(len(near))
    if spec["snr"] is not None:
        source = load_noise(NOISES[spec["noise"]])
        offset = int(np.random.default_rng(spec["noise_seed"]).integers(0, len(source) - len(near)))
        noise = source[offset:offset + len(near)].copy()
        noise *= math.sqrt(p_near / np.mean(noise ** 2) * 10 ** (-spec["snr"] / 10))
    scale = min(1.0, 0.95 / np.max(np.abs(near + echo + noise)))
    return {"near": near * scale, "far": far, "echo": echo * scale, "noise": noise * scale, "regions": regions, "change": change}


def build():
    for name, url in FILES.items():
        Q.fetch(url, DOWN / name)
    rows = {r["file"]: r for r in dev_rows()}
    for spec in json.loads(MANIFEST.read_text(encoding="utf-8")):
        folder = RECORDINGS / spec["name"]
        if (folder / "truth.json").exists():
            continue
        c = components(spec)
        mic = c["near"] + c["echo"] + c["noise"]
        Q.write_wav(folder / "mic.wav", mic)
        Q.write_wav(folder / "system.wav", c["far"])
        Q.write_wav(folder / "near.wav", c["near"])
        Q.write_meta(folder, len(mic) / RATE, {"mic": "mic.wav", "system": "system.wav"})
        truth = {"kind": "echo", "noise": spec["noise"] if spec["snr"] is not None else None, "room": spec["room"]["kind"] if spec["room"] else "none",
                 "ser": spec["ser"], "snr": spec["snr"], "drift_ppm": spec["drift_ppm"], "change": c["change"], "drive": spec["drive"],
                 "me": " ".join(rows[f]["text"] for f in spec["near"]), "them": " ".join(rows[f]["text"] for f in spec["far"]),
                 "regions": c["regions"]}
        (folder / "truth.json").write_text(json.dumps(truth, ensure_ascii=False), encoding="utf-8")
        print(spec["name"], f"{len(mic) / RATE:.0f} s", flush=True)


if __name__ == "__main__":
    if sys.argv[1] == "plan":
        if MANIFEST.exists():
            sys.exit("the manifest exists; the held-out set is frozen")
        MANIFEST.write_text(json.dumps(plan(), ensure_ascii=False, indent=1), encoding="utf-8")
    elif sys.argv[1] == "build":
        build()
    elif sys.argv[1] == "hash":
        for folder in sorted(RECORDINGS.iterdir()):
            print(hashlib.sha256((folder / "mic.wav").read_bytes()).hexdigest(), folder.name)
