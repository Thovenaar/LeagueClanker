using System.Text.Json;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Runes;

/// <param name="StyleId">The tree it belongs to (8000 Precision, 8100 Domination, ...). 0 for stat shards.</param>
/// <param name="Slot">Row in its tree: 0 is the keystone row. For stat shards, the shard row (0-2).</param>
/// <param name="Icon">Path under Data Dragon's img/ folder.</param>
public sealed record RuneInfo(int Id, string Name, string Icon, int StyleId, int Slot);

public sealed record RuneStyle(int Id, string Name, string Icon, IReadOnlyList<IReadOnlyList<RuneInfo>> Slots)
{
    public IReadOnlyList<RuneInfo> Keystones => Slots[0];
}

/// <summary>A rune page as the League client stores it.</summary>
/// <param name="PerkIds">Nine ids: keystone, three primary runes, two secondary runes, three stat shards.</param>
public sealed record RunePage(int PrimaryStyleId, int SubStyleId, IReadOnlyList<int> PerkIds)
{
    public int Keystone => PerkIds[0];
    public IEnumerable<int> PrimaryRunes => PerkIds.Take(4);
    public IEnumerable<int> SecondaryRunes => PerkIds.Skip(4).Take(2);
    public IEnumerable<int> Shards => PerkIds.Skip(6);

    public bool SameAs(RunePage other) =>
        PrimaryStyleId == other.PrimaryStyleId && SubStyleId == other.SubStyleId && PerkIds.SequenceEqual(other.PerkIds);
}

/// <summary>Stat shards aren't in Data Dragon's rune data, so they're listed here. Each row offers three.</summary>
public static class StatShards
{
    public const int AdaptiveForce = 5008;
    public const int AttackSpeed = 5005;
    public const int AbilityHaste = 5007;
    public const int MoveSpeed = 5010;
    public const int HealthScaling = 5001;
    public const int Health = 5011;
    public const int Tenacity = 5013;

    public static readonly IReadOnlyList<IReadOnlyList<int>> Rows =
    [
        [AdaptiveForce, AttackSpeed, AbilityHaste],
        [AdaptiveForce, MoveSpeed, HealthScaling],
        [Health, Tenacity, HealthScaling],
    ];

    internal static readonly IReadOnlyList<RuneInfo> All =
    [
        new(AdaptiveForce, "Adaptive Force", "perk-images/StatMods/StatModsAdaptiveForceIcon.png", 0, 0),
        new(AttackSpeed, "Attack Speed", "perk-images/StatMods/StatModsAttackSpeedIcon.png", 0, 0),
        new(AbilityHaste, "Ability Haste", "perk-images/StatMods/StatModsCDRScalingIcon.png", 0, 0),
        new(MoveSpeed, "Move Speed", "perk-images/StatMods/StatModsMovementSpeedIcon.png", 0, 1),
        new(HealthScaling, "Health Scaling", "perk-images/StatMods/StatModsHealthPlusIcon.png", 0, 1),
        new(Health, "Health", "perk-images/StatMods/StatModsHealthScalingIcon.png", 0, 2),
        new(Tenacity, "Tenacity and Slow Resist", "perk-images/StatMods/StatModsTenacityIcon.png", 0, 2),
    ];
}

/// <summary>The rune trees of one patch, from Data Dragon's runesReforged.json.</summary>
public sealed class RuneCatalog
{
    public static readonly RuneCatalog Empty = new([]);

    private readonly Dictionary<int, RuneInfo> _byId;
    private readonly Dictionary<int, RuneStyle> _styles;

    public RuneCatalog(IReadOnlyList<RuneStyle> styles)
    {
        Styles = styles;
        _styles = styles.ToDictionary(s => s.Id);
        _byId = styles.SelectMany(s => s.Slots.SelectMany(slot => slot))
            .Concat(StatShards.All)
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public IReadOnlyList<RuneStyle> Styles { get; }

    public bool IsEmpty => Styles.Count == 0;

    public RuneStyle? Style(int id) => _styles.GetValueOrDefault(id);

    public RuneStyle? Style(string name) => Styles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public RuneInfo? Get(int id) => _byId.GetValueOrDefault(id);

    public static string IconUrl(string icon) => $"https://ddragon.leagueoflegends.com/cdn/img/{icon}";

    /// <summary>Null when the client would accept the page, otherwise what's wrong with it.</summary>
    public string? Validate(RunePage page)
    {
        if (page.PerkIds.Count != 9)
            return $"A rune page has 9 runes, this one has {page.PerkIds.Count}.";
        if (Style(page.PrimaryStyleId) is not { } primary)
            return $"Unknown rune tree {page.PrimaryStyleId}.";
        if (Style(page.SubStyleId) is not { } secondary || secondary.Id == primary.Id)
            return $"The secondary tree {page.SubStyleId} must be a different, known tree.";

        var ids = page.PerkIds;
        for (var slot = 0; slot < 4; slot++)
        {
            if (!primary.Slots[slot].Any(r => r.Id == ids[slot]))
                return $"Rune {ids[slot]} isn't in row {slot + 1} of {primary.Name}.";
        }

        var secondarySlots = ids.Skip(4).Take(2)
            .Select(id => Enumerable.Range(1, secondary.Slots.Count - 1).FirstOrDefault(slot => secondary.Slots[slot].Any(r => r.Id == id), -1))
            .ToList();
        if (secondarySlots.Contains(-1))
            return $"The secondary runes must come from the lower rows of {secondary.Name}.";
        if (secondarySlots[0] == secondarySlots[1])
            return "The two secondary runes must come from different rows.";

        for (var row = 0; row < 3; row++)
        {
            if (!StatShards.Rows[row].Contains(ids[6 + row]))
                return $"Stat shard {ids[6 + row]} isn't in shard row {row + 1}.";
        }
        return null;
    }

    public static RuneCatalog Parse(string runesReforgedJson)
    {
        using var doc = JsonDocument.Parse(runesReforgedJson);
        var styles = doc.RootElement.EnumerateArray().Select(style =>
        {
            var styleId = style.GetProperty("id").GetInt32();
            var slots = style.GetProperty("slots").EnumerateArray()
                .Select((slot, index) => (IReadOnlyList<RuneInfo>)slot.GetProperty("runes").EnumerateArray()
                    .Select(r => new RuneInfo(r.GetProperty("id").GetInt32(), r.GetStringOrEmpty("name"), r.GetStringOrEmpty("icon"), styleId, index))
                    .ToList())
                .ToList();
            return new RuneStyle(styleId, style.GetStringOrEmpty("name"), style.GetStringOrEmpty("icon"), slots);
        });
        return new RuneCatalog(styles.ToList());
    }
}
