# Quality suite

Measures transcription accuracy and echo removal without anybody checking text by
hand. Every score comes from public Slovak test sets with exact transcripts, or from
calls `quality.py` mixes itself, so it knows every word said and every sample of echo
and noise it added.

```
py quality.py prepare     # once: downloads about 1.2 GB, builds 98 test recordings
py quality.py all         # run + report + check; about 3 hours on a Ryzen 7 5800H
```

Close Amanu first. The suite runs `artifacts\publish\Amanu.exe --measure-transcription`
(or `--exe <path>`) on each recording, so it tests the whole pipeline the app uses:
decode, echo removal, level normalization, the local engine and the transcript echo
filter. It needs Python 3 with `numpy`, `scipy`, `soundfile`, `pyarrow` and `rapidfuzz`.

Data and runs live in `%USERPROFILE%\Amanu Quality Set` (`AMANU_QUALITY_SET` moves it),
never in the repository. A run resumes where it stopped: `py quality.py run --name <run>`.
`--only <regex>` runs some recordings only.

## What it measures

| Recordings | Source | Score |
|---|---|---|
| `asr-fleurs-*` | FLEURS Slovak test, 792 read sentences (CC BY 4.0) | WER and CER on clean speech |
| `asr-voxpopuli-*` | VoxPopuli Slovak test, 604 parliament segments (CC0) | WER and CER on clean, partly spontaneous speech |
| `echo-*` | Mixed calls, see below | WER of my words, echo words leaked into my text, echo removed, my voice level in double talk |
| `aec-real` | 40 far-end clips from the Microsoft AEC Challenge ICASSP 2022 test set, recorded on real devices | Echo removed on real hardware |

WER is words wrong per 100 reference words; CER the same for characters. Both texts are
lower-cased and stripped of punctuation first.

### The mixed calls

"Me" speaks ten FLEURS sentences into the mic and "them" eight other sentences on the call
track, in five blocks: them alone, me alone, both at once, them alone, me alone. Their
voice reaches the mic again through a soft-clipping speaker, a computed room (80 ms
latency and 0.30 s reverb in room A, 180 ms and 0.55 s in room B) and is mixed in at a
fixed level against my voice (SER +5, −5 and −15 dB; lower is louder echo). Real noise
from DEMAND is added at a fixed level against my voice (SNR none, 20, 10, 5, 0 and
−5 dB; lower is louder noise): office and cafeteria over the whole grid, street and
living room at four points. Each call runs with echo removal off and with `sonora`,
`fast` and `thorough`; `off` is the floor every remover must beat.

Scores for a mixed call:

- **My WER**, and **echo words**: words from their sentences that turn up in my transcript.
- **Echo removed** (ERLE): how much quieter the parts where only they talk became. Noise
  stays in, so this is capped by the noise floor.
- **My voice in double talk**: how loud my own voice still is where both talk, in dB
  (0 is best), and how much cleaner it got (SI-SDR gain).
- **Where it breaks**: the noise level at which my WER first passes 1.5 × its clean value
  plus 5 points.

## Regression check

`py quality.py check` compares the last run with `baseline.json` and exits 1 when clean
WER rose by more than 1 point, mixed-call WER by more than 2 points, echo words by more
than 10, or echo removed fell by more than 3 dB. `--update-baseline` makes the last run
the new baseline; do that only on purpose, after reading its report.

## Licenses of the data

The data is downloaded, never committed. FLEURS: CC BY 4.0. VoxPopuli: CC0 (European
Parliament legal notice). DEMAND: Creative Commons (the Zenodo page states both CC BY 4.0
and CC BY-SA 3.0). AEC Challenge clips: the terms of the Microsoft AEC Challenge.
