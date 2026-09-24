using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class MatchupTests
{
    private static readonly ChampionInfo Garen = new("Garen", "Garen", ["Fighter", "Tank"], 7, 7, 1, Key: 86);
    private static readonly ChampionInfo Darius = new("Darius", "Darius", ["Fighter", "Tank"], 9, 5, 1, Key: 122);
    private static readonly ChampionInfo Teemo = new("Teemo", "Teemo", ["Marksman", "Assassin"], 5, 3, 7, Key: 17);
    private static readonly ChampionInfo Volibear = new("Volibear", "Volibear", ["Fighter", "Tank"], 7, 7, 4, Key: 106);
    private static readonly ChampionInfo Singed = new("Singed", "Singed", ["Tank", "Mage"], 4, 8, 7, Key: 27);
    private static readonly ChampionInfo Amumu = new("Amumu", "Amumu", ["Tank", "Support"], 2, 6, 8, Key: 32);
    private static readonly ChampionInfo Syndra = new("Syndra", "Syndra", ["Mage"], 2, 3, 9, Key: 134);
    private static readonly ChampionInfo Lux = new("Lux", "Lux", ["Mage", "Support"], 2, 4, 9, Key: 99);
    private static readonly ChampionInfo Caitlyn = new("Caitlyn", "Caitlyn", ["Marksman"], 8, 2, 2, Key: 51);
    private static readonly ChampionInfo Jinx = new("Jinx", "Jinx", ["Marksman"], 9, 2, 4, Key: 222);

    private static readonly ChampionCatalog Champions = new([Garen, Darius, Teemo, Volibear, Singed, Amumu, Syndra, Lux, Caitlyn, Jinx]);

    private static readonly Dictionary<int, IReadOnlyDictionary<Position, double>> Rates = new()
    {
        [Darius.Key] = new Dictionary<Position, double> { [Position.Top] = 0.88, [Position.Jungle] = 0.05 },
        [Amumu.Key] = new Dictionary<Position, double> { [Position.Jungle] = 0.8, [Position.Support] = 0.2 },
        [Syndra.Key] = new Dictionary<Position, double> { [Position.Middle] = 0.9, [Position.Support] = 0.05 },
        [Lux.Key] = new Dictionary<Position, double> { [Position.Middle] = 0.55, [Position.Support] = 0.4 },
        [Caitlyn.Key] = new Dictionary<Position, double> { [Position.Bottom] = 0.97 },
    };

    [Fact]
    public void RoleGuesser_GivesEachEnemyTheMostLikelyFreeRole()
    {
        var roles = RoleGuesser.Guess([Lux, Darius, Syndra, Caitlyn, Amumu], Rates).ToDictionary(r => r.Champion, r => r.Position);

        Assert.Equal(Position.Top, roles[Darius]);
        Assert.Equal(Position.Jungle, roles[Amumu]);
        Assert.Equal(Position.Middle, roles[Syndra]);   // Syndra is mid far more often than Lux...
        Assert.Equal(Position.Support, roles[Lux]);      // ...so Lux, who also supports, goes support
        Assert.Equal(Position.Bottom, roles[Caitlyn]);
    }

    [Fact]
    public void RoleGuesser_UsesTheClassWhenThereAreNoRates()
    {
        var roles = RoleGuesser.Guess([Jinx, Lux], new Dictionary<int, IReadOnlyDictionary<Position, double>>());

        Assert.Equal(Position.Bottom, roles.Single(r => r.Champion == Jinx).Position);
        Assert.Equal(Position.Middle, roles.Single(r => r.Champion == Lux).Position);
    }

    [Fact]
    public async Task Analyze_FindsYourLaneOpponentAndTheMatchup()
    {
        var data = new FakeData { [(Garen.Key, Position.Top)] = [new(Darius.Key, 2792, 1407)] };

        var report = (await Advisor(data).AnalyzeAsync(new MatchupRequest(Garen, true, Position.Top, GameMode.SummonersRift, [Darius, Amumu, Caitlyn])))!;

        Assert.Equal(Darius, report.Opponent);
        Assert.True(report.RolesAreGuessed);
        Assert.Equal(0.504, report.Matchup!.WinRate, precision: 3);
        Assert.Equal("even", report.Matchup.Verdict);
        Assert.Empty(report.CounterPicks); // locked in: too late to counter
    }

    [Fact]
    public async Task Analyze_TurnsTheOpponentsStatsAround_WhenYoursAreMissing()
    {
        var data = new FakeData { [(Darius.Key, Position.Top)] = [new(Garen.Key, 1000, 450)] };

        var report = (await Advisor(data).AnalyzeAsync(new MatchupRequest(Garen, true, Position.Top, GameMode.SummonersRift, [Darius])))!;

        Assert.Equal(0.55, report.Matchup!.WinRate, precision: 3);
        Assert.Equal("favored", report.Matchup.Verdict);
    }

    [Fact]
    public async Task Analyze_ShowsThePartnerInBottomLane()
    {
        var report = (await Advisor(new FakeData()).AnalyzeAsync(new MatchupRequest(Jinx, true, Position.Bottom, GameMode.SummonersRift, [Lux, Caitlyn, Syndra])))!;

        Assert.Equal(Caitlyn, report.Opponent);
        Assert.Equal(Lux, report.Partner);
        Assert.Null(report.Matchup); // no stats for Jinx vs Caitlyn in the fake
    }

    [Fact]
    public async Task CounterPicks_PutChampionsYouPlayFirst_AndSkipWhatYouCantPick()
    {
        var data = new FakeData
        {
            // Darius's results against each champion: fewer wins for Darius means a better pick for you.
            [(Darius.Key, Position.Top)] =
            [
                new(Volibear.Key, 1406, 612),  // you win 56.5%
                new(Teemo.Key, 1795, 822),     // 54.2%, and you play Teemo
                new(Singed.Key, 487, 212),     // 56.5%, but not pickable
                new(Garen.Key, 2792, 1375),    // the champion you hover
                new(Lux.Key, 150, 50),         // too few games
                new(Amumu.Key, 900, 450),      // 50%: no real edge
                new(Syndra.Key, 3000, 1200),   // 60%, but an enemy has her
            ],
        };
        var request = new MatchupRequest(Garen, IsLocked: false, Position.Top, GameMode.SummonersRift, [Darius, Syndra])
        {
            Pickable = new HashSet<int> { Volibear.Key, Teemo.Key, Garen.Key, Lux.Key, Amumu.Key, Syndra.Key },
            Mastery = new Dictionary<int, int> { [Teemo.Key] = 34_800, [Volibear.Key] = 900 },
        };

        var report = (await Advisor(data).AnalyzeAsync(request))!;

        Assert.Equal([Teemo, Volibear], report.CounterPicks.Select(c => c.Champion));
        Assert.True(report.CounterPicks[0].YouPlayIt);
        Assert.Equal(0.542, report.CounterPicks[0].WinRate, precision: 3);
    }

    [Theory]
    [InlineData(GameMode.Aram, Position.None)]
    [InlineData(GameMode.SummonersRift, Position.None)]
    [InlineData(GameMode.LeagueClassic, Position.Top)]
    public async Task Analyze_NeedsSummonersRiftAndARole(GameMode mode, Position position)
    {
        Assert.Null(await Advisor(new FakeData()).AnalyzeAsync(new MatchupRequest(Garen, true, position, mode, [Darius])));
    }

    [Fact]
    public async Task Analyze_SaysSo_WhenOpggDoesntAnswer()
    {
        var noRates = await Advisor(new FakeData { FailRates = true }).AnalyzeAsync(new MatchupRequest(Garen, true, Position.Top, GameMode.SummonersRift, [Darius]));
        var noMatchups = await Advisor(new FakeData { FailMatchups = true }).AnalyzeAsync(new MatchupRequest(Garen, true, Position.Top, GameMode.SummonersRift, [Darius]));

        Assert.Equal(Darius, noRates!.Opponent); // guessed from his class instead
        Assert.Contains("guessed from their class", noRates.Note);
        Assert.Equal(Darius, noMatchups!.Opponent);
        Assert.Contains("no matchup stats", noMatchups.Note);
    }

    [Fact]
    public void ForGame_UsesTheRolesTheGameReports()
    {
        var game = new AllGameData
        {
            ActivePlayer = new ActivePlayer { RiotId = "Me#TEST" },
            AllPlayers =
            [
                new LivePlayer { ChampionName = "Garen", RawChampionName = "game_character_displayname_Garen", RiotId = "Me#TEST", Team = "ORDER", Position = "TOP" },
                new LivePlayer { ChampionName = "Ornn", RawChampionName = "game_character_displayname_Ornn", Team = "CHAOS", Position = "TOP" },
                new LivePlayer { ChampionName = "Lux", RawChampionName = "game_character_displayname_Lux", Team = "CHAOS", Position = "UTILITY" },
            ],
            GameData = new LiveGameInfo { GameMode = "CLASSIC", MapNumber = 11 },
        };

        var request = MatchupRequest.ForGame(GameAnalyzer.Analyze(game, TestData.Static)!)!;

        Assert.Equal(Position.Top, request.Position);
        Assert.True(request.IsLocked);
        Assert.Equal(Position.Top, request.KnownEnemyRoles![request.Enemies.Single(e => e.Id == "Ornn")]);
        Assert.Equal(Position.Support, request.KnownEnemyRoles[request.Enemies.Single(e => e.Id == "Lux")]);
    }

    [Fact]
    public void ChampSelectState_KnowsWhenYouLockedIn()
    {
        ClientSnapshot Snapshot(bool completed) => new(
            new ChampSelectSession
            {
                LocalPlayerCellId = 0,
                MyTeam = [new() { CellId = 0, ChampionId = 86, AssignedPosition = "top" }],
                Actions = [[new() { ActorCellId = 0, ChampionId = 86, Type = "pick", Completed = completed }]],
            },
            null, null)
        {
            PickableChampionIds = [86, 17],
            Mastery = [new() { ChampionId = 17, ChampionPoints = 34_800 }],
        };

        var hovering = ChampSelectState.From(Snapshot(completed: false), Champions)!;
        var locked = ChampSelectState.From(Snapshot(completed: true), Champions)!;

        Assert.False(hovering.IsLocked);
        Assert.True(locked.IsLocked);
        Assert.NotEqual(hovering.Fingerprint, locked.Fingerprint);
        Assert.Contains(17, hovering.Pickable);
        Assert.Equal(34_800, hovering.Mastery[17]);
    }

    [Fact]
    public void ParseChampion_ReadsMatchupsAndMainRole()
    {
        var champion = OpggClient.ParseChampion("""
            {"data": {"summary": {"positions": [{"name": "TOP"}, {"name": "JUNGLE"}]},
                      "counters": [{"champion_id": 86, "play": 2792, "win": 1375}]}}
            """);
        var stats = OpggClient.ParseRoleStats("""
            {"data": [{"id": 122, "positions": [
              {"name": "TOP", "stats": {"play": 12740, "win_rate": 0.494, "ban_rate": 0.115, "role_rate": 0.88, "tier_data": {"tier": 2, "rank": 15}}},
              {"name": "JUNGLE", "stats": {"play": 500, "win_rate": 0.47, "ban_rate": null, "role_rate": 0.05}}]}]}
            """);

        Assert.Equal(Position.Top, champion.MainRole);
        Assert.Equal(new OpggMatchup(86, 2792, 1375), champion.Matchups.Single());
        Assert.Equal(new OpggRoleStats(0.88, 0.494, 0.115, 2, 15, 12740), stats[122][Position.Top]);
        Assert.Equal(0, stats[122][Position.Jungle].BanRate); // op.gg sends null
        Assert.Equal(5, stats[122][Position.Jungle].Tier);
    }

    private static MatchupAdvisor Advisor(FakeData data) => new(data, Champions);

    private sealed class FakeData : Dictionary<(int Champion, Position Role), IReadOnlyList<OpggMatchup>>, IMatchupData
    {
        public bool FailRates { get; init; }
        public bool FailMatchups { get; init; }

        public Task<IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>>> GetRoleStatsAsync(CancellationToken ct) =>
            FailRates
                ? throw new HttpRequestException("offline")
                : Task.FromResult<IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>>>(Rates.ToDictionary(
                    c => c.Key,
                    c => (IReadOnlyDictionary<Position, OpggRoleStats>)c.Value.ToDictionary(r => r.Key, r => new OpggRoleStats(r.Value, 0.5, 0, 3, 50, 1000))));

        public Task<IReadOnlyList<OpggMatchup>> GetMatchupsAsync(int championKey, Position role, CancellationToken ct) =>
            FailMatchups ? throw new HttpRequestException("offline") : Task.FromResult(this.GetValueOrDefault((championKey, role)) ?? []);
    }
}
