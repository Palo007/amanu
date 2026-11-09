# Slack huddle summaries: roadmap

Started 2026-11-09. Source: the scripts in `C:\Backup\CoDiNG\Amanu\amanu-gemini`
(`amanu_gemini.py`, `diarize.py`, `README.md`).

Goal: each Slack huddle is recorded with no action from me, and a summary that
names the people correctly is ready to paste into the Slack channel.

Not in this roadmap (they need a Slack app that a workspace admin must approve):
find the huddle's channel from Slack, get the participant list from Slack, and
paste into the right channel without help. Slack has no public way to make a
draft, so a draft is not possible with or without an app.

Not ported: Gemini web automation (`amanu_web.py` and the Chrome extension). It
breaks each time Google changes the page.

## Status

| Step | What | Size | Session | State |
|---|---|---|---|---|
| 1 | Summary prompt for Slack | S | — | Installed; my review is open |
| 2 | People list | S | A: Speakers | Done, checked by hand |
| 3 | "Summary ready" card | M | B: Slack card | Done, checked by hand |
| 4 | Diarization (split the call side by voice) | M | A: Speakers | Parked: built, off by default |
| 5 | Stricter speaker-name checks | M | A: Speakers | Skipped for now: with step 4 off, only engines that label voices (AssemblyAI, later Gemini) need it |
| 6 | Gemini: measure transcription (and summary) | M | C: Engines | Measured; decision: yes, as an option (step 6b) |
| 6b | Gemini as an optional engine | L | C: Engines | Not started |
| 7 | Slovak Whisper (`whisper-sk`): measure | M | C: Engines | Measured; not better (higher WER, 4 times slower) |
| 8 | Slovak Canary (`canary-sk`): research, measure | L | C: Engines | Measured; not better (higher WER, a little slower) |
| 9 | Settings screen for more engines | M | D: Settings | Not started |
| 10 | Summaries on demand, several per recording | M | B: Slack card | Done, checked by hand |

## How to work on this (sessions)

The steps are put in four sessions. Steps in one session share the same code
and the same test recordings, so one session keeps that context once. Steps in
different sessions share almost nothing, so a new session costs less than one
long session that holds all of it.

| Session | Steps | Main code | Can run with |
|---|---|---|---|
| A: Speakers | 2 → 4 → 5 | `SpeakerNamingService`, `SpeakerLabels.cs`, `speakers.json`, `ProcessingCoordinator.cs` | B |
| B: Slack card | 3 → 10 (and my review of step 1) | a new card window, `TrayIconService.cs`, `RecordingsWindow.cs`, `TranscriptVersions.cs` | A, C |
| C: Engines | 7 → 6 → 8 | `LocalModels.cs`, `ModelManager.cs`, `TranscriptionServices.cs`, `AppSettings.cs` | B |
| D: Settings | 9 | `SettingsWindow.cs`, `SettingsSchema.cs` | none: start it after A, B and C are merged |

B (step 10) and A (step 2) both change `SummaryService` in `LanguageServices.cs`: A
changes the prompt, B changes where the summary is written. Keep the two
changes apart, and merge A first.

A and C both change `ProcessingCoordinator.cs` and `AppSettings.cs`. Do not run
them at the same time in one folder. Each session works in its own git
worktree (a second checkout on its own branch) and is merged when its steps
are done.

Rules for each session, to keep the context small:

1. Read only this file's "Status", this section, and the steps of the session.
   Each step lists the files and facts it needs, so do not explore the code again.
2. `docs/pitfalls.md` is about macOS. For Windows, read `windows/BUILDING-HERE.md`
   (build: `dotnet build`, tests: `dotnet test Amanu.Windows.slnx`).
3. Never read a whole transcript, log or recording into the context. They are
   private and large. Use a script that prints only counts, WER or a short diff.
4. Give research (a package, a model, a license) and long measurement runs to a
   subagent on a cheap model, with a report of 15 lines or less.
5. Show only the failing lines of build and test output.
6. At the end of each step: tick the items here, write the facts you found
   into the step (paths, numbers, decisions), commit (one prose sentence, as in
   `git log`), and update the "Status" table. The next session starts from these
   notes, not from the code.
7. Audio paths can only be checked with a real recording. Ask me to make one,
   and say what to check.

## What Amanu already has

- Auto-record for Slack: `slack.exe` is on the call list, and `meta.json` keeps
  `"process_family": "slack.exe"` for each recording.
- Speaker names from a model, accepted only with a "high" confidence answer and
  an exact quote (`SpeakerNamingService` in `LanguageServices.cs`,
  `SpeakerNameValidator` in `ProcessingPolicy.cs`).
- Summary engines: Claude Code, Anthropic API, OpenAI, Ollama, and a summary
  template that I can change (`summary.template`).
- The local Slovak engine `parakeet-sk` gives one label, `them`, for all the
  people on the call. Gemini transcripts give `them 1`, `them 2` and so on.

Test recordings (in `%USERPROFILE%\Amanu Recordings`):

| Recording | Engine | Call side | Use it for |
|---|---|---|---|
| `2026.10.29-1000` | Gemini | 25 labels, 13 named | names, diarization with many people |
| `2026.11.06-1021` | Parakeet | 1 person (Andrea) | quiet speaker, diarization must give 1 |
| `2026.11.06-1438` | Parakeet | several people | summary, diarization |
| `2026.11.09-1001` | Parakeet | several people | summary, diarization |

The speed set with references is in `%USERPROFILE%\Amanu Speed Set`. Measure
with `windows/scripts/Measure-Transcription.ps1`.

## Step 1: a summary prompt for Slack (size: S, no code)

