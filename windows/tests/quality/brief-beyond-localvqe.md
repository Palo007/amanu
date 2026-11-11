# Brief: beat LocalVQE on my transcript, on this CPU, without giving up

You are the strongest model available, working alone for as long as it takes in the Amanu
repository on the maintainer's Windows PC. The smaller models and the earlier sessions got
the echo stage to here. Your job is to go clearly past it, with methods they did not try,
and to prove the gain on calls that were not used to tune it.

Write every status message to the user in ASD-STE100 Simplified Technical English: short
sentences, one fact per sentence. Commit messages and code comments follow `CLAUDE.md`.

## 1. The goal, in one line

Lower **my WER** (the words *I* say into the mic, after the far end's voice leaks back
through my speakers) on the quality suite's mixed calls, with an echo/clean-up stage that
runs on a Ryzen 7 5800H with no NVIDIA GPU, faster than LocalVQE `fast`, and without
losing what the current best variants already do well.

## 2. Where things stand (2026-11-11)

Read these first, in full:

- `windows/tests/quality/sonora-delay-2026-11-11.md` — the latest write-up. Why AEC3's
  filter failed (its delay estimate jumped ~90 times in 150 s and each jump reset the
  filter), how an external delay, a 256 ms filter, a 60 s warm-up on the stretch with the
  most far-end speech and a gain floor fixed it, and what that result does **not** show.
- `windows/tests/quality/results-2026-11-11-sonora-delay.md` — the full grid per noise,
  SNR and SER.
- `windows/tests/quality/sonora-tuning-2026-11-10.md` — the scoring error (per-region lag)
  and the suppressor tunings that did not help.
- `windows/tests/quality/README.md`, `windows/native/sonora/README.md`,
  `windows/native/localvqe/README.md`, `windows/BUILDING-HERE.md`, `CLAUDE.md`,
  `docs/pitfalls.md`.

Work on branch `claude/sonora-doubletalk-e44253` (worktree
`.claude/worktrees/sonora-doubletalk-e44253`, head `3a2156c`). It is not merged into
`master`.

The suite's 40 mixed calls (Slovak FLEURS speech, rooms A and B, SER +5/−5/−15 dB,
office/cafe noise over SNR clean…−5 dB, plus street and living room):

| Echo remover | My WER % | No noise % | Echo words | ERLE real (dB) | DT voice (dB) | DT SI-SDR gain (dB) | Echo time (s) |
|---|---|---|---|---|---|---|---|
| off | 58.5 | 46.1 | 603 | 0.0 | −0.1 | 0.0 | 0 |
| sonora (default) | 38.9 | 33.5 | 4 | 21.7 | −6.0 | 3.9 | 39 |
| fast (LocalVQE 2.7K) | 28.7 | 11.5 | 0 | 7.3 | −0.0 | 8.1 | 511 |
| thorough (LocalVQE 200K) | 25.4 | 14.3 | 2 | 19.4 | −1.8 | 11.0 | 929 |
| sonora-delay-floor | **23.6** | 11.4 | 5 | 21.7 | −2.2 | 8.4 | 76 |
| sonora-delay-nearend-floor | 24.0 | **11.1** | 1 | 21.7 | −1.5 | 8.7 | 76 |

Clean read speech with no echo and no noise transcribes at 6.8 % WER (FLEURS) and 7.0 %
(VoxPopuli); the far end's own WER is 8.1 %. That is the floor the engine sets.

**Read the grid before you choose a method.** For `sonora-delay-floor`, 46 % of all its
word errors sit in 6 of the 40 calls: cafe noise at SNR 0 and −5 dB, where *every* remover
scores 40 to 100 %. In most other cells the best variants are already within a few points of the clean
floor. A better echo canceller alone cannot fix a call that the noise ruins. Find out how
much of the error is echo, how much is noise, and how much is the engine, before you tune.

## 3. The machine and the tools

