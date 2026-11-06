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
- [x] Keep 2–3 fixed test recordings, at least one Slovak call with a quiet speaker. For each, keep a hand-checked reference transcript of 2–3 minutes for WER (word error rate) checks.
  - Done: `2026.11.06-1000` and `2026.11.06-1021`, window 120–300 s. References are in `%USERPROFILE%\Amanu Speed Set\*.ref.txt` (outside git: private call text).
  - Quiet speaker and echo case: `2026.11.06-1021`. The mic is very quiet: speech alone about −45 dBFS (about 25 dB under the step 4 target), median −46, max −30. The call side is median −29, peak −5. The mic also picks up the call side (echo) about 16 dB under it. `gemini-web\me.m4a` and `them.m4a` in that folder are the same call, split, and are easy to listen to when correcting the reference.
  - References (decided 2026-11-06): no full hand check.
    - `*.ref.txt`, window 120–300 s: the old `gemini-3.5-flash-lite` transcript (silver, not hand-checked). Today Parakeet differs from it by 42.1 % (`1000`) and 30.8 % (`1021`). Use it only for "better or worse than before".
    - `2026.11.06-1021.me-check.txt`, window 650–710 s, `me` only: the quiet speaker. Parakeet hears 152 words here, Gemini 185. It shows both texts side by side in 10 s blocks; each block gets `=> P`, `=> G`, `=> -` or the right words. The script reads this file directly (`-Reference ...me-check.txt`).
  - Hand check done 2026-11-06: Parakeet was right in all 6 blocks (`=> P`). WER on the quiet speaker: **0.0 %** (152 words). So Gemini was about 25 % off here, and most of the 31–42 % difference above is Gemini's error, not Parakeet's.
  - Score an existing transcript without a new run: `Measure-Transcription.ps1 <recording> -ScoreOnly -Reference <file>`.

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

- [x] Find the thread option of the transcribe.cpp CLI. Pass an explicit count (`TranscriptionServices.cs:156`).
  - Done: `--threads N` (CLI default: all cores). Amanu passes `Environment.ProcessorCount` (16 on this Ryzen 7 5800H, 8 cores). `AMANU_ASR_THREADS` overrides it, for measuring.
- [x] Batch runs use BelowNormal priority, not Idle (`:232`). Live transcription keeps its priority.
  - Done. Live has its own worker (`LiveSpeechWorker.cs`, AboveNormal); it is not changed.

Done when: the ASR time is measured with 2–3 thread counts, and the best count is the default. **Done (2026-11-06).**

Step 1 result (`2026.11.06-1000`, AC power, Balanced plan, Amanu closed, after a warm-up run):

| Threads | Echo removal | ASR me | ASR them | ASR | Total |
|---|---|---|---|---|---|
| Step 0 (Idle, CLI default) | 121.9 s | 117.7 s | 121.0 s | 238.7 s | 362.9 s |
| 16 | 118.8 s | 99.2 s | 99.7 s | **199.0 s** | 319.9 s |
| 12 | 118.0 s | 97.8 s | 101.8 s | 199.6 s | 319.7 s |
| 8 | 118.5 s | 109.9 s | 112.3 s | 222.2 s | 342.8 s |

- 16 and 12 threads are the same; 8 is about 12 % slower. So the default stays "all logical processors".
- The gain (ASR about −17 %) comes from the priority: Idle lost CPU time to other apps.
- `2026.11.06-1021`, 16 threads: echo 135.0 s, ASR 238.8 s (Step 0: 259.4 s, −8 %), total 376.5 s. One more run of `1000` in the same batch was slower in all stages (echo 151.1 s, ASR 215.1 s): the computer was busier then. Numbers move about ±10 % between runs.
- Words: transcripts of both recordings are identical to Step 0 (same text and timestamps). WER: `1000` 42.1 % vs silver (same as before), `1021` quiet speaker 0.0 %.
- A quick CLI test on three 61 s pieces gave the same words for 4, 6, 8, 12 and 16 threads.

## Step 2: both tracks at the same time (small)

- [x] Run mic and system ASR in parallel (`TranscriptionServices.cs`, `TranscribeTracksAsync`), with half the threads each (`ShareThreads`: 16 → 8 + 8).
- [x] Take `CpuTranscriptionGate` once for the pair (`CpuTranscriptionGate.cs:7`).
- [x] Live preemption cancels both tracks. A finished track keeps its result; only the unfinished track runs again. If one track fails, the other is stopped and the real error is reported.

Done when: ASR time is ≥25% lower and the transcripts are identical to step 1. **Partly met (2026-11-06): transcripts identical, but ASR is only 9–18 % lower.**

