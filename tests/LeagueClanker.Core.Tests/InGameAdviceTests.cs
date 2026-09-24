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
