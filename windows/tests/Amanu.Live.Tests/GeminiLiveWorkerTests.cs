using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using Amanu.App;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class GeminiLiveWorkerTests
{
    private const string Setup = """{"setup":{}}""";

    [Fact]
    public async Task Connects_on_the_first_audio_and_streams_it_in_100_ms_parts()
    {
        var service = new Service();
        await using var worker = new GeminiLiveWorker(["good"], Setup, service.OpenAsync);
        Assert.Empty(service.Sockets);

        await worker.FeedAsync(new float[35_840], 35_840, CancellationToken.None);

        var socket = Assert.Single(service.Sockets);
        Assert.Equal(Setup, socket.Sent.First());
        Assert.Equal(23, socket.Sent.Count(message => message.Contains("audio/pcm;rate=16000")));
        Assert.True(worker.Healthy);
    }

    [Fact]
    public async Task A_key_the_service_closes_passes_to_the_next_key_which_then_stays()
    {
        var service = new Service();
        await using var worker = new GeminiLiveWorker(["spent", "good"], Setup, service.OpenAsync);
        await worker.FeedAsync(new float[1600], 1600, CancellationToken.None);
        Assert.Equal(["spent", "good"], service.Sockets.Select(socket => socket.Key));
    }

    [Fact]
    public async Task When_every_key_is_refused_the_worker_is_unhealthy_and_says_why()
    {
        var service = new Service();
        await using var worker = new GeminiLiveWorker(["spent", "spent"], Setup, service.OpenAsync);
        var failure = await Assert.ThrowsAsync<IOException>(() => worker.FeedAsync(new float[1600], 1600, CancellationToken.None));
        Assert.Contains("did not start a session", failure.Message);
        Assert.False(worker.Healthy);
    }

    [Fact]
    public async Task Interim_and_final_text_make_the_caption_and_closing_waits_for_the_final()
    {
        var service = new Service();
        await using var worker = new GeminiLiveWorker(["good"], Setup, service.OpenAsync);
        await worker.FeedAsync(new float[1600], 1600, CancellationToken.None);
        var socket = service.Sockets.Single();

        socket.Answer("""{"serverContent":{"interimInputTranscription":{"text":"hello wor"}}}""");
        await Until(() => worker.FeedAsync(new float[1600], 1600, CancellationToken.None), () => worker.Text == "hello wor");

        var closing = worker.CommandAsync(1, CancellationToken.None);
        socket.Answer("""{"serverContent":{"inputTranscription":{"text":"Hello world."}}}""");
        await closing;
        Assert.Equal("Hello world.", worker.Text);

        await worker.CommandAsync(2, CancellationToken.None);
        Assert.Equal("", worker.Text);
    }

    [Fact]
    public async Task A_session_is_replaced_before_the_ten_minute_limit_and_the_old_one_is_ended()
    {
        var service = new Service();
        var clock = DateTimeOffset.UnixEpoch;
        await using var worker = new GeminiLiveWorker(["good"], Setup, service.OpenAsync, () => clock);
        await worker.FeedAsync(new float[1600], 1600, CancellationToken.None);
        clock += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(1);
        await worker.FeedAsync(new float[1600], 1600, CancellationToken.None);

        Assert.Equal(2, service.Sockets.Count);
        var old = service.Sockets.First();
        await Until(() => Task.CompletedTask, () => old.Sent.Contains("""{"realtimeInput":{"audioStreamEnd":true}}"""));
    }

    [Fact]
    public async Task A_go_away_message_moves_the_next_audio_to_a_new_session()
    {
        var service = new Service();
        await using var worker = new GeminiLiveWorker(["good"], Setup, service.OpenAsync);
        await worker.FeedAsync(new float[1600], 1600, CancellationToken.None);
        service.Sockets.Single().Answer("""{"goAway":{"timeLeft":"5s"}}""");
        await Until(() => worker.FeedAsync(new float[1600], 1600, CancellationToken.None), () => service.Sockets.Count == 2);
    }

    private static async Task Until(Func<Task> step, Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done())
        {
            await step();
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Stands in for Google: "good" keys start a session, any other key is closed after the setup.</summary>
    private sealed class Service
    {
        public ConcurrentQueue<FakeSocket> Sockets { get; } = new();

        public Task<IGeminiLiveSocket> OpenAsync(string key, CancellationToken token)
        {
            var socket = new FakeSocket(key);
            Sockets.Enqueue(socket);
            return Task.FromResult<IGeminiLiveSocket>(socket);
        }
    }

    private sealed class FakeSocket(string key) : IGeminiLiveSocket
    {
        private readonly Channel<string?> incoming = Channel.CreateUnbounded<string?>();
        public string Key => key;
        public ConcurrentQueue<string> Sent { get; } = new();

        public void Answer(string message) => incoming.Writer.TryWrite(message);

        public Task SendAsync(string message, CancellationToken token)
        {
            Sent.Enqueue(message);
            if (message == Setup) incoming.Writer.TryWrite(key == "good" ? """{"setupComplete":{}}""" : null);
            else if (message.Contains("audioStreamEnd")) incoming.Writer.TryWrite(null);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken token) => await incoming.Reader.ReadAsync(token);

        public ValueTask DisposeAsync()
        {
            incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
