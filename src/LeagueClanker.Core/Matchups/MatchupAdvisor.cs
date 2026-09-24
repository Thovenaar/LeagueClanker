using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Matchups;

/// <summary>Role play rates and lane matchups. <see cref="OpggClient"/> provides them; tests use a fake.</summary>
public interface IMatchupData
{
    Task<IReadOnlyDictionary<int, IReadOnlyDictionary<Position, double>>> GetRoleRatesAsync(CancellationToken ct);

    /// <summary>How the champion does in that role against each opponent in the same role.</summary>
    Task<IReadOnlyList<OpggMatchup>> GetMatchupsAsync(int championKey, Position role, CancellationToken ct);
}

/// <summary>Your champion against your lane opponent, from your side.</summary>
public sealed record MatchupStat(ChampionInfo Opponent, double WinRate, int Games)
{
    public string Verdict => WinRate >= 0.52 ? "favored" : WinRate <= 0.48 ? "tough" : "even";
}

/// <param name="WinRate">Your win rate with this champion against the lane opponent.</param>
/// <param name="MasteryPoints">Your mastery on this champion, 0 when unknown.</param>
public sealed record CounterPick(ChampionInfo Champion, double WinRate, int Games, int MasteryPoints)
{
    public bool YouPlayIt => MasteryPoints >= MatchupAdvisor.PlayedMasteryPoints;
}

/// <param name="Me">Your pick or hover. Null before either.</param>
/// <param name="IsLocked">Counter picks only matter until you lock in.</param>
public sealed record MatchupRequest(ChampionInfo? Me, bool IsLocked, Position Position, GameMode Mode, IReadOnlyList<ChampionInfo> Enemies)
{
    /// <summary>Enemy roles when they're known, as in a live game. Otherwise they're guessed.</summary>
    public IReadOnlyDictionary<ChampionInfo, Position>? KnownEnemyRoles { get; init; }

    /// <summary>Champions you can pick right now. Null when unknown: then every champion counts.</summary>
    public IReadOnlySet<int>? Pickable { get; init; }

    /// <summary>Your mastery points per champion key.</summary>
    public IReadOnlyDictionary<int, int> Mastery { get; init; } = new Dictionary<int, int>();

    /// <summary>
    /// A running game: your pick is locked, and matchmade games report every role. When they don't (blind pick),
    /// the enemy roles are guessed; without your own role there's no lane to talk about.
    /// </summary>
    public static MatchupRequest? ForGame(GameAnalysis game)
    {
        if (game.Me.Position == Position.None)
            return null;

        var enemies = game.Enemies.Players;
        var rolesKnown = enemies.All(e => e.Position != Position.None) && enemies.Select(e => e.Position).Distinct().Count() == enemies.Count;
        return new MatchupRequest(game.Me.Champion, IsLocked: true, game.Me.Position, game.Mode, enemies.Select(e => e.Champion).ToList())
        {
            KnownEnemyRoles = rolesKnown ? enemies.ToDictionary(e => e.Champion, e => e.Position) : null,
        };
    }
}

/// <param name="Opponent">The enemy in your role. Null until they pick.</param>
public sealed record MatchupReport(Position Position, IReadOnlyList<RoleGuess> EnemyRoles, ChampionInfo? Opponent)
{
    /// <summary>The other half of the enemy bottom lane: their support when you play bottom, their ADC when you support.</summary>
    public ChampionInfo? Partner { get; init; }

    public bool RolesAreGuessed { get; init; } = true;
    public MatchupStat? Matchup { get; init; }
    public IReadOnlyList<CounterPick> CounterPicks { get; init; } = [];

    /// <summary>Why something is missing, e.g. op.gg not answering.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Works out who you'll lane against and how that matchup goes. Before you lock in it lists champions that beat your
/// lane opponent, the ones you play first. Only Summoner's Rift has lanes, so other modes get nothing.
/// </summary>
public sealed class MatchupAdvisor(IMatchupData data, ChampionCatalog champions)
{
    public const int MinMatchupGames = 200;

    /// <summary>Mastery points from which a champion counts as one you play.</summary>
    public const int PlayedMasteryPoints = 20_000;

    private const int MaxCounterPicks = 5;

