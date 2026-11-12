# Cutting the cleaned call into speech regions, and a leaner solver, 2026-11-12

Follows `echo-solver-2026-11-11.md` and answers `brief-overnight-2026-11-12.md`: bring
"my WER" (the share of my own words that the transcript gets wrong) of `solve-gate` (the
echo remover that solves the call offline, then turns the audio down wherever a speech
detector hears no voice of mine) from 18.4 % on the suite's 40 echo calls toward the lab
oracle's 9.7 % (the engine given my voice with echo and noise removed perfectly). One new
`echo_model` value came out of it: `solve-gate-cut`. The default is unchanged and is still
`solve-floor`. Two memory changes came out of it too, and they change no result. The lab
notebook with every step, including the dropped tracks, is `lab/NOTES.md`, sections 9 to 23
(they are not in numeric order there). The held-out set is the 52 frozen calls of the last
round (`lab/heldout.py`); the suite is the 40 echo calls of `sonora-tuning`.

## Result

`solve-gate-cut` is `solve-gate` plus one change in what the speech-to-text engine
(`transcribe-cli`, running the Parakeet model) receives. `solve-gate` hands the engine the
cleaned mic in fixed pieces of 60 s. `solve-gate-cut` finds the stretches where the speech
detector (the "gate", `SpeechGate.cs`) is open, joins stretches less than 1.5 s apart, adds
1.0 s on each side, scales each piece to -20 dBFS, and hands the engine one piece per
region. The code is `SpeechRegions.cs`; the setting values were fixed in the lab before any
test on the held-out set (the lab calls this "setting c").

Held-out set, 52 calls that no setting was chosen on, through the app's own echo models:

| Echo remover | My WER % | No noise | Echo words | Against `solve-gate`, 95 % interval |
|---|---|---|---|---|
| solve-floor | 13.0 | 7.4 | 1 | |
| solve-gate | 11.8 | 6.3 | 1 | |
| solve-gate-cut (the app) | 10.2 | 4.9 | 9 | −1.67 [−2.88, −0.33] |
| lab setting c (not the app) | 10.0 | 5.0 | | −1.8 [−3.0, −0.6] |

The first build of the port gave 10.6 % (−1.3 [−2.4, −0.0]). It gave the speech detector the
output behind the 0.15 residual suppressor, and the detector opened on far-end leftovers.
The shipped form gates the solver's plain output, as the lab does (`lab/NOTES.md`, 13.1).

By kind of call, lab setting c against `solve-gate` (my WER %; the app's own split by kind
was measured only for the first build):

| Calls | solve-gate | setting c | Difference |
|---|---|---|---|
| SNR 0, −5 (10) | 27.2 | 21.7 | −5.5 [−8.5, −2.8] |
| SNR 20..5 (31) | 10.3 | 9.1 | −1.2 [−2.7, +0.6] |
| no noise (11) | 6.3 | 5.0 | −1.3 [−2.3, +0.3] |
| steady (16) | 10.0 | 7.9 | −2.1 |
| clock drift (22) | 13.4 | 12.6 | −0.8 |
| change of device (6) | 21.4 | 17.9 | −3.4 |
| no echo (4) | 6.8 | 6.1 | −0.6 |
| 12 minutes (4) | 9.7 | 7.2 | −2.5 |

The gain sits in the loud calls. The quiet and mild calls are flat within noise.

The suite's 40 echo calls (my WER from the lab runs of the app's build; the signal scores
from `quality.py`, run `overnight-cut`):

| Echo remover | My WER % | No noise | SNR 0 | SNR −5 | Echo words | ERLE real (dB) | Double-talk voice (dB) | Echo time (s) |
|---|---|---|---|---|---|---|---|---|
| solve-floor | 21.1 | 10.2 | 24.7 | 55.6 | 4 | 21.7 | −0.4 | 90 |
| solve-gate | 18.4 | 10.2 | 20.7 | 43.3 | 3 | 33.7 | −0.4 | 427 |
| solve-gate-cut | 15.9 | 9.0 | 17.6 | 38.8 | 3 | 33.7 | −0.05 | 448 |
| ideal removal of echo and noise (lab oracle) | 9.7 | 9.3 | 10.1 | 9.6 | | | | |

