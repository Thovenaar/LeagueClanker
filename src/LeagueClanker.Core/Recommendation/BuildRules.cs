using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

/// <summary>Something about the current game that should change the build, e.g. "Enemy team is 80% AP".</summary>
/// <param name="Label">Short tag shown next to items, e.g. "vs AP".</param>
/// <param name="Description">Full sentence start, completed by the advice ("..., so I suggest X").</param>
/// <param name="Impact">How strongly this situation moves the ranking. Roughly: 1 is "worth one good stat line".</param>
/// <param name="Match">How well an item answers the situation, typically 0-2.</param>
public sealed record Situation(string Label, string Description, double Impact, Func<ItemInfo, double> Match)
{
    public double Score(ItemInfo item) => Impact * Match(item);
}

public interface IBuildRule
{
    /// <summary>Returns the situation this rule reacts to, or null when it doesn't apply to this game.</summary>
    Situation? Evaluate(GameAnalysis game);
}

public static class BuildRules
{
    public static IReadOnlyList<IBuildRule> Default { get; } =
    [
        new DamageTypeRule(),
        new MixedDamageRule(),
        new TankShredRule(),
        new SquishyTeamRule(),
        new AntiHealRule(),
        new AntiShieldRule(),
        new CritDefenseRule(),
        new AttackSpeedDefenseRule(),
        new EnemyPenetrationRule(),
        new CrowdControlRule(),
        new BurstDefenseRule(),
        new TrueDamageRule(),
        new NoFrontlineRule(),
        new TeamDamageSkewRule(),
        new PopularItemsRule(),
    ];

    public static string Percent(double share) => $"{Math.Round(share * 100):0}%";

    /// <summary>0.5 for a champion who is behind, 1 for an average one, capped so one fed carry can't dominate.</summary>
    internal static double ThreatFactor(PlayerProfile p) => Math.Min(p.Threat, 2) / 2;

    internal static string NameList(IEnumerable<PlayerProfile> players) =>
        string.Join(", ", players.OrderByDescending(p => p.Threat).Take(3).Select(p => p.Name));

    /// <summary>Frontliners get full value from resistances; squishies mostly want them as a side stat.</summary>
    internal static double DefenseFactor(Archetype me) => me switch
    {
        Archetype.Tank => 1.3,
        Archetype.Bruiser or Archetype.ApBruiser => 1.1,
        _ => 0.9,
    };
}

/// <summary>Enemy damage leans AP or AD: buy the matching resistance.</summary>
public sealed class DamageTypeRule : IBuildRule
{
    private const double Threshold = 0.6;
    private const double Weight = 2.5;

    public Situation? Evaluate(GameAnalysis game)
    {
        var magic = game.Enemies.MagicShare;
        var defense = BuildRules.DefenseFactor(game.Me.Archetype);

        if (magic >= Threshold)
            return new Situation("vs AP", $"Enemy team is {BuildRules.Percent(magic)} AP", (magic - 0.5) * 2 * Weight,
                item => item.Normalized(Stat.MagicResist) * defense + (item.Has(ItemTraits.SpellShield) ? 0.3 : 0));

        var physical = 1 - magic;
        if (physical >= Threshold)
            return new Situation("vs AD", $"Enemy team is {BuildRules.Percent(physical)} AD", (physical - 0.5) * 2 * Weight,
                item => item.Normalized(Stat.Armor) * defense);

        return null;
    }
}

/// <summary>Balanced enemy damage: stacking one resistance is weak, health and dual-resist items gain value.</summary>
public sealed class MixedDamageRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        var magic = game.Enemies.MagicShare;
        if (magic is < 0.4 or > 0.6 || game.Me.Archetype.IsSquishy())
            return null;

        var intensity = 1 - Math.Abs(magic - 0.5) * 10;
        return new Situation("mixed dmg",
            $"Enemy damage is mixed ({BuildRules.Percent(1 - magic)} AD / {BuildRules.Percent(magic)} AP)",
            0.3 + 0.5 * intensity,
            item => Math.Min(item.Normalized(Stat.Armor), item.Normalized(Stat.MagicResist)) + 0.3 * item.Normalized(Stat.Health));
    }
}

