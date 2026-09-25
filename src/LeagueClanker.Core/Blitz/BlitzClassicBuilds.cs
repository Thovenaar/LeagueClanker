using System.Net.Http.Json;
using System.Text.Json;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Blitz;

/// <summary>
/// League Classic builds from Blitz.gg's champion pages. op.gg has no League Classic data; Blitz has curated builds per
/// champion (for Miss Fortune an AD and a crit build) with the classic item ids. They have no win rates, so the game's
/// situation and your items choose between them. The API behind the page is unofficial: callers fall back when it fails.
/// </summary>
public sealed class BlitzClassicBuilds(HttpClient? http = null)
{
    public const string Source = "Blitz";

    private readonly HttpClient _http = http ?? CreateHttpClient();

    /// <param name="championKey">The champion's usual key; League Classic numbers it from 60000.</param>
    public async Task<IReadOnlyList<MetaBuild>> LoadAsync(int championKey, Position position, ItemCatalog items, CancellationToken ct)
    {
        var url = $"https://data.v2.iesdev.com/api/v1/query_objects/prod/lol/champion_cms_builds?champion_id={ChampionCatalog.LeagueClassicKeyOffset + championKey}&queue_id=CLASSIC_5x5";
        return Parse(await _http.GetStringAsync(url, ct), position, items);
    }

    /// <summary>One <see cref="MetaBuild"/> per Blitz build, for your role when Blitz has builds for it.</summary>
    public static IReadOnlyList<MetaBuild> Parse(string json, Position position, ItemCatalog items)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var builds = data.EnumerateArray().Select(b => (
            Role: Positions.Parse(b.GetStringOrEmpty("individual_position")),
            Label: Label(b),
            Core: Ids(b, "coreItems"),
            Optional: Ids(b, "optionalItems"),
            Later: new[] { "slot4", "slot5", "slot6", "slot7" }.SelectMany(slot => Ids(b, slot)).ToList())).ToList();
        if (position != Position.None && builds.Any(b => b.Role == position))
            builds = builds.Where(b => b.Role == position).ToList();

        ItemInfo? Legendary(int id) => items.Get(id) is { Kind: ItemKind.Legendary } item && item.Maps.Contains(GameModes.LeagueClassicMap) ? item : null;
        return builds
            .Select(b =>
            {
                var core = b.Core.Select(Legendary).OfType<ItemInfo>().DistinctBy(i => i.Id).ToList();
                var later = b.Later.Select(Legendary).OfType<ItemInfo>().Where(i => core.All(c => c.Id != i.Id)).DistinctBy(i => i.Id).Take(MetaBuilds.LaterCount).ToList();
                if (core.Count == 0)
                    return null;
                var inBuild = core.Concat(later).Select(i => i.Id).ToHashSet();
                return new MetaBuild(MetaBuilds.StyleOf(core), core, later, 0, 0.5)
                {
                    Source = Source,
                    Label = b.Label,
                    Situational = b.Optional.Select(Legendary).OfType<ItemInfo>().Where(i => !inBuild.Contains(i.Id)).DistinctBy(i => i.Id).ToList(),
                    Boots = b.Core.Concat(b.Later).Select(items.Get).FirstOrDefault(i => i is { Kind: ItemKind.Boots }),
                };
            })
            .OfType<MetaBuild>()
            .DistinctBy(b => b.Key)
            .ToList();
    }

    // Blitz tags builds "AD", "Crit", "AP"...: "AD" stays, "Crit" becomes "crit".
    private static string? Label(JsonElement build) =>
        build.TryGetProperty("name_tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0
            ? string.Join(" ", tags.EnumerateArray().Select(t => t.GetString() ?? "").Select(t => t.Length <= 2 ? t : t.ToLowerInvariant()))
            : null;

    private static List<int> Ids(JsonElement build, string property) =>
        build.TryGetProperty(property, out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Number).Select(i => i.GetInt32()).ToList()
            : [];

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LeagueClanker (+https://github.com/Thovenaar/LeagueClanker)");
        return http;
    }
}
