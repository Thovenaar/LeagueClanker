using System.Text.Json;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Augments;

/// <summary>
/// Augment names in the League client's language, so screen reading works in a German or Korean client too.
/// Community Dragon's cherry-augments.json lists every Arena and Mayhem card per language; the English file and the
/// local one are joined on the card's id, then matched to the wiki's English names.
/// </summary>
public static class AugmentTranslations
{
    public const string Attribution = "Augment names in other languages: Community Dragon";

    /// <summary>The League client's locale ("de_DE") as Community Dragon's folder name ("de_de"). English is "default".</summary>
    public static string Folder(string locale) => IsEnglish(locale) ? "default" : locale.Replace('-', '_').ToLowerInvariant();

    public static bool IsEnglish(string? locale) => string.IsNullOrWhiteSpace(locale) || locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public static string Url(string locale) =>
        $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/{Folder(locale)}/v1/cherry-augments.json";

    /// <summary>English name, in <see cref="AugmentCatalog.Key"/> form, to its names in the other language.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Join(string englishJson, string localJson)
    {
        var local = Names(localJson).ToDictionary(n => n.Id, n => n.Name);
        return Names(englishJson)
            .Where(n => local.ContainsKey(n.Id))
            .GroupBy(n => AugmentCatalog.Key(n.Name))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(n => local[n.Id]).Where(name => name.Length > 0).Distinct().ToList());
    }

    private static IEnumerable<(int Id, string Name)> Names(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Where(a => a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            .Select(a => (a.GetProperty("id").GetInt32(), a.GetStringOrEmpty("nameTRA").Trim()))
            .ToList();
    }
}
