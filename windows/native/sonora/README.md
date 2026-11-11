# Sonora echo canceller

`sonora_ffi.dll` is the default echo remover (`echo_model: sonora`). Sonora is a
Rust port of WebRTC's AEC3 echo canceller, used here through its C API (`wap_*`).

- Source: https://github.com/dignifiedquire/sonora, commit `1632d47c1e77d0857f9145bd175134b348541e11`
- Build: GitHub Actions, `.github/workflows/sonora-windows.yml` on branch `build-sonora`, on `windows-latest`:
  the workflow applies `amanu-aec3-tuning.patch` (a copy of it lives in `.github/sonora/` on that
  branch) and runs `RUSTFLAGS=-C target-feature=+crt-static cargo rustc -p sonora-ffi --release --crate-type cdylib`.
  The static CRT means the DLL imports only Windows system libraries (KERNEL32, ntdll, api-ms-win-core-synch).
- `sonora_ffi.dll`: 964,096 bytes, SHA-256 `35f84d9855dd22ed7a27fe99e444099d80f6a203bee0518c79801d8839e9bab5`
  (run 42929610801). It gives byte-identical output to the previous build (957,440 bytes,
  `9bdf9b05…24785d6e`, run 42905777131) for every earlier tuning. The unpatched build was
  918,016 bytes, `a2afc386…6654deb`.
- License: BSD-3-Clause, in `windows/Licenses/Sonora.txt`.

Amanu turns on only the echo canceller: high-pass filter, noise suppression, gain
control and the other stages stay off, so the mic copy changes only where echo is.
`SonoraBackend` feeds 10 ms frames and keeps LocalVQE's one-hop output delay, so
`EchoCanceller` drives both the same way.

## Amanu's patch

The C API exposes no AEC3 tuning, and the default suppressor lowers the near voice by
about 20 dB while both people talk. `amanu-aec3-tuning.patch` adds two functions:

- `wap_create_with_aec3_tuning(config, tuning)`: like `wap_create_with_config`, but the
  default `EchoCanceller3Config` is changed by `tuning`, a list of `path=value` entries
  separated by `;`, for example `suppressor.dominant_nearend_detection.enr_threshold=1`.
  `output=linear` hands out the linear filter's output, before the suppressor.
- `wap_check_aec3_tuning(tuning)`: 0, or the 1-based index of the first entry it does not know.
- `wap_aec3_debug(apm, out)`: AEC3's state for diagnosis: how often the echo delay changed
  (each change resets the linear filter), the render and filter delays, whether AEC3 trusts
  its linear filter, transparent mode and its ERLE estimate.

The tuning also reaches the delay estimator (`delay.*`, including
`delay.use_external_delay_estimator`, which makes AEC3 take the delay given with
`wap_set_stream_delay_ms` instead of estimating it), the filters' noise gates and
initial-phase settings, and `suppressor.amanu_gain_floor`, a new knob: the lowest gain,
0 to 1, the suppressor may give any band (0, the default, is no floor).

`wap_create_with_config` is unchanged, so `echo_model: sonora` runs as before. The
`sonora-*` echo models are the tunings under test; see `LocalVqeAssets` in
`EchoCancellation.cs` for their values and `windows/tests/quality` for the numbers.

The `sonora-delay-*` models also take the echo delay from LocalVQE's whole-signal prime
(`SonoraPrime` in `SonoraBackend.cs`), so they need `liblocalvqe.dll` and the 2.7K model
beside Sonora. They use it only when most 30 s windows of the recording agree on it;
otherwise they run as plain `sonora`.

The C++ module Sonora was ported from (WebRTC M145, Sonora's `cpp` submodule) is run on
the same input by `.github/workflows/aec3-upstream.yml` on branch `build-sonora`; on
2026-11-11 it cleaned the near voice in double talk as Sonora does within 0.2 dB, and removed
far-only echo within 3 dB, with both the default and the delay-primed 256 ms tuning
(`windows/tests/quality/sonora-delay-2026-11-11.md`).

## Why it is the default

On two real calls on a Ryzen 7 5800H (14 and 16 minutes):

| Call | Echo removal, Sonora | Echo removal, LocalVQE 2.7K | Word errors, Sonora | Word errors, 2.7K |
|---|---|---|---|---|
| 1000 | 4.5 s | 66.5 s | 204 / 489 | 207 / 489 |
| 1021 | 5.9 s | 74.4 s | 143 / 467 | 142 / 467 |

The quality suite in `windows/tests/quality` measures it further, under noise.
