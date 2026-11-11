"""Offline lab: scenes from the quality suite (on disk or rebuilt with other room/speaker
settings) and the suite's own echo scores, for any echo remover given as a function."""
from __future__ import annotations

import importlib.util
import json
import math
import sys
from pathlib import Path

import numpy as np
from scipy import signal

WT = Path(__file__).resolve().parents[4]
_spec = importlib.util.spec_from_file_location("quality", WT / "windows/tests/quality/quality.py")
Q = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(Q)
RATE = Q.RATE


def load_recording(name: str):
    folder = Q.RECORDINGS / name
    truth = json.loads((folder / "truth.json").read_text(encoding="utf-8"))
    mic = Q.read_mono(folder / "mic.wav")
    far = Q.read_mono(folder / "system.wav")
    near = Q.read_mono(folder / "near.wav") if (folder / "near.wav").exists() else None
    return {"name": name, "mic": mic, "far": far, "near": near, "truth": truth}


_fleurs = None
_speech = {}


def direct_room(latency_ms: float, rt60: float, drr_db: float, rng) -> np.ndarray:
    """A device path with a strong direct sound: the direct impulse, then a diffuse tail
    scaled so direct-to-reverberant energy is drr_db."""
    delay = int(RATE * latency_ms / 1000)
    length = int(RATE * min(rt60 * 1.2, 0.8))
    t = np.arange(length) / RATE
    tail = rng.standard_normal(length) * np.exp(-6.9 * t / rt60)
    tail[: int(RATE * 0.003)] = 0
    tail *= math.sqrt(10 ** (-drr_db / 10) / np.sum(tail ** 2))
    h = np.zeros(delay + length)
    h[delay] = 1.0
    h[delay:] += tail
    return h / np.sqrt(np.sum(h ** 2))


def build_scene(room: str = "A", ser: int = -5, snr=None, noise: str = "office", distortion: bool = True,
                rir: str = "suite", drr_db: float = 0.0, latency_ms: float | None = None, rt60: float | None = None):
    """The suite's scene recipe, with switches for the speaker model and the room."""
    global _fleurs
    if _fleurs is None:
        _fleurs = Q.load_fleurs()
    cfg = dict(Q.ROOMS[room])
    if latency_ms is not None:
        cfg["latency_ms"] = latency_ms
    if rt60 is not None:
        cfg["rt60"] = rt60
    if room not in _speech:
        _speech[room] = Q.pick_speech(_fleurs, Q.ROOMS[room]["seed"])
    near_rows, far_rows = _speech[room]
    near, far, regions = Q.lay_out(near_rows, far_rows)
    rng = np.random.default_rng(cfg["seed"])
    h = Q.room_response(cfg["rt60"], cfg["latency_ms"], rng) if rir == "suite" else direct_room(cfg["latency_ms"], cfg["rt60"], drr_db, rng)
    src = Q.speaker_distortion(far) if distortion else far
    echo = signal.fftconvolve(src, h)[: len(far)]
    p_near = Q.active_power(near)
    echo *= math.sqrt(p_near / Q.active_power(echo) * 10 ** (-ser / 10))
    mic = near + echo
    n = np.zeros(len(near))
    if snr is not None:
        source = Q.load_noise(Q.NOISES[noise])
        offset = int(np.random.default_rng(snr + 100).integers(0, len(source) - len(near)))
        n = source[offset:offset + len(near)].copy()
        n *= math.sqrt(p_near / np.mean(n ** 2) * 10 ** (-snr / 10))
        mic = mic + n
    scale = min(1.0, 0.95 / np.max(np.abs(mic)))
    # The suite writes 16-bit files; quantize the same way.
    q = lambda x: np.round(np.clip(x, -1, 1) * 32767) / 32767
    return {"name": f"{room}-ser{ser}-snr{snr}-{rir}-dist{int(distortion)}", "mic": q(mic * scale), "far": q(far),
            "near": q(near * scale), "echo": echo * scale, "noise": n * scale, "h": h,
            "truth": {"kind": "echo", "regions": regions, "room": room, "ser": ser, "snr": snr}}


def align_whole(out: np.ndarray, raw: np.ndarray) -> np.ndarray:
    window = slice(0, min(len(raw), len(out), RATE * 30))
    corr = signal.correlate(out[window], raw[window], mode="full", method="fft")
    mid = len(raw[window]) - 1
    span = RATE // 5
    lag = int(np.argmax(np.abs(corr[mid - span: mid + span + 1]))) - span
    out = np.roll(out, -lag) if lag else out
    return out


def align_near(out: np.ndarray, raw: np.ndarray, regions) -> np.ndarray:
    """Shift by the remover's constant latency, found where only the near end talks and the
    output equals the mic; a far-only start can mislead a whole-file correlation."""
    near = [r for r in regions if r["kind"] == "near"]
    if not near:
        return align_whole(out, raw)
    r = near[0]
    best = max((-256, 0, 64, 128), key=lambda lag: float(np.dot(np.roll(out, -lag)[r["start"]:r["end"]], raw[r["start"]:r["end"]])))
    return np.roll(out, -best)


def score(scene: dict, out: np.ndarray) -> dict:
    raw, regions = scene["mic"], scene["truth"]["regions"]
    out = align_near(out, raw, regions)
    far = [r for r in regions if r["kind"] == "far"]
    row = {"erle": float(np.mean([Q.db(Q.region_power(raw, r) / Q.region_power(out, r)) for r in far]))}
    if scene.get("near") is not None:
        row["erle_settled"] = Q.db(Q.region_power(raw, far[-1], 2) / Q.region_power(out, far[-1], 2))
        dbl = [r for r in regions if r["kind"] == "double"]
        cut = lambda x, r: x[r["start"]:r["end"]]
        sin = float(np.mean([Q.si_sdr(cut(raw, r), cut(scene["near"], r)) for r in dbl]))
        aligned = [Q.cut_aligned(out, scene["near"], r) for r in dbl]
        sout = float(np.mean([Q.si_sdr(o, t) for o, t in aligned]))
        row.update(dt_sisdr_in=sin, dt_sisdr_out=sout, dt_gain=sout - sin,
                   dt_voice=float(np.mean([Q.near_gain(o, t) for o, t in aligned])))
    return row


def oracle_linear_erle(scene: dict, taps: int, delay: int) -> float:
    """ERLE the best fixed linear filter of `taps` taps starting at `delay` could reach on the
    far-only regions (least squares on the echo alone, so noise and near voice do not count)."""
    far = scene["far"]
    echo = scene.get("echo")
    if echo is None:
        return float("nan")
    regions = [r for r in scene["truth"]["regions"] if r["kind"] == "far"]
    # Fit on frequency domain least squares per bin would be heavier; time-domain LS with a
    # decimated column set is enough for a ceiling.
    X, y = [], []
    for r in regions:
        for s in range(r["start"] + delay + taps, r["end"], 7):
            X.append(far[s - delay - taps + 1: s - delay + 1][::-1])
            y.append(echo[s])
    X, y = np.array(X), np.array(y)
    w, *_ = np.linalg.lstsq(X, y, rcond=None)
    res = y - X @ w
    return Q.db(np.mean(y ** 2) / np.mean(res ** 2))
