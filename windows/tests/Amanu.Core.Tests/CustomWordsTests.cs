using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class CustomWordsTests
{
    private static IReadOnlyList<TranscriptSegment> One(string text) => [new TranscriptSegment(1000, 2000, text, "me")];

    private static (string Text, IReadOnlyList<CustomWordCorrection> Found) Run(string text, params string[] words)
    {
        var (segments, found) = CustomWords.Correct(One(text), CustomWords.Parse(words));
        return (segments[0].Text, found);
    }

    [Fact]
    public void EmptyListChangesNothing()
    {
        var input = One("We talked to Zorbatrix today.");
        var (segments, found) = CustomWords.Correct(input, []);
        Assert.Same(input, segments);
        Assert.Empty(found);
    }

    [Fact]
    public void CloseSpellingOfOneNameIsCorrected()
    {
        var (text, found) = Run("We talked to Zorbatrics today.", "Zorbatrix");
        Assert.Equal("We talked to Zorbatrix today.", text);
        Assert.Single(found);
        Assert.Equal(1000, found[0].StartMs);
    }

    [Fact]
    public void TwoHeardWordsBecomeOneName()
    {
        var (text, _) = Run("Open the quill feather, please.", "Quillfeather");
        Assert.Equal("Open the Quillfeather, please.", text);
    }

    [Fact]
    public void PunctuationAndCaseAroundTheWordStay()
    {
        var (text, _) = Run("\"zorbatrics!\" she said.", "Zorbatrix");
        Assert.Equal("\"Zorbatrix!\" she said.", text);
    }

    [Theory]
    [InlineData("The doctor is late.", "Zorbatrix")]       // too far away
    [InlineData("A bit of luck.", "Lick")]                 // entry under five letters
    [InlineData("Then Xorbatrix came.", "Zorbatrix")]      // other first letter
    [InlineData("Zorbatrax came.", "Zorbatrix")]           // other consonants? same skeleton, so corrected: guard below
    public void StrictThresholds(string input, string word)
    {
        var (text, found) = Run(input, word);
        if (input.StartsWith("Zorbatrax")) Assert.Single(found);
        else
        {
            Assert.Equal(input, text);
            Assert.Empty(found);
        }
    }

    [Fact]
    public void CaseEndingIsKept()
    {
        var (text, _) = Run("Hovoril som so zorbatricsom.", "Zorbatrix");
        Assert.Equal("Hovoril som so Zorbatrixom.", text);
    }

    [Fact]
    public void VoicedAndUnvoicedConsonantsAreOneSound()
    {
        var (text, _) = Run("It was Madko again.", "Matko");
        Assert.Equal("It was Matko again.", text);
    }

    [Fact]
    public void WordsAcrossACommaAreNotJoined()
    {
        var (text, found) = Run("Open quill, feather now.", "Quillfeather");
        Assert.Equal("Open quill, feather now.", text);
        Assert.Empty(found);
    }

    [Fact]
    public void ExactEntryIsLeftAlone()
    {
        var (_, found) = Run("Zorbatrix is here.", "Zorbatrix");
        Assert.Empty(found);
    }

    [Fact]
    public void ParseDropsCommentsDoublesAndLongEntries()
    {
        var list = CustomWords.Parse(["# names", "", "  Alpha  ", "alpha", "one two three four five six", "bad[x]", "Beta"]);
        Assert.Equal(["Alpha", "Beta"], list);
    }

    [Fact]
    public void DigestIsEmptyForNoWordsAndChangesWithTheList()
    {
        Assert.Equal("", CustomWords.Digest([]));
        Assert.NotEqual(CustomWords.Digest(["a"]), CustomWords.Digest(["b"]));
    }
}
