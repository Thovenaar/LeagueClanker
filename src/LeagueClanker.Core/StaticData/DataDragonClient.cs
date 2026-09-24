using System.Net.Http.Json;
using System.Text.Json;
using LeagueClanker.Core.Runes;

namespace LeagueClanker.Core.StaticData;

/// <summary>Item, champion and rune data for one patch.</summary>
public sealed record StaticGameData(string Version, ItemCatalog Items, ChampionCatalog Champions)
{
    /// <summary>Empty when the rune data couldn't be loaded; the item advisor works without it.</summary>
    public RuneCatalog Runes { get; init; } = RuneCatalog.Empty;

    public SummonerSpellCatalog Spells { get; init; } = SummonerSpellCatalog.Empty;

    public string SpellIconUrl(int spellId) =>
        Spells.Get(spellId) is { } spell ? $"https://ddragon.leagueoflegends.com/cdn/{Version}/img/spell/{spell.Image}" : "";

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
        return new StaticGameData(version, ItemCatalog.Parse(itemJson), ChampionCatalog.Parse(championJson))
        {
            Runes = await LoadOptionalAsync(version, "runesReforged.json", RuneCatalog.Parse, RuneCatalog.Empty, ct),
            Spells = await LoadOptionalAsync(version, "summoner.json", SummonerSpellCatalog.Parse, SummonerSpellCatalog.Empty, ct),
        };
    }

    // Older caches predate runes and spells; offline, the build advisor still runs without them.
    private async Task<T> LoadOptionalAsync<T>(string version, string file, Func<string, T> parse, T empty, CancellationToken ct)
    {
        try
        {
            return parse(await GetCachedAsync(version, file, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Log.Error($"Couldn't load {file}", ex);
            return empty;
        }
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
