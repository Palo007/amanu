using System.IO;
using System.Net.WebSockets;
using System.Text;
using Amanu.Core.Processing;

namespace Amanu.App;

/// <summary>One open Gemini Live connection, reduced to what the worker needs so tests can stand in for the service.</summary>
internal interface IGeminiLiveSocket : IAsyncDisposable
{
    Task SendAsync(string message, CancellationToken token);

    /// <summary>The next whole message, or null once the service has closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken token);
}

/// <summary>
/// Captions one audio track with Gemini Live in place of the local live model.
/// It connects on the first audible audio rather than when it is prepared, so an
/// idle app holds no session open, and it opens a new session before the
/// service's ten-minute limit. A key the service refuses passes to the next one.
/// </summary>
internal sealed class GeminiLiveWorker : ILiveSpeechWorker
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FinalWait = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RetireWait = TimeSpan.FromSeconds(5);
    private readonly Func<string, CancellationToken, Task<IGeminiLiveSocket>> open;
    private readonly IReadOnlyList<string> keys;
    private readonly string setup;
    private readonly Func<DateTimeOffset> now;
    private readonly GeminiLiveCaption caption = new();
    private readonly List<Task> retiring = [];
    private Session? session;
    private int keyIndex;

    public GeminiLiveWorker(IReadOnlyList<string> keys, string setup,
        Func<string, CancellationToken, Task<IGeminiLiveSocket>> open, Func<DateTimeOffset>? now = null)
    {
        if (keys.Count == 0) throw new ArgumentException("Gemini Live needs at least one key.", nameof(keys));
        this.keys = keys;
        this.setup = setup;
        this.open = open;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string Text { get; private set; } = "";
    public bool Healthy { get; private set; } = true;

    public async Task FeedAsync(float[] samples, int count, CancellationToken token)
    {
        try
        {
            var current = await SessionAsync(token).ConfigureAwait(false);
            for (var offset = 0; offset < count; offset += GeminiLive.ChunkSamples)
            {
                var length = Math.Min(GeminiLive.ChunkSamples, count - offset);
                await current.Socket.SendAsync(GeminiLive.AudioMessage(samples.AsSpan(offset, length)), token).ConfigureAwait(false);
            }
            Text = caption.Text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Healthy = false; throw; }
    }

    /// <summary>1 closes the utterance, 2 starts the next one; the same commands the local decoder takes.</summary>
    public async Task CommandAsync(byte command, CancellationToken token)
    {
        if (command == 1)
        {
            // The service makes words final when the speaker pauses, a moment
            // after the audio; wait briefly so the closed line holds the final text.
            var deadline = now() + FinalWait;
            while (caption.Pending && session is { Ended: false } && now() < deadline)
                await Task.Delay(50, token).ConfigureAwait(false);
            Text = caption.Text;
        }
        else if (command == 2)
        {
            caption.Begin();
            Text = "";
        }
    }

    private async Task<Session> SessionAsync(CancellationToken token)
    {
        if (session is { Ended: false, GoAway: false } current && now() - current.Opened < GeminiLive.SessionLength) return current;
        if (session is { } old) Retire(old);
        session = null;
        session = await ConnectAsync(token).ConfigureAwait(false);
        return session;
    }

    private async Task<Session> ConnectAsync(CancellationToken token)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < keys.Count; attempt++)
        {
            var index = (keyIndex + attempt) % keys.Count;
            IGeminiLiveSocket? socket = null;
            try
            {
                socket = await open(keys[index], token).ConfigureAwait(false);
                await socket.SendAsync(setup, token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(SetupTimeout);
                // A key over its quota is accepted as a connection and then closed
                // instead of answering the setup, so only setupComplete counts.
                while (true)
                {
                    var message = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false)
                        ?? throw new IOException("Gemini Live closed the connection before the session started.");
                    if (GeminiLive.Read(message).SetupComplete) break;
                }
                keyIndex = index;
                var opened = new Session(socket, now());
                opened.Receiving = ReceiveAsync(opened);
                return opened;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (socket is not null) await socket.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is IOException or WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                last = exception;
                if (socket is not null) await socket.DisposeAsync().ConfigureAwait(false);
            }
        }
        throw new IOException("Gemini Live did not start a session: " + (last?.Message ?? "no key"), last);
    }

    private async Task ReceiveAsync(Session current)
    {
        try
        {
            while (await current.Socket.ReceiveAsync(current.Stop.Token).ConfigureAwait(false) is { } message)
            {
                var answer = GeminiLive.Read(message);
                if (answer.GoAway) current.GoAway = true;
                caption.Apply(answer);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
        finally { current.Ended = true; }
    }

    /// <summary>Ends the old session in the background, keeping its last words, while audio goes to the new one.</summary>
    private void Retire(Session old)
    {
        lock (retiring)
        {
            retiring.RemoveAll(task => task.IsCompleted);
            retiring.Add(Task.Run(async () =>
            {
                try
                {
                    if (!old.Ended)
                    {
                        using var send = new CancellationTokenSource(RetireWait);
                        await old.Socket.SendAsync(GeminiLive.EndMessage(), send.Token).ConfigureAwait(false);
                    }
                    await old.Receiving.WaitAsync(RetireWait).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
                await old.DisposeAsync().ConfigureAwait(false);
            }));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (session is { } current) await current.DisposeAsync().ConfigureAwait(false);
        session = null;
        Task[] pending;
        lock (retiring) pending = [.. retiring];
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private sealed class Session(IGeminiLiveSocket socket, DateTimeOffset opened) : IAsyncDisposable
    {
        public IGeminiLiveSocket Socket { get; } = socket;
        public DateTimeOffset Opened { get; } = opened;
        public CancellationTokenSource Stop { get; } = new();
        public Task Receiving { get; set; } = Task.CompletedTask;
        public volatile bool Ended;
        public volatile bool GoAway;
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await Stop.CancelAsync().ConfigureAwait(false);
            try { await Receiving.ConfigureAwait(false); } catch (Exception exception) when (exception is OperationCanceledException) { }
            await Socket.DisposeAsync().ConfigureAwait(false);
            Stop.Dispose();
        }
    }
}

/// <summary>The real connection: a WebSocket to Google's BidiGenerateContent endpoint.</summary>
internal sealed class GeminiLiveSocket(ClientWebSocket socket) : IGeminiLiveSocket
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public static async Task<IGeminiLiveSocket> OpenAsync(string key, CancellationToken token)
    {
        // The key goes in a header so it stays out of the address; the address is
        // tried only if the service will not take the header.
        try { return await ConnectAsync(new Uri(GeminiLive.Endpoint), key, header: true, token).ConfigureAwait(false); }
        catch (WebSocketException) when (!token.IsCancellationRequested)
        {
            return await ConnectAsync(new Uri(GeminiLive.Endpoint + "?key=" + Uri.EscapeDataString(key)), key, header: false, token).ConfigureAwait(false);
        }
    }

    private static async Task<IGeminiLiveSocket> ConnectAsync(Uri address, string key, bool header, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        if (header) socket.Options.SetRequestHeader("x-goog-api-key", key);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(address, timeout.Token).ConfigureAwait(false);
            return new GeminiLiveSocket(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task SendAsync(string message, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, token);

    public async Task<string?> ReceiveAsync(CancellationToken token)
    {
        // The service sends its JSON in binary frames as well as text frames.
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        socket.Dispose();
    }
}
