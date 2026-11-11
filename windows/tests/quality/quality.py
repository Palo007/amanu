"""Amanu quality suite: transcription accuracy and echo removal under noise, scored by script.

Nobody has to check text by hand. Every number comes from public test sets with exact
transcripts, or from calls this script mixes itself, so it knows every word that was said
and every sample of echo and noise it added.

    py quality.py prepare            # download (about 1.2 GB) and build the test recordings
    py quality.py run --modes a,b    # run them through Amanu.exe with the named echo removers
    py quality.py report             # score the last run, write report.md and results.json
    py quality.py check              # compare the last run with baseline.json; exit 1 if worse
    py quality.py all --modes all --yes   # run + report + check, every model (about 3 hours)

Data lives in %USERPROFILE%\\Amanu Quality Set, never in the repository. Close Amanu first:
a running Amanu shares the CPU and the timings would be wrong.

The suite has three parts.
  asr-*      FLEURS (read) and VoxPopuli (parliament) Slovak test sets: word and character
             error rate of the local engine on clean speech, the ceiling for everything else.
  echo-*     Mixed calls: "me" speaks FLEURS sentences into the mic, "them" speaks other
             FLEURS sentences on the call track, and their voice reaches the mic again as
             echo through a computed room and a little speaker distortion. Real DEMAND noise
             is added at fixed signal-to-noise ratios, and the echo at fixed levels. Each call
             runs with echo removal off and with every echo remover.
  aec-real   Microsoft AEC Challenge clips recorded on real devices, far end only: how much
             echo each remover takes out of real hardware echo.
"""
from __future__ import annotations

import argparse
import datetime as dt
import io
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tarfile
import time
import unicodedata
import urllib.request
import zipfile
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy import signal

RATE = 16000
HERE = Path(__file__).resolve().parent
ROOT = Path(os.environ.get("AMANU_QUALITY_SET", Path.home() / "Amanu Quality Set"))
DOWNLOADS = ROOT / "downloads"
SETS = ROOT / "sets"
RECORDINGS = ROOT / "recordings"
RUNS = ROOT / "runs"
DEFAULT_EXE = HERE.parent.parent / "artifacts" / "publish" / "Amanu.exe"

ECHO_MODES = ["off", "sonora", "fast", "thorough", "sonora-linear", "sonora-nearend", "sonora-gentle", "sonora-long", "sonora-long80", "sonora-long80-nearend", "sonora-delay-linear", "sonora-delay-nearend", "sonora-delay-floor", "sonora-delay-nearend-floor", "sonora-delay-floor70", "sonora-delay-nearend-nf70", "sonora-delay-nearend-floor70", "solve", "solve-floor", "solve-gate"]
SNRS = [None, 20, 10, 5, 0, -5]          # near-end speech against noise, dB; None = no noise
SERS = [5, -5, -15]                      # near-end speech against echo, dB; lower = louder echo
NOISES = {"office": "OOFFICE", "cafe": "PCAFETER", "street": "STRAFFIC", "living": "DLIVING"}
# Two rooms, so a result is not an accident of one room. Office uses room A, cafe room B.
ROOMS = {"A": {"latency_ms": 80, "rt60": 0.30, "seed": 11}, "B": {"latency_ms": 180, "rt60": 0.55, "seed": 23}}

DOWNLOAD_FILES = {
    "fleurs-sk-test.tsv": "https://huggingface.co/datasets/google/fleurs/resolve/main/data/sk_sk/test.tsv",
    "fleurs-sk-test.tar.gz": "https://huggingface.co/datasets/google/fleurs/resolve/main/data/sk_sk/audio/test.tar.gz",
    "voxpopuli-sk-test.parquet": "https://huggingface.co/datasets/facebook/voxpopuli/resolve/main/sk/test-00000-of-00001.parquet",
    **{f"demand-{name}_16k.zip": f"https://zenodo.org/records/1227121/files/{name}_16k.zip" for name in NOISES.values()},
}
AEC_REPO = "microsoft/AEC-Challenge"
AEC_FOLDER = "datasets/test_set_icassp2022/farend-singletalk"
AEC_CLIPS = 40


# ---------------------------------------------------------------- helpers

def say(*parts):
    print(*parts, flush=True)


def fetch(url: str, target: Path):
    if target.exists() and target.stat().st_size > 0:
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_suffix(target.suffix + ".part")
    say(f"  download {url}")
    request = urllib.request.Request(url, headers={"User-Agent": "amanu-quality-suite"})
    with urllib.request.urlopen(request, timeout=120) as response, open(partial, "wb") as out:
        shutil.copyfileobj(response, out, 1 << 20)
    partial.replace(target)


def read_mono(source) -> np.ndarray:
    data, rate = sf.read(source, dtype="float64", always_2d=True)
    data = data.mean(axis=1)
    if rate != RATE:
        data = signal.resample_poly(data, RATE, rate)
    return data


def write_wav(path: Path, data: np.ndarray):
    path.parent.mkdir(parents=True, exist_ok=True)
    sf.write(path, np.clip(data, -1, 1).astype(np.float32), RATE, subtype="PCM_16")


def active_power(x: np.ndarray) -> float:
    """Mean power over the 20 ms frames within 30 dB of the loudest one: speech level, not pause level."""
    frame = RATE // 50
    n = len(x) // frame
    if n == 0:
        return float(np.mean(x ** 2) + 1e-20)
    p = (x[: n * frame].reshape(n, frame) ** 2).mean(axis=1)
    keep = p > p.max() * 1e-3
    return float(p[keep].mean() + 1e-20)


