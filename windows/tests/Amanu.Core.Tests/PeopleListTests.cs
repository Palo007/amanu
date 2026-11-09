using Amanu.Core.Processing;
using Xunit;

namespace Amanu.Core.Tests;

public sealed class PeopleListTests
{
    private static readonly IReadOnlyList<Person> People = PeopleList.Parse(
    [
        "\uFEFF# who is on my calls",
        "",
        "Radoslav Benes, Rado, Radko",
        "Pavol Harvanka, Palo (me)",
        "Michal Novak, Mišo",
        "Michal Kral, Mišo",
        "Radoslav Benes, Radúz",
        "Andrea Sulikova",
    ]);

    [Fact]
    public void ParsesTheAmanuGeminiFormat()
    {
        Assert.Equal(["Radoslav Benes", "Pavol Harvanka", "Michal Novak", "Michal Kral", "Andrea Sulikova"],
            People.Select(person => person.FullName));
        Assert.Equal(["Rado", "Radko"], People[0].OtherNames);
        Assert.Empty(People[4].OtherNames);
    }

    [Fact]
    public void TheMeMarkerIsNotPartOfAName()
    {
        Assert.Equal("Pavol Harvanka", PeopleList.Me(People));
        Assert.Equal(["Palo"], People[1].OtherNames);
        Assert.Single(People, person => person.IsMe);
    }

    [Theory]
    [InlineData("Rado", "Radoslav Benes")]
    [InlineData("radko", "Radoslav Benes")]
    [InlineData("radoslav benes", "Radoslav Benes")]
    [InlineData(" Palo ", "Pavol Harvanka")]
    [InlineData("Mišo", "Mišo")]          // two people are called that: no guess
    [InlineData("Juraj", "Juraj")]        // not on the list
    public void FullNameForANickname(string given, string expected) =>
        Assert.Equal(expected, PeopleList.FullNameFor(given, People));

    [Fact]
    public void DescribesTheListForAPrompt()
    {
        var text = PeopleList.Describe(People);
        Assert.Contains("- Radoslav Benes (also called: Rado, Radko)", text);
        Assert.Contains("- Pavol Harvanka (also called: Palo) — the person recording", text);
        Assert.Contains("- Andrea Sulikova", text);
        Assert.Equal("", PeopleList.Describe([]));
    }

    [Fact]
    public void AnEmptySettingMeansTheDataFolder()
    {
        var data = Path.Combine(Path.GetTempPath(), "amanu-data");
        Assert.Equal(Path.Combine(data, "people.txt"), PeopleList.PathFor(null, data));
        Assert.Equal(Path.Combine(data, "people.txt"), PeopleList.PathFor("  ", data));
        Assert.Equal(Path.GetFullPath(@"C:\lists\people.txt"), PeopleList.PathFor(@"C:\lists\people.txt", data));
    }

    [Fact]
    public void AMissingFileIsAnEmptyList() =>
        Assert.Empty(PeopleList.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "people.txt")));
}