- CPU: AMD Ryzen 7 5800H, Zen 3, 8 cores / 16 threads, AVX2 and FMA, no AVX-512.
- GPU: integrated Radeon Vega 8 (512 MB carve-out, shared RAM). Vulkan works
  (`ggml-vulkan.dll` already ships in `artifacts/publish/local-runtime`); DirectML via
  `onnxruntime-directml` is possible. No NVIDIA, no CUDA. Measure before you assume the
  iGPU is faster: for small models the CPU often wins.
- RAM 15.4 GB. **Disk: only about 15 GB free on C:**, and the test data
  (`%USERPROFILE%\Amanu Quality Set`) already takes 7.2 GB. A full suite run keeps the
  cleaned audio of every call (about 0.9 GB per four echo models). Clean up your own
  caches and old runs.
- Python 3.13 with numpy 2.3.5, scipy 1.16.3, soundfile, sherpa_onnx 2.0.1 (it carries
  onnxruntime and has offline speech denoisers). You may `pip install` more.
- .NET 10 SDK in `%USERPROFILE%\.dotnet` only (see `BUILDING-HERE.md` for the PATH).
- **No CMake, no MSVC.** Do not install a C++ toolchain. GCC is in
  `%USERPROFILE%\.tools\w64devkit`. Native DLLs (`sonora_ffi.dll`, `liblocalvqe.dll`) are
  built by GitHub Actions on the `build-sonora` and `build-localvqe` branches. Ask the user
  once before the first push to a build branch.
- **Never delete `windows/artifacts/publish`.** Its native runtimes cannot be rebuilt here.
- Quit Amanu before a suite run (`Get-Process Amanu`).

A method that ships does not need a native build: C# on .NET 10 with
`System.Numerics.Tensors` / `Vector<T>` SIMD and an FFT can run an STFT-domain canceller
or a small ONNX model (via the onnxruntime NuGet package) inside the app. Weigh that path
against a patched DLL.

## 4. The fast lab

The ctypes lab that made the last round fast is in `windows/tests/quality/lab/`; read its
`README.md` first. Put its caches and outputs in a work directory outside the repository
(`%USERPROFILE%\Amanu Quality Set\lab`). Two things from the last round are kept there:

- `%USERPROFILE%\Amanu Quality Set\measure-sonora-delay\Amanu.exe` — the build that made
  the `sonora-delay` suite run. It knows every `sonora-delay-*` echo model; pass it to
  `wer.py` and to `quality.py --exe`. The `artifacts\publish` build does not have them.
- `%USERPROFILE%\Amanu Quality Set\lab\aecsyn` — the 40 AEC Challenge synthetic clips
  that `fetch_aecsyn.py` downloaded.

What the lab does: it calls `sonora_ffi.dll` and `liblocalvqe.dll` from Python the way
`SonoraBackend` and `LocalVqeBackend` do, scores with the suite's own `quality.py`
functions, and runs one scene in ~1.5 s instead of a 15–20 min suite run. For WER without
a rebuild it writes the lab output as a recording and runs
`Amanu.exe --measure-transcription` with `AMANU_MEASURE_ECHO=off`; that matched the suite
within ~0.6 points. Run scenes in parallel on all 16 threads.

Pitfalls that already cost hours:

- Each remover has a constant latency (AEC3 suppressed +128 samples, its linear output
  +64, LocalVQE `fast` −256, `thorough` 0). Score double talk at that fixed lag, found
  where only the near end talks. Aligning by correlation over a far-only start picks a
  wrong lag and makes the voice look ~12 dB quieter.
- A warm-up must be one contiguous stretch. Far-active frames joined from all over the
  file made AEC3 adapt wrongly at every join.
- `aec-real` joins 40 different devices. One delay for the whole file is wrong there.

## 5. First, measure the ceiling

Do this before any new method. The lab has `near.wav` (my clean voice) for every mixed
call, so you can build oracle inputs and transcribe them:

1. **No echo, same noise:** near + the same noise at the same SNR, no echo. This is the
   best any echo canceller can reach. The gap between it and the best variant, per cell,
   is the echo headroom.