/// <summary>
/// Enemy frontline is actually tanky. Answers what they stacked, judged from estimated real stats:
/// high armor/MR favors % penetration and shred, high health favors %HP damage.
/// </summary>
public sealed class TankShredRule : IBuildRule
{
    private const double Weight = 1.5;
    private const double HighAverageResist = 110;

    public Situation? Evaluate(GameAnalysis game)
    {
        // No enemies yet: blind pick champ select builds a pre-game item set before anyone is visible.
        var damage = game.Me.DamageType;
        if (damage == DamageType.None || game.Enemies.Players.Count == 0)
            return null;

        var physical = damage == DamageType.Physical;
        double Resist(PlayerProfile p) => physical ? p.Stats.Armor : p.Stats.MagicResist;

        var tanks = game.Enemies.Tanks;
        var teamAverage = game.Enemies.Players.Average(Resist);
        if (tanks.Count < 2 && teamAverage < HighAverageResist)
            return null;

        var targets = tanks.Count >= 2 ? tanks : game.Enemies.Players;
        var resist = targets.Average(Resist);
        var health = targets.Average(p => p.Stats.Health);
        var penValue = Math.Clamp((resist - 50) / 100, 0.4, 1.2);
        var healthValue = Math.Clamp((health - 1500) / 1500, 0.4, 1.2);

        var resistName = physical ? "armor" : "MR";
        var description = tanks.Count >= 2
            ? $"Enemy has {tanks.Count} tanks ({BuildRules.NameList(tanks)}) at ~{resist:0} {resistName} / {health:0} HP"
            : $"Enemy averages ~{resist:0} {resistName}";

        var percentPen = physical ? Stat.ArmorPenPercent : Stat.MagicPenPercent;
        var flatPen = physical ? Stat.Lethality : Stat.MagicPen;
        var flatPenPenalty = physical ? 0.4 : 0.2;
        var intensity = tanks.Count >= 2 ? Math.Min(1.0, 0.3 * tanks.Count) : 0.5;

        return new Situation("vs tanks", description, intensity * Weight, item =>
            penValue * (item.Normalized(percentPen) + (item.Has(ItemTraits.ResistShred) ? 0.6 : 0))
            + healthValue * (item.Has(ItemTraits.MaxHealthDamage) ? 0.8 : 0)
            - flatPenPenalty * item.Normalized(flatPen));
    }
}

/// <summary>Mostly squishy enemies with little defense: flat penetration (lethality, flat magic pen) is most efficient.</summary>
public sealed class SquishyTeamRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        var damage = game.Me.DamageType;
        var squishies = game.Enemies.Squishies;
        if (damage == DamageType.None || squishies.Count < 3 || game.Enemies.Tanks.Count > 1)
            return null;

        var stat = damage == DamageType.Physical ? Stat.Lethality : Stat.MagicPen;
        if (ArchetypeProfiles.For(game.Me.Archetype).StatWeights.GetValueOrDefault(stat) < 0.5)
            return null; // e.g. bruisers and marksmen don't build lethality

        return new Situation("vs squishies", $"Enemy team is squishy ({BuildRules.NameList(squishies)})",
            (squishies.Count - 2) / 3.0,
            item => item.Normalized(stat));
    }
}

/// <summary>Heavy enemy healing: anti-heal (Wounds). Less urgent when an ally already applies it.</summary>
public sealed class AntiHealRule : IBuildRule
{
    private const double Weight = 2.0;

    public Situation? Evaluate(GameAnalysis game)
    {
        if (game.Me.HasAntiHeal || game.Enemies.HealingScore < 1.2)
            return null;

        var healers = game.Enemies.Players.Where(p => p.HealingScore >= 0.8);
        var intensity = Math.Clamp((game.Enemies.HealingScore - 0.8) / 2.5, 0.2, 1.0);
        var description = $"Enemy has heavy healing ({BuildRules.NameList(healers)})";

        var carriers = game.Allies.AntiHealCarriers;
        if (carriers.Count > 0)
        {
            intensity *= 0.4;
            description += $", but {BuildRules.NameList(carriers)} already applies anti-heal";
        }

        return new Situation("anti-heal", description, intensity * Weight, item => item.Has(ItemTraits.AntiHeal) ? 1 : 0);
    }
}

