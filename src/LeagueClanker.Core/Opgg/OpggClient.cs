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

/// <summary>A set of items or spells and how often it was picked. <see cref="Ids"/> can repeat, e.g. two potions.</summary>
public sealed record OpggChoice(IReadOnlyList<int> Ids, int Games, int Wins)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
}

/// <param name="MaxOrder">Which ability to max first, second and third, e.g. Q, W, E.</param>
/// <param name="Levels">The ability taken at each level, from the most played order.</param>
public sealed record OpggSkillOrder(IReadOnlyList<string> MaxOrder, IReadOnlyList<string> Levels, int Games, int Wins)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
}

/// <summary>How a champion does in one role, from op.gg's champion list.</summary>
/// <param name="RoleRate">Share of the champion's games in this role, 0-1.</param>
/// <param name="Tier">op.gg's tier, 1 (best) to 5. <see cref="Rank"/> orders champions within the role.</param>
public sealed record OpggRoleStats(double RoleRate, double WinRate, double BanRate, int Tier, int Rank, int Games);

/// <summary>One champion in one role (or ARAM): its rune pages, matchups, the role it's played in most, and its build.</summary>
public sealed record OpggChampion(IReadOnlyList<OpggPage> Pages, IReadOnlyList<OpggMatchup> Matchups, Position MainRole)
{
    public int TotalGames => Pages.Sum(p => p.Games);

    /// <summary>Summoner spell pairs, most played first.</summary>
    public IReadOnlyList<OpggChoice> Spells { get; init; } = [];

    public IReadOnlyList<OpggChoice> StarterItems { get; init; } = [];

    /// <summary>The first three finished items, in buy order.</summary>
    public IReadOnlyList<OpggChoice> CoreItems { get; init; } = [];

    public IReadOnlyList<OpggChoice> Boots { get; init; } = [];

    /// <summary>Single items bought after the core (fourth to sixth item), most played first.</summary>
    public IReadOnlyList<OpggChoice> LaterItems { get; init; } = [];

    public OpggSkillOrder? SkillOrder { get; init; }
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
    private IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>>? _roleStats;

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

    /// <summary>For every champion (by numeric id), how it does in each role it's played in.</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>>> GetRoleStatsAsync(CancellationToken ct)
    {
        if (_roleStats is not null)
            return _roleStats;

        using var response = await _http.GetAsync("ranked?tier=all", ct);
        return _roleStats = ParseRoleStats(await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(ct));
    }

    /// <summary>op.gg's role names. Without a role, guess from the playstyle.</summary>
    public static Position RoleFor(Position position, Archetype playstyle) => position != Position.None
        ? position
        : playstyle switch
        {
            Archetype.Marksman or Archetype.OnHit => Position.Bottom,
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

        IReadOnlyList<OpggChoice> Choices(string property) => Array(data, property)
            .Where(c => c.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            .Select(c => new OpggChoice(c.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32()).ToList(),
                c.GetProperty("play").GetInt32(), c.GetProperty("win").GetInt32()))
            .OrderByDescending(c => c.Games)
            .ToList();

        var skills = Array(data, "skill_masteries").OrderByDescending(s => s.GetProperty("play").GetInt32()).FirstOrDefault();
        var skillOrder = skills.ValueKind == JsonValueKind.Object
            ? new OpggSkillOrder(
                Array(skills, "ids").Select(i => i.GetString() ?? "").ToList(),
                Array(skills, "builds").OrderByDescending(b => b.GetProperty("play").GetInt32()).Select(b => Array(b, "order").Select(o => o.GetString() ?? "").ToList())
                    .FirstOrDefault() ?? [],
                skills.GetProperty("play").GetInt32(), skills.GetProperty("win").GetInt32())
            : null;

        return new OpggChampion(pages, matchups, mainRole)
        {
            Spells = Choices("summoner_spells"),
            StarterItems = Choices("starter_items"),
            CoreItems = Choices("core_items"),
            Boots = Choices("boots"),
            LaterItems = Choices("last_items"),
            SkillOrder = skillOrder,
        };
    }

    internal static IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>> ParseRoleStats(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>>();
        foreach (var champion in Array(doc.RootElement, "data"))
        {
            var roles = new Dictionary<Position, OpggRoleStats>();
            foreach (var position in Array(champion, "positions"))
            {
                var role = Positions.Parse(position.GetProperty("name").GetString());
                if (role == Position.None || roles.ContainsKey(role) || !position.TryGetProperty("stats", out var stats))
                    continue;
                var tier = stats.TryGetProperty("tier_data", out var tierData) ? tierData : default;
                roles[role] = new OpggRoleStats(
                    Number(stats, "role_rate"), Number(stats, "win_rate"), Number(stats, "ban_rate"),
                    tier.ValueKind == JsonValueKind.Object ? (int)Number(tier, "tier") : 5,
                    tier.ValueKind == JsonValueKind.Object ? (int)Number(tier, "rank") : 999,
                    (int)Number(stats, "play"));
            }
            result[champion.GetProperty("id").GetInt32()] = roles;
        }
        return result;
    }

    // op.gg sends null for missing numbers, like the ban rate of an unbannable champion.
    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
