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
