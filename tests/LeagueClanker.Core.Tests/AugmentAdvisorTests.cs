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

    // Reroll advice compares a card with what a reroll could give, so it needs a pool the size of the real one
    // (about 70 cards per tier). In a tiny pool a card you pass on keeps coming back, and every choice looks equal.
    private static readonly AugmentCatalog BigPool = BuildBigPool();
    private readonly AugmentAdvisor _bigAdvisor = new(BigPool, simulations: 400);

    [Fact]
    public void Rerolls_KeepTheBestCardAndRerollTheRest()
    {
        var ctx = BigPoolContext(picked: ["Rhythm"]);

        var advice = _bigAdvisor.Rank([Card("Lucky"), Card("Pack Leader"), Card("Plated")], ctx);
        var reroll = advice.Reroll!;

        Assert.Equal(RerollAction.Keep, reroll.For(Card("Lucky"))!.Action);
        Assert.Equal(RerollAction.Reroll, reroll.For(Card("Pack Leader"))!.Action);
        Assert.Equal(RerollAction.Reroll, reroll.For(Card("Plated"))!.Action);
        Assert.StartsWith("Keep Lucky. Reroll ", reroll.Text);
    }

    [Fact]
    public void Rerolls_EvenTheBestCardWhenItIsWeak()
    {
        // Jinx has no pets and no Plate: both are worse than almost anything a reroll gives.
        var advice = _bigAdvisor.Rank([Card("Pack Leader"), Card("Plated")], BigPoolContext());
        var reroll = advice.Reroll!;

        var keeper = reroll.Cards[0];
        Assert.True(keeper.Action == RerollAction.RerollLast,
            $"{keeper.Action}; " + string.Join(", ", advice.Ranked.Select(o => $"{o.Augment.Name} now {o.Now:0.00} exp {o.Expected:0.00} beats {reroll.For(o.Augment)!.RerollBeatsIt:P0}")));
        Assert.True(keeper.RerollBeatsIt > 0.5, $"a reroll beats it {keeper.RerollBeatsIt:P0}");
        Assert.All(reroll.Cards.Skip(1), c => Assert.Equal(RerollAction.Reroll, c.Action));
        Assert.Contains("reroll it too", reroll.Text);
    }

    [Fact]
    public void Rerolls_SkipCardsThatWereAlreadyRerolled()
    {
        var packLeader = Card("Pack Leader");
        var plated = Card("Plated");

        var oneLeft = _bigAdvisor.Rank([packLeader, plated], BigPoolContext(), rerolled: new HashSet<AugmentInfo> { plated });
        var noneLeft = _bigAdvisor.Rank([packLeader, plated], BigPoolContext(), rerolled: new HashSet<AugmentInfo> { packLeader, plated });

        Assert.Equal(RerollAction.AlreadyRerolled, oneLeft.Reroll!.For(plated)!.Action);
        Assert.Null(noneLeft.Reroll);
    }

    [Fact]
    public void GoldenReroll_OnACardYouWontTake_IsAlwaysUsed()
    {
        var ctx = BigPoolContext(picked: ["Rhythm"]);
        var state = new RerollState { Golden = Card("Pack Leader") };

        var reroll = _bigAdvisor.Rank([Card("Lucky"), Card("Pack Leader"), Card("Plated")], ctx, state).Reroll!;

        Assert.Equal(RerollAction.Keep, reroll.For(Card("Lucky"))!.Action);
        Assert.Equal(RerollAction.GoldenReroll, reroll.For(Card("Pack Leader"))!.Action);
        Assert.Equal(RerollAction.Reroll, reroll.For(Card("Plated"))!.Action);
        Assert.Equal("Keep Lucky. Golden-reroll Pack Leader (it becomes a Prismatic card) and reroll Plated: you won't take them, so a reroll can only help.", reroll.Text);
    }

    [Fact]
    public void GoldenReroll_OnAWeakBestCard_IsUsedLast()
    {
        var offer = new[] { Card("Pack Leader"), Card("Plated") };
        var keeper = _bigAdvisor.Rank(offer, BigPoolContext()).Best.Augment;

        var reroll = _bigAdvisor.Rank(offer, BigPoolContext(), new RerollState { Golden = keeper }).Reroll!;

        Assert.Equal(RerollAction.GoldenRerollLast, reroll.For(keeper)!.Action);
        Assert.Contains("golden-reroll it too", reroll.Text);
    }

    [Fact]
    public void ExtraRerolls_KeepACardRerollableAfterOneReroll()
    {
        var plated = Card("Plated");
        var state = new RerollState { RerollsPerCard = 2, Used = new Dictionary<AugmentInfo, int> { [plated] = 1 } };

        var reroll = _bigAdvisor.Rank([Card("Lucky"), Card("Pack Leader"), plated], BigPoolContext(picked: ["Rhythm"]), state).Reroll!;

        Assert.Equal(RerollAction.Reroll, reroll.For(plated)!.Action);
        Assert.Contains("up to 2 times each", reroll.Text);
    }

    private static AugmentInfo Card(string name) => BigPool.Find(name)!;

    private static AugmentContext BigPoolContext(string[]? picked = null)
    {
        var game = TestData.Game(allies: [("Jinx", [])], enemies: [("Zed", []), ("Lux", [])]);
        return new AugmentContext(GameAnalyzer.Analyze(game, TestData.Static)!) { Picked = (picked ?? []).Select(Card).ToList() };
    }

    /// <summary>A few strong cards for a marksman and many forgettable ones, in every tier.</summary>
    private static AugmentCatalog BuildBigPool()
    {
        var cards = new List<string>
        {
            """["Lucky"] = { ["description"] = "Grants 50% critical strike chance.", ["tier"] = "Gold" }""",
            """["Rhythm"] = { ["description"] = "Your critical strikes grant you 6% bonus attack speed.", ["tier"] = "Gold" }""",
            """["Swift"] = { ["description"] = "Grants 40% bonus attack speed.", ["tier"] = "Gold" }""",
            """["Keen"] = { ["description"] = "Increases attack damage by 25%.", ["tier"] = "Gold" }""",
            """["Pack Leader"] = { ["description"] = "Your pets deal 40% increased damage.", ["tier"] = "Gold" }""",
            """["Plated"] = { ["description"] = "Upgrades Plate, granting 50 armor.", ["tier"] = "Gold" }""",
        };
        foreach (var tier in new[] { "Silver", "Gold", "Prismatic" })
            for (var i = 1; i <= 25; i++)
                cards.Add($$"""["{{(tier == "Gold" ? "Guard" : tier)}} {{i}}"] = { ["description"] = "Grants 10 bonus magic resistance.", ["tier"] = "{{tier}}" }""");
        return AugmentCatalog.ParseWikiModule($"return {{ {string.Join(", ", cards)} }}", TestData.Static.Items);
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
