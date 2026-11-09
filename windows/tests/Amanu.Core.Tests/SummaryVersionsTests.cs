using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class SummaryVersionsTests
{
    private static async Task<string> SessionAsync(TemporaryDirectory temporary, string transcript = "{}")
    {
        var directory = temporary.Path;
        await File.WriteAllTextAsync(Path.Combine(directory, "transcript.json"), transcript);
        return directory;
    }

    [Fact]
    public async Task ArchivingKeepsTheSummaryAndWhoWroteIt()
    {
        using var temporary = new TemporaryDirectory();
        var directory = await SessionAsync(temporary);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), "one");
        await SummaryVersions.WriteInfoAsync(directory, "claude-cli", "opus", DateTimeOffset.UtcNow.AddHours(-1));
        await SummaryVersions.ArchiveCurrentAsync(directory);
        await SummaryVersions.ArchiveCurrentAsync(directory); // Twice is once.
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), "two");
        await SummaryVersions.WriteInfoAsync(directory, "ollama", "llama", DateTimeOffset.UtcNow);

        var versions = await SummaryVersions.ReadAsync(directory);
        Assert.Equal(2, versions.Count);
        Assert.Equal(("ollama", true), (versions[0].Backend, versions[0].IsCurrent));
        Assert.Equal(("claude-cli", "opus", false), (versions[1].Backend, versions[1].Model, versions[1].IsCurrent));
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(versions[1].Directory, "summary.md")));
        Assert.Equal("Claude Code", versions[1].BackendName);
    }

    [Fact]
    public async Task AStaleSummaryIsNotArchivedAgainNorShownAsCurrent()
    {
        using var temporary = new TemporaryDirectory();
        var directory = await SessionAsync(temporary);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), "old");
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.stale"), "retranscribed");
        await SummaryVersions.ArchiveCurrentAsync(directory);
        Assert.False(Directory.Exists(Path.Combine(directory, SummaryVersions.Folder)));
        Assert.Empty(await SummaryVersions.ReadAsync(directory));
    }

    [Fact]
    public async Task ATranscriptArchiveTakesItsSummariesAndTheNewTranscriptStartsWithoutThem()
    {
        using var temporary = new TemporaryDirectory();
        var directory = await SessionAsync(temporary, """{"engine":"assemblyai"}""");
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), "a");
        await SummaryVersions.WriteInfoAsync(directory, "ollama", null, DateTimeOffset.UtcNow.AddMinutes(-2));
        await SummaryVersions.ArchiveCurrentAsync(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), "b");
        await SummaryVersions.WriteInfoAsync(directory, "ollama", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var oldId = SummaryVersions.TranscriptId(directory)!;

        await TranscriptVersions.ArchiveCurrentAsync(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "transcript.json"), """{"engine":"local"}""");
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.stale"), "retranscribed");

        var archived = await SummaryVersions.ReadAsync(Path.Combine(directory, "transcripts", oldId));
        Assert.Equal(["b", "a"], archived.Select(version => File.ReadAllText(Path.Combine(version.Directory, "summary.md"))));
        Assert.Empty(await SummaryVersions.ReadAsync(directory));
    }

    [Fact]
    public void RequestsWaitInTheFileInOrderAndLeaveOneByOne()
    {
        using var temporary = new TemporaryDirectory();
        var directory = temporary.Path;
        SummaryVersions.Request(directory, new SummaryRequest("ollama"));
        SummaryVersions.Request(directory, new SummaryRequest("claude-cli", "abc"));
        Assert.True(File.Exists(Path.Combine(directory, SummaryVersions.RequestFile)));
        Assert.Equal([new SummaryRequest("ollama"), new SummaryRequest("claude-cli", "abc")], SummaryVersions.ReadRequests(directory));

        SummaryVersions.Remove(directory, new SummaryRequest("ollama"));
        Assert.Equal([new SummaryRequest("claude-cli", "abc")], SummaryVersions.ReadRequests(directory));
        SummaryVersions.Remove(directory, new SummaryRequest("claude-cli", "abc"));
        Assert.False(File.Exists(Path.Combine(directory, SummaryVersions.RequestFile)));
    }

    [Fact]
    public void ARequestTargetsTheCurrentOrAnArchivedTranscriptAndNothingOutside()
    {
        var root = Path.Combine("C:", "rec");
        Assert.Equal(root, SummaryVersions.Target(root, new SummaryRequest("ollama")));
        Assert.Equal(Path.Combine(root, "transcripts", "abc"), SummaryVersions.Target(root, new SummaryRequest("ollama", "abc")));
        Assert.Equal(root, SummaryVersions.Target(root, new SummaryRequest("ollama", "..")));
        Assert.Equal(root, SummaryVersions.Target(root, new SummaryRequest("ollama", @"..\x")));
    }

    [Fact]
    public void ABrokenRequestFileIsNoRequest()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, SummaryVersions.RequestFile), "not json");
        Assert.Empty(SummaryVersions.ReadRequests(temporary.Path));
    }
}