ERLE is the echo reduction in dB; "real" is the real call `aec-real`. The WER, no-noise,
SNR 0 and SNR −5 cells of `solve-gate-cut` come from the lab run of the same build (the
notebook's 13.2, run C); in that run `solve-gate` read 18.1 and 19.6 and 42.8, and the
`quality.py` run read 18.4 for it. The two sources differ by a few tenths and a few
points at SNR 0 and −5, as they did last round.

Against the bar of the brief (section 5):

| Condition | Limit | solve-gate-cut |
|---|---|---|
| Held-out: my WER below `solve-gate`, interval excludes zero | | −1.67 [−2.88, −0.33] |
| Suite: my WER without noise | ≤ 10.7 % | 9.0 |
| Suite: echo words | ≤ 3 | 3 |
| Suite: double-talk voice level | ≥ −1.5 dB | −0.05 |
| Suite: ERLE on `aec-real` | ≥ 19 dB | 33.7 |
| Suite: echo time, 40 calls | ≤ 511 s | 448 |

Every limit is met. Two weak points stay, and the next section and "What this does not
show" give them in full: the held-out echo words rose from 1 to 9, and the stress set is
worse for the cut on wandering drift.

### Memory: the long call

Two hours of audio are 115 M samples, 460 MB per array of 32-bit floats. The solver held
many such arrays and let the collector pile up the rest. The solver now reads its tracks in
pieces (`ITrack`), makes the warped call track a window at a time, and turns the echo array
into the cleaned mic in place. The speech gate now keeps two long arrays instead of four.

| | Before | After |
|---|---|---|
| Solver, 2 h, one echo path, peak working set | 5.9 to 6.9 GB | 0.87 GB |
| Solver, 2 h, eight echo paths | 5.6 to 7.2 GB | 1.06 GB |
| Whole echo stage, 51 minutes, `solve-gate-cut` | 1.6 and 4.7 GB | 0.83 and 1.3 GB |
| Solver time | | the same within 0.93 to 1.06 |

The cleaned mic is bit-identical for `solve` and `solve-gate-cut` and within −150 dB for
`solve-floor` and `solve-gate` (8 calls times 4 models, 32 pairs). The gate's open and
closed decisions are identical on four suite calls and a 51-minute call (0 of 292 175
frames of 10 ms differ). 457 tests pass (332 + 125).

### The stress set

12 new synthetic calls on which the held-out set is thin (`stress.py`, report only, nothing
tuned): wandering clock drift (4), four devices in a call (4), and a call in which I say one
sentence in 180 s, about 1.5 % of the time (4).

| Group | Words | solve-gate, WER % / echo words | solve-gate-cut (app), WER % / echo words | Solver ERLE (dB) |
|---|---|---|---|---|
| wandering drift (4) | 585 | 27.7 / 18 | 29.6 / 30 | 6.6 |
| four devices (4) | 564 | 19.3 / 1 | 11.9 / 0 | 21.2 |
| one sentence in 180 s (4) | 32 | 96.9 / 0 | 225.0 / 4 | 26.2 |

On wandering drift the solver removes only 6.6 dB of echo, against 33.7 dB on the real call.
That is a weakness of the solver, which fits one drift per section, and it was not fixed.
The lab's setting c scored 47.2 % with 105 echo words there; the app's version scored 29.6 %
with 30, so the lab number overstated the loss. With four devices the cut is better, 11.9 %
against 19.3 %. In the 1 % calls both models insert many words and the cut inserts more.

### Where the rest of the gap is

The suite is at 15.9 % and the oracle at 9.7 %. What is left is cafe babble. At SNR −5 the
lab's cutting still leaves about 40 % of my words wrong. The words that remain wrong are
mostly inserted words: the engine writes down a background talker. Three findings point to
the same cause:

- On the held-out set the calls where the cut is worse than `solve-gate` by more than 5
  points (ho-27, ho-32, ho-13, ho-30) are all insertions, 8 to 27 words against 0 to 2.
  The notebook's guess, not tested, is that the 1.0 s of padding lets the engine hear a
  background talker at the edge of a region.
- In two held-out calls the app's text ends with English words from the noise, not from me.
- Of the 35 speech regions on the suite that hold under 0.5 s of my speech, 24 are
  background talk in the noisy calls. The solver does not touch it, because it is not echo.
  Its energy stays about the same, so how much energy the solver removed cannot tell it
  from my speech. Level cannot either. No threshold drops half of the 35 regions and loses
  under 0.3 % of my speech.

A noise remover that works on babble, or a speech detector that knows my voice, would be
the next step. Neither was found tonight.

## What was tried and dropped

Each row has the number that decided it. "Loud 14" is the 14 suite calls at SNR 0 and −5.
The baseline on those 14 calls is 29.6 % for `solve-gate` and 27.2 % for the cut alone.

| Track | What it was | Number | Why dropped |
|---|---|---|---|
| R | Score the two real calls of the Sonora README | none | The audio and the reference text are not on this PC. Searched 98 recordings, 29 call folders, four kept echo folders. |
| N2 | Mix the denoiser's output in by local SNR, then cut | 25.9 against 27.2 on loud 14 (−1.3 [−3.4, +0.5]) | Interval includes zero; echo words rose to 6. |
| N3 | A larger denoiser (`dpdfnet2`, `dpdfnet4`) | at SNR −5 they lose more speech than the small one | Signal check failed; no engine run. |
| N4 | Wiener gain with floors per band | 3.2 to 3.9 dB of speech lost for 1.1 to 2.0 dB of SNR at cafe −5 | Same trade as the neural denoisers; no engine run. |
| N5 | A tiny mask trained for the engine (548 k parameters) | 28.7 against 27.2 on loud 14 | It raised SNR by 3 dB and still lost words. |
| E | Pick or vote between transcripts | 0.4 to 0.8 gained for three times the engine time | Too small for the cost. |
| S1 | Gate model in int8 | 12 % of decisions differ on loud 14; time 1.70 and 0.92 times | Neither faster nor faithful. |
| S3 | A distilled gate (168 k parameters) | 28.5 against 27.2; 0.028 of the time | Cafe babble is not learnt; 13.5 % of frames differ on loud 14. |
| S2 / P | Run the gate pieces in parallel | 0.41 of one worker with 8 workers | The app already does it; nothing to port. |
| B | One engine process for all regions | none | Batch mode gives no word times, so each region still needs its own process. |
| C | Cut without the gate, by an energy detector | 12.4 against 13.0 held-out (−0.7 [−2.1, +0.9]) | Interval includes zero; the cut needs the gate. |
| N6 | Drop regions that are mostly removed echo | no threshold works (see above) | No signal; no engine run. |
| cut3 | Engine gets the audio from behind the suppressor | suite 15.9 and 6 echo words, against 15.9 and 4 | No better; removed from the source. |

### What this does not show

- No real call was scored. The two real calls of the README are not on this PC, and
  `aec-real` has no reference text (it has ERLE only). Every WER here is on synthetic calls.
- The held-out echo words rose from 1 to 9. All of them are inserted words, and on the
  suite the count is equal (3 against 3), but on 52 calls it is the weak point. The notebook
  has not looked at which words they are.
- The memory changes were not measured with the engine. The claim "no result moves" is
  argued from identical audio. The gate's saving was measured on a 51-minute call, not on
  two hours; at two hours it is counted by arrays (2 × 460 MB instead of 4 × 460 MB). The
  whole echo stage at two hours was not run because the disk is too small.
- The times were read on a busy machine; the user's own job ran. Echo times are good for
  ratios only. The held-out "engine time" of 1115 s against 1320 s for `solve-gate` is from
  the notebook's run A and may not be the same measure as the suite's echo time.
- The cut starts one engine process per speech region, about 2 s each more than a batch
  would (a model load), with about 10 regions per 150 s call. Batch mode gives no word
  times, so it cannot replace them (`lab/NOTES.md`, 13.1 and 10.2).
- The stress set is 12 synthetic calls. The 1 % group holds 32 reference words: its
  percentages (225.0 %, 96.9 %) rest on a few words and decide nothing. A few points on 585
  words is inside the noise of one run.
- Lab setting c (10.0 %) and the app (10.2 %) differ slightly: the app's solver and gate
  are written in C# and do not match the lab's numbers exactly (for example, the app opens a
  short run in ho-00 that the lab does not).
