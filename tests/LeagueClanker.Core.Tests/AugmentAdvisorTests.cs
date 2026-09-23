using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Augments;

namespace LeagueClanker.Core.Tests;

public class AugmentAdvisorTests
{
    private readonly AugmentAdvisor _advisor = new(TestAugments.Catalog, simulations: 400);

    [Fact]
    public void CardsThatFeedWhatYouPicked_RankHigher()
    {
        var ctx = Context("Jinx", picked: ["Rhythm"]);

        var advice = _advisor.Rank([TestAugments.Get("Wizard"), TestAugments.Get("Lucky")], ctx);

        Assert.Equal("Lucky", advice.Best.Augment.Name);
        Assert.Contains(advice.Best.Reasons, r => r.Text.StartsWith("pairs with Rhythm"));
    }

    [Fact]
    public void ChampionSpecificCards_OnlyScoreForChampionsThatCanUseThem()
    {
        var packLeader = TestAugments.Get("Pack Leader");
        var scorer = new AugmentScorer();
        var jinxReasons = new List<ScoreReason>();

        var forJinx = scorer.Value(packLeader, [], Context("Jinx"), jinxReasons);
        var forAnnie = scorer.Value(packLeader, [], Context("Annie"));

        Assert.True(forJinx < 0.1, $"Jinx has no pets, got {forJinx:0.00}");
        Assert.True(forAnnie > 0.5, $"Annie has Tibbers, got {forAnnie:0.00}");
        Assert.Contains(jinxReasons, r => r.Text.Contains("little use for Jinx"));
    }

    [Fact]
    public void LookAhead_PrefersTheCardThatSetsUpCombos()
    {
        var brute = TestAugments.Get("Brute");
        var quick = TestAugments.Get("Quick");

        var advice = _advisor.Rank([brute, quick], Context("Jinx"));
        var bruteOption = advice.Ranked.Single(o => o.Augment == brute);
        var quickOption = advice.Ranked.Single(o => o.Augment == quick);

        // Brute is worth a bit more on its own, but attack speed feeds the on-attack cards you can still get.
        Assert.True(bruteOption.Now > quickOption.Now);
        Assert.Equal(quick, advice.Best.Augment);
        Assert.Contains(quickOption.Partners, p => p.Name.StartsWith("Echo"));
        Assert.StartsWith("Take Quick", advice.Text);
        Assert.EndsWith("Brute is also ok.", advice.Text);
    }

    [Fact]
    public void Ranking_IsStableForTheSameOffer()
    {
        var offer = new[] { TestAugments.Get("Brute"), TestAugments.Get("Quick"), TestAugments.Get("Wizard") };

        var first = _advisor.Rank(offer, Context("Jinx"));
        var second = _advisor.Rank(offer, Context("Jinx"));

        Assert.Equal(first.Ranked.Select(o => o.Expected), second.Ranked.Select(o => o.Expected));
    }

    [Fact]
    public void ItemUpgrades_DependOnYourBuild()
    {
        var plated = TestAugments.Get("Plated");
        var scorer = new AugmentScorer();
        var ownedReasons = new List<ScoreReason>();

        var owned = scorer.Value(plated, [], Context("Garen", items: [TestData.Plate]), ownedReasons);
        var missing = scorer.Value(plated, [], Context("Garen"));

        Assert.True(owned > missing + 1);
        Assert.Contains(ownedReasons, r => r.Text == "upgrades your Plate");
    }

    [Fact]
    public void SecondSelection_IsNeverSilverAfterASilverFirst()
    {
        var rng = new Random(1);
        var second = Enumerable.Range(0, 2000).Select(_ => AugmentAdvisor.SampleTier(1, AugmentTier.Silver, rng)).ToList();
        var third = Enumerable.Range(0, 2000).Select(_ => AugmentAdvisor.SampleTier(2, AugmentTier.Silver, rng)).ToList();

        Assert.DoesNotContain(AugmentTier.Silver, second);
        Assert.Contains(AugmentTier.Silver, third);
    }

    private static AugmentContext Context(string champion, string[]? picked = null, int[]? items = null)
    {
        var game = TestData.Game(allies: [(champion, items ?? [])], enemies: [("Zed", []), ("Lux", [])]);
        return new AugmentContext(GameAnalyzer.Analyze(game, TestData.Static)!)
        {
            Picked = (picked ?? []).Select(TestAugments.Get).ToList(),
        };
    }
}
