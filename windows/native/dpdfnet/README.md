# DPDFNet speech denoiser

`dpdfnet_baseline.onnx` tells the speech gate where I talk (`echo_model: solve-gate`,
`windows/src/Amanu.App/SpeechGate.cs`). The gate does not use the denoiser's sound: it
only reads where the denoiser's output is loud, and turns the cleaned mic down by 16 dB
elsewhere. The model runs through sherpa-onnx's `OfflineSpeechDenoiser`, which the app
already ships for the call's diarization, so there is no new native library.

- Source: https://github.com/k2-fsa/sherpa-onnx/releases/download/speech-enhancement-models/dpdfnet_baseline.onnx
  (the ONNX export of Ceva's DPDFNet baseline model, 2.3M parameters, 16 kHz).
- `dpdfnet_baseline.onnx`: 8,791,035 bytes, SHA-256
  `debaf0e8893479fca91b8b4c1eae8db195aa8980ccc9012f5809fde2b738151a`.
- License: Apache-2.0.

**The model file is not in the repository yet.** It is the first file under Apache-2.0
that the app would ship, so it waits for a decision. Until then:

- Put the file here by hand to build with it: `Amanu.App.csproj` copies it into
  `echo-runtime\` when it exists.
- A build without it runs `solve-gate` as `solve-floor` and writes
  `speech gate left out: no dpdfnet_baseline.onnx` in the `echo stages` log line.

When it ships, add `windows/Licenses/DPDFNet.txt` with the Apache-2.0 text and the
attribution to Ceva.

## Why a gate and not the denoiser's own output

Measured on the quality suite (`windows/tests/quality/lab/NOTES.md`, sections 2 and 6):
the denoiser's own output loses a quarter of my speech frames in a cafe at 0 dB (39.5 %
of my words wrong), while my speech as it was, with only the stretches between my
sentences turned down, gives 27 to 29 %. The engine needs quiet between the sentences
more than it needs clean sentences.