- The distilled gate and the tiny mask were trained on FLEURS speech and DEMAND noise, both
  licensed CC BY 4.0. They are lab-only. Neither is in the app, and neither could ship
  without a licence decision. The 1 GB download budget is fully used (about 1 GB went into
  the mask's data and PyTorch).
- The disk fell to its 5 GB floor twice (the suite signal scores waited at 5.07 GB free,
  and the first stress run was cut by low disk). The audio caches of some dropped tracks
  were deleted afterwards (the notebook names N3, S1/S2, S3, N6 and the stress set; the
  orchestrating session itself deleted the caches of N2 and C and the tiny mask's training
  data and PyTorch copy at 03:56, which the notebook does not record). The score rows (small JSON files) are kept, so the numbers above can be read
  again. A new score with other settings needs the solver and the gate run again.
- The sessions of tonight's orchestrator sat idle from 02:18 to 03:14 (the notebook does
  not record it; no process ran in that time), so the order of the tracks is not the
  order of the brief.
- The suite WER of `solve-gate` reads 18.4 in the `quality.py` run and 18.1 in the lab run;
  the cut is compared with the cached 18.1 in the paired interval (−2.05 [−2.49, −1.68] for
  the first build).

## Options now

1. **Leave `solve-floor` as the default and keep `solve-gate-cut` as an option.** Nothing
   changes for users. Anyone who sets `echo_model` to `solve-gate-cut` gets 10.2 % instead of
   11.8 % on the held-out set. This is the state of the branch.
