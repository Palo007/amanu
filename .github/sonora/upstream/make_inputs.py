"""Builds the inputs for the C++ against Rust AEC3 comparison from public AEC Challenge
clips (synthetic set, test split, clean near end), so the same files can be run by the
C++ reference here and by Sonora on the Windows PC.

  aec-<id>-cold   the challenge's own near-end mic clip: echo plus near speech
  aec-<id>-warm   10 s of that clip's echo alone, then the clip, so the filter has converged
  room-A, room-B  the Amanu quality suite's two computed rooms (80 ms / 0.30 s and
                  180 ms / 0.55 s, soft-clipping speaker) on 20 far and 20 near clips:
                  40 s far only, 20 s near only, 40 s both, at SER -5 dB
"""
import json, sys, urllib.request
from pathlib import Path
import numpy as np, soundfile as sf
from scipy import signal

IDS = "222 115 268 406 30 51 489 341 74 251 369 33 327 151 24 66 286 281 46 175 87 347 260 45 351 109 136 375 414 352 476 472 380 233 388 439 43 111 157 235 ".split()
BASE = "https://media.githubusercontent.com/media/microsoft/AEC-Challenge/main/datasets/synthetic"
RATE = 16000
out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)

def get(folder, name):
    req = urllib.request.Request(f"{BASE}/{folder}/{name}", headers={"User-Agent": "amanu-aec3-compare"})
    data, rate = sf.read(__import__("io").BytesIO(urllib.request.urlopen(req, timeout=120).read()), dtype="float64")
    assert rate == RATE
    return data

def save(name, **tracks):
    for k, v in tracks.items():
        np.asarray(v, dtype=np.float32).tofile(out / f"{name}.{k}.f32")

clips = {}
for i in IDS:
    clips[i] = {k: get(f, n) for k, f, n in (("far", "farend_speech", f"farend_speech_fileid_{i}.wav"),
                                             ("echo", "echo_signal", f"echo_fileid_{i}.wav"),
                                             ("near", "nearend_speech", f"nearend_speech_fileid_{i}.wav"),
                                             ("mic", "nearend_mic_signal", f"nearend_mic_fileid_{i}.wav"))}
meta = {}
for i, c in clips.items():
    n = min(len(c["far"]), len(c["mic"]), len(c["echo"]), len(c["near"]))
    far, mic, echo, near = (c[k][:n] for k in ("far", "mic", "echo", "near"))
    # The mic is near*scale + echo; recover the scaled near truth by least squares.
    scale = float(np.dot(mic - echo, near) / (np.dot(near, near) + 1e-20))
    save(f"aec-{i}-cold", far=far, mic=mic, near=near * scale)
    save(f"aec-{i}-warm", far=np.concatenate([far, far]), mic=np.concatenate([echo, mic]),
         near=np.concatenate([np.zeros(n), near * scale]))
    meta[f"aec-{i}-cold"] = {"regions": [{"kind": "double", "start": 0, "end": n}], "echo_only": None}
    meta[f"aec-{i}-warm"] = {"regions": [{"kind": "far", "start": n // 2, "end": n}, {"kind": "double", "start": n, "end": 2 * n}]}

def active_power(x):
    f = RATE // 50; k = len(x) // f
    p = (x[: k * f].reshape(k, f) ** 2).mean(axis=1)
    return float(p[p > p.max() * 1e-3].mean() + 1e-20)

def room_response(rt60, latency_ms, rng):
    delay = int(RATE * latency_ms / 1000); length = int(RATE * min(rt60 * 1.2, 0.8))
    t = np.arange(length) / RATE
    tail = rng.standard_normal(length) * np.exp(-6.9 * t / rt60) * 0.25
    tail[: int(RATE * 0.003)] = 0
    h = np.zeros(delay + length); h[delay] = 1.0; h[delay:] += tail
    return h / np.sqrt(np.sum(h ** 2))

def speaker_distortion(x):
    peak = np.max(np.abs(x)) + 1e-12
    return np.tanh(1.5 * x / peak) / np.tanh(1.5) * peak

def norm(a): return a * 10 ** (-26 / 20) / np.sqrt(active_power(a))
ids = list(clips)
def run_of(kind, which, seconds):
    parts, total = [], 0
    for i in which:
        a = norm(clips[i][kind]); parts += [a, np.zeros(RATE // 4)]; total += len(a) + RATE // 4
        if total >= seconds * RATE: break
    return np.concatenate(parts)[: seconds * RATE]
far = np.concatenate([run_of("far", ids[:10], 40), np.zeros(20 * RATE), run_of("far", ids[10:20], 40)])
near = np.concatenate([np.zeros(40 * RATE), run_of("near", ids[20:30], 20), run_of("near", ids[30:40], 40)])
regions = [{"kind": "far", "start": 0, "end": 40 * RATE}, {"kind": "near", "start": 40 * RATE, "end": 60 * RATE},
           {"kind": "double", "start": 60 * RATE, "end": 100 * RATE}]
for room, (lat, rt60, seed) in {"A": (80, 0.30, 11), "B": (180, 0.55, 23)}.items():
    echo = signal.fftconvolve(speaker_distortion(far), room_response(rt60, lat, np.random.default_rng(seed)))[: len(far)]
    echo *= np.sqrt(active_power(near) / active_power(echo) * 10 ** (5 / 10))
    mic = near + echo
    s = min(1.0, 0.95 / np.max(np.abs(mic)))
    save(f"room-{room}", far=far, mic=mic * s, near=near * s)
    meta[f"room-{room}"] = {"regions": regions, "latency_ms": lat}
(out / "meta.json").write_text(json.dumps(meta, indent=1))
print(len(meta), "inputs")