def db(x: float) -> float:
    return 10 * math.log10(max(x, 1e-20))


def normalize_text(text: str) -> list[str]:
    text = unicodedata.normalize("NFC", text.lower())
    text = re.sub(r"[^\w\s]|_", " ", text)
    return text.split()


def align(ref: list, hyp: list):
    """Levenshtein alignment; returns (substitutions, deletions, insertions, inserted hyp items)."""
    from rapidfuzz.distance import Levenshtein
    s = d = i = 0
    inserted = []
    for op in Levenshtein.editops(ref, hyp):
        if op.tag == "replace":
            s += 1
        elif op.tag == "delete":
            d += 1
        else:
            i += 1
            inserted.append(hyp[op.dest_pos])
    return s, d, i, inserted


def error_counts(ref_text: str, hyp_text: str):
    ref, hyp = normalize_text(ref_text), normalize_text(hyp_text)
    s, d, i, inserted = align(ref, hyp)
    cref, chyp = list(" ".join(ref)), list(" ".join(hyp))
    cs, cd, ci, _ = align(cref, chyp)
    return {"ref_words": len(ref), "word_errors": s + d + i, "sub": s, "del": d, "ins": i,
            "ref_chars": len(cref), "char_errors": cs + cd + ci, "inserted": inserted}


def write_meta(folder: Path, seconds: float, files: dict):
    meta = {"started": "2026-01-01T10:00:00+01:00", "ended": "2026-01-01T10:00:00+01:00",
            "duration_seconds": int(round(seconds)), "title": folder.name, "trigger": "manual",
            "stop_reason": "manual", "paused_seconds": 0, "platform": "windows",
            "files": files, "start_offset_ms": {k: 0 for k in files}}
    (folder / "meta.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")


# ---------------------------------------------------------------- prepare

def load_fleurs():
    rows = []
    for line in (DOWNLOADS / "fleurs-sk-test.tsv").read_text(encoding="utf-8").splitlines():
        cols = line.split("\t")
        if len(cols) >= 4:
            rows.append({"sentence": cols[0], "file": cols[1], "text": cols[2], "gender": cols[6] if len(cols) > 6 else ""})
    folder = SETS / "fleurs"
    if not (folder / "done").exists():
        say("  unpack FLEURS")
        folder.mkdir(parents=True, exist_ok=True)
        with tarfile.open(DOWNLOADS / "fleurs-sk-test.tar.gz") as tar:
            for member in tar:
                if member.isfile() and member.name.endswith(".wav"):
                    (folder / Path(member.name).name).write_bytes(tar.extractfile(member).read())
        (folder / "done").write_text("ok")
    return [dict(r, path=folder / r["file"]) for r in rows if (folder / r["file"]).exists()]


def load_voxpopuli():
    import pyarrow.parquet as pq
    folder = SETS / "voxpopuli"
    index = folder / "index.json"
    if not index.exists():
        say("  unpack VoxPopuli")
        folder.mkdir(parents=True, exist_ok=True)
        table = pq.read_table(DOWNLOADS / "voxpopuli-sk-test.parquet", columns=["audio_id", "audio", "normalized_text", "raw_text"])
        rows = []
        for row in table.to_pylist():
            text = row["raw_text"] or row["normalized_text"]
            if not text or not text.strip():
                continue
            path = folder / (re.sub(r"[^\w.-]", "_", row["audio_id"]) + ".wav")  # its ids hold colons
            write_wav(path, read_mono(io.BytesIO(row["audio"]["bytes"])))
            rows.append({"file": path.name, "text": text})
        index.write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    return [dict(r, path=folder / r["file"]) for r in json.loads(index.read_text(encoding="utf-8"))]


def load_noise(name: str) -> np.ndarray:
    path = SETS / "noise" / f"{name}.wav"
    if not path.exists():
        with zipfile.ZipFile(DOWNLOADS / f"demand-{name}_16k.zip") as z:
            member = next(n for n in z.namelist() if n.endswith("ch01.wav"))
            write_wav(path, read_mono(io.BytesIO(z.read(member))))
    return read_mono(path)


def fetch_aec_clips():
    folder = SETS / "aec-real"
    index = folder / "index.json"
    if index.exists():
        return json.loads(index.read_text(encoding="utf-8"))
    say("  download AEC Challenge far-end clips")
    listing = json.load(urllib.request.urlopen(f"https://api.github.com/repos/{AEC_REPO}/contents/{AEC_FOLDER}"))
    names = sorted(x["name"] for x in listing if x["name"].endswith("_mic.wav"))[:AEC_CLIPS]
    clips = []
    for mic in names:
        lpb = mic.replace("_mic.wav", "_lpb.wav")
        for name in (mic, lpb):
            fetch(f"https://media.githubusercontent.com/media/{AEC_REPO}/main/{AEC_FOLDER}/{name}", folder / name)
        clips.append({"mic": mic, "lpb": lpb})
    index.write_text(json.dumps(clips, indent=1), encoding="utf-8")
    return clips


