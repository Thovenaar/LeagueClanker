using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeagueClanker.Core.StaticData;

/// <summary>
/// Stat names as they appear in the item description's &lt;stats&gt; block. Percentage stats are prefixed with "%".
/// Data Dragon's structured "stats" object misses lethality, penetration and haste, so the description is the source of truth.
/// </summary>
public static class Stat
{
    public const string AttackDamage = "Attack Damage";
    public const string AbilityPower = "Ability Power";
    public const string Armor = "Armor";
    public const string MagicResist = "Magic Resist";
    public const string Health = "Health";
    public const string AttackSpeed = "%Attack Speed";
    public const string CritChance = "%Critical Strike Chance";
    public const string CritDamage = "%Critical Strike Damage";
    public const string Lethality = "Lethality";
    public const string ArmorPenPercent = "%Armor Penetration";
    public const string MagicPen = "Magic Penetration";
    public const string MagicPenPercent = "%Magic Penetration";
    public const string AbilityHaste = "Ability Haste";
    public const string LifeSteal = "%Life Steal";
    public const string Omnivamp = "%Omnivamp";
    public const string MoveSpeed = "Move Speed";
    public const string MoveSpeedPercent = "%Move Speed";
    public const string Mana = "Mana";
    public const string ManaRegen = "%Base Mana Regen";
    public const string HealthRegen = "%Base Health Regen";
    public const string HealShieldPower = "%Heal and Shield Power";
    public const string Tenacity = "%Tenacity";
}

/// <summary>Special effects detected from item passives/actives. These drive the situational rules.</summary>
[Flags]
public enum ItemTraits
{
    None = 0,
    AntiHeal = 1 << 0,        // Applies (Grievous) Wounds
    AntiShield = 1 << 1,      // Reduces enemy shields (Serpent's Fang)
    CritReduction = 1 << 2,   // Take less damage from critical strikes (Randuin's)
    AttackSpeedSlow = 1 << 3, // Reduces nearby enemies' attack speed (Frozen Heart)
    Stasis = 1 << 4,          // Zhonya's, Guardian Angel
    SpellShield = 1 << 5,     // Banshee's, Edge of Night
    Cleanse = 1 << 6,         // Removes crowd control (Quicksilver)
    MaxHealthDamage = 1 << 7, // %max or %bonus health damage, good against tanks
    ResistShred = 1 << 8,     // Reduces the target's armor or magic resist
    GrantsShield = 1 << 9,    // Gives its owner or allies shields
    Sustain = 1 << 10,        // Life steal, omnivamp or heal power, in stats or passives
}

public enum ItemKind
{
    Other,
    Component,
    Legendary,
    Boots,
}

public sealed record ItemInfo
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required int TotalGold { get; init; }
    public required ItemKind Kind { get; init; }
    public required IReadOnlyDictionary<string, double> Stats { get; init; }
    public required ItemTraits Traits { get; init; }
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> Passives { get; init; } = new HashSet<string>();
    public IReadOnlyList<int> BuildsFrom { get; init; } = [];

    /// <summary>Data Dragon map ids the item can be bought on (11 = Summoner's Rift, 12 = Howling Abyss).</summary>
    public IReadOnlySet<int> Maps { get; init; } = new HashSet<int> { GameModes.SummonersRiftMap };

    public bool IsBoots => Tags.Contains("Boots");

    public double Stat(string name) => Stats.TryGetValue(name, out var value) ? value : 0;

    public bool Has(ItemTraits trait) => (Traits & trait) != 0;

    public override string ToString() => Name;
}

public sealed partial class ItemCatalog
{
    private const int LegendaryMinGold = 2200;

    // Items with 6-digit ids are copies for other modes (Arena, ARAM variants, Mayhem specials) with changed stats or prices.
    // The standard versions are available on those maps too, so only standard ids are recommended.
    private const int MaxStandardItemId = 9999;

    private readonly Dictionary<int, ItemInfo> _byId;

    public ItemCatalog(IEnumerable<ItemInfo> items)
    {
        _byId = items.ToDictionary(i => i.Id);
    }

    /// <summary>Summoner's Rift legendaries.</summary>
    public IReadOnlyList<ItemInfo> Legendaries => LegendariesOn(GameModes.SummonersRiftMap);

    /// <summary>Summoner's Rift tier 2 boots.</summary>
    public IReadOnlyList<ItemInfo> Boots => BootsOn(GameModes.SummonersRiftMap);

    public IEnumerable<ItemInfo> All => _byId.Values;

    public IReadOnlyList<ItemInfo> LegendariesOn(int map) => OfKind(ItemKind.Legendary, map);

    public IReadOnlyList<ItemInfo> BootsOn(int map) => OfKind(ItemKind.Boots, map);

    private IReadOnlyList<ItemInfo> OfKind(ItemKind kind, int map) =>
        _byId.Values.Where(i => i.Kind == kind && i.Maps.Contains(map)).OrderBy(i => i.Id).ToList();

    public ItemInfo? Get(int id) => _byId.GetValueOrDefault(id);

    public ItemInfo? FindByName(string name) =>
        _byId.Values.Where(i => i.Id <= MaxStandardItemId).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses Data Dragon's item.json.</summary>
    public static ItemCatalog Parse(string itemJson)
    {
        using var doc = JsonDocument.Parse(itemJson);
        var items = new List<ItemInfo>();
        foreach (var entry in doc.RootElement.GetProperty("data").EnumerateObject())
        {
            if (int.TryParse(entry.Name, out var id))
                items.Add(ParseItem(id, entry.Value));
        }
        return new ItemCatalog(items);
    }

