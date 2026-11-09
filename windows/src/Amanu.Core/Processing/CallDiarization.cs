namespace Amanu.Core.Processing;

/// <summary>
/// The two diarization models, pinned as the speech models are. They are the
/// files amanu-gemini was tuned with (same SHA-256), so its measurements hold.
/// </summary>
public static class DiarizationModels
{
    public static DownloadArtifact Segmentation { get; } = new(
        "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx",
        "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079",
        5_992_913,
        "pyannote-segmentation-3-0.onnx");

    public static DownloadArtifact Embedding { get; } = new(
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx",
        "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012",
        26_530_550,
        "wespeaker_en_voxceleb_resnet34_LM.onnx");
}

/// <summary>One stretch of the call track that diarization gave to one voice, in transcript time.</summary>
public sealed record SpeakerTurn(long StartMs, long EndMs, int Speaker);

/// <summary>
/// Splits the call side of a local transcript by voice. The local engines hear
/// the call track as one speaker, "them"; diarization gives stretches of time to
/// voices, and each "them" line takes the voice it overlaps most. A line in a
/// gap between stretches takes the nearest voice, because the segmentation
/// model drops short pauses that the recognizer keeps inside a line. The new
/// labels go through <see cref="SpeakerLabels.Assign"/>, so one voice stays a
/// bare "them" and several become "them A", "them B" in the order they speak.
/// </summary>
public static class CallDiarization
{
    /// <summary>A line further than this from any stretch keeps no voice of its own: it is not guessed.</summary>
    public const long NearestLimitMs = 2_000;

    /// <summary>
    /// A voice with less speech than this in the whole call is not counted as a
    /// person: on a one-person call the clustering also made "voices" of 2 to 7
    /// seconds out of coughs and line noise, while on a call of 13 people real
    /// quiet speakers had 7 to 15 seconds. The margin is thin, so the threshold
    /// setting is the knob a user turns. Its lines go to the voices around them.
    /// Tuned in the roadmap's step 4.
    /// </summary>
    public const long MinimumVoiceMs = 7_000;

    public sealed record Result(IReadOnlyList<TranscriptSegment> Segments, int Speakers);

    /// <summary>The turns without the voices that have less than <paramref name="minimumMs"/> of speech in all.</summary>
    public static IReadOnlyList<SpeakerTurn> WithoutShortVoices(IReadOnlyList<SpeakerTurn> turns, long minimumMs)
    {
        var total = turns.GroupBy(turn => turn.Speaker).ToDictionary(group => group.Key, group => group.Sum(turn => turn.EndMs - turn.StartMs));
        var kept = turns.Where(turn => total[turn.Speaker] >= minimumMs).ToArray();
        // Never drop every voice: a short call with one quiet voice is still one voice.
        return kept.Length == 0 ? turns : kept;
    }

    /// <summary>
    /// The transcript with "them" lines given to voices. Lines of other labels
    /// are left alone, and so is a transcript whose call side already has more
    /// than one label (an engine that diarizes on its own).
    /// </summary>
    public static Result Apply(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<SpeakerTurn> turns, long minimumVoiceMs = MinimumVoiceMs)
    {
        turns = WithoutShortVoices(turns, minimumVoiceMs);
        if (turns.Count == 0 || !segments.Any(IsCall)) return new(segments, 0);
        var voices = segments.Select(segment => (int?)VoiceFor(segment, turns)).ToArray();
        // A line with no voice near it joins the voice of the call line before
        // it (or after it, at the start), so it is never a speaker of its own.
        int? last = null;
        for (var index = 0; index < segments.Count; index++)
        {
            if (!IsCall(segments[index])) continue;
            if (voices[index] is { } voice) last = voice;
            else voices[index] = last;
        }
        int? next = null;
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            if (!IsCall(segments[index])) continue;
            if (voices[index] is { } voice) next = voice;
            else voices[index] = next;
        }
        var labelled = segments.Select((segment, index) => IsCall(segment) && voices[index] is { } voice
            ? segment with { Speaker = SpeakerLabels.Raw(SpeakerLabels.Them, voice.ToString(System.Globalization.CultureInfo.InvariantCulture)) }
            : segment).ToArray();
        var count = voices.Where((voice, index) => IsCall(segments[index]) && voice is not null).Distinct().Count();
        return new(SpeakerLabels.Assign(labelled), count);
    }

    private static bool IsCall(TranscriptSegment segment) => segment.Speaker == SpeakerLabels.Them;

    /// <summary>The voice with the most time inside the line; else the nearest voice within <see cref="NearestLimitMs"/>.</summary>
    internal static int? VoiceFor(TranscriptSegment segment, IReadOnlyList<SpeakerTurn> turns)
    {
        if (!IsCall(segment)) return null;
        var overlap = new Dictionary<int, long>();
        foreach (var turn in turns)
        {
            var shared = Math.Min(segment.EndMs, turn.EndMs) - Math.Max(segment.StartMs, turn.StartMs);
            if (shared > 0) overlap[turn.Speaker] = overlap.GetValueOrDefault(turn.Speaker) + shared;
        }
        if (overlap.Count > 0)
            return overlap.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
        var nearest = turns
            .Select(turn => (turn.Speaker, Distance: Math.Max(turn.StartMs - segment.EndMs, segment.StartMs - turn.EndMs)))
            .OrderBy(item => item.Distance).First();
        return nearest.Distance <= NearestLimitMs ? nearest.Speaker : null;
    }
}
