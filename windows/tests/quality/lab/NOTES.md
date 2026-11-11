# Lab notebook: beyond LocalVQE

The brief is `../brief-beyond-localvqe.md`. Each entry: the hypothesis, the change, the
numbers, the conclusion. WER is my WER in percent; "lab WER" is `asr.py` (the lab's output
transcribed by `measure-sonora-delay\Amanu.exe` with its own echo removal off). The work
directory is `%USERPROFILE%\Amanu Quality Set\lab`.

## 0. Facts about the suite that decide where the error is (2026-11-11)

- The 40 calls hold only two sets of speech: room A (office, street; 178 reference words)
  and room B (cafe, living room; 176 words). A WER difference between two removers on the
  suite is a difference on two speakers' sentences, repeated under 20 conditions each.
- Six of my ten sentences are in the two near-only blocks, where the call is silent.
  Amanu passes the mic unchanged there. So 60 % of my words never meet an echo remover,
  and their errors are noise and engine only.
- One transcription takes 14 s on the Vulkan iGPU; three at once take 10 s each; six at
  once take 180 s each. `asr.py` lets three run.

## 1. The ceiling (oracle.py)

Hypothesis: most of the remaining error is noise, not echo.

Inputs made from the suite's own parts (`lab.build_scene` rebuilds every call to within
one 16-bit step): `near` my voice alone; `nn` my voice and the noise, no echo; `nn-irm`
the same through an ideal ratio mask that removes the noise; `irm-echo` and `irm-all`
the mic through an ideal mask that removes the echo, or echo and noise. STFT 512/128.

Result (`py -P headroom.py <work dir>`), my WER in percent on the suite's 40 calls:

| Input | All 40 | No noise | SNR 20..5 | SNR 0 | SNR -5 |
|---|---|---|---|---|---|
| engine floor (my voice alone) | 9.5 | 9.5 | 9.5 | 9.5 | 9.5 |
| no echo, ideal noise mask | 9.8 | 9.5 | 9.9 | 10.1 | 9.5 |
| no echo, noise left in | 24.6 | 9.5 | 12.5 | 38.7 | 61.2 |
| ideal echo mask on the mic | 24.6 | 9.3 | 12.3 | 38.7 | 61.9 |
| ideal echo and noise mask on the mic | 9.7 | 9.3 | 9.7 | 10.1 | 9.6 |
| sonora-delay-floor (suite) | 23.6 | 11.4 | 15.3 | 30.6 | 54.4 |
| sonora-delay-nearend-floor (suite) | 24.0 | 11.1 | 15.0 | 32.1 | 56.0 |
| thorough (suite) | 25.4 | 14.3 | 19.2 | 29.3 | 52.0 |
| fast (suite) | 28.7 | 11.5 | 15.4 | 43.8 | 70.1 |

Per noise (no echo, noise left in): office 8.3 down to SNR 5, 10.5 at 0, 26.0 at -5; cafe
13.6 / 14.1 / 23.2 / 72.9 / 97.2 at 20 / 10 / 5 / 0 / -5; living room 44.6 at 0; street
17.1 at 0. The errors in loud noise are deletions (113 of 177 words at cafe 0, 170 at -5):
the engine leaves my speech out. An ideal noise mask with a floor of 0.3 (10 dB) still gives
15.3 at cafe 0 and 31.6 at -5; with a floor of 0.5, 17.5 and 57.6.

Conclusion:
- A perfect echo canceller that leaves the noise in scores 24.6, worse than the best
  remover today (23.6), because AEC3's suppressor also cuts noise while the far end talks.
- Echo headroom: 1.6 to 1.9 points without noise (11.1 to 11.4 against 9.5) and 2.5 to
  2.8 points at SNR 20 to 5 (15.0 to 15.3 against 12.5). About 1.7 points over the suite.
- Noise headroom: 14.8 points (24.6 against 9.8). It sits in 14 calls at SNR 0 and -5.
- So the next gain on the suite is a noise clean-up that the engine likes, on the whole
  mic track, also where the call is silent. A better canceller is worth at most 2 points
  there. It is worth much more on calls with clock drift (section 3).

## 2. Denoisers on my voice with noise, no echo (d1.py)

Hypothesis: an off-the-shelf denoiser recovers part of the noise headroom and does not
hurt quiet speech.

`sherpa_onnx` 2.0.1 runs GTCRN (MIT, 48K parameters, 0.5 MB) and DPDFNet (Apache-2.0,
`dpdfnet_baseline` 2.3M parameters, 8.8 MB). Input: the oracle's `nn` (my voice and the
noise). WER per noise and SNR:

