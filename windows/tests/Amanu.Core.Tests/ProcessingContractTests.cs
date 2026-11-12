using System.Text.Json;
using Amanu.Core.Processing;
using Amanu.Core.Configuration;

namespace Amanu.Core.Tests;

public sealed class ProcessingContractTests
{
    [Fact]
    public async Task TranscriptWriter_creates_human_and_machine_readable_completion_files()
    {
        using var temp = new TemporaryDirectory();
        var transcript = new TranscriptDocument(
            "assemblyai",
            "universal-3-pro",
            DateTimeOffset.Parse("2026-09-20T12:00:00Z"),
            [new TranscriptSegment(1_500, 4_000, "Привет, мир", "them")]);

        await TranscriptWriter.WriteAsync(temp.Path, "Разговор", transcript,
            new Dictionary<string, string> { ["them"] = "Анна" });

        var markdown = await File.ReadAllTextAsync(Path.Combine(temp.Path, "transcript.md"));
        Assert.Contains("# Разговор", markdown);
        Assert.Contains("engine: assemblyai (universal-3-pro)", markdown);
        Assert.Contains("them → Анна", markdown);
        Assert.Contains("**[0:01] Анна:** Привет, мир", markdown);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "transcript.json")));
        Assert.Equal("assemblyai", json.RootElement.GetProperty("engine").GetString());
        Assert.Equal(1_500, json.RootElement.GetProperty("segments")[0].GetProperty("start_ms").GetInt64());
    }

    [Fact]
    public async Task The_transcript_names_the_echo_removal_beside_the_engine_and_keeps_it_through_a_rewrite()
    {
        using var temp = new TemporaryDirectory();
        var transcript = new TranscriptDocument("local", "parakeet", DateTimeOffset.Parse("2026-11-12T12:00:00Z"),
            [new TranscriptSegment(0, 1_000, "Hello", "me")],
            new TranscriptEcho("solve-gate-cut", true, Processor: "echo-solver-v1", TranscriptFilter: true));

        await TranscriptWriter.WriteAsync(temp.Path, "Call", transcript);

        var markdown = await File.ReadAllTextAsync(Path.Combine(temp.Path, "transcript.md"));
        Assert.Contains("engine: local (parakeet)\necho removal: solve-gate-cut; transcript echo filter on\n", markdown.ReplaceLineEndings("\n"));
        var json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "transcript.json"));
        var read = JsonSerializer.Deserialize<TranscriptDocument>(json)!;
        Assert.Equal(transcript.Echo, read.Echo);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("solve-gate-cut", parsed.RootElement.GetProperty("echo").GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("off", false, null, false, "off")]
    [InlineData("sonora", true, null, false, "sonora")]
    [InlineData("solve-gate", true, "many devices on the call, ran as plain sonora", true, "solve-gate (many devices on the call, ran as plain sonora); transcript echo filter on")]
    [InlineData("solve-gate-cut", false, "skipped: no DLL", false, "solve-gate-cut, not run (skipped: no DLL)")]
    public void Echo_removal_is_described_as_it_ran(string model, bool ran, string? note, bool filter, string expected) =>
        Assert.Equal(expected, new TranscriptEcho(model, ran, note, TranscriptFilter: filter).Describe());

    [Fact]
    public void A_transcript_from_before_echo_was_recorded_has_no_echo_line()
    {
        var read = JsonSerializer.Deserialize<TranscriptDocument>("""{"engine":"local","model":"parakeet","created_at":"2026-11-01T00:00:00Z","segments":[]}""")!;
        Assert.Null(read.Echo);
        Assert.DoesNotContain("echo removal", TranscriptWriter.RenderMarkdown("Old", read));
    }

    [Theory]
    [InlineData("assemblyai", true, true, "assemblyai", null)]
    [InlineData("assemblyai", false, true, null, null)]
    [InlineData("parakeet", true, true, null, "parakeet")]
    [InlineData("parakeet", true, false, null, null)]
    [InlineData("auto", true, true, "assemblyai", "parakeet")]
    [InlineData("auto", false, true, null, "parakeet")]
    [InlineData("auto", true, false, "assemblyai", null)]
    public void A_named_engine_never_falls_back_and_a_local_one_never_uploads(
        string engine, bool hasKey, bool modelReady, string? cloud, string? local)
    {
        var plan = EngineResolver.Plan(engine, "assemblyai", "parakeet", _ => hasKey, _ => modelReady);
        Assert.Equal(cloud, plan.Cloud);
        Assert.Equal(local, plan.Local);
        Assert.Equal(cloud is null && local is null, plan.Missing is not null);
    }

    [Fact]
    public void LocalCliResultParser_reads_jsonl_segments()
    {
        const string jsonl = "{\"file\":\"input.wav\",\"text\":\"hello\",\"segments\":[{\"t0_ms\":120,\"t1_ms\":900,\"speaker_id\":\"0\",\"text\":\"hello\"}]}";

        var result = LocalCliResultParser.Parse(jsonl);

        Assert.Equal("hello", result.Text);
        Assert.Collection(result.Segments, segment =>
        {
            Assert.Equal(120, segment.StartMs);
            Assert.Equal(900, segment.EndMs);
            Assert.Equal("0", segment.Speaker);
        });
    }

    [Fact]
    public void LocalCliResultParser_reads_windows_batch_output_with_unescaped_file_path()
    {
        const string jsonl = "{\"file\":\"C:\\Users\\tester\\AppData\\Local\\Temp\\input.wav\",\"text\":\"Привет\",\"segments\":[{\"t0_ms\":100,\"t1_ms\":700,\"text\":\"Привет\"}]}";

        var result = LocalCliResultParser.Parse(jsonl);

        Assert.Equal("Привет", result.Text);
        Assert.Single(result.Segments);
        Assert.Equal(100, result.Segments[0].StartMs);
    }

    [Fact]
    public void LocalCliResultParser_reads_every_line_of_a_batch_by_file_name()
    {
        const string jsonl =
            "{\"type\":\"batch_header\",\"load_ms\":279.6}\n" +
            "{\"file\":\"C:\\Users\\tester\\AppData\\Local\\Temp\\Amanu\\x\\chunk-0000.wav\",\"text\":\"это очень прикольный разговор\",\"segments\":[]}\r\n" +
            "{\"file\":\"C:\\Users\\tester\\AppData\\Local\\Temp\\Amanu\\x\\chunk-0001.wav\",\"text\":\"\",\"segments\":[]}\n";

        var results = LocalCliResultParser.ParseAll(jsonl);

        Assert.Equal(["chunk-0000.wav", "chunk-0001.wav"], results.Select(result => result.File));
        Assert.Equal("это очень прикольный разговор", results[0].Text);
        Assert.Equal("", results[1].Text);
        Assert.Equal("", LocalCliResultParser.Parse(jsonl).Text);
    }

    // What transcribe-cli v0.1.3 printed for the mic side of the first real
    // Windows recording, cut short; the lines before `words:` are its own.
    private const string CliWordOutput =
        "audio: C:\\Users\\tester\\AppData\\Local\\Temp\\Amanu\\input.wav\n" +
        "  duration:   141.867 s\r\n" +
        "run: ok\n" +
        "text: На английском языке про сериал Breaking Bad. А я сейчас вот говорю\n" +
        "segments: 1\n" +
        "  [  20.72 ->  122.88] На английском языке про сериал Breaking Bad. А я сейчас вот говорю\n" +
        "words: 11\n" +
        "  [  20.72 ->   20.96] На\n" +
        "  [  20.96 ->   21.52] английском\n" +
        "  [  21.52 ->   22.00] языке\n" +
        "  [  22.32 ->   22.64] про\n" +
        "  [  22.64 ->   23.04] сериал\n" +
        "  [  23.28 ->   23.84] Breaking\n" +
        "  [  23.84 ->   24.64] Bad.\r\n" +
        "  [  24.64 ->   24.88] А\n" +
        "  [  24.88 ->   25.04] я\n" +
        "  [  34.40 ->   34.72] сейчас\n" +
        "  [  34.88 ->   35.20] говорю\n" +
        "  [ timings ]  mel 12 ms\n";

    [Fact]
    public void LocalCliWords_reads_the_word_block_of_the_plain_output()
    {
        var words = LocalCliWords.Parse(CliWordOutput);

        Assert.Equal(11, words.Count);
        Assert.Equal(new TimedWord(20_720, 20_960, "На"), words[0]);
        Assert.Equal("Bad.", words[6].Text);
        Assert.Equal(new TimedWord(34_880, 35_200, "говорю"), words[^1]);
    }

    [Fact]
    public void LocalCliWords_is_empty_without_a_word_block()
    {
        Assert.Empty(LocalCliWords.Parse("run: ok\ntext: hello\nsegments: 1\n  [   0.00 ->    1.00] hello\n"));
        Assert.Equal("hello", LocalCliWords.FullText("run: ok\ntext: hello\r\n"));
        Assert.Equal("", LocalCliWords.FullText("run: ok\ntext: (empty)\n"));
    }

    [Fact]
    public void LocalCliWords_break_on_sentence_ends_and_on_pauses_over_a_second()
    {
        var segments = LocalCliWords.Segments(LocalCliWords.Parse(CliWordOutput));

        Assert.Collection(segments,
            first =>
            {
                Assert.Equal(20_720, first.StartMs);
                Assert.Equal(24_640, first.EndMs);
                Assert.Equal("На английском языке про сериал Breaking Bad.", first.Text);
            },
            second => Assert.Equal("А я", second.Text),
            third =>
            {
                Assert.Equal(34_400, third.StartMs);
                Assert.Equal("сейчас говорю", third.Text);
            });
    }

    [Fact]
    public void LocalCliWords_wrap_a_run_on_speaker_every_sixty_words()
    {
        var words = Enumerable.Range(0, 130).Select(index => new TimedWord(index * 300, index * 300 + 250, "слово")).ToArray();

        var segments = LocalCliWords.Segments(words);

        Assert.Equal([60, 60, 10], segments.Select(segment => segment.Text.Split(' ').Length));
    }

    [Fact]
    public void ModelCatalog_lists_three_parakeet_models()
    {
        Assert.Equal("parakeet-ultra-Q8_0.gguf", ModelCatalog.Models["parakeet-ultra"].FileName);
        Assert.Equal("parakeet-tdt-0.6b-v3-sk-Q8_0.gguf", ModelCatalog.Models["parakeet-sk"].FileName);
        Assert.Null(ModelCatalog.Models["parakeet-sk"].Url);
        Assert.NotNull(ModelCatalog.Models["parakeet-ultra"].Url);
        Assert.Contains("parakeet-ultra", TranscriptionSettings.LocalEngines);
        Assert.Contains("parakeet-sk", TranscriptionSettings.LocalEngines);
    }

    [Theory]
    [InlineData("parakeet", true)]
    [InlineData("parakeet-ultra", true)]
    [InlineData("parakeet-sk", true)]
    [InlineData("gigaam", false)]
    [InlineData("whisper", false)]
    [InlineData("nemotron-live", false)]
    public void IsParakeet_matches_only_parakeet_models(string id, bool expected) =>
        Assert.Equal(expected, ModelCatalog.IsParakeet(id));

    [Fact]
    public void ModelCatalog_pins_verified_local_models()
    {
        Assert.Equal("parakeet-tdt-0.6b-v3-Q8_0.gguf", ModelCatalog.Models["parakeet"].FileName);
        Assert.Equal("gigaam-v3-ctc-Q8_0.gguf", ModelCatalog.Models["gigaam"].FileName);
        Assert.Equal("whisper-large-v3-turbo-Q8_0.gguf", ModelCatalog.Models["whisper"].FileName);
    }

    [Theory]
    [InlineData("high", "Это говорит Анна Петрова", "говорит Анна", true)]
    [InlineData("medium", "Это говорит Анна Петрова", "говорит Анна", false)]
    [InlineData("high", "Это говорит Анна Петрова", "другая цитата", false)]
    [InlineData("high", "Анна", "Анна", false)]
    public void SpeakerProposal_requires_high_confidence_and_a_two_word_quote(
        string confidence, string transcript, string quote, bool expected)
    {
        Assert.Equal(expected, SpeakerNameValidator.Accept(confidence, quote, transcript));
    }

    [Fact]
    public void SummaryChunker_preserves_all_text_with_bounded_chunks()
    {
        var text = string.Join("\n", Enumerable.Repeat("строка для резюме", 1_000));

        var chunks = SummaryChunker.Split(text, 500);

        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 500));
        Assert.Equal(text.Replace("\n", ""), string.Concat(chunks).Replace("\n", ""));
    }

    [Fact]
    public void EchoFilter_removes_the_microphone_copy_but_keeps_real_turns()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, 2_000, "Нам нужно выпустить бету", "them"),
            new TranscriptSegment(120, 2_100, "Нам нужно выпустить бету", "me"),
            new TranscriptSegment(2_500, 3_500, "Согласен", "me"),
        };

        var filtered = TranscriptEchoFilter.Filter(segments);

        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, segment => segment.Speaker == "me" && segment.Text.Contains("выпустить"));
        Assert.Contains(filtered, segment => segment.Text == "Согласен");
    }
}