def spread_repeats(rows: list) -> list:
    """FLEURS reads each sentence two or three times in a row, by different speakers. The app
    drops a sentence that repeats the one before it, which would count as lost words here, so
    the order is shuffled (always the same way) until no sentence follows itself."""
    rng = np.random.default_rng(7)
    rows = [rows[k] for k in rng.permutation(len(rows))]
    for k in range(1, len(rows)):
        if rows[k].get("sentence") is not None and rows[k]["sentence"] == rows[k - 1]["sentence"]:
            for j in range(k + 1, len(rows)):
                if rows[j]["sentence"] != rows[k - 1]["sentence"] and (j + 1 >= len(rows) or rows[k]["sentence"] != rows[j + 1]["sentence"]):
                    rows[k], rows[j] = rows[j], rows[k]
                    break
    return rows


def build_asr_set(name: str, rows: list, chunk_seconds: float, limit: int | None):
    if name == "fleurs":
        rows = spread_repeats(rows)
    rows = rows[:limit] if limit else rows
    gap = np.zeros(RATE)
    chunk, texts, seconds, number = [], [], 0.0, 0

    def flush():
        nonlocal chunk, texts, seconds, number
        if not chunk:
            return
        folder = RECORDINGS / f"asr-{name}-{number:02d}"
        audio = np.concatenate(chunk + [gap])
        audio *= 10 ** (-23 / 20) / math.sqrt(active_power(audio))
        write_wav(folder / "mic.wav", audio)
        write_meta(folder, len(audio) / RATE, {"mic": "mic.wav"})
        (folder / "truth.json").write_text(json.dumps({"kind": "asr", "set": name, "me": " ".join(texts)}, ensure_ascii=False), encoding="utf-8")
        chunk, texts, seconds, number = [], [], 0.0, number + 1

    for row in rows:
        audio = read_mono(row["path"])
        # FLEURS recordings differ by up to 30 dB, and the quietest ones vanish whole. A meeting mic
        # has automatic gain, so each sentence is set to one level: this part measures recognition,
        # not loudness. The echo scenes are levelled the same way.
        audio *= 10 ** (-23 / 20) / math.sqrt(active_power(audio))
        chunk += [gap, audio]
        texts.append(row["text"])
        seconds += len(audio) / RATE + 1
        if seconds >= chunk_seconds:
            flush()
    flush()


def room_response(rt60: float, latency_ms: float, rng: np.random.Generator) -> np.ndarray:
    """A speaker-to-mic path: the system's playback latency, a direct sound, then a decaying diffuse tail."""
    delay = int(RATE * latency_ms / 1000)
    length = int(RATE * min(rt60 * 1.2, 0.8))
    t = np.arange(length) / RATE
    tail = rng.standard_normal(length) * np.exp(-6.9 * t / rt60) * 0.25
    tail[: int(RATE * 0.003)] = 0
    h = np.zeros(delay + length)
    h[delay] = 1.0
    h[delay:] += tail
    return h / np.sqrt(np.sum(h ** 2))


def speaker_distortion(x: np.ndarray) -> np.ndarray:
    """A small laptop speaker driven hard: soft clipping, so the echo is not a pure linear copy."""
    peak = np.max(np.abs(x)) + 1e-12
    return np.tanh(1.5 * x / peak) / np.tanh(1.5) * peak


def pick_speech(fleurs: list, seed: int):
    """Ten near-end and eight far-end utterances, near and far never sharing a sentence."""
    rng = np.random.default_rng(seed)
    order = rng.permutation(len(fleurs))
    near, far, used = [], [], set()
    for k in order:
        row = fleurs[k]
        if row["sentence"] in used:
            continue
        seconds = sf.info(row["path"]).duration
        if not 4 <= seconds <= 14:
            continue
        if len(near) < 10:
            near.append(row)
        elif len(far) < 8:
            far.append(row)
        else:
            break
        used.add(row["sentence"])
    return near, far


def lay_out(near: list, far: list):
    """Places utterances on a timeline: far only, near only, both at once, far only, near only."""
    n_audio = [read_mono(r["path"]) for r in near]
    f_audio = [read_mono(r["path"]) for r in far]
    for group in (n_audio, f_audio):
        for k, a in enumerate(group):
            group[k] = a * 10 ** (-26 / 20) / math.sqrt(active_power(a))
    near_track, far_track, regions = [], [], []
    t = RATE  # one second of quiet at the start

    def put(track, audio, start):
        track.append((start, audio))
        return start + len(audio)

    def block(kind, near_items, far_items):
        nonlocal t
        start = t
        end_n = end_f = t
        for a in near_items:
            end_n = put(near_track, a, end_n) + RATE // 4
        for a in far_items:
            end_f = put(far_track, a, end_f) + RATE // 4
        t = max(end_n, end_f)
        regions.append({"kind": kind, "start": start, "end": t})
        t += RATE

    fi, ni = iter(f_audio), iter(n_audio)
    block("far", [], [next(fi), next(fi)])
    block("near", [next(ni), next(ni), next(ni)], [])
    block("double", [next(ni), next(ni), next(ni), next(ni)], [next(fi), next(fi), next(fi), next(fi)])
    block("far", [], [next(fi), next(fi)])
    block("near", [next(ni), next(ni), next(ni)], [])
    length = t + RATE
    near_signal, far_signal = np.zeros(length), np.zeros(length)
    for start, a in near_track:
        near_signal[start:start + len(a)] += a
    for start, a in far_track:
        far_signal[start:start + len(a)] += a
    return near_signal, far_signal, regions


