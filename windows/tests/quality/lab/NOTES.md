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
