using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

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

    [Fact]
    public void WithTwoSlotsLeft_OnlyYourNextTwoItemsCanChange()
    {
        // Three legendaries and boots leave room for two more. A new third item would never show up in the window,
        // so accepting a swap of it looked like the build stayed the same.
        var game = GameAnalyzer.Analyze(TestData.Game([("Garen", [TestData.LethBlade, TestData.PenBow, TestData.CritSword, TestData.ArmorBoots])], ApTeam), TestData.Static)!;
        var planner = new BuildPlanner();
        planner.Update(Ranking(game, TestData.Cloak, TestData.Veil, TestData.Cleaver, TestData.Plate, TestData.WoundBlade, TestData.Heart));

        planner.Update(Ranking(game, TestData.Cloak, TestData.Veil, TestData.Plate, TestData.Cleaver, TestData.WoundBlade, TestData.Heart));

        Assert.Equal(2, planner.Latest!.SlotsLeft);
        Assert.False(planner.CanSwitch);
        Assert.Null(planner.PendingPivot);
    }

    [Fact]
    public void AnAcceptedPivot_ShowsUpInTheSlotsYouHaveLeft()
    {
        var planner = new BuildPlanner();
        int[] owned = [TestData.LethBlade, TestData.PenBow, TestData.CritSword, TestData.ArmorBoots];
        planner.Update(Garen(AdTeam, owned));
        planner.Update(Garen(ApTeam, owned));
        var pivot = planner.PendingPivot!;

        planner.Accept();

        var shown = planner.Upcoming.Take(planner.Latest!.SlotsLeft).Select(i => i.Item.Id).ToList();
        Assert.All(pivot.Add, added => Assert.Contains(added.Id, shown));
        Assert.All(pivot.Drop, dropped => Assert.DoesNotContain(dropped.Id, shown));
    }

    [Fact]
    public void PromotingAnItemYouAlreadyPlanned_SaysWhatItPushesBack()
    {
        // Kai'Sa owned a Recurve Bow and more: Guinsoo's Rageblade and Wit's End were both started. Her plan was
        // Guinsoo's > Nashor's > Wit's End > Blade of the Ruined King, and "Add Blade of the Ruined King to your next items"
        // only moved it up one place, so accepting looked like it did nothing.
        const int Recurve = 1043, Tome = 1052, Rage = 3124, Wits = 3091, Nashors = 3115, Botrk = 3153, Statikk = 3087, Terminus = 3302;
        var data = new StaticGameData("test", ItemCatalog.Parse(JsonSerializer.Serialize(new
        {
            data = new Dictionary<string, object>
            {
                [$"{Recurve}"] = TestData.Item("Recurve Bow", 1000, TestData.Stats(("25%", "Attack Speed")), into: [$"{Rage}", $"{Wits}"]),
                [$"{Tome}"] = TestData.Item("Amplifying Tome", 500, TestData.Stats(("Ability Power", "20")), into: [$"{Rage}"]),
                [$"{Rage}"] = TestData.Item("Guinsoo's Rageblade", 3000, TestData.Stats(("30%", "Attack Speed")), from: [$"{Recurve}", $"{Tome}"]),
                [$"{Wits}"] = TestData.Item("Wit's End", 3000, TestData.Stats(("50%", "Attack Speed")), from: [$"{Recurve}"]),
                [$"{Nashors}"] = TestData.Item("Nashor's Tooth", 3000, TestData.Stats(("Ability Power", "80"))),
                [$"{Botrk}"] = TestData.Item("Blade of The Ruined King", 3200, TestData.Stats(("Attack Damage", "40"))),
                [$"{Statikk}"] = TestData.Item("Statikk Shiv", 2900, TestData.Stats(("Attack Damage", "45"))),
                [$"{Terminus}"] = TestData.Item("Terminus", 3000, TestData.Stats(("Attack Damage", "30"))),
            },
        })), TestData.Static.Champions);
        var game = GameAnalyzer.Analyze(TestData.Game([("Jinx", [Recurve, Tome])], ApTeam), data)!;
        ScoredItem Plain(int id, double score) => new(data.Items.Get(id)!, score, []);
        BuildRecommendation Recommend(List<ScoredItem> ranked) => new(game, ranked.Take(6).ToList(), null, [Answer], []) { Ranked = ranked };
        var planner = new BuildPlanner { Items = data.Items };
        planner.Update(Recommend([Plain(Rage, 10), Plain(Nashors, 9), Plain(Wits, 8), Plain(Botrk, 7), Plain(Statikk, 6), Plain(Terminus, 5)]));

        // The Ruined King now answers something real and ranks ahead of Wit's End.
        planner.Update(Recommend([Plain(Rage, 10), Plain(Nashors, 9), new(data.Items.Get(Botrk)!, 8.5, [new(Answer, 1.0)]),
            Plain(Statikk, 6), Plain(Terminus, 5), Plain(Wits, 4)]));

        var pivot = planner.PendingPivot!;
        Assert.Equal("Buy Blade of The Ruined King before Wit's End", pivot.Summary); // Wit's End is started, so it stays
        planner.Accept();
        Assert.Equal([Nashors, Rage, Botrk], planner.Upcoming.Take(3).Select(i => i.Item.Id).Order());
        Assert.Contains(planner.Upcoming, i => i.Item.Id == Wits); // pushed back, not dropped
        Assert.Null(planner.PendingPivot);
    }

    private static readonly Situation Answer = new("healing", "Enemy heals a lot", 1, _ => 1);

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
