using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.ItemSets;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

/// <summary>Arena, ARAM: Mayhem Classic and Swiftplay.</summary>
public class GameModesTests
{
    private static readonly ItemCatalog Items = ItemCatalog.Parse(JsonSerializer.Serialize(new
    {
        data = new Dictionary<string, object>
        {
            ["3031"] = Item("Infinity Edge", 3450, ["11", "12"]),
            ["223031"] = Item("Infinity Edge", 2500, ["30"]),
            ["223006"] = Item("Berserker's Greaves", 500, ["30"], tags: ["Boots"]),
            ["443054"] = Item("Darksteel Talons", 2750, ["30"]),
            ["3430"] = Item("Rite Of Ruin", 2500, ["11", "30"]),
            ["773031"] = Item("Infinity Edge", 3800, ["12", "453"]),
            ["663031"] = Item("Infinity Edge (another mode)", 3000, ["11"]),
        },
    }));

    [Fact]
    public void Arena_AndMayhemClassic_HaveTheirOwnShopAndAugments()
    {
        Assert.Equal(AugmentSet.Arena, GameMode.Arena.Augments());
        Assert.Equal(GameModes.ArenaMap, GameMode.Arena.MapId());
        Assert.Equal(AugmentSet.Mayhem, GameMode.MayhemClassic.Augments());
        Assert.True(GameMode.MayhemClassic.UsesClassicItems());
        Assert.Equal("KIWI_JADE", GameMode.MayhemClassic.ClientModeName());
        Assert.False(GameMode.Arena.HasLanes());
    }

    [Fact]
    public void ItemPools_FollowTheModesCopies()
    {
        Assert.Equal([3430, 223031, 443054], Items.LegendariesFor(GameMode.Arena).Select(i => i.Id)); // Arena copies, prismatics, and standard items sold there
        Assert.Equal([223006], Items.BootsFor(GameMode.Arena).Select(i => i.Id)); // Arena sells boots outright
        Assert.Equal([773031], Items.LegendariesFor(GameMode.MayhemClassic).Select(i => i.Id)); // classic items on Howling Abyss
        Assert.Equal([3031], Items.LegendariesFor(GameMode.AramMayhem).Select(i => i.Id));
        Assert.Equal([3031, 3430], Items.LegendariesFor(GameMode.SummonersRift).Select(i => i.Id)); // not the other mode's copy
    }

    [Fact]
    public void Arena_CountsTheWholeLobbyAsEnemies()
    {
        var game = TestData.Game(allies: [("Jinx", []), ("Braum", [])], enemies: [("Annie", []), ("Syndra", [])]);
        var arena = new AllGameData { ActivePlayer = game.ActivePlayer, AllPlayers = game.AllPlayers, GameData = new LiveGameInfo { GameMode = "CHERRY", MapNumber = 30 } };

        var analysis = GameAnalyzer.Analyze(arena, TestData.Static)!;

        Assert.Equal(GameMode.Arena, analysis.Mode);
        Assert.Empty(analysis.Allies.Players);
        Assert.Equal(3, analysis.Enemies.Players.Count); // your duo partner can't be told apart
    }

    [Fact]
    public void MayhemWithClassicItems_IsMayhemClassic()
    {
        var game = TestData.Game(allies: [("Jinx", [773031])], enemies: [("Annie", [])]);
        var mayhem = new AllGameData { ActivePlayer = game.ActivePlayer, AllPlayers = game.AllPlayers, GameData = new LiveGameInfo { GameMode = "KIWI", MapNumber = 12 } };

        Assert.Equal(GameMode.MayhemClassic, GameAnalyzer.DetectMode(mayhem));
    }

    [Fact]
    public void ArenaTiers_CanBeSilverTwice()
    {
        var rng = new Random(1);
        var mayhem = Enumerable.Range(0, 300).Select(_ => AugmentAdvisor.SampleTier(1, AugmentTier.Silver, rng)).ToList();
        var arena = Enumerable.Range(0, 300).Select(_ => AugmentAdvisor.SampleTier(1, AugmentTier.Silver, rng, AugmentSet.Arena)).ToList();

        Assert.DoesNotContain(AugmentTier.Silver, mayhem);
        Assert.Contains(AugmentTier.Silver, arena);
    }

