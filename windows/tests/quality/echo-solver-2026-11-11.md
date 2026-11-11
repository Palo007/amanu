# An echo remover that solves the call offline, 2026-11-11

Follows `sonora-delay-2026-11-11.md` and answers `brief-beyond-localvqe.md`: lower my WER
on the mixed calls with an echo and clean-up stage that runs on the Ryzen 7 5800H faster
than LocalVQE `fast`. Three new `echo_model` values came out of it: `solve`, `solve-floor`
and `solve-gate`. The default is unchanged. The suite run is `sonora-tuning` again, resumed
with the build of this branch for the three new models (full report:
`results-2026-11-11-echo-solver.md`); the held-out set is new (`lab/heldout.py`). The lab
notebook with every step is `lab/NOTES.md`.

## Result

Held-out set, 52 calls that no setting was chosen on (FLEURS dev speech, four other
DEMAND rooms, clock drift in 22 calls, a change of device in 6, four calls without echo,
four calls of 12 minutes), run through the app's own echo models:

| Echo remover | My WER % | No noise | SNR 20..5 | SNR 0, −5 | Echo words | Echo time (s) | Against `thorough`, 95 % interval |
|---|---|---|---|---|---|---|---|
| thorough | 24.7 | 24.3 | 22.2 | 36.5 | 119 | 2004 | |
| sonora-delay-floor | 31.1 | 21.3 | 28.8 | 56.4 | 203 | 147 | +6.5 [+0.1, +13.2] |
| solve-floor | 13.0 | 7.4 | 10.8 | 31.4 | 1 | 175 | −11.7 [−16.6, −6.8] |
| solve-gate | 11.8 | 6.3 | 10.3 | 27.2 | 1 | 1320 | −12.9 [−17.9, −7.9] |

By kind of call (my WER %; the difference is `solve-gate` against `thorough`, paired
bootstrap over calls):

| Calls | thorough | sonora-delay-floor | solve-floor | solve-gate | Difference |
|---|---|---|---|---|---|
| steady: one device, no drift (16) | 13.8 | 14.1 | 9.8 | 10.0 | −3.8 [−6.4, −1.5] |
| clock drift 50 to 200 ppm (22) | 30.9 | 41.7 | 15.8 | 13.4 | −17.5 [−24.6, −10.7] |
| change of device (6) | 30.9 | 62.0 | 24.0 | 21.4 | −9.5 [−15.8, −4.5] |
| no echo (4) | 6.6 | 7.6 | 7.1 | 6.8 | +0.2 [−0.8, +0.6] |
| 12 minutes (4) | 28.1 | 28.3 | 10.4 | 9.7 | −18.4 [−31.8, −4.7] |

The quality suite, 40 mixed calls and `aec-real`:

| Echo remover | My WER % | My WER %, no noise | Echo words | ERLE synthetic (dB) | ERLE real (dB) | Double-talk voice (dB) | Double-talk SI-SDR gain (dB) | Echo time (s) |
|---|---|---|---|---|---|---|---|---|
| fast | 28.7 | 11.5 | 0 | 11.4 | 7.3 | −0.0 | 8.1 | 511 |
| thorough | 25.4 | 14.3 | 2 | 22.0 | 19.4 | −1.8 | 11.0 | 929 |
| sonora-delay-floor | 23.6 | 11.4 | 5 | 15.6 | 21.7 | −2.2 | 8.4 | 76 |
| sonora-delay-nearend-floor | 24.0 | 11.1 | 1 | 14.9 | 21.7 | −1.5 | 8.7 | 76 |
| solve | 25.5 | 10.2 | 1 | 13.8 | 21.7 | −0.0 | 11.9 | 66 |
| solve-floor | 21.1 | 10.2 | 4 | 15.6 | 21.7 | −0.4 | 12.5 | 90 |
| solve-gate | 18.4 | 10.2 | 3 | 30.1 | 33.7 | −0.4 | 14.6 | 427 |

My WER % on the suite by noise level:

| Echo remover | No noise | SNR 20..5 | SNR 0 | SNR −5 |
|---|---|---|---|---|
| thorough | 14.3 | 19.2 | 29.3 | 52.0 |
| sonora-delay-floor | 11.4 | 15.3 | 30.6 | 54.4 |
| solve | 10.2 | 13.2 | 40.1 | 62.0 |
| solve-floor | 10.2 | 12.6 | 24.7 | 55.6 |
| solve-gate | 10.2 | 12.5 | 20.7 | 43.3 |
| ideal removal of echo and noise (lab oracle) | 9.3 | 9.7 | 10.1 | 9.6 |

Against the bar of the brief (section 9), for `solve-gate` and `solve-floor`:

| Condition | Limit | solve-gate | solve-floor |
|---|---|---|---|
| Held-out: my WER below the better of `thorough` and `sonora-delay-floor` | 2 points, interval excludes zero | −12.9 [−17.9, −7.9] | −11.7 [−16.6, −6.8] |
| Suite: my WER without noise | ≤ 11.5 % | 10.2 | 10.2 |
| Suite: echo words | ≤ 2 | **3** | **4** |
| Suite: ERLE on `aec-real` | ≥ 19 dB | 33.7 | 21.7 |
| Suite: double-talk voice level | ≥ −1.5 dB | −0.4 | −0.4 |
| Suite: clean FLEURS and VoxPopuli WER | unchanged | unchanged (see below) | unchanged |
| Suite: echo time, 40 calls | ≤ 511 s, aim ≤ 150 s | 427 (481 with `aec-real`) | 90 (98) |

The one limit not met is the echo words. All of them are single short words ("v",
"tri") in cafe noise at 0 dB. The lab's oracle that gives the engine my voice and the
noise with **no echo at all** gets the same "v" in the same call (`lab/NOTES.md`,
section 8), so they are the engine's errors on noisy speech that happen to be words the
far end also said, not echo that came through. `sonora-delay-floor` has 5 of the same kind.

The clean sets have no call track, so the echo stage does not run on them with any
`echo_model`; they were not run again.

### What this does not show

- The held-out calls are simulated like the suite's: an image-source room, a speaker
  model, DEMAND noise, a constant drift. No real recording with a real second device was
  scored for WER. `aec-real` is real, but it joins 40 devices, has no near speech, and
  goes to plain Sonora.
- The drift in the held-out set is constant over a call and at most 200 ppm, and a call
  has at most one change of device. A drift that wanders, or three devices in one call,
  were not tested; the tracker keeps at most three delay lines.
- `thorough` and `sonora-delay-floor` lose most of their echo removal under drift, so
  the large held-out difference is mostly "handles drift" against "does not". Without
  drift (16 steady calls) the difference is −3.8 [−6.4, −1.5] points.
- The gate's values (25 dB under the denoiser's 99th percentile, 0.3 s, a floor of 0.15)
  were set once from level statistics on four suite calls and not tuned on WER, but the
  suite is still where the gate was invented. Its held-out gain over `solve-floor` is
  −1.2 [−2.2, −0.3] points; on the suite it is −2.7.
- The gate judges my speech by the level of a denoiser's output over the whole call. A
  person who talks for less than about 1 % of a call, or a second voice in my room, were
  not tested. A missed sentence is turned down by 16 dB, not removed.
- The echo words limit of the bar is missed by one word (see above).
- Memory: the solver holds both tracks and three more arrays of the same length as
  32-bit floats, about 2.3 GB for two hours. The longest call scored is 12 minutes. The
  fit runs in blocks of five minutes, so the spectra stay at about 100 MB.
- The suite ran with `--force` while an idle Amanu was open, so the echo times can be a
  little high. On an idle machine one call of 156 s takes 1.7 s in the solver and 7.0 s
  in the gate.
- The aim of 150 s for the suite is met only without the gate.

## 1. Where the error was: the ceiling

`lab/oracle.py` gives the engine the suite's calls with the echo, the noise or both
removed perfectly (my WER %, 40 calls):

| Input | All 40 | No noise | SNR 20..5 | SNR 0 | SNR −5 |
|---|---|---|---|---|---|
| my voice alone (engine floor) | 9.5 | 9.5 | 9.5 | 9.5 | 9.5 |
| no echo, noise left in | 24.6 | 9.5 | 12.5 | 38.7 | 61.2 |
| ideal echo and noise mask on the mic | 9.7 | 9.3 | 9.7 | 10.1 | 9.6 |
| sonora-delay-floor (suite) | 23.6 | 11.4 | 15.3 | 30.6 | 54.4 |

A perfect echo canceller that leaves the noise in scores 24.6, worse than the best remover
of the last round. The echo headroom on the suite was about 1.7 points; the noise headroom
14.8 points, all of it in the 14 calls at SNR 0 and −5. The suite's calls also have no
clock drift, which hides the largest weakness of the delay-primed filter: on the held-out
calls with drift the lab's copy of it removes 1.8 dB of echo where it removes 12.6 dB
without.

## 2. Family A: solve the echo path (`OfflineEchoSolver.cs`, `lab/solve.py`)

With the whole file at hand nothing has to adapt:

1. **Delay, drift, device.** Phase-weighted cross-correlations over 2 s windows give delay
   points; a vote over slopes from −300 to 300 ppm finds up to three lines (one per device).
   The call track is then warped onto the mic's clock with a windowed sinc, and 8 s windows
   refine the line. Against the manifest's truth the delay is within 0.02 ms and the drift
   within 0.1 ppm in dry rooms.
2. **The fit.** Per STFT bin (frames of 256 ms, hop 64 ms), a filter of 12 taps and one
   non-causal tap (0.75 s) by weighted least squares, the weights one over the smoothed
   residual power, two rounds. So the frames where I talk count less, and double talk
   does not disturb the filter. One fit per five minutes of a stretch.
