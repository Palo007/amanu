using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amanu.Core.Configuration;

/// <summary>
/// Everything in <c>config.json</c>. Key names and meanings follow the macOS
/// app's config reference wherever the two platforms do the same thing, so one
/// README describes both; the Windows-only settings (the tray and taskbar icons,
/// the hook as an executable plus arguments) are named for what they are here.
/// </summary>
public sealed class AppSettings
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("recordings_dir")]
    public string RecordingsDirectory { get; set; } = string.Empty;

    [JsonPropertyName("keep_audio")]
    public bool KeepAudio { get; set; }

    [JsonPropertyName("analytics")]
    public bool Analytics { get; set; } = true;

    [JsonPropertyName("interface_language")]
    public string InterfaceLanguage { get; set; } = "auto";

    [JsonPropertyName("user_name")]
    public string? UserName { get; set; }

    /// <summary><c>app</c>: only the call app's process tree. <c>all</c>: everything Windows plays.</summary>
    [JsonPropertyName("system_audio")]
    public string SystemAudio { get; set; } = "app";

    [JsonPropertyName("transcript_echo_filter")]
    public bool TranscriptEchoFilter { get; set; } = true;

    /// <summary>Removes acoustic echo from a copy of the mic with LocalVQE before transcription; the recording is untouched.</summary>
    [JsonPropertyName("offline_echo_cancellation")]
    public bool OfflineEchoCancellation { get; set; } = true;

    /// <summary>
    /// <c>sonora</c>: WebRTC's AEC3 echo canceller, the default and by far the fastest.
    /// <c>fast</c>: LocalVQE's adaptive filter alone. <c>thorough</c>: the filter and a
    /// neural mask, for rooms whose echo is heavily reverberant.
    /// </summary>
    [JsonPropertyName("echo_model")]
    public string EchoModel { get; set; } = "sonora";

    public static readonly IReadOnlyList<string> EchoModels = ["sonora", "fast", "thorough"];

    /// <summary>
    /// Keeps the echo-cleaned mic and its aligned call track in the session, so
    /// "Transcribe again" hears the same cleaned audio instead of running
    /// LocalVQE once more. Echo removal does not depend on the engine, the
    /// language or the custom words, so nothing a re-transcription changes
    /// would make a fresh copy differ.
    /// </summary>
    [JsonPropertyName("keep_echo_cleaned_audio")]
    public bool KeepEchoCleanedAudio { get; set; } = true;

    [JsonPropertyName("auto_record")]
    public AutoRecordSettings AutoRecord { get; set; } = new();

    [JsonPropertyName("transcription")]
    public TranscriptionSettings Transcription { get; set; } = new();

    [JsonPropertyName("live_transcription")]
    public LiveTranscriptionSettings LiveTranscription { get; set; } = new();

    [JsonPropertyName("summary")]
    public SummarySettings Summary { get; set; } = new();

    [JsonPropertyName("speaker_names")]
    public SpeakerNameSettings SpeakerNames { get; set; } = new();

    [JsonPropertyName("slack_summary")]
    public SlackSummarySettings SlackSummary { get; set; } = new();

    [JsonPropertyName("start_at_login")]
    public bool StartAtLogin { get; set; } = true;

    [JsonPropertyName("tray_icon")]
    public bool TrayIcon { get; set; } = true;

    [JsonPropertyName("taskbar_icon")]
    public bool TaskbarIcon { get; set; } = true;

    [JsonPropertyName("on_stop")]
    public CommandHook? OnStop { get; set; }

    /// <summary>Everything the file held that no property above reads — kept so a save never drops it.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// In the home folder rather than in Documents: on most Windows 11 machines
    /// OneDrive backs Documents up, and recordings of meetings should not start
    /// travelling to a cloud nobody chose for them.
    /// </summary>
    public static string DefaultRecordingsDirectory(string homeDirectory) =>
        Path.Combine(homeDirectory, "Amanu Recordings");

    public static AppSettings CreateDefault(string homeDirectory) => new()
    {
        RecordingsDirectory = DefaultRecordingsDirectory(homeDirectory),
    };

    /// <summary>
    /// What Amanu runs on when the config file exists and has never been
    /// readable in this process: nothing starts by itself and nothing leaves the
    /// machine, because the file may well say so and nobody can tell.
    /// </summary>
    public static AppSettings CreateConservative(string homeDirectory)
    {
        var settings = CreateDefault(homeDirectory);
        settings.AutoRecord.Enabled = false;
        settings.Analytics = false;
        settings.Transcription.Engine = "local";
        settings.Summary.Backend = "none";
        settings.SpeakerNames.Backend = "none";
        settings.LiveTranscription.Enabled = false;
        settings.OnStop = null;
        return settings;
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}

