using Amanu.Core.Processing;
using Xunit;

namespace Amanu.Core.Tests;

public sealed class PeopleListTests
{
    private static readonly IReadOnlyList<Person> People = PeopleList.Parse(
    [
        "\uFEFF# who is on my calls",
        "",
        "Joseph Smith, Joe, Jojo",
        "Pavol Harvanka, Palo (me)",
        "Michal Novak, Mišo",
        "Michal Kral, Mišo",
        "Joseph Smith, Joey",
        "Andrea Sulikova",
    ]);

    [Fact]
    public void ParsesTheAmanuGeminiFormat()
    {
        Assert.Equal(["Joseph Smith", "Pavol Harvanka", "Michal Novak", "Michal Kral", "Andrea Sulikova"],
            People.Select(person => person.FullName));
        Assert.Equal(["Joe", "Jojo"], People[0].OtherNames);
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
    [InlineData("Joe", "Joseph Smith")]
    [InlineData("jojo", "Joseph Smith")]
    [InlineData("joseph smith", "Joseph Smith")]
    [InlineData(" Palo ", "Pavol Harvanka")]
    [InlineData("Mišo", "Mišo")]          // two people are called that: no guess
    [InlineData("Juraj", "Juraj")]        // not on the list
    public void FullNameForANickname(string given, string expected) =>
        Assert.Equal(expected, PeopleList.FullNameFor(given, People));

    [Fact]
    public void DescribesTheListForAPrompt()
    {
        var text = PeopleList.Describe(People);
        Assert.Contains("- Joseph Smith (also called: Joe, Jojo)", text);
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
