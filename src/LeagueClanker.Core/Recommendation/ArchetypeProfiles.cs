using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

/// <summary>What an archetype values in an item, independent of the current game.</summary>
/// <param name="StatWeights">Weight per stat, applied to the stat's normalized value (see <see cref="StatScale"/>).</param>
/// <param name="CoreItems">Staple items for the archetype. They get a small bonus so the baseline looks like a sensible build.</param>
/// <param name="ClassicCoreItems">The same for League Classic's item shop.</param>
/// <param name="MinFit">Items below this base score are not considered at all (e.g. tank items for a mage).</param>
public sealed record ArchetypeProfile(
    Archetype Archetype,
    IReadOnlyDictionary<string, double> StatWeights,
    IReadOnlySet<string> CoreItems,
    IReadOnlySet<string> ClassicCoreItems,
    double MinFit = 1.3)
{
    public const double CoreItemBonus = 0.8;

    private const double AttackSpeedCap = 2.5;
    private const double TypicalBaseAttackSpeed = 0.65;

    /// <param name="mine">Your current stats. Stats past their cap are worth nothing to you.</param>
    public double BaseScore(ItemInfo item, StatBlock? mine = null) =>
        item.Stats.Sum(s => StatWeights.GetValueOrDefault(s.Key) * StatScale.Normalize(s.Key, Useful(s.Key, s.Value, mine)))
        + ((item.IsClassic ? ClassicCoreItems : CoreItems).Contains(item.Name) ? CoreItemBonus : 0);

    // Crit stops at 100%. Attack speed stops at 2.5 per second; bonus attack speed scales base attack speed.
    // Classic cooldown reduction stops at 40%.
    private static double Useful(string stat, double value, StatBlock? mine) => (stat, mine) switch
    {
        (Stat.CritChance, { } m) => Math.Clamp(100 - m.CritChance, 0, value),
        (Stat.AttackSpeed, { } m) => Math.Clamp((AttackSpeedCap - m.AttackSpeed) / TypicalBaseAttackSpeed * 100, 0, value),
        (Stat.CooldownReduction, { } m) => Math.Clamp(StatEstimator.CooldownReductionCap - m.CooldownReduction, 0, value),
        _ => value,
    };
}

/// <summary>Typical amount of each stat on a full item, used to put stats on a comparable 0-1 scale.</summary>
public static class StatScale
{
    private static readonly Dictionary<string, double> TypicalAmount = new(StringComparer.OrdinalIgnoreCase)
    {
        [Stat.AttackDamage] = 55,
        [Stat.AbilityPower] = 90,
        [Stat.Armor] = 50,
        [Stat.MagicResist] = 45,
        [Stat.Health] = 400,
        [Stat.AttackSpeed] = 30,
        [Stat.CritChance] = 25,
        [Stat.CritDamage] = 30,
        [Stat.Lethality] = 15,
        [Stat.ArmorPenPercent] = 30,
        [Stat.MagicPen] = 12,
        [Stat.MagicPenPercent] = 35,
        [Stat.AbilityHaste] = 20,
        [Stat.LifeSteal] = 10,
        [Stat.Omnivamp] = 8,
        [Stat.MoveSpeed] = 45,
        [Stat.MoveSpeedPercent] = 5,
        [Stat.Mana] = 400,
        [Stat.ManaRegen] = 100,
        [Stat.HealthRegen] = 100,
        [Stat.HealShieldPower] = 10,
        [Stat.Tenacity] = 25,
        [Stat.CooldownReduction] = 15,
        [Stat.SpellVamp] = 12,
    };

    public static double Normalize(string stat, double value) =>
        TypicalAmount.TryGetValue(stat, out var typical) ? value / typical : 0;

    public static double Normalized(this ItemInfo item, string stat) => Normalize(stat, item.Stat(stat));
}

public static class ArchetypeProfiles
{
    public static ArchetypeProfile For(Archetype archetype) => All[archetype];

