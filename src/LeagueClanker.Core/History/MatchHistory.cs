using System.Text.Json.Nodes;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.History;

/// <summary>
/// Reads the League client's match history (/lol-match-history). The list only holds your own row per game; a game's
/// detail call has all ten players, which is what finds your lane opponent. Riot doesn't document the shape, so the
/// parser skips what it doesn't recognize rather than failing.
/// </summary>
public static class MatchHistory
{
    /// <summary>Summoner's Rift games, League Classic included, from a list or detail response. Other modes are skipped.</summary>
    /// <param name="puuid">Your account id, to find your row among ten. With only one row, that row is you.</param>
    public static IReadOnlyList<PlayedGame> Parse(string json, string? puuid)
    {
        var root = JsonNode.Parse(json);
        var games = root?["games"]?["games"] as JsonArray ?? (root?["gameId"] is not null ? new JsonArray(root.DeepClone()) : null);
        return games?.Select(g => ParseGame(g, puuid)).OfType<PlayedGame>().ToList() ?? [];
    }

    /// <summary>All games in a list response, whatever the mode. Fewer than asked for means there are no older games.</summary>
    public static int GameCount(string json) => (JsonNode.Parse(json)?["games"]?["games"] as JsonArray)?.Count ?? 0;

    /// <summary>Summoner's Rift game ids in a list response whose rows are incomplete, newest first, so their details can be fetched.</summary>
    public static IReadOnlyList<long> GamesWithoutOpponents(string json) =>
        (JsonNode.Parse(json)?["games"]?["games"] as JsonArray ?? [])
            .Where(g => IsSummonersRift(g) && (g?["participants"] as JsonArray)?.Count < 10 && g?["gameId"] is not null)
            .Select(g => g!["gameId"]!.GetValue<long>())
            .ToList();

    // Normal, ranked, Swiftplay and League Classic ("JADE" on map 453) games. ARAM, Arena and the rotating modes
    // have no lanes to compare.
    private static bool IsSummonersRift(JsonNode? game) =>
        Text(game?["gameMode"])?.ToUpperInvariant() is "CLASSIC" or "SWIFTPLAY" or "JADE";

    private static PlayedGame? ParseGame(JsonNode? game, string? puuid)
    {
        if (game is null || !IsSummonersRift(game))
            return null;
        if (game["participants"] is not JsonArray participants || participants.Count == 0)
            return null;

        var myId = (game["participantIdentities"] as JsonArray ?? [])
            .FirstOrDefault(p => puuid is not null && Text(p?["player"]?["puuid"]) == puuid)?["participantId"]?.GetValue<int>();
        var me = participants.FirstOrDefault(p => myId is not null ? p?["participantId"]?.GetValue<int>() == myId : participants.Count == 1);
        if (me?["championId"] is null || me["stats"]?["win"] is null)
            return null;

        var position = PositionOf(me);
        var team = me["teamId"]?.GetValue<int>();
        var opponent = position == Position.None
            ? null
            : participants.FirstOrDefault(p => p?["teamId"]?.GetValue<int>() != team && PositionOf(p!) == position)?["championId"]?.GetValue<int>();

        var created = game["gameCreation"]?.GetValue<long>() ?? 0;
        return new PlayedGame(DateTimeOffset.FromUnixTimeMilliseconds(created).LocalDateTime, ChampionCatalog.NormalizeKey(me["championId"]!.GetValue<int>()),
            me["stats"]!["win"]!.GetValue<bool>(), position, opponent is { } key ? ChampionCatalog.NormalizeKey(key) : null);
    }

    // Newer responses have "teamPosition"; older ones a lane and a role, where bottom lane's support is "DUO_SUPPORT".
    private static Position PositionOf(JsonNode participant)
    {
        if (Positions.Parse(Text(participant["teamPosition"])) is var position and not Position.None)
            return position;
        var lane = Text(participant["timeline"]?["lane"])?.ToUpperInvariant();
        var role = Text(participant["timeline"]?["role"])?.ToUpperInvariant();
        return lane switch
        {
            "BOTTOM" or "BOT" => role is "DUO_SUPPORT" or "SUPPORT" ? Position.Support : Position.Bottom,
            _ => Positions.Parse(lane),
        };
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
