using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.History;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class HistoryTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 20, 0, 0);

    [Fact]
    public void Recorder_WritesARecapWithTheBuildThePivotsAndTheResult()
    {
        var recorder = new GameRecorder();
        recorder.Observe(Recommend(minutes: 10, result: null, items: []));
        recorder.Pivot("Swap Cloak for Plate", accepted: false);
        recorder.Observe(Recommend(minutes: 28, result: "Win", items: [TestData.Plate, TestData.Cleaver]));

        var recap = recorder.Finish(Now)!;

        Assert.Equal(("Garen", true), (recap.ChampionName, recap.Win));
        Assert.Equal(28 * 60, recap.DurationSeconds);
        Assert.Equal([TestData.Plate, TestData.Cleaver], recap.FinalItems);
        Assert.Equal(600, recap.Pivots.Single().GameTime);
        Assert.False(recap.Pivots.Single().Accepted);
        Assert.Equal(recap.FinalItems.Count(recap.AdvisedItems.Contains), recap.AdvisedAndBuilt);
        Assert.Null(recorder.Finish(Now)); // the recorder starts over
    }

    [Fact]
    public void Recorder_SkipsShortGames()
    {
        var recorder = new GameRecorder();
        recorder.Observe(Recommend(minutes: 3, result: null, items: []));

        Assert.Null(recorder.Finish(Now));
    }

    [Fact]
    public void Store_KeepsRecapsNewestFirstAcrossRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lc-games-{Guid.NewGuid()}.json");
        try
        {
            var store = new RecapStore(path);
            store.Add(Recap("Garen", 86, true, Now.AddDays(-1)));
            store.Add(Recap("Jinx", 222, false, Now));

            var reopened = new RecapStore(path).Games;

            Assert.Equal(["Jinx", "Garen"], reopened.Select(g => g.ChampionName));
            Assert.Equal(GameMode.SummonersRift, reopened[0].Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stats_CountYourChampionsAndLaneOpponents()
    {
        var champions = new ChampionCatalog([new ChampionInfo("Darius", "Darius", ["Fighter"], 9, 5, 1, Key: 122)]);
        var recaps = new[]
        {
            Recap("Garen", 86, true, Now, laneOpponent: "Darius"),
            Recap("Garen", 86, false, Now.AddDays(-1), laneOpponent: "Darius"),
            Recap("Jinx", 222, true, Now.AddDays(-2)),
            Recap("Garen", 86, null, Now.AddDays(-3)), // no result: left out
            Recap("Garen", 86, false, Now.AddDays(-4)) with { Mode = GameMode.Aram }, // not Summoner's Rift: left out
        };
        var history = new[]
        {
            new PlayedGame(Now.AddMinutes(-30), 86, true, Position.Top, 122),   // the same game as the first recap
            new PlayedGame(Now.AddDays(-5), 86, true, Position.Top, null),
        };

        var stats = PersonalStats.Combine(recaps, history, champions);

        Assert.Equal(new WinLoss(3, 2), stats.With(86));
        Assert.Equal(new WinLoss(2, 1), stats.Against(122));
        Assert.Equal(86, stats.Champions()[0].ChampionKey);
        Assert.Equal("2W 1L", stats.With(86).ToString());
    }

    [Fact]
    public void MatchHistory_FindsYouAndYourLaneOpponentInAGamesDetails()
    {
        var games = MatchHistory.Parse("""
            {"gameId": 1, "gameMode": "CLASSIC", "gameCreation": 1790000000000,
             "participantIdentities": [{"participantId": 3, "player": {"puuid": "me"}}, {"participantId": 8, "player": {"puuid": "them"}}],
             "participants": [
               {"participantId": 3, "teamId": 100, "championId": 86, "stats": {"win": true}, "timeline": {"lane": "TOP", "role": "SOLO"}},
               {"participantId": 4, "teamId": 100, "championId": 222, "stats": {"win": true}, "timeline": {"lane": "BOTTOM", "role": "DUO_CARRY"}},
               {"participantId": 8, "teamId": 200, "championId": 122, "stats": {"win": false}, "timeline": {"lane": "TOP", "role": "SOLO"}},
               {"participantId": 9, "teamId": 200, "championId": 89, "stats": {"win": false}, "timeline": {"lane": "BOTTOM", "role": "DUO_SUPPORT"}}
             ]}
            """, puuid: "me");

        var game = games.Single();
        Assert.Equal((86, true, Position.Top, 122), (game.ChampionKey, game.Win, game.Position, game.OpponentKey));
    }

    [Fact]
    public void MatchHistory_ReadsTheListAndSkipsOtherModes()
    {
        const string list = """
            {"games": {"games": [
              {"gameId": 11, "gameMode": "CLASSIC", "gameCreation": 1790000000000,
               "participants": [{"participantId": 1, "championId": 222, "stats": {"win": false}, "teamPosition": "BOTTOM"}]},
              {"gameId": 12, "gameMode": "ARAM", "gameCreation": 1789000000000,
               "participants": [{"participantId": 1, "championId": 99, "stats": {"win": true}}]}
            ]}}
            """;

        var games = MatchHistory.Parse(list, puuid: null);

        Assert.Equal((222, false, Position.Bottom), (games.Single().ChampionKey, games.Single().Win, games.Single().Position));
        Assert.Null(games.Single().OpponentKey); // the list only has your own row
        Assert.Equal([11L], MatchHistory.GamesWithoutOpponents(list)); // no details needed for ARAM
    }

    [Fact]
    public void MatchHistory_CountsLeagueClassic()
    {
        // A real client reports League Classic as "JADE" on map 453, with champion ids from 60000.
        var games = MatchHistory.Parse("""
            {"games": {"games": [
              {"gameId": 21, "gameMode": "JADE", "mapId": 453, "gameCreation": 1790000000000,
               "participants": [{"participantId": 6, "championId": 60222, "stats": {"win": true}, "timeline": {"lane": "BOTTOM", "role": "CARRY"}}]}
            ]}}
            """, puuid: null);

        Assert.Equal((222, Position.Bottom), (games.Single().ChampionKey, games.Single().Position)); // League Classic's 60222 is Jinx
    }

    [Fact]
    public void Analyze_ReadsTheResultFromTheGameEndEvent()
    {
        var game = TestData.Game(allies: [("Garen", [])], enemies: [("Annie", [])]);
        var ended = new AllGameData
        {
            ActivePlayer = game.ActivePlayer,
            AllPlayers = game.AllPlayers,
            Events = new LiveEvents { Events = [new LiveEvent { EventName = "GameStart" }, new LiveEvent { EventName = "GameEnd", Result = "Lose" }] },
        };

        Assert.False(GameAnalyzer.Analyze(ended, TestData.Static)!.Result);
        Assert.Null(GameAnalyzer.Analyze(game, TestData.Static)!.Result);
    }

    private static BuildRecommendation Recommend(int minutes, string? result, int[] items)
    {
        var game = TestData.Game(allies: [("Garen", items)], enemies: [("Annie", [])]);
        var data = new AllGameData
        {
            ActivePlayer = game.ActivePlayer,
            AllPlayers = game.AllPlayers,
            GameData = new LiveGameInfo { GameTime = minutes * 60 },
            Events = result is null ? null : new LiveEvents { Events = [new LiveEvent { EventName = "GameEnd", Result = result }] },
        };
        return new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(data, TestData.Static)!);
    }

    private static GameRecap Recap(string name, int key, bool? win, DateTime played, string? laneOpponent = null) =>
        new(played, name, name, key, GameMode.SummonersRift, Archetype.Bruiser, Position.Top, 30 * 60, win, [], [], [], laneOpponent);
}
