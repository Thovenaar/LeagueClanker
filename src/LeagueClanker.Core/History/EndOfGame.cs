using System.Text.Json;

namespace LeagueClanker.Core.History;

/// <param name="ChampionKey">Your champion, by Riot's numeric id.</param>
/// <param name="Length">Game length in seconds.</param>
public sealed record EndOfGameResult(int ChampionKey, bool Win, double Length)
{
    /// <summary>The same game as the recap: your champion, and a length within two minutes (loading and the end screen differ).</summary>
    public bool Matches(GameRecap recap) =>
        StaticData.ChampionCatalog.NormalizeKey(ChampionKey) == recap.ChampionKey && Math.Abs(Length - recap.DurationSeconds) < 120;
}

/// <summary>
/// The League client's end-of-game screen (lol-end-of-game/v1/eog-stats-block). The game sometimes closes before its
/// own "GameEnd" event reaches the app; the client still knows who won.
/// </summary>
public static class EndOfGame
{
    public static EndOfGameResult? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("localPlayer", out var me) || !me.TryGetProperty("championId", out var champion))
            return null;

        bool? win = null;
        if (root.TryGetProperty("teams", out var teams) && teams.ValueKind == JsonValueKind.Array)
            foreach (var team in teams.EnumerateArray())
                if (team.TryGetProperty("isPlayerTeam", out var mine) && mine.ValueKind == JsonValueKind.True && team.TryGetProperty("isWinningTeam", out var won))
                    win = won.ValueKind == JsonValueKind.True;
        if (win is null && me.TryGetProperty("stats", out var stats))
            win = stats.TryGetProperty("WIN", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() == 1;
        if (win is null)
            return null;

        var length = root.TryGetProperty("gameLength", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : 0;
        return new EndOfGameResult(champion.GetInt32(), win.Value, length);
    }
}
