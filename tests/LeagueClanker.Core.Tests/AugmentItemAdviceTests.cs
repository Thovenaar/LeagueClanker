using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;

namespace LeagueClanker.Core.Tests;

public class AugmentItemAdviceTests
{
    private static readonly (string, int[])[] FiveMages =
        [("Annie", []), ("Syndra", []), ("Lux", []), ("Brand", []), ("Xerath", [])];

    [Fact]
    public void PayoffAugment_BoostsTheItemsThatFeedIt()
    {
        var rec = Recommend(allies: [("Jinx", [])], enemies: FiveMages, augments: ["Rhythm"]);

        var situation = Assert.Single(rec.Situations, s => s.Label == "Rhythm");
        Assert.Equal("Your Rhythm augment pays off on crits", situation.Description);
        Assert.True(rec.Find(TestData.CritSword)!.PointsFor(situation) > 0.5);
        Assert.Contains(rec.Advice, a => a.Situation == situation);
    }

    [Fact]
    public void UpgradeAugment_PushesThatItemToTheTop()
    {
        (string, int[])[] balanced = [("Caitlyn", []), ("Annie", [])];
        var without = Recommend(allies: [("Garen", [])], enemies: balanced);
        var with = Recommend(allies: [("Garen", [])], enemies: balanced, augments: ["Plated"]);

        Assert.NotEqual(TestData.Plate, without.Items[0].Item.Id);
        Assert.Equal(TestData.Plate, with.Items[0].Item.Id);
        Assert.Contains(with.Situations, s => s.Description == "Your Plated augment upgrades Plate");
    }

    [Fact]
    public void CritFromAugments_CountsTowardTheCap()
    {
        int[] twoSwords = [TestData.CritSword, TestData.CritSword]; // 50% crit from items

        var without = Recommend(allies: [("Jinx", twoSwords)], enemies: FiveMages);
        var with = Recommend(allies: [("Jinx", twoSwords)], enemies: FiveMages, augments: ["Lucky"]); // +50% = capped

        Assert.True(with.Find(TestData.PenBow)!.BaseScore < without.Find(TestData.PenBow)!.BaseScore - 0.5,
            "Pen Bow's crit chance is wasted at 100% crit");
    }

    [Fact]
    public void AugmentThatAnswersAThreat_MakesItLessUrgent()
    {
        var without = Recommend(allies: [("Garen", [])], enemies: FiveMages);
        var with = Recommend(allies: [("Garen", [])], enemies: FiveMages, augments: ["Warded"]);

        Assert.True(VsAp(with, TestData.Cloak) < VsAp(without, TestData.Cloak));
    }

    [Fact]
    public void Advisor_PassesPickedAugmentsToTheAnalysis()
    {
        var game = TestData.Game(allies: [("Jinx", [])], enemies: FiveMages);
        var advisor = new BuildAdvisor(new FileGameDataSource("unused.json"), TestData.Static) { Augments = [TestAugments.Get("Rhythm")] };

        var rec = advisor.RecommendOnce(game)!;

        Assert.Contains(rec.Situations, s => s.Label == "Rhythm");
    }

    private static double VsAp(BuildRecommendation rec, int itemId) =>
        rec.Find(itemId)!.PointsFor(rec.Situations.Single(s => s.Label == "vs AP"));

    private static BuildRecommendation Recommend((string, int[])[] allies, (string, int[])[] enemies, string[]? augments = null)
    {
        var picked = (augments ?? []).Select(TestAugments.Get).ToList();
        var analysis = GameAnalyzer.Analyze(TestData.Game(allies, enemies), TestData.Static, picked)!;
        return new RecommendationEngine(TestData.Static).Recommend(analysis, maxItems: 10);
    }
}
