# Sonora's delay in double talk, 2026-11-11

Follows `sonora-tuning-2026-11-10.md`, which found that AEC3's linear filter cleaned the
near voice in double talk by only 2 to 4.5 dB and that no suppressor tuning helped the
transcript. This asks why the filter fails, whether the port or the test is to blame,
and what an offline caller can do about it. The suite run is `sonora-tuning` again (full
report: `results-2026-11-11-sonora-delay.md`); the rows of earlier echo removers are the
same results scored again.

## Result

| Echo remover | My WER % | My WER %, no noise | Echo words | ERLE synthetic (dB) | ERLE real (dB) | Double-talk voice (dB) | Double-talk SI-SDR gain (dB) | Echo time (s) |
|---|---|---|---|---|---|---|---|---|
| off | 58.5 | 46.1 | 603 | 0.0 | 0.0 | −0.1 | 0.0 | 0 |
| sonora | 38.9 | 33.5 | 4 | 17.5 | 21.7 | −6.0 | 3.9 | 39 |
| fast | 28.7 | 11.5 | 0 | 11.4 | 7.3 | −0.0 | 8.1 | 511 |
| thorough | 25.4 | 14.3 | 2 | 22.0 | 19.4 | −1.8 | 11.0 | 929 |
| sonora-delay-linear | 27.8 | 11.1 | 1 | 11.6 | 21.7 | −0.9 | 7.5 | 75 |
| sonora-delay-nearend | 31.1 | 23.9 | 2 | 19.0 | 21.7 | −2.8 | 7.2 | 76 |
| sonora-delay-floor | 23.6 | 11.4 | 5 | 15.6 | 21.7 | −2.2 | 8.4 | 76 |
| sonora-delay-nearend-floor | 24.0 | 11.1 | 1 | 14.9 | 21.7 | −1.5 | 8.7 | 76 |
| sonora-delay-floor70 | 25.2 | 11.2 | 2 | 13.8 | 21.7 | −1.3 | 8.6 | 75 |
| sonora-delay-nearend-nf70 | 23.9 | 12.6 | 7 | 14.3 | 21.7 | −1.1 | 8.7 | 76 |
| sonora-delay-nearend-floor70 | 25.5 | 10.8 | 2 | 13.5 | 21.7 | −0.9 | 8.7 | 76 |

The `sonora-delay-*` models:

- take AEC3's echo delay from LocalVQE's delay prime instead of letting AEC3 estimate it,
  primed on the first four 30 s windows and every fourth after, and used only when most
  of them agree; otherwise they run as plain `sonora`;
- use a 256 ms linear filter instead of 52 ms;
- before the stream, run the 60 s stretch with the most far-end speech once, output dropped.

They differ only in what follows the filter:

- `sonora-delay-linear`: the linear filter's output, no suppressor.
- `sonora-delay-nearend`: WebRTC's suppressor in the near-end tuning of `sonora-nearend`.
- `sonora-delay-floor`: WebRTC's suppressor, which may lower no band below 0.5 (6 dB).
- `sonora-delay-nearend-floor`: the near-end tuning with the 0.5 floor.
- `sonora-delay-floor70`, `sonora-delay-nearend-floor70`: the same with a 0.7 floor (3 dB).
- `sonora-delay-nearend-nf70`: the near-end tuning with a 0.7 floor only while AEC3 finds
  the near end dominant (`suppressor.amanu_nearend_gain_floor`).

`sonora-delay-nearend-floor` against LocalVQE: my WER 24.0 % against 28.7 % for `fast` and
25.4 % for `thorough`; 11.1 % without noise against 11.5 and 14.3 %; 1 echo word against 0
and 2; 21.7 dB of echo removed on real devices against 7.3 and 19.4. It is behind `fast`
in voice level (−1.5 against −0.0 dB) and behind `thorough` in synthetic ERLE and SI-SDR
gain. `sonora-delay-floor` has the lowest WER (23.6 %) and 5 echo words, each one common
word in a very noisy cafe call (SNR 0 or 5 dB). The default stays `sonora`.

Why each result:

- With the delay fixed and the filter converged before the stream, the filter alone
  (`sonora-delay-linear`) keeps the voice (−0.9 dB) and matches `fast` (27.8 % against
  28.7 %, 11.1 against 11.5 % without noise, 1 echo word).
- WebRTC's suppressor on top lowers the WER most where the call is noisy: at SNR 0 and
  −5 dB its cuts help the engine more than they cost the voice. Unbounded, it cuts the
  voice in double talk with loud echo by up to 12 dB (`sonora-delay-nearend`, 23.9 %
  without noise). A floor keeps the cut to 6 or 3 dB, and those variants keep the gain in
  noise without the loss in quiet: 23.6 to 25.5 % overall, 10.8 to 11.4 % without noise.
