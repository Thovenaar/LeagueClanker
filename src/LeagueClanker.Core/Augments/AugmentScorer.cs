using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Augments;

/// <summary>Everything that decides how good an augment is for you right now.</summary>
public sealed record AugmentContext(GameAnalysis Game)
{
    public IReadOnlyList<AugmentInfo> Picked { get; init; } = [];

    /// <summary>Items you're going to build (e.g. the accepted build). Counted at half the weight of owned items.</summary>
    public IReadOnlyList<ItemInfo> PlannedItems { get; init; } = [];

    /// <summary>What the item rules detected about this game (enemy is AP, tanks, healing, ...).</summary>
    public IReadOnlyList<Situation> Situations { get; init; } = [];

    /// <summary>Win rates and your champion's favorite cards from a stats site. Null scores from card effects alone.</summary>
    public CommunityAugments? Community { get; init; }

    public PlayerProfile Me => Game.Me;
}

public sealed record ScoreReason(string Text, double Points);

/// <summary>
/// Scores augments: how well a card fits your champion, how it pairs with the cards and items you have, and what it
/// answers in this game, on top of community win rates when those are loaded. All weights are in one place so they can be tuned.
/// </summary>
public sealed class AugmentScorer
{
    private const double SynergyWeight = 0.7;
    private const double ItemSynergyWeight = 0.6;
    private const double SituationWeight = 0.5;
    private const double RepeatedStatDecay = 0.7;
    private const double UnknownEffectFit = 0.4;
    private const double RandomAugmentFit = 0.8;
    private const double DrawbackPenalty = 0.3;
    private const double QuestFactor = 0.8;

    // Each extra effect on one card counts for less, so long descriptions don't outscore focused cards.
    private const double ExtraEffectDecay = 0.65;

    // Champion-specific cards (pets, spinning, stacking) are niche: worthless for most, strong when they fit.
    private const double NicheMatch = 1.4;

    private static readonly (AugmentEffect Effect, string[] Stats)[] StatEffects =
    [
        (AugmentEffect.AttackDamage, [Stat.AttackDamage]),
        (AugmentEffect.AbilityPower, [Stat.AbilityPower]),
        (AugmentEffect.AttackSpeed, [Stat.AttackSpeed]),
        (AugmentEffect.CritChance, [Stat.CritChance]),
        (AugmentEffect.CritDamage, [Stat.CritDamage]),
        (AugmentEffect.AbilityHaste, [Stat.AbilityHaste]),
        (AugmentEffect.Health, [Stat.Health]),
        (AugmentEffect.Armor, [Stat.Armor]),
        (AugmentEffect.MagicResist, [Stat.MagicResist]),
        (AugmentEffect.MoveSpeed, [Stat.MoveSpeedPercent, Stat.MoveSpeed]),
        (AugmentEffect.Penetration, [Stat.ArmorPenPercent, Stat.MagicPenPercent, Stat.Lethality, Stat.MagicPen]),
        (AugmentEffect.Omnivamp, [Stat.Omnivamp, Stat.LifeSteal]),
        (AugmentEffect.HealShieldPower, [Stat.HealShieldPower]),
        (AugmentEffect.Tenacity, [Stat.Tenacity]),
        (AugmentEffect.AdaptiveForce, [Stat.AttackDamage, Stat.AbilityPower]),
    ];

