using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Augments;

/// <summary>
/// Downloads ARAM: Mayhem and Arena augment data from the League of Legends Wiki (CC BY-SA 3.0) and caches it for a day.
/// Riot's own static data has neither.
/// </summary>
public sealed class AugmentDataClient(HttpClient? http = null, string? cacheDirectory = null)
{
    public const string MayhemModuleUrl = "https://wiki.leagueoflegends.com/en-us/Module:MayhemAugmentData/data?action=raw";
    public const string ArenaModuleUrl = "https://wiki.leagueoflegends.com/en-us/Module:ArenaAugmentData/data?action=raw";
    public const string Attribution = "Augment data: League of Legends Wiki (CC BY-SA 3.0)";

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly HttpClient _http = http ?? CreateHttpClient();

    private readonly string _cacheDirectory =
        cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueClanker", "augments");

    public Task<AugmentCatalog> LoadMayhemAsync(ItemCatalog? items = null, CancellationToken ct = default) => LoadAsync(AugmentSet.Mayhem, items, ct);

    public async Task<AugmentCatalog> LoadAsync(AugmentSet set, ItemCatalog? items = null, CancellationToken ct = default)
    {
        var source = await GetSourceAsync(set, ct);
        return AugmentCatalog.ParseWikiModule(source, items);
    }

    /// <summary>
    /// Card names in the client's language ("de_DE"), keyed by English name. Empty for English, so nothing changes there.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> LoadLocalNamesAsync(string locale, CancellationToken ct = default)
    {
        if (AugmentTranslations.IsEnglish(locale))
            return new Dictionary<string, IReadOnlyList<string>>();
        var english = await GetCachedAsync(AugmentTranslations.Url("en_US"), "cherry-augments.default.json", ct);
        var local = await GetCachedAsync(AugmentTranslations.Url(locale), $"cherry-augments.{AugmentTranslations.Folder(locale)}.json", ct);
        return AugmentTranslations.Join(english, local);
    }

    private Task<string> GetSourceAsync(AugmentSet set, CancellationToken ct) =>
        set == AugmentSet.Arena ? GetCachedAsync(ArenaModuleUrl, "arena.lua", ct) : GetCachedAsync(MayhemModuleUrl, "mayhem.lua", ct);

    private async Task<string> GetCachedAsync(string url, string file, CancellationToken ct)
    {
        var path = Path.Combine(_cacheDirectory, file);
        var cached = File.Exists(path);
        if (cached && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge)
            return await File.ReadAllTextAsync(path, ct);

        try
        {
            var source = await _http.GetStringAsync(url, ct);
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(path, source, ct);
            return source;
        }
        catch (HttpRequestException) when (cached)
        {
            return await File.ReadAllTextAsync(path, ct); // offline: a stale copy beats nothing
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        // The wiki asks automated clients to identify themselves.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LeagueClanker/0.1 (+https://github.com/Thovenaar/LeagueClanker)");
        return http;
    }
}