Step 2 result (AC power, Amanu closed, after a warm-up run):

| Recording | Echo removal | ASR me | ASR them | ASR | Step 1 ASR | Change | Total | Words |
|---|---|---|---|---|---|---|---|---|
| `2026.11.06-1000` | 120.8 s | 161.1 s | 162.7 s | **162.7 s** | 199.0 s | −18 % | 285.9 s | 1646 |
| `2026.11.06-1021` | 139.1 s | 218.0 s | 217.9 s | **218.0 s** | 238.8 s | −9 % | 359.5 s | 2228 |

- Words: both transcripts are identical to step 1 (same segments, text and timestamps). WER: `1000` 42.1 % vs silver (same as before), `1021` quiet speaker 0.0 %.
- Threads per track, tested on `1000`: 8 + 8 = 162.7 s, 12 + 12 = 220.7 s, 16 + 16 = 388.1 s. Too many threads fight for the CPU, so half each is the best.
- Why not −25 %: one track alone already uses most of the CPU (step 1: 8 threads is only 12 % slower than 16). Two tracks at the same time mostly share the same CPU time. The gain is the part one track left unused.
- The warm-up run of `1000` was 193.0 s; the cold run after a publish is slower, as before.
- Full report (code changes, measurements, thread research): [reviews/2026-11-06-step2-parallel-asr.html](reviews/2026-11-06-step2-parallel-asr.html) (Word copy beside it).

## Step 3: system track during echo removal (medium)

- [ ] Echo removal changes only the mic. Start system-track ASR on the original system audio while echo removal runs (`ProcessingCoordinator.cs:420-437`, `OfflineEchoAudio.cs`).
- [ ] Then run mic ASR on the cleaned mic.

Done when: total time is ≥20% lower than step 2, and the echo filter results are unchanged.

## Step 4: speech level normalization (medium) — quality

Problem: quiet speakers (far from the mic, or a quiet remote participant) are often
missed or misheard. Some speakers in the same track are loud and some are quiet.

Step 0 finding: on `1021` the mic speech is at about −45 dBFS, and Parakeet still got 0.0 % WER
on the hand-checked minute. This minute can only catch a step 4 regression, not show a gain.
Before starting step 4, find a recording where Parakeet really misses quiet words. If none is
found, step 4 may not be needed.

Gate result (2026-11-06): **no failing case found, step 4 is on hold.** All 21 kept recordings
were measured (RMS in 400 ms windows, hop 100 ms, floor = 10th percentile of the windows).
- Mic speech is −40 to −60 dBFS, the far side −20 to −34 dBFS. The far side is already near the target.
- On the 6 recordings with a Parakeet transcript, 88–98 % of the mic speech time (mic active, far
  side quiet) is inside a "me" segment, also on the quietest mics (−54 dBFS). Only 3 gaps of 1.5 s
  or more were found (6 s in all), and they are not quieter than the speech that was found.
