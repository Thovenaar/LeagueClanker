using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Augments;

/// <summary>
/// Downloads ARAM: Mayhem augment data from the League of Legends Wiki (CC BY-SA 3.0) and caches it for a day.
/// Riot's own static data has no Mayhem augments.
/// </summary>
public sealed class AugmentDataClient(HttpClient? http = null, string? cacheDirectory = null)
{
    public const string MayhemModuleUrl = "https://wiki.leagueoflegends.com/en-us/Module:MayhemAugmentData/data?action=raw";
    public const string Attribution = "Augment data: League of Legends Wiki (CC BY-SA 3.0)";

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly HttpClient _http = http ?? CreateHttpClient();

    private readonly string _cachePath = Path.Combine(
        cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueClanker", "augments"),
        "mayhem.lua");

    public async Task<AugmentCatalog> LoadMayhemAsync(ItemCatalog? items = null, CancellationToken ct = default)
    {
        var source = await GetSourceAsync(ct);
        return AugmentCatalog.ParseWikiModule(source, items);
    }

    private async Task<string> GetSourceAsync(CancellationToken ct)
    {
        var cached = File.Exists(_cachePath);
        if (cached && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) < MaxAge)
            return await File.ReadAllTextAsync(_cachePath, ct);

        try
        {
            var source = await _http.GetStringAsync(MayhemModuleUrl, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllTextAsync(_cachePath, source, ct);
            return source;
        }
        catch (HttpRequestException) when (cached)
        {
            return await File.ReadAllTextAsync(_cachePath, ct); // offline: a stale copy beats nothing
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
