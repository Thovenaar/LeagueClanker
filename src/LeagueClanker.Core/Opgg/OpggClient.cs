using System.Collections.Concurrent;
using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Runes;

namespace LeagueClanker.Core.Opgg;

public sealed record OpggPage(RunePage Page, int Games, int Wins)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
}

/// <summary>How a champion did against one opponent in the same role. <see cref="Wins"/> are the champion's wins.</summary>
public sealed record OpggMatchup(int OpponentKey, int Games, int Wins)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
}

/// <summary>One champion in one role (or ARAM): its rune pages, its matchups, and the role it's played in most.</summary>
public sealed record OpggChampion(IReadOnlyList<OpggPage> Pages, IReadOnlyList<OpggMatchup> Matchups, Position MainRole)
{
    public int TotalGames => Pages.Sum(p => p.Games);
}

/// <summary>
/// Reads the JSON API behind op.gg's champion pages. It's unofficial and undocumented, so it can change without notice;
/// callers fall back to LeagueClanker's own logic when it fails. Answers are cached for the session, failures aren't.
/// Ranked data covers all ranks: about four times the games of op.gg's default Emerald+ filter, which matters for
/// matchups, where one pairing can have only a hundred games at high rank.
/// </summary>
public sealed class OpggClient : IMatchupData
{
    private static readonly Uri BaseAddress = new("https://lol-api-champion.op.gg/api/global/champions/");

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, OpggChampion?> _champions = new();
    private IReadOnlyDictionary<int, IReadOnlyDictionary<Position, double>>? _roleRates;

    public OpggClient(HttpClient? http = null, string? userAgent = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.BaseAddress ??= BaseAddress;
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent ?? "LeagueClanker (+https://github.com/Thovenaar/LeagueClanker)");
    }

    /// <summary>Null when op.gg has no data for that champion and role.</summary>
    public async Task<OpggChampion?> GetChampionAsync(int championKey, bool aram, Position role, CancellationToken ct)
    {
        var path = aram ? $"aram/{championKey}/none" : $"ranked/{championKey}/{RoleName(role)}?tier=all";
        if (_champions.TryGetValue(path, out var cached))
            return cached;

        using var response = await _http.GetAsync(path, ct);
        var data = response.StatusCode == System.Net.HttpStatusCode.NotFound
            ? null
            : ParseChampion(await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(ct));
        _champions[path] = data;
        return data;
    }

    public async Task<IReadOnlyList<OpggMatchup>> GetMatchupsAsync(int championKey, Position role, CancellationToken ct) =>
        (await GetChampionAsync(championKey, aram: false, role, ct))?.Matchups ?? [];

    /// <summary>For every champion (by numeric id), the share of its ranked games in each role.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<Position, double>>> GetRoleRatesAsync(CancellationToken ct)
    {
        if (_roleRates is not null)
            return _roleRates;

        using var response = await _http.GetAsync("ranked?tier=all", ct);
        return _roleRates = ParseRoleRates(await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(ct));
    }

    /// <summary>op.gg's role names. Without a role, guess from the playstyle.</summary>
    public static Position RoleFor(Position position, Archetype playstyle) => position != Position.None
        ? position
        : playstyle switch
        {
            Archetype.Marksman => Position.Bottom,
            Archetype.Enchanter => Position.Support,
            Archetype.Mage or Archetype.ApAssassin or Archetype.AdAssassin => Position.Middle,
            _ => Position.Top,
        };

    private static string RoleName(Position role) => role switch
    {
        Position.Top => "top",
        Position.Jungle => "jungle",
        Position.Middle => "mid",
        Position.Bottom => "adc",
        Position.Support => "support",
        _ => "top",
    };

    /// <summary>Pages come from "runes" and each "rune_pages" entry's "builds", without duplicates. Matchups from "counters".</summary>
    internal static OpggChampion ParseChampion(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var pages = new List<OpggPage>();
        void Add(JsonElement build)
        {
            if (!build.TryGetProperty("primary_rune_ids", out var primaryIds) || !build.TryGetProperty("stat_mod_ids", out var shards))
                return;
            var ids = primaryIds.EnumerateArray().Concat(build.GetProperty("secondary_rune_ids").EnumerateArray()).Concat(shards.EnumerateArray())
                .Select(e => e.GetInt32()).ToList();
            var page = new RunePage(build.GetProperty("primary_page_id").GetInt32(), build.GetProperty("secondary_page_id").GetInt32(), ids);
            if (pages.All(p => !p.Page.SameAs(page)))
                pages.Add(new OpggPage(page, build.GetProperty("play").GetInt32(), build.GetProperty("win").GetInt32()));
        }

        foreach (var build in Array(data, "runes"))
            Add(build);
        foreach (var group in Array(data, "rune_pages"))
            foreach (var build in Array(group, "builds"))
                Add(build);

        var matchups = Array(data, "counters")
            .Select(c => new OpggMatchup(c.GetProperty("champion_id").GetInt32(), c.GetProperty("play").GetInt32(), c.GetProperty("win").GetInt32()))
            .ToList();

        var mainRole = data.TryGetProperty("summary", out var summary)
            ? Array(summary, "positions").Select(p => Positions.Parse(p.GetProperty("name").GetString())).FirstOrDefault()
            : Position.None;

        return new OpggChampion(pages, matchups, mainRole);
    }

    internal static IReadOnlyDictionary<int, IReadOnlyDictionary<Position, double>> ParseRoleRates(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rates = new Dictionary<int, IReadOnlyDictionary<Position, double>>();
        foreach (var champion in Array(doc.RootElement, "data"))
        {
            var roles = Array(champion, "positions")
                .Select(p => (Role: Positions.Parse(p.GetProperty("name").GetString()), Rate: p.GetProperty("stats").GetProperty("role_rate").GetDouble()))
                .Where(r => r.Role != Position.None)
                .GroupBy(r => r.Role)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Rate));
            rates[champion.GetProperty("id").GetInt32()] = roles;
        }
        return rates;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
