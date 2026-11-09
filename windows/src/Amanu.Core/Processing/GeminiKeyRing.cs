namespace Amanu.Core.Processing;

/// <summary>
/// The Gemini keys a session may use, in the order they were saved. A free key allows only a
/// few requests a day, so when one answers 429 the next is asked at once, and the key that
/// worked stays first for the rest of the session: the pieces after it should not each spend a
/// request finding out again that the earlier keys are used up.
/// </summary>
public sealed class GeminiKeyRing
{
    private readonly string[] keys;
    private int current;

    public GeminiKeyRing(IEnumerable<string> keys)
    {
        this.keys = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()).Distinct().ToArray();
        if (this.keys.Length == 0) throw new ArgumentException("At least one key is needed.", nameof(keys));
    }

    public int Count => keys.Length;

    public string Current => keys[current];

    /// <summary>
    /// Sends with the current key and, while the answer is 429, with each other key once. Any other
    /// busy answer (500, 503) is the service's, not the key's, so another key would not help and the
    /// caller's back-off decides what happens next.
    /// </summary>
    public async Task<(string? Body, ProcessingFailure? Busy)> SendAsync(
        Func<string, Task<(string? Body, ProcessingFailure? Busy, int Status)>> send)
    {
        ProcessingFailure? busy = null;
        for (var tried = 0; tried < keys.Length; tried++)
        {
            var (body, failure, status) = await send(Current).ConfigureAwait(false);
            if (body is not null) return (body, null);
            busy = failure;
            if (status != 429) break;
            current = (current + 1) % keys.Length;
        }
        return (null, busy);
    }
}
