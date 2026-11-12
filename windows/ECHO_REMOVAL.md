# Echo removal in Amanu for Windows

This page explains what echo removal does, which models the setting `echo_model` offers,
how each one works, what each one costs, and how to choose. The default is
`solve-gate-cut`.

## The problem

You record both sides of a call. The microphone picks up your voice. It also picks up the
other person's voice from your speakers. That second sound is the echo.
If the echo stays in the microphone track, the transcript shows the other person's words
as if you said them. Noise in your room makes it worse.

## What the app does

Echo removal runs after the call, before transcription. It is a setting:
**Remove echo before transcribing** (`offline_echo_cancellation`, on by default).

- It works on a **copy** of the microphone track. Your recording is never changed.
- It uses the call track (the far end) as the reference for what is echo.
- The speech engine then reads the cleaned copy.
- All work is local. No audio leaves your computer. Nothing is downloaded at run time.
- A recording with no call track gets no echo removal. The clean test sets are such
  recordings.
- You set it in **Settings → Setup → Transcription**, in the echo card. The card has two
  switches. The first is this setting, with the `echo_model` menu beside it. The menu
  shows the default and the six other main models first. The test variants come after a
  line. The second switch is **Drop echoed speech from the transcript**
  (`transcript_echo_filter`). It works on the words after transcription, not on the
  sound.
- Setting **Keep the cleaned audio** (`keep_echo_cleaned_audio`) keeps the cleaned copy
  beside the recording, so a second transcription can reuse it. The copy is only reused
  if it came from the same model and the same processor revision. A model change makes a
  new cleaned copy.

## How a transcript shows which echo removal made it

Each new transcript records the echo removal that the engine heard. You can compare two
models on the same recording: set `echo_model`, transcribe again, and keep both versions.

- `transcript.md` starts with a table below the title:

  ```markdown
  | | |
  |---|---|
  | Transcribed | 2026-11-12 14:32 (UTC+01:00) |
  | Engine | local · parakeet |
  | Echo removal (audio) | solve-gate-cut |
  | Echo filter (text) | on |
  | Speakers | me → Alice, them → Bob |
  ```

  - **Transcribed** is when the transcript was made, in local time with its UTC offset.
    It is not when the call was recorded.
  - **Echo removal (audio)** is `offline_echo_cancellation` with `echo_model`. It cleans
    the mic audio before the engine runs.
  - **Echo filter (text)** is `transcript_echo_filter` (**Drop echoed speech**). After the
    engine runs, it drops a line of yours that repeats the far end's words at the same
    time. The two are separate settings, and both change which words are left, so compare
    only transcripts that have the same filter value.
  - **Speakers** shows only when speakers have names.
  - The recordings window shows the table without its empty header row.
- `transcript.json` has an `echo` object: `model` (the `echo_model` name, or `off`),
  `ran` (true only if the mic that the engine heard was changed), `note`, `processor`
  (the revision of the audio path; it changes when the code of a model changes), and
  `transcript_filter` (the text filter `transcript_echo_filter` also ran). The time is
  `created_at`, in UTC.
- The **Echo removal (audio)** row tells what really happened, not only the setting:
  - `off` — the setting **Remove echo before transcribing** was off.
  - `solve-gate-cut, not run (only one track was recorded)` — no call track.
  - `…, not run (skipped: …)` — the step failed, so the engine heard the mic as recorded.
  - `…, not run (the call track was silent)` — nothing to remove.
  - `solve-gate (many devices on the call, ran as plain sonora)` — the solver found
    no single echo path and fell back.
- In the recordings window, the transcript tabs show the echo name when two versions
  differ by it, for example `Parakeet · solve-gate` and `Parakeet · sonora`.
- The quality runs (`MeasureAsync`) write the same table into their output transcript.
- Transcripts made before 2026-11-12 have no echo rows. When such a transcript is
  written again (for example, when you name a speaker), it gets the table without them.

## The words used in this page

- **My WER**: the share of your own words that the transcript gets wrong. Lower is better.
- **Echo words**: words of the other person that appear in your part of the transcript.
- **ERLE**: how many dB of echo the remover takes out. Higher is better.
- **Double talk**: both people speak at the same time. A good remover keeps your voice
  at the same level then. The tables give the change of your voice level in dB. Zero is
  best.
- **SNR**: speech level against noise level. At SNR 0 dB, speech and noise are equally
  loud. At SNR −5 dB, the noise is louder. A cafe is the hardest noise.

## The models

The setting `echo_model` takes one of these names. An unknown name gets the default.

### Group 1: the solver family (the ones to use)

