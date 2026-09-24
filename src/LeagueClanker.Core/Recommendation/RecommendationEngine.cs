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
        var map = game.Mode.MapId();
        var candidates = data.Items.LegendariesOn(map)
            .Where(Available)
            .Where(i => !i.IsJungleItem || game.Me.HasSmite)
            .Where(i => profile.BaseScore(i, mine) >= profile.MinFit)
            .ToList();
        var boots = owned.Any(i => i.IsBoots && !ItemCatalog.BasicBootsIds.Contains(i.Id))
            ? []
            : data.Items.BootsOn(map).Select(i => Score(i, profile, mine, situations, weights)).OrderByDescending(s => s.Total).ToList();

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

        return new BuildRecommendation(game, ranked.Take(maxItems).ToList(), boots.FirstOrDefault(), situations, advice) { Ranked = ranked, Owned = ownedScores };
    }

    private static ScoredItem Score(ItemInfo item, ArchetypeProfile profile, StatBlock mine, IReadOnlyList<Situation> situations, Dictionary<Situation, double> weights) =>
        new(item, profile.BaseScore(item, mine), situations.Select(s => new ItemContribution(s, s.Score(item) * weights[s])).ToList());

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
