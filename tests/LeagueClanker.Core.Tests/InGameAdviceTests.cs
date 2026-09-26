using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class InGameAdviceTests
{
    // Black Cleaver's real recipe: Phage (Ruby Crystal + Long Sword + 350), Kindlegem, Pickaxe, plus 225 to combine.
    private const int LongSword = 1036, Ruby = 1028, Phage = 3044, Kindlegem = 3067, Pickaxe = 1037, Cleaver = 3071;

    private static readonly ItemCatalog Items = ItemCatalog.Parse(JsonSerializer.Serialize(new
    {
        data = new Dictionary<string, object>
        {
            [$"{LongSword}"] = Item("Long Sword", 350, into: [$"{Phage}"]),
            [$"{Ruby}"] = Item("Ruby Crystal", 400, into: [$"{Phage}", $"{Kindlegem}"]),
            [$"{Phage}"] = Item("Phage", 1100, from: [$"{Ruby}", $"{LongSword}"], into: [$"{Cleaver}"]),
            [$"{Kindlegem}"] = Item("Kindlegem", 800, from: [$"{Ruby}"], into: [$"{Cleaver}"]),
            [$"{Pickaxe}"] = Item("Pickaxe", 875, into: [$"{Cleaver}"]),
            [$"{Cleaver}"] = Item("Black Cleaver", 3000, from: [$"{Phage}", $"{Kindlegem}", $"{Pickaxe}"]),
        },
    }));

    private static ItemInfo Get(int id) => Items.Get(id)!;

    [Fact]
    public void Buy_TheFinishedItem_WhenYourPartsMakeItAffordable()
    {
        var advice = BuyAdvisor.Advise(Get(Cleaver), [Get(Phage), Get(Kindlegem)], gold: 1200, Items)!;

        Assert.Equal([Cleaver], advice.Items.Select(i => i.Id));
        Assert.Equal(1100, advice.Cost); // Pickaxe 875 + 225 to combine
        Assert.StartsWith("Buy Black Cleaver now: 1,100g", advice.Text);
    }

    [Fact]
    public void Buy_TheBiggestPartsThatFit()
    {
        var advice = BuyAdvisor.Advise(Get(Cleaver), [Get(LongSword)], gold: 1600, Items)!;

        // Phage costs 750 with your Long Sword; then Kindlegem (800) fits, Pickaxe (875) no longer does.
        Assert.Equal([Kindlegem, Phage], advice.Items.Select(i => i.Id));
        Assert.Equal(1550, advice.Cost);
        Assert.Contains("toward Black Cleaver (2,650g left)", advice.Text);
    }

    [Fact]
    public void Buy_PartsOfAPart_WhenTheWholePartDoesntFit()
    {
        var advice = BuyAdvisor.Advise(Get(Cleaver), [], gold: 500, Items)!;

        Assert.Equal([Ruby], advice.Items.Select(i => i.Id)); // Pickaxe (875) is too much; a Ruby Crystal from Phage fits
    }

    [Fact]
    public void Buy_SaysHowMuchToSaveUp_WhenNothingFits()
    {
        var advice = BuyAdvisor.Advise(Get(Cleaver), [], gold: 200, Items)!;

        Assert.Empty(advice.Items);
        Assert.StartsWith("Save up: 150g more for Long Sword", advice.Text);
    }

    [Fact]
    public void LateGame_SuggestsAControlWard_OnceTheBuildIsFull()
    {
        var game = FullBuildGame(minutes: 30);
        var rec = new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(game, TestData.Static)!);

        var tips = LateGameAdvisor.Advise(rec, gold: 800, TestData.Static.Items);

        Assert.Contains(tips, t => t.StartsWith("Carry a Control Ward"));
        Assert.All(tips, t => Assert.DoesNotContain("Elixir", t)); // TestData has no elixirs; the tip needs the item to exist
    }

    [Fact]
    public void LateGame_StaysQuiet_BeforeTheBuildIsDone()
    {
        var game = TestData.Game(allies: [("Garen", [TestData.Plate])], enemies: [("Annie", [])]);
        var rec = new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(game, TestData.Static)!);

        Assert.Empty(LateGameAdvisor.Advise(rec, gold: 5000, TestData.Static.Items));
    }

    [Fact]
    public void FullBuild_SuggestsSellingTheItemThatFitsTheGameLeast()
    {
        var rec = Recommend(FullBuildVsMages(TestData.Plate));

        var swap = Assert.Single(rec.Swaps.Take(1));
        Assert.Equal("Plate", swap.Sell.Item.Name); // armor against five mages
        Assert.Contains(swap.Buy.Item.Id, new[] { TestData.Cloak, TestData.Veil });
        Assert.True(swap.Gain >= RecommendationEngine.SellMargin);
    }

    [Fact]
    public void FullBuild_NeverSuggestsSwappingBack()
    {
        var before = Recommend(FullBuildVsMages(TestData.Plate)).Swaps[0];

        var after = Recommend(FullBuildVsMages(before.Buy.Item.Id));

        Assert.DoesNotContain(after.Swaps, s => s.Buy.Item.Id == TestData.Plate);
    }

    [Fact]
    public void FullBuild_KeepsAnItemYourAugmentUpgrades()
    {
        var plate = TestData.Static.Items.Get(TestData.Plate)!;
        var upgrade = new Augments.AugmentInfo { Name = "Upgrade Plate", Tier = Augments.AugmentTier.Silver, Description = "", MentionedItems = [plate] };

        var rec = new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(FullBuildVsMages(TestData.Plate), TestData.Static, augments: [upgrade])!);

        Assert.DoesNotContain(rec.Swaps, s => s.Sell.Item.Id == TestData.Plate);
    }

    [Fact]
    public void FullBuild_BuyNowSaysWhatToSell_AndWhatTheGoldBackPaysFor()
    {
        var rec = Recommend(FullBuildVsMages(TestData.Plate));
        var buy = rec.Swaps[0].Buy.Item;

        var now = BuyAdvisor.ForBuild(rec, rec.Items[0].Item, gold: 1000, TestData.Static.Items)!;
        var later = BuyAdvisor.ForBuild(rec, rec.Items[0].Item, gold: 0, TestData.Static.Items)!;

        // Plate costs 2,800 and sells for 70% of that.
        Assert.Equal($"Sell Plate (1,960g back) and buy {buy.Name} ({buy.TotalGold:N0}g) now.", now.Text);
        Assert.Equal($"Save up: {buy.TotalGold - 1960:N0}g more, then sell Plate (1,960g back) for {buy.Name} ({buy.TotalGold:N0}g).", later.Text);
    }

    [Fact]
    public void NotFull_HasNoSwaps_AndBuysParts()
    {
        var game = TestData.Game(allies: [("Garen", [TestData.Plate])], enemies: [("Annie", [])]);
        var rec = Recommend(game);

        Assert.False(rec.IsFull);
        Assert.Empty(rec.Swaps);
        Assert.Equal(BuyAdvisor.Advise(rec.Items[0].Item, rec.Game.Me.Items, 5000, TestData.Static.Items)?.Text,
            BuyAdvisor.ForBuild(rec, rec.Items[0].Item, 5000, TestData.Static.Items)?.Text);
    }

    [Fact]
    public void SellGold_ComesFromDataDragon_OrElse70Percent()
    {
        var items = ItemCatalog.Parse(JsonSerializer.Serialize(new
        {
            data = new Dictionary<string, object>
            {
                ["3161"] = new { name = "Spear of Shojin", description = "", gold = new { total = 3100, sell = 2170, purchasable = true }, maps = new Dictionary<string, bool> { ["11"] = true }, tags = Array.Empty<string>(), from = Array.Empty<string>(), into = Array.Empty<string>() },
                ["3071"] = Item("Black Cleaver", 3000),
            },
        }));

        Assert.Equal(2170, items.Get(3161)!.SellGold);
        Assert.Equal(2100, items.Get(3071)!.SellGold);
    }

    [Fact]
    public void PopularItems_NudgeTheRanking()
    {
        var game = TestData.Game(allies: [("Garen", [])], enemies: [("Annie", [])]);
        var plain = new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(game, TestData.Static)!);
        var heart = new HashSet<int> { TestData.Heart };
        var popular = new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(game, TestData.Static, popularItems: heart)!);

        Assert.True(popular.Find(TestData.Heart)!.Total > plain.Find(TestData.Heart)?.Total);
        Assert.Contains(popular.Situations, s => s.Label == "popular");
        Assert.DoesNotContain(plain.Situations, s => s.Label == "popular");
    }

    [Fact]
    public async Task Advisor_SendsAnUpdateWhenOnlyTheGoldChanges()
    {
        var snapshots = new Queue<AllGameData>([WithGold(500), WithGold(510), WithGold(900)]);
        var advisor = new BuildAdvisor(new QueueSource(snapshots), TestData.Static);
        var updates = new List<AdvisorUpdate>();

        await foreach (var update in advisor.RunAsync(TimeSpan.FromMilliseconds(1)))
        {
            updates.Add(update);
            if (snapshots.Count == 0)
                break;
        }

        Assert.Equal([500d, 900d], updates.Select(u => u.Gold)); // 510 is in the same 50-gold step
        Assert.Same(updates[0].Recommendation, updates[1].Recommendation); // same build, so the same recommendation
    }

    private static AllGameData WithGold(double gold)
    {
        var game = TestData.Game(allies: [("Garen", [])], enemies: [("Annie", [])]);
        return new AllGameData
        {
            ActivePlayer = new ActivePlayer { RiotId = game.ActivePlayer!.RiotId, CurrentGold = gold },
            AllPlayers = game.AllPlayers,
            GameData = game.GameData,
        };
    }

    private static BuildRecommendation Recommend(AllGameData game) =>
        new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(game, TestData.Static)!);

    /// <summary>A full Garen build against five mages, with <paramref name="defense"/> next to its magic resist.</summary>
    private static AllGameData FullBuildVsMages(int defense) =>
        TestData.Game(allies: [("Garen", [defense, TestData.Veil, TestData.Heart, TestData.Cleaver, TestData.WoundBlade, TestData.ArmorBoots])],
            enemies: [("Annie", []), ("Syndra", []), ("Lux", []), ("Brand", []), ("Xerath", [])]);

    private static AllGameData FullBuildGame(int minutes)
    {
        int[] build = [TestData.Plate, TestData.Cloak, TestData.Veil, TestData.Cleaver, TestData.Heart, TestData.ArmorBoots];
        var game = TestData.Game(allies: [("Garen", build)], enemies: [("Annie", []), ("Syndra", [])]);
        return new AllGameData { ActivePlayer = game.ActivePlayer, AllPlayers = game.AllPlayers, GameData = new LiveGameInfo { GameTime = minutes * 60 } };
    }

    private sealed class QueueSource(Queue<AllGameData> games) : IGameDataSource
    {
        public Task<AllGameData?> TryGetAsync(CancellationToken ct) => Task.FromResult(games.TryDequeue(out var game) ? game : null);
    }

    private static object Item(string name, int gold, string[]? from = null, string[]? into = null) => new
    {
        name,
        description = "<mainText><stats><attention>10</attention> Attack Damage</stats></mainText>",
        gold = new { total = gold, purchasable = true },
        maps = new Dictionary<string, bool> { ["11"] = true },
        tags = Array.Empty<string>(),
        from = from ?? [],
        into = into ?? [],
    };
}

public class ItemDirectionsTests
{
    [Fact]
    public void Alternatives_OfferOneItemPerDirection()
    {
        var game = TestData.Game([("Garen", [])], [("Annie", []), ("Syndra", []), ("Lux", []), ("Brand", []), ("Zed", [])]);
        var rec = new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(game, TestData.Static)!);

        var alternatives = ItemDirections.Alternatives(rec, rec.Ranked.Take(2).Select(s => s.Item.Id));

        Assert.NotEmpty(alternatives);
        Assert.Equal(alternatives.Count, alternatives.Select(a => a.Direction).Distinct().Count());
        Assert.DoesNotContain(alternatives, a => rec.Ranked.Take(2).Any(s => s.Item.Id == a.Item.Item.Id));
    }

    [Fact]
    public void Of_UsesTheSituationOrElseTheMainStats()
    {
        var heart = new ScoredItem(TestData.Static.Items.Get(TestData.Heart)!, 1, []);

        Assert.Equal("Tankier", ItemDirections.Of(heart));
    }
}