    /// <summary>Which effects feed which triggers, and how strongly. This is where combinations come from.</summary>
    private static readonly (AugmentTrigger Trigger, AugmentEffect Enabler, double Weight)[] Enablers =
    [
        (AugmentTrigger.Attacks, AugmentEffect.AttackSpeed, 1.0),
        (AugmentTrigger.Attacks, AugmentEffect.OnHit, 0.8),
        (AugmentTrigger.Attacks, AugmentEffect.AttackRange, 0.3),
        (AugmentTrigger.Attacks, AugmentEffect.CritChance, 0.3),
        (AugmentTrigger.Crits, AugmentEffect.CritChance, 1.0),
        (AugmentTrigger.Crits, AugmentEffect.CritDamage, 0.7),
        (AugmentTrigger.AbilityHits, AugmentEffect.AbilityHaste, 0.8),
        (AugmentTrigger.Ultimate, AugmentEffect.AbilityHaste, 0.5),
        (AugmentTrigger.Healing, AugmentEffect.Omnivamp, 0.8),
        (AugmentTrigger.Healing, AugmentEffect.Heal, 0.8),
        (AugmentTrigger.Healing, AugmentEffect.HealShieldPower, 0.8),
        (AugmentTrigger.AllySupport, AugmentEffect.HealShieldPower, 1.0),
        (AugmentTrigger.AllySupport, AugmentEffect.Heal, 0.5),
        (AugmentTrigger.AllySupport, AugmentEffect.Shield, 0.5),
        (AugmentTrigger.Shields, AugmentEffect.Shield, 1.0),
        (AugmentTrigger.Shields, AugmentEffect.HealShieldPower, 0.6),
        (AugmentTrigger.LowHealth, AugmentEffect.Omnivamp, 0.4),
        (AugmentTrigger.LowHealth, AugmentEffect.Shield, 0.4),
        (AugmentTrigger.LowHealth, AugmentEffect.Heal, 0.4),
        (AugmentTrigger.MaxHealth, AugmentEffect.Health, 1.0),
        (AugmentTrigger.BonusResists, AugmentEffect.Armor, 0.8),
        (AugmentTrigger.BonusResists, AugmentEffect.MagicResist, 0.8),
        (AugmentTrigger.MoveSpeed, AugmentEffect.MoveSpeed, 1.0),
        (AugmentTrigger.AbilityHaste, AugmentEffect.AbilityHaste, 1.0),
        (AugmentTrigger.AttackDamage, AugmentEffect.AttackDamage, 1.0),
        (AugmentTrigger.AttackDamage, AugmentEffect.AdaptiveForce, 0.5),
        (AugmentTrigger.AbilityPower, AugmentEffect.AbilityPower, 1.0),
        (AugmentTrigger.AbilityPower, AugmentEffect.AdaptiveForce, 0.5),
        (AugmentTrigger.Immobilize, AugmentEffect.CrowdControl, 0.8),
        (AugmentTrigger.Takedowns, AugmentEffect.Execute, 0.6),
        (AugmentTrigger.Takedowns, AugmentEffect.Damage, 0.2),
        (AugmentTrigger.Dashes, AugmentEffect.Dash, 1.0),
        (AugmentTrigger.Stealth, AugmentEffect.Stealth, 1.0),
    ];

    private static string DescribeGate(AugmentTrigger gate) => gate switch
    {
        AugmentTrigger.Spinning => "spinning abilities",
        AugmentTrigger.Pets => "a pet or summon",
        AugmentTrigger.Stealth => "stealth",
        _ => "stacking abilities",
    };

    /// <summary>Conditions only some champions meet at all. When a card has one, it decides how well the card fits.</summary>
    private const AugmentTrigger GateTriggers = AugmentTrigger.Pets | AugmentTrigger.Spinning | AugmentTrigger.Stealth | AugmentTrigger.Stacking;

    /// <summary>Triggers that are things you do (conditions), as opposed to stats the card scales with.</summary>
    private const AugmentTrigger ScalingTriggers =
        AugmentTrigger.AttackDamage | AugmentTrigger.AbilityPower | AugmentTrigger.MaxHealth |
        AugmentTrigger.BonusResists | AugmentTrigger.MoveSpeed | AugmentTrigger.AbilityHaste;

