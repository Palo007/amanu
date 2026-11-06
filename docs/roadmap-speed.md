# Windows offline transcription: speed and quality roadmap

Started 2026-11-06. Source review: `docs/reviews/2026-11-06-speed-reliability.html`.
Do one step at a time. Each step ends with a measurement against the baseline, a
commit, and a tick here. Change only the ASR (speech recognition) copy of the audio,
never the archived tracks.

## Baseline (2026-11-06, CPU only, GPU idle)

| Recording | Length | Echo removal | Transcription | Total |
|---|---|---|---|---|
| `2026.11.06-1000` | 14 min | 120 s | ~240 s | ~6 min |
| `2026.11.06-1021` | 16 min | 136 s | ~250 s | ~6.5 min |

Engine `parakeet-sk`, mic + system tracks, LocalVQE echo removal on.

## Step 0: measurement first

- [x] Write per-stage timings to `transcribe.log`: decode, echo, ASR per track, total.
- [x] Add a small script (`windows/scripts/Measure-Transcription.ps1`) that runs one recording again and prints the timings and the word count.
- [ ] Keep 2–3 fixed test recordings, at least one Slovak call with a quiet speaker. For each, keep a hand-checked reference transcript of 2–3 minutes for WER (word error rate) checks.
  - Done: `2026.11.06-1000` and `2026.11.06-1021`, window 120–300 s. References are in `%USERPROFILE%\Amanu Speed Set\*.ref.txt` (outside git: private call text).
  - Quiet speaker and echo case: `2026.11.06-1021`. The mic is very quiet: speech alone about −45 dBFS (about 25 dB under the step 4 target), median −46, max −30. The call side is median −29, peak −5. The mic also picks up the call side (echo) about 16 dB under it. `gemini-web\me.m4a` and `them.m4a` in that folder are the same call, split, and are easy to listen to when correcting the reference.
  - Open: the references are drafts copied from parakeet-sk output. They must be corrected by hand.

Done when: one command gives the baseline numbers again within ±10%. **Done for timings (2026-11-06).**

How to measure (close Amanu first; the first run after a publish or reboot is ~30% slow, so run once to warm up and use the second run):

```
windows\scripts\Measure-Transcription.ps1 2026.11.06-1000 -Reference "$env:USERPROFILE\Amanu Speed Set\2026.11.06-1000.ref.txt"
```

It runs `Amanu.exe --measure-transcription <recording> <temp copy>`: the copy is transcribed with the local model only, and the recording itself is not changed. Timing lines in `transcribe.log` look like `timing asr.me 117.7 s (convert 0.0 s, 15 runs, preempted 0)`.

Step 0 result (warm run, AC power, Balanced plan, Amanu closed):

| Recording | Decode | Echo removal | ASR me | ASR them | ASR | Total | Words | WER vs draft |
|---|---|---|---|---|---|---|---|---|
| `2026.11.06-1000` | 2.4 s | 121.9 s | 117.7 s | 121.0 s | 238.7 s | 362.9 s | 1646 | 0.0 % |
| `2026.11.06-1021` | 2.2 s | 129.0 s | 126.5 s | 132.9 s | 259.4 s | 390.6 s | 2228 | 0.0 % |

Cold first run of `1000`: echo 164.8 s, ASR 306.8 s, total 475.3 s. WER 0.0 % only shows the output is the same as before; it is not a quality number until the references are checked by hand.

## Step 1: threads and priority (small)

- [ ] Find the thread option of the transcribe.cpp CLI. Pass an explicit count (`TranscriptionServices.cs:156`).
- [ ] Batch runs use BelowNormal priority, not Idle (`:232`). Live transcription keeps its priority.

Done when: the ASR time is measured with 2–3 thread counts, and the best count is the default.

## Step 2: both tracks at the same time (small)

- [ ] Run mic and system ASR in parallel (`TranscriptionServices.cs:104-123`), with half the threads each.
- [ ] Take `CpuTranscriptionGate` once for the pair (`CpuTranscriptionGate.cs:7`).
- [ ] Live preemption cancels both tracks.

Done when: ASR time is ≥25% lower and the transcripts are identical to step 1.

## Step 3: system track during echo removal (medium)

- [ ] Echo removal changes only the mic. Start system-track ASR on the original system audio while echo removal runs (`ProcessingCoordinator.cs:420-437`, `OfflineEchoAudio.cs`).
- [ ] Then run mic ASR on the cleaned mic.

Done when: total time is ≥20% lower than step 2, and the echo filter results are unchanged.

## Step 4: speech level normalization (medium) — quality

Problem: quiet speakers (far from the mic, or a quiet remote participant) are often
missed or misheard. Some speakers in the same track are loud and some are quiet.

Plan:
- [ ] **Measure.** For each track, compute the RMS level in dBFS over short windows (e.g. 400 ms, hop 100 ms). Estimate the noise floor (e.g. the 10th percentile) and the speech level (e.g. the median of the windows clearly above the floor). Log both per track.
- [ ] **Decide.** Do nothing if the speech level is already in the target range (e.g. −26 to −16 dBFS). Otherwise, normalize.
- [ ] **Normalize dynamically** (slow AGC, automatic gain control). Gain follows the speech level with a slow attack and release (about 1–3 s), so each speaker in turn is lifted or lowered toward about −20 dBFS.
  - Limit the gain (e.g. +20 dB up, −12 dB down).
  - Never raise windows that are only at the noise floor. Hold the gain there, so silence and hiss do not get loud.
  - Put a peak limiter at the end, so nothing clips.
- [ ] **Order.** Run it after echo removal (LocalVQE needs the true mic/system level relation) and before ASR. Mic and system each get their own AGC.
- [ ] **Setting.** `transcription.level_normalization`: `auto` (default) / `off`. Write what was done to `processing.json` (levels before and after, max gain).
- [ ] **Check with WER, not by ear.** NeMo/Parakeet probably normalizes the features of each chunk itself, so a plain overall gain may change little. The expected gain is in chunks where loud and quiet speakers are mixed. The research in the review found that "speech enhancement" can make ASR worse. So keep the change only if WER on the test set goes down. Most of all, check the quiet-speaker recording.

Done when: WER is lower (or the same) on all test recordings and lower on the quiet one, there is no clipping, and the extra time is under 5 s per 15 min.

## Step 5: fewer model reloads (medium)

- [ ] Today each 60 s chunk starts a new process (~14 per track). Test `--batch` with `--timestamps word` (the Whisper path uses `--batch`, `:184`). If it works, use one process per track.

Done when: ASR time is lower and word timestamps are the same.

## Step 6: skip silence (medium)

- [ ] Reuse the step 4 level measurement. Drop chunks that are only at the noise floor before ASR. Keep the timestamps right.

Done when: ASR time is lower on calls with long silence, with no lost words in the WER set.

## Step 7: GPU (large, optional)

- [ ] Build transcribe.cpp with Vulkan (`Build-LocalRuntime.ps1:40`). Use `--backend vulkan` with CPU fallback.
- [ ] Check runtime size, driver failures and WER.

Done when: it is clearly faster on this PC and falls back safely on a PC without a GPU.

## Later (other review items, not speed)

- Speaker diarization on the system track (sherpa-onnx).
- Easier speaker naming: medium confidence, loose quote matching, one retry.
- Echo removal: fall back to the raw mic when the result looks worse; do not fail the whole pass on one bad sample; measure the delay again over time.