2. **No echo, no noise:** near alone (the engine's floor per call).
3. **Ideal masks:** an oracle ratio mask that removes the echo only, and one that removes
   echo and noise, applied to the mic in the STFT domain. These show whether a mask-based
   stage can reach the ceiling, and what a gain floor costs.

Write the headroom table (per noise type, SER and SNR) into your notes. It decides where
your effort goes. If the echo headroom is small and the noise headroom is large, the next
gain is a clean-up stage that the engine likes, not a better canceller.

The remover may never see `near.wav`, the truth text or the noise track. They are for the
oracle and the scores only.

## 6. Ideas to try — do not stop at the first family that fails

Amanu processes a **finished recording**. It has the whole mic track and the whole call
track before it starts. Real-time cancellers, LocalVQE and AEC3 included, throw that
advantage away. Most of the ideas below use it.

**A. Solve the echo path, do not chase it.**
- Estimate the echo path in closed form over the whole file: per STFT bin, a multi-tap
  convolutive transfer function (CTF) fitted by least squares on far-only and
  low-near-activity frames. No step size, no convergence time, no delay jumps. Cover the
  tail: room B rings for 0.55 s, so plan for 0.6–0.8 s of taps at a hop that keeps the
  solve cheap.
- Make it robust to double talk: iteratively reweighted least squares (Huber or Cauchy
  weights on the residual), or weights from a near-end activity estimate.
- Re-fit per segment (for example 30–60 s, overlapping) and detect where the path or the
  delay changes; use the per-window delay prime that already exists.
- Estimate clock drift between the mic and the call track over the whole file and
  resample the call track before the fit. Drift is not handled at all today.
- Run forward and backward and combine, or use future frames in the post-filter; nothing
  forces you to be causal.

**B. Model the speaker.** A Hammerstein front (a memoryless polynomial or tanh basis on the
far-end signal) fitted jointly with the linear path. In the suite the nonlinear part was
25.6 dB below the echo, so test it on `aec-real` and the AEC Challenge clips, where real
speakers distort.

**C. Clean up for the engine, not for ears.** The 0.5–0.7 gain floor proved that the
engine prefers less suppression. Design the residual echo and noise gain with that in
mind: soft masks with floors that depend on band and on the noise level, blending a
fraction of the unprocessed mic back in, keeping the speech band intact. Test each by WER,
never by ERLE alone.

**D. Small neural models that run on this CPU.** Check license (MIT, Apache-2.0 or BSD
only for anything that ships), size and CPU speed first:
- LocalVQE `thorough`'s neural mask on top of a *good* linear filter (yours or AEC3's with
  the fixed delay) — the hybrid that the last brief named but did not build.
- Tiny residual-echo or Kalman-style models (for example NKF-AEC, DTLN-aec), and denoisers
  (for example GTCRN through sherpa_onnx, DeepFilterNet). Many of them hurt ASR on clean
  speech: measure the no-noise column every time.
- Training a tiny mask network yourself on CPU is allowed and may be the strongest move:
  hours of 16-thread training are cheap. Train only on data that is not the test set
  (FLEURS *train/dev*, other noise than DEMAND's test recordings, generated rooms with
  other seeds), and write down exactly what you trained on.

**E. Use the engine in the loop.** Offline, you can afford more than one transcript:
- Transcribe two or three cleaned versions and combine them (ROVER-style word voting, or
  a per-segment choice by the engine's confidence).
- Choose per segment between cancellers by a signal score you can compute without the
  truth (residual echo estimate, near-end activity, noise level).
- Strengthen the transcript echo filter with the far end's own transcript.
Count the extra engine time honestly.

**F. Fix the known gaps in the current best variant.** From the write-up's "What this does
not show": clock drift; a call that changes device part-way (a quarter of the call can run
with a wrong delay today); the ~300 ms of stale render history after the warm-up; every
worker repeating the prime and the warm-up when `AMANU_ECHO_WORKERS` > 1; the record
naming the variant when it fell back to plain `sonora`.

