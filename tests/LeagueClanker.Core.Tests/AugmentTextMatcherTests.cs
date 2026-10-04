using LeagueClanker.Core.Augments;

namespace LeagueClanker.Core.Tests;

public class AugmentTextMatcherTests
{
    private static readonly AugmentCatalog Catalog = AugmentCatalog.ParseWikiModule("""
        return {
            ["Critical Rhythm"] = { ["description"] = "Your critical strikes grant attack speed.", ["tier"] = "Gold" },
            ["Recursion"] = { ["description"] = "Grants 60 ability haste.", ["tier"] = "Gold" },
            ["Celestial Body"] = { ["description"] = "Gain 1500 bonus health.", ["tier"] = "Gold" },
            ["Critical Missile"] = { ["description"] = "Grants critical strike chance.", ["tier"] = "Gold" },
            ["Deft"] = { ["description"] = "Grants 60% bonus attack speed.", ["tier"] = "Silver" },
            ["Stats on Stats!"] = { ["description"] = "Gain 3 Stat Bonus.", ["tier"] = "Gold" },
            ["Stats on Stats on Stats!"] = { ["description"] = "Gain 4 Stat Bonus.", ["tier"] = "Prismatic" },
            ["Quest: Wooglet's Witchcap"] = { ["description"] = "QUEST: Possess Rabadon's Deathcap and Zhonya's Hourglass. REWARD: Gain Wooglet's Witchcap.", ["tier"] = "Prismatic" },
            ["Mystic Punch"] = { ["description"] = "On-hit reduce the cooldown of your abilities.", ["tier"] = "Prismatic" },
            ["Triggered Inferno"] = { ["description"] = "Gain Daredevil Impulse.", ["tier"] = "Prismatic" },
        }
        """);

    [Fact]
    public void FindsTheThreeCardsLeftToRight()
    {
        var lines = new[]
        {
            Line("Celestial Body", x: 1300, y: 400),
            Line("Critical Rhythm", x: 500, y: 400),
            Line("Your critical strikes grant attack speed.", x: 480, y: 460),
            Line("Recursion", x: 900, y: 400),
        };

        var offer = AugmentTextMatcher.FindOffer(lines, Catalog);

        Assert.Equal(["Critical Rhythm", "Recursion", "Celestial Body"], offer.Select(d => d.Augment.Name));
    }

    [Fact]
    public void AClickOnACard_IsThatCard_ButTheRerollButtonsAreNot()
    {
        // Measured on samples/mayhem/offer-1440p.png: names at y=592, rerolls at y=1018, the reroll-all button at 1140.
        var cards = new[] { "Critical Rhythm", "Recursion", "Celestial Body" }.Select(n => Catalog.Find(n)!).ToList();
        var layout = new OfferLayout([new(cards[0], 798, 592), new(cards[1], 1288, 594), new(cards[2], 1778, 592)], ScreenHeight: 1440);

        Assert.Equal("Critical Rhythm", layout.CardAt(800, 800)?.Name); // the description, low on the card
        Assert.Equal("Recursion", layout.CardAt(1286, 300)?.Name); // the art, near the top
        Assert.Equal("Celestial Body", layout.CardAt(1960, 592)?.Name); // the right edge
        Assert.Null(layout.CardAt(801, 1018)); // its reroll button
        Assert.Null(layout.CardAt(1286, 1140)); // reroll all
        Assert.Null(layout.CardAt(1043, 592)); // between two cards
    }

    [Fact]
    public void TheNamesPosition_IsKeptForEachCard()
    {
        var offer = AugmentTextMatcher.FindOffer([Line("Recursion", x: 900, y: 400), Line("Deft", x: 1300, y: 400)], Catalog);

        Assert.All(offer, d => Assert.True(d.Y > 400, $"{d.Augment.Name} at {d.Y}")); // the middle of the line
    }

    [Fact]
    public void AQuestCard_IsReadWithoutItsQuestPrefix()
    {
        // The card says "Wooglet's Witchcap"; the wiki calls it "Quest: Wooglet's Witchcap". Only two cards were read.
        var offer = AugmentTextMatcher.FindOffer([Line("Wooglet's Witchcap", 300, 345), Line("Mystic Punch", 660, 345), Line("Triggered Inferno", 1030, 345)], Catalog);

        Assert.Equal(["Quest: Wooglet's Witchcap", "Mystic Punch", "Triggered Inferno"], offer.Select(d => d.Augment.Name));
    }

    [Fact]
    public void ToleratesOcrMistakesAndWrappedNames()
    {
        var lines = new[]
        {
            Line("CRITICA1 RHYTHM", x: 500, y: 400),
            Line("Recursi0n", x: 900, y: 400),
            Line("Celestial", x: 1300, y: 400),
            Line("Body", x: 1330, y: 432),
        };

        var offer = AugmentTextMatcher.FindOffer(lines, Catalog);

        Assert.Equal(["Critical Rhythm", "Recursion", "Celestial Body"], offer.Select(d => d.Augment.Name));
        Assert.All(offer, d => Assert.True(d.Confidence >= 0.8));
    }

    [Fact]
    public void ShortNamesMustMatchExactly()
    {
        Assert.Empty(AugmentTextMatcher.FindOffer([Line("Left", 500, 400)], Catalog));
        Assert.Single(AugmentTextMatcher.FindOffer([Line("Deft", 500, 400)], Catalog));
    }

    [Fact]
    public void PrefersTheLongestNameThatMatches()
    {
        var offer = AugmentTextMatcher.FindOffer([Line("Stats on Stats on Stats!", 500, 400)], Catalog);

        Assert.Equal("Stats on Stats on Stats!", Assert.Single(offer).Augment.Name);
    }

    [Fact]
    public void AcceptsOneCardOfTheNextTierAfterAGoldenReroll()
    {
        var lines = new[] { Line("Critical Rhythm", 500, 400), Line("Stats on Stats on Stats!", 900, 400), Line("Recursion", 1300, 400) };

        var offer = AugmentTextMatcher.FindOffer(lines, Catalog);

        Assert.Equal(["Critical Rhythm", "Stats on Stats on Stats!", "Recursion"], offer.Select(d => d.Augment.Name));
    }

    [Fact]
    public void IgnoresStrayNamesOfAnotherTier()
    {
        var lines = new[]
        {
            Line("Critical Rhythm", 500, 400), Line("Recursion", 900, 400), Line("Celestial Body", 1300, 400),
            Line("Deft", 100, 950), // e.g. someone typed it in chat
        };

        var offer = AugmentTextMatcher.FindOffer(lines, Catalog);

        Assert.Equal(3, offer.Count);
        Assert.DoesNotContain(offer, d => d.Augment.Name == "Deft");
    }

    private static TextLine Line(string text, double x, double y) => new(text, x, y, text.Length * 14, 28);
}
