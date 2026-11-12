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

## 8. The proof: held-out set and suite with the app's build

Held-out, lab pipeline (`ho.py lab:`): solve with suppressor 12.8 (against `thorough`
-11.9 [-17.0, -6.8]); with the gate 11.7 (-13.0 [-18.0, -8.0]); 1 echo word each.

Held-out, the app's models from the build of this branch (`AMANU_LAB_EXE`, `ho.py amanu:`):
`solve-floor` 13.0 (-11.7 [-16.6, -6.8], echo time 175 s), `solve-gate` 11.8
(-12.9 [-17.9, -7.9], 1320 s); 1 echo word each, against 119 for `thorough`. The app agrees
with the lab within 0.2 points. By kind of call (`hg.py`), `solve-gate` against `thorough`:
steady -3.8 [-6.4, -1.5], drift -17.5, device change -9.5, no echo +0.2 [-0.8, +0.6],
12 minutes -18.4. The gate against `solve-floor`: -1.2 [-2.2, -0.3].

Suite, the app's models (run `sonora-tuning` resumed for the echo calls):

| Model | My WER | No noise | SNR 20..5 | SNR 0 | SNR -5 | Echo words | ERLE real | DT voice | Echo time |
|---|---|---|---|---|---|---|---|---|---|
| solve | 25.5 | 10.2 | 13.2 | 40.1 | 62.0 | 1 | 21.7 | -0.0 | 66 s |
| solve-floor | 21.1 | 10.2 | 12.6 | 24.7 | 55.6 | 4 | 21.7 | -0.4 | 90 s |
| solve-gate | 18.4 | 10.2 | 12.5 | 20.7 | 43.3 | 3 | 33.7 | -0.4 | 427 s |

The lab loop had said 25.6, 21.0 and 18.6.

Echo words: every one is a single word ("v", "tri") in cafe noise at 0 dB (office at
-5 dB once in the lab). The oracle `nn-duckfar` of section 6, which has no echo in its
input, inserts the same "v" in `echo-cafe-ser-5-snr+0`. They are the engine's errors on
noisy speech that are also words of the far end, not echo. The bar's limit of 2 is still
missed by one word for `solve-gate` and two for `solve-floor`.

Time on an idle machine, one call of 156 s: solver 1.7 s, gate 7.0 s. DPDFNet alone takes
2.4 s for 30 s on one thread, and six pieces side by side take 5.2 s: it does not scale
with threads here.

A mistake to avoid: `quality.py run --name sonora-tuning` without `--only` also starts
the 754 clean-set files, because that run never held them.

Conclusion: the bar is reached on the held-out set with a wide margin and on the suite
except for the echo-word count. The headroom left on the suite (18.4 against 9.7) is
noise over my words at SNR 0 and -5; it needs a denoiser the engine likes or the engine
in the loop (family E), which were not tried.

## 9. Real calls 1000 and 1021 (overnight, track R)

Verdict: not found. No transcriptions were run, so there is no WER per model on real calls.
Nothing in this track changes the echo-model ranking.

What the search covered (about 15 minutes, read-only):

- `%USERPROFILE%\Amanu Quality Set\recordings` (98 entries). The only mic/system pairs there
  are the suite's synthetic scenes and `aec-real` (997 s, 40 regions, no reference words in
  `truth.json`). Neither is a real call with a reference transcript.
- The app's default recordings folder, `%USERPROFILE%\Amanu Recordings` (from
  `AppSettings.DefaultRecordingsDirectory`, `windows/src/Amanu.Core/Configuration/AppSettings.cs`).
  It has 29 call folders. Folders named `*-1000` and `*-1021` are timestamps, and none has
  a 14 or 16 minute mic/system pair. `2026.11.06-1021` holds `audio.m4a` and Gemini's
  `me.m4a` and `them.m4a`, not separate tracks, and a test comment calls it a one-person call.
- The four `.echo-kept` folders (`2026.11.09-1502`, `2026.11.10-1001`, `2026.11.10-1100`,
  `2026.11.11-1000`) have `mic.wav` and `system.wav`, but they run 3291, 2820, 1348 and 1215 s.
  None has a reference transcript, only the app's own `transcript.json`.
- `%LOCALAPPDATA%\Amanu Data` (config, people.txt, models): no transcripts.
- Greps for `1000`, `1021`, `489` and `467` in `windows/tests/quality`, `windows/native/sonora`
  and the recordings `*.json`/`*.md`: the only hits are the README's own table
  (`windows/native/sonora/README.md`, lines 71-72), the brief's mention of the two calls, and
  unrelated numbers in the code. No reference transcript exists in the repository.
- `git log` for `windows/native/sonora/README.md` (commit `5ae0f87`): the table was added with
  the Sonora commit, and the commit does not name the source folders.

What would unblock it: a copy of the two calls' `mic.wav` and `system.wav`, plus a reference
transcript with 489 and 467 words. Once they exist, run the lab's Amanu mode (`asr.py`, `amanu()`)
for `thorough`, `sonora`, `solve-floor` and `solve-gate` on each call, and score with `wer.py`
or `Q.error_counts`.

Disk: C: had 12 GB free before and after the search (5 GB floor). Nothing was written, deleted or
transcribed.

Next step: find the two calls' tracks and references (the sonora README's 489 and 467 counts
suggest a hand-corrected transcript, which may sit outside this PC). Failing that, record a
new real call with a known script so that the reference is known.

## 12. Our own segmentation (overnight, track N1)

Status: signal path built and checked, plumbing proved with one transcription. Screening is
prepared and not run (the engine slot belongs to another track). `lab/n1.py` is the script.

What the engine does (binary strings, `TranscriptionServices.cs`, the model file):

- `transcribe-cli` has no voice-activity detector and no gain normalization. The model's
  GGUF header has `stt.frontend.normalize = per_feature`. In NeMo's frontend that is the mean
  and variance of each mel bin over the time axis of the one utterance it is given. So it is
  per input file, and with the app's 60 s chunks it is per chunk. I read this from the
  header and the NeMo definition; I did not test it by experiment (that needs engine runs).
- The app's parakeet path (`TranscriptionServices.cs` 348-380): `SilenceSkip` (drops quiet
  runs of 5 s or more), then 60 s chunks with 1 s context, and **one `transcribe-cli` process
  per chunk**: `--backend vulkan -m <model> --threads N -q --timestamps word [--language]
  <chunk.wav>`. N is 8 shared over the tracks being transcribed (4 for the mic when mic and
  system run together). The batch form (`--batch <list> --batch-jsonl --timestamps auto`) is
  only used for GigaAM and Whisper. It works for parakeet too (proved below): one process,
  the model loaded once, the files one after the other (`--batch-size` 0/1).
- `level_normalization` is `off` by default, so the app does not normalize the level either.
- `Amanu.exe --measure-transcription ... auto` uses `local_engine` of `config.json`, which is
  `parakeet-sk` (the Slovak fine-tune) on this PC, with `language: sk`. `MeetingLanguages`
  makes that [sk, en] and pins nothing, so no `--language` is passed. The suite's speech is
  Slovak. `n1.py` reads the same `local_engine` for `-m`. This also means every WER in this
  notebook is the Slovak model's.

Design of `n1.py` (mic and far only; the truth is read for scoring and for nothing else; no
per-call settings):

1. `solve-gate` pieces per call: the solver's output `e` and the speech gate's gain `g`
   (`pipeline.VARIANTS["solve"]`, `denoise.dpdf`, `pipeline.speech_gate`), cached as
   `cache/<call>.npz` under the work directory. The gate's output is `g * e`. The engine is
   fed `g * e`, so a padding stretch where the gate is closed is at 0.15, as in solve-gate.
2. Regions: runs where `g > 0.5`; merged across gaps under `merge_s`; padded by `pad_s`
   each side and merged again where the padding touches; cut at 25 s at the quietest 100 ms
   of the last 40 % of the allowed length (none was needed on the suite: the longest region
   is 24.8 s with setting c on one call, 18.4 s or less otherwise; the cut code is untested).
3. Each region is scaled so that the RMS over its open samples is -20 dBFS (the app's own
   `SpeechLevels.TargetDb`), gain limited to +30 dB, peak kept under 0.97.
4. One WAV per region, one `--batch` process per call, texts joined in time order, scored
   with `Q.error_counts(truth["me"], ...)` exactly as `asr.py` does. Rows go to
   `<work>/wer2/n1/<setting>/<call>.json` in the same shape as `asr.py`'s, so `asr.rows`,
   `asr.wer` and `asr.bootstrap` read them.

Three fixed settings:

| Setting | Merge gap | Padding | Cap | Packaging |
|---|---|---|---|---|
| a | 0.6 s | 0.3 s | 25 s | one WAV per region, each at -20 dBFS RMS, through `--batch` |
| b | 0.6 s | 0.3 s | 25 s | the regions of a stay in the one file at their own level; the gaps become pink-ish noise at -60 dBFS (30 ms crossfades); the app's normal path (`asr.transcribe` through `Amanu.exe`, echo off). `SilenceSkip` still runs there. |
| c | 1.5 s | 1.0 s | 25 s | as a |

Signal check, 14 noisy calls (`py -P n1.py <work> check`): the share of the gate's open
samples that lie inside a region is 100.0 % for a and for c on every call (the padding only
adds). Setting a: 168 regions, 12 per call (10 to 18), mean 5.4 to 9.2 s, longest 15.6 s;
1245 s of the 2295 s go to the engine (54 %). Setting c: 139 regions, 8 to 15 per call, mean
7.9 to 11.8 s, longest 24.8 s (living 0 dB; 18.4 s elsewhere); 1448 s go to the engine
(63 %). The office and street calls come out identical across SER (the gate sees the same
speech), and have 10 regions (a) or 9 (c).

Plumbing proof, one transcription, `echo-cafe-ser-5-snr+0` (cafe 0 dB), setting a: 14
regions, 88.9 s of audio, 8.5 s of engine time in one `--batch` process; my WER 29.9 % (53
errors of 177 words) against 31.1 % cached for solve-gate on the same call. One call says
nothing about the setting; it proves the chain. No other transcription was run.

Screening command (not run), 14 noisy calls plus the 6 no-noise calls, baseline = the
cached `su/solve-gate` rows of the lab folder, never re-measured (the table prints them
beside each setting with a paired bootstrap):

    cd windows\tests\quality\lab
    py -P n1.py "%USERPROFILE%\Amanu Quality Set\lab\overnight\N1" run:a,c,b
    py -P n1.py "%USERPROFILE%\Amanu Quality Set\lab\overnight\N1" show:a,b,c

`run:` takes the calls with SNR 0, -5 or none (20), three at a time (`asr._gpu`), settings in
the order given, and caches every row, so a restart skips what is done. a and c use one
`transcribe-cli` process per call (so at most 3 engine processes); b uses `Amanu.exe`,
which starts one process per 60 s chunk and per track (mic and system side by side), so up
to 6 at once from 3 transcriptions. Estimate: a and c about 8.5 s of engine per call, 20
calls three at a time, about 1.5 to 2.5 min each; b about 25 s per call, about 3 to 4 min;
about 8 to 10 minutes for the three, plus start-up. The gate pieces for all 20 calls are
cached (11 min of CPU, 400 MB in `...\N1\cache`). `AMANU_LAB_EXE` names the Amanu build for b.

Not done: any screening; any check that per_feature is per chunk by experiment. An
experiment that would show it: transcribe one region alone, and again with 30 s of
loud noise appended in the same file; if only the per-file statistics move, the words of
the region change.

### 12.1 Screening result (N1)

My WER, % (errors / reference words over the calls), against the cached `su/solve-gate` rows
(lab WER, same calls). Engine = transcribe-cli time only; solve-gate's cached `secs` is echo
time, not engine time, so no like-for-like engine figure exists for it.

