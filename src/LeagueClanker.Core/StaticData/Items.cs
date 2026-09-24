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

    // League Classic stats. Cooldown reduction stacks additively up to 40%; spell vamp heals from ability damage.
    public const string CooldownReduction = "%Cooldown Reduction";
    public const string SpellVamp = "%Spell Vamp";
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

    /// <summary>League Classic's version of the item, sold on map 453.</summary>
    public bool IsClassic => ItemCatalog.IsClassicId(Id);

    /// <summary>Classic jungle items (Spirit of the ..., Wriggle's Lantern). Only worth it with Smite.</summary>
    public bool IsJungleItem => Tags.Contains("Jungle") || Passives.Contains("Butcher");

    public double Stat(string name) => Stats.TryGetValue(name, out var value) ? value : 0;

    public bool Has(ItemTraits trait) => (Traits & trait) != 0;

    public override string ToString() => Name;
}

public sealed partial class ItemCatalog
{
    private const int LegendaryMinGold = 2200;

    // Classic items are cheaper: Sword of the Occult costs 1200, and Doran's items (400-475) are the only cheaper finished items.
    private const int ClassicLegendaryMinGold = 1100;

    // Items with 6-digit ids are copies for other modes (Arena, ARAM variants, Mayhem specials) with changed stats or prices.
    // The standard versions are available on those maps too, so only standard ids are recommended.
    // The exception is League Classic, whose shop is its own set of 77xxxx items.
    private const int MaxStandardItemId = 9999;
    private const int ClassicMinId = 770000;
    private const int ClassicMaxId = 779999;

    // Boots of Speed, and League Classic's copy of it.
    public static readonly IReadOnlySet<int> BasicBootsIds = new HashSet<int> { 1001, 771001 };

    // Flat regeneration (Classic's "per 5 seconds") in percent of base regeneration. Base regeneration is about 10 per 5 seconds.
    private const double FlatRegenToPercent = 10;

    public static bool IsClassicId(int id) => id is >= ClassicMinId and <= ClassicMaxId;

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

