using LeagueClanker.Core.Augments;

namespace LeagueClanker.Core.Tests;

public class AugmentTranslationsTests
{
    // Trimmed from Community Dragon's cherry-augments.json, English ("default") and German ("de_de").
    private const string English = """
        [{"id": 1205, "nameTRA": "ADAPt", "rarity": "kSilver"},
         {"id": 1, "nameTRA": "Adamant", "rarity": "kSilver"},
         {"id": 2, "nameTRA": "Blunt Force", "rarity": "kSilver"},
         {"id": 102, "nameTRA": "Blunt Force", "rarity": "kSilver"},
         {"id": 3, "nameTRA": "Sneakerhead", "rarity": "kPrismatic"}]
        """;

    private const string German = """
        [{"id": 1205, "nameTRA": "ADAPtieren"},
         {"id": 1, "nameTRA": "Unnachgiebig"},
         {"id": 2, "nameTRA": "Stumpfe Gewalteinwirkung"},
         {"id": 102, "nameTRA": "Stumpfe Gewalt"},
         {"id": 3, "nameTRA": "Sneakerhead"}]
        """;

    private static readonly AugmentCatalog Catalog = new AugmentCatalog(
    [
        Card("ADAPt"), Card("Adamant"), Card("Blunt Force"), Card("Quest: Sneakerhead", AugmentTier.Prismatic),
    ]).WithLocalNames(AugmentTranslations.Join(English, German));

    [Fact]
    public void Join_MatchesNamesOnTheCardId()
    {
        var names = AugmentTranslations.Join(English, German);

        Assert.Equal(["ADAPtieren"], names["adapt"]);
        Assert.Equal(["Stumpfe Gewalteinwirkung", "Stumpfe Gewalt"], names["bluntforce"]); // Arena and Mayhem word it differently
    }

    [Fact]
    public void WithLocalNames_FindsQuestsWithoutTheirPrefix()
    {
        Assert.Equal(["Sneakerhead"], Catalog.Find("Quest: Sneakerhead")!.LocalNames);
        Assert.Equal("Adamant", Catalog.Find("unnachgiebig")!.Name); // typing the German name works too
    }

    [Fact]
    public void FindOffer_ReadsCardsInTheClientsLanguage()
    {
        var lines = new[]
        {
            new TextLine("ADAPtieren", 100, 300, 120, 20),
            new TextLine("Unnachqiebig", 400, 300, 130, 20), // OCR slip: q for g
            new TextLine("Stumpfe Gewalt", 700, 300, 150, 20),
        };

        var offer = AugmentTextMatcher.FindOffer(lines, Catalog);

        Assert.Equal(["ADAPt", "Adamant", "Blunt Force"], offer.Select(d => d.Augment.Name));
    }

    [Fact]
    public void FindOffer_ReadsKoreanNames()
    {
        var korean = new AugmentCatalog([Card("ADAPt"), Card("Adamant"), Card("Blunt Force")]).WithLocalNames(new Dictionary<string, IReadOnlyList<string>>
        {
            ["adapt"] = ["적응형 능력치"], ["adamant"] = ["단호함"], ["bluntforce"] = ["육중한 힘"],
        });
        var lines = new[] { new TextLine("적응형 능력치", 100, 300, 120, 20), new TextLine("단호함", 400, 300, 60, 20), new TextLine("육중한 힘", 700, 300, 90, 20) };

        Assert.Equal(3, AugmentTextMatcher.FindOffer(lines, korean).Count);
    }

    [Fact]
    public void Folder_UsesCommunityDragonsNames()
    {
        Assert.Equal("de_de", AugmentTranslations.Folder("de_DE"));
        Assert.Equal("default", AugmentTranslations.Folder("en_GB"));
        Assert.True(AugmentTranslations.IsEnglish(null));
    }

    private static AugmentInfo Card(string name, AugmentTier tier = AugmentTier.Silver) => new() { Name = name, Tier = tier, Description = "" };
}
