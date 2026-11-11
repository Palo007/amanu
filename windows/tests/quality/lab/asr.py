"""WER of lab audio: writes a cleaned mic as a recording, has Amanu transcribe it with its
own echo removal off, and scores my words as the suite does. Results are cached per
(tag, call) under <work>/wer2, with the transcript kept, so later steps (voting between
transcripts, bootstrap intervals) need no second run.

Three transcriptions at once is the most the integrated GPU takes: three run in 10 s each
against 14 s alone, six take 180 s each.
"""
import json, os, shutil, subprocess, threading, time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import numpy as np
import lab
Q = lab.Q
# AMANU_LAB_EXE names another build, for the echo models that only a newer one has.
EXE = Path(os.environ.get("AMANU_LAB_EXE") or Path.home() / "Amanu Quality Set" / "measure-sonora-delay" / "Amanu.exe")
_gpu = threading.Semaphore(3)


def cached(work, tag, name):
    f = Path(work) / "wer2" / tag / f"{name}.json"
    return json.loads(f.read_text(encoding="utf-8")) if f.exists() else None


def transcribe(work, tag, name, audio, src=None, secs=0.0, exe=EXE):
    """Score `audio` as the mic of recording `name` (taken from `src`, default the suite's)."""
    row = cached(work, tag, name)
    if row is not None:
        return row
    src = Path(src) if src else Q.RECORDINGS / name
    base = Path(work) / "wer2" / tag
    folder, target = base / f"{name}.in", base / f"{name}.run"
    for d in (folder, target):
        if d.exists():
            shutil.rmtree(d)
    folder.mkdir(parents=True)
    Q.write_wav(folder / "mic.wav", audio)
    for f in ("system.wav", "meta.json", "truth.json"):
        shutil.copy(src / f, folder / f)
    with _gpu:
        subprocess.run([str(exe), "--measure-transcription", str(folder), str(target), "auto"],
                       env=dict(os.environ, AMANU_MEASURE_ECHO="off"), check=True)
    truth = json.loads((src / "truth.json").read_text(encoding="utf-8"))
    hyp = Q.transcript_text(target, "me")
    me = Q.error_counts(truth["me"], hyp)
    far_only = set(Q.normalize_text(truth["them"])) - set(Q.normalize_text(truth["me"]))
    row = {"errors": me["word_errors"], "words": me["ref_words"], "sub": me["sub"], "del": me["del"], "ins": me["ins"],
           "leak": sum(1 for w in me["inserted"] if w in far_only), "secs": secs, "hyp": hyp,
           **{k: truth.get(k) for k in ("noise", "room", "ser", "snr")}}
    (base / f"{name}.json").write_text(json.dumps(row, ensure_ascii=False), encoding="utf-8")
    shutil.rmtree(folder); shutil.rmtree(target)
    return row


def run_jobs(work, jobs, workers=4):
    """jobs: (tag, name, make) with make() -> audio or (audio, seconds), or (tag, name, make, src).
    Cached jobs are skipped. The audio is made in the worker; only three transcribe at once."""
    until = float(os.environ.get("AMANU_LAB_UNTIL", "inf"))    # epoch seconds: start no job after this

    def one(job):
        tag, name, make = job[:3]
        if cached(work, tag, name) is None and time.time() < until:
            out = make()
            audio, secs = out if isinstance(out, tuple) else (out, 0.0)
            transcribe(work, tag, name, audio, job[3] if len(job) > 3 else None, secs)
    with ThreadPoolExecutor(workers) as pool:
        list(pool.map(one, jobs))


def rows(work, tag):
    return {f.stem: json.loads(f.read_text(encoding="utf-8")) for f in sorted((Path(work) / "wer2" / tag).glob("*.json"))}


def wer(rs):
    rs = list(rs)
    return 100 * sum(r["errors"] for r in rs) / max(1, sum(r["words"] for r in rs))


def summary(work, tag):
    rs = list(rows(work, tag).values())
    return (f"{tag:28s} n={len(rs):3d} WER {wer(rs):5.1f}  no noise {wer(r for r in rs if r['snr'] is None):5.1f}"
            f"  echo words {sum(r['leak'] for r in rs):3d}")


def amanu(work, tag, name, src, echo_model, exe=EXE):
    """Score recording `name` in folder `src` with one of Amanu's own echo models, as the suite does."""
    row = cached(work, tag, name)
    if row is not None:
        return row
    src = Path(src)
    base = Path(work) / "wer2" / tag
    target = base / f"{name}.run"
    if target.exists():
        shutil.rmtree(target)
    base.mkdir(parents=True, exist_ok=True)
    with _gpu:
        subprocess.run([str(exe), "--measure-transcription", str(src), str(target), "auto"],
                       env=dict(os.environ, AMANU_MEASURE_ECHO=echo_model), check=True)
    truth = json.loads((src / "truth.json").read_text(encoding="utf-8"))
    hyp = Q.transcript_text(target, "me")
    me = Q.error_counts(truth["me"], hyp)
    far_only = set(Q.normalize_text(truth["them"])) - set(Q.normalize_text(truth["me"]))
    row = {"errors": me["word_errors"], "words": me["ref_words"], "sub": me["sub"], "del": me["del"], "ins": me["ins"],
           "leak": sum(1 for w in me["inserted"] if w in far_only), "secs": Q.timing(target, "echo") or 0.0, "hyp": hyp,
           **{k: truth.get(k) for k in ("noise", "room", "ser", "snr")}}
    (base / f"{name}.json").write_text(json.dumps(row, ensure_ascii=False), encoding="utf-8")
    shutil.rmtree(target)
    return row


def bootstrap(a, b, n=10000, seed=1):
    """WER of rows `a` minus WER of rows `b` (dicts by call name, the same calls) with a 95 %
    interval from a paired bootstrap over calls."""
    names = sorted(set(a) & set(b))
    ea, eb = np.array([a[k]["errors"] for k in names]), np.array([b[k]["errors"] for k in names])
    w = np.array([a[k]["words"] for k in names])
    rng = np.random.default_rng(seed)
    pick = rng.integers(0, len(names), (n, len(names)))
    d = 100 * (ea[pick].sum(1) - eb[pick].sum(1)) / w[pick].sum(1)
    return 100 * (ea.sum() - eb.sum()) / w.sum(), float(np.percentile(d, 2.5)), float(np.percentile(d, 97.5))