def build_echo_scenes(fleurs: list):
    speech = {room: pick_speech(fleurs, cfg["seed"]) for room, cfg in ROOMS.items()}
    grid = [(noise, ser, snr) for noise in ("office", "cafe") for ser in SERS for snr in SNRS]
    grid += [(noise, -5, snr) for noise in ("street", "living") for snr in (10, 0)]
    noises = {}
    for noise, ser, snr in grid:
        room = "A" if noise in ("office", "street") else "B"
        cfg = ROOMS[room]
        near_rows, far_rows = speech[room]
        near, far, regions = lay_out(near_rows, far_rows)
        rng = np.random.default_rng(cfg["seed"])
        echo = signal.fftconvolve(speaker_distortion(far), room_response(cfg["rt60"], cfg["latency_ms"], rng))[: len(far)]
        p_near = active_power(near)
        echo *= math.sqrt(p_near / active_power(echo) * 10 ** (-ser / 10))
        mic = near + echo
        if snr is not None:
            if noise not in noises:
                noises[noise] = load_noise(NOISES[noise])
            source = noises[noise]
            offset = int(np.random.default_rng(snr + 100).integers(0, len(source) - len(near)))
            n = source[offset:offset + len(near)].copy()
            n *= math.sqrt(p_near / np.mean(n ** 2) * 10 ** (-snr / 10))
            mic = mic + n
        scale = min(1.0, 0.95 / np.max(np.abs(mic)))
        name = f"echo-{noise}-ser{ser:+d}-snr{'clean' if snr is None else f'{snr:+d}'}"
        folder = RECORDINGS / name
        write_wav(folder / "mic.wav", mic * scale)
        write_wav(folder / "system.wav", far)  # the call as the computer played it, unclipped
        write_wav(folder / "near.wav", near * scale)            # the truth the cleaned mic is scored against
        write_meta(folder, len(mic) / RATE, {"mic": "mic.wav", "system": "system.wav"})
        truth = {"kind": "echo", "noise": noise, "room": room, "ser": ser, "snr": snr,
                 "me": " ".join(r["text"] for r in near_rows), "them": " ".join(r["text"] for r in far_rows),
                 "regions": regions}
        (folder / "truth.json").write_text(json.dumps(truth, ensure_ascii=False), encoding="utf-8")


