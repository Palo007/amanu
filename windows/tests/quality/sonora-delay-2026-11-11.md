# Sonora's delay in double talk, 2026-11-11

Follows `sonora-tuning-2026-11-10.md`, which found that AEC3's linear filter cleaned the
near voice in double talk by only 2 to 4.5 dB and that no suppressor tuning helped the
transcript. This asks why the filter fails, whether the port or the test is to blame,
and what an offline caller can do about it. The suite run is `sonora-tuning` again (full
report: `results-2026-11-11-sonora-delay.md`); the rows of earlier variants are the same
results scored again.

## Result

| Echo remover | My WER % | My WER %, no noise | Echo words | ERLE synthetic (dB) | ERLE real (dB) | Double-talk voice (dB) | Double-talk SI-SDR gain (dB) | Echo time (s) |
|---|---|---|---|---|---|---|---|---|
| off | 58.5 | 46.1 | 603 | 0.0 | 0.0 | −0.1 | 0.0 | 0 |
| sonora | 38.9 | 33.5 | 4 | 17.5 | 21.7 | −6.0 | 3.9 | 39 |
| fast | 28.7 | 11.5 | 0 | 11.4 | 7.3 | −0.0 | 8.1 | 511 |
| thorough | 25.4 | 14.3 | 2 | 22.0 | 19.4 | −1.8 | 11.0 | 929 |
| sonora-long | 37.2 | 29.1 | 5 | 14.0 | 16.4 | −5.1 | 4.5 | 49 |
| sonora-delay-linear | 33.1 | 14.2 | 88 | 9.3 | 21.7 | −0.2 | 7.4 | 87 |
| sonora-delay-nearend | 31.4 | 23.3 | 3 | 14.1 | 21.7 | −2.3 | 6.5 | 87 |
| sonora-delay-floor | 27.2 | 12.4 | 24 | 13.2 | 21.7 | −2.0 | 7.8 | 87 |
| sonora-delay-nearend-floor | 27.4 | 12.8 | 15 | 11.8 | 21.7 | −1.2 | 7.7 | 87 |

`sonora-delay-*` give AEC3 the echo delay from LocalVQE's delay prime instead of letting
it estimate the delay itself, use a 256 ms linear filter instead of 52 ms, and run the
first 60 s of the recording once before the stream with the output dropped. They differ
only in what comes after the filter:

- `sonora-delay-linear`: the linear filter's output, no suppressor.
- `sonora-delay-nearend`: WebRTC's suppressor in the near-end tuning of `sonora-nearend`.
- `sonora-delay-floor`: WebRTC's suppressor, which may lower no band below half its level.
- `sonora-delay-nearend-floor`: both.

Why each result:

- `sonora-delay-linear`: with the delay fixed, the 256 ms filter converges and the voice keeps
  its level in double talk (−0.2 dB), so without noise the transcript nearly reaches `fast`
  (14.2 against 11.5 %). The filter alone removes 9.3 dB, though, and with the loudest echo
  in room A the rest is transcribed: 88 echo words, most of them at SER −15 in the office.
- `sonora-delay-nearend`: the suppressor takes those words out (3), but with loud echo in
  room B it still cuts the voice by up to 12 dB, so its WER without noise is 23.3 %.
- `sonora-delay-floor`: the suppressor removes 4 dB more echo than the filter alone, and
  the floor keeps its voice loss to 6 dB in any band, 2.0 dB on average. My WER is 27.2 %
  against 28.7 % for `fast`, 12.4 % without noise against 11.5 %. It is clearly better than
  `fast` at SNR 0 and −5 dB (where every remover does badly) and worse with loud echo at
  moderate noise, and it leaves 24 echo words, 5 to 7 per call at SER −15 in the office.
- `sonora-delay-nearend-floor`: the same with the near-end tuning: the voice loses 1.2 dB,
  15 echo words, my WER 27.4 %.
- All four remove 21.7 dB on `aec-real`, as plain `sonora`: the forty devices give no
  common delay, so they run as plain `sonora` there (section 4a).

Speed: 87 s for the 40 calls against 511 s for `fast`, 5.9 times faster. The calls last
about 160 s, so the 60 s warm-up adds a third; on a long call it adds little, but the delay
prime does. From the parts (the 256 ms filter costs 1.7 times the 52 ms one, the prime
4.2 s per 1000 s of audio), the two real calls of `windows/native/sonora/README.md` would
take about 11.7 s (14 min) and 14.6 s (16 min) against 66.5 and 74.4 s for `fast`: 5.7
and 5.1 times faster. This is an estimate, not a measurement. About a third of it is the
prime, which could run on every fourth window instead of every one.

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
against 38 to 56 dB for `fast` after its first 10 s. A stronger hysteresis on AEC3's own estimate
(`detect_pre_echo=false`, `hysteresis_limit_blocks=25`, `converged=200`) was enough in
room A (7 changes) but not in room B (53).

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
responses, half of them with the far end's speaker distortion, clean near-end truth), the
order does not change, but the gaps do (SI-SDR gain / voice, dB):

