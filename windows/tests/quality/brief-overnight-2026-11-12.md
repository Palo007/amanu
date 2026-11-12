# Brief: one night, unattended — close the gap between 18.4 % and 9.7 %

You are the orchestrator of an overnight run on the user's Windows PC. Nobody will answer
questions until morning. The user is on a limited plan: **every token you spend in your own
context is the most expensive token of the night.** Your job is to choose, delegate, judge
and record — not to read logs or write lab code yourself.

Read this brief once. Do not read `brief-beyond-localvqe.md`, `echo-solver-2026-11-11.md`
or `lab/NOTES.md` in full yourself; a subagent can answer a question about them in three
lines.

## 1. Where things stand (2026-11-11)

The app removes echo offline with `echo_model` values from the last round. "My WER" is the
share of the user's own words the transcript gets wrong.

| On the suite's 40 mixed calls | My WER % | No noise | SNR 20..5 | SNR 0 | SNR −5 | Echo time |
|---|---|---|---|---|---|---|
| `solve-floor` (the default) | 21.1 | 10.2 | 12.6 | 24.7 | 55.6 | 90 s |
| `solve-gate` | 18.4 | 10.2 | 12.5 | 20.7 | 43.3 | 427 s |
| ideal removal of echo and noise (oracle) | 9.7 | 9.3 | 9.7 | 10.1 | 9.6 | |

Held-out set (52 frozen calls, `lab/heldout.py`): `solve-gate` 11.8, `solve-floor` 13.0,
`thorough` 24.7.

Facts that decide where to dig (all measured, `lab/NOTES.md` §1, §6):

- Echo is nearly solved. The gap is **noise**, and nearly all of it sits in the 14 suite
  calls at SNR 0 and −5 (cafe −5: 78 % wrong; ideal mask 10.7 %).
- The engine (parakeet through transcribe-cli) cuts the track into pieces by voice
  activity and normalizes each piece. **Loud noise where I am silent costs the words beside
  it.** Turning down only the far-only stretches took cafe 0 dB from 72.9 % to 27.1 %.
- A denoiser's own output is worse for the engine than the gated original (DPDFNet removes
  a quarter of my speech frames at cafe 0 dB). Mixing half of DPDFNet's output back in gave
  17.9 in the lab — inside the noise of that comparison, never pursued.
- The gate (DPDFNet as a speech detector, `SpeechGate.cs`) costs 7 s per 156 s of audio
  and does not get faster with more threads in one process.
- The remaining "echo words" are single short words the engine also inserts with no echo.
- Nothing has been scored on a real recording since the solver was built.

## 2. The roadmap, in the order to run it

Each track has a cheap first step. Do the cheap step of every track before the expensive
step of any. Stop a track after two attempts that fail for a reason you understand; write
the reason down and move on. A track that wins goes on to confirmation (section 5).

### R. Real calls — first, because it is cheap and it can change everything