public sealed class AutoRecordSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("mic_activity")]
    public bool MicrophoneActivity { get; set; } = true;

    [JsonPropertyName("start_delay_seconds")]
    public int StartDelaySeconds { get; set; } = 3;

    [JsonPropertyName("stop_delay_seconds")]
    public int StopDelaySeconds { get; set; } = 15;

    [JsonPropertyName("min_duration_seconds")]
    public int MinimumDurationSeconds { get; set; } = 45;

    [JsonPropertyName("silence_stop_minutes")]
    public int SilenceStopMinutes { get; set; } = 10;

    [JsonPropertyName("max_duration_minutes")]
    public int MaximumDurationMinutes { get; set; } = 300;

    [JsonPropertyName("apps")]
    public List<string> CallProcesses { get; set; } = [.. DefaultCallProcesses];

    [JsonPropertyName("ignore_apps")]
    public List<string> IgnoreProcesses { get; set; } = [];

    public static readonly IReadOnlyList<string> DefaultCallProcesses =
    [
        "Zoom.exe",
        "ms-teams.exe",
        "Teams.exe",
        "Telegram.exe",
        "WhatsApp.exe",
        "Discord.exe",
        "WebexHost.exe",
        "CiscoCollabHost.exe",
        "slack.exe",
        "chrome.exe",
        "msedge.exe",
        "firefox.exe",
        "brave.exe",
        "opera.exe",
        "vivaldi.exe",
        "arc.exe",
        "Yandex.exe",
    ];
}

public sealed class TranscriptionSettings
{
    public static readonly IReadOnlyList<string> CloudEngines = ["assemblyai", "openai", "elevenlabs", "gemini"];
    public static readonly IReadOnlyList<string> LocalEngines = ["parakeet", "parakeet-ultra", "parakeet-sk", "whisper", "gigaam"];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>auto</c>: the cloud engine when there is a key and the network answers,
    /// the local one otherwise. A cloud engine's name means that service and
    /// nothing else; a local engine's name means nothing leaves the machine.
    /// </summary>
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "auto";

    [JsonPropertyName("cloud")]
    public string Cloud { get; set; } = "assemblyai";

    [JsonPropertyName("local_engine")]
    public string LocalEngine { get; set; } = "parakeet";

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    public static readonly IReadOnlyList<string> LevelNormalizations = ["auto", "off"];

    /// <summary>
    /// <c>auto</c>: a copy of each track whose speech is outside −26…−16 dBFS is
    /// brought toward −20 dBFS by a slow AGC before recognition. <c>off</c>: the
    /// tracks are heard as recorded. The recording itself is never changed.
    /// </summary>
    [JsonPropertyName("level_normalization")]
    public string LevelNormalization { get; set; } = "off";

    public static readonly IReadOnlyList<string> Diarizations = ["auto", "off"];

    /// <summary>
    /// <c>auto</c>: after a local engine, the call track is split by voice, so
    /// "them" becomes "them A", "them B". Cloud engines label voices themselves.
    /// Off by default: on a 5-person call it found 14 voices, and it adds about
    /// as much time as speech recognition before the summary (roadmap step 4).
    /// </summary>
    [JsonPropertyName("diarization")]
    public string Diarization { get; set; } = "off";

    /// <summary>
    /// The tuned default for <see cref="DiarizationThreshold"/> (roadmap step 4).
    /// At 0.4 and 0.5 one person on a one-person call became two voices with
    /// the app's own audio decoder (and one with ffmpeg's), so the result
    /// flipped on decoding noise; from 0.6 it is one voice either way. The cost
    /// is that quiet people on a large call merge (6 voices for about 13
    /// people). A split is the smaller harm: speaker naming can join two labels
    /// it finds to be one person, but nothing can part two people under one.
    /// </summary>
    public const double DefaultDiarizationThreshold = 0.6;

    /// <summary>How alike two voices must be to count as one person: higher gives fewer people. Empty means the tuned default.</summary>
    [JsonPropertyName("diarization_threshold")]
    public double? DiarizationThreshold { get; set; }

    [JsonPropertyName("openai")]
    public OpenAiTranscriptionSettings OpenAi { get; set; } = new();

