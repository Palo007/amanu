using Amanu.App;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;
using NAudio.Wave;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class KeptEchoAudioTests
{
    private sealed class PassBackend : IEchoCancellationBackend
    {
        public int SampleRate => 16_000;
        public int HopSize => 256;
        public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output) => microphone.CopyTo(output);
        public void Dispose() { }
    }

    private static string Tone(string path, double seconds, double hz)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1));
        var samples = new short[(int)(seconds * 16_000)];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * hz * i / 16_000));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static (string Directory, OfflineEchoAudio.Result Kept, EchoCancellationRecord Echo) KeepOne()
    {
        var directory = Directory.CreateTempSubdirectory("amanu-kept").FullName;
        var audio = new SessionAudio(directory, "t", Tone(Path.Combine(directory, "mic.wav"), 1.2, 300),
            Tone(Path.Combine(directory, "system.wav"), 1.0, 440), null, 20, 0);
        var prepared = OfflineEchoAudio.Prepare(audio, _ => new PassBackend(), CancellationToken.None);
        var echo = new EchoCancellationRecord(LocalVqeAssets.Default.ProcessorVersion,
            prepared.ModelRan ? LocalVqeAssets.Default.Sha256 : null, EchoCanceller.SampleRate, prepared.Frames, Mode: "sonora");
        var kept = OfflineEchoAudio.Keep(prepared, directory, echo, CancellationToken.None);
        Assert.False(Directory.Exists(prepared.Directory));
        return (directory, kept, echo);
    }

    [Fact]
    public void Kept_audio_is_found_again_and_survives_a_pass_cleaning_up()
    {
        var (directory, kept, echo) = KeepOne();
        try
        {
            Assert.True(kept.Kept);
            Assert.Equal(Path.Combine(directory, OfflineEchoAudio.KeptFolder), kept.Directory);
            using (var reader = new WaveFileReader(kept.Microphone))
            {
                Assert.Equal(16, reader.WaveFormat.BitsPerSample);
                Assert.Equal(kept.Frames, reader.SampleCount);
            }

            kept.RemoveAudio();
            kept.Delete();
            var found = OfflineEchoAudio.TryReuse(directory);
            Assert.NotNull(found);
            Assert.Equal(kept.OffsetMs, found.Value.Audio.OffsetMs);
            Assert.Equal(kept.Frames, found.Value.Audio.Frames);
            Assert.Equal(echo, found.Value.Echo);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void A_retranscription_keeps_the_audio_but_drops_provider_answers_unless_told_not_to()
    {
        var (directory, kept, _) = KeepOne();
        try
        {
            var cache = Path.Combine(kept.Directory, ".cache-test-0.json");
            File.WriteAllText(cache, "{}");
            OfflineEchoAudio.Clear(directory, keepCleaned: true);
            Assert.False(File.Exists(cache));
            Assert.NotNull(OfflineEchoAudio.TryReuse(directory));

            OfflineEchoAudio.Clear(directory, keepCleaned: false);
            Assert.False(Directory.Exists(kept.Directory));
            Assert.Null(OfflineEchoAudio.TryReuse(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Audio_kept_by_another_echo_remover_revision_is_not_reused()
    {
        var (directory, kept, _) = KeepOne();
        try
        {
            var manifest = Path.Combine(kept.Directory, "kept.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(LocalVqeAssets.Default.ProcessorVersion, "older-revision"));
            Assert.Null(OfflineEchoAudio.TryReuse(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Audio_kept_by_the_other_echo_model_is_not_reused()
    {
        var (directory, _, _) = KeepOne();
        try
        {
            Assert.NotNull(OfflineEchoAudio.TryReuse(directory, LocalVqeAssets.Default));
            Assert.Null(OfflineEchoAudio.TryReuse(directory, LocalVqeAssets.Fast));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Keeping_is_on_by_default()
    {
        Assert.True(new AppSettings().KeepEchoCleanedAudio);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"keep_echo_cleaned_audio": false}""")!.KeepEchoCleanedAudio);
    }
}
