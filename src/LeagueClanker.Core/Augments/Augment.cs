using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Augments;

public enum AugmentTier
{
    Silver,
    Gold,
    Prismatic,
}

/// <summary>What an augment gives you.</summary>
[Flags]
public enum AugmentEffect : long
{
    None = 0,

    // Stats
    AttackDamage = 1L << 0,
    AbilityPower = 1L << 1,
    AttackSpeed = 1L << 2,
    CritChance = 1L << 3,
    CritDamage = 1L << 4,
    AbilityHaste = 1L << 5,
    Health = 1L << 6,
    Armor = 1L << 7,
    MagicResist = 1L << 8,
    MoveSpeed = 1L << 9,
    Penetration = 1L << 10,
    Omnivamp = 1L << 11,
    HealShieldPower = 1L << 12,
    Tenacity = 1L << 13,
    AttackRange = 1L << 14,
    AdaptiveForce = 1L << 15,

    // Mechanics
    OnHit = 1L << 20,
    TrueDamage = 1L << 21,
    MaxHealthDamage = 1L << 22,
    Burn = 1L << 23,
    ResistShred = 1L << 24,
    Shield = 1L << 25,
    Heal = 1L << 26,
    CrowdControl = 1L << 27,
    Execute = 1L << 28,
    SpellShield = 1L << 29,
    Invulnerable = 1L << 30,
    Stealth = 1L << 31,
    Gold = 1L << 32,
    SummonerSpell = 1L << 33,
    Keystones = 1L << 34,
    AntiHeal = 1L << 35,
    Dash = 1L << 36,

    /// <summary>Deals damage of its own (procs, explosions, increased damage).</summary>
    Damage = 1L << 37,
}

/// <summary>What makes an augment stronger: things you do, or stats it scales with.</summary>
[Flags]
public enum AugmentTrigger : long
{
    None = 0,
    Attacks = 1L << 0,
    Crits = 1L << 1,
    AbilityHits = 1L << 2,
    Ultimate = 1L << 3,
    Healing = 1L << 4,
    AllySupport = 1L << 5,
    Shields = 1L << 6,
    LowHealth = 1L << 7,
    Immobilize = 1L << 8,
    Takedowns = 1L << 9,
    Dashes = 1L << 10,
    Pets = 1L << 11,
    Spinning = 1L << 12,
    SummonerSpells = 1L << 13,
    Death = 1L << 14,
    Distance = 1L << 15,
    Stealth = 1L << 16,
    Mana = 1L << 17,

    /// <summary>Champions that stack permanently (Nasus, Veigar, Cho'Gath, ...).</summary>
    Stacking = 1L << 18,

    // Scales with a stat
    AttackDamage = 1L << 24,
    AbilityPower = 1L << 25,
    MaxHealth = 1L << 26,
    BonusResists = 1L << 27,
    MoveSpeed = 1L << 28,
    AbilityHaste = 1L << 29,
}

public sealed record AugmentInfo
{
    public required string Name { get; init; }
    public required AugmentTier Tier { get; init; }

    /// <summary>Plain-text description, wiki markup removed.</summary>
    public required string Description { get; init; }

    public AugmentEffect Effects { get; init; }
    public AugmentTrigger Triggers { get; init; }

    /// <summary>Items the augment upgrades or asks for, e.g. "Upgrade Infinity Edge". Worth little unless you build them.</summary>
    public IReadOnlyList<ItemInfo> MentionedItems { get; init; } = [];

    /// <summary>It comes with a downside ("but ...", "cannot ...").</summary>
    public bool HasDrawback { get; init; }

    /// <summary>Grants random augments instead of a fixed effect (Transmute).</summary>
    public bool IsRandom { get; init; }

    public bool IsQuest { get; init; }
    public bool IsDisabled { get; init; }

    public bool Gives(AugmentEffect effect) => (Effects & effect) != 0;
    public bool Needs(AugmentTrigger trigger) => (Triggers & trigger) != 0;

    public override string ToString() => Name;
}

public sealed class AugmentCatalog
{
    private readonly Dictionary<string, AugmentInfo> _byKey;

    public AugmentCatalog(IEnumerable<AugmentInfo> augments)
    {
        All = augments.OrderBy(a => a.Tier).ThenBy(a => a.Name).ToList();
        _byKey = All.GroupBy(a => Key(a.Name)).ToDictionary(g => g.Key, g => g.First());
    }

    public IReadOnlyList<AugmentInfo> All { get; }

    /// <summary>Augments that can currently show up in a selection.</summary>
    public IEnumerable<AugmentInfo> Offerable => All.Where(a => !a.IsDisabled);

    public IReadOnlyList<AugmentInfo> OfferableOfTier(AugmentTier tier) => Offerable.Where(a => a.Tier == tier).ToList();

    /// <summary>Case, spacing and punctuation don't matter, so OCR'd or typed names still match.</summary>
    public AugmentInfo? Find(string name) => _byKey.GetValueOrDefault(Key(name));

    /// <summary>Parses the wiki's Module:MayhemAugmentData/data Lua source and tags every augment.</summary>
    public static AugmentCatalog ParseWikiModule(string luaSource, ItemCatalog? items = null)
    {
        var data = LuaTable.ParseReturn(luaSource);
        var augments = data
            .Where(kv => kv.Value is Dictionary<string, object?>)
            .Select(kv => AugmentTagger.Tag(kv.Key, (Dictionary<string, object?>)kv.Value!, items));
        return new AugmentCatalog(augments);
    }

    /// <summary>Lowercase letters and digits only: the form names are compared in.</summary>
    public static string Key(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
