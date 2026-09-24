using System.Collections.Concurrent;
using System.Text.Json;
using LeagueClanker.Core.Analysis;

namespace LeagueClanker.Core.Runes;

/// <summary>
/// The most played rune page on op.gg that fits your playstyle, from the JSON API behind op.gg's champion pages.
/// That API is unofficial and undocumented, so it can change without notice; <see cref="RuneAdvisor"/> then falls back
/// to the rules. Each champion, role and mode is fetched once per session.
/// </summary>
public sealed class OpggRuneSource : IRuneSource
{
    public const string SourceName = "op.gg";

    // Pages below this many games are noise.
    private const int MinGames = 50;

    // With fewer games than this in the role you asked for, the champion's main role is a better sample.
    private const int MinGamesInRole = 1000;

    private static readonly Uri BaseAddress = new("https://lol-api-champion.op.gg/api/global/champions/");

    private readonly HttpClient _http;
    private readonly RuneCatalog _catalog;
    private readonly ConcurrentDictionary<string, OpggChampion?> _cache = new();

    public OpggRuneSource(RuneCatalog catalog, HttpClient? http = null, string? userAgent = null)
    {
        _catalog = catalog;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.BaseAddress ??= BaseAddress;
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent ?? "LeagueClanker (+https://github.com/Thovenaar/LeagueClanker)");
    }

    public async Task<RuneRecommendation?> RecommendAsync(RuneRequest request, CancellationToken ct)
    {
        if (request.Champion.Key <= 0)
            return null;

        var aram = request.Mode is GameMode.Aram or GameMode.AramMayhem;
        var role = aram ? "none" : RoleFor(request.Position, request.Playstyle);
        var data = await LoadAsync(request.Champion.Key, aram, role, ct);

        // No role picked (blind pick, normals) and a guessed role with few games: use the role op.gg says they're played in.
        if (!aram && request.Position == Position.None && data is { MainRole: { } main } && main != role && data.TotalGames < MinGamesInRole)
        {
            role = main;
            data = await LoadAsync(request.Champion.Key, aram, role, ct);
        }
        if (data is null)
            return null;

        var best = data.Pages
            .Where(p => p.Games >= MinGames && _catalog.Validate(p.Page) is null)
            .Where(p => _catalog.Get(p.Page.Keystone) is { } keystone && KeystoneFit.Suits(request.Playstyle, keystone))
            .MaxBy(p => p.Games);
        if (best is null)
            return null;

        var where = aram ? "ARAM" : RoleName(role);
        var reason = $"Most played {request.Playstyle.DisplayName().ToLowerInvariant()} page for {request.Champion.Name} ({where}) on op.gg.";
        return new RuneRecommendation(best.Page, SourceName, [reason]) { Games = best.Games, WinRate = best.WinRate };
    }

    // Only answers are cached. A failed fetch throws, so the next champ select poll tries again.
    private async Task<OpggChampion?> LoadAsync(int championKey, bool aram, string role, CancellationToken ct)
    {
        var path = aram ? $"aram/{championKey}/none" : $"ranked/{championKey}/{role}";
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        var data = await FetchAsync(path, ct);
        _cache[path] = data;
        return data;
    }

    private async Task<OpggChampion?> FetchAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    internal sealed record OpggPage(RunePage Page, int Games, int Wins)
    {
        public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
    }

    internal sealed record OpggChampion(IReadOnlyList<OpggPage> Pages, string? MainRole)
    {
        public int TotalGames => Pages.Sum(p => p.Games);
    }

    /// <summary>Reads the pages from "runes" and from each "rune_pages" entry's "builds", without duplicates.</summary>
    internal static OpggChampion Parse(string json)
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

        if (data.TryGetProperty("runes", out var runes) && runes.ValueKind == JsonValueKind.Array)
            foreach (var build in runes.EnumerateArray())
                Add(build);
        if (data.TryGetProperty("rune_pages", out var runePages) && runePages.ValueKind == JsonValueKind.Array)
            foreach (var group in runePages.EnumerateArray())
                if (group.TryGetProperty("builds", out var builds))
                    foreach (var build in builds.EnumerateArray())
                        Add(build);

        string? mainRole = null;
        if (data.TryGetProperty("summary", out var summary) && summary.TryGetProperty("positions", out var positions)
            && positions.ValueKind == JsonValueKind.Array && positions.GetArrayLength() > 0)
            mainRole = positions[0].GetProperty("name").GetString()?.ToLowerInvariant();

        return new OpggChampion(pages, mainRole);
    }

    // op.gg's role names. Without a role, guess from the playstyle; the main-role check above corrects bad guesses.
    private static string RoleFor(Position position, Archetype playstyle) => position switch
    {
        Position.Top => "top",
        Position.Jungle => "jungle",
        Position.Middle => "mid",
        Position.Bottom => "adc",
        Position.Support => "support",
        _ => playstyle switch
        {
            Archetype.Marksman => "adc",
            Archetype.Enchanter => "support",
            Archetype.Mage or Archetype.ApAssassin or Archetype.AdAssassin => "mid",
            _ => "top",
        },
    };

    private static string RoleName(string role) => role switch
    {
        "adc" => "bottom",
        _ => role,
    };
}