    /// <summary>What each game situation (from the item rules) wants from an augment.</summary>
    private static readonly Dictionary<string, (AugmentEffect Effect, double Weight)[]> SituationWants = new()
    {
        ["vs tanks"] = [(AugmentEffect.TrueDamage, 1.0), (AugmentEffect.MaxHealthDamage, 1.0), (AugmentEffect.ResistShred, 0.8), (AugmentEffect.Penetration, 0.8)],
        ["vs AP"] = [(AugmentEffect.MagicResist, 1.0), (AugmentEffect.SpellShield, 0.5)],
        ["vs AD"] = [(AugmentEffect.Armor, 1.0)],
        ["mixed dmg"] = [(AugmentEffect.Health, 0.5), (AugmentEffect.Armor, 0.3), (AugmentEffect.MagicResist, 0.3)],
        ["anti-heal"] = [(AugmentEffect.AntiHeal, 1.0)],
        ["vs burst"] = [(AugmentEffect.Shield, 0.6), (AugmentEffect.Invulnerable, 0.8), (AugmentEffect.SpellShield, 0.6)],
        ["vs CC"] = [(AugmentEffect.Tenacity, 1.0)],
        ["vs squishies"] = [(AugmentEffect.Penetration, 0.6), (AugmentEffect.Execute, 0.5), (AugmentEffect.Damage, 0.3)],
        ["vs crit"] = [(AugmentEffect.Armor, 0.4)],
        ["vs auto-attacks"] = [(AugmentEffect.Armor, 0.4)],
        ["vs pen"] = [(AugmentEffect.Health, 0.6)],
        ["vs true dmg"] = [(AugmentEffect.Health, 0.6)],
        ["frontline"] = [(AugmentEffect.Health, 0.6), (AugmentEffect.Armor, 0.4), (AugmentEffect.MagicResist, 0.4), (AugmentEffect.Shield, 0.3)],
        ["team AD"] = [(AugmentEffect.Penetration, 0.6)],
        ["team AP"] = [(AugmentEffect.Penetration, 0.6)],
    };

    /// <summary>How much of what <paramref name="situation"/> asks for the augment already gives.</summary>
    internal static double Answers(AugmentInfo augment, Situation situation) =>
        SituationWants.TryGetValue(situation.Label, out var wants) ? wants.Where(w => augment.Gives(w.Effect)).Sum(w => w.Weight) : 0;

    /// <summary>
    /// Scoring for one game state with caching, for simulations that score the same cards thousands of times.
    /// Gives the same numbers as the scorer's own methods.
    /// </summary>
    internal Session CreateSession(AugmentContext ctx) => new(this, ctx);

    internal sealed class Session(AugmentScorer scorer, AugmentContext ctx)
    {
        private readonly Dictionary<AugmentInfo, (FitParts Fit, double ItemsAndSituations)> _cards = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(AugmentInfo, AugmentInfo), double> _synergy = new(PairComparer.Instance);

        public AugmentContext Context => ctx;

        public double Value(AugmentInfo augment, IReadOnlyList<AugmentInfo> set)
        {
            var statCounts = new Dictionary<AugmentEffect, int>();
            foreach (var other in set)
                CountStats(other, statCounts);
            var value = Standalone(augment, statCounts);
            foreach (var other in set)
                value += Synergy(augment, other);
            return value;
        }

        public double SetValue(IReadOnlyList<AugmentInfo> set)
        {
            var total = 0.0;
            var statCounts = new Dictionary<AugmentEffect, int>();
            for (var i = 0; i < set.Count; i++)
            {
                total += Standalone(set[i], statCounts);
                CountStats(set[i], statCounts);
                for (var j = 0; j < i; j++)
                    total += Synergy(set[i], set[j]);
            }
            return total;
        }

        public double Synergy(AugmentInfo a, AugmentInfo b)
        {
            if (!_synergy.TryGetValue((a, b), out var value))
                _synergy[(a, b)] = value = scorer.Synergy(a, b, ctx, null);
            return value;
        }

        private double Standalone(AugmentInfo augment, Dictionary<AugmentEffect, int> statCounts)
        {
            if (!_cards.TryGetValue(augment, out var card))
                _cards[augment] = card = (scorer.ComputeFitParts(augment, ctx),
                    scorer.ItemSynergy(augment, ctx, null) + Situational(augment, ctx, null) + (ctx.Community?.Points(augment, null) ?? 0));
            return CombineFit(augment, card.Fit, statCounts) + card.ItemsAndSituations;
        }

        /// <summary>Synergy is symmetric, so (a, b) and (b, a) share a cache entry.</summary>
        private sealed class PairComparer : IEqualityComparer<(AugmentInfo, AugmentInfo)>
        {
            public static readonly PairComparer Instance = new();

            public bool Equals((AugmentInfo, AugmentInfo) x, (AugmentInfo, AugmentInfo) y) =>
                ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2)
                || ReferenceEquals(x.Item1, y.Item2) && ReferenceEquals(x.Item2, y.Item1);