def build_aec_real(clips: list):
    folder = RECORDINGS / "aec-real"
    mics, refs, regions, t = [], [], [], 0
    gap = np.zeros(RATE)
    for clip in clips:
        mic = read_mono(SETS / "aec-real" / clip["mic"])
        lpb = read_mono(SETS / "aec-real" / clip["lpb"])
        n = min(len(mic), len(lpb))
        mics += [mic[:n], gap]
        refs += [lpb[:n], gap]
        # The challenge scores far-end single talk on the second half, after the canceller has settled.
        regions.append({"kind": "far", "start": t + n // 2, "end": t + n, "clip": clip["mic"]})
        t += n + RATE
    write_wav(folder / "mic.wav", np.concatenate(mics))
    write_wav(folder / "system.wav", np.concatenate(refs))
    write_meta(folder, t / RATE, {"mic": "mic.wav", "system": "system.wav"})
    (folder / "truth.json").write_text(json.dumps({"kind": "aec-real", "regions": regions}), encoding="utf-8")


def prepare(args):
    for name, url in DOWNLOAD_FILES.items():
        fetch(url, DOWNLOADS / name)
    clips = fetch_aec_clips()
    if RECORDINGS.exists():
        shutil.rmtree(RECORDINGS)
    fleurs = load_fleurs()
    say(f"  FLEURS {len(fleurs)} utterances")
    build_asr_set("fleurs", fleurs, 300, args.limit)
    vox = load_voxpopuli()
    say(f"  VoxPopuli {len(vox)} utterances")
    build_asr_set("voxpopuli", vox, 300, args.limit)
    build_echo_scenes(fleurs)
    build_aec_real(clips)
    say(f"prepared {len(list(RECORDINGS.iterdir()))} recordings in {RECORDINGS}")


# ---------------------------------------------------------------- run

def plan_runs(only: str | None, modes: list[str] | None = None):
    for folder in sorted(RECORDINGS.iterdir()):
        if only and not re.search(only, folder.name):
            continue
        kind = json.loads((folder / "truth.json").read_text(encoding="utf-8"))["kind"]
        for mode in (["off"] if kind == "asr" else (modes or ECHO_MODES)):
            yield folder, mode


def parse_modes(text: str | None) -> list[str] | None:
    if text is None or text == "all":
        return None
    modes = [m.strip() for m in text.split(",") if m.strip()]
    unknown = [m for m in modes if m not in ECHO_MODES]
    if unknown:
        sys.exit(f"unknown echo model: {', '.join(unknown)}. Known: {', '.join(ECHO_MODES)}")
    return modes


def seconds_per_job() -> float:
    """The mean time of finished jobs in earlier runs, from their logs; 35 s when there are none."""
    seen = []
    for log in RUNS.glob("*/*__*/transcribe.log"):
        m = re.search(r" timing total ([\d.]+) s", log.read_text(encoding="utf-8", errors="replace"))
        if m:
            seen.append(float(m.group(1)))
        if len(seen) >= 300:
            break
    return float(np.mean(seen)) if seen else 35.0


def check_plan(args, jobs: list, out_root: Path):
    """Refuse a run whose models were not named, and one longer than an hour that was not
    confirmed. Both have cost hours: a run without --only queued 754 clean-speech files
    (about 7 hours), and a new run folder runs every one of the echo models, old ones too.
    The plan is printed first, so the choice is made with the cost in view."""
    todo = [(f, m) for f, m in jobs if not (out_root / f"{f.name}__{m}" / "transcript.json").exists()]
    hours = len(todo) * seconds_per_job() / 3600
    echo = sorted({m for f, m in todo if not f.name.startswith("asr-")})
    say(f"plan: {len(todo)} jobs to run ({len(jobs) - len(todo)} done already), about {hours:.1f} h; "
        f"recordings {len({f for f, _ in todo})}, echo models {', '.join(echo) or 'none'}")
    has_echo = any(not f.name.startswith("asr-") for f, _ in jobs)
    if has_echo and args.modes is None:
        sys.exit("name the echo models to run: --modes a,b (or --modes all). "
                 "Run only the models the question needs; earlier results stay in their run folders.")
    if hours > 1 and not args.yes:
        sys.exit("this plan takes more than an hour. Narrow it with --modes or --only, or pass --yes.")


def run(args):
    exe = Path(args.exe).resolve()
    if not exe.exists():
        sys.exit(f"no Amanu.exe at {exe}")
    if not args.force and "Amanu.exe" in subprocess.run(["tasklist"], capture_output=True, text=True).stdout:
        sys.exit("Amanu is running. Quit it first (or pass --force).")
    out_root = RUNS / (args.name or dt.datetime.now().strftime("%Y%m%d-%H%M%S"))
    jobs = list(plan_runs(args.only, parse_modes(args.modes)))
    check_plan(args, jobs, out_root)
    out_root.mkdir(parents=True, exist_ok=True)
    (RUNS / "last.txt").write_text(out_root.name, encoding="utf-8")
    (out_root / "build.json").write_text(json.dumps({"exe": str(exe), "exe_time": exe.stat().st_mtime,
                                                     "started": dt.datetime.now().isoformat()}), encoding="utf-8")
    for k, (folder, mode) in enumerate(jobs, 1):
        target = out_root / f"{folder.name}__{mode}"
        if (target / "transcript.json").exists():
            continue
        if target.exists():
            shutil.rmtree(target)
        env = dict(os.environ, AMANU_MEASURE_ECHO=mode, AMANU_MEASURE_KEEP_ECHO="1")
        clock = time.monotonic()
        result = subprocess.run([str(exe), "--measure-transcription", str(folder), str(target), "auto"], env=env)
        seconds = time.monotonic() - clock
        state = "ok" if result.returncode == 0 else f"FAILED exit {result.returncode}"
        say(f"[{k}/{len(jobs)}] {folder.name} {mode}: {state} in {seconds:.0f} s")
        # The run folder gets copies of the input tracks; scoring reads the originals, so drop
        # them, and the kept call track, to keep a full run near 2 GB instead of 5.
        for copy in list(target.glob("*.wav")) + [target / ".echo-kept" / "system.wav"]:
            copy.unlink(missing_ok=True)
        save_audio_metrics(target, folder, mode, args.keep_audio)
        if result.returncode != 0 and (target / "measure.error").exists():
            say("   " + (target / "measure.error").read_text(encoding="utf-8", errors="replace").splitlines()[0])
    say(f"run done: {out_root}")
    return out_root


# ---------------------------------------------------------------- score

def transcript_text(folder: Path, speaker: str) -> str:
    doc = json.loads((folder / "transcript.json").read_text(encoding="utf-8-sig"))
    segments = sorted((s for s in doc["segments"] if s.get("speaker") == speaker), key=lambda s: s["start_ms"])
    return " ".join(s["text"] for s in segments)


def timing(folder: Path, stage: str) -> float | None:
    log = folder / "transcribe.log"
    if not log.exists():
        return None
    for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
        m = re.search(rf" timing {re.escape(stage)} ([\d.]+) s", line)
        if m:
            return float(m.group(1))
    return None


def cleaned_mic(run_folder: Path, recording: Path, mode: str) -> np.ndarray | None:
    raw = read_mono(recording / "mic.wav")
    if mode == "off":
        return raw
    kept = run_folder / ".echo-kept" / "mic.wav"
    if not kept.exists():
        return None
    out = read_mono(kept)
    # Line the cleaned copy up with the original, whatever start offset or hop delay it carries.
    window = slice(0, min(len(raw), len(out), RATE * 30))
    corr = signal.correlate(out[window], raw[window], mode="full", method="fft")
    mid = len(raw[window]) - 1
    span = RATE // 5
    lag = int(np.argmax(np.abs(corr[mid - span: mid + span + 1]))) - span
    out = np.roll(out, -lag) if lag else out
    n = min(len(raw), len(out))
    return np.pad(out[:n], (0, len(raw) - n))


def region_power(x: np.ndarray, region: dict, skip_seconds: float = 0) -> float:
    start = min(region["start"] + int(skip_seconds * RATE), region["end"] - RATE // 2)
    seg = x[start:region["end"]]
    return float(np.mean(seg ** 2) + 1e-20)


def si_sdr(estimate: np.ndarray, target: np.ndarray) -> float:
    target = target - target.mean()
    estimate = estimate - estimate.mean()
    alpha = np.dot(estimate, target) / (np.dot(target, target) + 1e-20)
    projection = alpha * target
    return db(np.sum(projection ** 2) / (np.sum((estimate - projection) ** 2) + 1e-20))


def near_gain(estimate: np.ndarray, target: np.ndarray) -> float:
    alpha = np.dot(estimate, target) / (np.dot(target, target) + 1e-20)
    return 20 * math.log10(max(abs(alpha), 1e-6))


def cut_aligned(out: np.ndarray, target: np.ndarray, region: dict, span: int = 512) -> tuple:
    """The output and the truth over one region, the output shifted to where it matches the truth best.

    Amanu passes the mic through unchanged where the call is silent and runs the remover only
    where it plays, and each remover adds its own small delay there (Sonora 128 samples, its
    linear output 64, LocalVQE fast -256), so one lag for the whole file, as cleaned_mic finds,
    leaves the double-talk parts a few milliseconds off. That was enough to make the voice look
    15 to 20 dB quieter than it was.
    """
    start, end = region["start"], region["end"]
    lo, hi = max(0, start - span), min(len(out), end + span)
    t = target[start:end]
    corr = signal.correlate(out[lo:hi], t, mode="valid", method="fft")
    lag = int(np.argmax(np.abs(corr))) + lo - start
    return out[start + lag:end + lag], t


def score_one(run_folder: Path, recording: Path, mode: str) -> dict:
    truth = json.loads((recording / "truth.json").read_text(encoding="utf-8"))
    row = {"recording": recording.name, "kind": truth["kind"], "mode": mode,
           "echo_s": timing(run_folder, "echo"), "asr_s": timing(run_folder, "asr"), "total_s": timing(run_folder, "total")}
    for key in ("noise", "room", "ser", "snr", "set"):
        if key in truth:
            row[key] = truth[key]
    if not (run_folder / "transcript.json").exists():
        row["failed"] = True
        return row
    if "me" in truth:
        me = error_counts(truth["me"], transcript_text(run_folder, "me"))
        if "them" in truth:
            far_only = set(normalize_text(truth["them"])) - set(normalize_text(truth["me"]))
            me["echo_leak_words"] = sum(1 for w in me["inserted"] if w in far_only)
            them = error_counts(truth["them"], transcript_text(run_folder, "them"))
            row["them"] = {k: v for k, v in them.items() if k != "inserted"}
        row["me"] = {k: v for k, v in me.items() if k != "inserted"}
    row.update(audio_metrics(run_folder, recording, mode, truth))
    return row


AUDIO_METRICS = "audio-metrics.json"


def audio_metrics(run_folder: Path, recording: Path, mode: str, truth: dict) -> dict:
    """The signal scores of one job. They come from the kept cleaned mic when it is there, and
    otherwise from the numbers `run` saved before it deleted that copy (see `save_audio_metrics`)."""
    if truth["kind"] not in ("echo", "aec-real"):
        return {}
    out = cleaned_mic(run_folder, recording, mode)
    if out is None:
        saved = run_folder / AUDIO_METRICS
        return json.loads(saved.read_text(encoding="utf-8")) if saved.exists() else {}
    m = {}
    raw = read_mono(recording / "mic.wav")
    regions = truth["regions"]
    far = [r for r in regions if r["kind"] == "far"]
    # ERLE: how much quieter the far-end-only parts got. Noise stays, so it is capped by the noise floor.
    m["erle_db"] = float(np.mean([db(region_power(raw, r) / region_power(out, r)) for r in far]))
    if truth["kind"] == "echo":
        m["erle_settled_db"] = db(region_power(raw, far[-1], 2) / region_power(out, far[-1], 2))
        near_truth = read_mono(recording / "near.wav")
        near = [r for r in regions if r["kind"] == "near"]
        double = [r for r in regions if r["kind"] == "double"]
        # Near-end loss: how much of my own voice the remover took away where there was no echo.
        m["near_loss_db"] = float(np.mean([db(region_power(raw, r) / region_power(out, r)) for r in near]))
        cut = lambda x, r: x[r["start"]:r["end"]]
        m["double_sisdr_in_db"] = float(np.mean([si_sdr(cut(raw, r), cut(near_truth, r)) for r in double]))
        aligned = [cut_aligned(out, near_truth, r) for r in double]
        m["double_sisdr_out_db"] = float(np.mean([si_sdr(o, t) for o, t in aligned]))
        # How loud my voice still is while both talk: the part of the output that is my voice, against my voice.
        m["double_near_gain_db"] = float(np.mean([near_gain(o, t) for o, t in aligned]))
    return m


def save_audio_metrics(target: Path, recording: Path, mode: str, keep_audio: bool):
    """Score the cleaned mic as soon as a job ends, save the numbers, and delete the copy.
    The copies were about 6 MB per call and model, so a tuning run grew by gigabytes and
    filled the disk; a later cleanup that deleted them also made the run impossible to score
    again. With the numbers saved, `report` needs no audio, and `--keep-audio` keeps the copy
    for a study that reads it."""
    truth = json.loads((recording / "truth.json").read_text(encoding="utf-8"))
    kept = target / ".echo-kept" / "mic.wav"
    if truth["kind"] not in ("echo", "aec-real") or not (target / "transcript.json").exists():
        return
    if mode != "off" and not kept.exists():
        return
    (target / AUDIO_METRICS).write_text(json.dumps(audio_metrics(target, recording, mode, truth)), encoding="utf-8")
    if not keep_audio:
        kept.unlink(missing_ok=True)


def last_run() -> Path:
    marker = RUNS / "last.txt"
    if not marker.exists():
        sys.exit("no run yet: py quality.py run")
    return RUNS / marker.read_text(encoding="utf-8").strip()


def pct(errors, total):
    return 100.0 * errors / total if total else float("nan")


def summarize(rows: list) -> dict:
    s = {"asr": {}, "echo": {}, "aec_real": {}}
    for name in sorted({r["set"] for r in rows if r["kind"] == "asr"}):
        sel = [r["me"] for r in rows if r["kind"] == "asr" and r["set"] == name and "me" in r]
        s["asr"][name] = {"wer": pct(sum(x["word_errors"] for x in sel), sum(x["ref_words"] for x in sel)),
                          "cer": pct(sum(x["char_errors"] for x in sel), sum(x["ref_chars"] for x in sel)),
                          "words": sum(x["ref_words"] for x in sel)}
    for mode in ECHO_MODES:
        sel = [r for r in rows if r["kind"] == "echo" and r["mode"] == mode and "me" in r]
        if not sel:
            continue
        cells = {}
        for r in sel:
            cells[f"{r['noise']}|{r['ser']}|{r['snr']}"] = {
                "wer": pct(r["me"]["word_errors"], r["me"]["ref_words"]), "leak": r["me"]["echo_leak_words"],
                "erle": r.get("erle_db"), "near_loss": r.get("near_loss_db"),
                "dt_gain": (r["double_sisdr_out_db"] - r["double_sisdr_in_db"]) if "double_sisdr_out_db" in r else None,
                "dt_voice": r.get("double_near_gain_db"),
                "echo_s": r.get("echo_s")}
        s["echo"][mode] = {
            "wer": pct(sum(r["me"]["word_errors"] for r in sel), sum(r["me"]["ref_words"] for r in sel)),
            "leak_words": sum(r["me"]["echo_leak_words"] for r in sel),
            "them_wer": pct(sum(r["them"]["word_errors"] for r in sel), sum(r["them"]["ref_words"] for r in sel)),
            "erle_db": float(np.mean([r["erle_db"] for r in sel if r.get("erle_db") is not None] or [float("nan")])),
            "echo_seconds": sum(r["echo_s"] or 0 for r in sel), "cells": cells}
        real = [r for r in rows if r["kind"] == "aec-real" and r["mode"] == mode and r.get("erle_db") is not None]
        if real:
            s["aec_real"][mode] = {"erle_db": real[0]["erle_db"], "echo_s": real[0].get("echo_s")}
    return s


def breaking_point(cells: dict, noise: str, ser: int, clean_wer: float):
    """The quietest noise level (highest SNR) at which the error rate is more than 1.5 times the clean one plus 5 points."""
    for snr in [s for s in SNRS if s is not None]:
        cell = cells.get(f"{noise}|{ser}|{snr}")
        if cell and cell["wer"] > clean_wer * 1.5 + 5:
            return snr
    return None


def fmt(v, spec="{:.1f}"):
    return "–" if v is None or (isinstance(v, float) and math.isnan(v)) else spec.format(v)


def write_report(folder: Path, s: dict):
    lines = [f"# Amanu quality run {folder.name}", ""]
    lines += ["## Clean transcription (no echo, no noise)", "", "| Set | Words | WER % | CER % |", "|---|---|---|---|"]
    for name, v in s["asr"].items():
        lines.append(f"| {name} | {v['words']} | {fmt(v['wer'])} | {fmt(v['cer'])} |")
    lines += ["", "WER = words wrong per 100 words. CER = characters wrong per 100 characters.", ""]
    lines += ["## Mixed calls, all conditions together", "",
              "| Echo remover | My WER % | Echo words in my text | Their WER % | Echo removed (dB) | Echo time (s) |", "|---|---|---|---|---|---|"]
    for mode, v in s["echo"].items():
        lines.append(f"| {mode} | {fmt(v['wer'])} | {v['leak_words']} | {fmt(v['them_wer'])} | {fmt(v['erle_db'])} | {fmt(v['echo_seconds'])} |")
    if s["aec_real"]:
        lines += ["", "## Real device echo (Microsoft AEC Challenge, far end only)", "", "| Echo remover | Echo removed (dB) | Time (s) |", "|---|---|---|"]
        for mode, v in s["aec_real"].items():
            lines.append(f"| {mode} | {fmt(v['erle_db'])} | {fmt(v['echo_s'])} |")
    snr_names = ["clean" if x is None else f"{x:+d} dB" for x in SNRS]
    for metric, title in (("wer", "My WER %"), ("leak", "Echo words that leaked into my text"),
                          ("erle", "Echo removed in far-end-only parts (dB)"), ("near_loss", "My voice lost where there was no echo (dB, 0 is best)"),
                          ("dt_voice", "Double talk: my voice level after echo removal (dB, 0 is best)"),
                          ("dt_gain", "Double talk: my voice cleaner by (SI-SDR dB, higher is better)")):
        lines += ["", f"## {title}", "", "Rows: noise type and echo level (SER: lower = louder echo). Columns: noise level (SNR: lower = louder noise).", ""]
        lines += ["| Remover | Noise | SER | " + " | ".join(snr_names) + " |", "|---|---|---|" + "---|" * len(SNRS)]
        for mode, v in s["echo"].items():
            for noise in ("office", "cafe"):
                for ser in SERS:
                    vals = [v["cells"].get(f"{noise}|{ser}|{snr}", {}).get(metric) for snr in SNRS]
                    lines.append(f"| {mode} | {noise} | {ser:+d} | " + " | ".join(fmt(x, "{:.0f}" if metric == "leak" else "{:.1f}") for x in vals) + " |")
    lines += ["", "## Where each remover stops working", "",
              "The noise level at which my WER first passes 1.5 × its clean value + 5 points. \"–\" means it held to −5 dB.", "",
              "| Remover | Noise | SER | Breaks at SNR |", "|---|---|---|---|"]
    for mode, v in s["echo"].items():
        for noise in ("office", "cafe"):
            for ser in SERS:
                clean = v["cells"].get(f"{noise}|{ser}|None", {}).get("wer")
                if clean is not None:
                    lines.append(f"| {mode} | {noise} | {ser:+d} | {fmt(breaking_point(v['cells'], noise, ser, clean), '{:+d} dB')} |")
    other = [(k, c) for mode, v in s["echo"].items() for k, c in v["cells"].items() if k.split("|")[0] in ("street", "living")]
    if other:
        lines += ["", "## Other noise (SER −5)", "", "| Remover | Noise | SNR | My WER % | Echo words |", "|---|---|---|---|---|"]
        for mode, v in s["echo"].items():
            for k, c in sorted(v["cells"].items()):
                noise, ser, snr = k.split("|")
                if noise in ("street", "living"):
                    lines.append(f"| {mode} | {noise} | {snr} | {fmt(c['wer'])} | {c['leak']} |")
    (folder / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def report(args):
    folder = Path(args.run) if args.run else last_run()
    rows = []
    for target in sorted(folder.iterdir()):
        if target.is_dir() and "__" in target.name:
            name, mode = target.name.rsplit("__", 1)
            if (RECORDINGS / name).exists():
                rows.append(score_one(target, RECORDINGS / name, mode))
    summary = summarize(rows)
    (folder / "results.json").write_text(json.dumps({"rows": rows, "summary": summary}, indent=1, ensure_ascii=False), encoding="utf-8")
    write_report(folder, summary)
    say(f"report: {folder / 'report.md'}")
    return summary


# ---------------------------------------------------------------- check

# How much worse a number may get before check fails. Each limit sits above the
# run-to-run noise of the same build, so a failure means the build changed something.
TOLERANCE = {"asr_wer": 1.0, "echo_wer": 2.0, "leak_words": 10, "erle_db": 3.0}


def check(args):
    folder = Path(args.run) if args.run else last_run()
    summary = json.loads((folder / "results.json").read_text(encoding="utf-8"))["summary"]
    baseline_path = HERE / "baseline.json"
    if args.update_baseline or not baseline_path.exists():
        slim = {"asr": {k: {"wer": v["wer"], "cer": v["cer"]} for k, v in summary["asr"].items()},
                "echo": {k: {"wer": v["wer"], "leak_words": v["leak_words"], "erle_db": v["erle_db"]} for k, v in summary["echo"].items()},
                "aec_real": summary["aec_real"], "run": folder.name}
        baseline_path.write_text(json.dumps(slim, indent=1) + "\n", encoding="utf-8")
        say(f"baseline written: {baseline_path}")
        return 0
    base = json.loads(baseline_path.read_text(encoding="utf-8"))
    problems = []
    for name, v in base["asr"].items():
        now = summary["asr"].get(name, {}).get("wer")
        if now is not None and now > v["wer"] + TOLERANCE["asr_wer"]:
            problems.append(f"clean {name} WER {v['wer']:.1f} -> {now:.1f} %")
    for mode, v in base["echo"].items():
        now = summary["echo"].get(mode)
        if not now:
            continue
        if now["wer"] > v["wer"] + TOLERANCE["echo_wer"]:
            problems.append(f"{mode}: mixed-call WER {v['wer']:.1f} -> {now['wer']:.1f} %")
        if now["leak_words"] > v["leak_words"] + TOLERANCE["leak_words"]:
            problems.append(f"{mode}: echo words {v['leak_words']} -> {now['leak_words']}")
        if now["erle_db"] < v["erle_db"] - TOLERANCE["erle_db"]:
            problems.append(f"{mode}: echo removed {v['erle_db']:.1f} -> {now['erle_db']:.1f} dB")
    for line in problems:
        say("WORSE: " + line)
    say("check: " + ("FAILED" if problems else "passed, nothing got worse than baseline.json"))
    return 1 if problems else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["prepare", "run", "report", "check", "all"])
    parser.add_argument("--exe", default=str(DEFAULT_EXE), help="Amanu.exe to test (default: the published build)")
    parser.add_argument("--only", help="regular expression: run only recordings whose name matches")
    parser.add_argument("--name", help="name of the run folder (default: date and time); an existing run resumes")
    parser.add_argument("--run", help="run folder to report or check (default: the last run)")
    parser.add_argument("--limit", type=int, help="prepare: use only the first N utterances of each clean set")
    parser.add_argument("--force", action="store_true", help="run even when Amanu is open")
    parser.add_argument("--modes", help="run: comma-separated echo models, or 'all'; needed when echo calls are in the plan")
    parser.add_argument("--yes", action="store_true", help="run: start a plan that takes more than an hour")
    parser.add_argument("--keep-audio", action="store_true",
                        help="run: keep each job's cleaned mic (.echo-kept/mic.wav, about 6 MB) after scoring it")
    parser.add_argument("--update-baseline", action="store_true", help="check: write this run as the new baseline")
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args)
    elif args.command == "run":
        run(args)
    elif args.command == "report":
        report(args)
    elif args.command == "check":
        sys.exit(check(args))
    else:
        args.run = str(run(args))
        report(args)
        sys.exit(check(args))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
