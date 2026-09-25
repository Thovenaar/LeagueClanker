using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

public sealed record ItemContribution(Situation Situation, double Points);

public sealed record ScoredItem(ItemInfo Item, double BaseScore, IReadOnlyList<ItemContribution> Contributions)
{
    public const double MinReasonPoints = 0.15;

    public double Total { get; } = BaseScore + Contributions.Sum(c => c.Points);

    /// <summary>Situations that pushed this item up, strongest first.</summary>
    public IEnumerable<ItemContribution> Reasons =>
        Contributions.Where(c => c.Points >= MinReasonPoints).OrderByDescending(c => c.Points);

    public double PointsFor(Situation situation) => Contributions.FirstOrDefault(c => c.Situation == situation)?.Points ?? 0;

    /// <summary>What the item's scaling passives add for you: "Magical Opus: +95 AP".</summary>
    public IReadOnlyList<string> Effects { get; init; } = [];
}

/// <summary>"Enemy team is 80% AP, so I suggest X, but Y is also ok."</summary>
public sealed record Advice(Situation Situation, ItemInfo Suggested, ItemInfo? Alternative, ItemInfo? RushComponent)
{
    public string Text
    {
        get
        {
            var text = $"{Situation.Description}, so I suggest {Suggested.Name}";
            if (RushComponent is not null)
                text += $" (rush {RushComponent.Name} early)";
            return Alternative is null ? text + "." : text + $", but {Alternative.Name} is also ok.";
        }
    }
}

public sealed record BuildRecommendation(
    GameAnalysis Game,
    IReadOnlyList<ScoredItem> Items,
    ScoredItem? Boots,
    IReadOnlyList<Situation> Situations,
    IReadOnlyList<Advice> Advice)
{
    /// <summary>Every candidate item in ranked order; <see cref="Items"/> is the top of this list.</summary>
    public IReadOnlyList<ScoredItem> Ranked { get; init; } = Items;

    /// <summary>Your finished legendaries, scored like the candidates. Used to suggest swaps once your build is full.</summary>
    public IReadOnlyList<ScoredItem> Owned { get; init; } = [];

    /// <summary>The op.gg build this recommendation follows, or null when it comes from item scores alone.</summary>
    public MetaChoice? Meta { get; init; }

    public ScoredItem? Find(int itemId) => Ranked.FirstOrDefault(s => s.Item.Id == itemId);

    public string DamageSummary =>
        $"Enemy damage: {BuildRules.Percent(Game.Enemies.PhysicalShare)} AD / {BuildRules.Percent(Game.Enemies.MagicShare)} AP";
}

/// <summary>
/// Ranks items for the active player: a base score from what their archetype values,
/// plus bonuses from every situation the rules detect in the live game.
/// </summary>
public sealed class RecommendationEngine(StaticGameData data, IReadOnlyList<IBuildRule>? rules = null)
{
    private const double RepeatDecay = 0.5;
    private const double OwnedDecay = 0.6;

    /// <summary>A meta build's later item is only swapped for an item whose game-situation points are this much higher.</summary>
    public const double SwapMargin = 1.0;

    // Effects worth rushing a component for, e.g. Oblivion Orb before Morellonomicon.
    private const ItemTraits RushableTraits =
        ItemTraits.AntiHeal | ItemTraits.AntiShield | ItemTraits.Stasis | ItemTraits.SpellShield | ItemTraits.Cleanse;

    private readonly IReadOnlyList<IBuildRule> _rules = rules ?? BuildRules.Default;