2. **Make `solve-gate-cut` the default.** The gain over `solve-floor` is 2.8 points on the
   held-out set and 5.2 on the suite (21.1 to 15.9), and the call takes about 5 times as
   long to clean (448 s against 90 s on the suite's 40 calls), plus one engine process per
   region. The default was not changed on purpose. Before it is, two things should be true:
   a real call, scored by hand-checked reference text, shows the same direction; and the 9
   held-out echo words are understood, or a rule keeps them out (the stress set suggests
   wandering drift is where they come from, with 30 against 18).
3. **Spend the next night on babble.** The remaining gap is the background talker that the
   solver does not touch. A speech detector that knows my voice, or a recording of a real
   cafe for training, are the two ideas the notebook leaves open.

## On the branch

The branch is `claude/overnight-noise-2026-11-12`. It is not pushed and not merged into
`master`. It holds 12 commits: the lab scripts and notebook, `SpeechRegions.cs` and the
`solve-gate-cut` value, the solver's and the gate's memory changes, and the tests. Test
builds are in `windows\artifacts` (not in git): `overnight-cut2` (the shipped form, used for
the held-out set and the suite), `overnight-par` (the gate change; also used for the stress
set), `overnight-cut3` (the suppressed-audio variant, kept for reference), and
`overnight-mem` (the solver memory change). `windows\artifacts\publish` was not touched. The
earlier `overnight-cut` build was deleted.

## How to repeat it

```powershell
# the app's cut on the held-out set (build the branch into its own folder first)
$env:AMANU_LAB_EXE = "<publish folder>\Amanu.exe"
py -P lab\ho.py "<work dir>" amanu:solve-gate-cut
py -P lab\hg.py "<work dir>" solve-gate-cut,solve-gate solve-gate-cut solve-gate

# the suite's signal scores, only the echo calls
py quality.py run --name overnight-cut --only "^(echo-|aec-real)" --modes solve-gate-cut --yes --exe "<publish folder>\Amanu.exe"

# the stress set
py -P lab\stress.py engine
```

`solve-gate-cut` needs `dpdfnet_baseline.onnx` in `windows/native/dpdfnet/` at build time,
as `solve-gate` does. Without it the model runs as `solve-floor` and says so in the log.