These models solve the echo path over the **whole** recording. They do not follow the
echo sample by sample. First they find the echo delay, the clock drift between the two
devices, and any change of device (up to three delay lines). Then they fit one filter for
each five minutes of the call. Frames where you speak count less in the fit, so double
talk does not disturb it.

| Model | What it adds | Use when |
|---|---|---|
| `solve` | The filter only. | You want the least change to the sound. |
| `solve-floor` | `solve`, then it turns down what the filter left of the echo, by at most 16 dB. | Quiet and medium noise. The previous default. |
| `solve-gate` | `solve-floor`, then it turns the microphone down by 16 dB where you do not talk. | Loud noise. |
| `solve-gate-cut` | The plain solver output, gated, and then cut. The engine gets one file for each stretch of speech, not 60 s pieces. | Everything. **The default.** |

How the gate works: a small denoiser (DPDFNet, Apache-2.0, 8.8 MB, in
`echo-runtime\dpdfnet_baseline.onnx`) is used only as a speech detector. The gate is open
where its output is within 25 dB of its loudest level, for at least 0.3 s. Elsewhere the
microphone gets a gain of 0.15. The audio that passes is the canceller's audio. It is not
the denoiser's audio.

Why `solve-gate-cut` cuts: the speech engine cuts a call into 60 s pieces and scales
each piece. Loud noise with no speech in a piece costs the speech beside it. Pieces made
from the gate's speech regions (gaps under 1.5 s are joined, 1 s of padding, at most about
25 s each, each scaled to −20 dBFS) avoid that.
`solve-gate-cut` has no residual suppressor. In tests the gate opened on the leftovers
of the far end when the suppressor was in front of it.

Fallbacks (the model still gives a result):

- A recording that joins many devices, or a delay that is not clear, runs as plain
  `sonora` (see Group 2).
- If `dpdfnet_baseline.onnx` is missing, `solve-gate` runs as `solve-floor`. The log
  line `echo stages` says `speech gate left out`.
- If there is no gate file or no region, `solve-gate-cut` uses the old 60 s path.

### Group 2: Sonora (fast, classic)

`sonora` is a Rust port of WebRTC's AEC3 echo canceller (BSD-3-Clause). It runs in a
streaming way. It is by far the fastest model, and it is the fallback of the solver family.

The other `sonora-*` names are tuning experiments from 2026-11-10 and 2026-11-11. They
stay so that old test results can be repeated. They are not recommended.

| Name | Change from `sonora` |
|---|---|
| `sonora-linear` | Linear filter output only, without the suppressor. |
| `sonora-nearend` | The suppressor treats a block as your speech sooner. |
| `sonora-gentle` | Near-end tuning, and the normal tuning suppresses less. |
| `sonora-long`, `sonora-long80`, `sonora-long80-nearend` | Longer filters: 160 ms, 320 ms, 320 ms with near-end tuning. |
| `sonora-delay-linear` | Echo delay set by a delay prime, 256 ms filter, linear output only. |
| `sonora-delay-nearend` | The same, with near-end tuning. |
| `sonora-delay-floor`, `-floor70` | The same, with a gain floor of 0.5 (6 dB) or 0.7 (3 dB). |
| `sonora-delay-nearend-floor`, `-nearend-floor70`, `-nearend-nf70` | Near-end tuning with a floor in every band, or only while your voice dominates. |

The `sonora-delay-*` models lose most of their echo removal when the clocks of the two
devices drift (a drift of 200 ppm takes almost all of it away). This is why the solver
family replaced them.

### Group 3: LocalVQE (neural, slow)

| Model | What it is | Speed |
|---|---|---|
| `fast` | LocalVQE's adaptive filter, 2.7K parameters. | About 13 times slower than `sonora`. |
| `thorough` | A larger neural model (200K parameters), good in rooms with strong reverb. | Slowest. About 14 times slower than `sonora-delay-floor` on the held-out set (2004 s against 147 s). |