| Setting | SNR 0 (7) | SNR -5 (7) | 14 noisy | no noise (6) | All 20 against solve-gate (95 % CI) | Engine s (20 calls) |
|---|---|---|---|---|---|---|
| solve-gate | 19.6 | 42.8 | 29.6 | 10.4 | | |
| a (merge 0.6, pad 0.3, per region, -20 dBFS) | 18.3 | 41.8 | 28.4 | 8.9 | -1.3 [-1.9, -0.8] | 281 |
| b (comfort noise -60 dBFS, app path) | 19.6 | 43.6 | 29.9 | 10.0 | +0.1 [-0.4, +0.5] | n/a (Amanu.exe) |
| c (merge 1.5, pad 1.0, per region) | 17.6 | 40.0 | 27.2 | 9.2 | -2.0 [-2.9, -1.3] | 287 |
| d (c's regions in one file, 0.3 s zeros between) | 19.4 | 48.0 | 31.6 | 9.7 | +1.2 [-0.4, +3.2] | 483 |
| e (merge 2.5, pad 1.5, per region) | 17.3 | 40.6 | 27.3 | 9.0 | -2.0 [-2.7, -1.4] | 308 |

Guard (no noise within 0.5 points of solve-gate): a, c, d, e pass (all better than 10.4);
b passes. Verdict: a small win. c takes 2.4 points off the 14 noisy calls (29.6 to 27.2) and
1.9 off the 20 together; e is the same within noise. Not close to the ideal (about 10): the
-5 dB half is still at 40.

What it says:
- b (silence replaced by comfort noise in the normal path) changes nothing. So the app's
  quiet-stretch handling is not the lever; the engine already copes with digital near-silence.
- Cutting regions into separate files helps (a, c, e); more context at the edges helps a bit
  (a to c); beyond that it saturates (e = c).
- d (the same speech joined into one file) is worse than c, mostly at -5 dB (48.0 against
  40.0). If per-feature statistics from speech only were the whole cause, d would equal c.
  It does not, so per-utterance isolation (short files, each with its own statistics and its
  own decoding start) matters more than which frames enter the statistics. Caveat: d is a
  100 s single file, which the engine may treat differently (engine time 483 s against 287),
  so d does not cleanly separate the two causes.
- No per-call tuning; settings fixed before the run. Rows: `...\N1\wer2\n1\{a,b,c,d,e}`.

All engine runs of this track have ended (the last was `run:d,e`).

### 12.2 Held-out set, setting c

Plan (written before the run): 52 held-out calls (158 min of mic audio, four of them 12 min),
setting c only, frozen (merge 1.5 s, pad 1.0 s, -20 dBFS, `--batch`), 3 calls at a time. The
cleaned audio of solve-gate is not cached for the held-out set (only its rows), so each call
recomputes solve and the gate (about 0.2 x real time on this PC, in the workers), then one
`transcribe-cli` process. Expected 35 to 45 minutes. Why: the deciding test of N1 before the
app port. Baseline: the cached `ho/solve-gate` rows (11.8), not re-measured. Run with
`N1_SET=ho py -P n1.py "%USERPROFILE%\Amanu Quality Set\lab\overnight\N1\ho" run:c`.

## 11. Adaptive blend (overnight, track N2)

Hypothesis: mix DPDFNet's output into solve's output by local SNR (0 where quiet, up to
`w_max` in babble), with solve-gate's gate on top. Step 1, CPU only; no transcription has
been run yet.

Files: `n2.py` (the variants, `cache`, `check`), `n2sw.py` (screening driver, see below).
Caches: `%USERPROFILE%\Amanu Quality Set\lab\overnight\N2\cache\*.npz` (solver output `e`
and DPDFNet output `c` per call, float32, keyed by a hash of mic and far; 40 calls, about
330 MB), `...\N2\check.txt` (the first full signal check, before the last guard change).

Output: `speech_gate(e, c) * ((1 - w) * e + w * c)`, with `e = solve(mic, far)` and `c =
dpdf(e)`, exactly solve-gate's pieces; `w = 0` gives solve-gate bit for bit. Only mic and far
are used, no per-call setting.

The weight `w` (`n2.weight_curve`):
- SNR estimate per 16 ms frame over a 0.5 s window: `10 log10(Sc / (Se - Sc))`, where Se and
  Sc are the energies of `e` and `c`. DPDFNet keeps speech and removes noise, so the energy it
  removes is the noise. On the truth: at true SNR 0 the median estimate over my speech is -3
  (cafe, living) to +3 (office); 10 gives 6 to 13; 20 gives 14 to 18; no noise 17 to 19.
- Trapezoid: 0 at 10 dB and above, `w_max` from -5 dB down to -17 dB, back to 0 at -27 dB.
  Under -17 dB DPDFNet kept almost nothing: silence, or speech it removed, and mixing it in would
  only turn my words down; the gate decides there.
- Quiet-call guard: `w` is also scaled by the noise floor, the 10th percentile of the removed
  level over +-6 s, ramping from 0 at -50 dBFS to 1 at -44. Babble never goes quiet; the far
  end's residual in a quiet call comes in bursts. Without it, the three SER -15 and -5 calls
  without noise got weight in 82 to 246 windows (double talk and far-only), a plain 
  absolute level cannot tell them from SNR 20. With it the six no-noise calls have `w` = 0
  in every window.
- Smoothing: the frame weights are interpolated to samples and averaged over 0.5 s.

Settings (fixed for all calls):

| Tag | w_max | Curve |
|---|---|---|
| `n2-w50` | 0.5 | 10 / -5 dB, dead zone -17 to -27 |
| `n2-w80` | 0.8 | the same |
| `n2-w65-guard` | 0.65 | the same up-ramp; dead zone -8 to -16, so it stays out of windows where DPDFNet removed most of the signal (my choice of curve) |

Signal check (`py -P n2.py check <work> [call regex]`), against solve-gate's own signal,
speech gain = projection of the output on near.wav over my speech frames, noise = the
rest, dB:
- Six no-noise calls and the SNR 20 calls of cafe SER -15 / office: weight 0, output
  identical (differences 240 dB under the peak, i.e. zero). Other SNR 20 calls: max weight
  0.35 in a few windows, speech -0.01 dB, noise +0.0 dB.
- Noisy calls, `w50` / `w80` (first pass of `w65-guard`, dead zone -10 to -20 in brackets):
  speech gain at SNR 0: cafe -0.8 / -1.3 to -1.5 (guard -1.0), living -0.3 / -0.5, office
  -0.0, street -0.1 / -0.2; at SNR -5: cafe -1.9 / -3.3 (guard -1.8), office -0.03 / -0.07.
  Noise (everything that is not my speech) lower by 3.3 to 4.0 dB (`w50`) and 5.3 to 7.0 dB
  (`w80`) at SNR 0 and -5; 2 dB at SNR 5; 0.7 dB at SNR 10.
- With the final guard (dead zone -8 to -16): cafe SNR -5 speech -1.4 dB, noise -1.8 dB; cafe 0
  -0.9 / -3.3; living 0 -0.35 / -4.1; office -5 -0.05 / -4.0; street 0 -0.1 / -4.6.
- Verdict: the speech level is within 1 dB on 11 of the 14 calls for `w50` and everywhere
  except cafe for `w80`; the cafe SNR -5 calls (3 of the 14) lose 1.4 to 1.9 dB in every
  setting and the cafe SNR 0 calls 0.8 to 1.5. It is where DPDFNet itself removes speech
  (sections 2 and 6), not broken code. No code error was found; the first version's
  absolute noise floor (see above) was the only fault, and is fixed.

Screening, to run when the engine is free (14 calls at SNR 0 and -5; 3 settings x 14 = 42
transcriptions, 3 at once, about 10 s each, so 5 to 8 minutes in all; the variants are cheap
because `e` and `c` are cached). The baseline is the cached `su/solve-gate` rows (lab WER
19.6 at SNR 0, 42.8 at -5, 29.6 over the 14; the app's numbers 20.7 and 43.3 are a few
points apart on the same calls), never measured again:

    py -P n2sw.py "%USERPROFILE%\Amanu Quality Set\lab" run:n2-w50,n2-w80,n2-w65-guard

Work directory is the lab's own, so rows land in `...\lab\wer2\su\n2-*` (small JSON); `show:`
instead of `run:` prints the table again with paired bootstrap against solve-gate.
Bar for the next step (held-out, no-noise check): at least 1 point better than solve-gate
on these 14 calls with an interval that excludes 0, and no more echo words than 4.

Result (52 calls, setting c frozen, no tuning; rows in `...\N1\ho\wer2\n1\c`; baseline the
cached `ho/solve-gate` rows, not re-run). My WER %, paired bootstrap over calls, 95 %:

| Calls | n | c | solve-gate | c minus solve-gate |
|---|---|---|---|---|
| all | 52 | 10.0 | 11.8 | -1.8 [-3.0, -0.6] |
| SNR 0, -5 | 10 | 21.7 | 27.2 | -5.5 [-8.5, -2.8] |
| SNR 20..5 | 31 | 9.1 | 10.3 | -1.2 [-2.7, +0.6] |
| no noise | 11 | 5.0 | 6.3 | -1.3 [-2.3, +0.3] |
| steady / drift / change / no echo / long | 16 / 22 / 6 / 4 / 4 | 7.9 / 12.6 / 17.9 / 6.1 / 7.2 | 10.0 / 13.4 / 21.4 / 6.8 / 9.7 | -2.1 / -0.8 / -3.4 / -0.6 / -2.5 |

Engine time, 52 calls: 571 s of transcribe-cli (the four 12-minute calls included), one
process per call. The cleaned audio is recomputed per call (not cached for the held-out set;
cache off for it to save 1.2 GB), so the whole run took about 20 minutes of wall time.

Calls where c is worse than solve-gate by more than 5 points: ho-27 (+13.2, SNR 10 meeting
noise: 27 inserted words against 2), ho-32 (+9.6, SNR 20: 16 insertions against 2), ho-13
(+5.6, no noise: 8 insertions against 0), ho-30 (+5.5, SNR 20: 11 insertions against 1).
They are insertions, not echo words (leak 0 on ho-27 and ho-32). A plausible reason, not
tested: with 1.0 s padding the engine hears background talkers (meeting babble) at the edge
of a region and writes them down, where solve-gate's long chunks kept them as noise. The
big gains are the loud-noise calls: ho-20 -14.2, ho-33 -12.5, ho-37 -11.0.

Verdict: win on the held-out set, driven by SNR 0 and -5 (-5.5, the interval clear of zero);
the quiet and mild-noise calls are flat within noise, with a few insertion-heavy losses.
All engine runs of track N1 have ended (the last was the held-out `run:c`).

## 13. The port of the region cutting (overnight)

Status: built and unit-tested; not yet measured. The smoke test was skipped because six
`transcribe-cli` processes of other tracks were running when it was due (20:59 on 2026-11-11).

What was added (setting c of `n1.py`, `echo_model` = `solve-gate-cut`):

- `Amanu.Core/Processing/SpeechRegions.cs`: open runs of the gate (gain above 0.5), merge under
  1.5 s, pad 1.0 s each side and merge where the padding touches, cut at 25 s at the quietest
  100 ms of the last 40 % (the same search as `n1.split_long`), gain to -20 dBFS RMS over the
  open samples (at most +30 dB, peak under 0.97), and the gate file (`mic.gate.json`).
- `EchoSolver` has a third field, `CutRegions`; `LocalVqeAssets.SolveGateCut` is solve-gate with it
  on, processor `echo-solver-v1-floor15-gate-cut`, so a kept copy of solve-gate is never taken
  for it. It is in `AppSettings.EchoModels` and the settings text. The default stays solve-floor.
- `SpeechGate.Apply` has an overload that also returns the open runs. `OfflineEchoAudio.Prepare`
  writes them to `mic.gate.json` beside the cleaned mic when the model cuts; `Keep` copies the file
  into the kept folder; `RemoveAudio` deletes it. They are handed over as a file, not in memory,
  because recognition may run in a later pass from the kept copy.
- `TranscriptionServices.TranscribeFileCoreAsync`: for a parakeet engine and the mic track, if the
  gate file sits beside the source and its frame count is that of the track, the track goes
  through `TranscribeRegionsAsync` instead of SilenceSkip and the 60 s chunks. Without a file, with
  a file for another length, or with no region, the usual path runs. The system track is unchanged.
- `Amanu.exe --measure-transcription` needs no change: `AMANU_MEASURE_ECHO=solve-gate-cut` sets
  `EchoModel`, and the coordinator then runs the same echo stage and transcription as the app.
- Tests: `tests/Amanu.Core.Tests/SpeechRegionsTests.cs` (merge, pad, touching pads, cap and cut
  place, offsets, level gain, gate file, model flags). Whole solution: 331 + 113 tests pass.

Differences from `n1.py` setting c:

1. One `transcribe-cli` process per region with `--timestamps word`, as the 60 s chunks run
   today, not one `--batch --batch-jsonl` process. `--batch-jsonl` of v0.1.3 carries no word
   times, and the transcript needs them in call time. The files and the model are the same, so
   the text should match; the cost is a model load per region (the 60 s path pays that per
   chunk already). Whether batch is still wanted for speed is a decision for after the numbers.
2. The audio is the cleaned mic as the app writes it (float WAV, then 16-bit PCM when kept or
   converted for the engine); the lab used float `g * e` written as 16-bit. No difference
   beyond rounding is expected.
3. Gate open runs are taken from the app's own gain (`SpeechGate.Gain`), which is the lab's
   `speech_gate` re-implemented in C#; the notes already say the app and the lab agree within
   0.2 points. The cut at the cap is exercised by unit tests only (as in the lab).
4. Word times: each word is the region's start plus its time in the region; no word is dropped
   by a window, because regions never overlap. The 60 s path's mid-point filter is not needed.
5. SilenceSkip does not run on this path (the regions already leave the quiet out).

Build: `windows\artifacts\overnight-cut` (335 MB; managed files published there, `local-runtime`
and `live-runtime` copied from `artifacts\publish`, which was not touched).

    AMANU_LAB_EXE=C:\Backup\CoDiNG\Amanu\amanu-windows\windows\artifacts\overnight-cut\Amanu.exe

To confirm (engine runs; only when the engine slot is free):

    set AMANU_LAB_EXE=C:\Backup\CoDiNG\Amanu\amanu-windows\windows\artifacts\overnight-cut\Amanu.exe
    cd windows\tests\quality\lab
    py -P ho.py "%USERPROFILE%\Amanu Quality Set\lab" amanu:solve-gate-cut
    py -P ho.py "%USERPROFILE%\Amanu Quality Set\lab" show:solve-gate-cut,solve-gate solve-gate

The suite's echo calls through the same build (rows go to tag `su/solve-gate-cut`; compare with the
cached `su/solve-gate` rows):

    py -P -c "import asr, lab; from concurrent.futures import ThreadPoolExecutor as P; W=r'%USERPROFILE%\Amanu Quality Set\lab'; ns=sorted(p.name for p in lab.Q.RECORDINGS.iterdir() if p.name.startswith('echo-')); list(P(3).map(lambda n: asr.amanu(W,'su/solve-gate-cut',n,lab.Q.RECORDINGS/n,'solve-gate-cut'), ns)); print(asr.summary(W,'su/solve-gate-cut'))"

### 11.1 Results of the N2 screen (track N2, continues section 11)

Screen: `py -P n2sw.py "%USERPROFILE%\Amanu Quality Set\lab" run:n2-w50,n2-w80,n2-w65-guard`
(14 noisy calls, 3 transcriptions at once, about 12 minutes; log `...\overnight\N2\screen1.log`,
rows `...\lab\wer2\su\n2-*`). Baseline: the cached `su/solve-gate` rows, not re-measured.
My WER, lab:

| Remover | 14 calls | SNR 0 | SNR -5 | Echo words | Against solve-gate |
|---|---|---|---|---|---|
| solve-gate | 29.6 | 19.6 | 42.8 | 4 | |
| n2-w50 | 27.3 | 19.1 | 38.1 | 3 | -2.3 [-4.9, -0.2] |
| n2-w80 | 28.0 | 19.8 | 39.0 | 3 | -1.6 [-3.9, +0.4] |
| n2-w65-guard | 27.9 | 19.6 | 38.9 | 3 | -1.7 [-3.9, +0.1] |

