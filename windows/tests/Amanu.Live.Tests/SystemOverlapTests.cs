using Amanu.App;
using Amanu.Core.Processing;
using NAudio.Wave;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class SystemOverlapTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", true)]
    [InlineData(" ON ", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    public void Overlap_is_off_unless_switched_on(string? setting, bool expected) =>
        Assert.Equal(expected, SystemOverlap.Enabled(setting));

    [Theory]
    [InlineData(null, 16, 8)]
    [InlineData("abc", 16, 8)]
    [InlineData("4", 16, 4)]
    [InlineData("40", 16, 16)]
    [InlineData(null, 1, 1)]
    [InlineData("0", 16, 8)]
    public void Overlap_threads_default_to_half(string? setting, int total, int expected) =>
        Assert.Equal(expected, SystemOverlap.OverlapThreads(setting, total));

    private sealed class PassBackend : IEchoCancellationBackend
    {
        public int SampleRate => 16_000;
        public int HopSize => 256;
        public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output) => microphone.CopyTo(output);
        public void Dispose() { }
    }

    private static string Tone(string path, double seconds, double hz, int rate = 16_000)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        var samples = new short[(int)(seconds * rate)];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * hz * i / rate));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(40, 0)]
    public void System_only_audio_matches_the_system_track_echo_removal_writes(int micOffsetMs, int systemOffsetMs)
    {
        var directory = Directory.CreateTempSubdirectory("amanu-overlap").FullName;
        try
        {
            var audio = new SessionAudio(directory, "t", Tone(Path.Combine(directory, "mic.wav"), 1.2, 300, 16_000),
                Tone(Path.Combine(directory, "system.wav"), 1.0, 440, 48_000), null, micOffsetMs, systemOffsetMs);
            var prepared = OfflineEchoAudio.Prepare(audio, () => new PassBackend(), CancellationToken.None);
            var only = Path.Combine(directory, "only.wav");
            OfflineEchoAudio.PrepareSystemOnly(audio, only, CancellationToken.None);

            float[] Read(string path)
            {
                using var reader = new AudioFileReader(path);
                var all = new List<float>();
                var buffer = new float[4096];
                int read;
                while ((read = ((ISampleProvider)reader).Read(buffer.AsSpan())) != 0) all.AddRange(buffer.Take(read));
                return [.. all];
            }
            var expected = Read(prepared.System);
            var actual = Read(only);
            Assert.True(actual.Length <= expected.Length);
            Assert.Equal(expected.Take(actual.Length), actual);
            Assert.All(expected.Skip(actual.Length), sample => Assert.Equal(0f, sample));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
