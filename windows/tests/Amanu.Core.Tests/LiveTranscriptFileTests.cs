using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class LiveTranscriptFileTests
{
    [Fact]
    public void Final_lines_are_kept_in_order_with_a_heading_naming_the_engine()
    {
        var directory = Directory.CreateTempSubdirectory("amanu-live-file").FullName;
        try
        {
            LiveTranscriptFile.Append(directory, 65_000, "me", " Good morning. ", "Gemini Live (x)");
            LiveTranscriptFile.Append(directory, 3_725_000, "them", "Hello.", "Gemini Live (x)");
            LiveTranscriptFile.Append(directory, 3_726_000, "them", "  ", "Gemini Live (x)");

            var text = File.ReadAllText(Path.Combine(directory, LiveTranscriptFile.FileName));
            Assert.StartsWith("# Live transcript", text);
            Assert.Contains("by Gemini Live (x).", text);
            Assert.Contains("[01:05] **me:** Good morning.\n", text);
            Assert.Contains("[1:02:05] **them:** Hello.\n", text);
            Assert.True(text.IndexOf("Good morning", StringComparison.Ordinal) < text.IndexOf("Hello", StringComparison.Ordinal));
            Assert.Single(text.Split("# Live transcript").Skip(1));
            Assert.Equal(2, text.Split("**").Length / 2);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