| Input | sonora | delay, 256 ms, suppressed | delay, 256 ms, linear, 60 s warm-up | fast |
|---|---|---|---|---|
| AEC Challenge clips, 10 s, cold | 1.8 / −4.1 | 2.4 / −1.9 | 4.8 / −0.3 | 8.1 / −0.0 |
| Same clips after 10 s of their echo alone | 1.5 / −5.0 | 3.6 / −1.9 | 8.1 / −0.3 | 9.6 / −0.0 |
| Suite room A, 100 s | 3.4 / −13.1 | 8.7 / −7.2 | 17.4 / −0.2 | 14.9 / −0.2 |
| Suite room B, 100 s | 3.7 / −13.0 | 8.3 / −7.4 | 16.8 / −0.1 | 14.4 / −0.1 |

On 10 s clips a 256 ms filter has not converged and `fast` stays ahead; on long input,
which is what Amanu cleans, the delay-primed linear filter is ahead of `fast`. The suite's
`aec-real` far-end clips are real devices: there plain `sonora` removes 21.7 dB and `fast`
7.3 dB.

## 4. What being offline allows

- **(a) The delay prime.** `SonoraBackend` now takes part in the whole-signal delay prime
  that LocalVQE models use (`IEchoDelayPrimer`) and runs LocalVQE's prime on 30 s windows.
  When at least 75 % of the windows that found a delay are within 128 samples of their
  median, AEC3 is given that delay; otherwise the variant runs as plain `sonora`. One
  delay for the whole of `aec-real`, which joins forty devices, cost 12 dB of ERLE there
  (21.7 against 9.9 dB with the suppressor, 1.6 dB with the linear output); 4 of its 33
  windows agreed with the whole-signal delay, against 4 of 4 or 5 in the suite's scenes. The prime lands 128 to 224 samples
  early, which a longer filter absorbs. A first version primed the whole signal and every
  window in two passes; the second pass cost as much as Sonora itself and was dropped.
- **(b) Two passes.** Running the file twice (output from the second pass) gave SI-SDR
  gain 8.4 dB with the linear output; running only the first 60 s before the stream gave
  7.4 dB at a fraction of the cost and needs no copy of the whole recording, so the
  variants use the warm-up. Lab sums over the 40 scenes: two passes 98 s, warm-up 79 s,
  one pass 59 s, plain `sonora` 35 s, `fast` 442 s.
- **(c) A longer filter only after the delay was fixed:** 160 ms gave 17.7 dB ERLE and
  5.6 dB gain with the suppressor, 256 ms 15.2 and 5.4 in one pass, 24.0 and 5.7 with two.

## 5. After the filter

The default suppressor still lowered the voice by 4 to 5 dB once the filter worked, and
the linear output alone left enough echo in the loudest room A scenes (SER −15) to be
transcribed as 7 to 27 words per call. Between the two, a new knob,
`suppressor.amanu_gain_floor`, keeps every band at no less than the given gain. Lab,
40 scenes, 60 s warm-up (ERLE / SI-SDR gain, no noise in brackets / voice, dB):

| After the filter | ERLE | SI-SDR gain | Voice |
|---|---|---|---|
| suppressor, default | 19.9 | 5.8 (6.4) | −4.2 |
| suppressor, near-end tuning | 14.1 | 6.5 (8.0) | −2.3 |
| floor 0.25 | 15.8 | 7.2 (10.0) | −3.1 |
| floor 0.35 | 14.7 | 7.5 (11.0) | −2.6 |
| floor 0.5 | 13.2 | 7.8 (12.3) | −2.0 |
| near-end tuning, floor 0.5 | 11.8 | 7.7 (12.5) | −1.2 |
| linear output | 9.3 | 7.4 (13.5) | −0.2 |
| `fast` | 11.4 | 8.1 (13.3) | −0.0 |

The hybrid of the brief's step 5, LocalVQE's filter before AEC3's suppressor, was not
built: once its delay was fixed, AEC3's own filter cleaned as well as LocalVQE's on long
input.

## Kept as a record

Lab only (40 scenes; ERLE / SI-SDR gain, no noise in brackets / voice, dB): 160 ms with
two passes and linear output 10.9 / 3.8 (1.7) / −4.0, worse than 256 ms because 160 ms
does not cover room B's tail; two passes with `ep_strength.default_len=0` 22.2 / 6.1 / −4.2;
with the gentle suppressor of `sonora-gentle` 19.5 / 7.1 / −2.8, and both 18.7 / 7.3 / −2.5.
The first suite run of `sonora-delay-linear` and `sonora-delay-nearend`, with the two-pass
prime, is in run `sonora-tuning-two-primes`: the same WER, echo words and scores as below,
echo time 110 and 108 s.

## How to repeat it

The lab harness is not in the repository: it is Python with `ctypes` over the two DLLs in
`windows/native`, the suite's own `quality.py` functions for the scenes and scores, and
the remover latencies below. Scoring double talk needs each remover's constant latency
(AEC3 suppressed 128 samples, its linear output 64, LocalVQE `fast` −256): lining the
output up by correlation over a far-only start picked a wrong lag and made the voice look
12 dB quieter than it was.