            public int GetHashCode((AugmentInfo, AugmentInfo) pair) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Item1) ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Item2);
        }
    }

    /// <summary>Total value of a set of augments: each card's own value plus every pair's synergy.</summary>
    public double SetValue(IReadOnlyList<AugmentInfo> set, AugmentContext ctx)
    {
        var total = 0.0;
        var statCounts = new Dictionary<AugmentEffect, int>();
        for (var i = 0; i < set.Count; i++)
        {
            total += Standalone(set[i], ctx, statCounts, null);
            for (var j = 0; j < i; j++)
                total += Synergy(set[i], set[j], ctx, null);
        }
        return total;
    }

    /// <summary>How much <paramref name="augment"/> adds on top of what you already picked, with the reasons.</summary>
    public double Value(AugmentInfo augment, IReadOnlyList<AugmentInfo> picked, AugmentContext ctx, List<ScoreReason>? reasons = null)
    {
        var statCounts = new Dictionary<AugmentEffect, int>();
        foreach (var p in picked)
            CountStats(p, statCounts);

        var value = Standalone(augment, ctx, statCounts, reasons);
        foreach (var p in picked)
            value += Synergy(augment, p, ctx, reasons);
        return value;
    }

    /// <summary>Champion fit, item synergy and game situation for one card.</summary>
    private double Standalone(AugmentInfo augment, AugmentContext ctx, Dictionary<AugmentEffect, int> statCounts, List<ScoreReason>? reasons)
    {
        var fit = Fit(augment, ctx, statCounts, reasons);
        CountStats(augment, statCounts);
        return fit + ItemSynergy(augment, ctx, reasons) + Situational(augment, ctx, reasons) + (ctx.Community?.Points(augment, reasons) ?? 0);
    }

    /// <summary>What a card gives, valued for your champion, before repeated stats are discounted.</summary>
    private sealed record FitParts(IReadOnlyList<(AugmentEffect Effect, string Name, double Value)> Effects, double Factor);

    private FitParts ComputeFitParts(AugmentInfo augment, AugmentContext ctx)
    {
        var me = ctx.Me;
        var weights = ArchetypeProfiles.For(me.Archetype).StatWeights;
        var effects = new List<(AugmentEffect, string, double)>();

        foreach (var (effect, stats) in StatEffects)
        {
            if (!augment.Gives(effect))
                continue;
            var value = stats.Max(s => weights.GetValueOrDefault(s));
            if (effect == AugmentEffect.CritChance && me.Stats.CritChance >= 75)
                value *= 0.3; // mostly past the 100% cap
            effects.Add((effect, Describe(effect), value));
        }

        foreach (var effect in MechanicEffects)
        {
            if (augment.Gives(effect))
                effects.Add((effect, Describe(effect), MechanicValue(effect, me)));
        }

        return new FitParts(effects, TriggerFactor(augment.Triggers, ctx));
    }

    private static double CombineFit(AugmentInfo augment, FitParts parts, Dictionary<AugmentEffect, int> statCounts)
    {
        if (augment.IsRandom)
            return RandomAugmentFit;

        var gives = augment.Effects == AugmentEffect.None
            ? UnknownEffectFit
            : parts.Effects
                .Select(e => IsStat(e.Effect) ? e.Value * Math.Pow(RepeatedStatDecay, statCounts.GetValueOrDefault(e.Effect)) : e.Value)
                .OrderByDescending(v => v)
                .Select((v, i) => v * Math.Pow(ExtraEffectDecay, i))
                .Sum();

        var fit = gives * parts.Factor;
        if (augment.HasDrawback) fit -= DrawbackPenalty;
        if (augment.IsQuest) fit *= QuestFactor;
        return fit;
    }

    private double Fit(AugmentInfo augment, AugmentContext ctx, Dictionary<AugmentEffect, int> statCounts, List<ScoreReason>? reasons)
    {
        var parts = ComputeFitParts(augment, ctx);
        var fit = CombineFit(augment, parts, statCounts);

        if (reasons is not null && !augment.IsRandom)
        {
            var me = ctx.Me;
            var top = parts.Effects.Where(e => e.Value >= 0.5).OrderByDescending(e => e.Value).Take(2).Select(e => e.Name).ToList();
            var gate = Each(augment.Triggers & GateTriggers).FirstOrDefault(t => TriggerAffinity(t, ctx) >= 1.0);
            if (gate != AugmentTrigger.None)
                reasons.Add(new($"{me.Name} has {DescribeGate(gate)}", 0.5));
            if (parts.Factor < 0.35 && augment.Triggers != AugmentTrigger.None)
                reasons.Add(new($"little use for {me.Name} ({DescribeWorstTrigger(augment.Triggers, ctx)})", -1));
            else if (top.Count > 0 && fit >= 0.8)
                reasons.Add(new($"{string.Join(" and ", top)} {(top.Count == 1 ? "fits" : "fit")} a {me.Archetype.DisplayName().ToLowerInvariant()}", fit * 0.5));
        }
        return fit;
    }

    /// <summary>
    /// How well you meet the card's conditions. Conditions (attacking, pets, dashes) multiply fully;
    /// scaling (AD ratio, max health) matters less because most cards scale with several stats.
    /// </summary>
    private double TriggerFactor(AugmentTrigger triggers, AugmentContext ctx)
    {
        var conditions = Each(triggers & ~ScalingTriggers).Select(t => TriggerAffinity(t, ctx)).ToList();
        var scaling = Each(triggers & ScalingTriggers).Select(t => TriggerAffinity(t, ctx)).ToList();
        var condition = conditions.Count > 0 ? conditions.Average() : 1.0;

        // A card built around spinning, pets, stealth or stacks lives or dies by that. Its other conditions (ability
        // hits, the ultimate) describe how, and averaging them in watered down a Garen with Spin To Win.
        if ((triggers & GateTriggers) != 0)
            condition = Each(triggers & GateTriggers).Max(t => TriggerAffinity(t, ctx));
        var scale = scaling.Count > 0 ? scaling.Max() : 1.0;
        return condition * (0.5 + 0.5 * scale);
    }

    /// <summary>0-1.4: how naturally your champion does this, or how much of this stat it has.</summary>
    internal double TriggerAffinity(AugmentTrigger trigger, AugmentContext ctx)
    {
        var me = ctx.Me;
        var id = me.Champion.Id;
        var a = me.Archetype;
        return trigger switch
        {
            AugmentTrigger.Attacks => a switch { Archetype.Marksman or Archetype.OnHit => 1.0, Archetype.Bruiser => 0.8, Archetype.AdAssassin or Archetype.ApBruiser => 0.5, Archetype.Tank => 0.4, _ => 0.2 },
            AugmentTrigger.Crits => Math.Clamp(me.Stats.CritChance / 100 + (a == Archetype.Marksman ? 0.5 : 0) + 0.15 * CountItems(ctx, i => i.Stat(Stat.CritChance) > 0), 0.1, 1.2),
            AugmentTrigger.AbilityHits => a switch { Archetype.Mage or Archetype.ApAssassin => 1.0, Archetype.ApBruiser or Archetype.AdAssassin => 0.9, Archetype.Marksman or Archetype.OnHit => 0.5, _ => 0.7 },
            AugmentTrigger.Ultimate => 0.7,
            AugmentTrigger.Healing => me.Champion.Has(ChampionTraits.Healer) || a is Archetype.Bruiser or Archetype.ApBruiser ? 0.8 : 0.3,
            AugmentTrigger.AllySupport => a == Archetype.Enchanter ? 1.0 : me.Champion.Has(ChampionTraits.Shielder) || me.Champion.Has(ChampionTraits.Healer) ? 0.5 : 0.1,
            AugmentTrigger.Shields => me.Champion.Has(ChampionTraits.Shielder) ? 0.9 : 0.3,
            AugmentTrigger.LowHealth => a is Archetype.Bruiser or Archetype.ApBruiser ? 0.8 : a == Archetype.Tank ? 0.6 : 0.3,
            AugmentTrigger.Immobilize => me.Champion.Has(ChampionTraits.HeavyCrowdControl) ? 1.0 : 0.25,
            AugmentTrigger.Takedowns => a.IsAssassin() ? 0.9 : a is Archetype.Marksman or Archetype.OnHit ? 0.6 : 0.4,
            AugmentTrigger.Dashes => ChampionKnowledge.Dashers.Contains(id) ? 1.0 : 0.1,
            AugmentTrigger.Pets => ChampionKnowledge.Pets.Contains(id) ? NicheMatch : 0.0,
            AugmentTrigger.Spinning => ChampionKnowledge.Spinners.Contains(id) ? NicheMatch : 0.0,
            AugmentTrigger.Stealth => ChampionKnowledge.Stealthers.Contains(id) ? NicheMatch : 0.1,
            AugmentTrigger.Stacking => ChampionKnowledge.Stackers.Contains(id) ? NicheMatch : 0.0,
            AugmentTrigger.Mana => me.Champion.UsesMana ? 0.7 : 0.0,
            AugmentTrigger.SummonerSpells => 0.6,
            AugmentTrigger.Death => 0.3,
            AugmentTrigger.Distance => me.Stats.IsRanged ? 0.8 : 0.2,
            AugmentTrigger.AttackDamage => me.DamageType == DamageType.Physical ? 1.0 : a == Archetype.OnHit ? 0.6 : 0.15,
            AugmentTrigger.AbilityPower => me.DamageType == DamageType.Magic || a == Archetype.Enchanter ? 1.0 : a == Archetype.OnHit ? 0.6 : 0.15,
            AugmentTrigger.MaxHealth => a == Archetype.Tank ? 1.0 : a is Archetype.Bruiser or Archetype.ApBruiser ? 0.7 : 0.25,
            AugmentTrigger.BonusResists => a == Archetype.Tank ? 1.0 : a is Archetype.Bruiser or Archetype.ApBruiser ? 0.6 : 0.15,
            AugmentTrigger.MoveSpeed => 0.5,
            AugmentTrigger.AbilityHaste => 0.8 * TriggerAffinity(AugmentTrigger.AbilityHits, ctx),
            _ => 0.5,
        };
    }

    private static double MechanicValue(AugmentEffect effect, PlayerProfile me)
    {
        var a = me.Archetype;
        var dealsDamage = me.DamageType != DamageType.None;
        return effect switch
        {
            AugmentEffect.OnHit => a switch { Archetype.Marksman or Archetype.OnHit => 1.0, Archetype.Bruiser or Archetype.ApBruiser => 0.6, _ => 0.2 },
            AugmentEffect.TrueDamage => dealsDamage ? 0.8 : 0.3,
            AugmentEffect.MaxHealthDamage => dealsDamage ? 0.7 : 0.4,
            AugmentEffect.Burn => a is Archetype.Mage or Archetype.ApBruiser or Archetype.Tank ? 0.7 : 0.4,
            AugmentEffect.ResistShred => dealsDamage ? 0.6 : 0.3,
            AugmentEffect.Shield => a.IsFrontline() ? 0.7 : 0.5,
            AugmentEffect.Heal => a.IsFrontline() ? 0.7 : 0.4,
            AugmentEffect.CrowdControl => a is Archetype.Tank or Archetype.Enchanter ? 0.7 : 0.4,
            AugmentEffect.Execute => a.IsAssassin() ? 0.8 : 0.4,
            AugmentEffect.SpellShield => a.IsSquishy() ? 0.6 : 0.3,
            AugmentEffect.Invulnerable => 0.6,
            AugmentEffect.Stealth => a.IsAssassin() ? 0.8 : 0.3,
            AugmentEffect.Gold => 0.3,
            AugmentEffect.SummonerSpell => 0.4,
            AugmentEffect.Keystones => 0.6,
            AugmentEffect.AntiHeal => 0.3,
            AugmentEffect.Dash => a.IsAssassin() || a.IsFrontline() ? 0.6 : 0.4,
            AugmentEffect.AttackRange => me.Stats.IsRanged ? (a is Archetype.Marksman or Archetype.OnHit ? 0.8 : 0.4) : 0.2,
            AugmentEffect.Damage => dealsDamage ? 0.6 : 0.3,
            _ => 0.3,
        };
    }

    /// <summary>Two cards pair up when one gives what the other needs, weighted by whether you actually trigger it.</summary>
    public double Synergy(AugmentInfo a, AugmentInfo b, AugmentContext ctx, List<ScoreReason>? reasons)
    {
        var points = 0.0;
        string? how = null;
        foreach (var (trigger, enabler, weight) in Enablers)
        {
            if (a.Needs(trigger) && b.Gives(enabler) || b.Needs(trigger) && a.Gives(enabler))
            {
                var p = weight * TriggerAffinity(trigger, ctx);
                if (p > 0.2 && how is null)
                    how = $"{Describe(enabler)} feeds {DescribeTrigger(trigger)}";
                points += p;
            }
        }

        var synergy = Math.Min(points, 2.0) * SynergyWeight;
        if (reasons is not null && synergy >= 0.3)
            reasons.Add(new($"pairs with {b.Name}{(how is null ? "" : $" ({how})")}", synergy));
        return synergy;
    }

    private double ItemSynergy(AugmentInfo augment, AugmentContext ctx, List<ScoreReason>? reasons)
    {
        var owned = ctx.Me.Items.Where(i => i.Kind is ItemKind.Legendary or ItemKind.Boots).ToList();
        var points = 0.0;

        // Cards that pay off on something your items give.
        foreach (var trigger in Each(augment.Triggers))
        {
            Func<ItemInfo, bool>? enables = trigger switch
            {
                AugmentTrigger.Attacks => i => i.Stat(Stat.AttackSpeed) > 0 || i.Tags.Contains("OnHit"),
                AugmentTrigger.Crits => i => i.Stat(Stat.CritChance) > 0,
                AugmentTrigger.AbilityHits or AugmentTrigger.AbilityHaste => i => i.Stat(Stat.AbilityHaste) > 0,
                AugmentTrigger.MaxHealth => i => i.Stat(Stat.Health) >= 300,
                AugmentTrigger.BonusResists => i => i.Stat(Stat.Armor) > 0 || i.Stat(Stat.MagicResist) > 0,
                AugmentTrigger.Healing => i => i.Has(ItemTraits.Sustain),
                AugmentTrigger.Shields => i => i.Has(ItemTraits.GrantsShield),
                AugmentTrigger.AllySupport => i => i.Stat(Stat.HealShieldPower) > 0,
                AugmentTrigger.AttackDamage => i => i.Stat(Stat.AttackDamage) > 0,
                AugmentTrigger.AbilityPower => i => i.Stat(Stat.AbilityPower) > 0,
                _ => null,
            };
            if (enables is null)
                continue;

            var count = owned.Count(enables) + 0.5 * ctx.PlannedItems.Count(enables);
            var p = Math.Min(1.0, 0.35 * count) * TriggerAffinity(trigger, ctx);
            if (p >= 0.3)
                reasons?.Add(new($"uses your {DescribeTrigger(trigger)} items", p * ItemSynergyWeight));
            points += p;
        }

        // Cards that give what your items pay off on.
        if (augment.Gives(AugmentEffect.AttackSpeed) && owned.Concat(ctx.PlannedItems).Any(i => i.Tags.Contains("OnHit")))
            points += 0.4;
        if (augment.Gives(AugmentEffect.CritChance) && owned.Concat(ctx.PlannedItems).Any(i => i.Stat(Stat.CritDamage) > 0))
            points += 0.5;

        // "Upgrade Infinity Edge" is great if you build Infinity Edge, and dead weight if you don't.
        if (augment.MentionedItems.Count > 0)
        {
            var mine = augment.MentionedItems.FirstOrDefault(i => owned.Any(o => o.Name == i.Name));
            var planned = augment.MentionedItems.FirstOrDefault(i => ctx.PlannedItems.Any(o => o.Name == i.Name));
            if (mine is not null)
            {
                points += 2.5;
                reasons?.Add(new($"upgrades your {mine.Name}", 1.5));
            }
            else if (planned is not null)
            {
                points += 1.7;
                reasons?.Add(new($"upgrades {planned.Name} from your build", 1.0));
            }
            else
            {
                points -= 0.7;
                reasons?.Add(new($"needs {augment.MentionedItems[0].Name}, which isn't in your build", -0.4));
            }
        }

        return points * ItemSynergyWeight;
    }

    private static double Situational(AugmentInfo augment, AugmentContext ctx, List<ScoreReason>? reasons)
    {
        var total = 0.0;
        foreach (var situation in ctx.Situations)
        {
            if (!SituationWants.TryGetValue(situation.Label, out var wants))
                continue;
            var points = situation.Impact * wants.Where(w => augment.Gives(w.Effect)).Sum(w => w.Weight) * SituationWeight;
            if (points >= 0.25)
                reasons?.Add(new($"good {situation.Label}", points));
            total += points;
        }
        return total;
    }

    private static void CountStats(AugmentInfo augment, Dictionary<AugmentEffect, int> counts)
    {
        foreach (var (effect, _) in StatEffects)
            if (augment.Gives(effect))
                counts[effect] = counts.GetValueOrDefault(effect) + 1;
    }

    private static int CountItems(AugmentContext ctx, Func<ItemInfo, bool> predicate) =>
        ctx.Me.Items.Count(i => i.Kind == ItemKind.Legendary && predicate(i)) + ctx.PlannedItems.Count(predicate) / 2;

    private static readonly HashSet<AugmentEffect> StatEffectSet = StatEffects.Select(s => s.Effect).ToHashSet();

    private static readonly AugmentEffect[] MechanicEffects =
        Enum.GetValues<AugmentEffect>().Where(e => e != AugmentEffect.None && !StatEffectSet.Contains(e)).ToArray();

    private static bool IsStat(AugmentEffect effect) => StatEffectSet.Contains(effect);

    private static IEnumerable<AugmentTrigger> Each(AugmentTrigger triggers) =>
        Enum.GetValues<AugmentTrigger>().Where(t => t != AugmentTrigger.None && (triggers & t) != 0);

    private string DescribeWorstTrigger(AugmentTrigger triggers, AugmentContext ctx) =>
        DescribeTrigger(Each(triggers).MinBy(t => TriggerAffinity(t, ctx)));

    private static string Describe(AugmentEffect effect) => effect switch
    {
        AugmentEffect.AttackDamage => "attack damage",
        AugmentEffect.AbilityPower => "ability power",
        AugmentEffect.AttackSpeed => "attack speed",
        AugmentEffect.CritChance => "crit chance",
        AugmentEffect.CritDamage => "crit damage",
        AugmentEffect.AbilityHaste => "ability haste",
        AugmentEffect.MagicResist => "magic resist",
        AugmentEffect.MoveSpeed => "move speed",
        AugmentEffect.HealShieldPower => "heal and shield power",
        AugmentEffect.AdaptiveForce => "adaptive force",
        AugmentEffect.OnHit => "on-hit",
        AugmentEffect.TrueDamage => "true damage",
        AugmentEffect.MaxHealthDamage => "%health damage",
        AugmentEffect.ResistShred => "resist shred",
        AugmentEffect.CrowdControl => "crowd control",
        AugmentEffect.SpellShield => "a spell shield",
        AugmentEffect.AttackRange => "attack range",
        AugmentEffect.SummonerSpell => "a summoner spell",
        AugmentEffect.AntiHeal => "anti-heal",
        _ => effect.ToString().ToLowerInvariant(),
    };

    private static string DescribeTrigger(AugmentTrigger trigger) => trigger switch
    {
        AugmentTrigger.Attacks => "on-hit/attack",
        AugmentTrigger.Crits => "crit",
        AugmentTrigger.AbilityHits => "ability",
        AugmentTrigger.AbilityHaste => "ability haste",
        AugmentTrigger.AllySupport => "ally healing and shielding",
        AugmentTrigger.LowHealth => "low-health",
        AugmentTrigger.MaxHealth => "health",
        AugmentTrigger.BonusResists => "resistance",
        AugmentTrigger.AttackDamage => "attack damage",
        AugmentTrigger.AbilityPower => "ability power",
        AugmentTrigger.MoveSpeed => "move speed",
        AugmentTrigger.Immobilize => "crowd control",
        AugmentTrigger.Pets => "no pets",
        AugmentTrigger.Spinning => "no spinning abilities",
        AugmentTrigger.Dashes => "dash",
        AugmentTrigger.Stacking => "no permanent stacks",
        AugmentTrigger.Mana => "mana",
        _ => trigger.ToString().ToLowerInvariant(),
    };
}
