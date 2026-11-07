using System.IO;
using Amanu.App;
using NAudio.Wave;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class LongAudioChunkTests
{
    [Fact]
    public void Context_chunks_cover_the_track_once_and_keep_audio_across_the_cut()
    {
        var root = Path.Combine(Path.GetTempPath(), "amanu-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wave = Path.Combine(root, "input.wav");
            using (var writer = new WaveFileWriter(wave, new WaveFormat(16000, 16, 1)))
            {
                var samples = new short[125 * 16000];
                // Two sides of the first boundary must survive in both context windows.
                samples[60 * 16000 - 1] = 1234;
                samples[60 * 16000] = 2345;
                var bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                writer.Write(bytes, 0, bytes.Length);
            }
            var chunks = AudioPreprocessor.SplitWithContext(wave, root, 60, 1);
            Assert.Equal(3, chunks.Count);
            Assert.Equal(new long[] { 0, 60000, 120000 }, chunks.Select(chunk => chunk.StartMs));
            Assert.Equal(new long[] { 60000, 120000, 125000 }, chunks.Select(chunk => chunk.EndMs));
            Assert.Equal(new long[] { 0, 59000, 119000 }, chunks.Select(chunk => chunk.OffsetMs));
            foreach (var chunk in chunks)
            {
                using var reader = new WaveFileReader(chunk.Path);
                Assert.InRange(reader.TotalTime.TotalSeconds, 1, 62);
            }
            foreach (var chunk in chunks.Take(2))
            {
                using var reader = new WaveFileReader(chunk.Path);
                reader.Position = (60000 - chunk.OffsetMs) * 32 - 2;
                var bytes = new byte[4];
                Assert.Equal(4, reader.Read(bytes, 0, 4));
                Assert.Equal(1234, BitConverter.ToInt16(bytes, 0));
                Assert.Equal(2345, BitConverter.ToInt16(bytes, 2));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Silence_is_left_out_of_chunks_and_times_map_back()
    {
        var root = Path.Combine(Path.GetTempPath(), "amanu-silence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wave = Path.Combine(root, "input.wav");
            var random = new Random(3);
            using (var writer = new WaveFileWriter(wave, new WaveFormat(16000, 16, 1)))
            {
                // 0-20 s speech-like tone, 20-100 s noise floor, 100-110 s tone again.
                var samples = new short[110 * 16000];
                for (var index = 0; index < samples.Length; index++)
                {
                    var second = index / 16000.0;
                    var tone = second < 20 || second >= 100;
                    samples[index] = (short)(tone ? 3000 * Math.Sin(index * 0.05) : random.Next(-3, 4));
                }
                var bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                writer.Write(bytes, 0, bytes.Length);
            }
            var keep = AudioPreprocessor.MeasureSpeechBlocks(wave);
            Assert.NotNull(keep);
            var chunks = AudioPreprocessor.SplitWithContext(wave, root, 60, 1, keep);
            Assert.Equal(2, chunks.Count);
            var sent = chunks.Sum(chunk => new FileInfo(chunk.Path).Length / 32000.0);
            Assert.InRange(sent, 25, 50);
            // A time on the short chunk is a time on the track: the second chunk's tone starts 100 s in.
            var last = chunks[1];
            var map = Assert.IsType<Amanu.Core.Processing.SilenceSkip.TimeMap>(last.Map);
            Assert.InRange(last.OffsetMs + map.ToOriginal(map.ShortLengthMs - 1), 109_000, 110_000);
            Assert.Equal(60_000, last.StartMs);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pause_cuts_split_the_track_where_given_and_cover_it_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "amanu-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wave = Path.Combine(root, "input.wav");
            using (var writer = new WaveFileWriter(wave, new WaveFormat(16000, 16, 1)))
            {
                var bytes = new byte[125 * 32000];
                writer.Write(bytes, 0, bytes.Length);
            }
            var chunks = AudioPreprocessor.SplitWithContext(wave, root, 60, 1, null, [520, 1120, 1250]);
            Assert.Equal(new long[] { 0, 52000, 112000 }, chunks.Select(chunk => chunk.StartMs));
            Assert.Equal(new long[] { 52000, 112000, 125000 }, chunks.Select(chunk => chunk.EndMs));
            Assert.Equal(new long[] { 0, 51000, 111000 }, chunks.Select(chunk => chunk.OffsetMs));
        }
        finally { Directory.Delete(root, true); }
    }
}