    private static ItemInfo ParseItem(int id, JsonElement json)
    {
        var description = json.GetStringOrEmpty("description");
        var tags = json.GetStringArray("tags").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var from = json.GetStringArray("from").Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0).ToList();
        var gold = json.GetProperty("gold");

        return new ItemInfo
        {
            Id = id,
            Name = json.GetStringOrEmpty("name"),
            TotalGold = gold.GetProperty("total").GetInt32(),
            Kind = ClassifyKind(id, json, tags, from),
            Stats = ParseStats(description),
            Traits = DetectTraits(description),
            Tags = tags,
            Passives = PassiveRegex().Matches(description).Select(m => m.Groups[1].Value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase),
            BuildsFrom = from,
            Maps = ParseMaps(json),
        };
    }

    private static HashSet<int> ParseMaps(JsonElement json) =>
        json.TryGetProperty("maps", out var maps)
            ? maps.EnumerateObject().Where(m => m.Value.GetBoolean() && int.TryParse(m.Name, out _)).Select(m => int.Parse(m.Name)).ToHashSet()
            : [];

    private static ItemKind ClassifyKind(int id, JsonElement json, HashSet<string> tags, List<int> from)
    {
        var gold = json.GetProperty("gold");
        var purchasable = gold.GetProperty("purchasable").GetBoolean();
        var inStore = !json.TryGetProperty("inStore", out var store) || store.GetBoolean();
        var restricted = json.TryGetProperty("requiredChampion", out _) || json.TryGetProperty("requiredAlly", out _);

        var soldSomewhere = json.TryGetProperty("maps", out var maps) && maps.EnumerateObject().Any(m => m.Value.GetBoolean());
        if (!purchasable || !inStore || restricted || !soldSomewhere || id > MaxStandardItemId)
            return ItemKind.Other;
        if (tags.Contains("Consumable") || tags.Contains("Trinket"))
            return ItemKind.Other;

        // Tier 2 boots build out of basic Boots (1001). Tier 3 upgrades build out of tier 2.
        if (tags.Contains("Boots"))
            return from.Contains(1001) ? ItemKind.Boots : ItemKind.Other;

        if (json.GetStringArray("into").Any())
            return ItemKind.Component;

        return gold.GetProperty("total").GetInt32() >= LegendaryMinGold ? ItemKind.Legendary : ItemKind.Other;
    }

    internal static Dictionary<string, double> ParseStats(string description)
    {
        var stats = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var block = StatsBlockRegex().Match(description);
        if (!block.Success)
            return stats;

        foreach (Match m in StatLineRegex().Matches(block.Groups[1].Value))
        {
            var raw = m.Groups[1].Value;
            var isPercent = raw.EndsWith('%');
            var value = double.Parse(raw.TrimEnd('%'), CultureInfo.InvariantCulture);
            var name = (isPercent ? "%" : "") + m.Groups[2].Value.Trim();
            stats[name] = stats.GetValueOrDefault(name) + value;
        }
        return stats;
    }

    internal static ItemTraits DetectTraits(string description)
    {
        var text = WhitespaceRegex().Replace(TagRegex().Replace(description, " "), " ");
        var traits = ItemTraits.None;

        if (Regex.IsMatch(text, @"\bWounds\b")) traits |= ItemTraits.AntiHeal;
        if (Regex.IsMatch(text, @"Shield Reaver|reduces? (the )?Shields", RegexOptions.IgnoreCase)) traits |= ItemTraits.AntiShield;
        if (Regex.IsMatch(text, @"less damage from Critical Strikes", RegexOptions.IgnoreCase)) traits |= ItemTraits.CritReduction;
        if (Regex.IsMatch(text, @"Reduce the Attack Speed", RegexOptions.IgnoreCase)) traits |= ItemTraits.AttackSpeedSlow;
        if (Regex.IsMatch(text, @"\bStasis\b")) traits |= ItemTraits.Stasis;
        if (Regex.IsMatch(text, @"Spell Shield", RegexOptions.IgnoreCase)) traits |= ItemTraits.SpellShield;
        if (Regex.IsMatch(text, @"removes? all crowd control", RegexOptions.IgnoreCase)) traits |= ItemTraits.Cleanse;
        if (Regex.IsMatch(text, @"max Health (magic |physical |true )?damage|based on their (bonus|max(imum)?) Health", RegexOptions.IgnoreCase)) traits |= ItemTraits.MaxHealthDamage;
        if (Regex.IsMatch(text, @"reduces (the target.s |their )?(Armor|Magic Resist) by", RegexOptions.IgnoreCase)) traits |= ItemTraits.ResistShred;
        if (Regex.IsMatch(text, @"Omnivamp|Life Steal|Heal and Shield Power", RegexOptions.IgnoreCase)) traits |= ItemTraits.Sustain;

        var grantsShield = description.Contains("<shield>", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"grants? (a |an )?(\w+ )?Shield\b", RegexOptions.IgnoreCase);
        if (grantsShield && !traits.HasFlag(ItemTraits.AntiShield) && !traits.HasFlag(ItemTraits.SpellShield))
            traits |= ItemTraits.GrantsShield;

        return traits;
    }

    [GeneratedRegex(@"<stats>(.*?)</stats>", RegexOptions.Singleline)]
    private static partial Regex StatsBlockRegex();

    [GeneratedRegex(@"<attention>\s*([\d.]+%?)\s*</attention>\s*([^<]+)")]
    private static partial Regex StatLineRegex();

    [GeneratedRegex(@"<passive>([^<]+)</passive>")]
    private static partial Regex PassiveRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal static class JsonElementExtensions
{
    public static string GetStringOrEmpty(this JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public static IEnumerable<string> GetStringArray(this JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
            : [];
}
