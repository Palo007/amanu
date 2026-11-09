using System.Text.Json;
using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class GeminiTranscriptTests
{
    [Fact]
    public void Prompt_names_the_language_the_words_and_the_names()
    {
        var prompt = GeminiTranscript.Prompt(["ru", "en"], ["Kubernetes", "Anna Petrova"]);
        Assert.Contains("Русский (ru)", prompt);
        Assert.Contains("English (en)", prompt);
        Assert.Contains("Kubernetes, Anna Petrova", prompt);
        Assert.Contains("Do not translate", prompt);
    }

    [Fact]
    public void Prompt_without_a_language_or_words_asks_for_neither()
    {
        var prompt = GeminiTranscript.Prompt([], null);
        Assert.DoesNotContain("The speech is in", prompt);
        Assert.DoesNotContain("spell them", prompt);
    }

    [Fact]
    public void Request_body_carries_the_audio_and_a_zero_temperature_but_no_key()
    {
        using var document = JsonDocument.Parse(GeminiTranscript.RequestBody("say it", [1, 2, 3]));
        var parts = document.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("say it", parts[0].GetProperty("text").GetString());
        Assert.Equal("audio/wav", parts[1].GetProperty("inline_data").GetProperty("mime_type").GetString());
        Assert.Equal("AQID", parts[1].GetProperty("inline_data").GetProperty("data").GetString());
        var config = document.RootElement.GetProperty("generationConfig");
        Assert.Equal(0, config.GetProperty("temperature").GetInt32());
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
    }

    [Fact]
    public void Response_gives_the_text_and_the_model_that_answered()
    {
        var body = """{"candidates":[{"content":{"parts":[{"text":"[]"}]}}],"modelVersion":"gemini-3.7-flash"}""";
        var (text, version) = GeminiTranscript.ReadResponse(body);
        Assert.Equal("[]", text);
        Assert.Equal("gemini-3.7-flash", version);
        Assert.Null(GeminiTranscript.ReadResponse("""{"candidates":[{"content":{"parts":[{"text":"[]"}]}}]}""").ModelVersion);
    }

    [Fact]
    public void Response_that_is_not_json_is_a_transient_failure_that_does_not_quote_it()
    {
        var failure = Assert.Throws<ProcessingFailure>(() => GeminiTranscript.ReadResponse("<html>secret body</html>"));
        Assert.Equal(FailureKind.Transient, failure.Kind);
        Assert.DoesNotContain("secret", failure.Message);
    }

    [Theory]
    [InlineData("\"01:05\"", 65000)]
    [InlineData("\"1:02:03\"", 3723000)]
    [InlineData("\"00:07.5\"", 7500)]
    [InlineData("12", 12000)]
    [InlineData("12.25", 12250)]
    [InlineData("\"90\"", 90000)]
    public void Start_is_read_as_mm_ss_h_mm_ss_or_seconds(string start, long expectedMs)
    {
        var text = $$"""[{"start": {{start}}, "speaker": "A", "text": "hi"}]""";
        var segment = Assert.Single(GeminiTranscript.Segments(text, SpeakerLabels.Me, 0, 0, long.MaxValue));
        Assert.Equal(expectedMs, segment.StartMs);
    }

    [Fact]
    public void End_is_the_next_start_and_the_last_ends_with_the_piece()
    {
        var text = """[{"start":"00:01","speaker":"A","text":"one"},{"start":"00:04","speaker":"B","text":"two"}]""";
        var segments = GeminiTranscript.Segments(text, SpeakerLabels.Them, 0, 0, 10000);
        Assert.Equal([1000L, 4000L], segments.Select(s => s.StartMs));
        Assert.Equal([4000L, 10000L], segments.Select(s => s.EndMs));
    }

    [Fact]
    public void Context_is_stripped_and_times_move_to_the_track()
    {
        // A piece file starts at 299 s of the track, one second of context before its own span of 300 s to 600 s.
        var text = """
            [{"start":"00:00","speaker":"A","text":"heard twice"},
             {"start":"00:02","speaker":"A","text":"mine"},
             {"start":"05:00","speaker":"A","text":"mine too"},
             {"start":"05:02","speaker":"A","text":"next piece's"}]
            """;
        var segments = GeminiTranscript.Segments(text, SpeakerLabels.Them, 299000, 300000, 600000);
        Assert.Equal(["mine", "mine too"], segments.Select(s => s.Text));
        Assert.Equal([301000L, 599000L], segments.Select(s => s.StartMs));
        Assert.Equal(600000L, segments[^1].EndMs);
    }

    [Fact]
    public void Empty_text_is_dropped_and_speaker_letters_become_raw_labels()
    {
        var text = """[{"start":"00:01","speaker":"A","text":"  "},{"start":"00:02","speaker":"B","text":" hello "},{"start":"00:03","text":"no letter"}]""";
        var segments = GeminiTranscript.Segments(text, SpeakerLabels.Them, 0, 0, 10000);
        Assert.Equal(["hello", "no letter"], segments.Select(s => s.Text));
        Assert.Equal(SpeakerLabels.Raw(SpeakerLabels.Them, "B"), segments[0].Speaker);
        Assert.Equal(SpeakerLabels.Raw(SpeakerLabels.Them, ""), segments[1].Speaker);
    }

    [Fact]
    public void Two_voices_on_the_call_side_become_them_A_and_them_B()
    {
        var text = """[{"start":"00:01","speaker":"A","text":"x"},{"start":"00:02","speaker":"B","text":"y"}]""";
        var labels = SpeakerLabels.Assign(GeminiTranscript.Segments(text, SpeakerLabels.Them, 0, 0, 10000)).Select(s => s.Speaker);
        Assert.Equal(["them A", "them B"], labels);
    }

    [Fact]
    public void A_fenced_answer_is_read_like_a_bare_one()
    {
        var text = "```json\n[{\"start\":\"00:01\",\"speaker\":\"A\",\"text\":\"fenced\"}]\n```";
        Assert.Equal("fenced", Assert.Single(GeminiTranscript.Segments(text, SpeakerLabels.Me, 0, 0, 5000)).Text);
    }

    [Fact]
    public void An_answer_cut_off_mid_array_keeps_its_complete_lines()
    {
        var text = """[{"start":"00:01","speaker":"A","text":"one {two}"},{"start":"00:02","speaker":"A","text":"three"},{"start":"00:03","speaker":"A","text":"cut of""";
        Assert.Equal(["one {two}", "three"], GeminiTranscript.Segments(text, SpeakerLabels.Me, 0, 0, 5000).Select(s => s.Text));
    }

    [Fact]
    public void Model_order_drops_blanks_and_repeats_keeps_three_and_falls_back_to_the_tested_pair()
    {
        Assert.Equal(["b", "a", "c"], GeminiTranscript.ModelOrder([" b ", "", "a", "b", "c", "d"]));
        Assert.Equal(GeminiTranscript.DefaultModels, GeminiTranscript.ModelOrder([" "]));
        Assert.Equal(GeminiTranscript.DefaultModels, GeminiTranscript.ModelOrder(null));
    }

    [Fact]
    public void Model_list_keeps_gemini_models_that_generate_content_and_leaves_out_the_others()
    {
        var body = """
            {"models":[
              {"name":"models/gemini-3.7-flash","supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/gemini-3.8-flash","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3.8-flash-tts","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-embedding-2","supportedGenerationMethods":["embedContent"]},
              {"name":"models/gemini-3.8-flash-live","supportedGenerationMethods":["bidiGenerateContent"]},
              {"name":"models/gemma-4-27b","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3.8-pro","supportedGenerationMethods":["countTokens"]}
            ]}
            """;
        Assert.Equal(["gemini-3.8-flash", "gemini-3.7-flash"], GeminiTranscript.TranscriptionModels(body));
        Assert.Empty(GeminiTranscript.TranscriptionModels("<html>"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[{\"start\": ")]
    [InlineData("\"just a string\"")]
    public void Malformed_answers_are_a_transient_failure(string text)
    {
        var failure = Assert.Throws<ProcessingFailure>(() => GeminiTranscript.Segments(text, SpeakerLabels.Me, 0, 0, 5000));
        Assert.Equal(FailureKind.Transient, failure.Kind);
    }
}