| Input | cafe 20 | cafe 10 | cafe 5 | cafe 0 | cafe -5 | living 10 | living 0 | office 5 | office 0 | office -5 | street 10 | street 0 | quiet A | quiet B |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| noise left in | 13.6 | 14.1 | 23.2 | 72.9 | 97.2 | 13.6 | 44.6 | 8.3 | 10.5 | 26.0 | 9.9 | 17.1 | 8.3 | 10.7 |
| ideal mask | 11.3 | 11.3 | 12.4 | 10.7 | 10.7 | 11.3 | 11.9 | 7.7 | 8.8 | 8.3 | 8.8 | 9.9 | 8.3 | 10.7 |
| DPDFNet baseline | 12.4 | 14.1 | 24.3 | 39.5 | 83.6 | 15.8 | 20.9 | 8.8 | 8.3 | 9.4 | 10.5 | 17.1 | 8.8 | 11.9 |
| GTCRN | 13.0 | 15.8 | 26.6 | 54.8 | 94.9 | 14.7 | 24.9 | 9.4 | 8.8 | 11.6 | 9.4 | 16.6 | 8.3 | 10.7 |

Signal view at cafe 0 dB (SI-SDR in -4.3 dB): GTCRN 0.9 dB with my voice 10 dB lower;
DPDFNet baseline 6.4, dpdfnet2 7.1, dpdfnet4 7.4 dB with my voice about 2 dB lower.

Conclusion: DPDFNet recovers the office and living-room cells and half of cafe 0, costs
0.5 to 1.2 points in quiet, and does nothing at cafe 5. Put after a perfect canceller it
would give about 19.5 on the suite (GTCRN 21.8) against 23.6 today and 9.8 for the ideal
mask. Next: larger DPDFNet models, a mix with the unprocessed mic, and whether the
denoiser should run on the whole track or only where the noise is loud.

## 3. The held-out set (heldout.py) and what drift does to today's removers

52 calls, frozen in `heldout-manifest.json` before any tuning (hashes of the mic tracks in
`heldout-sha256.txt`): FLEURS dev speech, four other DEMAND rooms, three kinds of echo
path, SER 5 to -15, SNR none to -5, clock drift in 22 of the 44 short calls with echo,
a device change in 6, four calls without echo, four calls of 12 minutes.

Signal scores of the lab's copies of today's removers on the 44 short calls with echo
(`hs.py`; ERLE in far-only parts / double-talk SI-SDR gain / my voice level, dB):

| Remover | steady (16) | drift (22) | device change (6) |
|---|---|---|---|
| delay-primed AEC3, linear output | 12.6 / 10.5 / -0.2 | 1.8 / 1.1 / -0.2 | 4.7 / 4.2 / -0.3 |
| delay-primed AEC3, floor 0.5 | 17.2 / 10.6 / -1.9 | 3.7 / 1.6 / -1.1 | 7.7 / 4.6 / -1.9 |
| LocalVQE fast | 13.5 / 10.0 / -0.1 | 6.1 / 5.6 / -0.0 | 8.6 / 8.0 / -0.1 |

Conclusion: a clock drift of 50 to 200 ppm takes almost all of the delay-primed filter's
echo removal away (the lab copy primes the whole file once; the app primes windows and
falls back to plain `sonora` when they disagree, so the app's numbers must be measured
with the app). A canceller that estimates the drift has a large gain to win here.

## 4. Family A: the echo path solved in closed form (solve.py)

Hypothesis: with the whole file at hand, a weighted least-squares fit of a convolutive
transfer function per STFT bin beats an adaptive filter, most of all in double talk.

`solve.cancel`: STFT 1024/256, 47 taps and 2 non-causal ones per bin (0.75 s), three
rounds of weights 1 / (smoothed residual power). First numbers on four suite calls
(ERLE / double-talk SI-SDR gain / voice, dB):

| Call | solve | delay-primed AEC3 linear | fast |
|---|---|---|---|
| office, SER -5, quiet | 30.0 / 22.9 / 0.0 | 26.7 / 14.9 / -0.2 | 27.2 / 14.4 / 0.0 |
| cafe, SER -5, quiet | 23.6 / 24.9 / -0.0 | 19.6 / 14.6 / -0.2 | 22.1 / 12.1 / -0.1 |
| cafe, SER -15, quiet | 23.3 / 24.8 / -0.0 | 19.9 / 17.0 / -0.2 | 21.9 / 17.3 / -0.1 |
| cafe, SER -5, SNR 5 | 8.9 / 8.6 / 0.0 | 7.8 / 6.8 / -0.1 | 7.1 / 6.7 / -0.0 |