    [Fact]
    public void Swiftplay_ReadsBothChampionsFromTheLobby()
    {
        var champions = new ChampionCatalog([
            new ChampionInfo("Garen", "Garen", ["Fighter"], 7, 7, 1, Key: 86),
            new ChampionInfo("Jinx", "Jinx", ["Marksman"], 9, 2, 4, Key: 222)]);
        var lobby = new Lobby
        {
            GameConfig = new LobbyGameConfig { GameMode = "SWIFTPLAY" },
            LocalMember = new LobbyMember
            {
                PlayerSlots = [new() { ChampionId = 86, PositionPreference = "TOP", Spell1 = 4, Spell2 = 12 }, new() { ChampionId = 222, PositionPreference = "BOTTOM" }],
            },
        };

        var state = ChampSelectState.From(new ClientSnapshot(null, null, lobby), champions)!;
        var second = state.ForSwiftplaySlot(1);

        Assert.True(state.IsSwiftplay);
        Assert.Equal(("Garen", Position.Top, (4, 12)), (state.Champion!.Name, state.Position, state.Spells));
        Assert.Equal(("Jinx", Position.Bottom, 1), (second.Champion!.Name, second.Position, second.SwiftplaySlot!.Value));
        Assert.NotEqual(state.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task SwiftplaySlot_IsWrittenWithTheOtherSlotUntouched()
    {
        var client = new FakeClient("""{"localMember": {"playerSlots": [{"championId": 86, "spell1": 4, "spell2": 12, "skinId": 86001}, {"championId": 222, "perks": "keep"}]}}""");
        var page = new RunePage(8000, 8400, [8010, 9111, 9105, 8299, 8473, 8451, 5005, 5008, 5001]);

        await new LeagueClientApi(new HttpClient(client) { BaseAddress = new Uri("https://127.0.0.1:1/") }).WriteSwiftplaySlotAsync(0, page, 4, 14, default);

        var slots = JsonNode.Parse(client.Put!)!.AsArray();
        var perks = JsonNode.Parse(slots[0]!["perks"]!.GetValue<string>())!;
        Assert.Equal("lol-lobby/v1/lobby/members/localMember/player-slots", client.PutPath);
        Assert.Equal(8000, perks["perkStyle"]!.GetValue<int>());
        Assert.Equal(9, perks["perkIds"]!.AsArray().Count);
        Assert.Equal(14, slots[0]!["spell2"]!.GetValue<int>());
        Assert.Equal(86001, slots[0]!["skinId"]!.GetValue<int>()); // fields we don't know stay
        Assert.Equal("keep", slots[1]!["perks"]!.GetValue<string>());
    }

    [Fact]
    public async Task ItemSet_IsMergedIntoYourSetsThroughTheClient()
    {
        var client = new FakeClient("""{"accountId": 7, "itemSets": [{"title": "Mine", "associatedChampions": [222], "blocks": []}]}""", summonerId: 55);
        var set = new ItemSetDefinition("LeagueClanker Jinx", 222, 11, [new ItemSetBlock("Core", [new ItemSetEntry(3031)])]);

        var result = await new LeagueClientApi(new HttpClient(client) { BaseAddress = new Uri("https://127.0.0.1:1/") }).WriteItemSetAsync(set, default);

        Assert.True(result.Success);
        Assert.Equal("lol-item-sets/v1/item-sets/55/sets", client.PutPath);
        Assert.Equal(["Mine", "LeagueClanker Jinx"], JsonNode.Parse(client.Put!)!["itemSets"]!.AsArray().Select(s => s!["title"]!.GetValue<string>()));
    }

    /// <summary>Answers GETs with one JSON body and records the PUT.</summary>
    private sealed class FakeClient(string body, long summonerId = 1) : HttpMessageHandler
    {
        public string? Put { get; private set; }
        public string? PutPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Method == HttpMethod.Put)
            {
                (Put, PutPath) = (await request.Content!.ReadAsStringAsync(ct), path);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var json = path == "lol-summoner/v1/current-summoner" ? $$"""{"summonerId": {{summonerId}}}""" : body;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static object Item(string name, int gold, string[] maps, string[]? tags = null) => new
    {
        name,
        description = "<mainText><stats><attention>60</attention> Attack Damage</stats></mainText>",
        gold = new { total = gold, purchasable = true },
        maps = maps.ToDictionary(m => m, _ => true),
        tags = tags ?? [],
        from = Array.Empty<string>(),
        into = Array.Empty<string>(),
    };
}
