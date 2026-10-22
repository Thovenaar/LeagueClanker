using LeagueClanker.Core;
using LeagueClanker.Core.Augments;

namespace LeagueClanker.Core.Tests;

public class AugmentSupplementTests
{
    // Trimmed from Community Dragon's cherry-augments.json on patch 16.21: Mayhem ids start at 1000.
    private const string RiotList = """
        [
          { "id": 1201, "nameTRA": "BONK!", "rarity": "kGold" },
          { "id": 3085, "nameTRA": "Flow of Spirit", "rarity": "kSilver" },
          { "id": 1170, "nameTRA": "Scoped Weapons", "rarity": "kSilver" },
          { "id": 1777, "nameTRA": "Twice", "rarity": "kSilver" },
          { "id": 1778, "nameTRA": "Twice", "rarity": "kGold" },
          { "id": 388, "nameTRA": "Chroma Flux", "rarity": "kGold" },
          { "id": 341, "nameTRA": "Gain an Augment slot", "rarity": "kEventChoice" }
        ]
        """;

    private static readonly AugmentCatalog Wiki = AugmentCatalog.ParseWikiModule("""
        return {
            ["BONK!"] = { ["description"] = "Basic attacks and abilities deal bonus damage.", ["tier"] = "Silver" },
            ["Flow of Spirit"] = { ["description"] = "Casting the upgraded ability grants 60 bonus attack speed.", ["tier"] = "Gold" },
            ["Scoped Weapons"] = { ["description"] = "Gain 100 attack range.", ["tier"] = "Silver" },
            ["Twice"] = { ["description"] = "Gain 10 ability haste.", ["tier"] = "Prismatic" },
        }
        """);

    [Fact]
    public void RiotTiers_ReadOneSet_AndSkipNamesInTwoTiers()
    {
        var mayhem = AugmentSupplement.RiotTiers(RiotList, AugmentSet.Mayhem);

        Assert.Equal(AugmentTier.Gold, mayhem["bonk"]);
        Assert.False(mayhem.ContainsKey("twice")); // Silver and Gold: can't tell which the wiki means
        Assert.False(mayhem.ContainsKey("chromaflux")); // an Arena card
        Assert.Equal(AugmentTier.Gold, AugmentSupplement.RiotTiers(RiotList, AugmentSet.Arena)["chromaflux"]);
    }

    [Fact]
    public void Apply_UsesRiotsTier_WhereTheWikiDiffers()
    {
        var catalog = AugmentSupplement.Apply(Wiki, AugmentSet.Mayhem, AugmentSupplement.RiotTiers(RiotList, AugmentSet.Mayhem));

        Assert.Equal(AugmentTier.Gold, catalog.Find("BONK!")!.Tier); // the wiki says Silver
        Assert.Equal(AugmentTier.Silver, catalog.Find("Flow of Spirit")!.Tier);
        Assert.Equal(AugmentTier.Prismatic, catalog.Find("Twice")!.Tier);
        Assert.Equal(4, catalog.All.Count); // no Arena cards added to Mayhem
    }

    [Fact]
    public void Apply_KeepsTheWikisTiers_WithoutRiotsList()
    {
        var catalog = AugmentSupplement.Apply(Wiki, AugmentSet.Mayhem, riotTiers: null);

        Assert.Equal(AugmentTier.Silver, catalog.Find("BONK!")!.Tier);
    }

    [Fact]
    public void Apply_AddsArenaCardsTheWikiLacks_UntilItHasThem()
    {
        var empty = AugmentCatalog.ParseWikiModule("return { }");
        var added = AugmentSupplement.Apply(empty, AugmentSet.Arena, riotTiers: null);
        Assert.Equal(AugmentTier.Prismatic, added.Find("Death Dealer")!.Tier);
        Assert.NotNull(added.Find("got that dog in em"));

        var wikiHasIt = AugmentCatalog.ParseWikiModule("""
            return { ["Death Dealer"] = { ["description"] = "The wiki's own text.", ["tier"] = "Prismatic" } }
            """);
        Assert.Equal("The wiki's own text.", AugmentSupplement.Apply(wikiHasIt, AugmentSet.Arena, null).Find("Death Dealer")!.Description);
    }

    [Theory]
    [InlineData("Ultimate Recursion", "Gain 50 ultimate haste.", AugmentEffect.AbilityHaste, AugmentTrigger.Ultimate)]
    [InlineData("Arcane Axiom", "Scoring a takedown against an enemy champion within 3 seconds of damaging them refunds 15 to 30 of your ultimate ability's total cooldown.", AugmentEffect.AbilityHaste, AugmentTrigger.Takedowns)]
    [InlineData("Fruitify", "Enemy champions that you score a takedown against drop Honeyfruit. You and allied champions can step over a Honeyfruit to consume it, heal for 6% of maximum health and restoring 6% of maximum mana.", AugmentEffect.Heal, AugmentTrigger.Takedowns)]
    public void Tag_ReadsThe1621CardsTheWordingHides(string name, string description, AugmentEffect gives, AugmentTrigger needs)
    {
        var card = AugmentTagger.Tag(name, new() { ["description"] = description, ["tier"] = "Gold" }, null);

        Assert.True(card.Effects.HasFlag(gives));
        Assert.True(card.Triggers.HasFlag(needs));
        Assert.False(card.Triggers.HasFlag(AugmentTrigger.Mana)); // Fruitify restores mana; it doesn't scale with it
    }

    [Fact]
    public void Tag_DoesNotCountDyingAsAPayoff_ForHeavyIsTheCrown()
    {
        var card = AugmentTagger.Tag("Heavy Is The Crown", new()
        {
            ["description"] = "Scoring a takedown against an enemy champion generates a stack of Burden. Upon death, you lose half of your stacks.",
            ["tier"] = "Gold",
        }, null);

        Assert.False(card.Triggers.HasFlag(AugmentTrigger.Death));
        Assert.True(card.Triggers.HasFlag(AugmentTrigger.Takedowns));
    }
}