- A floor only while the near end is dominant (`nearend-nf70`) lets AEC3 cut far-only
  echo fully, but it judged the near end not dominant often enough to cost the voice in
  quiet calls (12.6 % without noise) and left more echo words (7).
- All of them remove 21.7 dB on `aec-real`, as plain `sonora`: its forty devices give no
  common delay, so they run as plain `sonora` there (section 4a).

Speed: 75 to 76 s for the 40 calls against 511 s for `fast`, 6.7 times faster. The calls
last about 160 s, so the 60 s warm-up adds more than a third; on a long call it adds
little. From the parts (the 256 ms filter costs 1.7 times the 52 ms one, the prime
4.2 s per 1000 s of primed audio, and about a third of a long call's windows primed), the
two real calls of `windows/native/sonora/README.md` would take about 9.4 s (14 min) and
11.9 s (16 min) against 66.5 and 74.4 s for `fast`: 7.1 and 6.2 times faster. This is an
estimate, not a measurement; neither the speed nor the quality was measured on real calls.

### What this does not show

- The floors, the warm-up length and the variants were chosen on the same 40 calls they
  are scored on, with the lab loop below. The suite then agreed with the loop within
  0.6 points of WER (1.1 points without noise), but no other set of calls checked the choice, and a difference of
  one or two points between variants has no confidence interval here.
- The suite's calls have one constant delay and no clock drift between mic and call
  track, which is the best case for a fixed delay. A call that changes device for part of
  its length keeps one delay as long as most primed windows agree on it, so up to a
  quarter of the call can run with a wrong delay; with AEC3's own estimator off, nothing
  corrects it. Clock drift is not handled at all.
- After the warm-up the stream starts again at t = 0, so for about 300 ms AEC3 sees render
  history from the end of the warm-up stretch. Amanu passes the mic unchanged before the
  first far-end sample, which hides this in the suite; its effect was not measured apart.
- With `AMANU_ECHO_WORKERS` above 1 every worker repeats the prime and the warm-up.
- The record names the variant even when it fell back to plain `sonora`; the log says so
  (`echo delay prime found no delay for the whole recording`).

## 1. Why the linear filter failed: its delay kept changing

A harness that calls `sonora_ffi.dll` and `liblocalvqe.dll` from Python the way
`SonoraBackend` and `LocalVqeBackend` do ran one scene in 1.5 s instead of a 15 to 20
minute suite run, and reproduced the suite's numbers for `sonora` (ERLE 17.5 dB, SI-SDR
gain 4.0 dB, voice −5.9 dB) and `fast` (11.4, 8.1, −0.0) exactly. A new call,
`wap_aec3_debug`, reads AEC3's state.

- **The delay estimate.** On 150 s of far-end speech through the suite's rooms, AEC3's
  delay changed 101 times in room A and 69 times in room B. Each change makes the
  subtractor reset its filters completely, as WebRTC does, so the filter never converged:
  2 to 5 dB at 52 ms, and no better at 160 or 320 ms. In the same rooms with a strong
  direct sound (direct path 6 dB above the tail) the delay changed once and the filter
  converged to 13 dB at 52 ms, 33 dB at 160 ms and 50 dB at 320 ms in room A (11, 22
  and 35 dB in room B).
- **The delay itself was right.** AEC3 settled on 76 ms in room A (80 ms) and 180 to
  192 ms in room B (180 ms); the 180 ms latency is not the problem.
- **Transparent mode** never switched on; AEC3 judged the filter usable 88 to 91 % of the time.
- **The speaker model** is not the problem: its nonlinear part is 25.6 dB below the echo,
  and scenes without it gave the same filter output (4.4 against 4.2 dB in room A).
- **The rooms are hard for a short filter.** Their direct sound carries only 5.2 % (room A)
  and 2.6 % (room B) of the echo's energy; the rest is a dense tail of 0.30 and 0.55 s.
  A perfect 52 ms filter could remove at most 9.7 and 5.6 dB there. LocalVQE `fast` is
  not a short filter: its front end has 16 partitions of 256 samples, 256 ms.

With the delay given from outside (`delay.use_external_delay_estimator`, a new tuning
knob, and `wap_set_stream_delay_ms`), the delay changed 0 times and the suite's rooms
converged to 27 dB (A) and 17 dB (B) with a 160 ms filter, and 44 and 25 dB with 256 ms,
against 38 to 56 dB for `fast` after its first 10 s. A stronger hysteresis on AEC3's own
estimate (`detect_pre_echo=false`, `hysteresis_limit_blocks=25`, `converged=200`) was
enough in room A (7 changes) but not in room B (53).