public sealed class AntiShieldRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        var shielders = game.Enemies.Players.Where(p => p.ShieldScore >= 0.8).ToList();
        if (game.Enemies.ShieldScore < 2)
            return null;

        return new Situation("anti-shield", $"Enemy has lots of shields ({BuildRules.NameList(shielders)})",
            Math.Clamp((game.Enemies.ShieldScore - 1.5) / 2, 0.3, 1.0) * 1.5,
            item => item.Has(ItemTraits.AntiShield) ? 1 : 0);
    }
}

/// <summary>Enemy carries crit a lot, judged from their crit chance: crit damage reduction (Randuin's).</summary>
public sealed class CritDefenseRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        var critters = game.Enemies.Players.Where(p => p.Stats.CritChance >= 40).OrderByDescending(p => p.Stats.CritChance * p.Threat).ToList();
        var weightedCrit = critters.Sum(p => p.Stats.CritChance * BuildRules.ThreatFactor(p));
        if (weightedCrit < 40)
            return null;

        var list = string.Join(", ", critters.Take(3).Select(p => $"{p.Name} {p.Stats.CritChance:0}%"));
        return new Situation("vs crit", $"Enemy crit chance is high ({list})",
            Math.Clamp(weightedCrit / 120, 0.3, 1.0) * 1.5,
            item => item.Has(ItemTraits.CritReduction) ? 1 : 0);
    }
}

/// <summary>Enemy auto-attackers attack fast, judged from their attack speed: attack speed slows (Frozen Heart).</summary>
public sealed class AttackSpeedDefenseRule : IBuildRule
{
    private const double FastAttackSpeed = 1.1;

    public Situation? Evaluate(GameAnalysis game)
    {
        var attackers = game.Enemies.Players.Where(p => p.Stats.AttackSpeed >= FastAttackSpeed).OrderByDescending(p => p.Stats.AttackSpeed).ToList();
        var excess = attackers.Sum(p => (p.Stats.AttackSpeed - 0.8) * BuildRules.ThreatFactor(p));
        if (excess < 0.6)
            return null;

        var list = string.Join(", ", attackers.Take(3).Select(p => $"{p.Name} {p.Stats.AttackSpeed:0.00}/s"));
        return new Situation("vs auto-attacks", $"Enemy attacks fast ({list})",
            Math.Clamp(excess / 1.5, 0.3, 1.0) * 1.5,
            item => item.Has(ItemTraits.AttackSpeedSlow) ? 1 : 0);
    }
}

/// <summary>Enemy damage dealers carry a lot of penetration: resistances do less for you, health does more.</summary>
public sealed class EnemyPenetrationRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        var physical = game.Enemies.Players.Where(p => p.Stats.Lethality >= 20 || p.Stats.ArmorPenPercent >= 30).ToList();
        var magic = game.Enemies.Players.Where(p => p.Stats.MagicPen >= 15 || p.Stats.MagicPenPercent >= 35).ToList();
        var usePhysical = physical.Sum(BuildRules.ThreatFactor) >= magic.Sum(BuildRules.ThreatFactor);
        var dealers = usePhysical ? physical : magic;
        var weight = dealers.Sum(BuildRules.ThreatFactor);
        if (weight < 0.7)
            return null;

        string Pen(PlayerProfile p) => usePhysical
            ? p.Stats.Lethality >= 20 ? $"{p.Name} {p.Stats.Lethality:0} lethality" : $"{p.Name} {p.Stats.ArmorPenPercent:0}% armor pen"
            : p.Stats.MagicPenPercent >= 35 ? $"{p.Name} {p.Stats.MagicPenPercent:0}% magic pen" : $"{p.Name} {p.Stats.MagicPen:0} magic pen";

        var resist = usePhysical ? Stat.Armor : Stat.MagicResist;
        var list = string.Join(", ", dealers.OrderByDescending(p => p.Threat).Take(3).Select(Pen));
        return new Situation("vs pen",
            $"Enemy has a lot of penetration ({list}), so {(usePhysical ? "armor" : "magic resist")} does less for you",
            Math.Clamp(weight / 2, 0.3, 1.0),
            item => 0.6 * item.Normalized(Stat.Health) - 0.2 * item.Normalized(resist));
    }
}

