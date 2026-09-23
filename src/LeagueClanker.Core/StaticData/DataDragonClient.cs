using System.Net.Http.Json;

namespace LeagueClanker.Core.StaticData;

/// <summary>Item and champion data for one patch.</summary>
public sealed record StaticGameData(string Version, ItemCatalog Items, ChampionCatalog Champions)
{
    public string ItemIconUrl(int itemId) => $"https://ddragon.leagueoflegends.com/cdn/{Version}/img/item/{itemId}.png";
    public string ChampionIconUrl(string championId) => $"https://ddragon.leagueoflegends.com/cdn/{Version}/img/champion/{championId}.png";
}

/// <summary>Downloads static data from Riot's Data Dragon CDN and caches it per patch under %LOCALAPPDATA%.</summary>
public sealed class DataDragonClient(HttpClient? http = null, string? cacheDirectory = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { BaseAddress = new Uri("https://ddragon.leagueoflegends.com/") };

    private readonly string _cacheDirectory = cacheDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueClanker", "ddragon");

    public async Task<StaticGameData> LoadAsync(CancellationToken ct = default)
    {
        var version = await GetLatestVersionAsync(ct);
        var itemJson = await GetCachedAsync(version, "item.json", ct);
        var championJson = await GetCachedAsync(version, "champion.json", ct);
        return new StaticGameData(version, ItemCatalog.Parse(itemJson), ChampionCatalog.Parse(championJson));
    }

    private async Task<string> GetLatestVersionAsync(CancellationToken ct)
    {
        try
        {
            var versions = await _http.GetFromJsonAsync<string[]>("api/versions.json", ct);
            if (versions is { Length: > 0 })
                return versions[0];
        }
        catch (HttpRequestException) when (NewestCachedVersion() is not null)
        {
            // Offline: fall back to the newest patch we have on disk.
        }
        return NewestCachedVersion() ?? throw new InvalidOperationException("Data Dragon is unreachable and no cached data exists.");
    }

    private async Task<string> GetCachedAsync(string version, string file, CancellationToken ct)
    {
        var path = Path.Combine(_cacheDirectory, version, file);
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path, ct);

        var json = await _http.GetStringAsync($"cdn/{version}/data/en_US/{file}", ct);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, ct);
        return json;
    }

    private string? NewestCachedVersion()
    {
        if (!Directory.Exists(_cacheDirectory))
            return null;

        return Directory.GetDirectories(_cacheDirectory)
            .Where(d => File.Exists(Path.Combine(d, "item.json")) && File.Exists(Path.Combine(d, "champion.json")))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version())
            .FirstOrDefault();
    }
}
