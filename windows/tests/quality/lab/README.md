# Echo lab

A fast loop for echo removers. It calls `sonora_ffi.dll` and `liblocalvqe.dll` from Python
with `ctypes`, the way `SonoraBackend` and `LocalVqeBackend` drive them, and scores the
output with the quality suite's own functions in `../quality.py`. One scene takes about
1.5 s instead of a 15 to 20 minute suite run, and it reproduced the suite's signal scores
for `sonora` and `fast` exactly. It was used for `../sonora-delay-2026-11-11.md`.

It needs the suite's recordings (`py quality.py prepare`) and the same Python packages as
the suite. Run the scripts with `py -P` so the user's site packages load and the current
folder does not.

The DLLs and models come from `windows/native`. `SONORA_DLL` (a path to a DLL) and
`LOCALVQE_DIR` (a folder with `liblocalvqe.dll` and the two `.gguf` models) point the lab at
another build.

Most scripts take a **work directory** as their first argument. Caches and outputs go
there, never into the repository; `%USERPROFILE%\Amanu Quality Set\lab` is a good place.
The AEC Challenge clips that `fetch_aecsyn.py` fetched are in its `aecsyn` folder, and the
Amanu build that knows the `sonora-delay-*` echo models (for `wer.py`) is
`%USERPROFILE%\Amanu Quality Set\measure-sonora-delay\Amanu.exe`.

## Files

| File | What it is |
|---|---|
| `lab.py` | Loads the suite's recordings, builds new scenes (other rooms, a direct-path room with a set DRR, no speaker distortion), and scores ERLE, double-talk SI-SDR gain and voice level. |
| `sonora.py` | `ctypes` harness for `sonora_ffi.dll`: tuning strings, linear output, external delay, `wap_aec3_debug`. |
| `localvqe.py` | `ctypes` harness for `liblocalvqe.dll`: the whole-signal delay prime and the `fast` / `thorough` models. |
| `variants.py` | Every echo remover under test, as a function `(mic, far) -> cleaned mic`. `bw60-floor50` and its siblings are what became the `sonora-delay-*` echo models. |
| `t7.py` | Signal scores of variants over all 40 mixed calls, cached per call: `py -P t7.py <work dir> <variant,...>`. |
| `summ.py` | Prints the cached scores of `t7.py` per variant. |
| `wer.py` | WER without a rebuild: writes each lab output as a recording and has Amanu transcribe it with its own echo removal off (`AMANU_MEASURE_ECHO=off`). `py -P wer.py <work dir> <Amanu.exe> <variant,...> [scene regex]`. Matched the suite within about 0.6 WER points. |
| `cmp.py` | Scores the C++ AEC3 reference outputs of the `aec3-upstream` workflow (branch `build-sonora`) against the lab's variants. |
| `fetch_aecsyn.py` | Downloads 40 clips of the AEC Challenge synthetic test set (clean near end) into a folder that holds its `meta.csv`. |
| `table.py` | The write-up's table from a suite run's `results.json`. |
| `t1.py` … `t6.py`, `t8.py`, `t9.py` | The one-off experiments behind the write-up: the linear filter's ceiling per room (`t1`), filter length and output (`t2`), cancellers on built scenes (`t3`), AEC3's delay changes (`t4`, `t6`), the delay prime's speed (`t5`), `aec-real` (`t8`) and the per-window primes (`t9`). |
| `asr.py` | The WER loop as functions: transcribe a lab output or run one of Amanu's own echo models on a recording, cached per call, three at once; a paired bootstrap over calls. `AMANU_LAB_EXE` names another build. |
| `oracle.py`, `headroom.py` | The ceiling study: the engine on the calls with echo, noise or both removed perfectly, and the headroom table. |
| `denoise.py`, `d1.py` | GTCRN and DPDFNet through `sherpa_onnx`, and their WER on my voice with noise. |
| `heldout.py`, `heldout-manifest.json`, `heldout-sha256.txt` | The held-out set: 52 calls with clock drift, device changes, other rooms, noises and speech, frozen before any tuning. `plan`, `build`, `hash`. |
| `ho.py`, `hg.py`, `hs.py` | Held-out WER of Amanu's models (`amanu:`) or lab variants (`lab:`), the same by kind of call with paired intervals, and signal scores. |
| `solve.py` | The offline echo solver: delay, drift and device tracker, the least-squares fit per STFT bin, the residual suppressor. The app's `OfflineEchoSolver.cs` is its port. |
| `pipeline.py` | The candidates built from `solve.py` and `denoise.py`, and the speech gate (the app's `SpeechGate.cs`). |
| `sw.py` | Suite WER of lab variants: `run:<variants>`, `show:<tags> [baseline]`. |
| `m1.py`, `g1.py` | Why the engine fails in loud noise (noise turned down in parts of the call), and the denoisers' output level inside and outside my speech. |
| `s1.py`, `s2.py`, `s3.py`, `p1.py` | Signal scores of the solver on suite calls, the tracker against the manifest's truth, block-wise against whole-call fits on the long calls, and the app's output against the lab's. |
| `NOTES.md` | The notebook of the work behind `../echo-solver-2026-11-11.md`. |

## Scoring pitfall

Each remover has a constant latency: AEC3 suppressed +128 samples, its linear output +64,
LocalVQE `fast` −256, `thorough` 0. Score double talk at that fixed lag (`align_near`
finds it where only the near end talks). Lining the output up by correlation over a
far-only start picks a wrong lag and makes the voice look about 12 dB quieter than it is.