    // Classic items are also flagged for Howling Abyss (for a Classic ARAM variant), so the id range decides, not just the map.
    private IReadOnlyList<ItemInfo> OfKind(ItemKind kind, int map) =>
        _byId.Values.Where(i => i.Kind == kind && i.Maps.Contains(map) && i.IsClassic == (map == GameModes.LeagueClassicMap)).OrderBy(i => i.Id).ToList();

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
            Passives = ParsePassives(description),
            BuildsFrom = from,
            Maps = ParseMaps(json),
        };
    }

    // Classic items name their unique passives as "<jadeUnique>Name:</jadeUnique>". "Active" and "Passive:" aren't names.
    private static HashSet<string> ParsePassives(string description) =>
        PassiveRegex().Matches(description).Select(m => m.Groups[1].Value.Trim())
            .Concat(ClassicUniqueRegex().Matches(description).Select(m => m.Groups[1].Value.Trim()).Where(name => name != "Passive"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        if (!purchasable || !inStore || restricted || !soldSomewhere || (id > MaxStandardItemId && !IsClassicId(id)))
            return ItemKind.Other;
        if (tags.Contains("Consumable") || tags.Contains("Trinket"))
            return ItemKind.Other;

        // Tier 2 boots build out of basic Boots. Tier 3 upgrades build out of tier 2.
        if (tags.Contains("Boots"))
            return from.Any(BasicBootsIds.Contains) ? ItemKind.Boots : ItemKind.Other;

        if (json.GetStringArray("into").Any())
            return ItemKind.Component;

        var minGold = IsClassicId(id) ? ClassicLegendaryMinGold : LegendaryMinGold;
        return gold.GetProperty("total").GetInt32() >= minGold ? ItemKind.Legendary : ItemKind.Other;
    }

    internal static Dictionary<string, double> ParseStats(string description)
    {
        var stats = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, double value) => stats[name] = stats.GetValueOrDefault(name) + value;

        var block = StatsBlockRegex().Match(description);
        if (block.Success)
        {
            foreach (Match m in StatLineRegex().Matches(block.Groups[1].Value))
            {
                var raw = m.Groups[1].Value;
                var isPercent = raw.EndsWith('%');
                var value = double.Parse(raw.TrimEnd('%'), CultureInfo.InvariantCulture);
                var name = (isPercent ? "%" : "") + m.Groups[2].Value.Trim();
                switch (name)
                {
                    case "Mana Regen per 5 seconds": Add(Stat.ManaRegen, value * FlatRegenToPercent); break;
                    case "Health Regen per 5 seconds": Add(Stat.HealthRegen, value * FlatRegenToPercent); break;
                    default: Add(name, value); break;
                }
            }
        }

        if (description.Contains("<jadeUnique>", StringComparison.Ordinal))
            AddClassicPassiveStats(description, Add);
        return stats;
    }

    /// <summary>
    /// Classic items keep some stats in their unique passives: "Wicked Edge: 10 Lethality", "Light Step: 15% Cooldown Reduction",
    /// boots' "Enhanced Movement: 45 Move Speed", and the "ignore 35% of Armor/Magic Resist" of Last Whisper and Void Staff.
    /// Only passives that are nothing but a stat count, so "At 20 stacks, grants 15% Cooldown Reduction" doesn't.
    /// </summary>
    private static void AddClassicPassiveStats(string description, Action<string, double> add)
    {
        foreach (Match m in ClassicUniqueTextRegex().Matches(description))
        {
            var text = WhitespaceRegex().Replace(TagRegex().Replace(m.Groups[1].Value, " "), " ").Trim();
            var stat = ClassicPassiveStatRegex().Match(text);
            if (stat.Success)
            {
                var value = double.Parse(stat.Groups[1].Value, CultureInfo.InvariantCulture);
                var name = (stat.Groups[2].Value == "%" ? "%" : "") + stat.Groups[3].Value;
                add(name, value);
                continue;
            }

            var ignore = ClassicIgnoreResistRegex().Match(text);
            if (ignore.Success)
                add(ignore.Groups[2].Value == "Armor" ? Stat.ArmorPenPercent : Stat.MagicPenPercent, double.Parse(ignore.Groups[1].Value, CultureInfo.InvariantCulture));
            else if (TenacityPassiveRegex().Match(text) is { Success: true } tenacity)
                add(Stat.Tenacity, double.Parse(tenacity.Groups[1].Value, CultureInfo.InvariantCulture));
        }
    }

    internal static ItemTraits DetectTraits(string description)
    {
        var text = WhitespaceRegex().Replace(TagRegex().Replace(description, " "), " ");
        var traits = ItemTraits.None;

        if (Regex.IsMatch(text, @"\bWounds\b")) traits |= ItemTraits.AntiHeal;
        if (Regex.IsMatch(text, @"Shield Reaver|reduces? (the )?Shields", RegexOptions.IgnoreCase)) traits |= ItemTraits.AntiShield;
        if (Regex.IsMatch(text, @"less damage from Critical Strikes", RegexOptions.IgnoreCase)) traits |= ItemTraits.CritReduction;
        // Classic wording: Randuin's "reduces the attacker's Attack Speed", Zhonya's "Invulnerable and Untargetable",
        // Quicksilver's "Removes all debuffs", Madred's "4% of the target's maximum Health", Abyssal's "Reduces the Magic Resist of".
        if (Regex.IsMatch(text, @"Reduces? the Attack Speed|reduces the attacker.s Attack Speed", RegexOptions.IgnoreCase)) traits |= ItemTraits.AttackSpeedSlow;
        if (Regex.IsMatch(text, @"\bStasis\b|Invulnerable and Untargetable", RegexOptions.IgnoreCase)) traits |= ItemTraits.Stasis;
        if (Regex.IsMatch(text, @"Spell Shield", RegexOptions.IgnoreCase)) traits |= ItemTraits.SpellShield;
        if (Regex.IsMatch(text, @"removes? all (crowd control|debuffs|Stuns)", RegexOptions.IgnoreCase)) traits |= ItemTraits.Cleanse;
        if (Regex.IsMatch(text, @"max Health (magic |physical |true )?damage|based on their (bonus|max(imum)?) Health|% of (the )?target(.s| champion.s) (current |max(imum)? )Health", RegexOptions.IgnoreCase)) traits |= ItemTraits.MaxHealthDamage;
        if (Regex.IsMatch(text, @"reduces (the target.s |their )?(Armor|Magic Resist) by|reduces the (Armor|Magic Resist) of|removes? \d+ (Armor|Magic Resist) from", RegexOptions.IgnoreCase)) traits |= ItemTraits.ResistShred;
        if (Regex.IsMatch(text, @"Omnivamp|Life Steal|Heal and Shield Power|Spell Vamp", RegexOptions.IgnoreCase)) traits |= ItemTraits.Sustain;

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

    [GeneratedRegex(@"<jadeUnique>([^<]+?):\s*</jadeUnique>")]
    private static partial Regex ClassicUniqueRegex();

    // The text of one classic unique passive, up to the next line break.
    [GeneratedRegex(@"</jadeUnique>(.*?)(?=<br>|</mainText>|$)", RegexOptions.Singleline)]
    private static partial Regex ClassicUniqueTextRegex();

    [GeneratedRegex(@"^(\d+)(%?) (Lethality|Magic Penetration|Cooldown Reduction|Spell Vamp|Move Speed)\b(?! for)")]
    private static partial Regex ClassicPassiveStatRegex();

    [GeneratedRegex(@"ignores? (\d+)% of (?:your opponent's|the target's) (Armor|Magic Resist)")]
    private static partial Regex ClassicIgnoreResistRegex();

    [GeneratedRegex(@"^Reduces the duration of Stuns.* by (\d+)%")]
    private static partial Regex TenacityPassiveRegex();

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
