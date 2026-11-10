using System.Text.Json;
using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class GeminiLiveTests
{
    [Fact]
    public void Setup_asks_for_text_with_the_languages_and_words_but_no_key()
    {
        using var document = JsonDocument.Parse(GeminiLive.SetupMessage("gemini-3.5-transcribe-live", ["sk", "en"], ["Amanu", " ", "Kubernetes"]));
        var setup = document.RootElement.GetProperty("setup");
        Assert.Equal("models/gemini-3.5-transcribe-live", setup.GetProperty("model").GetString());
        Assert.Equal("TEXT", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        var transcription = setup.GetProperty("inputAudioTranscription");
        Assert.Equal(["sk", "en"], transcription.GetProperty("languageCodes").EnumerateArray().Select(code => code.GetString()));
        Assert.Equal(["Amanu", "Kubernetes"], transcription.GetProperty("customVocabulary").EnumerateArray().Select(word => word.GetString()));
    }

    [Fact]
    public void Setup_without_languages_or_words_lets_the_service_detect_the_language()
    {
        using var document = JsonDocument.Parse(GeminiLive.SetupMessage("models/x", [], null));
        var setup = document.RootElement.GetProperty("setup");
        Assert.Equal("models/x", setup.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Object, setup.GetProperty("inputAudioTranscription").ValueKind);
        Assert.False(setup.GetProperty("inputAudioTranscription").TryGetProperty("languageCodes", out _));
    }

    [Fact]
    public void Audio_is_16_bit_little_endian_pcm_in_base64()
    {
        using var document = JsonDocument.Parse(GeminiLive.AudioMessage([0f, 1f, -1f, 2f]));
        var audio = document.RootElement.GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
        Assert.Equal(new byte[] { 0, 0, 0xFF, 0x7F, 0x01, 0x80, 0xFF, 0x7F }, Convert.FromBase64String(audio.GetProperty("data").GetString()!));
    }

    [Fact]
    public void Server_messages_are_read_and_anything_else_is_ignored()
    {
        Assert.True(GeminiLive.Read("""{"setupComplete":{}}""").SetupComplete);
        Assert.True(GeminiLive.Read("""{"goAway":{"timeLeft":"10s"}}""").GoAway);
        Assert.Equal("hel", GeminiLive.Read("""{"serverContent":{"interimInputTranscription":{"text":"hel"}}}""").Interim);
        Assert.Equal("Hello.", GeminiLive.Read("""{"serverContent":{"inputTranscription":{"text":"Hello."}}}""").Final);
        Assert.Equal(new GeminiLiveEvent(), GeminiLive.Read("<html>"));
        Assert.Equal(new GeminiLiveEvent(), GeminiLive.Read("""{"serverContent":{"turnComplete":true}}"""));
    }

    [Fact]
    public void Caption_joins_final_text_with_the_current_guess()
    {
        var caption = new GeminiLiveCaption();
        caption.Apply(new(Interim: "good mor"));
        Assert.Equal("good mor", caption.Text);
        Assert.True(caption.Pending);
        caption.Apply(new(Final: "Good morning."));
        caption.Apply(new(Interim: "how are"));
        Assert.Equal("Good morning. how are", caption.Text);
        caption.Apply(new(Final: "How are you?"));
        Assert.Equal("Good morning. How are you?", caption.Text);
        Assert.False(caption.Pending);
    }

    [Fact]
    public void Words_shown_as_a_guess_in_a_closed_utterance_are_not_repeated_in_the_next()
    {
        var caption = new GeminiLiveCaption();
        caption.Apply(new(Interim: "see you"));
        caption.Begin();
        caption.Apply(new(Final: "See you."));
        Assert.Equal("", caption.Text);
        caption.Apply(new(Final: "Bye."));
        Assert.Equal("Bye.", caption.Text);
    }
}