3. **Where two devices meet** the border goes to the frame that leaves the least residual.
4. **Many devices.** A recording with clear delay peaks in most windows but no line through
   them (`aec-real`) is passed to plain Sonora, as the delay-primed models do.

On the suite the linear stage alone cleans my voice in double talk by 11.9 dB (AEC3's
linear filter with a fixed delay: 7.5 dB; `thorough`: 11.0 dB) and does not lower it.
It takes my WER without noise from 11.4 to 10.2, most of the echo headroom.

`solve-floor` then turns down what the filter left: per bin, the share of the echo estimate
that stays, measured in the far-only frames, with a gain floor of 0.15. It adds only 2 dB
of ERLE, but 15 points of WER at SNR 0. Section 3 is why.

## 3. Family C: what the engine needs is quiet between my sentences

`lab/m1.py` gives the engine my voice and the noise without echo, and turns the noise down
in parts of the call only (my WER %, one call per noise at SER −5):

| Input | cafe 0 | cafe −5 | living 0 | office −5 | street 0 |
|---|---|---|---|---|---|
| noise left in | 72.9 | 97.2 | 44.6 | 26.0 | 17.1 |
| noise at 0.15 only where the far end talks alone | 27.1 | 74.6 | 19.2 | 8.3 | 13.3 |
| DPDFNet's output | 39.5 | 83.6 | 20.9 | 9.4 | 17.1 |
| ideal noise mask | 10.7 | 10.7 | 11.9 | 8.3 | 9.9 |

The noise over my own words is not what loses most of them. With my blocks untouched and
only the stretches where I am silent turned down, the cafe at 0 dB goes from 72.9 to 27.1.
The engine cuts the track into pieces by voice activity and normalizes each piece; loud
noise without speech in a piece costs the speech beside it. A denoiser's own output is
worse than the gated original: DPDFNet removes a quarter of my speech frames at cafe 0 dB.

## 4. Family D, used as a detector: the speech gate (`SpeechGate.cs`)

DPDFNet (baseline model, 2.3M parameters, 8.8 MB, Apache-2.0) runs through sherpa-onnx's
`OfflineSpeechDenoiser`, which the app already carries for the call's diarization. Outside
my speech its output is 60 to 70 dB under its 99th percentile, also in babble; GTCRN
(48K parameters) does not separate the two and cannot steer a gate.

The gate reads only the level of the denoiser's output: open where it is within 25 dB of
its 99th percentile for at least 0.3 s, plus 0.3 s on both sides; a gain of 0.15 elsewhere,
with ramps of 30 ms. What passes is the canceller's signal, not the denoiser's.

On the suite it takes `solve-floor` from 21.1 to 18.4 (SNR 0: 24.7 to 20.7; SNR −5: 55.6
to 43.3) and changes nothing without noise. Mixing half of the denoiser's output in gave
17.9 in the lab, inside the noise of the comparison, and is not in the app.

## 5. What is left

The suite's ceiling is 9.7; `solve-gate` is at 18.4. The gap is the noise over my words at
SNR 0 and −5 (cafe at −5 dB: 78 % wrong, ideal mask 10.7 %). Closing it needs a denoiser
whose output the engine likes better than the noisy original, or the engine in the loop
(family E: transcribe with and without the denoiser and keep, per sentence, the more
confident one). Neither was tried here.

The gate costs 7 s per 156 s of audio and does not speed up with more threads; a smaller
detector trained for this one decision would bring `solve-gate` near `solve-floor`'s time.

## Kept as a record

- `solve` alone (25.5) is worse on the suite than `sonora-delay-floor` (23.6) although it
  removes more echo: AEC3's suppressor also turns the noise down while the far end talks,
  and that, not its echo removal, is what the engine rewarded (section 3).
- DPDFNet's output after the delay-primed AEC3 gained nothing (24.5): AEC3's output is
  already damaged where the denoiser would work.
- The first form of the delay tracker followed the delay point by point and made false
  device changes under strong drift in reverberant rooms; the vote over slopes replaced it.
- A hop of half a frame in the fit is clearly worse than a quarter; frames of 64 ms cost
  sixteen times more than 256 ms for the same result.

## How to repeat it

```powershell
# the app's models on the held-out set (build this branch into its own folder first)
$env:AMANU_LAB_EXE = "<publish folder>\Amanu.exe"
py -P lab\heldout.py build                      # the 52 calls from the frozen manifest
py -P lab\ho.py "<work dir>" amanu:thorough,sonora-delay-floor,solve-floor,solve-gate
py -P lab\hg.py "<work dir>" thorough,sonora-delay-floor,solve-floor,solve-gate solve-gate thorough

# the suite, only the echo calls, resuming the run
py quality.py run --name sonora-tuning --only "^(echo-|aec-real)" --exe "<publish folder>\Amanu.exe"
py quality.py report
```

`solve-gate` needs `dpdfnet_baseline.onnx` in `windows/native/dpdfnet/` at build time
(`windows/native/dpdfnet/README.md`); the file is not in the repository. Without it the
model runs as `solve-floor` and says so in the log.