- Words per second of speech do not drop on quieter segments (correlation about 0 on both sides,
  with the activity gate set 15 dB below the track's speech level).
- AGC does not change the signal-to-noise ratio, and Parakeet normalizes its features per chunk.
Open step 4 again only if a hand-checked minute shows quiet words that Parakeet misses.

Built anyway as an opt-in setting (2026-11-06, branch `step4-levels`):
`transcription.level_normalization` = `auto` / `off`, **default `off`**. Turn it on and
re-transcribe a recording where quiet speakers lose words; `Measure-Transcription.ps1
-LevelNormalization auto|off` compares both on a copy without a change to `config.json`.

| Run (parakeet-sk) | Levels | WER | Words | Level step |
|---|---|---|---|---|
| `1021`, me, hand-checked minute | off (step 0/1) | 0.0 % | — | — |
| `1021`, me, hand-checked minute | auto (me −46.6 → −26.7 dBFS, +20 dB cap; them in range) | 2.0 % (3 / 152) | 2247 | 0.6 s |
| `1000`, silver ref, 120–300 s | off | 42.1 % | 1646 | — |
| `1000`, silver ref, 120–300 s | auto (me −52.0 → −32.0, them −29.7 → −19.3) | 42.1 % | 1678 | 0.8 s |

So on these calls it does not help: the quiet-mic minute got 3 errors worse, the silver score
did not move, and 2 % more words came out. Time is under 1 s per 15 min, no clipping.
Keep it `off` by default until a recording with lost quiet words shows a gain.

Plan:
- [x] **Measure.** For each track, compute the RMS level in dBFS over short windows (e.g. 400 ms, hop 100 ms). Estimate the noise floor (e.g. the 10th percentile) and the speech level (e.g. the median of the windows clearly above the floor). Log both per track.
- [x] **Decide.** Do nothing if the speech level is already in the target range (e.g. −26 to −16 dBFS). Otherwise, normalize.
- [x] **Normalize dynamically** (slow AGC, automatic gain control). Gain follows the speech level with a slow attack and release (about 1–3 s), so each speaker in turn is lifted or lowered toward about −20 dBFS.
  - Limit the gain (e.g. +20 dB up, −12 dB down).
  - Never raise windows that are only at the noise floor. Hold the gain there, so silence and hiss do not get loud.
  - Put a peak limiter at the end, so nothing clips.
- [x] **Order.** Run it after echo removal (LocalVQE needs the true mic/system level relation) and before ASR. Mic and system each get their own AGC.
- [x] **Setting.** `transcription.level_normalization`: `auto` / `off` (default `off`, see above). Write what was done to `processing.json` (levels before and after, max gain).
- [x] **Check with WER, not by ear.** NeMo/Parakeet probably normalizes the features of each chunk itself, so a plain overall gain may change little. The expected gain is in chunks where loud and quiet speakers are mixed. The research in the review found that "speech enhancement" can make ASR worse. So keep the change only if WER on the test set goes down. Most of all, check the quiet-speaker recording.

Done when: WER is lower (or the same) on all test recordings and lower on the quiet one, there is no clipping, and the extra time is under 5 s per 15 min.

## Step 5: fewer model reloads (medium)

- [ ] Today each 60 s chunk starts a new process (~14 per track). Test `--batch` with `--timestamps word` (the Whisper path uses `--batch`, `:184`). If it works, use one process per track.

Done when: ASR time is lower and word timestamps are the same.

## Step 6: skip silence (medium)

- [ ] Reuse the step 4 level measurement. Drop chunks that are only at the noise floor before ASR. Keep the timestamps right.

Done when: ASR time is lower on calls with long silence, with no lost words in the WER set.

## Step 7: GPU (large, optional)

- [x] Build transcribe.cpp with Vulkan (`Build-LocalRuntime.ps1:40`). Use `--backend vulkan` with CPU fallback.
  - Done another way: a Vulkan build needs the Vulkan SDK. The official v0.1.3 release has a ready Windows Vulkan module (`ggml-vulkan.dll`), from the same pinned source. `Build-LocalRuntime.ps1` downloads it, checks its SHA-256 and adds it to `local-runtime` (`-NoVulkan` leaves it out). Our own patched CLI loads it as a separate ggml backend module.
  - Amanu uses `--backend vulkan` when `ggml-vulkan.dll` is in the runtime, else `cpu`. `AMANU_ASR_BACKEND=cpu|vulkan` overrides it. When a Vulkan run fails, Amanu runs that piece again on the CPU and uses the CPU until it restarts. The timing line shows `backend vulkan`, `cpu` or `cpu (vulkan failed)`.
- [x] Check runtime size, driver failures and WER.
  - Size: `local-runtime` goes from 13 MB to 84 MB (`ggml-vulkan.dll` is 74 MB).
  - No GPU: the CLI stops in less than 1 s with "vulkan backend requested but not available". Tested with the module removed: the run finished on the CPU. The module needs `vulkan-1.dll` (GPU driver) and the VC++ runtime. If one is missing, the module does not load and the same fallback happens.
  - Words: a 60 s piece gave the same 121 words on both. Full recording: 98.6 % of the words are the same. The small differences go both ways (fp16 on the GPU).

Done when: it is clearly faster on this PC and falls back safely on a PC without a GPU. **Done on branch `step7-vulkan` (2026-11-06), not merged.**

Step 7 result (`2026.11.06-1000`, AC power, Amanu closed, after a warm-up run, same build, tracks one after the other; integrated AMD Radeon Vega of the Ryzen 7 5800H):

| Backend | Echo removal | ASR me | ASR them | ASR | Total | WER (120–300 s) |
|---|---|---|---|---|---|---|
| CPU, 16 threads | 123.8 s | 96.9 s | 105.9 s | 202.8 s | 328.8 s | 42.1 % |
| Vulkan (iGPU) | 116.7 s | 59.0 s | 65.5 s | **124.6 s** | 243.4 s | 42.5 % |

ASR is 39 % faster. The CPU is also more free during ASR. Not measured yet: Vulkan together with Step 2 (both tracks at the same time on one iGPU).

## Later (other review items, not speed)

- Speaker diarization on the system track (sherpa-onnx).
- Easier speaker naming: medium confidence, loose quote matching, one retry.
- Echo removal: fall back to the raw mic when the result looks worse; do not fail the whole pass on one bad sample; measure the delay again over time.