- [x] Write a summary template for huddles. Done 2026-11-09:
  `docs/templates/slack-huddle-summary.md`. What it needs:
  - Who was there, by name (from `speakers.json`). If a speaker has no name, say "a colleague", not "them 2".
  - Decisions, one line each.
  - Action items: owner, task, date if one was said.
  - Open questions.
  - Short: fits on one screen in Slack. No headings deeper than one level.
  - Written in the language of the meeting.
- [x] Installed 2026-11-09 in `%LOCALAPPDATA%\Amanu Data\config.json`:
  `summary.enabled: true`, `backend: claude-cli`, `model: claude-sonnet-5-5`.
  Not `anthropic-api`: no Anthropic key is saved in Amanu. The old config is
  `config.json.bak-2026-11-09`.
- [ ] I read the three test notes and mark what I would change.
- [ ] Compare with the local summary engine.

Done when: summaries for 3 past huddles (one with 4+ people) need no edits
before I paste them.

Test 2026-11-09 (`claude-sonnet-5-5`, with the same CLI arguments that Amanu
uses), on `2026.10.29-1000`, `2026.11.06-1438` and `2026.11.09-1001`:

- The model copies English words from the prompt into Slovak notes. Fixes: the
  headings are slots (`<"Summary" in the language of the notes>`), and a task
  with no clear owner starts with `?`, which has no language. After the fixes,
  no English word was left in the three notes.
- Length: 303–371 words, more than the 250 that the prompt asks for.
- With one `them` label, the model still names the people who are called by
  name, and gives `?` to the tasks it cannot place: 2, 5 and 3 tasks.

## Step 2: a people list (size: S, session A)

- [x] New setting `speaker_names.people_file`. The default is
  `%LOCALAPPDATA%\Amanu Data\people.txt`. The format is the same as in
  amanu-gemini: one person per line, full name first, then other names with
  commas (`Joseph Smith, Joe, Jojo`). `(me)` marks my own line.
- [x] Give the list to the speaker-name prompt (`SpeakerNamingService`). Tell
  the model to pick from the list when the transcript says a name or nickname
  that is on it.
- [x] Give the list to the summary too, so that it writes full names.
- [x] Add the full names to the custom words, so that the transcription spells them correctly.
- [x] A Settings field that opens the file (a plain field now; step 9 places it).

Done when: the names on `2026.10.29-1000` are the full names from the list,
not the nicknames that the transcript holds.

What was built (branch `session-a-speakers`):

- `PeopleList.cs` (Core, Processing) reads the file: `#` lines are comments,
  `(me)` at the line end is case-blind, a second line with the same full name
  is dropped, at most 500 people. Tests: `PeopleListTests.cs`.
- The path: `ProcessingCoordinator.PeoplePath(settings)`; empty setting means
  `people.txt` in the data folder (`AMANU_TEST_DATA` in Debug, as for custom
  words). `%VARS%` in the setting are expanded. Read at each use: no restart.
- Naming: the list goes in the user message before "Unknown labels". It is not
  evidence: the "high" + exact-quote rule is unchanged. A name the model gives
  is mapped to the full name in code too (`PeopleList.FullNameFor`), but only
  when exactly one person has that nickname.
- The `(me)` line is the user's name when `user_name` is empty, before the
  Windows account name.
- Summary: the list is added to the system prompt after the language line.
- Custom words: `LoadCustomWords(settings)` adds the full names after the
  user's own words (so with 1000 words the user's own are kept).
- Settings, Advanced tab, Naming: "People list", new `SettingKind.File` = text
  field + "Open" button; the button makes the file with a comment header when
  it is missing.
- Checked by hand on 2026-11-09 on `2026.10.29-1000`: full names in names and summary.

## Step 3: the "summary ready" card (size: M, session B)

See "The card" at the end for how it looks and when it comes up. Today Amanu's
notifications are tray balloons (`TrayIconService.cs`, `ShowBalloonTip`). A
balloon cannot hold buttons, so the card is a small window of its own.

- [x] Show the card when all the conditions are true.
- [x] "Copy and open Slack": put the summary on the clipboard as rich text (HTML)
  and as plain text with Slack marks (`*bold*`, `•` lists), then start
  `slack://open` to bring Slack to the front.
- [x] "Open summary": open the recording in the Recordings window.
- [x] "Not now" and `×`: close it. The recording keeps a "Copy summary" button.
- [x] Write `slack-offer.json` in the recording folder (`shown`, `copied`, time),
  so that the card never comes up twice for one recording.
- [x] Settings: `slack_summary.enabled` (default on), `slack_summary.apps`
  (default `["slack.exe"]`), `slack_summary.min_minutes` (default 3).
- [x] Tests for the conditions, with fake recording folders (no UI).
- [x] Check by hand: paste into a Slack channel and a DM. Bold text, lists and
  names must look correct. Do not send.

Facts (session B, 2026-11-09):

- Rules and texts are in Core, `Processing/SlackOffer.cs`: `SlackOffer.Decide`
  (the six conditions of "The card"), `Record` (`slack-offer.json`: `shown`,
  `copied`, `time`), `SlackText` (plain text with Slack marks), `Preview`
  (first line and action-item count for the card), and `ClipboardHtml.Wrap`
  (the CF_HTML header, offsets in UTF-8 bytes). Tests: `SlackOfferTests.cs`.
- Condition 3 is the event `ProcessingCoordinator.SummaryWritten`, raised only
  after `SummaryService.GenerateAsync` succeeded. Condition 4 compares the local
  date of `summary.md`'s write time with `started` in `meta.json`.