## 2. It is not the port

`.github/workflows/aec3-upstream.yml` on branch `build-sonora` builds the C++ module Sonora
was ported from (WebRTC M145, Sonora's `cpp` submodule at `3b33b6e`) and runs it on 40 AEC
Challenge clips and on the suite's two rooms made from the same public speech; Sonora ran
on the same files here. Double-talk SI-SDR gain / voice level / far-only ERLE, dB:

| Input | C++, default | Sonora, default | C++, 256 ms + external delay | Sonora, 256 ms + external delay |
|---|---|---|---|---|
| AEC Challenge clips, 10 s | 1.9 / −4.1 / – | 1.8 / −4.1 / – | | |
| Same clips after 10 s of their echo alone | 1.7 / −4.9 / 25.6 | 1.5 / −5.0 / 25.9 | | |
| Suite room A, 100 s | 3.3 / −13.2 / 25.8 | 3.4 / −13.1 / 28.9 | 8.6 / −7.3 / 18.3 | 8.7 / −7.2 / 16.6 |
| Suite room B, 100 s | 3.9 / −12.5 / 25.8 | 3.7 / −13.0 / 25.1 | 8.3 / −7.4 / 18.8 | 8.3 / −7.4 / 18.1 |

The two clean the voice the same within 0.2 dB and remove far-only echo within 3 dB.

## 3. Is the test fair?

Partly not. The suite's rooms are far more reverberant than a laptop's speaker-to-mic path
(direct-to-reverberant ratio −12.6 and −15.7 dB), which is what made AEC3's delay estimate
wander. On the AEC Challenge's own synthetic double talk (real and simulated room
responses, half of them with the far end's speaker distortion, clean near-end truth),
SI-SDR gain / voice, dB, with the first warm-up (the first 60 s of the input):

| Input | sonora | delay, 256 ms, suppressed | delay, 256 ms, linear, warm-up | fast |
|---|---|---|---|---|
| AEC Challenge clips, 10 s | 1.8 / −4.1 | 2.4 / −1.9 | 4.8 / −0.3 | 8.1 / −0.0 |
| Same clips after 10 s of their echo alone | 1.5 / −5.0 | 3.6 / −1.9 | 8.1 / −0.3 | 9.6 / −0.0 |
| Suite room A, 100 s | 3.4 / −13.1 | 8.7 / −7.2 | 17.4 / −0.2 | 14.9 / −0.2 |
| Suite room B, 100 s | 3.7 / −13.0 | 8.3 / −7.4 | 16.8 / −0.1 | 14.4 / −0.1 |

On 10 s clips a 256 ms filter has not converged and `fast` stays ahead. In the warm-up
column a clip shorter than the warm-up is run in full once before it is scored, and in
the 100 s rows 60 % of the scored audio is, while `fast` gets no pre-pass; so these rows
compare an offline method with an online one, which is what Amanu can choose between,
not the two filters alone. The suite's `aec-real` far-end clips are real devices: there
plain `sonora` removes 21.7 dB and `fast` 7.3 dB.

## 4. What being offline allows

- **(a) The delay prime.** `SonoraBackend` takes part in the delay prime that LocalVQE
  models use (`IEchoDelayPrimer`) and runs LocalVQE's prime on 30 s windows: the first
  four, then every fourth. When at least 75 % of the windows that found a delay are within
  128 samples of their median, AEC3 is given that delay; otherwise the variant runs as
  plain `sonora`. One delay for the whole of `aec-real`, which joins forty devices, cost
  12 dB of ERLE there (21.7 against 9.9 dB with the suppressor, 1.6 dB with the linear
  output); 4 of its 33 windows agreed with the whole-signal delay, against all confident
  windows in the suite's calls. The prime lands 128 to 224 samples early, which a longer
  filter absorbs. Priming the whole signal and every window took as long as Sonora
  itself on a long call; every window alone, about half that; the sampled windows, a
  third of that.
- **(b) Two passes, or a warm-up.** Running the file twice (output from the second pass)
  gave SI-SDR gain 8.4 dB with the linear output; running the first 60 s once before the
  stream gave 7.4 dB. That first warm-up held only about 20 s of far-end speech in the
  suite's calls, and the linear output then left 103 far-end words in far-only parts of
  my transcript; those the 0.5 floor left were all in the first far-only block of the
  loudest room A calls, where the filter reached only 10 to 16 dB against 25 to 28 dB
  after two passes. The far-active frames from all over the file,
  joined, were worse still: AEC3 adapts wrongly at every join. One contiguous 60 s stretch
  with the most far-end speech, found during the prime (`WarmupWindow`), brought the first
  block to 19 to 28 dB, as two passes did, and the gain to 8.4 dB; a 90 s stretch gave
  the same.
- **(c) A longer filter only after the delay was fixed:** 160 ms gave 17.7 dB ERLE and
  5.6 dB gain with the suppressor, 256 ms 15.2 and 5.4 in one pass, 24.0 and 5.7 with two.

## 5. After the filter

The default suppressor still lowered the voice by 4 to 5 dB once the filter worked. Two new
knobs set a floor under its gain: `suppressor.amanu_gain_floor` in every block, and
`suppressor.amanu_nearend_gain_floor` only while AEC3 finds the near end dominant.

To choose between them a second lab loop wrote each lab output as a recording and had
Amanu transcribe it with its own echo removal off; it gave 27.6 % / 12.9 % / 21 echo words
for a variant the suite measured at 27.2 / 12.4 / 24, and `fast` 28.7 / 12.0 / 0 against
the suite's 28.7 / 11.5 / 0. With the 60 s stretch (my WER / no noise / echo words):

| After the filter | Lab loop | Suite |
|---|---|---|
| linear output | 27.5 / 12.2 / 0 | 27.8 / 11.1 / 1 |
| floor 0.5 | 23.0 / 12.0 / 4 | 23.6 / 11.4 / 5 |
| floor 0.6 | 23.8 / 12.1 / 1 | – |
| floor 0.7 | 25.1 / 12.2 / 0 | 25.2 / 11.2 / 2 |
| near-end tuning, near-end floor 0.7 | 24.4 / 11.5 / 4 | 23.9 / 12.6 / 7 |
| near-end tuning, floor 0.7 | 25.8 / 11.8 / 0 | 25.5 / 10.8 / 2 |
| `fast` | 28.7 / 12.0 / 0 | 28.7 / 11.5 / 0 |

Signal scores in the lab for the floors (ERLE / SI-SDR gain, no noise in brackets /
voice, dB): floor 0.35 17.3 / 7.9 (11.3) / −2.9, floor 0.5 15.8 / 8.4 (12.6) / −2.2,
floor 0.7 14.1 / 8.7 (13.8) / −1.3, near-end floor only 0.5 to 1.0 20.7 to 21.7 / 6.7 to
6.9 / −3.2 to −3.9.

The hybrid of the brief's step 5, LocalVQE's filter before AEC3's suppressor, was not
built: once its delay was fixed and it had converged, AEC3's own filter cleaned as well
as LocalVQE's on the suite's calls.

## Kept as a record

- Run `sonora-tuning-first-warmup`: the first four delay variants with the first 60 s as
  the warm-up and every window primed. My WER / no noise / echo words: linear 33.1 / 14.2
  / 88, near-end 31.4 / 23.3 / 3, floor 0.5 27.2 / 12.4 / 24, near-end with floor 0.5
  27.4 / 12.8 / 15; echo time 87 s each.
- Run `sonora-tuning-two-primes`: linear and near-end once more with the whole signal and
  every window primed in two passes: the same transcripts, echo time 110 and 108 s.
- Lab only (40 calls; ERLE / SI-SDR gain, no noise in brackets / voice, dB): 160 ms with
  two passes and linear output 10.9 / 3.8 (1.7) / −4.0, worse than 256 ms because 160 ms
  does not cover room B's tail; two passes with `ep_strength.default_len=0` 22.2 / 6.1 /
  −4.2; with the gentle suppressor of `sonora-gentle` 19.5 / 7.1 / −2.8, and both 18.7 /
  7.3 / −2.5; the first 60 s with floors 0.25 to 0.5 15.8 to 13.2 / 7.2 to 7.8 / −3.1
  to −2.0.

## How to repeat it

The lab harness is not in the repository: it is Python with `ctypes` over the two DLLs in
`windows/native`, the suite's own `quality.py` functions for the calls and scores, and, for
WER, Amanu's `--measure-transcription` with `AMANU_MEASURE_ECHO=off` on the lab's output.
Scoring double talk needs each remover's constant latency (AEC3 suppressed 128 samples,
its linear output 64, LocalVQE `fast` −256), found where only the near end talks: lining
the output up by correlation over a far-only start picked a wrong lag and made the voice
look 12 dB quieter than it was.
