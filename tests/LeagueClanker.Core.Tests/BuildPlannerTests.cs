using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Recommendation;

namespace LeagueClanker.Core.Tests;

public class BuildPlannerTests
{
    private static readonly (string, int[])[] AdTeam =
        [("Zed", []), ("Caitlyn", []), ("Aatrox", []), ("Jinx", []), ("Thresh", [])];

    private static readonly (string, int[])[] ApTeam =
        [("Annie", []), ("Syndra", []), ("Lux", []), ("Brand", []), ("Xerath", [])];

    [Fact]
    public void FirstRecommendation_IsAdoptedWithoutAsking()
    {
        var planner = new BuildPlanner();
        var rec = Garen(AdTeam);

        planner.Update(rec);

        Assert.Null(planner.PendingPivot);
        Assert.Equal(rec.Items.Select(i => i.Item.Id), planner.Upcoming.Select(i => i.Item.Id));
    }

    [Fact]
    public void EnemyTurningAp_SuggestsAPivotWithTheReason()
    {
        var planner = new BuildPlanner();
        planner.Update(Garen(AdTeam));

        planner.Update(Garen(ApTeam));

        var pivot = planner.PendingPivot!;
        Assert.Contains(pivot.Add, i => i.Id is TestData.Cloak or TestData.Veil);
        Assert.Contains(pivot.Reasons, r => r.Contains("AP") && r.EndsWith("(new)"));
        Assert.StartsWith("Swap ", pivot.Summary);
    }

    [Fact]
    public void Accept_AppliesTheSwap()
    {
        var planner = new BuildPlanner();
        planner.Update(Garen(AdTeam));
        planner.Update(Garen(ApTeam));
        var pivot = planner.PendingPivot!;

        planner.Accept();

        var next = planner.Upcoming.Take(BuildPlanner.NearTerm).Select(i => i.Item.Id).ToList();
        Assert.Null(planner.PendingPivot);
        Assert.All(pivot.Add, added => Assert.Contains(added.Id, next));
        Assert.All(pivot.Drop, dropped => Assert.DoesNotContain(dropped.Id, next));

        planner.Update(Garen(ApTeam)); // the next poll: the swap you just took isn't suggested again
        Assert.Null(planner.PendingPivot);
    }

    [Fact]
    public void Decline_StaysQuiet_ButSwitchingIsStillPossible()
    {
        var planner = new BuildPlanner();
        planner.Update(Garen(AdTeam));
        planner.Update(Garen(ApTeam));
        var planBefore = planner.Upcoming.Select(i => i.Item.Id).ToList();

        planner.Decline();
        planner.Update(Garen(ApTeam)); // next poll, same situation

        Assert.Null(planner.PendingPivot);
        Assert.Equal(planBefore, planner.Upcoming.Select(i => i.Item.Id));
        Assert.True(planner.CanSwitch);

        planner.SwitchToLatest();

        Assert.Equal(planner.Latest!.Items[0].Item.Id, planner.Upcoming[0].Item.Id);
        Assert.False(planner.CanSwitch);
    }

    [Fact]
    public void NewReasonAfterDecline_SuggestsOnlyTheNewItems()
    {
        var planner = new BuildPlanner();
        planner.Update(Garen(AdTeam));
        planner.Update(Garen(ApTeam));
        var declined = planner.PendingPivot!.Add.Select(i => i.Id).ToList();
        planner.Decline();

        // Healers show up: anti-heal is a new reason with a new item.
        planner.Update(Garen([("Soraka", []), ("Vladimir", []), ("Aatrox", []), ("Syndra", []), ("Lux", [])]));

        var pivot = planner.PendingPivot!;
        Assert.Contains(pivot.Add, i => i.Id == TestData.WoundBlade);
        Assert.DoesNotContain(pivot.Add, i => declined.Contains(i.Id));
    }

    [Fact]
    public void BuyingFromThePlan_IsNotAPivot()
    {
        var planner = new BuildPlanner();
        planner.Update(Garen(AdTeam));
        var first = planner.Upcoming[0].Item.Id;

        planner.Update(Garen(AdTeam, owned: first));

        Assert.Null(planner.PendingPivot);
        Assert.DoesNotContain(planner.Upcoming, i => i.Item.Id == first);
        Assert.Equal(BuildPlanner.PlanLength, planner.Upcoming.Count);
    }