All three pass the 1.5-point bar; only `n2-w50` has an interval that excludes 0. The gain is
at SNR -5 (4 to 5 points); at SNR 0 it is nil. Larger w_max is not better (0.5 beats 0.8),
which fits the 1.4 to 3.3 dB of speech that DPDFNet takes off the cafe calls.

Combined with n1's setting c (blend first, then cut; `n2c.py`, `py -P n2c.py
"%USERPROFILE%\Amanu Quality Set\lab\overnight\N2" run:n2-w50`, log `screen2.log`, rows
`...\N2\wer2\n2c\n2-w50`): WER 25.9 (SNR 0 17.9, SNR -5 36.5), echo words 6, against
solve-gate -3.7 [-6.4, -1.3], against setting c alone (27.2) -1.3 [-3.4, +0.5]. The gains
add only in part (-2.3 and -2.2 would give -4.5; got -3.7), and the interval against c
includes 0. Echo words rise to 6 (c alone: check N1's table), which misses the bar of 2 by
more. Verdict: unclear to weak win; not run on the held-out set or the quiet calls with
the cut (the quiet guard keeps w = 0 there, so the cut alone decides).
All engine runs of track N2 have ended.

## 14. solve-gate-cut in the app's build (overnight)

Run 1 plan (held-out, 52 calls, ho.py amanu:solve-gate-cut, 3 at once): about 20 minutes expected. Only solve-gate-cut is in it because it is the one echo model the overnight build adds and it has no cached row; solve-gate (11.8) is cached and is not re-measured, and the bootstrap reads its rows only.


## 15. Pick and vote across versions (overnight, track E)

No engine run. `lab/e1.py` reads cached rows only: solve-gate (`su/solve-gate`), n1 settings a, c, e, and n2-w50 cut (`n2c`). Output of the run: `%USERPROFILE%\Amanu Quality Set\lab\overnight\E\e1-results.json`. It reproduces 27.2 (n1-c), 25.9 (n2c-w50), 29.6 (solve-gate) and 27.3 (n1-e) on the 14 noisy calls.

What the cache lacks: every row holds the joined text of the whole call, with no per-region text and no word times. So E1 (per-region picks by speaking rate, agreement or "prefer non-denoised") cannot be computed, and no per-region oracle either. n1-c and n2c do use the same regions (same region count on 14 of 14 calls). To get E1 the engine must be run again with per-region texts saved: n1-c 14 calls (about 200 s of engine, `run_batch` already receives the per-file texts and joins them), n2c-w50 the same (about 200 s), solve-gate cut into the same regions (about 200 s) as third version for rule (ii). About 10 to 12 minutes with 3 at a time, CPU for the cleaned audio included. Not run.

What was done instead (whole-call, truth-free): word-level ROVER-style vote. Backbone = first system, the others aligned to it by edit distance, majority per slot, a tie goes to the backbone, an insertion is kept when more than half of all systems insert it. Agreement is a PROXY for correctness; it is not a confidence score, and versions that share an input share its errors. Rows are my WER %, 14 noisy calls; interval = paired bootstrap over calls against n1-c, 95 %:

| Version | 14 noisy | SNR 0 | SNR -5 | Echo words | Against n1-c |
|---|---|---|---|---|---|
| n1-c | 27.2 | 17.6 | 40.0 | 5 | |
| n2c-w50 | 25.9 | 17.9 | 36.5 | 6 | -1.3 [-3.4, +0.5] |
| solve-gate | 29.6 | 19.6 | 42.8 | 4 | +2.4 |
| vote c, n2c, solve-gate | 26.7 | 17.0 | 39.7 | 3 | -0.5 [-1.0, +0.0] |
| vote c, n2c, e | 26.5 | 16.8 | 39.5 | 3 | -0.7 [-1.1, -0.3] |
| vote c, e, a | 26.4 | 16.6 | 39.4 | 3 | -0.8 [-1.5, -0.2] |
| vote c, n2c, e, solve-gate | 26.4 | 16.6 | 39.6 | 3 | -0.8 [-1.3, -0.4] |
| vote n2c (backbone), c, solve-gate | 25.5 | 17.0 | 36.7 | 3 | -1.8 [-3.6, -0.2] |

The last row only changes the backbone to n2c-w50; it was tried after the others, so it is one pick of five and not a pre-registered rule. Against n2c-w50 alone it is -0.4 [-1.0, +0.0]. No-noise calls (n2c not cached there; the blend has weight 0 on them): vote c, e, solve-gate 9.0 against n1-c 9.2 (20 calls: 26.6 against 27.2, -0.5 [-1.1, +0.1]).

Ceilings (use the truth, so they are NOT a rule): per call, the better of c and n2c gives 25.3; per word (a reference word counts as right if either version has it right, insertions the smaller of the two) gives 24.1, i.e. 1.8 under 25.9. That is above the 1.5 stop line, but barely, and it is the limit for any picker or voter between these two; a real rule reaches a part of it.

Verdict: unclear, small. A vote gains 0.5 to 0.8 over n1-c, with a clear interval, but is no better than n2c-w50 alone beyond 0.4, and the whole headroom between the two versions is 1.8. Echo words fall to 3. The gain comes mostly from fewer errors at SNR 0, where the vote beats both inputs (16.6 to 17.0 against 17.6 and 17.9).

## 10. The gate in int8 (overnight, track S1)

Hypothesis: a dynamically quantized int8 DPDFNet gives the same open/closed decisions as the
float model at a fraction of the time. Verdict: **loss**, on both counts, for two attempts that
each fail for a reason I understand. S2 (skip the model where the canceller is quiet) was not
tried, because its condition (under 1 % of frames differ and time at most half) was not met.

Code: `s1q.py` (`quant`, `cache`, `gate`, `time`). Models, caches and logs are under
`%USERPROFILE%\Amanu Quality Set\lab\overnight\S1` (`models\int8.onnx`, `models\int8-gemm.onnx`,
`cache\`, `gate.log` with the numbers per call, `time.log`). Needs `pip install onnxruntime onnx`.

Commands (from `lab`): `py -P s1q.py quant`; `py -P s1q.py cache` (solve output of the 40 `echo-*`
calls, 7 min); `py -P s1q.py gate float,int8,int8-gemm` (about 80 min on the busy machine);
`py -P s1q.py time float,int8,int8-gemm`.

Method: the model is the same file format, so sherpa-onnx runs it unchanged (the metadata is
copied over) and the path is the app's: sherpa, 1 thread. The input is the `solve` output (no
residual suppressor, as `solve-gate`), the gate is `pipeline.speech_gate` with its defaults,
and a 10 ms frame is open when the mean gain over its 160 samples is above 0.5.
The model is DPDFNet baseline, one frame of 10 ms at a time with a 38 256-value state fed
back; it has 146 Gemm (the GRUs), 28 Conv and 2 MatMul.

- `int8`: `quantize_dynamic(QInt8)`, defaults. 148 MatMulInteger and 28 ConvInteger, 2.7 MB.
- `int8-gemm`: `op_types_to_quantize=["MatMul","Gemm"]`; the 28 convs stay float. 2.8 MB.

Decisions against float, share of 10 ms frames that differ:

| Model | All 40 calls | The 14 calls at SNR 0 and -5 |
|---|---|---|
| int8 | 4.71 % | 12.24 % |
| int8-gemm | 5.71 % | 14.78 % |

Worst calls (open share float / int8): cafe ser+5 snr-5 55.3 / 14.3 (46.6 % differ), cafe
ser-5 snr-5 54.6 / 13.7 (46.1 %), cafe ser+5 snr+0 51.1 / 36.5 (14.7 %), cafe ser-5 snr+0 51.3 /
36.4 (15.0 %), cafe ser-15 snr+0 46.3 / 33.6 (12.9 %). int8-gemm: cafe ser-15 snr-5 39.2 / 97.2
(58.4 %, gate nearly always open). Office calls differ on 0.0 to 1.9 % of frames; clean and
SNR 20 to 5 calls on 0.0 to 3 %. Per call numbers: `gate.log`.

Time, the same 5 calls (812 s of audio: cafe ser+5 snr+0, cafe ser-15 snr+20, cafe ser-5
snr-5, office ser+5 snr-5, office ser-5 snr+0), models run one after the other, twice, best of two,
model alone (no gate arithmetic), machine busy (the absolute time is 5 times the 7 s per 156 s
of the brief, so read the ratio):

| Model | Rep 0 | Rep 1 | Ratio to float (best) |
|---|---|---|---|
| float | 190.9 s | 192.0 s | 1.00 |
| int8 | 398.3 s | 324.0 s | 1.70 (slower) |
| int8-gemm | 207.0 s | 175.8 s | 0.92 |

Why it fails:
1. Time. The model is a stream of one frame per call, so each GRU/Gemm multiplies a vector,
   not a matrix. The multiplication is not the cost; the cost is the per-call overhead and the
   activation quantization (a `DynamicQuantizeLinear` and a `Cast` before and after each of
   148 matrix products). `ConvInteger` is slower than float `Conv` on this CPU (int8 1.70 x).
   Quantizing only the matrix products gives 0.92 x: the quantize/dequantize steps eat the gain.
2. Decisions. The gate threshold sits 25 dB below the output's 99th percentile, and in babble
   the model's output is near that border (DPDFNet puts most of the noise 60 dB down, but a
   small error in its mask moves the level by the 10 dB that matters). The state, 38 256 values,
   is fed back every 10 ms, so a quantization error does not stay in its frame. The effect is
   large where the noise is babble at 0 and -5 dB, and small in office noise.
   This second reason is argued, not measured: I did not test it on the state alone.

Not done: a static (calibrated) quantization, which would cut the `DynamicQuantizeLinear`
steps but needs calibration data and would face reason 2 as well; S2; any WER run (I did not
run an engine, as told). Do not spend more on int8 for this model. If the gate must get
cheaper, the lever is the 30 s pieces and the 4 s of overlap (about 13 %) or a smaller
detector, not weights in int8.

If someone wants a WER check anyway, it needs the engine and a new gate model path in the lab;
the cleanest form is the `int8-gemm` file put in `lab\models` and a `denoise.dpdf` variant that
loads it, then `py -P sw.py <work dir> run:<that variant>` on the 14 calls at SNR 0 and -5.
I do not expect it to pay: the gate opens in other places in the babble calls.


## 16. A larger DPDFNet where the noise is loud (overnight, track N3)

Hypothesis: `dpdfnet2` or `dpdfnet4` keep more of my speech in babble than the baseline, so
blending one in (n2's scheme) and then cutting (n1 setting c) beats 27.2 by 1.5 points. Step 1,
CPU only; no transcription was run.

Models: the same release as the shipped baseline (k2-fsa/sherpa-onnx, tag
`speech-enhancement-models`), Ceva DPDFNet, Apache-2.0 (the repo ceva-ip/DPDFNet reports
Apache-2.0). `dpdfnet2.onnx` 10,249,356 bytes (SHA-256 `ce35d602...487a9`), `dpdfnet4.onnx`
11,707,436 bytes (`71b588bc...d4420`); also in the release: `dpdfnet8` (14.6 MB) and
`dpdfnet2_48khz_hr` (not used). Copies in `%USERPROFILE%\Amanu Quality Set\lab\overnight\N3`;
the files in `lab\models` have the same hashes.

Files: `n3.py` (`cache`, `speed`, `check`, `run`, `show`), `...\N3\cache\*.npy` (the larger
models' output on N2's cached solver output, 40 files, 400 MB, 14 noisy and 6 no-noise calls),
`...\N3\chk-*.log` (the check output).

Speed (`n3.py speed`, 3 calls, 7.8 min of audio, 2 threads, CPU shared with the engine
agent, so the ratio matters more than the figure): baseline 17.4 s per minute of audio,
dpdfnet2 25.9 (1.5x), dpdfnet4 43.1 (2.5x). Not usable in the app's live path at that speed
on this PC; only an offline step could carry it.

Signal check (`n3.py check`; truth only places the scores). Raw model output against the solver
output `e`, speech level = projection on near.wav over my speech frames (mean over calls),
and share of my speech frames that end under half the level:

| SNR | baseline | dpdfnet2 | dpdfnet4 |
|---|---|---|---|
| -5 (cafe x3, office x3) | -6.71 dB | -8.59 dB | -7.51 dB |
| 0 (cafe x3) | -2.35 | -2.27 | -1.92 |
| 0 (living, street) | -0.56 | -0.53 | -0.54 |
| no noise | -0.26 | -0.30 | -0.27 |

Cafe SNR -5: frames under -6 dB 72 to 80 % (baseline), 82 to 83 % (d2), 75 to 78 % (d4). So
the larger models remove as much speech as the baseline at SNR -5, and more (d2 by 1.9 dB,
d4 by 0.8 dB). At SNR 0 only d4 is better, by 0.4 dB.

Blended settings (weight curve and gate from the baseline's output, only the blended sound
differs; against solve-gate, speech dB / noise dB), cafe SNR -5: w50 baseline -2.2..-2.6 / -3.1..-3.4,
d2 w50 about the same speech (-2.0..-2.3, 0.3 dB better) / same noise; d4 w50 -3.5..-4.0 / -4.8..-5.4,
i.e. 1.3 dB more speech lost for 2 dB more noise removed. Cafe SNR 0: d2 -0.7, d4 -1.1 against
-0.8 baseline. Office, living, street: speech within 0.4 dB everywhere, noise 2 to 7 dB lower.
No-noise calls: weight 0, output identical (240 dB under the peak) for all settings.

Verdict (stop rule): the larger models do not keep more speech at SNR -5 (within 0.5 dB, in fact
worse). Screening is not worth engine time. The only visible difference is noise removal: d4 takes
2 dB more noise out for the same or a larger loss of speech, which is the trade the engine has
already shown it does not like (section 2).

Settings prepared (fixed for all calls, not run): `n3-d2-w50`, `n3-d4-w50`, `n3-d4-w80`, each
through setting c's cutting. If run anyway (42 transcriptions, 3 at once, about 10 to 15 minutes
with cutting; baselines from the cached `su/solve-gate` and `N1\n1\c` rows, never measured again):

    for s in n3-d2-w50 n3-d4-w50 n3-d4-w80; do py -P n3.py run "%USERPROFILE%\Amanu Quality Set\lab" $s; done

(from `windows/tests/quality/lab`; rows land in `...\lab\wer2\n3\<setting>`, `n3.py show <work> <setting>`
prints the table with the paired bootstrap against solve-gate and against n1 setting c.)
Bar: 1.5 points under 27.2 with an interval that excludes 0.

The track was dropped on the coordinator's decision: the larger models remove as much speech as the baseline at SNR -5 (more, in fact), so no screening was run; the N3 cache was deleted and n3.py stays only to reproduce the check.
Run 2 plan (suite echo calls, 40 echo-* plus aec-real = 41, solve-gate-cut only, one worker because the build ran two transcribe-cli per job in run 1): about 60 minutes expected. solve-gate's cached rows are read, not re-run.


## 17. Gain floors by band (overnight, track N4)

Hypothesis: a classic Wiener gain per bin and frame with a floor, the noise spectrum taken from the gate's closed
stretches only, takes noise out of the cleaned track without the speech loss of a neural denoiser; then n1 setting c's
cutting. Step 1, CPU only; no transcription was run. `n4.py` is the script (`check`, `noise`, and the prepared `run`, `show`).

Design: input is N1's cached solver output `e` and gate `g` (no echo removal recomputed). STFT 512/128, sqrt-Hann. Noise
power per bin = mean power of the frames inside closed stretches (0.15 s guard), Gaussian-weighted in time (sigma 4 s) around
each frame, smoothed over 5 bins; a frame with almost no closed stretch within reach falls back to the call's mean closed
spectrum. Gain = xi/(1+xi) with xi = max(P/N - 1, 0), P smoothed 3x3, then floored, smoothed 3x3 again, floored again.
Output = g * filtered(e), then setting c's regions. Only mic and far (through `e` and `g`).

Settings: `f6` flat floor -6 dB, `f12` flat -12 dB, `bd` -15 dB outside 250..4000 Hz and inside it -15 dB (noise not like speech)
to -6 dB (babble), by the share of closed-stretch noise energy in 300..3000 Hz (0.45 -> 0, 0.75 -> 1). The share is a weak
measure: cafe 0.66-0.69, office 0.34-0.42 but 0.75 in one office call whose closed stretches hold far-end residual.

Signal check (`py -P n4.py check`; truth only measures. The gain chosen from the mixture is applied to my speech part and to
the rest separately). Mean per noise, SNR -5 / 0 / no noise; speech = level change over my speech frames (dB), SNR gain =
inside setting c's regions (dB):

| Call set | f6 speech / SNR gain | f12 | bd |
|---|---|---|---|
| cafe -5 (x3) | -3.18 / +1.09 | -3.86 / +1.89 | -3.77 / +1.95 |
| office -5 (x3) | -0.21 / +1.79 | -0.25 / +2.11 | -0.26 / +2.17 |
| cafe 0 (x3) | -1.70 / +2.29 | -1.86 / +3.33 | -1.83 / +3.28 |
| living 0 | -1.33 / +2.69 | -1.47 / +3.91 | -1.49 / +4.13 |
| office 0 (x3) | -0.11 / +0.81 | -0.13 / +0.97 | -0.13 / +0.99 |
| street 0 | -0.54 / +3.23 | -0.58 / +4.10 | -0.59 / +4.25 |
| no noise (6) | -0.05..-0.00 / +0.25..+0.54 | same | same |

No-noise calls change by under 0.1 dB in speech level (bar 0.5). Living and street exist only at SNR 0.

Verdict (stop rule): at SNR -5 no setting gains 2 dB of SNR while keeping speech within 1 dB on every noise. Office meets it
(f12 +2.1, bd +2.2 at -0.25 dB) but office -5 already stands at about 8-9 % WER with the gate (ideal 8-10), so there is nothing
to win there. Cafe, where the 40-60 point errors are, loses 3.2 to 3.9 dB of speech for 1.1 to 2.0 dB of SNR: the gain cannot tell
babble from speech in the same bins, so every dB of noise it takes out costs about 2 dB of speech, the same trade as the
neural denoisers. Not worth engine time. Run time of `check`: 17 min for 20 calls and 3 settings on a shared CPU.

Prepared, not run (do not run: stop rule): 20 calls per setting, 3 at once, about 4 min per setting (n1 c's 20 rows took 287 s of
engine time, plus about 1 min of filtering), baselines from the cached rows:

    cd windows/tests/quality/lab && for s in f6 f12 bd; do py -P n4.py run "%USERPROFILE%\Amanu Quality Set\lab\overnight\N4" $s; done

Nothing was written under `overnight\N4` (the filter is fast enough not to cache).

### 14.1 Run 1: held-out set (52 calls)

Commands (AMANU_LAB_EXE set to overnight-cut\Amanu.exe), from windows\tests\quality\lab:

    py -P ho.py "%USERPROFILE%\Amanu Quality Set\lab" amanu:solve-gate-cut
    py -P ho.py "%USERPROFILE%\Amanu Quality Set\lab" show:solve-gate-cut,solve-gate solve-gate
    py -P hg.py "%USERPROFILE%\Amanu Quality Set\lab" solve-gate-cut,solve-gate solve-gate-cut solve-gate

Output (log: overnight\run1-ho.log). solve-gate's row is its cached one, not re-measured.

| Remover | WER | No noise | Echo words | Echo time |
|---|---|---|---|---|
| solve-gate-cut | 10.6 | 4.7 | 3 | 1458 s |
| solve-gate (cached) | 11.8 | 6.3 | 1 | 1320 s |

Paired difference, solve-gate-cut minus solve-gate: -1.3 [-2.4, -0.0]. By kind: steady -1.9 [-3.4, -0.5]; drift -0.1 [-2.6, +2.3];
change -1.8 [-4.7, +0.9]; SNR 0 and -5 -3.9 [-7.9, -0.9]. The lab's setting c was 10.0.
Wall time about 50 minutes for the run, from the first job to the last (not printed by the tools).

Concurrency note: during run 1 the three Amanu.exe jobs each had two transcribe-cli children at once (six in total),
so the build exceeded the three-engine cap. Nothing was killed. Run 2 used one worker for that reason.

### 14.2 Run 2: suite echo calls (solve-gate-cut only)

Command (one worker, lab folder on sys.path, log overnight\run2-su.log):

    py -P -c "import sys; sys.path.insert(0, r'<lab>'); import asr, lab; ... P(1).map(asr.amanu(W,'su/solve-gate-cut',n,...,'solve-gate-cut'), ns) ..."

ns = the 40 echo-* recordings. The first attempt (a script in TEMP) failed on the import path, and the retry above ran.
The 40 rows are in wer2\su\solve-gate-cut.

Results over the 40 mixed calls (paired with the cached su/solve-gate rows):

| Remover | WER | No noise | Echo words |
|---|---|---|---|
| solve-gate-cut | 16.0 | 8.9 | 5 |
| solve-gate (cached) | 18.1 | 10.4 | 4 |

Paired difference: -2.05 [-2.49, -1.68]. Echo time for the 40 calls: 532 s (solve-gate-cut; the cached solve-gate
echo time is not printed here).

aec-real was not run. The one-liner uses echo-* only and the aec-real truth.json has no transcript ("me") key,
so it fails in asr.amanu at Q.error_counts. aec-real is not scored by this method.
Double-talk voice dB and ERLE on aec-real are not printed by these tools; they need quality.py's signal scores,
which were not run (the full run was not requested).

### 14.3 Status

All engine runs of this task have ended. No transcribe-cli process of mine is running. The other processes on the PC
(s2q.py, n4.py) belong to other tracks and were left alone.

### 14.4 Suite signal scores of solve-gate-cut (not started)

quality.py can point at the test build: \--exe <Amanu.exe>\ (default: the published build). No code change is needed.
Planned command (with the test build, never --keep-audio, no other model):

    py -P quality.py run --name overnight-cut --only "^(echo-|aec-real)" --modes solve-gate-cut --yes --exe C:\Backup\CoDiNG\Amanu\amanu-windows\windows\artifacts\overnight-cut\Amanu.exe

Not started. The disk check failed: C: had 5.07 GB free (the coordinator asked for over 6 GB). Nothing was deleted to make room.
Engine at the check: zero transcribe-cli processes. Parallel jobs: quality.py has no option for them; the run would use the suite's own worker count, not set to 1 here.
Estimate, before any start: 41 calls (40 echo-* and aec-real), one model, about 60 minutes by the run-2 pace; to be confirmed when it starts.

## 13.1 Why the app is behind the lab

No engine run. Held-out, the app's `solve-gate-cut` 1153 errors against 1090 for the lab's setting c (10 908 words): 10.6 against 10.0 %. The worst calls against c (errors app/lab): ho-00 35/19, ho-37 91/76, ho-35 101/90, ho-long-2 109/101, ho-long-0 53/45, ho-31 18/10. Inserted words over the 52 calls: 177 against 164, deleted 168 against 133, so the app both adds and loses words.

Evidence for the cause. In ho-00 and ho-31 the app's text has English words at the end of the call that the lab's text does not ("mix of outdoor and indoor environments", "So the database startu to symbol ..."): babble from the noise, not my speech. I ran the app's echo stage on both calls (a throwaway test, `OfflineEchoAudio.Prepare` with the model, then read `mic.gate.json`, since removed) and the lab's gate for the same calls (`n1.gate_pieces` for setting c, and the same code on `solve-res15`):

- App (solve + 0.15 residual suppressor + gate) opens the gate on 125.0-127.2 s in ho-31 and 98.3-99.2 s in ho-00, which the lab's setting c does not (ho-31: 13 open runs and 11 regions against 12 and 10, one extra region 124.0-127.5 s). The lab's `solve-res15` gate, run on the same recording, gives exactly the app's 13 runs and 11 regions for ho-31. So the gate code agrees, and the input differs.
- The reason: `n1.py` gates the output of `pipeline.VARIANTS["solve"]`, the solver alone. The app's `solve-gate` (and so my `solve-gate-cut`) put the 0.15 residual suppressor first, as the lab's `solve-res15-gate`. The suppressor leaves leftovers of the far end and noise around them, the gate opens on them, and the engine hears the floored audio. That is a real difference between the port and setting c, and my section 13 did not name it.

Fix: `LocalVqeAssets.SolveGateCut` is now `EchoSolver(SpeechGate: true, CutRegions: true)`, with no suppressor floor, processor `echo-solver-v1-gate-cut`. With that, ho-31 matches the lab: 12 open runs each, regions identical except one start 0.3 s earlier (28.7 against 29.0 s), and the extra 124 s region is gone. ho-00 matches better (17 open runs each, regions 9 against 8: the app still opens a short run at 98.3-99.2 s, which gives a region 97.3-100.2 s, and splits 36.8/37.7 and ends the 100.5 region at 124.3 s where the lab's goes to 120.8 s and 140.1 s). These residual differences come from the C# solver and gate numerics (float arithmetic, order of the denoiser pieces), not from the region logic: the app's regions equal what `n1.regions` makes from the app's own gate, in both calls except for the count in ho-00 (9 against the lab's 8 on a different gate). Not measured: the numbers of the six calls above with the new build. Level scaling and the cap were compared by reading and by the unit tests (same formulas: -20 dBFS over the open samples, +30 dB limit, peak 0.97; cap 25 s with the quietest 100 ms of the last 40 %); no region of these calls reached the cap, so the cap did not matter here. Likely not the cause: `--timestamps word` against `auto` (differences in case and punctuation only; the scorer normalizes them).

Expected: the held-out set should come close to the lab's 10.0, but this is an inference from two calls. Confirm with the same command as section 13, with `AMANU_LAB_EXE` set to `windows\artifacts\overnight-cut2\Amanu.exe`, and a new tag (the old rows are for the floor version: move or delete nothing, use another work folder).

Engine cost. Finished runs leave no logs (`asr.amanu` deletes the run folder), so the only log is the running suite's `su\solve-gate-cut\aec-real.run\transcribe.log`: the mic track took 44 engine runs for 262 s of audio in 116 s wall, side by side with the call track (17 runs, 1013 s, 96 s) and other suite jobs: 2.6 s per run, against 5.6 s per 60 s chunk run on the call track. The lab's batch run alone: 14 regions, 88.9 s of audio, 8.5 s in one process (0.6 s per region, `n1.py` plumb, section 12). So the per-region process costs about 2 s more per region than the batch (the model load; the run's own `load_ms` is only printed in jsonl mode), about 10 regions per call, about 20 s per held-out call of 150 s, under load. No exact figure without an engine run.

One process for all regions with word times: not implemented. The binary's strings show the jsonl line as `{"file":..,"text":..,<segments>,"mel_ms":..,"encode_ms":..,"decode_ms":..}` with segments as `{"t0_ms","t1_ms","text"}` only, so jsonl has no word list (confirms `LocalCliWords`). The word lines (`  [%7.2f -> %7.2f] %s` and `words: %d`) belong to the plain output. Whether `--batch FILE --timestamps word` without `--batch-jsonl` prints them per file, and how the files are separated (the string `[%zu/%zu] %s` suggests a progress line before each file), can only be shown by one engine run on a two-file list; a parser for it would be about 30 lines. The alternative that needs no new engine behaviour: jsonl for the text and the segment times (segments are likely one per file, so words would be spread in the region), which loses word times. Proposal: one two-region batch run after the suite finishes.

### 14.5 Run A and run B (overnight-cut2, fixed port)

Run A (held-out, 52 calls, solve-gate-cut in overnight-cut2, 2 calls at once, one process per call through asr.amanu): about 60 minutes expected. Only solve-gate-cut because the port changed and its old rows (wer2\ho\old-cut1, moved aside, not deleted) no longer describe this build; solve-gate's 11.8 is cached and is not re-measured.


## 10.1 The gate in parallel pieces (overnight, track S2)

Float DPDFNet, no engine run. Code: `s2q.py` (`time`, `dec`), `s2cmp.py`, `s1q_lib.py` (functions of
`s1q.py`). Logs and the int8 models: `%USERPROFILE%\Amanu Quality Set\lab\overnight\S1`
(`dec.log`, `time2.log`). I deleted both signal caches (3.2 GB) on the coordinator's order after
the decisions were summed, so `s2cmp.py` and `s2q.py dec` need `s1q.py cache` and a re-run
(about 7 min and 20 min per configuration on 4 processes). C: had 7.8 GB free at the end.

How the pieces are cut today (`SpeechGate.Denoise`): cores of 30 s, each run on its core plus 2 s
of sound on both sides (so 13 % extra audio), fresh model state per piece, the context's output
thrown away. `p<c>` below is that with c s of context; `s<c>` is the same and also skips the model
on stretches of at least 1 s where the canceller's output (0.3 s mean power) is more than 30 dB
under its 99th percentile; the skipped stretches count as silence (gate closed), and the model
state restarts, with c s of the real sound as context, at the next stretch. Parallel runs use
a pool of processes that each hold a model (started before the clock).

Decisions (10 ms frames, gate as `solve-gate`). Reference for "today" is `p2`.

| Config | vs whole-call run: all 40 / noisy 14 | vs p2: all 40 / noisy 14 | worst call vs p2 |
|---|---|---|---|
| p2 (today) | 0.161 % / 0.444 % | 0 | 0 |
| p1 | 0.191 % / 0.530 % | 0.064 % / 0.182 % | 1.06 % |
| p0.5 | 0.206 % / 0.571 % | 0.058 % / 0.164 % | 1.05 % |
| s2 | 0.178 % / 0.444 % | 0.017 % / 0.000 % | 0.66 % |

All under the 1 % bar except one call each for p1 and p0.5. Skipping removes only 5.6 % of the
audio on average (up to 52 % in one call); in the cafe calls nothing is 30 dB under the speech
level, because the canceller leaves noise there. The model ran on 112.2 % (p2), 106.1 % (p1),
103.1 % (p0.5) and 111.0 % (s2) of the audio.

Time, the same 5 calls (812 s of audio), one run of each configuration, back to back, the
machine busy (other agents use all 16 threads; read the ratios, and the x4 rows are noisy):

| Config | Seconds | Ratio to today's pieces in 1 process (p2 x1) | Ratio to one run over the whole call |
|---|---|---|---|
| whole call, one stream | 135.7 | 1.39 | 1.00 |
| p2 x1 | 97.8 | 1.00 | 0.72 |
| p2 x2 | 56.4 | 0.58 | 0.42 |
| p2 x3 | 42.5 | 0.43 | 0.31 |
| p2 x4 | 44.0 | 0.45 | 0.33 |
| p1 x1 | 89.9 | 0.92 | 0.66 |
| p0.5 x1 | 94.0 | 0.96 | 0.69 |
| s2 x1 | 111.4 | 1.14 | 0.82 |
| s2 x2 | 82.1 | 0.84 | 0.61 |
| s2 x3 | 56.7 | 0.58 | 0.42 |
| s2 x4 | 110.1 | 1.13 | 0.81 |

Result: not a quarter. Processes give 0.58 (2), 0.43 (3), 0.45 (4) of the serial piece time, with
decisions identical to `p2` by construction (same pieces; the processes only change who runs them).
The 5 calls are 155 to 170 s each, so a call has 5 to 6 pieces and 4 processes cannot be full; a
quarter needs more pieces than workers and free cores, and the box had none. A shorter overlap
saves only the share of audio it removes (13 % to 3 %): 0.92 and 0.96, inside the noise of this
machine, for 0.06 % more differing frames. The skip does not pay on these calls (mean 5.6 % of audio
skipped, and the time got worse because the run then has uneven pieces; s2 x4 is contention).
Why the x4 gain stops: cores shared with other jobs, and the pieces are uneven (the last one is short).

Real on a 4-core call in the app: the app already runs `Parallel.For` over the pieces with
`ProcessorCount / 2` threads in one process, and I measured no gain from threads (brief, §6), so
the gain of 0.43 to 0.58 is the process gain, not the piece gain. Estimate for 4 cores: 2 workers,
about 0.58 of today's time; 3 workers about 0.45 if nothing else runs. Not a quarter.

The port would need: (1) a child process per worker (the app's own exe with a `--gate-piece`
mode, or a small helper) that holds one `OfflineSpeechDenoiser` and reads a float piece from a
pipe or a temp file and writes the denoised piece back; (2) `SpeechGate.Denoise` to send the 30 s
pieces to a pool of 2 to 3 such processes, with the same 2 s context, so the decisions stay as
today's. The first thing to check in C# is why threads do not help: if it is a lock inside
sherpa-onnx (one `OfflineSpeechDenoiser` per thread already), a process pool is the fix; if it is
ONNX Runtime's own thread pool (`NumThreads = 1` is set), look at that first.

Stop: two attempts did not reach a quarter, both for a reason I understand (cores are
shared and there are about 5 pieces per call; skipping and a shorter overlap save under 10 %
of the audio). Verdict: partial win, 0.43 to 0.58 of today's time for the same decisions.

Run A result (held-out, overnight-cut2): WER 10.2 (no noise 4.9, echo words 9, engine time 1115 s), against solve-gate 11.8 cached: -1.67 [-2.88, -0.33]. Old port 10.6, lab 10.0.

Run B (suite, 41 calls: 40 echo-* and aec-real, solve-gate-cut only, overnight-cut2, quality.py --exe): about 60 minutes expected. Only solve-gate-cut is in it because its signal scores are the ones still missing for the fixed build; solve-gate's cached values are read, not re-run.


Run B did not start. quality.py refused the model before any engine run: "unknown echo model: solve-gate-cut" (its list of known models in quality.py has solve-gate but not solve-gate-cut). Adding the name needs a code change in quality.py, which this task does not allow, so nothing was run and nothing was changed. The suite signal scores for overnight-cut2 are still missing.
C: free GB at the end: 7.74. transcribe-cli processes: 0.


### 10.2 Parallel gate pieces in the app (overnight, track P)

Verdict: no speed-up to port, because the app already runs the gate pieces on threads; the section 21 memory fix is done. Decisions and cleaned mic are bit-identical.

Threads do speed up in one process. `SpeechGate.Denoise` already cuts the track into 30 s cores with 2 s of context and runs them with `Parallel.For`, one `OfflineSpeechDenoiser` per task, `NumThreads = 1`, `ProcessorCount / 2` tasks. The "no gain from threads" of 10.1 is therefore not what this code does today. Gate alone (`SpeechGate.Apply` on the 760 s of ho-long-3, `DOTNET_PROCESSOR_COUNT` set to 2, 4, 6, 16, busy machine): 107 s and 134 s with 1 worker, 64 s with 2 (0.60), 50 s with 3 (0.46), 43 s and 46 s with 8 workers, the default on 16 threads (0.41). That is the lab's 0.43 for 3 processes; 8 threads in one process are as good, so a process pool would add nothing, and I did not build one. A 4-core PC gets 2 workers, about 0.6 of one worker. N = min(3, cores/2) would be slower than the 8 workers this PC has; the existing `cores/2` stays.

Changed (`SpeechGate.cs`, no default and no decision changed): `Pieces(length)` and `Workers(pieces, processors)` are now small internal functions, so that the arithmetic can be tested; `Gain(..., inPlace)` writes the gain over the denoiser's array (`Apply` uses it) and the 30 ms ramp reads the step function directly instead of a full-length `steps` array. At the gate's peak a call holds the audio and the denoiser's array, two arrays, where it held four. New test file `SpeechGatePiecesTests.cs` (pieces tile the track once with 2 s context, workers, in-place gain equals gain of a copy). Tests: 332 + 125 pass (`dotnet test Amanu.Windows.slnx`). Build: `windows\artifacts\overnight-par`.

Echo stage alone, `OfflineEchoAudio.Prepare` with `solve-gate-cut`, no transcription (the exe has no mode for it; a harness `GateHarness` in `overnight\P\harness`, compiled against the DLLs of overnight-cut2 and run next to each build). Old = overnight-cut2, new = overnight-par; old, new, old, new back to back, machine busy.

| Call | Frames differing (10 ms) | Region shift | Cleaned mic old - new | Gate s old (2 runs) | Gate s new (2 runs) |
|---|---|---|---|---|---|
| echo-cafe-ser-5-snr+0 (156 s) | 0 of 15637 | 0 ms | identical | 7.2, 7.5 | 7.6, 7.5 |
| echo-office-ser-15-snr+0 | 0 of 17149 | 0 ms | identical | 7.7, 7.6 | 7.8, 7.8 |
| echo-street-ser-5-snr+10 | 0 of 17149 | 0 ms | identical | 8.6, 15.0 | 9.4, 13.7 |
| echo-living-ser-5-snr+0 | 0 of 15637 | 0 ms | identical | 12.0, 12.2 | 9.9, 12.6 |
| 51 min (ho-long-0..3 joined, 241 regions) | 0 of 292175 | 0 ms | identical | 159.1, 150.5 | 171.0, 144.4 |

Gate time is unchanged, as it must be: the parallel pieces were already there. Peak working set of the whole Prepare on the 51 min call: old 1587 and 4674 MB, new 831 and 1317 MB (two runs each; the working set follows the collector, so the spread is large). This mixes two changes, the section 21 solver fix, which the cut2 build lacks, and the gate's arrays; I did not run the gate change against the section 21 build alone. The gate's own arrays on two hours: 4 x 460 MB before, 2 x 460 MB after, by the array count, not measured at 2 h (disk; the longest input here is 51 min, no 2 h input is left).

Win bar (< 0.5 % frames, gate <= 0.6x): frames met (0 %); the time bar is not met because there is nothing left to cut by this route. Work files in `overnight\P` deleted.


## 13.2 Which signal the engine gets (overnight)

Code (variant cut3): `solve-gate-cut` now gates the solver's plain output (as overnight-cut2 did) but gives the engine the audio from behind the 0.15 residual suppressor (as solve-gate does). `SpeechGate.Apply` has an overload with a separate `detect` signal; `OfflineEchoAudio.Prepare` passes the plain solver output as `detect` when the model cuts regions. `LocalVqeAssets.SolveGateCut` is `EchoSolver(0.15, SpeechGate: true, CutRegions: true)`, processor `echo-solver-v1-floor15-gate-cut3`. `quality.py` lists `solve-gate-cut` among its known echo models. Unit tests: 331 + 113 pass. Build: `windows\artifacts\overnight-cut3` (cut2 stays in `overnight-cut2` as built; the superseded `overnight-cut` was deleted). Reason for the hypothesis: cut2 (plain output for gate and audio) had held-out WER 10.2 but 9 echo words; the old build (gate behind the suppressor) had 10.6 and 3.

Run C (suite, 40 echo-* calls, overnight-cut2, tag su/solve-gate-cut2, one job at a time): 1 job (2 transcribe-cli), about 25 to 30 minutes, because the cut2 numbers on the suite's calls are needed beside cut3's and su/solve-gate-cut holds rows of an older build.

Run C result (cut2, 26 minutes): my WER 15.9, no noise 9.0, SNR 0 17.6, SNR -5 38.8, echo words 4, echo time 471 s. solve-gate (cached): 18.1, 10.4, 19.6, 42.8, 4, 1300 s.

Run D (same 40 calls, overnight-cut3, tag su/solve-gate-cut3, one job at a time): 1 job (2 transcribe-cli), about 26 minutes, because it is the same measurement for the cut3 build, to be read beside cut2's.

Run D result (cut3, 27 minutes): my WER 15.9, no noise 8.8, SNR 0 17.9, SNR -5 39.1, echo words 6, echo time 508 s. Against cut2 (15.9, 9.0, 17.6, 38.8, 4, 471 s): the same WER within noise, and two more echo words, so the hypothesis did not hold on the suite: giving the engine the audio from behind the suppressor did not bring the echo words down below cut2's 4 (solve-gate also 4). Since cut2 is equal or better and simpler, I took the cut3 change out of the source again (`git checkout` of the three source files; the patch is kept in the session scratchpad as cut3.patch); the code is cut2's behaviour. `windows\artifacts\overnight-cut3` stays as built, for reference.

Run E (quality.py, 41 calls, `--only "^(echo-|aec-real)" --modes solve-gate-cut`, overnight-cut2 build, one job at a time): 1 job, about 60 minutes, because cut2 is the better of the two and its signal scores (double-talk voice, ERLE on aec-real, echo time) are the ones missing.

Run E result (quality.py, overnight-cut2 build, run folder `runs\overnight-cut`, 41 jobs, 27 minutes of jobs, report written with `report --run`): double-talk voice level -0.05 dB (mean of the 40 mixed calls; solve-gate from `runs\sonora-tuning`: -0.41), ERLE on aec-real 33.7 dB (solve-gate 33.7), echo removed on the 40 calls 28.7 dB (solve-gate 30.1), echo time for the 40 calls 448 s (solve-gate 427 s), my WER 15.9 (solve-gate 18.4 in that run), echo words 3 (solve-gate 3). aec-real time 68.8 s (solve-gate 53.6 s).

Summary of 13.2: cut3 is not better than cut2 on the suite (same WER 15.9; echo words 6 against 4), so the source keeps cut2's behaviour (gate and audio both on the solver's plain output) and `windows\artifacts\overnight-cut2` is the build for a final held-out check. quality.py now knows `solve-gate-cut`. All engine runs of this task have ended; no transcribe-cli process of mine is running.


## 20. Cutting without the gate (overnight, track C)

Plan for the held-out run (written before it): setting A only, 52 calls, 3 at a time, one transcribe-cli process each, about 20 to 30 minutes of engine plus solve-floor recomputed per call. Why: on the 20 screening calls A is 1.8 points under solve-floor (no-noise better), the pre-agreed bar of 1.5. Run: `C1_SET=ho py -P c1.py "%USERPROFILE%\Amanu Quality Set\lab\overnight\C\ho" run:A`.


## 19. A distilled gate (overnight, track S3)

Hypothesis: a small classifier on log-mel frames copies DPDFNet's gate decision at a quarter of its time with under 3 % of
frames different on the loud calls. Verdict: **loss on agreement, large win on time**. After the first run failed I made the
one allowed change; it helped but did not reach the bar, so I stopped (stop rule). No engine was run.

What it is. `s3d_train.py` `Gate`: a Conv1d (65 to 96 channels, kernel 5), a 2-layer bidirectional GRU (64 per direction) and a
linear output, 168 129 parameters, ONNX 677 KB (`gate-a.onnx`, `gate-b.onnx`). Input per STFT frame of
`pipeline.speech_gate` (512 samples, hop 256, so one frame is 16 ms): 64 log-mel values and the frame level re its
95th percentile. Output: how many dB DPDFNet keeps in the frame (10 log10 of the power of its output over the power of the input,
clipped to -70..+5). The gate arithmetic of `pipeline.speech_gate` (smoothing, 25 dB under the 99th percentile, 0.3 s
minimum, 0.3 s hang, ramps) then runs on input level + predicted kept dB (`gate_active`, `gain_from_active`; with the TRUE
kept dB it reproduces the real gate exactly, 0 frames differ, `s3d_eval.py check`). It is regression of a level, not a
yes/no, because the real gate's threshold is relative to the call's own 99th percentile. Look-ahead: it is bidirectional
inside 30 s pieces with 2 s context (as the app's pieces), so up to 2 s, not a few hundred ms.

Training data (strict). Only N5's files, read-only: FLEURS Slovak *train* speech (`N5\data\speech-files.tsv` as it stood at
23:00 on 11 Nov, sha256 of the tsv starts df44d460b5a0f44150ac; 295 utterances in my training pool, 16 in my validation pool;
N5's filter drops any sentence id, file name or text of the suite's FLEURS test list and the held-out set's dev list) and DEMAND
PSTATION and OHALLWAY, 4 channels each (`noise-files.tsv`, sha starts 09a14b7a07d21793d437).
**Licence: DEMAND is CC BY 4.0 (zenodo 1227121) and FLEURS is CC BY 4.0. A model trained on them could not ship without a
licence decision (attribution at least).** Nothing was downloaded or installed; PyTorch 2.15 CPU came from N5\pylib.
`s3d_data.py`: 201 training mixes and 8 validation mixes of 60 s (3.5 h of audio). Each has a near talker (utterances with
gaps, plus 0 to 3 stretches of 5 to 22 s of silence; 12 % of mixes are sparse), in 75 % of mixes a far-end-like leftover
(another train talker through a diffuse reverb, 8 to 45 dB under my speech, in 1 to 4 stretches), and noise = babble of 2 to 8
train talkers through reverb, DEMAND, both, or coloured noise (N5's `Pool.noise`) at SNR -8..3 dB (38 %), 3..12 (20 %), 12..30 (27 %),
30..55 (15 %); 30 % of mixes have noise that changes level during the call; overall level -38..-14 dBFS. Each mix was labelled by the
float DPDFNet baseline, 1 thread, the whole mix as one stream (the lab's `denoise.dpdf` path). Only int8 features, the frame level
(float16) and the int8 label were kept: 51 MB in `%USERPROFILE%\Amanu Quality Set\lab\overnight\S3\data`; no audio on disk.
Validation mixes use the validation speech and the last 20 % of each DEMAND channel (N5's split). No suite, held-out or
`near.wav` audio, no truth.

Training. 2 threads, CPU, machine busy: 3.6 s a step (12 windows of 24 s), 55 min = 913 steps, cosine lr 2e-3 to 1e-4, checkpoint
every 5 min (`S3\ckpt`, `train.log`). Disagreement of the 16 ms active flag on my 8 validation mixes: 11.9, 8.4, 6.2, 4.1, 4.4, 3.9,
3.9, 3.8, 4.2, 3.7 %, final 3.59 % (one mix at SNR -1.6 dB: 18.6 %; the others 0.2 to 4 %).

Run 1 (`gate-a`), the suite's 40 mixed echo calls. Solver output and DPDFNet output come from the N2 cache (read only); the real
gate is `speech_gate(e, c)`; a 10 ms frame is open when the mean gain is above 0.5 (as S1). Regions are n1 setting c (merge 1.5 s,
pad 1.0 s).

| | frames differ | regions real / distilled | seconds real / distilled | real region time covered | extra |
|---|---|---|---|---|---|
| all 40 calls | 9.84 % | 362 / 410 | 4023 / 4716 | 99.76 % | 703 s (17.5 %) |
| the 14 loud calls (SNR 0, -5) | 19.76 % | 139 / 131 | 1448 / 1816 | 99.69 % | 372 s (25.7 %) |

Where it fails: the cafe babble calls. At SNR -5 and 0 the distilled gate is open 89 to 98 % of the time where the real one is open
39 to 55 % (cafe ser-15 snr-5: 58.9 % of frames differ). It errs on the safe side (it covers the real regions) but sends 55 to 70 %
more audio to the engine in those calls. Office and street calls: 0.0 to 2.6 % differ with the echo at ser -5 or +5, but 3.8 to 6.9 %
with the echo at ser -15 (far-end leftover). My reading, argued and not measured: the model has not learnt what DPDFNet removes in
cafe babble (the training babble is Slovak read speech of 2 to 8 talkers, not a cafe recording), and 900 steps are few.

The one stated change (`gate-b`, `py -P s3d_train.py train 1500 b`, 25 min, started from `gate-a`): mixes with SNR under 5 dB three
times as likely, frames where DPDFNet removed more than 20 dB weigh three times as much, lr 6e-4 to 1e-4, 450 steps. Validation 3.43 %.

| | frames differ | regions real / distilled | seconds real / distilled | real region time covered | extra |
|---|---|---|---|---|---|
| all 40 calls | 6.72 % | 362 / 411 | 4023 / 4480 | 99.26 % | 487 s (12.1 %) |
| the 14 loud calls | 13.53 % | 139 / 136 | 1448 / 1694 | 98.98 % | 260 s (18.0 %) |

Better (loud calls from 19.8 to 13.5 %), still far from 3 %. Cafe snr -5 calls: 35 to 47 % differ; snr 0: 20 to 23 %. Per call numbers:
`S3\eval-a.log`, `eval-b.log`, `eval-a.json`, `eval-b.json`. Stop rule: one failure, one change, no pass; I stopped.

Time (`s3d_eval.py time`; the 5 calls of S1/S2, 812 s of audio; back to back, best of 2; machine busy; 1 thread each). The real gate in
the app's pieces (30 s + 2 s context, `s2q.compute`, plus the gate arithmetic) 184.4 s; the distilled gate 5.22 s = **0.028**
(features 2.2 s in numpy, ONNX model 1.0 s = 0.005, gate arithmetic 2.0 s). A C# port of the features (a 512-point FFT per 16 ms and
64 mel sums) would cost about what numpy does. So time is no problem even at ten times the model size; agreement is.

Not tried, with the reason I expect each to matter: (1) babble that looks like a cafe: more talkers (up to 20) and crowd noise other
than PCAFETER (the suite's own); the lab has none and I may not download. (2) A much longer run (913 steps on a busy PC are about 12
epochs of 201 mixes). (3) Predict the open/closed flag directly with a focal loss next to the regression. (4) Use it as a pre-filter:
where it says closed the real gate is closed (98.98 to 99.76 % of the real region time is covered), so DPDFNet could be skipped on the
stretches it calls closed, restarting at the next stretch as S2's skip does. In the cafe calls it is open 90 % of the time and saves
nothing; office and street calls (open 50 %) would save about half of the 17 s per minute. Not measured.

Commands. Reproduce: `py -P s3d_data.py A 1800 8` and `py -P s3d_data.py B 1800 0` (two processes), `py -P s3d_train.py train 3300 a`,
`py -P s3d_train.py train 1500 b`, `py -P s3d_train.py export b`, `py -P s3d_eval.py eval <onnx>`, `py -P s3d_eval.py time <onnx>`.
Engine check of setting c with the distilled gate on the 14 loud + 6 no-noise calls (not run; the cache is 21 MB a call, 420 MB for
20 calls, so use a work dir on a disk with room and delete it afterwards):
`py -P s3d_cache.py <work dir> "%USERPROFILE%\Amanu Quality Set\lab\overnight\S3\gate-b.onnx"` then `py -P n1.py <work dir> run:c`
(the baseline rows are N1's setting c and su/solve-gate). I would not spend engine time on it: the gate is not good enough to replace
DPDFNet on the loud calls.

### 20.1 Result

`lab/c1.py`. Regions from a cheap detector on the cleaned mic of solve-floor (the lab's `solve-res15`, run again per call; its cached rows `su/solve-res15` are the baseline, 29.4 on the 20 calls, because no `su/solve-floor` rows exist in the lab cache), then n1.py's setting c cutting (merge 1.5 s, pad 1.0 s, -20 dBFS per region, `--batch`). Fixed settings, mic and far only. Detector E: 16 ms frame energy against a 10th-percentile floor over 10 s, 3 dB margin, runs under 0.25 s dropped, 0.3 s hangover. Detector F: the same on 300-3400 Hz, and spectral flatness of that band below 1.5 times its sliding median. Time per call (156 s of audio, other jobs running): E 0.11 s, F 0.17 s.

Signal check against the gate's regions (20 calls, 2051 s of gate region time). First try, margin 6 dB, flatness below the median: E covered 63 %, F 64 %, and in the cafe babble at SNR 0 and -5 only 5 to 22 % (the detector does not open; frame energy cannot tell a voice at 0 dB from babble), and F lost 13 % in the no-noise calls. So the margin went to 3 dB and F's flatness test was loosened before any engine run (one signal tuning round, the engine saw only the final settings). Final: E covers 92 % of the gate's region time, F 89 % (cafe with noise 84 and 77 %), but the extra time is 1001 s (E) and 775 s (F): the detectors open on about 60 to 70 % of the call, the gate on about 65 % of the loud calls and the cuts are far less selective.

Screening, 20 calls (14 loud + 6 no noise), my WER %:

| | SNR 0 | SNR -5 | 14 loud | no noise | all 20 |
|---|---|---|---|---|---|
| solve-floor (lab, su/solve-res15) | 24.8 | 54.7 | 37.6 | 10.2 | 29.4 |
| solve-gate | 19.6 | 42.8 | 29.6 | 10.4 | 23.8 |
| n1 setting c | 17.6 | 40.0 | 27.2 | 9.2 | 21.8 |
| A (E on solve-floor, cut) | 24.4 | 50.5 | 35.6 | 8.9 | 27.6 |
| B (F on solve-floor, cut) | 30.2 | 48.0 | 37.8 | 12.4 | 30.2 |

A against solve-floor: -1.8 [-3.8, +0.4], no noise 1.3 better; against solve-gate +3.8 [+1.6, +6.1]. B: +0.8 [-2.5, +3.6] against solve-floor, no noise 2.2 worse, so B fails both guards. C (silence sends nothing) was skipped: cut pieces already do that.

Held-out set, A only (52 calls, no tuning): 12.4 against solve-floor 13.0 (-0.7 [-2.1, +0.9]), solve-gate 11.8 (+0.6 [-0.7, +2.0]), setting c 10.0. Loud calls 27.8 (floor 31.4, gate 27.2), no noise 5.5 (floor 7.4, gate 6.3).

Verdict: unclear, leaning loss. The cheap cut gets about a third of the gain of the gated cut on the suite and a third on held-out, with intervals that include zero. Why: in loud babble the detector is blind (it misses speech) or open everywhere, so the gain of setting c comes mostly from where the gate is closed over babble and noise only, plus the gate's own noise suppression (solve-gate cleans loud calls to 29.6 before cutting). Not tried: a detector that uses the far track (open only where far is quiet), a detector on solve-gate's mask for loud calls only. Rows: `...\overnight\C\wer2\n1\{A,B}` and `...\C\ho\wer2\n1\A`; solve-floor outputs cached in `...\C\cache` (20 calls). All engine runs of this track have ended.

### 19.1 WER of the distilled gate (gate-b), setting c, 14 loud + 6 no-noise calls

Run: `s3d_cache.py` then `n1.py run:c` on work dir `S3\w` (the plain solver output gated by gate-b, so only the gate differs; 3 transcriptions at once, none running before). Rows `S3\w\wer2\n1\c`; `s3d_cmp.py` compares them with N1's setting c rows.
My WER, distilled gate / setting c with the real gate / solve-gate: loud 14 28.5 / 27.2 / 29.6; SNR 0 18.2 / 17.6 / 19.6; SNR -5 42.4 / 40.0 / 42.8; no-noise 6 9.7 / 9.2 / 10.4; all 20 22.9 / 21.8 / 23.8.
Paired difference against setting c on the 20 calls: +1.06 points, 95 % interval [+0.28, +1.93]. Against solve-gate: -0.9 [-1.5, -0.3].
Engine seconds: 333 s against setting c's 287 s (+16 %, the extra region time). Win was within 0.3 points of setting c: this is a **loss** (it keeps about half of setting c's gain over solve-gate); the cut audio is cheap to make, but the gate decisions of section 19 cost the words.
All engine runs have ended (no transcribe-cli process); the 420 MB cache and temp WAVs are deleted; the held-out set was not run.


## 18. A tiny mask trained for the engine (overnight, track N5)

Hypothesis: a small bidirectional GRU on level-normalized log-mel features, trained to predict the ideal ratio mask with a floor of 0.3 on
babble, takes more noise out of the solver output than it takes speech, and then n1 setting c's cutting beats 27.2. Step 1, CPU only; no
transcription was run. Scripts: `n5.py` (`data`, `train`, `check`, and the prepared `run`, `show`), `n5_fetch.py` (the data).

Training data (all in `%USERPROFILE%\Amanu Quality Set\lab\overnight\N5\data`; lists with SHA-256 in `speech-files.tsv` and `noise-files.tsv`):
- Speech: FLEURS Slovak **train** split, the first 311 files of `train.tar.gz` (0.92 h, read as a stream, 160 MB; a first stream of 350 MB was
  wasted, see below). None has a sentence id, file name or normalized text that occurs in the suite's test list (`downloads\fleurs-sk-test.tsv`)
  or the held-out set's dev list (`heldout\downloads\fleurs-sk-dev.tsv`): 0 dropped. Every 20th file by sorted name (16) validates, 295 train. The set
  hash over names and FLAC hashes is `n5.py data`. Speed perturbation 0.88..1.12 on half the utterances.
- Noise: DEMAND PSTATION and OHALLWAY (CC BY 4.0, zenodo 1227121), channels 01/05/09/13 of each, 300 s each; the first 80 % trains, the last 20 %
  validates. The suite uses OOFFICE, PCAFETER, STRAFFIC, DLIVING and the held-out set OMEETING, PRESTO, DKITCHEN, SPSQUARE, so neither is used.
  Plus babble: sums of 2 to 8 FLEURS train talkers, each through its own diffuse reverb (RT60 0.4..1.1 s, DRR -6..+4 dB), a bank of 80 x 20 s, and
  coloured noise. Mix: 45 % babble, 25 % DEMAND, 15 % both, 7 % coloured, 8 % babble and coloured. My speech gets a light room on half the windows;
  SNR against my active power -8..3 dB (60 %), 3..15 (25 %), 15..35 (15 %). Windows of 10 s, features computed on the fly, nothing cached.
- Never used: the suite's or the held-out set's audio, near.wav, the noise track or truth inside the mask (at run time it sees the solver output `e` alone).

Model: Linear(128, 96), 2 x bidirectional GRU(128), Linear(256, 257), mask = 0.3 + 0.7 sigmoid; 548,449 parameters. STFT 512/128 as in `n4.py`. Input per
24 ms frame: 64 log-mel bands (dB) minus the 95th percentile of the window's frame energy, and the mean of the same over the window's loud frames
(within 12 dB of that percentile), as the call's long-term spectrum. The gate's open frames are not used: the loud-frame mean is self-contained and the
same in training and in use. Loss: mask against max(IRM, 0.3), weighted by the mixture magnitude^0.6 and by `ASYM` where the mask is under its target.
Use: windows of 10 s, hop 8 s, cross-faded.
- Run 1 (`ASYM` 2): AdamW, 2 threads, 3141 steps of 12 windows in 6601 s (110 min), cosine lr 2e-3, checkpoints every 10 min. Validation (24 windows,
  proxy from the true parts) ended at speech -1.54 dB, SNR gain +4.63 dB.
- Run 2 (the one change of the stop rule, `ASYM` 6, fine-tune of run 1 for 2400 s at lr 5e-4, `ckpt\a6-final.pt`): 1561 steps; validation -1.02 dB / +4.01 dB.
- Mistake on the way: the first training ran about 10 minutes on silence, because FLEURS wavs are 32-bit float and I read them as int16. Caught by the
  validation (NaN) and fixed; that log is `train-bad-int16.log`. The downloads came to about 1 GB in all (torch twice 126 MB, 350 + 160 MB speech, 197 MB noise).

Signal check (`n5.py check`, logs `N5\check.log` and `check-a6.log`; truth only measures; the mask chosen from the mixture is applied to my speech part and
to the rest separately, as in section 17). Mean over calls; speech = level change over my speech frames, SNR gain inside setting c's regions, both dB:

| Call set | run 1 (ASYM 2) | run 2 (ASYM 6) | n4 f12 (section 17) | DPDFNet baseline raw, speech level (section 16) |
|---|---|---|---|---|
| cafe -5 (x3) | -4.01 / +3.31 | -2.74 / +3.30 | -3.86 / +1.89 | -5.9, -8.3, -5.9 (mean of cafe+office -5: -6.71) |
| cafe 0 (x3) | -2.29 / +4.18 | -1.45 / +3.77 | -1.86 / +3.33 | -2.35 |
| office -5 (x3) | -0.25 / +9.83 | -0.14 / +9.62 | -0.25 / +2.11 | about -0.1 |
| office 0 (x3) | -0.18 / +9.46 | -0.10 / +9.03 | -0.13 / +0.97 | |
| living 0 | -1.28 / +6.36 | -0.79 / +6.03 | -1.47 / +3.91 | -0.56 (section 16) |
| street 0 | -0.65 / +8.11 | -0.39 / +7.83 | -0.58 / +4.10 | |
| no noise (6) | -0.04..-0.14 / +0.5..+1.4 | -0.04..-0.07 | under 0.1 | -0.26 |

No-noise calls change by under 0.15 dB in speech level (bar 0.5). DPDFNet's SNR gain is not comparable: `check` also prints it by projection on
near.wav (+6 to +9 dB at cafe, +23 to +28 at office), but the projection counts everything the denoiser removed from my speech as removed noise.
Section 16 gives only its speech level, and that is the figure to compare: the mask loses 2.7 to 4.0 dB at cafe -5 against 6 to 8 dB.

Verdict (stop rule): after run 1 the mask gave 3.3 dB of SNR gain at cafe -5 (bar 3) but lost 4.0 dB of speech (bar: under 2). One change (`ASYM` 6) cut the loss to 2.7 dB
at cafe -5 and 1.45 dB at cafe 0 for the same 3.3 dB of gain at -5 (3.8 at 0). Stopped there. The aim of 1.5 dB is met at cafe 0 and every other noise, not at
cafe -5, where the loss is still 2.7 dB; the 3 dB gain is met. So it is better than every earlier denoiser on this check (it takes about 1.2 dB of speech for 1 dB of
babble at -5, section 17 took 2), but still on the same curve: cafe -5 is where the 40 to 60 point errors are and it is the one setting that loses speech. Whether the
engine likes that cannot be told without a run. Not tried: a longer training, more speech (0.9 h is small), a floor of 0.4, a loss on the engine's own features.

Prepared, not run (3 transcriptions at once, 14 noisy and 6 no-noise calls = 20; baselines `su/solve-gate` and `N1\n1\c` from the cached rows, never measured again;
about 5 min of engine time plus 3 min of mask on CPU, as for n4):

    cd windows/tests/quality/lab && py -P n5.py run "%USERPROFILE%\Amanu Quality Set\lab" n5-a6 a6-final && py -P n5.py show "%USERPROFILE%\Amanu Quality Set\lab" n5-a6

(`n5-a6` is only the row tag; use `final` as the last argument of `run` for run 1. Rows land in `...\lab\wer2\n5\<tag>`; `show` prints the paired bootstrap
against solve-gate and against n1 setting c. Bar: 1.5 points under 27.2 with an interval that excludes 0.)
Disk: N5 holds 645 MB (torch in `pylib` 534 MB, data 88 MB, checkpoints 21 MB). Nothing else was written.

### 18.1 Engine screen of the tiny mask

Command (no change to `n5.py` was needed): `py -P n5.py run "%USERPROFILE%\Amanu Quality Set\lab" n5-a6 a6-final` (3 transcriptions at once, 20 calls; rows in `...\lab\wer2\n5\n5-a6`; log `overnight\N5\run-a6.log`).
Result, "my WER", mask on the plain solver output, then solve-gate's gate and setting c's cutting:

| | SNR 0 | SNR -5 | loud-14 | no noise (6) | echo words |
|---|---|---|---|---|---|
| n5-a6 | 19.5 | 40.9 | 28.7 | 9.2 | 6 |
| n1 setting c (no mask) | 17.6 | 40.0 | 27.2 | 9.2 | |
| solve-gate | 19.6 | 42.8 | 29.6 | 10.4 | |

Paired bootstrap on the 20 calls: against solve-gate -1.0 [-1.9, -0.1]; against setting c +1.0 [+0.3, +1.9] (worse). Engine time 241 s (sum of batch seconds over 20 calls), plus the mask on CPU.
Verdict: fail. The bar was loud-14 <= 25.7 and no-noise <= 9.7; no-noise holds (9.2), loud-14 is 28.7, 1.5 points worse than setting c. The mask's 3 dB of SNR gain does not turn into fewer word errors, and it costs words at SNR 0 (19.5 against 17.6). The held-out run was not made.
Deleted: nothing was left to delete (the script removes its temp WAV folder after each call; no audio cache was made). Free space on C: 7.6 GB before and after.

## 21. The solver's memory (overnight, track G)

Result: win. A two-hour call took 5.9 to 7.2 GB of working set in the solver stage before (default collector, this PC) and takes 0.87 GB after for one echo path,
1.06 GB for eight; the output is bit-identical for `solve` and `solve-gate-cut`, and 150 dB under the signal (float rounding) for `solve-floor` and `solve-gate`.

What was long. Two hours are 115.2 M samples, 460 MB per float array. Before: the mic and the call track whole (2 x 460), then `Track` warped the call track
once per coarse section into a full-length array and copied it into a second full-length one (2 x 460 per section, thrown away), `Cancel` held the echo and the cleaned mic
(2 x 460), `Suppress` an output array (460), and every block of the fit (300 s: y, x, d, the power, two smoothings, the weights, 40 to 80 MB each) was new
large-object-heap garbage that the collector let pile up. Live size at the worst moment was about 2.3 GB (the brief's figure; not measured again); the working set was far above it because the collector
ran a full collection only every few blocks. The new code runs under `DOTNET_GCHeapHardLimit` of 1.25 GB and fails at 1 GB (fragmentation); its live size by forced collection is 0.8 to 0.88 GB.

What changed (`OfflineEchoSolver.cs`, `OfflineEchoAudio.cs`, one new test):
- `ITrack` (read a piece at any place, silence outside) replaces whole arrays inside the solver; `ArrayTrack` keeps `Cancel(float[], float[])` and the lab/test callers as they were. The app reads
  the decoded float wave files through `FileTrack` (positional, under a lock). `Warp` reads only the stretch of the call track its output reaches; the fine delay pass makes the warped call track a window at a time (`WarpedTrack`) instead of as a 460 MB array; `GccPoints` streams the call track's power. Same sums in the same order, so no number moved.
- `OfflineEchoSolver.Solve(mic, far, floor)` is `Cancel` and `Suppress` for tracks: the echo array becomes the cleaned mic in place, `Suppress` runs in place (`SuppressInPlace`, which keeps the
  2.5 s of input the block before has already overwritten), and the echo it needs is mic minus cleaned. That is the only inexact step (one float rounding); everything else is the same arithmetic.
- `InverseResidualPower` smooths one bin at a time from y and d instead of building three double arrays the size of y; the second round's weights, which nothing used, are not made.
  `SuppressBlock` makes powers from the spectra where it needs them instead of four double arrays, keeps its gains in a float array, and gives each thread its four 300 KB rows (new rows per bin were 300 MB of large-object garbage per block).
- `GC.Collect` between blocks (`CollectBlockGarbage`): without it the first version had 0.8 GB live and 4 GB working set.

Proof, old build against new build, `OfflineEchoAudio.Prepare` end to end (no transcription), harness in `overnight\G\harness`, `cmp_run.py`; 8 calls x 4 models = 32 pairs, none differs by more than rounding:

| Calls | `solve` | `solve-gate-cut` | `solve-floor` | `solve-gate` |
|---|---|---|---|---|
| 6 suite echo calls (cafe, office, street, living; 171 s) and held-out `ho-long-3`, `ho-long-0` (760 s, 3 suppressor blocks) | bit-identical | bit-identical | -150.3 to -157.5 dB (max abs 6e-8) | -150.7 to -157.5 dB |

A 30-minute call joined from four held-out calls (4 echo paths, so the section joins and the in-place blocks are used): `solve` bit-identical, `solve-floor` -151.1 dB.
Unit tests: 331 + 113 + the new one pass (`dotnet test Amanu.Windows.slnx`, as BUILDING-HERE.md says).

Two hours, solver stage only (harness `solver` and `solver-new`, `solve-floor`, 16-bit input of 2 x 230 MB; the 2 h is a real held-out call tiled to 7200 s, plus a one-path synthetic):

| | peak working set before | after | time before | after |
|---|---|---|---|---|
| 1 echo path (synthetic, 7200 s) | 5.9 to 6.9 GB | 0.87 GB | 49 to 61 s | 52 s |
| 8 echo paths (held-out tiles, 7200 s) | 5.6 to 7.2 GB | 1.06 GB | 63 s | 58 s |
| 30 min, 4 paths | 4.2 GB | 0.66 GB | 14 s | 16 s |

Time is the same within the noise of a busy PC (ratio 0.93 to 1.06). Real calls take 1.0 to 1.1 of the old time on 760 s calls (6.8 against 6.4 s for `solve-floor`).

Left over. (1) The speech gate is outside the solver and still holds three arrays of the call's length (`Apply`: audio, the denoiser's output, the gain), so `solve-gate` and `solve-gate-cut` peak
near 1.4 GB on two hours at that step; making `Gain` write into the denoiser's array would take it to 0.92 GB. (2) The one stage that still holds a whole-call array is the cleaned mic (460 MB), because the gate, the cutter
and the writer want it; the rest of the solver is under 0.45 GB over it. (3) With several echo paths the block that waits for the next path's fit (`pending`, 77 MB) is the difference between 0.87 and 1.06 GB.
(4) Prepare itself writes 2 x 460 MB of decoded wave files and the same again for the output on two hours, so it was not run at 2 h here (disk); the solver stage numbers above are what it adds.

## 22. Stress set (overnight, track G)

Report-only set of 12 new calls for three cases the held-out set has too few of. Nothing may be tuned on it. Step 1 (CPU) is done; the transcription is pending and has not been run.
Script `stress.py` (new; `heldout.py` is untouched and its mixer is imported: `lay_out`, `response`, `load_noise`, `NOISES`, `PATTERNS`). Data in `%USERPROFILE%\Amanu Quality Set\lab\overnight\G\stress`
(`manifest.json` = the frozen recipe, `recordings\<name>` in the suite's format, `work\cache` = solver output `e` and gate `g` per call, `cpu_stats.json`, `cpu.log`). 162 MB of audio (mic, system, near per call), 214 MB of cache.

Material. Speech: FLEURS Slovak *test* sentences that none of the suite's 40 echo calls uses (the tuning set) and that are not in the held-out set (FLEURS dev; all 283 usable dev
files are already in the held-out manifest, so dev could not be disjoint). Noise: the held-out set's four DEMAND rooms (not the suite's), new offsets. Master seed 20261112;
room and noise seeds are in `manifest.json`. Calls are 112 to 140 s (drift, dev4) and 180 s (one); two drift/dev4 calls are a little under 2 minutes because the sentences drawn were short.

| Group | Calls | What is new |
|---|---|---|
| drift | `st-drift-0..3` | the call track runs against the mic's clock at a ppm that changes during the call: sine +-40 ppm (70 s), sine 20 +-40 ppm (45 s), steps 40/-40/15/-30/40 every 28 s, bounded random walk (peak 40); all ramped over 2 s; one room each (rev, direct, mid, rev), SER -5 to 0 to -10 dB, SNR 20, none, 10, 5 |
| dev4 | `st-dev-0..3` | four devices: echo path (kind, reverberation, latency 35/80/130/190 ms in shuffled order) changes three times, in the pauses between blocks nearest 1/4, 1/2, 3/4 of the call; SER -5, 0, -10, -5; SNR 10, 20, none, 0 |
| one | `st-one-0..3` | I say one sentence (8 words, 2.6 to 3.2 s of speech, picked among the 24 shortest-speech sentences) in 180 s, 1.4 to 1.8 % of the time; the far track talks all the rest (about 260 words), my sentence is over far speech in 0 and 2 and in an 8 s far pause in 1 and 3; SER -5, -10, 0, -10; SNR 5, 0, 10, 0 |

All have speaker drive 1.5 (tanh) as the held-out set's default. `truth.json` carries the group, `true_speech_s` (my speech by a 25 dB-below-peak frame detector, gaps under 0.3 s closed), the change points and the drift range.
Reference sanity check (not part of the set): `st-drift-2` rebuilt without drift gives ERLE 24.9 dB and with a constant 150 ppm 23.4 dB, so the low ERLE below comes from the wandering drift, not from the mixer.

CPU numbers (`py -P stress.py cpu`; solver = `solve.cancel`, plain Sonora if it reports many paths, which it did for none of the 12; gate = `pipeline.speech_gate` on DPDFNet, i.e. solve-gate = g * e; 26 to 45 s per call).
ERLE = echo power over (solver output - my voice - noise) power, on the stretches without my speech (1 s margin), power-weighted over the group:

| Group | ERLE of the solver | echo to the gated output | mic to solver output (noise included) |
|---|---|---|---|
| drift (4) | 6.6 dB (6.8, 9.5, 1.3, 5.4) | 6.4 dB | 6.0 dB |
| dev4 (4) | 21.2 dB (23.5, 21.9, 24.4, 17.5) | 7.8 dB | 8.0 dB |
| one (4) | 26.2 dB (24.6, 29.5, 24.4, 25.8) | 17.7 dB | 8.7 dB |

The solver loses 15 to 25 dB of echo cancellation to a drift that wanders (held-out constant drifts of 50 to 200 ppm are not like this; the section tracker fits one drift per section). Four devices cost it little (17.5 dB in the call with the 35 ms direct device last).
The last two columns include the noise floor (and in the dev4 group the noise-free call is the only one where the gated output can fall far under the echo), so they are not ERLE.

Setting c's regions in the 1 % calls (the gate, merge 1.5 s, pad 1.0 s, as `n1.py check`), seconds sent to the engine against my true speech seconds:

| Call | c regions | c seconds | a seconds | gate open | my speech | my speech inside c |
|---|---|---|---|---|---|---|
| st-one-0 | 3 | 12.4 | 8.2 | 6.4 | 2.6 | 94 % |
| st-one-1 | 4 | 22.5 | 16.5 | 13.5 | 3.0 | 87 % |
| st-one-2 | 10 | 166.8 | 157.2 | 145.0 | 2.8 | 100 % |
| st-one-3 | 4 | 17.6 | 12.7 | 10.5 | 3.2 | 100 % |
| total | 21 | 219 | 195 | 175 | 11.6 | |

So in three calls c sends 4 to 7 times my speech (12 to 23 s for 3 s), in `st-one-2` (direct room, SER 0, SNR 10) the gate is open 145 s of 180 and c sends 167 s: the far end's leftovers open it, which is the case where echo words would be expected. In the 8 other calls c sends 832 s of 1038 s (my speech 434 s). In `st-one-1` 13 % of my sentence falls outside c's regions (the gate clipped it).

Pending, engine (do not run while another job holds the engine): one command, solve-gate through the app's path (`Amanu.exe --measure-transcription`, echo removal off, on g * e from the cache; `asr.run_jobs`, 3 at a time)
and then setting c through `transcribe-cli --batch` (`n1.transcribe_regions` with the stress recordings and cache patched in), then the table per group with echo words:

    cd C:\Backup\CoDiNG\Amanu\amanu-windows\windows\tests\quality\lab
    py -P stress.py engine

Rows land in `...\G\stress\work\wer2\stress\solve-gate` and `...\n1\c` (cached, so a stop can be resumed by running the same command; `py -P stress.py show` prints the table again).
Expected time: audio is 1756 s of calls; the held-out runs cost 0.12 s of engine per second of call for c (1115 s on 9 500 s) and 0.14 for solve-gate, so about 4 min for solve-gate and about 4 min for c plus model loads: 8 to 12 minutes in all on this PC when the engine is free.
Not run yet; no WER exists for this set.

### 22.1 Engine result

Run finished (overnight-cut2 `Amanu.exe` for solve-gate, one at a time; `transcribe-cli --batch` for setting c; the first attempt was cut by low disk and resumed). My WER %, echo words; report only.

| Group | words | solve-gate WER | echo words | c WER | echo words |
|---|---|---|---|---|---|
| drift (4) | 585 | 27.7 | 18 | 47.2 | 105 |
| dev4 (4) | 564 | 19.3 | 1 | 11.9 | 0 |
| one (4) | 32 | 96.9 | 0 | 243.8 | 3 |
| all (12) | 1181 | 25.6 | 19 | 35.6 | 108 |

Per call (solve-gate / c): dev-0 7.1/6.3, dev-1 7.9/3.6, dev-2 10.7/4.0, dev-3 49.7/32.7, drift-0 9.9/38.0 (35 echo words in c), drift-1 51.9/68.2, drift-2 15.8/26.0, drift-3 31.5/55.2, one-0 25.0/50.0, one-1 0/0, one-2 275/850, one-3 87.5/75.0.
Reading: with four devices c is better; with wandering drift (solver ERLE 7 dB) c is much worse and passes the far end's words (105 against 18) because cutting hands the engine the echo leftovers the gate let through; in the 1 % calls the numbers rest on 32 words (insertions dominate; st-one-2, where the gate was open 145 s, gives 275 and 850 %). Cache deleted after the run.

### 22.2 The app's solve-gate-cut and the suppressed-audio variant on the stress set

Report only; nothing tuned. `stress.py app TAG` runs the app's own model `solve-gate-cut` through `Amanu.exe` (`AMANU_LAB_EXE`, `asr.amanu`, one job at a time, only the recordings needed) with row tag `stress/app-cut-TAG`; `stress.py appshow` prints the table. solve-gate rows are the cached ones from 22.1 (not re-measured). Builds: `par` = `windows\artifacts\overnight-par` (today's source, i.e. the shipped solve-gate-cut: gate and audio both on the solver's plain output), `cut3` = `windows\artifacts\overnight-cut3` (gate on the plain output, engine gets the audio from behind the 0.15 residual suppressor). The cut3 patch was hand-applied to the current source (the solver now returns one array, so for cut3 the plain output is kept, the echo is rebuilt as mic minus plain and `Suppress` is run on a copy), built, and the source restored with `git checkout -- windows/src`; the patch is in the session scratchpad as cut3.patch. The first start failed on both: overnight-par had no `local-runtime` ("No local model for 'auto'"); I copied it from `artifacts\publish` (a copy, nothing deleted) into par and cut3. My WER %, echo words:

| Group | words | solve-gate | app par | cut3 |
|---|---|---|---|---|
| drift (4) | 585 | 27.7 / 18 | 29.6 / 30 | 25.6 / 40 |
| dev4 (4) | 564 | 19.3 / 1 | 11.9 / 0 | 12.1 / 0 |
| one (4) | 32 | 96.9 / 0 | 225.0 / 4 | 290.6 / 5 |
| all (12) | 1181 | 25.6 / 19 | 26.4 / 34 | 26.3 / 45 |

Per call (solve-gate / par / cut3): dev-0 7.1/6.3/6.3, dev-1 7.9/3.6/3.6, dev-2 10.7/4.0/4.0, dev-3 49.7/32.7/33.3, drift-0 9.9/19.0/35.2 (28 echo words in cut3), drift-1 51.9/39.0/29.2, drift-2 15.8/26.0/12.3, drift-3 31.5/33.6/25.9, one-0 25.0/50.0/25.0, one-1 0/12.5/12.5, one-2 275/762.5/1050, one-3 87.5/75.0/75.0.
Reading: the app's cutting leaves 29.6 % on drift with 30 echo words, not the lab setting c's 47.2 % with 105 (22.1): the app differs from the lab's c (its own gate and regions, solver run of the app), so 22.1's c row overstates the app. Against solve-gate the app is still 1.9 points worse on drift (+12 echo words) and 7.4 points better on dev4; in the 1 % calls the 32 words decide nothing. cut3 on drift is 25.6 but with 40 echo words (28 in drift-0 alone), so the suppressed audio does not remove the echo words; it is not better than par on the 12 calls (26.3 against 26.4 overall, 45 against 34 echo words). Differences of a few points on 585 words are within the noise of one run.
Notebook numbers for cut3 (13.2, quoted): suite, 40 echo calls: WER 15.9 (cut2 15.9, solve-gate 18.1), no noise 8.8 (9.0), SNR 0 17.9 (17.6), SNR -5 39.1 (38.8), echo words 6 (4; solve-gate 4), echo time 508 s (471 s); the notebook concluded cut3 was not better than cut2 and took it out. Held-out: cut3 was never run on it (14.5 ran cut2 only; the docstring of the shipped model gives 10.0 % for cut2 against 11.8 % for solve-gate).
All engine runs have ended; the stress cache is gone and not needed.

## 23. Dropping regions that are mostly removed echo (overnight, track N6)

Verdict: no signal. Stopped after step 1 (CPU only); no engine run was made, so there is no engine time, no suite screen, no held-out run and no stress run.

Hypothesis: a setting-c region whose energy the solver mostly removed is echo, not me, so drop it (R < T) or do not scale it up (L < T2). R = energy of the solver output over energy of the raw mic, over the region's gate-open samples, in dB. L = the region's level before scaling (solver output, open samples) minus the call's median region level. Only mic, far and the solver output enter R and L. The label "holds my words" comes from `near.wav` (frames within 25 dB of its peak, gaps under 0.3 s closed) and is used for scoring only. The n1 rows keep only the joined text, not per-region transcripts, so labels come from truth timing: a region is "echo-only" when it holds under 0.5 s of my speech, and my words lost are counted as my speech seconds in the dropped regions (an estimate, words are not aligned).
Scripts: `n6a.py` (features, writes `overnight\N6\step1_all.json`), `n6b.py` (sweep). N1 cached only 20 of the suite's 40 echo calls (the 14 noisy and 6 no-noise ones); the other 20 (SNR 5, 10, 20) were computed into `N6\all` (about 12 min CPU) and that audio cache was deleted afterwards. 40 calls, 362 setting-c regions, 35 echo-only, 2784 s of my speech.

Result. The echo-only regions are of two kinds. In the noisy calls (cafe SNR 0 and -5 with SER +5 and -5) 24 of them have R between -3.4 and 0 dB: the solver leaves them alone because they are background talk, not echo, and the gate opened on them. In the calls with little noise and SER -15 (and office) 7 have R of -18 to -26 dB: these are real removed echo, short (about 1 s). My own regions in the SER -15 calls have R of -10 to -16 dB, because the echo there is strong and removed from under my voice, so R is low for them too. R separates nothing: my words lose 1.9 % of my speech at T = -14 for 7 of 35 echo-only regions dropped, 6.6 % at T = -12 (still 7 of 35), 26 % at T = -3 (12 of 35). L: T2 = -20 drops 3 of 35 at no loss, T2 = -6 drops 7 of 35, T2 = -4 drops 11 of 35 and still no loss, but the rest sit at L about -1 to 0 like my regions (T2 = 0 drops 33 of 35 and 39.8 % of my speech). No T drops half of the echo-only regions while losing under 0.3 % of my words: the best at 0.3 % is L < -4, 11 of 35 (31 %).
Reading: the solver's removal is the wrong sign for the held-out weak case. Leftover background talk is not echo to the solver, so it keeps its energy (R about 0), and where echo was removed hard, my own regions are also strongly reduced. A scale-down rule on level alone (L) would keep its no-loss property only up to about 30 % of the echo-only regions, and those are one-second sliver regions that the engine rarely turns into words. The held-out words (ho-27, ho-32, ho-13, ho-30) were not looked at, by the rules; a different feature would be needed (for example whether a region's spectrum matches the far end's, which is a different experiment).
All engine runs of this track have ended (none was started).
