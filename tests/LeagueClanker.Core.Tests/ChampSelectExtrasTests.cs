using System.Net;
using System.Text.Json.Nodes;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.ItemSets;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.Spells;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

/// <summary>Summoner spells, skill order, item sets and snapshots: what champ select adds on top of runes.</summary>
public class ChampSelectExtrasTests
{
    private static readonly SummonerSpellCatalog Spells =
        SummonerSpellCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "summoner.json")));

    private static readonly ChampionInfo Jinx = new("Jinx", "Jinx", ["Marksman"], 9, 2, 4, Key: 222);
    private static readonly ChampionInfo LeeSin = new("LeeSin", "Lee Sin", ["Fighter", "Assassin"], 8, 5, 3, Key: 64);
    private static readonly ChampionInfo Janna = new("Janna", "Janna", ["Support", "Mage"], 3, 5, 7, Key: 40);

    private const int Flash = SummonerSpellCatalog.Flash;

    [Fact]
    public void Spells_KnowWhichModesTheyWorkIn()
    {
        Assert.Equal("Flash", Spells.Get(Flash)!.Name);
        Assert.True(Spells.IsAvailable(SummonerSpellCatalog.Smite, GameMode.SummonersRift));
        Assert.False(Spells.IsAvailable(SummonerSpellCatalog.Smite, GameMode.Aram));
        Assert.True(Spells.IsAvailable(SummonerSpellCatalog.Mark, GameMode.Aram));
        Assert.Equal(74, Spells.ForMode(Flash, GameMode.LeagueClassic)); // League Classic's own Flash
    }

    [Theory]
    [InlineData(Position.Jungle, SummonerSpellCatalog.Smite)]
    [InlineData(Position.Top, SummonerSpellCatalog.Teleport)]
    [InlineData(Position.Bottom, SummonerSpellCatalog.Heal)]
    [InlineData(Position.Middle, SummonerSpellCatalog.Ignite)]
    public async Task SpellRules_PairFlashWithTheRoleSpell(Position position, int expected)
    {
        var recommendation = await new SpellAdvisor(Spells).RecommendAsync(
            new SpellRequest(Jinx, Archetype.Marksman, position, GameMode.SummonersRift, (0, 0)), RuneSourceKind.OwnRules);

        Assert.Equal((Flash, expected), (recommendation!.First, recommendation.Second));
        Assert.Equal(RuleRuneSource.SourceName, recommendation.Source);
    }

    [Fact]
    public async Task SpellRules_UseMarkInAramAndClassicSpellsInLeagueClassic()
    {
        var advisor = new SpellAdvisor(Spells);

        var aram = await advisor.RecommendAsync(new SpellRequest(Jinx, Archetype.Marksman, Position.None, GameMode.Aram, (0, 0)), RuneSourceKind.OwnRules);
        var classic = await advisor.RecommendAsync(new SpellRequest(LeeSin, Archetype.Bruiser, Position.Jungle, GameMode.LeagueClassic, (0, 0)), RuneSourceKind.OwnRules);

        Assert.Equal(SummonerSpellCatalog.Mark, aram!.Second);
        Assert.Equal((74, 711), (classic!.First, classic.Second)); // Flash and Smite, the Jade copies
    }

    [Fact]
    public void KeepKeys_LeavesFlashWhereYouHaveIt()
    {
        Assert.Equal((Flash, 14), SpellAdvisor.KeepKeys(Flash, 14, (Flash, 7)));
        Assert.Equal((14, Flash), SpellAdvisor.KeepKeys(Flash, 14, (7, Flash)));   // Flash on F stays on F
        Assert.Equal((Flash, 14), SpellAdvisor.KeepKeys(Flash, 14, (0, 0)));
    }

    [Fact]
    public async Task SpellsFromOpgg_SkipPairsThatDontFitTheRole()
    {
        var opgg = new OpggClient(new HttpClient(new JsonHandler("""
            {"data": {"summoner_spells": [
                {"ids": [4, 11], "play": 900, "win": 450},
                {"ids": [4, 7], "play": 800, "win": 420}
            ]}}
            """)));

        var bottom = await new SpellAdvisor(Spells, opgg).RecommendAsync(
            new SpellRequest(Jinx, Archetype.Marksman, Position.Bottom, GameMode.SummonersRift, (0, 0)), RuneSourceKind.StatsSite);

        Assert.Equal((Flash, SummonerSpellCatalog.Heal), (bottom!.First, bottom.Second)); // the Smite pair is a jungle odd one out
        Assert.Equal(OpggRuneSource.SourceName, bottom.Source);
        Assert.Equal(800, bottom.Games);
    }

    [Fact]
    public void ParseChampion_ReadsTheBuildParts()
    {
        var champion = OpggClient.ParseChampion("""
            {"data": {
              "skill_masteries": [{"ids": ["Q", "W", "E"], "play": 600, "win": 330,
                                   "builds": [{"order": ["Q", "W", "E", "Q", "Q", "R", "Q"], "play": 400, "win": 220}]}],
              "starter_items": [{"ids": [1055, 2003], "play": 100, "win": 50}, {"ids": [1083, 2003, 2003], "play": 300, "win": 160}],
              "core_items": [{"ids": [6672, 3085, 3031], "play": 200, "win": 110}],
              "boots": [{"ids": [3006], "play": 900, "win": 470}]
            }}
            """);

        Assert.Equal(["Q", "W", "E"], champion.SkillOrder!.MaxOrder);
        Assert.Equal("R", champion.SkillOrder.Levels[5]);
        Assert.Equal([1083, 2003, 2003], champion.StarterItems[0].Ids); // most played first
        Assert.Equal([6672, 3085, 3031], champion.CoreItems[0].Ids);
        Assert.Equal(3006, champion.Boots[0].Ids.Single());
    }

    [Fact]
    public void ToGameData_BuildsAPreGameMatchWithYourRole()
    {
        var state = new ChampSelectState(LeeSin, Position.Jungle, GameMode.SummonersRift, [Janna], [Jinx]);

        var game = state.ToGameData()!;
        var me = game.AllPlayers.Single(p => p.RiotId == game.ActivePlayer!.RiotId);

        Assert.Equal("game_character_displayname_LeeSin", me.RawChampionName);
        Assert.True(me.HasSmite);
        Assert.Equal("JUNGLE", me.Position);
        Assert.Equal(3, game.AllPlayers.Count);
        Assert.All(game.AllPlayers, p => Assert.Equal(1, p.Level));
    }

    [Fact]
    public void ItemSet_HasStartersCoreBootsAndSituationalItems()
    {
        var game = TestData.Game(allies: [("Jinx", [])], enemies: [("Annie", []), ("Syndra", []), ("Lux", [])]);
        var rec = new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(game, TestData.Static)!, maxItems: 3);

        var set = ItemSetBuilder.Build(rec, TestData.Static.Items, opgg: null);

        Assert.StartsWith(ItemSetBuilder.TitlePrefix, set.Title);
        Assert.Equal(GameModes.SummonersRiftMap, set.MapId);
        Assert.Contains(set.Blocks, b => b.Title.Contains("core build"));
        Assert.Equal(rec.Items.Select(i => i.Item.Id), set.Blocks.Single(b => b.Title.Contains("core build")).Items.Select(i => i.Id));
        Assert.Contains(set.Blocks, b => b.Title == "Boots");
        Assert.All(set.Blocks, b => Assert.All(b.Items, i => Assert.NotNull(TestData.Static.Items.Get(i.Id))));
    }

    [Fact]
    public void Recommend_WorksBeforeAnyEnemyIsVisible()
    {
        // Blind pick: champ select builds the item set before the enemy picks show.
        var state = new ChampSelectState(LeeSin, Position.Jungle, GameMode.SummonersRift, [], []);
        var analysis = GameAnalyzer.Analyze(state.ToGameData()!, TestData.Static)!;

        var rec = new RecommendationEngine(TestData.Static).Recommend(analysis);

        Assert.NotEmpty(rec.Ranked);
    }

    [Fact]
    public void Merge_ReplacesOurOldSetButKeepsYours()
    {
        var existing = JsonNode.Parse("""
            {"accountId": 42, "timestamp": 1, "itemSets": [
              {"title": "My Jinx", "associatedChampions": [222], "uid": "mine", "blocks": []},
              {"title": "LeagueClanker Jinx", "associatedChampions": [222], "uid": "old", "blocks": []},
              {"title": "LeagueClanker Lux", "associatedChampions": [99], "uid": "lux", "blocks": []}
            ]}
            """)!;
        var set = new ItemSetDefinition("LeagueClanker Jinx", 222, 11, [new ItemSetBlock("Core", [new ItemSetEntry(3031)])]);

        var merged = ItemSetBuilder.Merge(existing, set);
        var sets = merged["itemSets"]!.AsArray();

        Assert.Equal(42, merged["accountId"]!.GetValue<int>());
        Assert.Equal(["mine", "lux"], sets.Take(2).Select(s => s!["uid"]!.GetValue<string>()));
        Assert.Equal("3031", sets[2]!["blocks"]![0]!["items"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(3, existing["itemSets"]!.AsArray().Count); // the input isn't changed
    }

    [Fact]
    public void Anonymize_ReplacesNamesConsistently()
    {
        var json = Snapshots.Anonymize("""
            {"activePlayer": {"riotId": "Faker#KR1", "summonerName": "Faker#KR1"},
             "allPlayers": [
               {"riotId": "Faker#KR1", "riotIdGameName": "Faker", "riotIdTagLine": "KR1", "championName": "Ahri"},
               {"riotId": "Someone#EUW", "championName": "Jinx", "summonerId": 123456}
             ]}
            """);
        var root = JsonNode.Parse(json)!;

        Assert.DoesNotContain("Faker", json);
        Assert.DoesNotContain("Someone", json);
        Assert.Equal(root["activePlayer"]!["riotId"]!.GetValue<string>(), root["allPlayers"]![0]!["riotId"]!.GetValue<string>());
        Assert.Equal("Player1", root["allPlayers"]![0]!["riotIdGameName"]!.GetValue<string>());
        Assert.Equal("Ahri", root["allPlayers"]![0]!["championName"]!.GetValue<string>());
        Assert.Equal(0, root["allPlayers"]![1]!["summonerId"]!.GetValue<int>());
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
