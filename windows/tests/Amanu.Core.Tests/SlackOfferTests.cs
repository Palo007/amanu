using System.Text;
using System.Text.Json;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class SlackOfferTests
{
    private static readonly SlackSummarySettings Defaults = new();

    /// <summary>A recording folder that gets the card: a 25-minute Slack call today, summarized now.</summary>
    private static string Huddle(TemporaryDirectory temporary, string? family = "slack.exe", int seconds = 25 * 60, DateTimeOffset? started = null)
    {
        var directory = temporary.Path;
        File.WriteAllText(Path.Combine(directory, "meta.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["started"] = started ?? DateTimeOffset.Now.AddMinutes(-30),
            ["duration_seconds"] = seconds,
            ["process_family"] = family,
        }));
        File.WriteAllText(Path.Combine(directory, "summary.md"), "# Summary\n\nRelease moves to Thursday.\n");
        return directory;
    }

    [Fact]
    public void ASlackCallOfThreeMinutesOrMoreGetsTheCard()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Equal(SlackOfferDecision.Offer, SlackOffer.Decide(Huddle(temporary), Defaults, recordingNow: false));
    }

    [Fact]
    public void ACallFromAnotherAppGetsNoCard()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Equal(SlackOfferDecision.No, SlackOffer.Decide(Huddle(temporary, "Teams.exe"), Defaults, false));
    }

    [Fact]
    public void ARecordingWithNoAppGetsNoCard()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Equal(SlackOfferDecision.No, SlackOffer.Decide(Huddle(temporary, family: null), Defaults, false));
    }

    [Fact]
    public void TheAppMatchesWithoutExeAndInAnyCase()
    {
        Assert.True(SlackOffer.FromApp("Slack.EXE", ["slack"]));
        Assert.True(SlackOffer.FromApp("slack", ["slack.exe"]));
        Assert.False(SlackOffer.FromApp("slackbot.exe", ["slack.exe"]));
    }

    [Fact]
    public void ACallThatRangAndStoppedGetsNoCard()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Equal(SlackOfferDecision.No, SlackOffer.Decide(Huddle(temporary, seconds: 179), Defaults, false));
        File.Delete(Path.Combine(temporary.Path, "meta.json"));
        Assert.Equal(SlackOfferDecision.Offer, SlackOffer.Decide(Huddle(temporary, seconds: 180), Defaults, false));
    }

    [Theory]
    [InlineData("summary.stale")]
    [InlineData("summary.failed")]
    public void AStaleOrFailedSummaryGetsNoCard(string marker)
    {
        using var temporary = new TemporaryDirectory();
        var directory = Huddle(temporary);
        File.WriteAllText(Path.Combine(directory, marker), "");
        Assert.Equal(SlackOfferDecision.No, SlackOffer.Decide(directory, Defaults, false));
    }

    [Fact]
    public void NoSummaryGetsNoCard()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Huddle(temporary);
        File.Delete(Path.Combine(directory, "summary.md"));
        Assert.Equal(SlackOfferDecision.No, SlackOffer.Decide(directory, Defaults, false));
    }

    [Fact]
    public void ANewSummaryOfAnOldRecordingGetsNoCard()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Huddle(temporary, started: DateTimeOffset.Now.AddDays(-3));
        Assert.Equal(SlackOfferDecision.No, SlackOffer.Decide(directory, Defaults, false));
    }

    [Fact]
    public void TheCardNeverComesUpTwice()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Huddle(temporary);
        SlackOffer.Record(directory, copied: false, DateTimeOffset.Now);
        Assert.Equal(SlackOfferDecision.No, SlackOffer.Decide(directory, Defaults, false));

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, SlackOffer.FileName)));
        Assert.True(json.RootElement.GetProperty("shown").GetBoolean());
        Assert.False(json.RootElement.GetProperty("copied").GetBoolean());
        Assert.True(json.RootElement.TryGetProperty("time", out _));
    }

    [Fact]
    public void ACopyIsRecordedOverTheFirstRecord()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Huddle(temporary);
        SlackOffer.Record(directory, copied: false, DateTimeOffset.Now);
        SlackOffer.Record(directory, copied: true, DateTimeOffset.Now);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, SlackOffer.FileName)));
        Assert.True(json.RootElement.GetProperty("copied").GetBoolean());
        Assert.False(File.Exists(Path.Combine(directory, SlackOffer.FileName + ".tmp")));
    }

    [Fact]
    public void WhileRecordingTheCardWaits()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Equal(SlackOfferDecision.WaitForRecording, SlackOffer.Decide(Huddle(temporary), Defaults, recordingNow: true));
    }

    [Fact]
    public void SettingsTurnTheCardOffAndChangeTheRules()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Huddle(temporary, "Teams.exe", seconds: 90);
        Assert.Equal(SlackOfferDecision.No, SlackOffer.Decide(directory, new SlackSummarySettings { Enabled = false }, false));
        Assert.Equal(SlackOfferDecision.Offer, SlackOffer.Decide(directory,
            new SlackSummarySettings { Apps = ["slack.exe", "Teams.exe"], MinMinutes = 1 }, false));
    }

    [Fact]
    public void TheDefaultsAreOnSlackAndThreeMinutes()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", AppSettings.JsonOptions)!;
        Assert.True(settings.SlackSummary.Enabled);
        Assert.Equal(["slack.exe"], settings.SlackSummary.Apps);
        Assert.Equal(3, settings.SlackSummary.MinMinutes);
        var read = JsonSerializer.Deserialize<AppSettings>("""{"slack_summary":{"apps":["x.exe"],"min_minutes":5}}""", AppSettings.JsonOptions)!;
        Assert.Equal(["x.exe"], read.SlackSummary.Apps);
        Assert.Equal(5, read.SlackSummary.MinMinutes);
    }

    [Fact]
    public void SlackTextUsesSlackMarks()
    {
        var text = SlackOffer.SlackText("""
            # Huddle summary

            **Decisions:** the release moves to *Thursday*.

            ## Action items
            - [ ] Joseph Smith: send the [notes](https://example.com)
            - Michal: check the build
              - on both machines
            1. first
            ---
            """);
        Assert.Equal("""
            *Huddle summary*

            *Decisions:* the release moves to _Thursday_.

            *Action items*
            • Joseph Smith: send the notes (https://example.com)
            • Michal: check the build
                • on both machines
            1. first

            """.Replace("\r\n", "\n"), text);
    }

    [Fact]
    public void ThePreviewIsTheFirstLineAndTheActionItemCount()
    {
        var preview = SlackOffer.Preview("""
            # Summary
            **Decisions:** release moves to Thursday
            ## Action items
            - one
            - two
              - detail
            - three
            ## Risks
            - not an action
            """);
        Assert.Equal("Decisions: release moves to Thursday", preview.FirstLine);
        Assert.Equal(3, preview.ActionItems);
    }

    [Fact]
    public void ABoldLineOnItsOwnIsAHeadingForThePreview()
    {
        var preview = SlackOffer.Preview("**Úlohy**\n- a\n- b\n\n**Rozhodnutia**\n- c\n", maxLength: 10);
        Assert.Equal(2, preview.ActionItems);
        Assert.Equal("a", preview.FirstLine);
        Assert.Equal("123456789…", SlackOffer.Preview("1234567890123", maxLength: 10).FirstLine);
    }

    [Fact]
    public void ClipboardHtmlOffsetsPointAtTheFragmentInUtf8Bytes()
    {
        const string fragment = "<p><strong>Beneš</strong> – ok</p>";
        var wrapped = ClipboardHtml.Wrap(fragment);
        var bytes = Encoding.UTF8.GetBytes(wrapped);
        int Offset(string name)
        {
            var at = wrapped.IndexOf(name + ":", StringComparison.Ordinal) + name.Length + 1;
            return int.Parse(wrapped.Substring(at, 10));
        }
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment")));
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes, Offset("StartHTML"), 6));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
    }
}
