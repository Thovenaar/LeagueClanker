using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace LeagueClanker.Core.Augments;

/// <param name="WinRate">Share of Mayhem games won with the card, 0-1, over all champions.</param>
/// <param name="PickRate">Share of games the card was taken when offered, 0-1.</param>
public sealed record CommunityAugmentStat(string Name, double WinRate, double PickRate);

/// <summary>
/// Mayhem augment numbers from arammayhem.com: every card's win rate over all games, and the cards players of
/// your champion take most. The advisor uses them as a base and adds its own reasoning (combos, items, the game).
/// Off with the "community augment stats" setting.
/// </summary>
public sealed partial class CommunityAugments(IReadOnlyList<CommunityAugmentStat> global, IReadOnlyDictionary<string, double> championPicks, string? champion)
{
    public const string Source = "arammayhem.com";

    /// <summary>Points per percentage point of win rate above or below 50%. A 55% card gets +1, a 45% card -1.</summary>
    public const double PointsPerWinRatePoint = 0.2;

    /// <summary>The most a win rate adds or takes away, so a few outliers (Transmute: Prismatic at 65%) don't drown out the rest.</summary>
    public const double MaxWinRatePoints = 1.5;

    /// <summary>Bonus for a card that's among the ones your champion's players take most.</summary>
    public const double ChampionFavoritePoints = 0.4;

    private readonly Dictionary<string, CommunityAugmentStat> _global = global
        .GroupBy(s => AugmentCatalog.Key(s.Name)).ToDictionary(g => g.Key, g => g.First());

    private readonly Dictionary<string, double> _championPicks = championPicks.ToDictionary(p => AugmentCatalog.Key(p.Key), p => p.Value);

    public int Count => _global.Count;
    public string? Champion => champion;

    public CommunityAugmentStat? Get(AugmentInfo augment) => _global.GetValueOrDefault(AugmentCatalog.Key(augment.Name));

    /// <summary>Points for the card from the community numbers, with the reasons shown next to it.</summary>
    public double Points(AugmentInfo augment, List<ScoreReason>? reasons)
    {
        var points = 0.0;
        if (Get(augment) is { } stat)
        {
            var fromWinRate = Math.Clamp((stat.WinRate - 0.5) * 100 * PointsPerWinRatePoint, -MaxWinRatePoints, MaxWinRatePoints);
            points += fromWinRate;
            reasons?.Add(new($"{stat.WinRate:P1} win rate in Mayhem ({Source})", fromWinRate));
        }
        if (champion is not null && _championPicks.TryGetValue(AugmentCatalog.Key(augment.Name), out var appearance))
        {
            points += ChampionFavoritePoints;
            reasons?.Add(new($"a top pick on {champion} ({appearance:P0} of games)", ChampionFavoritePoints));
        }
        return points;
    }

    /// <summary>Reads the ranking on arammayhem.com/augments/: one row per card with its rarity, win rate and pick rate.</summary>
    public static IReadOnlyList<CommunityAugmentStat> ParseRanking(string html) =>
        RankRow().Matches(html)
            .Select(m => (Row: m.Value, Name: Alt().Match(m.Value), Win: RowWinRate().Match(m.Value), Pick: RowPickRate().Match(m.Value)))
            .Where(r => r.Name.Success && r.Win.Success && !r.Row.Contains("data-availability=\"disabled\"", StringComparison.Ordinal))
            .Select(r => new CommunityAugmentStat(WebUtility.HtmlDecode(r.Name.Groups[1].Value), Percent(r.Win), r.Pick.Success ? Percent(r.Pick) : 0))
            .ToList();

    /// <summary>Reads "Best Augments for Garen" on a champion's build page: card name to how often its players take it.</summary>
    public static IReadOnlyDictionary<string, double> ParseChampion(string html)
    {
        var start = html.IndexOf(">Best Augments for ", StringComparison.Ordinal);
        if (start < 0)
            return new Dictionary<string, double>();
        return ChampionEntry().Matches(html[start..])
            .GroupBy(m => WebUtility.HtmlDecode(m.Groups[1].Value))
            .ToDictionary(g => g.Key, g => Percent(g.First()));
    }

    /// <summary>arammayhem.com's page name for a champion: "Dr. Mundo" is "drmundo", "Kai'Sa" is "kaisa".</summary>
    public static string ChampionSlug(string championName) => new(championName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static double Percent(Match m) => double.Parse(m.Groups[m.Groups.Count - 1].Value, CultureInfo.InvariantCulture) / 100;

    [GeneratedRegex(@"<a href=""/augments/[^""]+/"" class=""augment-rank-row.*?</a>", RegexOptions.Singleline)]
    private static partial Regex RankRow();

    [GeneratedRegex(@"alt=""([^""]+)""")]
    private static partial Regex Alt();

    [GeneratedRegex(@"text-right font-data text-base[^""]*"">([\d.]+)%")]
    private static partial Regex RowWinRate();

    [GeneratedRegex(@"hidden text-right font-data text-sm[^""]*"">([\d.]+)%")]
    private static partial Regex RowPickRate();

    [GeneratedRegex(@"title=""([^""]+)""[^>]*>[^<]*</div><div[^>]*><span[^>]*>Appearance rate: <span[^>]*>([\d.]+)%")]
    private static partial Regex ChampionEntry();
}

/// <summary>Downloads the arammayhem.com pages and caches them for a day, like the wiki's card data.</summary>
public sealed class CommunityAugmentClient(HttpClient? http = null, string? cacheDirectory = null)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly HttpClient _http = http ?? CreateHttpClient();

    private readonly string _cacheDirectory =
        cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueClanker", "augments");

    /// <summary>The global ranking, plus your champion's favorites when <paramref name="championName"/> is given.</summary>
    public async Task<CommunityAugments> LoadAsync(string? championName, CancellationToken ct = default)
    {
        var ranking = CommunityAugments.ParseRanking(await GetCachedAsync("https://arammayhem.com/augments/", "arammayhem-ranking.html", ct));
        IReadOnlyDictionary<string, double> picks = new Dictionary<string, double>();
        if (championName is not null)
        {
            var slug = CommunityAugments.ChampionSlug(championName);
            try
            {
                picks = CommunityAugments.ParseChampion(await GetCachedAsync($"https://arammayhem.com/build/{slug}/", $"arammayhem-{slug}.html", ct));
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"Loading {championName}'s augments from {CommunityAugments.Source}", ex);
            }
        }
        return new CommunityAugments(ranking, picks, championName);
    }

    private async Task<string> GetCachedAsync(string url, string file, CancellationToken ct)
    {
        var path = Path.Combine(_cacheDirectory, file);
        var cached = File.Exists(path);
        if (cached && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge)
            return await File.ReadAllTextAsync(path, ct);
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(path, html, ct);
            return html;
        }
        catch (HttpRequestException) when (cached)
        {
            return await File.ReadAllTextAsync(path, ct); // offline: yesterday's numbers beat none
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LeagueClanker (+https://github.com/Thovenaar/LeagueClanker)");
        return http;
    }
}
