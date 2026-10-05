using System.Text.Json;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Augments;

/// <summary>
/// Where Community Dragon keeps each card's icon, the one the HUD shows for cards you took. Its cherry-augments.json
/// lists Arena and Mayhem cards together; Mayhem's have ids from 1000.
/// </summary>
public static class AugmentIcons
{
    private const string AssetRoot = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/";
    private const int FirstMayhemId = 1000;

    /// <summary>English name, in <see cref="AugmentCatalog.Key"/> form, to its icon URLs in one card set.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Urls(string englishJson, AugmentSet set)
    {
        using var doc = JsonDocument.Parse(englishJson);
        return doc.RootElement.EnumerateArray()
            .Where(a => a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                && (id.GetInt32() >= FirstMayhemId) == (set == AugmentSet.Mayhem))
            .Select(a => (Key: AugmentCatalog.Key(a.GetStringOrEmpty("nameTRA")), Path: a.GetStringOrEmpty("augmentSmallIconPath")))
            .Where(a => a.Key.Length > 0 && a.Path.Length > 0)
            .GroupBy(a => a.Key)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(a => Url(a.Path)).Distinct().ToList());
    }

    /// <summary>A card's icon URLs. Community Dragon names quest cards without the wiki's "Quest:" in front.</summary>
    public static IReadOnlyList<string> For(IReadOnlyDictionary<string, IReadOnlyList<string>> urls, string name) =>
        urls.GetValueOrDefault(AugmentCatalog.Key(name))
        ?? (name.StartsWith("Quest:", StringComparison.OrdinalIgnoreCase) ? urls.GetValueOrDefault(AugmentCatalog.Key(name["Quest:".Length..])) : null)
        ?? [];

    /// <summary>"/lol-game-data/assets/ASSETS/UX/Kiwi/..." as a download URL. Community Dragon lowercases its paths.</summary>
    public static string Url(string path) =>
        AssetRoot + (path.StartsWith("/lol-game-data/assets/", StringComparison.OrdinalIgnoreCase) ? path["/lol-game-data/assets/".Length..] : path.TrimStart('/')).ToLowerInvariant();
}
