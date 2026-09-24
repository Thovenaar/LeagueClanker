using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Analysis;

/// <summary>A champion's combat stats at a moment in the game. Percentages are 0-100.</summary>
public sealed record StatBlock
{
    public double Health { get; init; }
    public double Armor { get; init; }
    public double MagicResist { get; init; }
    public double AttackDamage { get; init; }
    public double BonusAttackDamage { get; init; }
    public double AbilityPower { get; init; }

    /// <summary>Health, armor and magic resist from items (and runes, for the active player), for passives that scale with them.</summary>
    public double BonusHealth { get; init; }
    public double BonusArmor { get; init; }
    public double BonusMagicResist { get; init; }

    /// <summary>Attacks per second.</summary>
    public double AttackSpeed { get; init; }

    public double CritChance { get; init; }
    public double Lethality { get; init; }
    public double ArmorPenPercent { get; init; }
    public double MagicPen { get; init; }
    public double MagicPenPercent { get; init; }
    public double AbilityHaste { get; init; }

    /// <summary>League Classic's cooldown reduction, capped at 40%.</summary>
    public double CooldownReduction { get; init; }

    public double LifeSteal { get; init; }
    public double Omnivamp { get; init; }
    public double SpellVamp { get; init; }
    public double HealShieldPower { get; init; }
    public double Tenacity { get; init; }
    public double MoveSpeed { get; init; }
    public double AttackRange { get; init; }

    /// <summary>True when the core stats come from the game itself (only possible for the active player).</summary>
    public bool IsReal { get; init; }

    /// <summary>Physical damage needed to kill them: every point of armor adds 1% of their health.</summary>
    public double PhysicalEffectiveHealth => Health * (1 + Armor / 100);
    public double MagicEffectiveHealth => Health * (1 + MagicResist / 100);
    public bool IsRanged => AttackRange >= 350;
}

public static class StatEstimator
{
    private const double CritCap = 100;
    public const double CooldownReductionCap = 40;

    // League Classic gives every champion 4 extra armor.
    private const double ClassicBonusArmor = 4;

    /// <summary>
    /// Base stats at their level plus item stats. Runes, stat shards and stacking passives aren't visible
    /// through the API, so real values run a little higher, mostly for tanks and scaling champions.
    /// </summary>
    /// <param name="mode">League Classic grows stats linearly and adds 4 armor.</param>
    public static StatBlock Estimate(ChampionStats champion, int level, IReadOnlyList<ItemInfo> items, GameMode mode = GameMode.SummonersRift)
    {
        double Sum(string stat) => items.Sum(i => i.Stat(stat));

        var classic = mode.UsesClassicItems();
        var bonusAttackSpeed = champion.BonusAttackSpeedAt(level, classic) + Sum(Stat.AttackSpeed);
        var stats = new StatBlock
        {
            Health = champion.HealthAt(level, classic) + Sum(Stat.Health),
            Armor = champion.ArmorAt(level, classic) + (classic ? ClassicBonusArmor : 0) + Sum(Stat.Armor),
            MagicResist = champion.MagicResistAt(level, classic) + Sum(Stat.MagicResist),
            AttackDamage = champion.AttackDamageAt(level, classic) + Sum(Stat.AttackDamage),
            BonusAttackDamage = Sum(Stat.AttackDamage),
            AbilityPower = Sum(Stat.AbilityPower),
            AttackSpeed = champion.AttackSpeed * (1 + bonusAttackSpeed / 100),
            CritChance = Math.Min(CritCap, Sum(Stat.CritChance)),
            Lethality = Sum(Stat.Lethality),
            ArmorPenPercent = Math.Min(100, Sum(Stat.ArmorPenPercent)),
            MagicPen = Sum(Stat.MagicPen),
            MagicPenPercent = Math.Min(100, Sum(Stat.MagicPenPercent)),
            AbilityHaste = Sum(Stat.AbilityHaste),
            CooldownReduction = Math.Min(CooldownReductionCap, Sum(Stat.CooldownReduction)),
            LifeSteal = Sum(Stat.LifeSteal),
            Omnivamp = Sum(Stat.Omnivamp),
            SpellVamp = Sum(Stat.SpellVamp),
            HealShieldPower = Sum(Stat.HealShieldPower),
            Tenacity = Math.Min(100, Sum(Stat.Tenacity)),
            MoveSpeed = (champion.MoveSpeed + Sum(Stat.MoveSpeed)) * (1 + Sum(Stat.MoveSpeedPercent) / 100),
            AttackRange = champion.AttackRange,
            BonusHealth = Sum(Stat.Health),
            BonusArmor = Sum(Stat.Armor),
            BonusMagicResist = Sum(Stat.MagicResist),
        };
        return WithPassives(stats, items);
    }

