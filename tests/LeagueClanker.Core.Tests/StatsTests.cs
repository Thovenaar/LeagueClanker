using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class StatsTests
{
    private static readonly ChampionStats Marksman = new()
    {
        Health = 630, HealthPerLevel = 105, AttackDamage = 60, AttackDamagePerLevel = 3,
        AttackSpeed = 0.625, AttackSpeedPerLevel = 2, MoveSpeed = 325, AttackRange = 525,
    };

    [Fact]
    public void Estimate_CombinesLevelGrowthAndItems()
    {
        var critSword = Item(TestData.CritSword);   // 65 AD, 25% crit
        var boots = Item(TestData.ArmorBoots);      // 25 armor, 45 move speed

        var stats = StatEstimator.Estimate(Marksman, level: 18, [critSword, boots]);

        Assert.Equal(60 + 3 * 17 + 65, stats.AttackDamage, precision: 1);
        Assert.Equal(65, stats.BonusAttackDamage);
        Assert.Equal(0.625 * (1 + 2 * 17 / 100.0), stats.AttackSpeed, precision: 3);
        Assert.Equal(25, stats.CritChance);
        Assert.Equal(325 + 45, stats.MoveSpeed);
        Assert.True(stats.IsRanged);
        Assert.False(stats.IsReal);
    }

    [Fact]
    public void Estimate_CapsCritAt100()
    {
        var sword = Item(TestData.CritSword);
        var stats = StatEstimator.Estimate(Marksman, 18, [sword, sword, sword, sword, sword]);

        Assert.Equal(100, stats.CritChance);
    }

    [Fact]
    public void RealStats_ReplaceTheEstimateForTheActivePlayer()
    {
        var game = TestData.Game(allies: [("Jinx", [TestData.CritSword])], enemies: [("Annie", [])]);
        var withReal = new AllGameData
        {
            ActivePlayer = new ActivePlayer
            {
                RiotId = "Me#TEST",
                ChampionStats = new LiveChampionStats { MaxHealth = 2222, Armor = 77, MagicResist = 44, AttackDamage = 199, AttackSpeed = 1.5 },
            },
            AllPlayers = game.AllPlayers,
        };

        var analysis = GameAnalyzer.Analyze(withReal, TestData.Static)!;

        Assert.True(analysis.Me.Stats.IsReal);
        Assert.Equal(2222, analysis.Me.Stats.Health);
        Assert.Equal(199, analysis.Me.Stats.AttackDamage);
        Assert.Equal(25, analysis.Me.Stats.CritChance); // percentages still come from items
        Assert.False(analysis.Enemies.Players[0].Stats.IsReal);
    }

    [Fact]
    public void RealStats_AreIgnoredWhileTheGameIsLoading()
    {
        var estimate = StatEstimator.Estimate(Marksman, 1, []);

        Assert.Same(estimate, estimate.WithRealStats(new LiveChampionStats())); // all zeros during the loading screen
    }

    [Fact]
    public void CritPastTheCap_IsWorthNothing()
    {
        var profile = ArchetypeProfiles.For(Archetype.Marksman);
        var sword = Item(TestData.CritSword);
        var noCrit = StatEstimator.Estimate(Marksman, 11, []);
        var fullCrit = StatEstimator.Estimate(Marksman, 11, [sword, sword, sword, sword]);

        Assert.True(profile.BaseScore(sword, fullCrit) < profile.BaseScore(sword, noCrit) - 1);
    }

    [Fact]
    public void CritRule_ReadsEnemyCritChance()
    {
        var rec = Recommend(allies: [("Garen", [])],
            enemies: [("Caitlyn", [TestData.CritSword, TestData.PenBow]), ("Annie", []), ("Ornn", []), ("Zed", []), ("Lux", [])]);

        var crit = Assert.Single(rec.Situations, s => s.Label == "vs crit");
        Assert.Contains("Caitlyn 50%", crit.Description);
    }

    [Fact]
    public void PenetrationRule_ReadsEnemyLethality()
    {
        var rec = Recommend(allies: [("Garen", [])],
            enemies: [("Zed", [TestData.LethBlade, TestData.LethBlade]), ("Annie", []), ("Ornn", []), ("Caitlyn", []), ("Lux", [])]);

        var pen = Assert.Single(rec.Situations, s => s.Label == "vs pen");
        Assert.Contains("Zed 36 lethality", pen.Description);
        Assert.Contains("armor does less for you", pen.Description);
    }

    private static ItemInfo Item(int id) => TestData.Static.Items.Get(id)!;

    private static BuildRecommendation Recommend((string, int[])[] allies, (string, int[])[] enemies) =>
        new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(TestData.Game(allies, enemies), TestData.Static)!);
}
