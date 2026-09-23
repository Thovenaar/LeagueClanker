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
    public void NewChampion_StartsAFreshPlan()
    {
        var planner = new BuildPlanner();
        planner.Update(Garen(AdTeam));
        planner.Update(Garen(ApTeam));

        planner.Update(Recommend([("Jinx", [])], ApTeam));

        Assert.Null(planner.PendingPivot);
        Assert.Equal(planner.Latest!.Items[0].Item.Id, planner.Upcoming[0].Item.Id);
    }

    private static BuildRecommendation Garen((string, int[])[] enemies, params int[] owned) =>
        Recommend([("Garen", owned)], enemies);

    private static BuildRecommendation Recommend((string, int[])[] allies, (string, int[])[] enemies) =>
        new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(TestData.Game(allies, enemies), TestData.Static)!);
}
