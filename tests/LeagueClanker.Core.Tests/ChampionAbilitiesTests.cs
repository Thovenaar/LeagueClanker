using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class ChampionAbilitiesTests
{
    // Trimmed from Data Dragon's championFull.json: tooltips carry markup, the passive only a description.
    private const string ChampionFull = """
        {"data": {
          "Newbie": {"id": "Newbie",
            "passive": {"description": "Newbie's third attack <b>stuns</b> the target."},
            "spells": [
              {"tooltip": "Deals <magicDamage>80 magic damage</magicDamage> and heals Newbie for <healing>40 Health</healing>.", "description": ""},
              {"tooltip": "Gains a <shield>120 Shield</shield>.", "description": ""},
              {"tooltip": "Dashes and <status>Knocks</status> <status>Back</status> enemies.", "description": ""},
              {"tooltip": "<status>Roots</status> everyone nearby and restores <healing>10% missing Health</healing>.", "description": ""}
            ]},
          "Leonalike": {"id": "Leonalike",
            "passive": {"description": "Damage marks the target."},
            "spells": [
              {"tooltip": "Next attack <status>Slows</status>.", "description": "Shield of Daybreak: her next attack slows."},
              {"tooltip": "Gains Armor.", "description": ""},
              {"tooltip": "Deals <trueDamage>50 true damage</trueDamage>.", "description": ""},
              {"tooltip": "Calls down a beam.", "description": ""}
            ]},
          "Jungler": {"id": "Jungler",
            "passive": {"description": "4 seconds after dying, Jungler explodes, dealing true damage to surrounding enemies."},
            "spells": [
              {"tooltip": "Bites, dealing <trueDamage>300 true damage</trueDamage> to monsters and minions.", "description": ""},
              {"tooltip": "For 5 seconds, he suffers <trueDamage>2% current Health true damage</trueDamage>.", "description": ""}
            ]}
        }}
        """;

    [Fact]
    public void Parse_ReadsEachAbilitysEffectsFromTheMarkup()
    {
        var newbie = ChampionAbilities.Parse(ChampionFull).Single(a => a.ChampionId == "Newbie");

        Assert.Equal("QR", newbie.Healing);
        Assert.Equal("W", newbie.Shielding);
        Assert.Equal("ERP", newbie.HardCrowdControl); // split status tags and the passive's plain text count too
        Assert.Equal(ChampionTraits.Healer | ChampionTraits.HeavyCrowdControl, newbie.Traits); // one shield isn't enough
    }

    [Fact]
    public void Parse_IgnoresSlowsAndAbilityNames()
    {
        var other = ChampionAbilities.Parse(ChampionFull).Single(a => a.ChampionId == "Leonalike");

        Assert.Equal("", other.HardCrowdControl);
        Assert.Equal("", other.Shielding); // "Shield of Daybreak" is a name, not a shield
        Assert.Equal(ChampionTraits.TrueDamage, other.Traits);
    }

    [Fact]
    public void Parse_OnlyCountsTrueDamageAgainstChampions()
    {
        var jungler = ChampionAbilities.Parse(ChampionFull).Single(a => a.ChampionId == "Jungler");

        Assert.Equal("", jungler.TrueDamage); // monsters, self damage and after-death explosions don't count
    }

    [Fact]
    public void WithAbilities_AddsToTheHandKeptTraits()
    {
        var catalog = new ChampionCatalog(
        [
            new ChampionInfo("Newbie", "Newbie", ["Fighter"], 7, 5, 3),
            new ChampionInfo("Soraka", "Soraka", ["Support"], 2, 5, 7),
        ]);

        var merged = catalog.WithAbilities(ChampionAbilities.Parse(ChampionFull));

        Assert.False(catalog.Get("Newbie")!.Has(ChampionTraits.Healer));
        Assert.True(merged.Get("Newbie")!.Has(ChampionTraits.Healer | ChampionTraits.HeavyCrowdControl));
        Assert.True(merged.Get("Soraka")!.Has(ChampionTraits.Healer)); // from the hand-kept list, no ability data needed
    }
}