    public BuildRecommendation Recommend(GameAnalysis game, int maxItems = 6)
    {
        var profile = ArchetypeProfiles.For(game.Me.Archetype);
        var situations = _rules
            .Select(r => r.Evaluate(game))
            .OfType<Situation>()
            .Concat(AugmentRules.Evaluate(game))
            .Where(s => s.Impact > 0)
            .OrderByDescending(s => s.Impact)
            .ToList();

        var owned = game.Me.Items;
        var ownedIds = owned.Select(i => i.Id).ToHashSet();
        // Unique passives don't stack, so skip items that repeat a passive of something already finished.
        var ownedPassives = owned
            .Where(i => i.Kind is ItemKind.Legendary or ItemKind.Boots)
            .SelectMany(i => i.Passives)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Available(ItemInfo item) => !ownedIds.Contains(item.Id) && !item.Passives.Any(ownedPassives.Contains);

        // A situation you've already answered with a finished item matters less for the next purchase.
        var weights = situations.ToDictionary(s => s, s =>
            owned
                .Where(i => i.Kind is ItemKind.Legendary or ItemKind.Boots)
                .Aggregate(1.0, (weight, item) => s.Score(item) >= ScoredItem.MinReasonPoints ? weight * OwnedDecay : weight)
            * game.Augments.Aggregate(1.0, (weight, augment) => AugmentRules.Answers(augment, s) ? weight * OwnedDecay : weight));

        var mine = AugmentRules.WithAugmentStats(game.Me.Stats, game.Augments);
        var ownedScores = owned.Where(i => i.Kind == ItemKind.Legendary).Select(i => Score(i, profile, mine, situations, weights)).ToList();
        var candidates = data.Items.LegendariesFor(game.Mode)
            .Where(Available)
            .Where(i => !i.IsJungleItem || game.Me.HasSmite)
            .Where(i => profile.BaseScore(i, mine) >= profile.MinFit)
            .ToList();
        var boots = owned.Any(i => i.IsBoots && !ItemCatalog.BasicBootsIds.Contains(i.Id))
            ? []
            : data.Items.BootsFor(game.Mode).Select(i => Score(i, profile, mine, situations, weights)).OrderByDescending(s => s.Total).ToList();

        var freshWeights = new Dictionary<Situation, double>(weights);

        // Greedy ranking with diminishing returns: once an item answers "enemy is AP", the next MR item is worth less.
        // Without this, a strong situation fills all six slots with the same kind of item.
        var ranked = new List<ScoredItem>();
        while (candidates.Count > 0)
        {
            var best = candidates.Select(i => Score(i, profile, mine, situations, weights)).MaxBy(s => s.Total)!;
            ranked.Add(best);
            candidates.Remove(best.Item);
            foreach (var reason in best.Reasons)
                weights[reason.Situation] *= RepeatDecay;
        }

        var advice = situations
            .Select(s => BuildAdvice(s, [.. ranked, .. boots]))
            .OfType<Advice>()
            .ToList();

        // With op.gg's builds, the plan is one of them, in its buy order, with at most one later item swapped for this game.
        MetaChoice? meta = null;
        if (game.MetaBuilds.Count > 0 && MetaBuilds.Choose(game.MetaBuilds, game, situations, game.KeepMeta) is { } choice)
        {
            meta = choice;
            var build = choice.Build.Items.Where(Available).Select(i => Score(i, profile, mine, situations, freshWeights)).ToList();
            var core = choice.Build.Core.Select(i => i.Id).ToHashSet();
            double Points(ScoredItem s) => s.Contributions.Sum(c => c.Points);
            // The swap picks from the build's own situational items when the source lists them (Blitz), otherwise from every candidate.
            var pool = choice.Build.Situational.Count > 0
                ? choice.Build.Situational.Where(Available).Select(i => Score(i, profile, mine, situations, freshWeights)).ToList()
                : ranked;
            if (build.Where(s => !core.Contains(s.Item.Id)).MinBy(Points) is { } weakest
                && pool.Where(s => build.All(b => b.Item.Id != s.Item.Id) && s.Reasons.Any(r => r.Points >= BuildPlanner.MinReasonPoints)).MaxBy(Points) is { } better
                && Points(better) >= Points(weakest) + SwapMargin)
            {
                build[build.IndexOf(weakest)] = better;
                var why = better.Reasons.First().Situation.Description;
                meta = choice with { Swap = $"{better.Item.Name} instead of {weakest.Item.Name}: {char.ToLowerInvariant(why[0])}{why[1..]}" };
            }
            ranked = [.. build, .. ranked.Where(s => build.All(b => b.Item.Id != s.Item.Id))];

            // The build's boots, unless the game has a real reason for others (Mercury's against heavy crowd control).
            if (boots.Count > 0 && choice.Build.Boots is { } metaBoots && boots.FirstOrDefault(s => s.Item.Id == metaBoots.Id) is { } theirs
                && !boots[0].Reasons.Any(r => r.Points >= BuildPlanner.MinReasonPoints))
                boots = [theirs, .. boots.Where(s => s != theirs)];
        }

        return new BuildRecommendation(game, ranked.Take(maxItems).ToList(), boots.FirstOrDefault(), situations, advice)
        {
            Ranked = ranked, Owned = ownedScores, Meta = meta,
        };
    }

    private static ScoredItem Score(ItemInfo item, ArchetypeProfile profile, StatBlock mine, IReadOnlyList<Situation> situations, Dictionary<Situation, double> weights) =>
        new(item, profile.BaseScore(item, mine), situations.Select(s => new ItemContribution(s, s.Score(item) * weights[s])).ToList())
        {
            Effects = item.Scaling.Where(s => s.Value(item, mine) >= 1 && profile.StatWeights.GetValueOrDefault(s.Stat) > 0).Select(s => s.Note(item, mine)).ToList(),
        };

    /// <param name="pool">Candidates in recommendation order: ranked legendaries, then boots.</param>
    private Advice? BuildAdvice(Situation situation, IReadOnlyList<ScoredItem> pool)
    {
        var answers = pool
            .Where(s => situation.Score(s.Item) >= ScoredItem.MinReasonPoints)
            .Take(2)
            .ToList();

        if (answers.Count == 0)
            return null;

        var suggested = answers[0].Item;
        return new Advice(situation, suggested, answers.ElementAtOrDefault(1)?.Item, FindRushComponent(suggested, situation));
    }

    private ItemInfo? FindRushComponent(ItemInfo item, Situation situation) =>
        item.BuildsFrom
            .Select(data.Items.Get)
            .OfType<ItemInfo>()
            .Where(c => (c.Traits & item.Traits & RushableTraits) != 0 && situation.Match(c) > 0)
            .MinBy(c => c.TotalGold);
}