These models need `liblocalvqe.dll` and their model file in `echo-runtime\`.

## How well do they work?

All numbers come from simulated calls (your voice, far-end speech, a room, noise).
No real call with a real second device was scored. See "What this does not show".

**Held-out set** (52 calls, no setting was chosen on them). My WER, in percent:

| Model | My WER | No noise | Echo words |
|---|---|---|---|
| `thorough` | 24.7 | 24.3 | 119 |
| `sonora-delay-floor` | 31.1 | 21.3 | 203 |
| `solve-floor` | 13.0 | 7.4 | 1 |
| `solve-gate` | 11.8 | 6.3 | 1 |
| `solve-gate-cut` | **10.2** | 4.9 | 9 |

`solve-gate-cut` against `solve-gate`: −1.67 points, 95 % interval [−2.88, −0.33].

**Quality suite** (40 mixed calls plus one real recording, `aec-real`):

| Model | My WER | SNR 0 | SNR −5 | Echo words | ERLE real (dB) | Double talk (dB) | Echo time, 40 calls (s) |
|---|---|---|---|---|---|---|---|
| `fast` | 28.7 | | | 0 | 7.3 | −0.0 | 511 |
| `thorough` | 25.4 | 29.3 | 52.0 | 2 | 19.4 | −1.8 | 929 |
| `sonora-delay-floor` | 23.6 | 30.6 | 54.4 | 5 | 21.7 | −2.2 | 76 |
| `solve` | 25.5 | 40.1 | 62.0 | 1 | 21.7 | −0.0 | 66 |
| `solve-floor` | 21.1 | 24.7 | 55.6 | 4 | 21.7 | −0.4 | 90 |
| `solve-gate` | 18.4 | 20.7 | 43.3 | 3 | 33.7 | −0.4 | 427 |
| `solve-gate-cut` | **15.9** | 17.6 | 38.8 | 3 | 33.7 | −0.05 | 448 |

A perfect remover of echo and noise would score 9.7. The rest of the gap is mostly
cafe babble: background talkers that the solver does not touch.

## Cost

- **Time.** For the 40 suite calls, `solve` needs 66 s, `solve-floor` 90 s,
  `solve-gate` 427 s and `solve-gate-cut` 448 s. The gate costs most: about 7 s for 156 s of audio. It
  already uses half of the CPU cores. `solve-gate-cut` also starts one speech-engine
  process for each region, about 2 s each.
- **Memory.** A two-hour call needs about 0.9 GB. Before the memory change of
  2026-11-12 it needed about 6 GB.
- **Disk.** The cleaned copy is kept only when you ask for it.

## How to choose

- Use the default `solve-gate-cut` for normal use.
- If you see words of the other person in your text, use `solve-gate`. On the held-out
  set it let 1 echo word through, where `solve-gate-cut` let 9.
- If a call is very quick to process or the computer is slow, use `solve-floor`
  (no gate) or `sonora`.
- Use the other names only to repeat an old test.

## Known weak points

- **Echo words.** Cutting into regions raises the echo words on the held-out set
  (1 to 9). The cause is background talkers and leftover echo, which the gate opens on.
  The engine then writes words in a region that is scaled up.
- **Wandering clock drift.** When the drift changes during the call, the solver removes
  only about 6.6 dB of echo, against 21 to 26 dB in other stress calls. On four such
  stress calls, `solve-gate-cut` left 29.6 % of words wrong and `solve-gate` 27.7 %.
- **Very little speech.** If you talk for about 1 % of a call, the gate and the cut
  write many false words. This is true for `solve-gate` too, but less.
- **Many devices.** A recording that joins many devices runs as plain `sonora`.

## What this does not show

- No real call with a second device was scored for words. `aec-real` is a real
  recording, but it has no near speech and joins about 40 devices.
- The simulated drift in the main test sets is constant and at most 200 ppm. A wandering
  drift was only tried in 4 stress calls.
- The setting values of the gate (25 dB, 0.3 s, gain 0.15, region padding) were
  chosen on the quality suite, not on real calls.
- Times were read on a busy computer, so they can read high.
- The memory change was checked on a 51-minute call and by comparing audio. It was
  not checked with the speech engine and not at two hours.

## Where to read more

- Overnight study, 2026-11-12 (cutting into regions, memory, stress set, ideas that
  failed): [`tests/quality/overnight-2026-11-12.md`](tests/quality/overnight-2026-11-12.md)
- Study of the solver, the floor and the gate, 2026-11-11:
  [`tests/quality/echo-solver-2026-11-11.md`](tests/quality/echo-solver-2026-11-11.md)
- Earlier Sonora studies: [`sonora-tuning-2026-11-10.md`](tests/quality/sonora-tuning-2026-11-10.md),
  [`sonora-delay-2026-11-11.md`](tests/quality/sonora-delay-2026-11-11.md)
- The lab notebook with every step: [`tests/quality/lab/NOTES.md`](tests/quality/lab/NOTES.md)
- Native parts: [`native/sonora`](native/sonora/README.md),
  [`native/localvqe`](native/localvqe/README.md), [`native/dpdfnet`](native/dpdfnet/README.md)
- Code: `src/Amanu.Core/Processing/EchoCancellation.cs` (the model list),
  `OfflineEchoSolver.cs`, `SpeechRegions.cs`, `src/Amanu.App/SpeechGate.cs`
