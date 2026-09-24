using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class DraftTests
{
    private static readonly ChampionInfo Garen = new("Garen", "Garen", ["Fighter", "Tank"], 7, 7, 1, Key: 86);
    private static readonly ChampionInfo Darius = new("Darius", "Darius", ["Fighter", "Tank"], 9, 5, 1, Key: 122);
    private static readonly ChampionInfo Teemo = new("Teemo", "Teemo", ["Marksman", "Assassin"], 5, 3, 7, Key: 17);
    private static readonly ChampionInfo Malphite = new("Malphite", "Malphite", ["Tank", "Fighter"], 5, 9, 7, Key: 54);
    private static readonly ChampionInfo Vladimir = new("Vladimir", "Vladimir", ["Mage", "Fighter"], 2, 6, 8, Key: 8);
    private static readonly ChampionInfo Jinx = new("Jinx", "Jinx", ["Marksman"], 9, 2, 4, Key: 222);
    private static readonly ChampionInfo Zed = new("Zed", "Zed", ["Assassin"], 9, 2, 1, Key: 238);
    private static readonly ChampionInfo Caitlyn = new("Caitlyn", "Caitlyn", ["Marksman"], 8, 2, 2, Key: 51);
    private static readonly ChampionInfo Amumu = new("Amumu", "Amumu", ["Tank", "Support"], 2, 6, 8, Key: 32);
    private static readonly ChampionInfo Lux = new("Lux", "Lux", ["Mage", "Support"], 2, 4, 9, Key: 99);

    private static readonly ChampionCatalog Champions = new([Garen, Darius, Teemo, Malphite, Vladimir, Jinx, Zed, Caitlyn, Amumu, Lux]);

    // Top laners by op.gg tier and rank: Darius, Malphite, Teemo, Vladimir, Garen.
    private static readonly Dictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>> TopStats = new()
    {
        [Darius.Key] = Top(1, 1, 0.52, 0.20),
        [Malphite.Key] = Top(1, 2, 0.515, 0.16),
        [Teemo.Key] = Top(2, 3, 0.51, 0.05),
        [Vladimir.Key] = Top(2, 4, 0.505, 0.03),
        [Garen.Key] = Top(3, 9, 0.50, 0.04),
        [Jinx.Key] = new Dictionary<Position, OpggRoleStats> { [Position.Bottom] = new(0.99, 0.52, 0.1, 1, 1, 90000) },
    };

    private static Dictionary<Position, OpggRoleStats> Top(int tier, int rank, double winRate, double banRate) =>
        new() { [Position.Top] = new OpggRoleStats(0.9, winRate, banRate, tier, rank, 10000) };

    [Fact]
    public async Task Bans_WithAChampionInMind_AreTheOpponentsItLosesTo()
    {
        var data = new FakeData { [(Garen.Key, Position.Top)] = [new(Darius.Key, 3000, 1380), new(Teemo.Key, 2000, 940), new(Malphite.Key, 1500, 780), new(Vladimir.Key, 900, 400)] };
        var request = new DraftRequest(Garen, false, Position.Top, GameMode.SummonersRift, [], []) { HasPendingBan = true, Unavailable = new HashSet<int> { Vladimir.Key } };

        var report = (await new DraftAdvisor(data, Champions).AnalyzeAsync(request))!;

        Assert.Equal([Darius, Teemo], report.Bans.Select(b => b.Champion)); // 46% and 47%; Malphite is 52% for you, Vladimir is banned
        Assert.StartsWith("Beats Garen: you win 46.0%", report.Bans[0].Reason);
    }

    [Fact]
    public async Task Bans_WithoutAChampion_AreTheStrongestInYourRole()
    {
        var request = new DraftRequest(null, false, Position.Top, GameMode.SummonersRift, [], []) { HasPendingBan = true, Unavailable = new HashSet<int> { Darius.Key } };

        var report = (await new DraftAdvisor(new FakeData(), Champions).AnalyzeAsync(request))!;

        Assert.Equal([Malphite, Teemo, Vladimir, Garen], report.Bans.Select(b => b.Champion));
        Assert.Equal("Tier 1 in top: 51.5% win rate, banned in 16% of games.", report.Bans[0].Reason);
    }

    [Fact]
    public async Task Bans_OnlyShowWhileYourBanIsComing()
    {
        var report = (await new DraftAdvisor(new FakeData(), Champions).AnalyzeAsync(new DraftRequest(null, false, Position.Top, GameMode.SummonersRift, [], [])))!;

        Assert.Empty(report.Bans);
    }

    [Fact]
    public async Task TeamCheck_WarnsAboutAnAllAdTeam_AndSuggestsApPicksInYourRole()
    {
        var request = new DraftRequest(null, false, Position.Top, GameMode.SummonersRift, [Jinx, Zed, Caitlyn], [])
        {
            Mastery = new Dictionary<int, int> { [Vladimir.Key] = 40_000 },
        };

        var report = (await new DraftAdvisor(new FakeData(), Champions).AnalyzeAsync(request))!;

        Assert.Contains(report.Warnings, w => w.Contains("AD, so enemies only need armor"));
        Assert.Contains(report.Warnings, w => w.StartsWith("No frontline yet"));
        Assert.Equal("PICKS THAT ADD A FRONTLINE", report.FillHeader); // frontline matters more than damage type
        Assert.Equal([Darius, Malphite, Garen], report.FillPicks.Select(p => p.Champion));
    }

    [Fact]
    public async Task TeamCheck_SuggestsApPicks_WhenTheFrontlineIsThere()
    {
        var request = new DraftRequest(null, false, Position.Top, GameMode.SummonersRift, [Jinx, Zed, Darius], [])
        {
            Mastery = new Dictionary<int, int> { [Vladimir.Key] = 40_000 },
        };

        var report = (await new DraftAdvisor(new FakeData(), Champions).AnalyzeAsync(request))!;

        Assert.Equal("PICKS THAT ADD AP", report.FillHeader);
        Assert.Equal([Vladimir, Malphite, Teemo], report.FillPicks.Select(p => p.Champion)); // Vladimir first: you play him
        Assert.True(report.FillPicks[0].YouPlayIt);
    }

    [Fact]
    public async Task TeamCheck_StaysQuietForABalancedTeam_AndAfterYouLockIn()
    {
        var advisor = new DraftAdvisor(new FakeData(), Champions);

        var balanced = (await advisor.AnalyzeAsync(new DraftRequest(Garen, false, Position.Top, GameMode.SummonersRift, [Jinx, Lux, Amumu], [])))!;
        var locked = (await advisor.AnalyzeAsync(new DraftRequest(Jinx, true, Position.Bottom, GameMode.SummonersRift, [Zed, Caitlyn], [])))!;

        Assert.Empty(balanced.Warnings);
        Assert.NotEmpty(locked.Warnings);
        Assert.Empty(locked.FillPicks); // too late to change your pick
    }

    [Fact]
    public async Task EnemySummary_CountsDamageTanksAndCrowdControl()
    {
        var report = (await new DraftAdvisor(new FakeData(), Champions).AnalyzeAsync(
            new DraftRequest(null, false, Position.Top, GameMode.SummonersRift, [], [Amumu, Malphite, Lux])))!;

        Assert.StartsWith("Enemy so far: ", report.EnemySummary);
        Assert.Contains("2 tanks", report.EnemySummary);
        Assert.Contains("heavy crowd control", report.EnemySummary);
    }

    [Fact]
    public async Task Draft_StillChecksTheTeam_WhenOpggIsDown()
    {
        var report = (await new DraftAdvisor(new FakeData { Fail = true }, Champions).AnalyzeAsync(
            new DraftRequest(null, false, Position.Top, GameMode.SummonersRift, [Jinx, Zed, Caitlyn], []) { HasPendingBan = true }))!;

        Assert.NotEmpty(report.Warnings);
        Assert.Empty(report.Bans);
        Assert.Contains("op.gg didn't answer", report.Note);
    }

    [Fact]
    public async Task Draft_IsOnlyForSummonersRift()
    {
        Assert.Null(await new DraftAdvisor(new FakeData(), Champions).AnalyzeAsync(new DraftRequest(Jinx, true, Position.None, GameMode.Aram, [Zed], [])));
    }

    [Fact]
    public void Notes_AreSavedPerOpponentAndSurviveARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lc-notes-{Guid.NewGuid()}.json");
        try
        {
            new MatchupNotes(path).Set(Darius, "  Don't trade at level 2.  ");
            var reopened = new MatchupNotes(path);

            Assert.Equal("Don't trade at level 2.", reopened.Get(Darius));
            Assert.Equal("", reopened.Get(Teemo));

            reopened.Set(Darius, "");
            Assert.Equal("", new MatchupNotes(path).Get(Darius));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ChampSelectState_ReadsBansAndYourBanTurn()
    {
        var snapshot = new ClientSnapshot(
            new ChampSelectSession
            {
                LocalPlayerCellId = 0,
                MyTeam = [new() { CellId = 0, AssignedPosition = "top" }, new() { CellId = 1, ChampionPickIntent = 222 }],
                Actions =
                [
                    [new() { ActorCellId = 5, ChampionId = 238, Type = "ban", Completed = true }],
                    [new() { ActorCellId = 0, ChampionId = 0, Type = "ban", Completed = false }],
                ],
                Bans = new ChampSelectBans { TheirTeamBans = [51] },
            },
            null, null);

        var state = ChampSelectState.From(snapshot, Champions)!;

        Assert.True(state.HasPendingBan);
        Assert.Equal(new HashSet<int> { 238, 51 }, state.Bans);
        Assert.Equal([Jinx], state.Allies); // a teammate's hover counts for the team check
    }

    private sealed class FakeData : Dictionary<(int Champion, Position Role), IReadOnlyList<OpggMatchup>>, IMatchupData
    {
        public bool Fail { get; init; }

        public Task<IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>>> GetRoleStatsAsync(CancellationToken ct) =>
            Fail ? throw new HttpRequestException("offline") : Task.FromResult<IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>>>(TopStats);

        public Task<IReadOnlyList<OpggMatchup>> GetMatchupsAsync(int championKey, Position role, CancellationToken ct) =>
            Fail ? throw new HttpRequestException("offline") : Task.FromResult(this.GetValueOrDefault((championKey, role)) ?? []);
    }
}
