using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amanu.Core.Processing;

/// <summary>What one message from a Gemini Live session says, as far as captions care.</summary>
public sealed record GeminiLiveEvent(bool SetupComplete = false, string? Interim = null, string? Final = null, bool GoAway = false);

/// <summary>
/// The JSON of a Gemini Live transcription session (BidiGenerateContent over a
/// WebSocket). The live model answers with text as the audio streams in, so it
/// can caption a meeting, but it does not tell speakers apart; the track the
/// audio came from still says whether it was "me" or "them".
/// </summary>
public static class GeminiLive
{
    public const string DefaultModel = "gemini-3.5-transcribe-live";
    public const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    /// <summary>
    /// One session may stream for ten minutes; a new one is opened before that
    /// so the server never cuts a sentence off.
    /// </summary>
    public static readonly TimeSpan SessionLength = TimeSpan.FromMinutes(9);

    /// <summary>The service asks for audio in parts of about 100 ms.</summary>
    public const int ChunkSamples = 1600;

    // The service takes up to 1000 words but works best with about 100.
    private const int MaxVocabulary = 100;

    public static string SetupMessage(string model, IReadOnlyList<string> languages, IReadOnlyList<string>? vocabulary)
    {
        var transcription = new JsonObject();
        if (languages.Count > 0) transcription["languageCodes"] = new JsonArray([.. languages.Select(code => (JsonNode?)JsonValue.Create(code))]);
        var words = vocabulary?.Where(word => !string.IsNullOrWhiteSpace(word)).Take(MaxVocabulary).ToArray() ?? [];
        if (words.Length > 0) transcription["customVocabulary"] = new JsonArray([.. words.Select(word => (JsonNode?)JsonValue.Create(word))]);
        var setup = new JsonObject
        {
            ["model"] = model.StartsWith("models/", StringComparison.Ordinal) ? model : "models/" + model,
            ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("TEXT") },
            ["inputAudioTranscription"] = transcription,
        };
        return new JsonObject { ["setup"] = setup }.ToJsonString();
    }

    /// <summary>16 kHz float samples as one realtime message of 16-bit little-endian PCM.</summary>
    public static string AudioMessage(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue));
        return new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = "audio/pcm;rate=16000" },
            },
        }.ToJsonString();
    }

    public static string EndMessage() => """{"realtimeInput":{"audioStreamEnd":true}}""";

    /// <summary>Reads one server message; anything it does not understand reads as an empty event.</summary>
    public static GeminiLiveEvent Read(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            if (root.TryGetProperty("setupComplete", out _)) return new(SetupComplete: true);
            if (root.TryGetProperty("goAway", out _)) return new(GoAway: true);
            if (!root.TryGetProperty("serverContent", out var content) || content.ValueKind != JsonValueKind.Object) return new();
            return new(Interim: Text(content, "interimInputTranscription"), Final: Text(content, "inputTranscription"));
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string? Text(JsonElement content, string name) =>
        content.TryGetProperty(name, out var transcription) && transcription.ValueKind == JsonValueKind.Object
        && transcription.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
}

/// <summary>
/// The caption for one utterance, built from the interim guesses and the final
/// text that arrive while the audio streams. The live coordinator decides where
/// an utterance ends; the service decides when its words are final, so the two
/// do not line up and this class keeps a word from being shown twice.
/// </summary>
public sealed class GeminiLiveCaption
{
    private readonly object sync = new();
    private string committed = "";
    private string interim = "";
    private bool dropNextFinal;

    public string Text
    {
        get { lock (sync) return Join(committed, interim); }
    }

    /// <summary>True while the service still holds words it has not made final.</summary>
    public bool Pending
    {
        get { lock (sync) return interim.Length > 0; }
    }

    public void Apply(GeminiLiveEvent message)
    {
        lock (sync)
        {
            if (message.Final is { } final)
            {
                if (dropNextFinal) dropNextFinal = false;
                else committed = Join(committed, final.Trim());
                interim = "";
            }
            else if (message.Interim is { } guess)
            {
                interim = guess.Trim();
            }
        }
    }

    /// <summary>
    /// Starts the next utterance. Words still in an interim guess were shown in
    /// the utterance that just closed, so their final text is not added again.
    /// </summary>
    public void Begin()
    {
        lock (sync)
        {
            dropNextFinal = interim.Length > 0;
            committed = "";
            interim = "";
        }
    }

    private static string Join(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : first + " " + second;
}
