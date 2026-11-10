# Sonora echo canceller

`sonora_ffi.dll` is the default echo remover (`echo_model: sonora`). Sonora is a
Rust port of WebRTC's AEC3 echo canceller, used here through its C API (`wap_*`).

- Source: https://github.com/dignifiedquire/sonora, commit `1632d47c1e77d0857f9145bd175134b348541e11`
- Build: GitHub Actions, `.github/workflows/sonora-windows.yml` on branch `build-sonora`, on `windows-latest`:
  the workflow applies `amanu-aec3-tuning.patch` (a copy of it lives in `.github/sonora/` on that
  branch) and runs `RUSTFLAGS=-C target-feature=+crt-static cargo rustc -p sonora-ffi --release --crate-type cdylib`.
  The static CRT means the DLL imports only Windows system libraries (KERNEL32, ntdll, api-ms-win-core-synch).
- `sonora_ffi.dll`: 957,440 bytes, SHA-256 `9bdf9b051e6d5d4d5d6f09ce14ff2cf1ebd2fcdf7dfb6c08cce11c8b24785d6e`
  (run 42905777131). The unpatched build was 918,016 bytes, `a2afc386…6654deb`.
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

`wap_create_with_config` is unchanged, so `echo_model: sonora` runs as before. The
`sonora-*` echo models are the tunings under test; see `LocalVqeAssets` in
`EchoCancellation.cs` for their values and `windows/tests/quality` for the numbers.

## Why it is the default

On two real calls on a Ryzen 7 5800H (14 and 16 minutes):

| Call | Echo removal, Sonora | Echo removal, LocalVQE 2.7K | Word errors, Sonora | Word errors, 2.7K |
|---|---|---|---|---|
| 1000 | 4.5 s | 66.5 s | 204 / 489 | 207 / 489 |
| 1021 | 5.9 s | 74.4 s | 143 / 467 | 142 / 467 |

The quality suite in `windows/tests/quality` measures it further, under noise.
