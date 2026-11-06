# Amanu fork: offline echo removal and faster Windows transcription

A fork of [gsamat/amanu](https://github.com/gsamat/amanu) ("Records and transcribes online meetings"). It adds offline echo removal, more local Parakeet models and faster, measured transcription to the Windows app.

Fork: [Palo007/amanu](https://github.com/Palo007/amanu), branch `master`. Fork version: **0.7.0** (upstream is 0.6.x).

## Get it

Download the Windows portable zip from [Releases](https://github.com/Palo007/amanu/releases/latest), unzip it and run `Amanu.exe`. It has no installer and no auto-update.

Or build from source:

```powershell
dotnet publish windows\src\Amanu.App\Amanu.App.csproj -c Release -r win-x64 --self-contained true --property:Version=0.7.0 -o windows\artifacts\publish
```

Run `windows\artifacts\publish\Amanu.exe`. Fork builds are not code-signed, so Windows SmartScreen may warn you.

## What is in this fork's master

- Upstream `master` (Windows 0.6.6 links only) does not hold the Windows source. The Windows app lives on upstream's Windows development branch. This fork's `master` includes that work (about 98 commits by the upstream author), merged with upstream `master` up to Windows 0.6.6.
- Eight commits are new in this fork. They are the items below.
- Upstream's own `FORK.md` (the history from quill) is unchanged. This file is separate.

## What this fork adds

### Offline echo removal (LocalVQE)

| Item | Benefit | Platform | Where |
|---|---|---|---|
| Echo removal before transcription | Your mic track is cleaned of the other side's voice. Fewer double words in the transcript. | Windows (ported from the macOS feature) | `windows/src/Amanu.Core/Processing/EchoCancellation.cs`, `windows/src/Amanu.App/OfflineEchoAudio.cs` |
| Setting `offline_echo_cancellation`, on by default | You can turn it off. It falls back to the raw mic if it fails. | Windows | `windows/src/Amanu.Core/Configuration/` |
| Delay estimated over the whole file first | Fixes a wrong delay lock on real recordings. Echo fell to -70.9 dB (was -52.7 to -54.9 dB without it). | Windows | `EchoCancellation.cs` |
| Bundled `liblocalvqe.dll` and model | Works offline. No download. Model is checked by SHA-256. | Windows | `windows/native/localvqe/` (patch, rebuild steps, licenses in `windows/Licenses/`) |
| Library is never unloaded | Fixes a freeze: the queue stuck on "transcribing..." at 0% CPU after echo removal. | Windows | `EchoCancellation.cs` |

### Local models

| Item | Benefit | Platform | Where |
|---|---|---|---|
| `parakeet-ultra` engine (downloaded) | Another local Parakeet model to choose. | Windows | `windows/src/Amanu.Core/Processing/LocalModels.cs`, `windows/src/Amanu.App/ModelManager.cs` |
| `parakeet-sk` engine (KInIT Slovak fine-tune) | Local Slovak transcription. Needs a locally converted model file. | Windows | same files |

### Transcription speed (measured)

| Item | Benefit | Platform | Where |
|---|---|---|---|
| Per-stage timings in `transcribe.log` | You can see where the time goes: decode, echo removal, ASR per track. | Windows | `windows/src/Amanu.App/ProcessingCoordinator.cs` |
| `Amanu.exe --measure-transcription` and `Measure-Transcription.ps1` | Repeat a run on a copy of a recording. Prints timings, words and WER. | Windows | `windows/scripts/Measure-Transcription.ps1`, `windows/src/Amanu.App/MeasureTranscription.cs` |
| Explicit thread count and BelowNormal priority | ASR is faster because batch work no longer loses CPU to other apps. | Windows | `windows/src/Amanu.App/TranscriptionServices.cs` |

Measured on a 14-minute call (CPU only, same words in the output):

| Step | ASR time | Total |
|---|---|---|
| Step 0 (Idle priority, CLI default threads) | 238.7 s | 362.9 s |
| Step 1 (16 threads, BelowNormal) | **199.0 s** (about -17 %) | 319.9 s |

Another call: ASR 259.4 s down to 238.8 s (-8 %). Numbers move about 10 % between runs. Details: [docs/roadmap-speed.md](docs/roadmap-speed.md). Source review: [docs/reviews/](docs/reviews/).

Quality check on a quiet speaker (one minute, hand-checked): Parakeet had 0.0 % WER over 152 words.

### UI

| Item | Benefit | Platform | Where |
|---|---|---|---|
| Recordings window changes | Small fixes to the recordings list. The commit has no description. | Windows | `windows/src/Amanu.App/RecordingsWindow.cs` |

### Tests and tools

| Item | Benefit | Platform | Where |
|---|---|---|---|
| Echo removal tests | Guards the echo code. | Windows | `windows/tests/Amanu.Core.Tests/OfflineEchoTests.cs` |
| ASR thread test | Guards the thread-count rule. | Windows | `windows/tests/Amanu.Live.Tests/LocalAsrThreadsTests.cs` |
| Measure script with reference scoring | Scores a transcript against a reference. Supports one-speaker references and `-ScoreOnly`. | Windows | `windows/scripts/Measure-Transcription.ps1` |

## Who should use it

- You record calls on Windows and want echo removed offline.
- You want local Parakeet, including Slovak.
- You want to measure and tune transcription speed.
- You can build from source.

## Who should stay on upstream

- You use macOS. This fork adds nothing for macOS (the echo feature already exists there).
- You want a signed installer and auto-update. Upstream ships them. This fork ships only an unsigned portable zip.
- You want the most tested build. Upstream's releases are validated on clean installs.

## Status

| Area | State |
|---|---|
| Echo removal, model choice, timings, thread setting | Done and measured on two real recordings (2026-11-06). All 211 Windows tests pass. |
| Roadmap step 0 (measure) and step 1 (threads, priority) | Done. |
| Roadmap steps 2-7 | Open: both tracks in parallel, system track during echo removal, level normalization, fewer model reloads, skip silence, GPU (Vulkan). |
| Release builds | Windows portable zip from 0.7.0. No signing, no installer, no auto-update. |
| Wider hardware testing | Measured on one PC (Ryzen 7 5800H). Not tested elsewhere. |