    [Fact]
    public void NextPurchases_FollowTheLatestOrder_ButPromotionsNeedAPivot()
    {
        var game = GameAnalyzer.Analyze(TestData.Game([("Garen", [])], ApTeam), TestData.Static)!;
        var planner = new BuildPlanner();
        planner.Update(Ranking(game, TestData.Cloak, TestData.Veil, TestData.Cleaver, TestData.Plate, TestData.WoundBlade, TestData.Heart));

        // Same three items up front, new order (e.g. an augment made Cleaver the priority): no pivot needed.
        planner.Update(Ranking(game, TestData.Cleaver, TestData.Cloak, TestData.Veil, TestData.Plate, TestData.WoundBlade, TestData.Heart));

        Assert.Null(planner.PendingPivot);
        Assert.Equal([TestData.Cleaver, TestData.Cloak, TestData.Veil], planner.Upcoming.Take(3).Select(i => i.Item.Id));

        // Plate jumping from fourth to first is a change in what you buy next: it stays put until you accept.
        planner.Update(Ranking(game, TestData.Plate, TestData.Cleaver, TestData.Cloak, TestData.Veil, TestData.WoundBlade, TestData.Heart));

        Assert.Equal([TestData.Cleaver, TestData.Cloak, TestData.Veil], planner.Upcoming.Take(3).Select(i => i.Item.Id));
        Assert.True(planner.CanSwitch);
    }

    [Fact]
    public void CloseScores_DontSwapTheOrderBackAndForth()
    {
        var game = GameAnalyzer.Analyze(TestData.Game([("Garen", [])], ApTeam), TestData.Static)!;
        var planner = new BuildPlanner();
        planner.Update(Scored(game, (TestData.Cloak, 10), (TestData.Veil, 9.8), (TestData.Cleaver, 9)));

        // Veil edges ahead by 0.1, as Rabadon's does when your AP ticks up: not enough to change what you buy first.
        planner.Update(Scored(game, (TestData.Veil, 10.1), (TestData.Cloak, 10), (TestData.Cleaver, 9)));
        Assert.Equal(TestData.Cloak, planner.Upcoming[0].Item.Id);

        planner.Update(Scored(game, (TestData.Veil, 10.6), (TestData.Cloak, 10), (TestData.Cleaver, 9)));
        Assert.Equal(TestData.Veil, planner.Upcoming[0].Item.Id);
    }

    [Fact]
    public void AStartedItem_IsFinishedFirst()
    {
        // Garen owns the 800g Wound Dagger, over a quarter of Wound Blade's 3,000g.
        var game = GameAnalyzer.Analyze(TestData.Game([("Garen", [TestData.WoundComponent])], ApTeam), TestData.Static)!;
        var planner = new BuildPlanner { Items = TestData.Static.Items };

        planner.Update(Ranking(game, TestData.Cloak, TestData.Veil, TestData.Cleaver, TestData.Plate, TestData.WoundBlade, TestData.Heart));

        Assert.Equal(TestData.WoundBlade, planner.Upcoming[0].Item.Id);
    }

    /// <summary>A recommendation with a fixed ranking and no situational reasons.</summary>
    private static BuildRecommendation Ranking(GameAnalysis game, params int[] itemIds) =>
        Scored(game, itemIds.Select((id, i) => (id, 10.0 - i)).ToArray());

    private static BuildRecommendation Scored(GameAnalysis game, params (int Id, double Score)[] items)
    {
        var ranked = items.Select(x => new ScoredItem(TestData.Static.Items.Get(x.Id)!, x.Score, [])).OrderByDescending(s => s.Total).ToList();
        return new BuildRecommendation(game, ranked.Take(BuildPlanner.PlanLength).ToList(), null, [], []) { Ranked = ranked };
    }

    [Fact]
    public void NewChampion_StartsAFreshPlan()
    {
        var planner = new BuildPlanner();
        planner.Update(Garen(AdTeam));
        planner.Update(Garen(ApTeam));

        planner.Update(Recommend([("Jinx", [])], ApTeam));

        Assert.Null(planner.PendingPivot);
        Assert.Equal(planner.Latest!.Items[0].Item.Id, planner.Upcoming[0].Item.Id);
    }

    // Garen with a marksman teammate: he's the team's only frontline, like in a real game.
    private static BuildRecommendation Garen((string, int[])[] enemies, params int[] owned) =>
        Recommend([("Garen", owned), ("Jinx", [])], enemies);

    private static BuildRecommendation Recommend((string, int[])[] allies, (string, int[])[] enemies) =>
        new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(TestData.Game(allies, enemies), TestData.Static)!);
}
