# Sonora echo canceller

`sonora_ffi.dll` is the default echo remover (`echo_model: sonora`). Sonora is a
Rust port of WebRTC's AEC3 echo canceller, used here through its C API (`wap_*`).

- Source: https://github.com/dignifiedquire/sonora, commit `1632d47c1e77d0857f9145bd175134b348541e11`
- Build: GitHub Actions, `.github/workflows/sonora-windows.yml` on branch `build-sonora`, on `windows-latest`:
  `RUSTFLAGS=-C target-feature=+crt-static cargo rustc -p sonora-ffi --release --crate-type cdylib`.
  The static CRT means the DLL imports only Windows system libraries.
- `sonora_ffi.dll`: 918,016 bytes, SHA-256 `a2afc38614aed013e1f158e180c8b4f80459df2a43f5e67f03bf2e82e6654deb`
- License: BSD-3-Clause, in `windows/Licenses/Sonora.txt`.

Amanu turns on only the echo canceller: high-pass filter, noise suppression, gain
control and the other stages stay off, so the mic copy changes only where echo is.
`SonoraBackend` feeds 10 ms frames and keeps LocalVQE's one-hop output delay, so
`EchoCanceller` drives both the same way.

## Why it is the default

On two real calls on a Ryzen 7 5800H (14 and 16 minutes):

| Call | Echo removal, Sonora | Echo removal, LocalVQE 2.7K | Word errors, Sonora | Word errors, 2.7K |
|---|---|---|---|---|
| 1000 | 4.5 s | 66.5 s | 204 / 489 | 207 / 489 |
| 1021 | 5.9 s | 74.4 s | 143 / 467 | 142 / 467 |

The quality suite in `windows/tests/quality` measures it further, under noise.
