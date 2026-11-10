using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using Amanu.App;
using NAudio.Wave;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class GeminiEngineProgressTests
{
    [Fact]
    public async Task A_stuck_model_is_left_after_the_request_limit_and_the_next_one_answers_with_progress_and_notes()
    {
        var directory = Directory.CreateTempSubdirectory("amanu-gemini-progress").FullName;
        try
        {
            var single = Tone(Path.Combine(directory, "audio.wav"), 2);
            var handler = new Handler();
            using var http = new HttpClient(handler);
            var engine = new GeminiTranscriptionEngine(http, ["key"], [], cache: false, models: ["stuck-model", "quick-model"])
            {
                RequestTimeout = TimeSpan.FromMilliseconds(200),
            };
            var progress = new ConcurrentQueue<double>();
            var notes = new ConcurrentQueue<string>();
            engine.Progress = progress.Enqueue;
            engine.Note = (line, _) => notes.Enqueue(line);

            var document = await engine.TranscribeAsync(new SessionAudio(directory, "t", null, null, single, 0, 0), CancellationToken.None);

            Assert.Equal("quick-model", document.Model);
            // A timeout is not retried on the same model: one try, then the next model.
            Assert.Equal(["stuck-model", "quick-model"], handler.Asked);
            Assert.Equal(1.0, progress.Last());
            Assert.Contains(notes, line => line.Contains("stuck-model no answer after") && line.Contains("trying quick-model"));
            Assert.Contains(notes, line => line.Contains("quick-model answered in"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void The_log_fold_out_shows_the_last_lines_with_local_clock_times()
    {
        var path = Path.GetTempFileName();
        try
        {
            var at = new DateTimeOffset(2026, 11, 10, 6, 50, 13, TimeSpan.Zero);
            File.WriteAllLines(path, Enumerable.Range(1, 20).Select(i => $"{at.AddSeconds(i):O} step {i}"));
            var lines = ProcessingDetails.Tail(path);
            Assert.Equal(14, lines.Count);
            var shown = ProcessingDetails.Format(lines)!;
            Assert.StartsWith(at.AddSeconds(7).ToLocalTime().ToString("HH:mm:ss") + "  step 7", shown);
            Assert.EndsWith("step 20", shown);
            Assert.Null(ProcessingDetails.Format(ProcessingDetails.Tail(path + ".missing")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Tone(string path, double seconds)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1));
        var samples = new short[(int)(seconds * 16_000)];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 300 * i / 16_000));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    /// <summary>"stuck-model" never answers; any other model answers at once with an empty transcript.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Asked { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var model = request.RequestUri!.AbsolutePath.Split('/')[^1].Split(':')[0];
            Asked.Enqueue(model);
            if (model == "stuck-model") await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"candidates":[{"content":{"parts":[{"text":"[]"}]}}],"modelVersion":"{{{model}}}"}"""),
            };
        }
    }
}