Conclusion: 8 to 10 dB more SI-SDR in double talk with no loss of my voice. The ERLE of
23 to 30 dB is near the limit that the speaker's distortion sets (25.6 dB below the echo).

`solve.track` finds the echo's delay, the clock drift and a device change from windowed
phase-weighted cross-correlations (2 s windows for a coarse line by a vote over slopes,
then 8 s windows on the warped call track). Against the manifest's truth on 30 held-out
calls it finds the latency within 0.02 ms and the drift within 0.1 ppm in the direct and
mid rooms, lands on a reflection 4 ms late in two reverberant rooms, finds no echo in the
headphone calls, and finds the device change in the calls tested except one (ho-18).

Later changes to `solve.py`, each measured with `s1.py` (suite signal) and `hs.py`:

- Frames of 4096 samples with a hop of 1024 (12 taps and 1 non-causal one) give the same
  ERLE and double-talk gain as 1024/256 at a sixteenth of the cost; two rounds of weights
  are as good as three. A hop of half a frame is clearly worse. About 8 s for a call of
  160 s in Python on four threads.
- The tracker's first form followed the delay point by point and made false sections
  under strong drift in reverberant rooms. It now votes over slopes (-300 to 300 ppm) on
  the coarse points, keeps a second line only when a run of five points and a tenth of
  all points support it, drops a run that sits between two runs of one line, and puts
  the border between two sections at the frame where the residual is least.
- The call track is warped onto the mic's clock with a windowed sinc (32 taps) before the
  fit, so a drift of 200 ppm costs nothing.
