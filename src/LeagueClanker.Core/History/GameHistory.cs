using System.Text.Json;
using System.Text.Json.Serialization;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.History;

/// <param name="GameTime">Seconds into the game.</param>
public sealed record PivotRecord(double GameTime, string Summary, bool Accepted);

/// <summary>One game LeagueClanker watched: what you played and built, what it advised, and how it went.</summary>
/// <param name="Win">Null when the game ended without a result in the data, e.g. when the app was closed first.</param>
/// <param name="LaneOpponent">The enemy in your role, by champion id, when the game reports roles.</param>
public sealed record GameRecap(
    DateTime Played, string ChampionId, string ChampionName, int ChampionKey, GameMode Mode, Archetype Playstyle, Position Position,
    double DurationSeconds, bool? Win, IReadOnlyList<int> FinalItems, IReadOnlyList<int> AdvisedItems, IReadOnlyList<PivotRecord> Pivots,
    string? LaneOpponent)
{
    /// <summary>Your finished items that LeagueClanker had in its build at some point.</summary>
    public int AdvisedAndBuilt => FinalItems.Count(AdvisedItems.Contains);
}

/// <summary>
/// Follows the game while it runs and writes the recap at the end. The app feeds it every recommendation and every
/// pivot you accept or decline.
/// </summary>
public sealed class GameRecorder
{
    private const double MinSeconds = 5 * 60; // shorter games (remakes, Practice Tool peeks) aren't worth a recap

    private BuildRecommendation? _last;
    private readonly HashSet<int> _advised = [];
    private readonly List<PivotRecord> _pivots = [];
    private string? _laneOpponent;

    public void Observe(BuildRecommendation rec)
    {
        if (_last is not null && _last.Game.Me.Champion.Id != rec.Game.Me.Champion.Id)
            Clear(); // a new game started without a gap between them
        _last = rec;
        foreach (var item in rec.Items)
            _advised.Add(item.Item.Id);
        var me = rec.Game.Me;
        _laneOpponent ??= me.Position == Position.None ? null : rec.Game.Enemies.Players.FirstOrDefault(e => e.Position == me.Position)?.Champion.Id;
    }

    public void Pivot(string summary, bool accepted) => _pivots.Add(new PivotRecord(_last?.Game.GameTimeSeconds ?? 0, summary, accepted));

    /// <summary>The game ended (the game's API went away). Returns its recap, or null for a game too short to count.</summary>
    public GameRecap? Finish(DateTime now)
    {
        var rec = _last;
        var recap = rec is null || rec.Game.GameTimeSeconds < MinSeconds ? null : new GameRecap(
            now, rec.Game.Me.Champion.Id, rec.Game.Me.Name, rec.Game.Me.Champion.Key, rec.Game.Mode, rec.Game.Me.Archetype, rec.Game.Me.Position,
            rec.Game.GameTimeSeconds, rec.Game.Result,
            rec.Game.Me.Items.Where(i => i.Kind is ItemKind.Legendary or ItemKind.Boots).Select(i => i.Id).ToList(),
            _advised.ToList(), _pivots.ToList(), _laneOpponent);
        Clear();
        return recap;
    }

    private void Clear()
    {
        _last = null;
        _advised.Clear();
        _pivots.Clear();
        _laneOpponent = null;
    }
}

/// <summary>Your recaps, newest first, in a JSON file. Keeps the last 200 games.</summary>
public sealed class RecapStore(string path)
{
    private const int MaxGames = 200;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private List<GameRecap>? _games;

    public IReadOnlyList<GameRecap> Games => Load();

    public void Add(GameRecap recap)
    {
        var games = Load();
        games.Insert(0, recap);
        if (games.Count > MaxGames)
            games.RemoveRange(MaxGames, games.Count - MaxGames);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(games, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving the game recap", ex);
        }
    }

    private List<GameRecap> Load()
    {
        if (_games is not null)
            return _games;
        try
        {
            _games = File.Exists(path) ? JsonSerializer.Deserialize<List<GameRecap>>(File.ReadAllText(path), Options) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Reading game recaps", ex);
            _games = [];
        }
        return _games;
    }
}

/// <summary>A game you played, from LeagueClanker's recaps or the client's match history.</summary>
/// <param name="OpponentKey">Your lane opponent's champion key, when known.</param>
public sealed record PlayedGame(DateTime Played, int ChampionKey, bool Win, Position Position, int? OpponentKey);

/// <param name="Wins">Your wins with this champion, or against this opponent.</param>
public sealed record WinLoss(int Games, int Wins)
{
    public int Losses => Games - Wins;
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
    public override string ToString() => $"{Wins}W {Losses}L";
}

/// <summary>How you do per champion and against each lane opponent, from the games you played.</summary>
public sealed class PersonalStats(IReadOnlyList<PlayedGame> games)
{
    /// <summary>Games needed before a record is worth showing.</summary>
    public const int MinGames = 3;

    public IReadOnlyList<PlayedGame> Games { get; } = games;

    public WinLoss With(int championKey) => Count(Games.Where(g => g.ChampionKey == championKey));

    public WinLoss Against(int opponentKey) => Count(Games.Where(g => g.OpponentKey == opponentKey));

    /// <summary>Your most played champions, most games first.</summary>
    public IReadOnlyList<(int ChampionKey, WinLoss Record)> Champions() =>
        Games.GroupBy(g => g.ChampionKey).Select(g => (g.Key, Count(g))).OrderByDescending(c => c.Item2.Games).ThenByDescending(c => c.Item2.WinRate).ToList();

    /// <summary>
    /// Summoner's Rift recaps and match history together, without counting a game twice: the same champion starting within ten minutes.
    /// A recap is written when a game ends, so its start is its end minus its length.
    /// </summary>
    public static PersonalStats Combine(IEnumerable<GameRecap> recaps, IEnumerable<PlayedGame> history, ChampionCatalog champions)
    {
        var fromRecaps = recaps.Where(r => r.Win is not null && r.Mode == GameMode.SummonersRift)
            .Select(r => new PlayedGame(r.Played.AddSeconds(-r.DurationSeconds), r.ChampionKey, r.Win!.Value, r.Position,
                r.LaneOpponent is { } id ? champions.Get(id)?.Key : null))
            .ToList();
        var extra = history.Where(h => !fromRecaps.Any(r => r.ChampionKey == h.ChampionKey && Math.Abs((r.Played - h.Played).TotalMinutes) < 10));
        return new PersonalStats([.. fromRecaps, .. extra]);
    }

    private static WinLoss Count(IEnumerable<PlayedGame> games)
    {
        var list = games.ToList();
        return new WinLoss(list.Count, list.Count(g => g.Win));
    }
}
