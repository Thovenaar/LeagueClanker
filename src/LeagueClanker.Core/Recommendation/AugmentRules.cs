using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

/// <summary>
/// Turns picked augments into situations for the item advice, so they rank items, label them, explain
/// themselves ("Your Critical Rhythm augment pays off on crits, so I suggest Infinity Edge") and can
/// trigger pivots, exactly like enemy-based rules do.
/// </summary>
public static class AugmentRules
{
    private const double PayoffImpact = 1.2;
    private const double ScalingImpact = 0.8;
    private const double UpgradeImpact = 2.0;
    private const double AnswerThreshold = 0.8;

    private const AugmentTrigger ScalingTriggers =
        AugmentTrigger.AttackDamage | AugmentTrigger.AbilityPower | AugmentTrigger.MaxHealth |
        AugmentTrigger.BonusResists | AugmentTrigger.MoveSpeed | AugmentTrigger.AbilityHaste;

    /// <summary>What an item needs to have to feed each augment trigger, on the usual 0-1 stat scale.</summary>
    private static readonly (AugmentTrigger Trigger, string Describe, Func<ItemInfo, double> Feeds)[] ItemFeeds =
    [
        (AugmentTrigger.Attacks, "attacks", i => i.Normalized(Stat.AttackSpeed) + (i.Tags.Contains("OnHit") ? 0.6 : 0)),
        (AugmentTrigger.Crits, "crits", i => i.Normalized(Stat.CritChance) + 0.5 * i.Normalized(Stat.CritDamage)),
        (AugmentTrigger.AbilityHits, "abilities", i => i.Normalized(Stat.AbilityHaste)),
        (AugmentTrigger.Ultimate, "your ultimate", i => 0.6 * i.Normalized(Stat.AbilityHaste)),
        (AugmentTrigger.AbilityHaste, "ability haste", i => i.Normalized(Stat.AbilityHaste)),
        (AugmentTrigger.MaxHealth, "health", i => i.Normalized(Stat.Health)),
        (AugmentTrigger.BonusResists, "armor and magic resist", i => 0.7 * (i.Normalized(Stat.Armor) + i.Normalized(Stat.MagicResist))),
        (AugmentTrigger.Healing, "healing", i => (i.Has(ItemTraits.Sustain) ? 0.6 : 0) + i.Normalized(Stat.Omnivamp) + i.Normalized(Stat.LifeSteal)),
        (AugmentTrigger.Shields, "shields", i => (i.Has(ItemTraits.GrantsShield) ? 0.8 : 0) + i.Normalized(Stat.HealShieldPower)),
        (AugmentTrigger.AllySupport, "healing and shielding allies", i => i.Normalized(Stat.HealShieldPower)),
        (AugmentTrigger.LowHealth, "staying alive at low health", i => (i.Has(ItemTraits.Sustain) ? 0.4 : 0) + (i.Has(ItemTraits.GrantsShield) ? 0.3 : 0)),
        (AugmentTrigger.AttackDamage, "attack damage", i => i.Normalized(Stat.AttackDamage)),
        (AugmentTrigger.AbilityPower, "ability power", i => i.Normalized(Stat.AbilityPower)),
        (AugmentTrigger.MoveSpeed, "move speed", i => i.Normalized(Stat.MoveSpeedPercent) + i.Normalized(Stat.MoveSpeed)),
        (AugmentTrigger.Mana, "mana", i => i.Normalized(Stat.Mana)),
    ];

    public static IEnumerable<Situation> Evaluate(GameAnalysis game)
    {
        foreach (var augment in game.Augments)
        {
            // "Upgrade Infinity Edge", "Quest: obtain Rabadon's Deathcap and Zhonya's Hourglass": build those items.
            if (augment.MentionedItems.Count > 0)
            {
                var names = augment.MentionedItems.Select(i => i.Name).ToHashSet();
                yield return new Situation(augment.Name,
                    $"Your {augment.Name} augment {(augment.IsQuest ? "asks for" : "upgrades")} {string.Join(" and ", augment.MentionedItems.Take(2).Select(i => i.Name))}",
                    UpgradeImpact,
                    item => names.Contains(item.Name) ? 1 : 0);
                continue;
            }

            var feeds = ItemFeeds.Where(f => augment.Needs(f.Trigger)).ToList();
            // Only stats the card grants outright count here. Critical Rhythm's attack speed comes from crits,
            // so it shouldn't make on-hit items better for a champion who doesn't crit.
            var boostsOnHit = augment.AttackSpeedBonus > 0 || augment.Gives(AugmentEffect.OnHit);
            var boostsCritDamage = augment.CritChanceBonus > 0;
            if (feeds.Count == 0 && !boostsOnHit && !boostsCritDamage)
                continue;

            var needs = feeds.Select(f => f.Describe).Take(2).ToList();
            var description = needs.Count > 0
                ? $"Your {augment.Name} augment pays off on {string.Join(" and ", needs)}"
                : boostsCritDamage
                    ? $"Your {augment.Name} augment gives crit chance, which crit damage items multiply"
                    : $"Your {augment.Name} augment gives attack speed, which on-hit items multiply";
            var impact = feeds.Any(f => (f.Trigger & ScalingTriggers) == 0) ? PayoffImpact : ScalingImpact;

            yield return new Situation(augment.Name, description, impact, item =>
                feeds.Sum(f => f.Feeds(item))
                + (boostsOnHit && item.Tags.Contains("OnHit") ? 0.5 : 0)
                + (boostsCritDamage ? 0.8 * item.Normalized(Stat.CritDamage) : 0));
        }
    }

    /// <summary>Your stats with the crit and attack speed your augments grant, so item stats past the caps count for less.</summary>
    public static StatBlock WithAugmentStats(StatBlock stats, IReadOnlyList<AugmentInfo> augments)
    {
        if (augments.Count == 0)
            return stats;
        var crit = augments.Sum(a => a.CritChanceBonus);
        var attackSpeed = augments.Sum(a => a.AttackSpeedBonus);
        return stats with
        {
            CritChance = Math.Min(100, stats.CritChance + crit),
            AttackSpeed = stats.AttackSpeed * (1 + attackSpeed / 100),
        };
    }

    /// <summary>The augment already covers this threat, e.g. a magic resist card against an AP team.</summary>
    public static bool Answers(AugmentInfo augment, Situation situation) =>
        AugmentScorer.Answers(augment, situation) >= AnswerThreshold;
}