**G. Anything else you can justify.** If your headroom table points somewhere this list
does not, go there.

## 7. How to work so that you do not give up early

- Keep a lab notebook (`windows/tests/quality/lab/NOTES.md`): for each attempt the
  hypothesis, the change, the numbers, and the conclusion. Commit it as you go.
- **Do not drop an idea until you know why it failed.** Instrument it, as the AEC3 delay
  counter did. The last round's breakthrough came from asking "why" four times after the
  suppressor tunings all failed.
- Try at least one serious idea from each of families A, C, D and E before you conclude
  that the bar cannot be reached. If you conclude that, show it with the headroom table.
- When you are stuck, go back to the grid: which cells hold the error, and which part
  (canceller, suppressor, noise, engine) limits each one.
- Escalate in cost: lab signal scores (seconds) → lab WER on a subset (minutes) → lab WER
  on all calls → a suite run (`py quality.py run --name <run> --only <regex>`) → the full
  suite (about 3 hours) only for finalists.
- Do not ask the user for permission for lab work. Ask only before: a push to a build
  branch, a download over 1 GB, a change to the default `echo_model`, a merge into
  `master`, and shipping anything under a new license.
- Do not game the score. No tuning on the held-out set. No use of truth, `near.wav` or the
  noise track inside a remover. No per-call settings chosen by the score.

## 8. A held-out test, because the last round had none

The current floors and warm-up were chosen on the same 40 calls they were scored on.
Before you tune anything, build a **held-out set** with `quality.py`'s mixer (or the lab's
`direct_room`) and freeze it:

- other speakers and sentences (FLEURS dev, not test), other seeds;
- rooms with a strong direct path (a laptop-like DRR of +6 dB) as well as the suite's
  reverberant ones, and real room responses where the license allows;
- clock drift of ±50 to ±200 ppm, a device change in the middle of a call, and long calls
  (10 to 20 minutes), because real meetings are long and warm-ups behave differently;
- the AEC Challenge synthetic double talk the last round already fetched
  (`fetch_aecsyn.py`, already in `%USERPROFILE%\Amanu Quality Set\lab\aecsyn`).

Tune on the suite's 40 calls. Report on the held-out set. Use a paired bootstrap over calls
for a 95 % confidence interval on every WER difference you claim.

The two real calls in `windows/native/sonora/README.md` (1000 and 1021, 14 and 16 minutes)
have reference word counts; ask the user where their audio and references are if you need
a check on real meetings.

## 9. The bar

A new `echo_model` value is a success when, on the **held-out set**:

- my WER is at least 2 points below the better of `thorough` and `sonora-delay-floor`,
  with the 95 % interval excluding zero;

and on the **suite**, none of these get worse than the current best variant:

- my WER without noise ≤ 11.5 %; echo words ≤ 2; ERLE on `aec-real` ≥ 19 dB;
  double-talk voice level ≥ −1.5 dB; clean FLEURS and VoxPopuli WER unchanged;
- echo time ≤ 511 s for the suite (LocalVQE `fast`); aim for ≤ 150 s.

If you reach the bar, keep going for as long as the headroom table says there is real room
left. If the bar proves out of reach, the deliverable is the proof: the headroom table, the
families tried, the numbers, and the best variant you found.

## 10. Deliverables

- New `echo_model` variants behind the setting, the default unchanged until the user
  agrees.
- A write-up in the style of `sonora-delay-2026-11-11.md`
  (`windows/tests/quality/<topic>-<date>.md`) with a "What this does not show" section,
  and the suite report beside it.
- New lab code and the notebook in `windows/tests/quality/lab/`.
- Updated `windows/native/*/README.md` for any rebuilt DLL (source commit, workflow run,
  size, SHA-256, license).
- `dotnet test Amanu.Windows.slnx` green.
- Commits in the repository's voice (one prose sentence on what changed and why; read
  `git log` first).