    /// <summary>
    /// Adds what item passives give on top of the item stats: stacks first, then health and resists, then stats converted
    /// from health, and multipliers of total AP last, so Rabadon's also multiplies Riftmaker's AP.
    /// </summary>
    private static StatBlock WithPassives(StatBlock stats, IReadOnlyList<ItemInfo> items)
    {
        var scaling = items.SelectMany(i => i.Scaling.Select(s => (Item: i, Scaling: s))).ToList();
        if (scaling.Count == 0)
            return stats;

        foreach (var order in new[] { ScalingSource.Stacked, ScalingSource.BonusArmor, ScalingSource.BonusMagicResist, ScalingSource.BonusHealth, ScalingSource.TotalAbilityPower })
            foreach (var (_, s) in scaling.Where(x => x.Scaling.Source == order).OrderBy(x => x.Scaling.Stat == Stat.Health ? 0 : 1))
            {
                // The item's own stats are in the totals already, so value the passive on the totals alone.
                var value = s.Source switch
                {
                    ScalingSource.Stacked => s.Amount,
                    ScalingSource.TotalAbilityPower => s.Amount * stats.AbilityPower,
                    ScalingSource.BonusHealth => s.Amount * stats.BonusHealth,
                    ScalingSource.BonusArmor => s.Amount * stats.BonusArmor,
                    _ => s.Amount * stats.BonusMagicResist,
                };
                stats = stats.Add(s.Stat, value);
            }
        return stats;
    }

    private static StatBlock Add(this StatBlock stats, string stat, double value) => stat switch
    {
        Stat.Health => stats with { Health = stats.Health + value, BonusHealth = stats.BonusHealth + value },
        Stat.Armor => stats with { Armor = stats.Armor + value, BonusArmor = stats.BonusArmor + value },
        Stat.MagicResist => stats with { MagicResist = stats.MagicResist + value, BonusMagicResist = stats.BonusMagicResist + value },
        Stat.AttackDamage => stats with { AttackDamage = stats.AttackDamage + value, BonusAttackDamage = stats.BonusAttackDamage + value },
        Stat.AbilityPower => stats with { AbilityPower = stats.AbilityPower + value },
        Stat.CritChance => stats with { CritChance = Math.Min(CritCap, stats.CritChance + value) },
        _ => stats,
    };

    /// <summary>Overlays the active player's real stats on the estimate. Zeroed stats (loading screen) are ignored.</summary>
    public static StatBlock WithRealStats(this StatBlock estimate, LiveChampionStats? real)
    {
        if (real is null || real.MaxHealth <= 0)
            return estimate;

        return estimate with
        {
            Health = real.MaxHealth,
            Armor = real.Armor,
            MagicResist = real.MagicResist,
            // The real totals include runes and passives; the base part is the same as in the estimate.
            BonusHealth = Math.Max(0, real.MaxHealth - (estimate.Health - estimate.BonusHealth)),
            BonusArmor = Math.Max(0, real.Armor - (estimate.Armor - estimate.BonusArmor)),
            BonusMagicResist = Math.Max(0, real.MagicResist - (estimate.MagicResist - estimate.BonusMagicResist)),
            AttackDamage = real.AttackDamage,
            AbilityPower = real.AbilityPower,
            AttackSpeed = real.AttackSpeed,
            MoveSpeed = real.MoveSpeed,
            AttackRange = real.AttackRange > 0 ? real.AttackRange : estimate.AttackRange,
            Lethality = real.PhysicalLethality,
            MagicPen = real.MagicPenetrationFlat,
            AbilityHaste = real.AbilityHaste ?? estimate.AbilityHaste,
            IsReal = true,
        };
    }
}