- `AmanuRuntime.ConsiderSlackOffer` writes `slack-offer.json` before it raises
  `SlackOfferReady`. If a recording runs, the folder waits in memory and is
  checked again when the recording stops. A card that waits is lost if Amanu
  quits first; the "Copy summary" button is still there.
- The card is `SlackCard.cs` (App): no focus, top-most, bottom right of the work
  area, one card at a time. The HTML comes from Markdig (emphasis extras, task
  lists, pipe tables).
- "Copy summary" is in the Recordings window's button row (renamed from
  "Copy for Slack" by the user: it copies only and does not open Slack). It is enabled when
  the summary is done, and hidden when `slack_summary.enabled` is off.
- The app matches `process_family` with or without `.exe`, in any case.

Done when: after 3 real huddles the card came up each time, and the paste
needed no fixes.

## Step 4: split the call side by voice — diarization (size: M, session A)

Why: `parakeet-sk` puts everyone on the call under one label, `them`. Steps 2
and 5 can only name a label, so without this step they help only Gemini
transcripts.

What it is: two small neural networks and some math, not a large AI model.

| Part | What it does | Size |
|---|---|---|
| Segmentation (pyannote segmentation 3.0) | finds speech and where the speaker changes | 1.5 MB (int8) or 5.7 MB |
| Speaker embedding (WeSpeaker ResNet34) | makes a "voice fingerprint" of each piece | 25 MB |
| Clustering | groups pieces with alike fingerprints into one person | no model |

Known facts:

- `amanu-gemini\diarize.py` (55 lines) does it with sherpa-onnx on the CPU.
  The models are in `amanu-gemini\models\`. `compare\diar_check.py` lines up
  the transcript with the diarization result.
- Test on `2026.10.29-1000` (about 13 named people): the speaker count depends
  on the threshold (how alike two voices must be to count as one person).

  | Model and threshold | Speakers found |
  |---|---|
  | WeSpeaker 0.5 | 13 |
  | WeSpeaker 0.6 | 8 |
  | WeSpeaker 0.7 | 5 (people merged) |
  | 3D-Speaker 0.6 / 0.7 | 63 / 51 (people split up) |

  Use WeSpeaker. Do not use 3D-Speaker.

Items:

- [x] Research first (subagent, cheap model, report of 15 lines or less):
  - the sherpa-onnx package for .NET: name, version, license, size of the
    native DLL for win-x64, and its offline speaker-diarization API;
  - the licenses of the two models;
  - whether `transcribe-cli` gives word or token times in Amanu's
    `transcript.json`.
  Write the answers here before any code.

  Answers (2026-11-09):
  - Package: `org.k2fsa.sherpa.onnx` 2.0.1 (Apache-2.0), with
    `org.k2fsa.sherpa.onnx.runtime.win-x64` 2.0.1: `onnxruntime.dll` 17.8 MB and
    `sherpa-onnx-c-api.dll` 4.3 MB (about 22 MB more in the publish folder).
  - API (checked in `scripts/dotnet/` of the sherpa-onnx repo):
    `OfflineSpeakerDiarizationConfig { Segmentation.Pyannote.Model,
    Embedding.Model, Clustering.NumClusters (-1 = by threshold),
    Clustering.Threshold, MinDurationOn, MinDurationOff }`;
    `new OfflineSpeakerDiarization(config)`, `.SampleRate` (16 kHz),
    `.ProcessWithCallback(float[] samples, callback, IntPtr)` returns
    `OfflineSpeakerDiarizationSegment[]` with `Start`, `End` (seconds, float)
    and `Speaker` (int). A higher threshold gives fewer speakers.
  - Models, one file each (the same files as `amanu-gemini\models`, same hash):
    | Model | URL | Size | SHA-256 | License |
    |---|---|---|---|---|
    | pyannote segmentation 3.0 | `https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx` | 5 992 913 | `220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079` | MIT |
    | WeSpeaker ResNet34-LM | `https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx` | 26 530 550 | `e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012` | Apache-2.0 |
    The int8 segmentation model (1.5 MB) was not tuned in amanu-gemini, so it
    is not used.
  - `amanu-gemini\diarize.py` uses: threshold 0.7, clusters -1,
    `MinDurationOn` 0.3, `MinDurationOff` 0.5, threads = CPU count, the right
    (call) channel at 16 kHz mono.
  - Word times: `transcribe-cli` gives them and Amanu parses them
    (`LocalCliWords.Parse`, `TranscriptionServices.cs` about line 355), but
    `TranscriptSegment` keeps only `start_ms` and `end_ms`. So
    `transcript.json` has no word times. Decision: a line gets the voice with
    the most time; splitting lines by word time is left for later.
- [x] Run it after the speech recognition, on the call track only, on the CPU,
  at BelowNormal priority (the same as the speech recognition).
  Done in `Amanu.App/CallDiarizer.cs`: a child process, `Amanu.exe --diarize
  track seg emb threshold threads`, at BelowNormal, with half the cores. A
  child process is needed because onnxruntime makes its own threads, and only
  a process priority class reaches them. It holds the same CPU gate as the
  speech recognition (`CpuTranscription.EnterBatchAsync`) and starts over when
  live transcription takes the CPU back. It is called from
  `ProcessingCoordinator.DiarizeCallAsync`, after the echo filter. If it fails,
  the reason goes to `transcribe.log` and the transcript stays as it was.
- [x] The models are downloaded by `ModelManager.cs`, with a SHA-256 check, as
  `parakeet-ultra` is. Folder: `models/diarization/`. Pinned in
  `Amanu.Core/Processing/CallDiarization.cs` (`DiarizationModels`).
- [x] Give each `them` line the speaker that overlaps it most in time:
  `them A`, `them B` (`SpeakerLabels.cs`). When a line holds two speakers, split
  it by word times if there are any. If not, keep the speaker with the most time.
  Done in `CallDiarization.Apply`. There are no word times (see above), so a
  line keeps the voice with the most time. A line in a gap takes the nearest
  voice within 2 s; else the voice of the call line before it. One voice stays
  a bare `them`.
- [x] Only for local engines. Gemini gives its own labels. A transcript whose
  call side already has more than one label is left alone.
- [x] Settings: `transcription.diarization` (`auto` / `off`) and
  `transcription.diarization_threshold` (the default comes from the tuning).
  The threshold accepts a comma or a point as the decimal mark.
- [x] Write the time it took into `transcribe.log`, and the speaker count into
  `processing.json`. The log line is `diarization`, with "N speakers, threshold
  X"; the count is `call_speakers`.
- [x] Tests for the line-to-speaker logic, with a fixed fake diarization result
  (no model in the test). `CallDiarizationTests.cs`, 8 tests.
- [x] Tune the threshold with a script that prints only the speaker count and
  how well the labels match the Gemini labels of `2026.10.29-1000`.

  Tuning (2026-11-09, 8 threads, call channel cut from `audio.m4a`):

  | Recording | Threshold | All voices | ≥ 10 s | ≥ 30 s | Seconds |
  |---|---|---|---|---|---|
  | `2026.10.29-1000` (about 13 people) | 0.5 | 12 | 7 | 5 | 141 |
  | | 0.6 | 8 | 5 | 2 | 149 |
  | | 0.7 | 5 | 3 | 1 | 148 |
  | | 0.8 | 3 | 3 | 1 | 164 |
  | `2026.11.06-1021` (1 person) | 0.5 | 3 | 1 | 1 | 138 |
  | | 0.6 | 2 | 1 | 1 | 136 |
  | | 0.7 / 0.8 | 1 | 1 | 1 | 141 / 125 |
  | `2026.11.06-1438` | 0.5 / 0.6 / 0.7 / 0.8 | 24 / 14 / 9 / 5 | 11 / 8 / 4 / 4 | 5 / 5 / 3 / 3 | 228–282 |
  | `2026.11.09-1001` | 0.5 / 0.6 / 0.7 / 0.8 | 31 / 20 / 14 / 8 | 10 / 7 / 7 / 4 | 5 / 5 / 5 / 4 | 116–159 |

  Speech per voice, in seconds:
  - `1021` at 0.5: 574, 7, 5. At 0.4: 572, 6, 5, 2, 0, 0.
  - `1000` at 0.5: 278, 238, 68, 56, 33, 26, 10, 3, 2, 1, 1, 1.
  - `1000` at 0.4: 272, 181, 64, 55, 41, 34, 20, 15, 10, 7, 7, 4, 2, 2, 2, 2, 1, 1, 1.

  Every threshold makes small "voices" of coughs and line noise. The match
  against the transcript labels says little: the `1000` transcript has 25 call
  labels for about 13 people, and the other three have only one. It takes 2–5
  minutes per call on this PC.

  First decision, 0.4 and 7 s, failed the check by hand: the app gave
  `1021` 3 speakers (Andrea split in two) and `1001` 13. Cause: the tuning cut
  the call channel with ffmpeg, but the app decodes `audio.m4a` with Windows
  Media Foundation (NAudio `AudioFileReader`, `AudioPreprocessor.SplitStereo`).
  The sound is almost the same, but the clustering flips on it. Measured again
  with the app's own decoder and the published `Amanu.exe --diarize`:

  | Recording | 0.4 | 0.5 | 0.6 | 0.7 |
  |---|---|---|---|---|
  | `1021` (1 person), speech per voice in s | 414, 151, 8, 6, … | 424, 157, 3, … | 580, 5 | 580, 5 |
  | `1000` (about 13 people) | | | 265, 189, 110, 65, 49, 38, 2, 2, 1 | |
  | `1001` | | | 180, 88, 86, 48, 48, 18, 14, 10, 7, 6, 5, … | |
  | `1438` | | | 619, 204, 113, 94, 17, 10, 6, 6, 6, … | |

  Decision (chosen with the user): threshold **0.6** and minimum **10 s**
  (`CallDiarization.MinimumVoiceMs`). This gives 1 voice on `1021`, 6 on
  `1000`, 8 on `1001` and 6 on `1438`. Large calls get too few voices: quiet
  people merge. This is accepted, because the other way is worse: below 0.6 a
  one-person call often shows 2 or 3 people. Step 5 joins labels that are one
  person. From now on, tune with the app's decoder, not ffmpeg.

Done when: `2026.11.06-1021` gives 1 speaker on the call side;
`2026.10.29-1000` gives a count within 30 % of the real one; and the time is
less than the speech-recognition time of the same recording.

Result: the one speaker on `1021` is met at 0.6 (measured with the app's
decoder; the check by hand is open). The count on `1000` is not met (6 of
about 13): the user chose a stable one-person result over the count, and step
5 makes up part of it. The time is not met: on `1021` the app took 117 s for
speech recognition (on the GPU) and 126 s for diarization (on the CPU).

Check by hand at 0.6 (2026-11-09): `1021` gave only Andrea, correct; `1001`
gave 8, which looks right; but a new call, `2026.11.09-1502`, gave 14 speakers
for 5 people. The user's decision: diarization brings no benefit for now and
only delays the summary, so `transcription.diarization` is now `off` by
default. The code, the models and the setting stay; a user can set `auto` in
Advanced settings. Pick this up again only with a faster and more reliable
method (for example a better voice model, or the summary model joining labels
as in step 5), and tune it with the app's own decoder.

## Step 5: stricter speaker-name checks (size: M, session A)

Port the checks from `amanu_gemini.py` (`proven`, `which_one`, `called_before`,
`sounds_like`, about lines 635–830) to C#, with tests. They also protect
against diarization errors: a label that merged two people gets no name.

- [ ] A name is proof only next to that speaker (in the 4 lines before their turn).
- [ ] A misheard name still counts ("Mišiak" → Michal).
- [ ] Two people with the same first name: a word that only one of them has
  (such as the surname) must be said, or the label stays without a name.
- [ ] One label with several different names called before its turns holds
  several people: give it no name.
- [ ] Ask the model two times. Keep a name only when both answers agree.
- [ ] Names that I typed by hand ("manual") are never replaced.
- [ ] Join labels that are one person. Diarization can split one person into
  two labels (step 4 saw this on `2026.11.06-1021`). When the checks above give
  two `them` labels the same proven name, they become one label in
  `transcript.json`, and `processing.json` `call_speakers` goes down. Also ask
  the model, from the content and the names that people say, which call labels
  are the same person; join only labels it is sure about, and only when no
  check above speaks against it. Tests with a fake model answer.

Done when: on `2026.10.29-1000` at least 12 of 25 labels get a name, all names
are correct (the same as the amanu-gemini result), and on a diarized Parakeet
recording no label gets a wrong name.

## What KInIT reports (research, 2026-11-09)

WER in %, from the KInIT model cards. CV24 = Common Voice 24 test (public).
Internal = KInIT's own set (not public, 2/3 with added noise). The base model
is in brackets. The cards do not say how the text is normalized.

| Model | CV24 | Internal |
|---|---|---|
| `kinit/parakeet-tdt-0.6b-v3-sk` (our `parakeet-sk`) | 7.92 [9.07] | 6.62 [24.32] |
| `kinit/whisper-large-v3-turbo-sk` | 9.29 [29.23] | 5.60 [18.35] |
| `kinit/whisper-large-v3-sk` (not turbo, 1.5B) | 7.68 [19.70] | 5.67 [16.22] |
| `kinit/canary-1b-v2-sk` | 8.27 [14.00] | 6.37 [14.39] |

Quantization: KInIT publishes only full-size weights and no quantized WER.
Nobody reports Slovak WER for quantized versions of these models. For the base
models: Whisper large-v3-turbo on English LibriSpeech is F16 2.01, Q8_0 2.01,
Q5_K_M 2.03, Q4_K_M 2.04 (handy-computer GGUF card); base Parakeet v3 on 24
languages is Q8_0 13.31 against F32 13.38 (audio.cpp). Nothing for Canary.
So Q8_0 is expected to lose almost nothing; our own runs must confirm it.

## Step 6: Gemini — measure only (size: M, session C)

Decision (2026-11-09): measure first, wire later. Do not add an engine, a
setting or a download to the app in this step. After the numbers are here, I
decide if Gemini becomes a transcription engine and a summary engine. That
decision is open.

- [x] Ask me before any call that uses a cloud key, and say what is sent.
- [x] Run Gemini outside the app (a script). Send each track (`me`, `them`)
  alone, as the app would, in chunks with a small overlap.
- [x] Measure on the speed set against `parakeet-sk`, with the same audio path
  as the app (Media Foundation decode of `audio.m4a`, then
  `AudioPreprocessor.SplitStereo`: mic left, call right):
  - WER on the quiet speaker (`2026.11.06-1021`) and on `2026.11.06-1000`.
  - Time for each recording.
  - How many labels get a correct name, where a reference has names.
- [x] If cheap: one summary from Gemini against one from the current summary
  engine, on the same transcript. I judge them; write only which one I chose.
- [x] Write the result table here. Status: "Measured; waiting for decision".

Result (2026-11-09): **about as accurate as `parakeet-sk`, and much slower.
Decision open: I decide if Gemini becomes a transcription engine and a summary
engine.**

| Engine | WER 1021 (me, them) | WER 1000 (me, them) | Audio per wall second | Echo lines |
|---|---|---|---|---|
| `parakeet-sk` Q8_0, Vulkan, local | 29.6 % (47.9, 16.5) | 42.9 % (77.5, 26.1) | 13 (1021), 13 (1000) | — |
| `gemini-3.8-flash`, free API key | **28.9 %** (49.0, 14.7) | **39.1 %** (73.8, 22.2) | 1.9 (1021), 0.8 (1000) | 0 / 1 |

How it was measured:

- Audio: the same echo-cleaned chunks as in steps 7 and 8, the first 600 s of
  each track. Each track went alone, in two pieces of about 5 minutes (no
  overlap), with the custom words as spelling hints, temperature 0, JSON out.
  Same per-side scorer.
- **The references favour Gemini:** they are silver transcripts made by
  `gemini-3.5-flash-lite`. A lead of 1-4 points is not proof that Gemini is
  better.
- Speed: one request took 26-372 s. The wall time counts only the successful
  requests, not the 15 s pauses (free-tier rate limit) or the retries.
  `gemini-3.8-flash` gave HTTP 503 on 4 of 8 pieces; all but one recovered on
  a retry, and one went to the fallback `gemini-3.7-flash` (the answer still
  said `gemini-3.8-flash`). No 429, no failed piece. A paid key with requests
  in parallel would be faster; this was not measured.
- Echo lines: almost none, because the audio was echo-cleaned first.
- Summary: one summary of `2026.11.09-1001` with `gemini-3.8-flash` and the
  app's own `summary.template` (without the people list), next to the app's
  current summary. Which one I chose: _open_.
- The free API tier lets Google use what is sent. Recordings sent: the first
  10 minutes of both tracks of `2026.11.06-1021` and `2026.11.06-1000`, and the
  transcript of `2026.11.09-1001`.

Decision (2026-11-09): **yes, Gemini becomes an engine, as an option that is
off by default.** `parakeet-sk` stays the default. The wiring is step 6b.

## Step 6b: Gemini as an optional engine (size: L, session C)

Facts from step 6 that the wiring needs:

- [ ] Engine `gemini`, off by default, with the key in the Windows Credential
  Manager (`Amanu/…`). The key file that amanu-gemini uses can be imported.
- [ ] Send each track alone, after the app's own echo removal (LocalVQE). In
  step 6, pieces of about 5 minutes worked; times come back as `mm:ss` from the
  start of each piece and are mapped back to the track.
- [ ] Model `gemini-3.8-flash`, 3 tries with backoff on 429 and 503, then the
  fallback `gemini-3.7-flash`. HTTP 503 came often on the free key. Write the
  model that really ran into `transcribe.log` and `transcript.md`.
- [ ] Prompt: Slovak verbatim, no translation, nothing for silence, custom words
  and the people list as spelling hints, temperature 0, JSON out.
- [ ] Remove echo lines (lines on `me` that repeat `them` at the same time) as
  a safety net; with LocalVQE first, step 6 found 0-1 per recording.
- [ ] A request can take up to about 6 minutes. Do not time out sooner, and
  show progress.
- [ ] Measure once in the app against the step 6 numbers (28.9 % / 39.1 %).

## Step 7: Slovak Whisper — measure only (size: M, session C)

Decision (2026-11-09): measure first, wire later. Do not add an engine, a
setting or a download to the app until the numbers show that `whisper-sk` is
as good as `parakeet-sk` or better. Then stop and ask me.

Baseline: `parakeet-sk` (kinit/parakeet-tdt-0.6b-v3-sk, converted to GGUF by
hand). It has 0.0 % WER on the quiet speaker in `2026.11.06-1021`, and 42.1 %
against the silver reference on `2026.11.06-1000`. The local runtime is
transcribe.cpp v0.1.3 (`transcribe-cli.exe`, ggml, Vulkan).

Model: kinit/whisper-large-v3-turbo-sk (the KInIT fine-tune of
whisper-large-v3-turbo, about 809M parameters, MIT).

- [x] Ask me before the download, and say its size.
- [x] Run it outside the app: a ggml/GGUF conversion that the existing
  `transcribe-cli` loads (Q8_0, as `parakeet-sk`), or a Python runtime if the
  conversion cannot run on this PC. Language: Slovak.
- [x] Measure against `parakeet-sk`, with the app's audio path:
  - WER on the quiet speaker (`2026.11.06-1021`) and on `2026.11.06-1000`.
  - ASR time on the CPU and on the GPU (if the runtime has a GPU path).
  - Hallucinations: repeated or invented lines in silence.
- [x] Write the result table here. If it is as good or better: Status "Measured;
  waiting for decision", and ask me whether to wire it. If not: Status
  "Measured; not better", and stop.

Result (2026-11-09): **not better.** `whisper-sk` has a higher WER than
`parakeet-sk` on both recordings, and it is about 4 times slower. It stays out
of the app. The CPU run was skipped (my decision): the GPU is the fast path.

| Model | Backend | WER 1021 | WER 1000 | ASR s 1021 | ASR s 1000 | Lines in near-silent chunks (1021 / 1000) |
|---|---|---|---|---|---|---|
| `parakeet-sk` Q8_0 | Vulkan | **29.6 %** | **42.9 %** | 137 | 115 | 19 / 104 |
| `whisper-sk` Q8_0 | Vulkan | 35.3 % | 44.8 % | 580 | 467 | 5 / 21 |
| `whisper-sk` Q5_K_M | Vulkan | 35.3 % | 45.0 % | 624 | 522 | 3 / 20 |
| `whisper-sk` Q8_0 + custom words (`--initial-prompt`) | Vulkan | 33.8 % | 45.2 % | 611 | 475 | 2 / 21 |

How it was measured:

- Audio: the app's own path. A small .NET program referenced the published
  `Amanu.dll` and ran Media Foundation decode, `SplitStereo`, LocalVQE echo
  removal (`OfflineEchoAudio.Prepare`) and the 60 s chunks with 1 s context.
  Both models got the same chunk WAVs. Without the echo removal, Parakeet gave
  68 %: call audio on the mic track counts as errors.
- WER: the reference is the silver reference (Gemini, window 120-300 s, not
  hand-checked). Each side (`me`, `them`) is aligned alone, against the best
  matching run of words in the first 600 s. This removes two errors of the
  app's scorer: it cuts by segment start, and Whisper's segments are about 60 s
  long, so the cut at the window edge cost Whisper about 20 points (71 % with
  the app's scorer); and Whisper's long segments mix the order of `me` and
  `them`. With the app's scorer, Parakeet gives 30.8 % and 42.1 %, the same as
  the recordings' own transcripts.
- The 0.0 % for the quiet speaker above comes from an older state. Today's
  `parakeet-sk` transcript of `1021` scores 30.8 % with the app's scorer.
- The `me` side has a high WER for every model (48-79 %). The silver
  reference is probably weak there; it does not change the order of the models.
- Q5_K_M loses nothing against Q8_0 here, and it is not faster on this GPU.
- Whisper writes much less text in near-silent chunks. A repeated line (3 or
  more times in a row) did not come up in any run.
- Conversion: `convert-whisper.py` needs `--repo-id openai/whisper-large-v3-turbo`
  for the KInIT repo, and `safetensors`, `huggingface_hub` and `transformers`
  in the venv. The model files and scripts were in the session scratchpad only.

Research (2026-11-09):

- Size: `model.safetensors` is 3.24 GB (F32). KInIT reports 9.29 % WER on
  Common Voice 24 (base model 29.23 %) and 5.60 % on its own set.
- Route on this PC: `C:\Backup\CoDiNG\transcribe.cpp\scripts\convert-whisper.py`
  (the transcribe.cpp v0.1.3 checkout), then `build-qin	ranscribe-quantize.exe`
  to Q8_0 (about 0.9 GB). The existing `transcribe-cli.exe` runs Whisper with
  `--initial-prompt`, on the CPU and on Vulkan. Python: the venv in
  `C:\Backup\CoDiNG\Amanu\sk-convert\.venv` has torch, gguf and numpy; it
  needs `safetensors`.
- Disk: C: has 8.6 GB free. Download plus F32 GGUF needs about 6.5 GB at the
  peak, if each file is deleted after its use.

Only if I say yes later: engine `whisper-sk` (names in `AppSettings.cs`
`LocalEngines`, `AnalyticsPolicy.cs`, `ProcessingPolicy.cs`, `SetupForm.cs`,
`ProcessingCoordinator.cs`), custom words in the initial prompt, and the
convert steps in `windows/BUILDING-HERE.md`.

## Step 8: Slovak Canary — research, then measure (size: L, session C)

Decision (2026-11-09): research first, then measure, wire later. Do not add an
engine, a setting or a download to the app.

Model: kinit/canary-1b-v2-sk (the KInIT fine-tune of nvidia/canary-1b-v2, about
978M parameters, CC-BY-4.0, NeMo). transcribe.cpp v0.1.3 in Amanu cannot run
Canary. `amanu_local.py` in amanu-gemini ran a Canary model through a
`transcribe-cli`, so check that first.

- [x] Research (subagent, cheap model, report of 15 lines or less): does a
  Slovak Canary exist, and does it run on this PC? Check `transcribe-cli` (the
  version that `amanu_local.py` used, or a newer one), sherpa-onnx (the library
  of step 4), an ONNX export, and a Python runtime. Write down CPU or Vulkan,
  and the size.
Research (2026-11-09):

- It exists: one `canary-1b-v2-sk.nemo`, 6.36 GB. KInIT reports 8.27 % WER on
  Common Voice 24 (base 14.00 %) and 6.37 % on its own set, where it lists
  `parakeet-sk` at 6.62 %. Long audio was not tested by KInIT.
- It can run on this PC without NeMo: transcribe-cli v0.1.3 already runs
  Canary (`--pnc`), and `amanu_local.py` ran the base model
  `handy-computer/canary-1b-v2-gguf` (Q8_0) with it. The Slovak fine-tune
  needs `C:\Backup\CoDiNG\transcribe.cpp\scripts\convert-canary.py`
  (variant `canary-1b-v2`, reads the `.nemo` directly), then Q8_0 (about 1.1 GB).
  Canary takes at most 400 s per call and gives no time stamps, so the audio
  goes in parts, as `amanu_local.py` does.
- Disk: the `.nemo` plus the F32 GGUF need about 10.5 GB. C: has 8.6 GB
  free, so this does not fit now.

- [x] If one runs: ask me before the download, then measure it as in step 7.
- [x] Write the result table here. If it is as good as `parakeet-sk` or better,
  ask me whether to wire it.

Result (2026-11-09): **not better.** `canary-sk` has a clearly higher WER than
`parakeet-sk` on both recordings, it is a little slower, and it writes a line
in nearly every near-silent chunk. It stays out of the app.

| Model (Vulkan) | WER 1021 (me, them) | WER 1000 (me, them) | ASR s 1021 | ASR s 1000 | Near-silent chunks with text | Loops |
|---|---|---|---|---|---|---|
| `parakeet-sk` Q8_0 | **29.6 %** (47.9, 16.5) | **42.9 %** (77.5, 26.1) | 137 | 115 | 19 lines (1021), 104 lines (1000) | 4 / 1 |
| `canary-sk` Q8_0 (1.14 GB) | 39.6 % (55.2, 28.6) | 48.1 % (75.6, 34.7) | 161 | 148 | 8 of 8 (1021), 13 of 15 (1000) | 5 / 3 |

How it was measured: the same echo-cleaned 60 s chunk WAVs as in step 7, one
`transcribe-cli` batch per recording, one segment per chunk (Canary gives no
time stamps), and the same per-side scorer.

- Conversion: `convert-canary.py` needs NeMo. A shim that reads the `.nemo`
  tar directly and feeds the converter worked at once: F32 GGUF 3.74 GB
  (1476 tensors), then Q8_0.
- In `1000`, 2 of 30 chunks failed every time with "output truncated: decode
  hit the context/generation cap", and `transcribe-cli` exited with code 1. Both
  are outside the scored window, so they do not change the WER above. In the
  app this would need shorter chunks.
- The base model (`canary-1b-v2`) was not run: its GGUF is not on this PC
  (only an empty lock file in the Hugging Face cache).

## Step 9: a Settings screen for more engines (size: M, session D)

Steps 2, 3, 4, 6, 7 and 8 add settings. Today the engine list in Settings is
one flat list: `auto`, the cloud engines, then the local engines
(`SettingsSchema.cs`, `transcription.engine`). With `gemini`, `whisper-sk` and
`canary-sk` it gets too long to read, and it does not say which engine is
ready to use.

- [ ] Put the engines in two groups: "On this computer" and "In the cloud".
- [ ] For each engine, show one line: its language, its size, and whether it
  is ready (model downloaded, key saved). An engine that is not ready says what
  to do, with a button (download, add key).
- [ ] Show the measured WER and speed next to each Slovak engine, from the
  speed set. Show them only after a measurement.
- [ ] Put the new settings in the section where they belong:
  - Gemini key and model: with the cloud engines.
  - Diarization and its threshold: with the local engines.
  - People list (`speaker_names.people_file`): in "Naming".
  - Summary card (`slack_summary.*`): in the "Summary" section.
- [ ] Check the window by hand in light and dark mode, at 100 % and 150 %
  display scale. No text may be cut off.

Done when: I can pick any engine in one look, and I can see why an engine
cannot be used.

## Step 10: summaries on demand, several per recording (size: M, session B)

Why: a summary is made once, after the transcript. I want to make a new one
when I change the template, the engine or the people list, and keep the old
ones to compare, as the Recordings window already does for transcripts.

Known facts (from the code on 2026-11-09):

- Transcripts already have versions: `TranscriptVersions.cs` (Core) keeps the
  earlier ones, `transcribe.requested` asks for a new one, and
  `RecordingsWindow.cs` has a "Transcribe again ▾" menu (`OpenRetranscribeMenu`,
  about line 429) and one tab per version.
- `SummaryService.GenerateAsync` (`LanguageServices.cs`) writes one
  `summary.md` and deletes `summary.stale`. `ProcessingCoordinator.cs` calls it
  at about line 916. `summary.off` turns a summary off for one recording.

Items:

- [x] A "Summarize again ▾" button beside "Transcribe again ▾". The menu lists
  the summary engines that can run now. It works on the transcript version that
  is open in the window.
- [x] Keep each summary. Copy the `TranscriptVersions` pattern: archive the
  current `summary.md` before a new one replaces it, and write the engine, the
  model, the time and the transcript version it came from.
- [x] Show one tab per summary, newest first, as the transcript tabs do.
- [x] A request file (like `transcribe.requested`), so a summary asked for
  while the app is busy runs in the next processing pass, and an app restart
  does not lose it.
- [x] Step 3's card shows the newest summary only.
- [x] Tests for the archive and the request file, with no model (a fake
  `LanguageModels`).

Facts (session B, 2026-11-09):

- Core, `Processing/SummaryVersions.cs`. Before a new `summary.md` replaces the
  current one, `SummaryService.GenerateAsync` archives it to
  `summaries/<first 16 hex of its SHA-256>/` and writes `summary.json`
  (`backend`, `model`, `created_at`, `transcript` = SHA-256 of
  `transcript.json`, the name the transcript archive uses). A summary from
  before this change is archived with `backend` empty (shown as "?").
- A stale summary is not archived again: it is already in its transcript's
  archive. `TranscriptVersions.ArchiveCurrentAsync` now also copies
  `summary.json` and `summaries/`, so each transcript version keeps its own
  summaries. `SummaryVersions.ReadAsync` shows only the summaries whose
  `transcript` matches the folder's `transcript.json`.
- The request file is `summarize.requested` in the recording folder: a JSON
  list of `{backend, transcript}` (`transcript` = archive folder name, or null
  for the current transcript), worked off in order. `NeedsWork` sees it, so a
  restart does not lose it. `ProcessingCoordinator.SummarizeOnRequestAsync`
  runs it after the normal summary. A model that cannot be reached keeps the
  request for the next pass; a permanent failure drops it and shows the error.
- The menu uses `LanguageModels.AvailableForSummary()`: what `auto` would walk,
  whatever `summary.backend` says, because the person picks the engine by hand.
  Ollama is always listed ("if it is running").
- A summary made on demand does not raise `SummaryWritten`, so it never brings
  up the Slack card. The card and the "Copy summary" button read `summary.md`;
  in the Recordings window, "Copy summary" copies the summary tab that is open.
- Tests: `SummaryVersionsTests.cs` (Core) and `SummaryOnDemandTests.cs` (Live,
  Ollama's address answered by a fake `HttpMessageHandler`).

Done when: on `2026.11.06-1438` I make two more summaries with two engines, see
three tabs, and the first summary is unchanged on disk.

## The card

### When it comes up

All of these must be true:

1. The recording came from Slack: `process_family` in `meta.json` is on the
   `slack_summary.apps` list.
2. The recording is at least `slack_summary.min_minutes` long (default 3). A
   short call that rang and stopped gives no card.
3. `summary.md` was just made, without errors, and it is not stale.
4. The summary was made on the same day as the recording. A new transcription
   of an old recording gives no card.
5. The card was not shown before for this recording (`slack-offer.json`).
6. Amanu is not recording now. If a new call has started, the card waits until
   that call stops, so it does not come up in the middle of the next huddle.

If the transcription or the summary fails, there is no card. The failure
message that Amanu already has is shown.

### What it does not do

- It does not pick the Slack channel. I pick it in Slack.
- It does not paste or send. I press Ctrl+V, read the text, and press Enter.

### How it looks

A small Amanu window at the bottom right, above the tray. It does not take the
focus, so it does not interrupt typing. It stays until I click on it.

```
┌──────────────────────────────────────────────┐
│ Amanu · Slack huddle summary ready        ×  │
│ Today 10:01–10:26 · 25 min                   │
│ Joe Benes, Michal Polko, me                 │
│ ──────────────────────────────────────────── │
│ Decisions: release moves to Thursday …       │
│ 3 action items                               │
│                                              │
│ [ Copy and open Slack ] [ Open summary ]     │
│ [ Not now ]                                  │
└──────────────────────────────────────────────┘
```

"Not now" and `×` do the same thing: the card closes, and the recording keeps
a "Copy summary" button.

After "Copy and open Slack", the card says: "Copied. In Slack, open the channel
and press Ctrl+V." Then it closes after 10 seconds.