    // A counter pick needs a real edge. Otherwise a champion you play at 50.3% would outrank a real counter.
    private const double MinCounterWinRate = 0.51;
    private const string NoAnswer = "op.gg didn't answer, so there are no matchup stats right now.";

    public async Task<MatchupReport?> AnalyzeAsync(MatchupRequest request, CancellationToken ct = default)
    {
        if (request.Mode != GameMode.SummonersRift || request.Position == Position.None)
            return null;

        string? note = null;
        IReadOnlyList<RoleGuess> roles;
        if (request.KnownEnemyRoles is { } known)
        {
            roles = known.Select(r => new RoleGuess(r.Key, r.Value, 1)).ToList();
        }
        else
        {
            IReadOnlyDictionary<int, IReadOnlyDictionary<Position, double>> rates = new Dictionary<int, IReadOnlyDictionary<Position, double>>();
            try
            {
                rates = await data.GetRoleRatesAsync(ct);
            }
            catch (Exception ex) when (IsUnreachable(ex, ct))
            {
                note = "op.gg didn't answer, so enemy roles are guessed from their class and there are no matchup stats.";
            }
            roles = RoleGuesser.Guess(request.Enemies, rates);
        }

        ChampionInfo? InRole(Position role) => roles.FirstOrDefault(r => r.Position == role)?.Champion;
        var opponent = InRole(request.Position);
        var report = new MatchupReport(request.Position, roles, opponent)
        {
            Partner = request.Position switch { Position.Bottom => InRole(Position.Support), Position.Support => InRole(Position.Bottom), _ => null },
            RolesAreGuessed = request.KnownEnemyRoles is null,
            Note = note,
        };
        if (opponent is null || opponent.Key <= 0 || note is not null)
            return report;

        var failed = false;
        MatchupStat? matchup = null;
        IReadOnlyList<CounterPick> counters = [];
        try
        {
            if (request.Me is { Key: > 0 } me)
                matchup = await MatchupAsync(me, opponent, request.Position, ct);
            if (!request.IsLocked)
                counters = await CounterPicksAsync(opponent, request, ct);
        }
        catch (Exception ex) when (IsUnreachable(ex, ct))
        {
            failed = true;
        }
        return report with { Matchup = matchup, CounterPicks = counters, Note = failed ? NoAnswer : null };
    }

    // Your own stats against them first; failing that, theirs against you, turned around.
    private async Task<MatchupStat?> MatchupAsync(ChampionInfo me, ChampionInfo opponent, Position role, CancellationToken ct)
    {
        var mine = (await data.GetMatchupsAsync(me.Key, role, ct)).FirstOrDefault(m => m.OpponentKey == opponent.Key && m.Games >= MinMatchupGames);
        if (mine is not null)
            return new MatchupStat(opponent, mine.WinRate, mine.Games);

        var theirs = (await data.GetMatchupsAsync(opponent.Key, role, ct)).FirstOrDefault(m => m.OpponentKey == me.Key && m.Games >= MinMatchupGames);
        return theirs is null ? null : new MatchupStat(opponent, 1 - theirs.WinRate, theirs.Games);
    }

    private async Task<IReadOnlyList<CounterPick>> CounterPicksAsync(ChampionInfo opponent, MatchupRequest request, CancellationToken ct)
    {
        // Enemy picks are taken, and the champion you hover already has its matchup shown.
        var skip = request.Enemies.Select(e => e.Key).Append(request.Me?.Key ?? 0).ToHashSet();
        return (await data.GetMatchupsAsync(opponent.Key, request.Position, ct))
            .Where(m => m.Games >= MinMatchupGames && 1 - m.WinRate >= MinCounterWinRate && !skip.Contains(m.OpponentKey))
            .Where(m => request.Pickable is null || request.Pickable.Contains(m.OpponentKey))
            .Select(m => champions.GetByKey(m.OpponentKey) is { } champion
                ? new CounterPick(champion, 1 - m.WinRate, m.Games, request.Mastery.GetValueOrDefault(m.OpponentKey))
                : null)
            .OfType<CounterPick>()
            .OrderByDescending(c => c.YouPlayIt)
            .ThenByDescending(c => c.WinRate)
            .Take(MaxCounterPicks)
            .ToList();
    }

    private static bool IsUnreachable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or InvalidOperationException || (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
