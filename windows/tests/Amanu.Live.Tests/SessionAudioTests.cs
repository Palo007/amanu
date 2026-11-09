using Amanu.App;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class SessionAudioTests
{
    [Fact]
    public void Cache_directory_is_the_session_folder_by_default()
    {
        var audio = new SessionAudio(@"C:\session", "t", "mic.wav", "system.wav", null, 0, 0);
        Assert.Equal(@"C:\session", audio.CacheDirectory);
    }

    [Fact]
    public void Pointing_at_an_echo_cleaned_copy_keeps_the_cache_in_the_session_folder()
    {
        // Echo removal hands the engines a copy in a temporary folder that is deleted after the
        // pass; a cloud answer cached there would be lost, and a failed pass would send it all again.
        var audio = new SessionAudio(@"C:\session", "t", "mic.wav", "system.wav", null, 0, 0);
        var cleaned = audio with { Directory = @"C:\temp\echo", Microphone = @"C:\temp\echo\mic.wav" };
        Assert.Equal(@"C:\temp\echo", cleaned.Directory);
        Assert.Equal(@"C:\session", cleaned.CacheDirectory);
    }
}