The two real calls of `windows/native/sonora/README.md` (1000 and 1021, 14 and 16 minutes,
489 and 467 reference words) were scored before. Have a cheap subagent find their audio
and reference transcripts (search `%USERPROFILE%\Amanu Quality Set\recordings`, the app's
recordings folder, and the lab's scripts that scored them). If found: run `thorough`,
`sonora`, `solve-floor`, `solve-gate` through `lab/asr.py`'s Amanu mode, word errors per
call. If not found within 15 minutes of search: record where it looked and move on. Do not
ask anyone.

### N. Noise over my words — the largest gain (about 9 points)

Ideas, the creative ones first. Screen each on the 14 noisy suite calls (section 4).

- **N1. Take the engine's segmentation away from it.** The engine fails because of what it
  does with silence-plus-noise. Feed it what it handles best: the gate's speech regions,
  each loudness-normalized by us, with the stretches between them replaced by low-level
  comfort noise or digital near-silence, and short padding at each edge. First find out
  (Explore subagent) what transcribe-cli's VAD and normalization actually do and whether
  segments can be passed in directly; that one answer decides the design.
- **N2. Adaptive blend.** Mix the denoiser's output into the canceller's by the local SNR
  that DPDFNet itself implies (its output level against its input level per 0.5 s): 0 in
  quiet, up to most of it in babble. The fixed 0.5 blend already hinted at 17.9.
- **N3. Denoise only where it pays.** Run a larger DPDFNet model (`dpdfnet2`, `dpdfnet4`;
  check size and license first) only inside speech regions whose local SNR is under about
  5 dB; leave everything else to `solve-gate`.
- **N4. Floors by band.** Babble lives in 300–3000 Hz like speech; street and living-room
  noise does not. A gain floor per band and per noise type estimated from the silent
  stretches, tested by WER, never by ERLE.
- **N5. Train a tiny mask for the engine** (only if N1–N3 stall by about 03:00). A small
  GRU on log-mel features, trained on CPU for at most two hours on FLEURS *train* speech
  and noise that is in neither the suite nor the held-out set, target: the ideal ratio mask
  with a floor of 0.3. Write down exactly what it trained on.

### E. The engine in the loop

- **E1. Pick per sentence.** Transcribe `solve-gate` and the best N variant; for each
  sentence keep the one the engine is surer of. First find out (cheap subagent) whether
  the engine exposes token scores. If not, use a proxy that needs no truth (agreement
  between versions, words per second against the speech length) and say it is a proxy.
- **E2. Vote.** Three versions, word voting (ROVER-style) on aligned transcripts.
- Count the extra engine time honestly: two transcripts double it.

### S. Speed of the gate — makes `solve-gate` cheap enough to be the default

- **S1.** Quantize DPDFNet to int8 (`onnxruntime.quantization.quantize_dynamic`) and
  check that the gate's open/closed decisions match the float model on the suite.
- **S2.** Since threads do not help inside one process, try pieces in parallel processes,
  and skipping the denoiser where the canceller's output is far below the speech level.
- **S3.** Distil the gate's decision (open or closed per 10 ms) into a tiny classifier on
  log-mel features, trained on DPDFNet's labels from audio that is not in either test set.
- Success: the gate at a quarter of its time with the same suite WER within 0.3 points.

### G. Gaps in the solver (code, lower value tonight)

- Memory: about 2.3 GB for a two-hour call. Make the solver hold the long arrays in blocks
  or float16; prove the output does not change (`lab/s3.py` style, −40 dB or better).
- Build a separate small **stress set** with `heldout.py`'s mixer and new seeds: drift that
  wanders, four devices in a call, a person who talks 1 % of the call. Report only; do not
  tune on it.

## 3. How to orchestrate (this is what keeps the night affordable)

**Your own context.** You read: this brief, the 10-line reports of subagents, and the
headings of `lab/NOTES.md`. You never read a raw log, `results.json`, a transcript, a WER
table of more than ten rows, or a source file over 100 lines. If you need a fact from one,
send a subagent with a question that has a short answer.

**Which agent for what.**

| Work | Agent | Model |
|---|---|---|
| Find a file, a flag, how a tool behaves | `Explore` | `haiku` |
| Babysit a long run, collect numbers, make a table, clean the disk | `general-purpose` | `haiku` |
| Write and run a lab experiment, port a winner to C#, build | `general-purpose` | `sonnet` |
| A design step where two sonnet attempts failed and the reason is unclear | `general-purpose` | `opus`, at most twice tonight |

Never use `fork` (it copies your context). Give each subagent a self-contained prompt of at
most 40 lines: the files, the hypothesis, the exact commands it may run, the stop rule,
and this output contract:

> Put every detail (commands, tables, numbers per call) in a new section of
> `windows/tests/quality/lab/NOTES.md` and caches under
> `%USERPROFILE%\Amanu Quality Set\lab\overnight`. Reply in at most 10 lines: the verdict
> (win / loss / unclear), the 2–4 numbers that decide it with the baseline beside them,
> why, the paths you wrote, and the next step you suggest.

Continue a subagent on the same track with `SendMessage` (its context, not yours). Start
a new one only for a new track. Run independent CPU tracks in parallel, but see section 6
for the transcription limit.

**Waiting.** Long runs go in the background; you are notified when they end. Never poll,
never sleep in a loop. While one track waits on transcriptions, start the CPU-only first
step of another.

**Decisions.** After each report, decide in one line: promote, retry once with a stated
change, or drop. Keep the scoreboard in one file,
`%USERPROFILE%\Amanu Quality Set\lab\overnight\scoreboard.md`, at most 15 lines: each
track, its state, its best number, the next step. Rewrite it after every decision. It is
what lets you (or a resumed session) carry on after a compaction or a usage-limit stop
without reading anything else; the last unattended Fable session was compacted twice and
hit the limit once.

**Status updates.** When the harness asks for one, answer in one line. The last
unattended run was asked 46 times; long answers there are pure cost.

## 4. The measurement ladder — always climb it from the bottom

1. **Signal check** (seconds per call, CPU only): does the output look sane — speech level
   kept, noise lower where intended? Catches broken code before any engine time.
2. **Noisy screen** (minutes): my WER on the 14 suite calls at SNR 0 and −5, lab variants
   through `lab/sw.py`. Baseline: `solve-gate` from the cached numbers, never re-measured.
   A variant goes on only if it beats `solve-gate` here by at least 1.5 points.
3. **Quiet guard**: the no-noise suite calls. The variant must not be more than 0.5 points
   worse than 10.2. Most denoisers fail here; check it before spending more.
4. **Held-out** (`lab/ho.py lab:` then `lab/hg.py`): the deciding number, with a paired
   bootstrap 95 % interval against `solve-gate`. Only for the best two variants, and as
   early as one exists: this is the deciding test, so it comes before any suite run.
5. **The app's build and the suite's echo calls**: only to confirm a winner ported to C#.
   Name only the winner in `--modes`, never its losing siblings
   (`quality.py run --name <run> --only "^(echo-|aec-real)" --modes <winner> --yes`).

Before any run over 30 minutes, the subagent writes in the notebook the jobs, the hours and
why each model is in it, and reports that line to you before it starts. Reuse cached
baselines; run a reference (`thorough`, `solve-floor`, `solve-gate`) again only when its
build or model version changed. Never measure a model already known to lose. A retro of
the last two rounds found about a quarter of their machine time spent on exactly these
three mistakes: losers carried to the suite, a reference re-measured, a run without
`--only`. Tune only on the suite;
never on held-out, never on the stress set; never use `near.wav`, the noise track or the
truth inside a remover; no per-call settings chosen by the score.

## 5. What counts as a win

A new `echo_model` value (or a stage after it) wins when on the held-out set its WER is
below `solve-gate`'s with the 95 % interval excluding zero, and on the suite: no-noise WER
≤ 10.7, echo words ≤ 3, double-talk voice ≥ −1.5 dB, ERLE on `aec-real` ≥ 19 dB, echo time
for the 40 calls ≤ 511 s. A speed win (track S) wins when the WER is within 0.3 points and
the time is at most a quarter.

Port a winner to the app (C#, beside `OfflineEchoSolver.cs` / `SpeechGate.cs`) as a new
`echo_model` value behind the setting. **Do not change the default.**

## 6. Hard rules of this machine (each one cost hours before)

- Build only as `windows/BUILDING-HERE.md` says: .NET SDK in `%USERPROFILE%\.dotnet`; no
  CMake, no MSVC; never delete `windows/artifacts/publish`. Build a test copy into its own
  folder and point the lab at it with `AMANU_LAB_EXE`.
- **At most 3 transcriptions at once on the whole machine** (6 thrash to 180 s each). So
  only one subagent at a time may run engine work; the others do CPU work.
- **Disk: about 12 GB free on C:.** Keep at least 5 GB free at all times. Never pass
  `--keep-audio` to `quality.py run`; the suite now saves each job's scores and drops the
  cleaned copy. Delete only what tonight's work created under
  `%USERPROFILE%\Amanu Quality Set\lab\overnight`. Never delete anything in `runs\`,
  `sets\`, `heldout\`, `recordings\` or a `.tsv` file: a cleanup once deleted 4.6 GB that a
  re-score needed, and it could not be undone. If space runs out, stop the track and say so
  in the morning report.
- `quality.py run --name sonora-tuning` without `--only` starts 754 clean-set files. Always
  pass `--only` and name the echo models.
- Never kill processes by image name; the user's own Python or Amanu may be running. If
  Amanu is open, leave it and note that times may read high.
- Run lab scripts with `py -P` (see `lab/README.md`), never with `-I`, which ignores
  `PYTHONUTF8`. This console is cp1252 and the lab prints "−" and "→"; the project's
  `.claude/settings.local.json` sets `PYTHONUTF8=1`, but only for sessions in the main
  checkout. So work in `C:\Backup\CoDiNG\Amanu\amanu-windows` itself, not in a worktree,
  and have any new script call `sys.stdout.reconfigure(encoding="utf-8")`.
- Do not push, do not merge into `master`, do not push to the build branches, do not
  download more than 1 GB, do not ship anything under a new license. Install Python
  packages only from PyPI, and only small ones.

## 7. Git and records

- Work on a new branch `claude/overnight-noise-2026-11-12` from `master`.
- Commit after every conclusive result (win or loss) — the notebook and the code. Commit
  messages follow `CLAUDE.md`: one prose sentence that says what changed and why, with the
  number; read `git log` once to match the voice.
- Keep the notebook honest: hypothesis, change, numbers, conclusion, also for losses.

## 8. The morning report

Stop starting new work at 07:00, or earlier when the plan's ideas are spent. Then:

1. A write-up `windows/tests/quality/overnight-2026-11-12.md` in the style of
   `echo-solver-2026-11-11.md`, with a "What this does not show" section.
2. Your final message, in **ASD-STE100 Simplified Technical English**: short sentences,
   one fact per sentence. Explain every internal name in plain words, or leave it out.
   For each result, give first **what the user gains, what the user loses, and the
   options now**. Then one recommendation. Then what is on the branch and what is not
   verified. At most 40 lines; the tables are in the write-up.