- `solve.suppress` turns down what the linear stage left: the share of the echo estimate
  that stays, per bin, measured in the far-only frames. It adds only about 1 dB of ERLE
  (the residual is the speaker's distortion, which is not a fixed share per bin), but see
  section 5 for what it does to the word errors in noise.
- `aec-real` (40 devices joined) has a clear delay peak in 733 of 799 windows but no line
  that explains them, where a call without echo has 2 to 4 such windows of about 65. The
  tracker reports this as "many paths", and the pipeline then uses plain `sonora`, as the
  delay-primed models do.

Held-out signal scores of `solve` on the 44 short calls with echo, same columns as the
table in section 3 (ERLE / double-talk SI-SDR gain, dB):

| Remover | steady (16) | drift (22) | device change (6) | no noise |
|---|---|---|---|---|
| solve | 16.1 / 16.3 | 14.9 / 14.9 | 11.8 / 11.9 | 26.0 / 26.7 |
| solve with suppressor, floor 0.15 | 17.1 / 17.0 | 16.1 / 15.5 | 12.5 / 12.3 | 28.6 / 28.0 |

My voice level stays at 0.0 dB. The drift no longer costs anything; the limit in the
noisy calls is the noise itself.

## 5. First word errors: the baselines on the held-out set, the candidates on the suite

Held-out, the app's own removers (`py -P ho.py <work> amanu:sonora-delay-floor,thorough`),
52 calls: `thorough` 24.7 (no noise 24.3, 119 echo words, 2004 s of echo removal);
`sonora-delay-floor` 31.1 (no noise 21.3, 203 echo words, 147 s). So the reference for
the bar is `thorough` at 24.7, and a candidate must reach 22.7 or less.

Suite, lab WER (`py -P sw.py <work> run:...`), 40 calls:

| Remover | All 40 | No noise | SNR 20..5 | SNR 0 | SNR -5 | Echo words |
|---|---|---|---|---|---|---|
| sonora-delay-floor (suite run) | 23.6 | 11.4 | 15.3 | 30.6 | 54.4 | 5 |
| solve | 25.6 | 10.1 | 13.2 | 39.8 | 63.8 | 1 |
| solve, suppressor floor 0.15 | 21.0 | 10.2 | 12.6 | 24.8 | 54.7 | 4 |
| solve, then DPDFNet baseline | 20.4 | 10.7 | 13.7 | 23.5 | 48.3 | 5 |
| sonora-delay-floor copy, then DPDFNet | 24.5 | 13.4 | 16.7 | 31.8 | 51.7 | 3 |

Conclusion: the closed-form canceller alone takes 1.3 points off the quiet calls and 2.1
off the calls at SNR 20 to 5, close to the echo headroom of section 1, and lands on the
"no echo, noise left in" row in loud noise, as it must. The suppressor then takes 15
points off the calls at SNR 0, more than the oracle without echo has (38.7): it turns
the noise down while the far end talks, and the engine then hears my speech in the other
blocks better. Section 6 tests that. The denoiser after AEC3 gains nothing: AEC3's
output is already damaged where the denoiser would work.

## 6. Why the engine fails in loud noise, and the speech gate (m1.py, g1.py)

Hypothesis: the suppressor's gain in loud noise (section 5) comes from the noise it
turns down while only the far end talks, not from the echo it removes.

`m1.py` gives the engine my voice and the noise without any echo, with the noise turned
down only in parts of the call. My WER, one call per noise at SER -5:

| Input | cafe 0 | cafe -5 | living 0 | office 0 | office -5 | street 0 |
|---|---|---|---|---|---|---|
| noise left in | 72.9 | 97.2 | 44.6 | 10.5 | 26.0 | 17.1 |
| noise at 0.15 in the far-only blocks | 27.1 | 74.6 | 19.2 | 9.4 | 8.3 | 13.3 |
| noise at 0 in the far-only blocks | 31.6 | 77.4 | 19.8 | 8.8 | 8.8 | 12.7 |
| noise at 0.15 everywhere outside my blocks | 29.4 | 76.3 | 19.8 | 8.8 | 8.8 | 11.0 |
| ideal noise mask | 10.7 | 10.7 | 11.9 | 8.8 | 8.3 | 9.9 |
| DPDFNet baseline (section 2) | 39.5 | 83.6 | 20.9 | 8.3 | 9.4 | 17.1 |

Conclusion: confirmed, and stronger than expected. The noise over my own words is not
what loses them: with my blocks untouched and only the far-only blocks turned down, cafe
at 0 dB goes from 72.9 to 27.1. The engine (its voice detector and the normalization of
each piece it cuts) needs quiet between my sentences. A denoiser's own output is worse
than that (39.5): DPDFNet removes a quarter of my speech frames at cafe 0 dB.

So the denoiser is used as a detector only. `g1.py`: DPDFNet's output outside my blocks
is 60 to 70 dB under its 99th percentile in the median, with short bursts up to -14 dB
(voices from the babble); inside my speech the median is -12 to -15 dB. GTCRN's output
does not separate the two (its median outside is -13 to -19 dB), so it cannot steer a gate.

`pipeline.speech_gate`: the level of DPDFNet's output over 0.3 s; open where it is within
25 dB of its 99th percentile for at least 0.3 s, plus 0.3 s on both sides; 0.15 elsewhere;
ramps of 30 ms. The signal that passes is the canceller's, not the denoiser's.

Suite, lab WER, 40 calls:

| Remover | All 40 | No noise | SNR 20..5 | SNR 0 | SNR -5 | Echo words |
|---|---|---|---|---|---|---|
| solve, suppressor 0.15 | 21.0 | 10.2 | 12.6 | 24.8 | 54.7 | 4 |
| solve, suppressor, then DPDFNet's output | 21.2 | 10.7 | 13.7 | 26.0 | 50.1 | 5 |
| solve, suppressor, gate | 18.6 | 10.2 | 12.5 | 20.5 | 44.7 | 3 |
| solve, gate (no suppressor) | 18.1 | 10.4 | 12.3 | 19.6 | 42.8 | 4 |
| solve, suppressor, gate, half the denoiser's output mixed in | 17.9 | 10.1 | 11.6 | 21.0 | 42.3 | 5 |

Conclusion: the gate takes 2.4 points off the suppressor's result and 5 off today's best
(23.6). The three gate variants are within each other's noise. The finalist is the
suppressor with the gate and no denoiser sound in the output: it changes my speech least.
The gate values (25 dB, 0.3 s, 0.15) were set once from `g1.py` and not tuned on WER.

Long calls: `solve.cancel` fits each stretch in blocks of about 300 s, so the app holds one
block's spectra at a time. `s3.py`, true echo removal on the four 12-minute held-out
calls, one fit over the whole call against blocks: 28.8 / 29.6, 27.3 / 27.3, 27.7 / 26.9,
27.9 / 27.4 dB. No loss.

## 7. The app's port

`OfflineEchoSolver.cs` (Amanu.Core) is `solve.py` in C#, `SpeechGate.cs` (Amanu.App) the
gate, with sherpa-onnx's `OfflineSpeechDenoiser`, which the app already carries. Models:
`solve` (canceller), `solve-floor` (with the suppressor; the lab's `solve-res15`),
`solve-gate` (with the gate; the lab's `solve-res15-gate`). `p1.py` on one suite call
(cafe, SER -5, SNR 0): the app's `solve-gate` output differs from the lab's by -39 dB;
the solver takes 2.2 s for 156 s of audio.
