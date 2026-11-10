using System.Net;
using System.Text;
using Amanu.App;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;
using Xunit;

namespace Amanu.Live.Tests;

/// <summary>"Summarize again" with no model: Ollama's address answered by a fake handler.</summary>
public sealed class SummaryOnDemandTests
{
    private sealed class FakeOllama : HttpMessageHandler
    {
        public Queue<string> Answers { get; } = new();
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { message = new { content = Answers.Dequeue() } }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    [Theory]
    [InlineData("transcript", true, false)]
    [InlineData("live", false, true)]
    [InlineData("both", true, true)]
    public async Task The_summary_is_written_from_the_source_the_setting_names(string source, bool sendsFinal, bool sendsLive)
    {
        var root = Path.Combine(Path.GetTempPath(), "amanu-summary-tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "meeting");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = AppSettings.CreateDefault(root);
            settings.Summary.Backend = "ollama";
            settings.Summary.OllamaModel = "fake";
            settings.Summary.Source = source;
            settings.SpeakerNames.PeopleFile = Path.Combine(root, "no-people.txt");
            await TranscriptWriter.WriteAsync(directory, "Test meeting",
                new TranscriptDocument("assemblyai", "universal", DateTimeOffset.UtcNow, [new(0, 1000, "FinalWords", "A")]));
            LiveTranscriptFile.Append(directory, 0, "me", "LiveWords", "Gemini Live (x)");

            var ollama = new FakeOllama();
            ollama.Answers.Enqueue("Summary");
            using var http = new HttpClient(ollama);
            await new SummaryService(new LanguageModels(http, new SecretStore(), () => settings), () => settings)
                .GenerateAsync(directory, directory, CancellationToken.None);

            var sent = Assert.Single(ollama.Requests);
            Assert.Equal(sendsFinal, sent.Contains("FinalWords"));
            Assert.Equal(sendsLive, sent.Contains("LiveWords"));
            Assert.Equal(source == "both", sent.Contains("two transcripts of the same meeting"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Each_new_summary_archives_the_one_before_and_leaves_it_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "amanu-summary-tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "meeting");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = AppSettings.CreateDefault(root);
            settings.Summary.Backend = "ollama";
            settings.Summary.OllamaModel = "fake";
            settings.SpeakerNames.PeopleFile = Path.Combine(root, "no-people.txt");
            await TranscriptWriter.WriteAsync(directory, "Test meeting",
                new TranscriptDocument("assemblyai", "universal", DateTimeOffset.UtcNow, [new(0, 1000, "Words", "A")]));
            var first = Path.Combine(directory, "summary.md");
            await File.WriteAllTextAsync(first, "First summary");
            var firstBytes = await File.ReadAllBytesAsync(first);

            var ollama = new FakeOllama();
            ollama.Answers.Enqueue("Second summary");
            ollama.Answers.Enqueue("Third summary");
            using var http = new HttpClient(ollama);
            var service = new SummaryService(new LanguageModels(http, new SecretStore(), () => settings), () => settings);
            await service.GenerateAsync(directory, directory, CancellationToken.None);
            await service.GenerateAsync(directory, directory, CancellationToken.None);

            var versions = await SummaryVersions.ReadAsync(directory);
            Assert.Equal(3, versions.Count);
            Assert.True(versions[0].IsCurrent);
            Assert.Equal("ollama", versions[0].Backend);
            Assert.Equal("fake", versions[0].Model);
            Assert.Equal("Third summary", (await File.ReadAllTextAsync(first)).Trim());
            Assert.Equal("Second summary", (await File.ReadAllTextAsync(Path.Combine(versions[1].Directory, "summary.md"))).Trim());
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(Path.Combine(versions[2].Directory, "summary.md")));
            Assert.Equal("?", versions[2].Backend);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Every_backend_that_could_run_is_offered_whatever_summary_backend_is_set()
    {
        var settings = AppSettings.CreateDefault(Path.GetTempPath());
        settings.Summary.Backend = "none";
        using var http = new HttpClient(new FakeOllama());
        var available = new LanguageModels(http, new SecretStore(), () => settings).AvailableForSummary();
        Assert.Contains(available, backend => backend.Name == "ollama");
        Assert.Equal("none", settings.Summary.Backend);
    }
}