    [JsonPropertyName("assemblyai")]
    public AssemblyAiTranscriptionSettings AssemblyAi { get; set; } = new();

    [JsonPropertyName("gemini")]
    public GeminiTranscriptionSettings Gemini { get; set; } = new();
}

public sealed class GeminiTranscriptionSettings
{
    /// <summary>
    /// The models asked in this order, at most three: the first is retried while it is busy, each
    /// later one is asked once when the one before it has given up. Empty means the tested pair.
    /// </summary>
    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = [Amanu.Core.Processing.GeminiTranscript.Model, Amanu.Core.Processing.GeminiTranscript.FallbackModel];
}

public sealed class OpenAiTranscriptionSettings
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-transcribe-diarize";
}

public sealed class AssemblyAiTranscriptionSettings
{
    [JsonPropertyName("speech_model")]
    public string? SpeechModel { get; set; }
}

public sealed class LiveTranscriptionSettings
{
    /// <summary>"local" keeps the audio on this PC; "gemini" streams it to Gemini Live.</summary>
    public static readonly IReadOnlyList<string> Engines = ["local", "gemini"];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "local";

    [JsonPropertyName("gemini_model")]
    public string GeminiModel { get; set; } = Processing.GeminiLive.DefaultModel;
}

public sealed class SummarySettings
{
    public static readonly IReadOnlyList<string> Backends =
        ["auto", "claude-cli", "anthropic-api", "codex-cli", "openai-api", "ollama", "none"];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "auto";

    /// <summary>
    /// What the summary is written from: "transcript" is the final transcript,
    /// "live" the live captions kept in live-transcript.md, "both" sends the two
    /// together. A recording without live captions always uses the transcript.
    /// </summary>
    public static readonly IReadOnlyList<string> Sources = ["transcript", "live", "both"];

    [JsonPropertyName("source")]
    public string Source { get; set; } = "transcript";

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("template")]
    public string? Template { get; set; }

    /// <summary>The Anthropic model, for the API and for the claude CLI once set.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("openai_model")]
    public string OpenAiModel { get; set; } = "gpt-5";

    [JsonPropertyName("openai_base_url")]
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    [JsonPropertyName("openai_compatible")]
    public bool? OpenAiCompatible { get; set; }

    [JsonIgnore]
    public bool UsesCompatibleOpenAi => OpenAiCompatible ??
        !(Uri.TryCreate(OpenAiBaseUrl, UriKind.Absolute, out var uri) &&
          uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase));

    // Keep the saved custom URL while the official provider is selected. An
    // unconfigured compatible provider must not fall through to OpenAI.
    [JsonIgnore]
    public string EffectiveOpenAiBaseUrl => OpenAiCompatible switch
    {
        false => "https://api.openai.com/v1",
        true when Uri.TryCreate(OpenAiBaseUrl, UriKind.Absolute, out var uri) &&
                  uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) => "",
        _ => OpenAiBaseUrl,
    };

    [JsonPropertyName("ollama_model")]
    public string OllamaModel { get; set; } = "qwen3:8b";

    [JsonPropertyName("ollama_base_url")]
    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";

    public const string DefaultAnthropicModel = "claude-opus-5";

    [JsonIgnore]
    public string AnthropicModel => string.IsNullOrWhiteSpace(Model) ? DefaultAnthropicModel : Model;
}

public sealed class SpeakerNameSettings
{
    public static readonly IReadOnlyList<string> Backends =
        ["summary", "auto", "claude-cli", "anthropic-api", "codex-cli", "openai-api", "ollama", "none"];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>summary</c> sends the transcript wherever summaries go, and nowhere when
    /// they are off. Anything else is a choice for naming alone; <c>none</c> asks no model.
    /// </summary>
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "summary";

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>people.txt: who may be on calls. Empty means people.txt in the app data folder.</summary>
    [JsonPropertyName("people_file")]
    public string? PeopleFile { get; set; }
}

/// <summary>The "summary ready" card after a call from one of these apps.</summary>
public sealed class SlackSummarySettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>The process_family values in meta.json that count as Slack.</summary>
    [JsonPropertyName("apps")]
    public List<string> Apps { get; set; } = ["slack.exe"];

    /// <summary>A call that rang and stopped is shorter than this, and gives no card.</summary>
    [JsonPropertyName("min_minutes")]
    public int MinMinutes { get; set; } = 3;
}

public sealed class CommandHook
{
    [JsonPropertyName("executable")]
    public string Executable { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = [];
}
