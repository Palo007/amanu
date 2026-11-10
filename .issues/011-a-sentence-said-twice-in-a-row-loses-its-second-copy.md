---
title: "A sentence said twice in a row loses its second copy, and very quiet speech is lost whole (Windows)"
date: 2026-11-10
status: open
affects: "the Windows local engine's transcript of the mic track"
---

## What happens

The quality suite (`windows/tests/quality`) found both of these while measuring the
local engine on the FLEURS Slovak test set. In both cases the engine was given the audio
(`audio sent` in `transcribe.log` covers the whole track) and the words did not appear.

1. **Repeats.** FLEURS reads each sentence two or three times in a row, by different
   speakers. With the sentences in that order the word error rate was 28.4 %, nearly all of
   it whole sentences missing; the second or third reading of a sentence was dropped or cut
   short. In a meeting this is someone repeating themselves ("so we ship on Monday — on
   Monday"), or answering "yes" to a "yes". Shuffling the order so no sentence follows
   itself brought the error rate down to 16.8 %. The suite now shuffles, so it no longer
   sees this; reproduce it with the FLEURS rows in their file order.

2. **Quiet speech.** Some FLEURS recordings are about 30 dB quieter than the rest (around
   −52 dBFS against −22). They produced no text at all, about 95 sentences and 1,680 words
   of 15,246, with `transcription.level_normalization` at its default. That is the level of
   a speaker far from a laptop mic, and may be part of why the quiet speaker in the
   2026.11.06-1021 call lost words. The suite now levels every sentence, so it no longer
   sees this either.

## Where to look

- Repeats: wherever the local engine joins overlapping pieces of audio or cleans its
  output — a step that removes text which repeats the text before it would do exactly this.
- Quiet speech: what level normalization does below about −45 dBFS, and whether the
  engine's own voice-activity stage drops a quiet piece before normalization can help.

## Done when

A suite run with the FLEURS rows in file order and without per-sentence levelling scores
within a point or two of the shuffled, levelled run.