    public static readonly IReadOnlyDictionary<Archetype, ArchetypeProfile> All = new Dictionary<Archetype, ArchetypeProfile>
    {
        [Archetype.Marksman] = new(Archetype.Marksman,
            Weights((Stat.AttackDamage, 1.0), (Stat.AttackSpeed, 1.0), (Stat.CritChance, 1.1), (Stat.CritDamage, 0.8), (Stat.ArmorPenPercent, 0.6),
                (Stat.Lethality, 0.2), (Stat.LifeSteal, 0.5), (Stat.MoveSpeed, 0.3), (Stat.MoveSpeedPercent, 0.3),
                (Stat.AbilityHaste, 0.2), (Stat.Armor, 0.15), (Stat.MagicResist, 0.15), (Stat.Health, 0.1), (Stat.Tenacity, 0.2)),
            Names("Infinity Edge", "Kraken Slayer", "Yun Tal Wildarrows", "Lord Dominik's Regards", "Phantom Dancer",
                "Bloodthirster", "Navori Flickerblade", "Runaan's Hurricane", "Berserker's Greaves"),
            Names("Infinity Edge", "Phantom Dancer", "The Bloodthirster", "Last Whisper", "Blade of The Ruined King", "Statikk Shiv",
                "Berserker's Greaves")),

        [Archetype.Mage] = new(Archetype.Mage,
            Weights((Stat.AbilityPower, 1.2), (Stat.MagicPen, 0.7), (Stat.MagicPenPercent, 0.9), (Stat.AbilityHaste, 0.6),
                (Stat.Mana, 0.3), (Stat.Health, 0.25), (Stat.Armor, 0.2), (Stat.MagicResist, 0.2), (Stat.MoveSpeed, 0.3),
                (Stat.MoveSpeedPercent, 0.2), (Stat.Tenacity, 0.2), (Stat.ManaRegen, 0.1), (Stat.SpellVamp, 0.2)),
            Names("Luden's Echo", "Rabadon's Deathcap", "Shadowflame", "Void Staff", "Zhonya's Hourglass", "Malignance",
                "Blackfire Torch", "Stormsurge", "Sorcerer's Shoes"),
            Names("Rabadon's Deathcap", "Zhonya's Hourglass", "Void Staff", "Deathfire Grasp", "Morellonomicon", "Rod of Ages",
                "Sorcerer's Shoes")),

        [Archetype.AdAssassin] = new(Archetype.AdAssassin,
            Weights((Stat.AttackDamage, 1.1), (Stat.Lethality, 1.1), (Stat.AbilityHaste, 0.6), (Stat.ArmorPenPercent, 0.4),
                (Stat.MoveSpeed, 0.3), (Stat.MoveSpeedPercent, 0.3), (Stat.Health, 0.15), (Stat.Armor, 0.15),
                (Stat.MagicResist, 0.15), (Stat.Omnivamp, 0.2), (Stat.Tenacity, 0.2)),
            Names("Youmuu's Ghostblade", "Hubris", "Profane Hydra", "Serylda's Grudge", "Edge of Night", "Voltaic Cyclosword",
                "Axiom Arc", "Bastionbreaker", "Umbral Glaive", "Ionian Boots of Lucidity"),
            Names("Youmuu's Ghostblade", "The Black Cleaver", "Last Whisper", "The Bloodthirster", "Ravenous Hydra", "Guardian Angel",
                "Ionian Boots of Lucidity")),

        [Archetype.ApAssassin] = new(Archetype.ApAssassin,
            Weights((Stat.AbilityPower, 1.2), (Stat.MagicPen, 0.9), (Stat.MagicPenPercent, 0.8), (Stat.AbilityHaste, 0.5),
                (Stat.Health, 0.3), (Stat.MoveSpeed, 0.3), (Stat.MoveSpeedPercent, 0.3), (Stat.Armor, 0.2),
                (Stat.MagicResist, 0.2), (Stat.Omnivamp, 0.3), (Stat.Tenacity, 0.2), (Stat.SpellVamp, 0.3)),
            Names("Stormsurge", "Shadowflame", "Rabadon's Deathcap", "Zhonya's Hourglass", "Lich Bane", "Hextech Rocketbelt",
                "Void Staff", "Cryptbloom", "Sorcerer's Shoes"),
            Names("Deathfire Grasp", "Lich Bane", "Rabadon's Deathcap", "Zhonya's Hourglass", "Void Staff", "Hextech Gunblade",
                "Sorcerer's Shoes")),

        [Archetype.Bruiser] = new(Archetype.Bruiser,
            Weights((Stat.AttackDamage, 0.9), (Stat.Health, 0.8), (Stat.AbilityHaste, 0.7), (Stat.Armor, 0.5),
                (Stat.MagicResist, 0.5), (Stat.Omnivamp, 0.4), (Stat.LifeSteal, 0.3), (Stat.Tenacity, 0.5),
                (Stat.AttackSpeed, 0.3), (Stat.ArmorPenPercent, 0.4), (Stat.Lethality, 0.2), (Stat.MoveSpeed, 0.3),
                (Stat.MoveSpeedPercent, 0.3)),
            Names("Black Cleaver", "Sterak's Gage", "Sundered Sky", "Death's Dance", "Spear of Shojin", "Trinity Force",
                "Eclipse", "Overlord's Bloodmail", "Experimental Hexplate"),
            Names("Trinity Force", "The Black Cleaver", "Frozen Mallet", "Randuin's Omen", "Spirit Visage", "Ravenous Hydra",
                "Mercury's Treads")),

        [Archetype.ApBruiser] = new(Archetype.ApBruiser,
            Weights((Stat.AbilityPower, 0.9), (Stat.Health, 0.8), (Stat.AbilityHaste, 0.6), (Stat.Armor, 0.5),
                (Stat.MagicResist, 0.5), (Stat.Omnivamp, 0.5), (Stat.MagicPen, 0.5), (Stat.MagicPenPercent, 0.5),
                (Stat.Tenacity, 0.5), (Stat.AttackSpeed, 0.2), (Stat.MoveSpeed, 0.3), (Stat.SpellVamp, 0.5)),
            Names("Riftmaker", "Liandry's Torment", "Rylai's Crystal Scepter", "Zhonya's Hourglass", "Cosmic Drive",
                "Bloodletter's Curse", "Rod of Ages"),
            Names("Rylai's Crystal Scepter", "Liandry's Torment", "Rod of Ages", "Spirit Visage", "Abyssal Scepter",
                "Zhonya's Hourglass", "Hextech Gunblade")),

        [Archetype.Tank] = new(Archetype.Tank,
            Weights((Stat.Health, 1.0), (Stat.Armor, 0.9), (Stat.MagicResist, 0.9), (Stat.AbilityHaste, 0.6),
                (Stat.Tenacity, 0.6), (Stat.MoveSpeedPercent, 0.4), (Stat.MoveSpeed, 0.3), (Stat.Mana, 0.1),
                (Stat.HealthRegen, 0.2)),
            Names("Sunfire Aegis", "Heartsteel", "Unending Despair", "Jak'Sho, The Protean", "Kaenic Rookern",
                "Hollow Radiance"),
            Names("Sunfire Cape", "Randuin's Omen", "Frozen Heart", "Warmog's Armor", "Banshee's Veil", "Spirit Visage",
                "Locket of the Iron Solari")),

        [Archetype.Enchanter] = new(Archetype.Enchanter,
            Weights((Stat.HealShieldPower, 1.2), (Stat.AbilityHaste, 0.8), (Stat.AbilityPower, 0.5), (Stat.ManaRegen, 0.8),
                (Stat.Health, 0.3), (Stat.Mana, 0.3), (Stat.MoveSpeedPercent, 0.4), (Stat.MoveSpeed, 0.3),
                (Stat.Armor, 0.2), (Stat.MagicResist, 0.2), (Stat.Tenacity, 0.2)),
            Names("Moonstone Renewer", "Echoes of Helia", "Ardent Censer", "Dawncore", "Shurelya's Battlesong", "Redemption",
                "Mikael's Blessing", "Imperial Mandate", "Staff of Flowing Water"),
            Names("Shurelya's Reverie", "Locket of the Iron Solari", "Mikael's Crucible", "Ardent Censer", "Shard of True Ice",
                "Eleisa's Miracle", "Soul Shroud"),
            MinFit: 1.0),
    };

    // League Classic's cooldown reduction is worth what ability haste is worth to the archetype.
    private static Dictionary<string, double> Weights(params (string Stat, double Weight)[] weights)
    {
        var result = weights.ToDictionary(w => w.Stat, w => w.Weight, StringComparer.OrdinalIgnoreCase);
        if (result.TryGetValue(Stat.AbilityHaste, out var haste))
            result.TryAdd(Stat.CooldownReduction, haste);
        return result;
    }

    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}
