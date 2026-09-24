using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;
using static LeagueClanker.Core.Tests.TestData;

namespace LeagueClanker.Core.Tests;

public class ItemScalingTests
{
    // Passive text as Data Dragon writes it for patch 16.19.
    private static readonly ItemCatalog Items = ItemCatalog.Parse(JsonSerializer.Serialize(new
    {
        data = new Dictionary<string, object>
        {
            ["3089"] = Item("Rabadon's Deathcap", 3500, Stats(("Ability Power", "130"))
                + "<passive>Magical Opus</passive><br>Increases your total <scaleAP>Ability Power by 30%</scaleAP>."),
            ["4633"] = Item("Riftmaker", 3100, Stats(("Ability Power", "70"), ("Health", "350"))
                + "<passive>Void Corruption</passive><br>For each second in combat with enemy champions, deal 2% bonus damage, up to 8%.<br><br>"
                + "<passive>Void Infusion</passive><br>Gain 2% of your <scaleHealth>bonus Health</scaleHealth> as <scaleAP>Ability Power</scaleAP>."),
            ["3083"] = Item("Warmog's Armor", 3100, Stats(("Health", "1000"))
                + "<passive>Warmog's Vitality</passive><br>Gain bonus Health equal to 12% of your Item Health (<healing>0</healing>)."),
            ["6657"] = Item("Rod of Ages", 2600, Stats(("Ability Power", "50"), ("Health", "400"), ("Mana", "400"))
                + "<passive>Timeless</passive><br>This item gains <scaleHealth>10 Health</scaleHealth>, <scaleMana>30 Mana</scaleMana> and "
                + "<scaleAP>3 Ability Power</scaleAP> every 60 seconds up to 10 times. Upon reaching max stacks, gain a level."),
            ["6665"] = Item("Jak'Sho, The Protean", 3200, Stats(("Health", "350"), ("Armor", "45"), ("Magic Resist", "45"))
                + "<passive>Voidborn Resilience</passive><br>After 5 seconds of champion combat, increase your bonus <scaleArmor>Armor</scaleArmor> and "
                + "<scaleMR>Magic Resist</scaleMR> by 30% until end of combat."),
            ["3032"] = Item("Yun Tal Wildarrows", 3000, Stats(("Attack Damage", "55"), ("25%", "Attack Speed"))
                + "<passive>Practice Makes Lethal</passive><br>On-Attack, gain Critical Strike Chance permanently up to 25%."),
        },
    }));

    private static ItemInfo Get(string name) => Items.All.Single(i => i.Name == name);

    [Fact]
    public void Parse_ReadsMultipliersConversionsAndStacks()
    {
        Assert.Equal(new ItemScaling("Magical Opus", Stat.AbilityPower, 0.3, ScalingSource.TotalAbilityPower), Get("Rabadon's Deathcap").Scaling.Single());
        Assert.Equal(new ItemScaling("Void Infusion", Stat.AbilityPower, 0.02, ScalingSource.BonusHealth), Get("Riftmaker").Scaling.Single());
        Assert.Equal(new ItemScaling("Warmog's Vitality", Stat.Health, 0.12, ScalingSource.BonusHealth), Get("Warmog's Armor").Scaling.Single());
        Assert.Equal([(Stat.Health, 100.0), (Stat.Mana, 300.0), (Stat.AbilityPower, 30.0)],
            Get("Rod of Ages").Scaling.Select(s => (s.Stat, s.Amount)));
        Assert.Equal([ScalingSource.BonusArmor, ScalingSource.BonusMagicResist], Get("Jak'Sho, The Protean").Scaling.Select(s => s.Source));
        Assert.Equal(new ItemScaling("Practice Makes Lethal", Stat.CritChance, 25, ScalingSource.Stacked), Get("Yun Tal Wildarrows").Scaling.Single());
    }

    [Fact]
    public void Rabadons_IsWorthMoreTheMoreAbilityPowerYouHave()
    {
        var rabadon = Get("Rabadon's Deathcap");
        var mage = ArchetypeProfiles.For(Archetype.Mage);

        Assert.Equal(130 + 0.3 * (200 + 130), rabadon.StatsFor(new StatBlock { AbilityPower = 200 })[Stat.AbilityPower], 3);
        Assert.True(mage.BaseScore(rabadon, new StatBlock { AbilityPower = 300 }) > mage.BaseScore(rabadon, new StatBlock { AbilityPower = 0 }) + 0.5);
        Assert.Equal("Magical Opus: +99 AP", rabadon.Scaling[0].Note(rabadon, new StatBlock { AbilityPower = 200 }));
    }

    [Fact]
    public void Estimate_AddsPassivesToTheItemStats()
    {
        var champion = new ChampionStats { Health = 600, HealthPerLevel = 0 };

        var stats = StatEstimator.Estimate(champion, 1, [Get("Riftmaker"), Get("Rabadon's Deathcap"), Get("Warmog's Armor")]);

        var bonusHealth = (350 + 1000) * 1.12;                     // Warmog's counts first
        Assert.Equal(600 + bonusHealth, stats.Health, 3);
        Assert.Equal((70 + 130 + 0.02 * bonusHealth) * 1.3, stats.AbilityPower, 3); // Rabadon's multiplies Riftmaker's AP too
    }

    [Fact]
    public void Engine_ExplainsWhatAScalingPassiveAdds()
    {
        var data = new StaticGameData("test", Items, Static.Champions);
        var game = Game(allies: [("Annie", [4633, 6657])], enemies: [("Garen", [])]);

        var rec = new RecommendationEngine(data).Recommend(GameAnalyzer.Analyze(game, data)!);

        var rabadon = rec.Find(3089)!;
        Assert.Contains(rabadon.Effects, e => e.StartsWith("Magical Opus: +", StringComparison.Ordinal));
    }
}