public sealed class CrowdControlRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        var cc = game.Enemies.CrowdControlScore;
        if (cc < 3)
            return null;

        var ccChampions = game.Enemies.Players.Where(p => p.CrowdControlScore >= 0.8);
        return new Situation("vs CC", $"Enemy has heavy crowd control ({BuildRules.NameList(ccChampions)})",
            Math.Clamp((cc - 2.5) / 2, 0.3, 1.0),
            item => item.Normalized(Stat.Tenacity) + (item.Has(ItemTraits.Cleanse) ? 0.8 : 0));
    }
}

/// <summary>Fed or multiple assassins: stasis, spell shields and lifeline shields keep you alive through burst.</summary>
public sealed class BurstDefenseRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        var assassins = game.Enemies.Assassins;
        var threat = assassins.Sum(p => p.Threat);
        if (threat < 1.5 || game.Me.Archetype == Archetype.Tank)
            return null;

        return new Situation("vs burst", $"Enemy has burst assassins ({BuildRules.NameList(assassins)})",
            Math.Clamp((threat - 1) / 2, 0.3, 1.0) * 1.5,
            item => (item.Has(ItemTraits.Stasis) ? 1.0 : 0) + (item.Has(ItemTraits.SpellShield) ? 0.7 : 0) + (item.Has(ItemTraits.GrantsShield) ? 0.3 : 0));
    }
}

public sealed class TrueDamageRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        var dealers = game.Enemies.TrueDamageDealers;
        if (dealers.Count < 2)
            return null;

        return new Situation("vs true dmg", $"Enemy deals true damage ({BuildRules.NameList(dealers)}) that ignores resistances",
            Math.Min(1.0, dealers.Count / 3.0),
            item => 0.6 * item.Normalized(Stat.Health));
    }
}

/// <summary>Ally-based: nobody else on my team can absorb damage.</summary>
public sealed class NoFrontlineRule : IBuildRule
{
    public Situation? Evaluate(GameAnalysis game)
    {
        // Needs teammates to talk about: in Arena, where your partner is unknown, there are none.
        if (!game.Me.Archetype.IsFrontline() || game.Allies.Players.Count == 0 || game.Allies.Frontline.Count > 0)
            return null;

        return new Situation("frontline", "Your team has no other frontline", 0.8,
            item => 0.6 * item.Normalized(Stat.Health) + 0.3 * (item.Normalized(Stat.Armor) + item.Normalized(Stat.MagicResist)));
    }
}

/// <summary>Ally-based: my team deals mostly one damage type, so enemies will stack that resistance.</summary>
public sealed class TeamDamageSkewRule : IBuildRule
{
    private const double Threshold = 0.75;

    public Situation? Evaluate(GameAnalysis game)
    {
        var damage = game.Me.DamageType;
        var team = game.MyTeam;
        if (game.Allies.Players.Count == 0)
            return null; // a "team" of just you says nothing

        if (damage == DamageType.Physical && team.PhysicalShare >= Threshold)
            return new Situation("team AD", $"Your team is {BuildRules.Percent(team.PhysicalShare)} AD (enemies will stack armor)",
                (team.PhysicalShare - 0.7) * 3,
                item => item.Normalized(Stat.ArmorPenPercent) + (item.Has(ItemTraits.ResistShred) ? 0.5 : 0));

        if (damage == DamageType.Magic && team.MagicShare >= Threshold)
            return new Situation("team AP", $"Your team is {BuildRules.Percent(team.MagicShare)} AP (enemies will stack magic resist)",
                (team.MagicShare - 0.7) * 3,
                item => item.Normalized(Stat.MagicPenPercent) + (item.Has(ItemTraits.ResistShred) ? 0.5 : 0));

        return null;
    }
}

/// <summary>
/// op.gg's most played items for your champion get a nudge, so the build starts from what players of the champion buy.
/// The nudge is small: the game's situations still decide, so a popular item that doesn't fit this game stays low.
/// </summary>
public sealed class PopularItemsRule : IBuildRule
{
    private const double Weight = 0.8;

    public Situation? Evaluate(GameAnalysis game) => game.PopularItems.Count == 0
        ? null
        : new Situation("popular", $"op.gg's most played items for {game.Me.Name} include it", Weight, item => game.PopularItems.Contains(item.Id) ? 1 : 0);
}
