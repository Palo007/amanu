# Sonora double-talk tuning, 2026-11-10

Run `sonora-tuning` (full report: `results-2026-11-10-sonora-tuning.md`). The
`off`, `fast` and `thorough` rows are the results of run `first` scored again. Plain `sonora`
was run again with the patched DLL and its cleaned mic is identical byte for byte to run `first`.

## The 20 dB was mostly a scoring error

`double_near_gain_db` lined the cleaned mic up with the raw mic once for the whole file.
Amanu passes the mic through unchanged where the call is silent and runs the remover
only where it plays, and there each remover adds its own small delay: Sonora 128 samples,
its linear output 64, LocalVQE `fast` −256, `thorough` 0. So in double talk the output
was compared a few milliseconds off, and the voice looked 15 to 20 dB quieter than it was.
`fast` showed the same false −15 dB. `quality.py` now finds the lag for each region
(`cut_aligned`). With it, Sonora lowers the near voice in double talk by 6.0 dB on average
(8.5 dB without noise, 17.5 dB at worst in room B with loud echo), not about 20.

## Variants

Each is an `echo_model` value; the tuning strings are in `LocalVqeAssets` in
`windows/src/Amanu.Core/Processing/EchoCancellation.cs`.

| Echo remover | My WER % | My WER %, no noise | Echo words | ERLE synthetic (dB) | ERLE real (dB) | Double-talk voice (dB) | Double-talk SI-SDR gain (dB) | Echo time (s) |
|---|---|---|---|---|---|---|---|---|
| off | 58.5 | 46.1 | 603 | 0.0 | 0.0 | −0.1 | 0.0 | 0 |
| sonora | 38.9 | 33.5 | 4 | 17.5 | 21.7 | −6.0 | 3.9 | 39 |
| fast | 28.7 | 11.5 | 0 | 11.4 | 7.3 | −0.0 | 8.1 | 511 |
| thorough | 25.4 | 14.3 | 2 | 22.0 | 19.4 | −1.8 | 11.0 | 929 |
| sonora-linear | 44.1 | 28.0 | 124 | 2.7 | 4.7 | −0.2 | 2.1 | 40 |
| sonora-nearend | 38.9 | 33.2 | 3 | 11.5 | 15.1 | −3.8 | 3.5 | 39 |
| sonora-gentle | 39.5 | 33.3 | 3 | 11.3 | 15.0 | −3.6 | 3.6 | 40 |
| sonora-long | 37.2 | 29.1 | 5 | 14.0 | 16.4 | −5.1 | 4.5 | 49 |
| sonora-long80 | 37.9 | 28.7 | 2 | 9.7 | 10.8 | −3.2 | 3.5 | 60 |
| sonora-long80-nearend | 40.0 | 28.8 | 21 | 5.4 | 5.7 | −1.4 | 2.7 | 60 |

- `sonora-linear`: AEC3's linear filter output, before the suppressor. Keeps the voice, removes almost no echo.
- `sonora-nearend`: double talk detected sooner (echo below the voice instead of a quarter of it, 4 blocks instead of 12, held 400 ms), and while detected, bands pass until the echo is 1.5 to 3 times the voice.
- `sonora-gentle`: `sonora-nearend`, and the normal suppressor also needs twice the default echo-to-voice ratio.
- `sonora-long`, `sonora-long80`: a 160 ms and a 320 ms linear filter instead of 52 ms.
- `sonora-long80-nearend`: both.

## What it shows

Tuning the suppressor raises the voice level in double talk (from −6.0 to −1.4 dB) but
not the transcript: my WER stays at 37 to 40 % against 28.7 % for `fast`. The reason is
the linear filter. It cleans the voice by only 2 to 4.5 dB SI-SDR in double talk, against
8.1 for `fast` and 11.0 for `thorough`; the scenes' echo passes a nonlinear speaker
model and rooms that ring for 300 to 550 ms, which AEC3's filter does not follow, so the
echo left in the voice is either cut together with the voice (default) or left in it
(gentler tunings). A longer filter helps a little (`sonora-long`, −1.7 points) and then
converges worse (`sonora-long80` removes less echo). No setting reached `fast`.
