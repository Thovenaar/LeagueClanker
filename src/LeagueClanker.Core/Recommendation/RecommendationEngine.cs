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

/// <summary>Selling <see cref="Sell"/> to make room for <see cref="Buy"/>, both scored as if that slot were empty.</summary>
public sealed record ItemSwap(ScoredItem Sell, ScoredItem Buy)
{
    public double Gain => Buy.Total - Sell.Total;
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

    public const int FullBuild = 6;

    /// <summary>All six slots hold finished items, boots included.</summary>
    public bool IsFull => Game.Me.Items.Count(i => i.Kind is ItemKind.Legendary or ItemKind.Boots) >= FullBuild;

    /// <summary>Slots still open: six, minus the finished items and boots you own. <see cref="BuyOrder"/> fills them.</summary>
    public int SlotsLeft => Math.Max(0, FullBuild - Game.Me.Items.Count(i => i.Kind is ItemKind.Legendary or ItemKind.Boots));

    /// <summary>
    /// What to buy, in order: the ranked items with your boots among them. Boots take one of the six slots, so they're
    /// part of the order: after your first item, or next when you have one already.
    /// </summary>
    public IReadOnlyList<ScoredItem> BuyOrder => Boots is not { } boots ? Ranked : [.. Ranked.Take(BootsIndex), boots, .. Ranked.Skip(BootsIndex)];

    /// <summary>Where the boots go in <see cref="BuyOrder"/>, or -1 without boots to buy.</summary>
    public int BootsIndex => Boots is null ? -1 : Math.Min(Ranked.Count, Game.Me.Items.Any(i => i.Kind == ItemKind.Legendary) ? 0 : 1);

    /// <summary>With a full build: items worth selling for a clearly better one, best first. Empty otherwise.</summary>
    public IReadOnlyList<ItemSwap> Swaps { get; init; } = [];

    /// <summary>The op.gg build this recommendation follows, or null when it comes from item scores alone.</summary>
    public MetaChoice? Meta { get; init; }

    public ScoredItem? Find(int itemId) => Ranked.FirstOrDefault(s => s.Item.Id == itemId) ?? (Boots?.Item.Id == itemId ? Boots : null);

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

    /// <summary>A full build's item is only worth selling for one that scores this much higher.</summary>
    public const double SellMargin = 1.0;

    private const int MaxSwaps = 3;

    /// <summary>Items a card you picked can swap out of an op.gg build, core included.</summary>
    private const int MaxAugmentSwaps = 2;

    /// <summary>An item feeds a picked card when that card's situation gives it this many points.</summary>
    private const double AugmentFeedPoints = 0.5;

    // These grow during the game (Heartsteel's health, Rod of Ages' stats, a Tear's mana) and selling throws that away.
    private static readonly HashSet<string> GrowingItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Heartsteel", "Rod of Ages", "Yun Tal Wildarrows", "Mejai's Soulstealer", "Seraph's Embrace", "Muramana", "Fimbulwinter",
        "Archangel's Staff", "Manamune", "Winter's Approach",
    };

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
        var finished = owned.Where(i => i.Kind is ItemKind.Legendary or ItemKind.Boots).ToList();
        // Unique passives don't stack, so skip items that repeat a passive of something already finished.
        var ownedPassives = finished.SelectMany(i => i.Passives).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Available(ItemInfo item) => !ownedIds.Contains(item.Id) && !item.Passives.Any(ownedPassives.Contains);

        // A situation you've already answered with a finished item matters less for the next purchase.
        Dictionary<Situation, double> WeightsWith(IReadOnlyList<ItemInfo> have) => situations.ToDictionary(s => s, s => s.Stacks ? 1.0 :
            have.Aggregate(1.0, (weight, item) => s.Score(item) >= ScoredItem.MinReasonPoints ? weight * OwnedDecay : weight)
            * game.Augments.Aggregate(1.0, (weight, augment) => AugmentRules.Answers(augment, s) ? weight * OwnedDecay : weight));
        var weights = WeightsWith(finished);

        var mine = AugmentRules.WithAugmentStats(game.Me.Stats, game.Augments);
        // An item that pays off on a card you picked fits, even if your playstyle alone wouldn't buy it: Rabadon's
        // Deathcap for a Marksmage Kai'Sa.
        var cards = situations.Where(s => game.Augments.Any(a => a.Name == s.Label)).ToList();
        bool Fits(ItemInfo item) => (!item.IsJungleItem || game.Me.HasSmite)
            && (profile.BaseScore(item, mine) >= profile.MinFit || cards.Sum(c => c.Score(item)) >= AugmentFeedPoints);
        var candidates = data.Items.LegendariesFor(game.Mode).Where(i => Available(i) && Fits(i)).ToList();
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
            foreach (var reason in best.Reasons.Where(r => !r.Situation.Stacks))
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
            // Never swap out an item you've started: you'd rather finish it (the planner puts it first anyway).
            bool Started(ItemInfo item) => item.TotalGold > 0
                && 1 - (double)BuyAdvisor.RemainingCost(item, owned, data.Items) / item.TotalGold >= BuildPlanner.StartedShare;

            // A card you picked changes the build more than an enemy does, and for the rest of the game: up to two items
            // you haven't started that do nothing for it make way for items that pay off on it, core items included.
            // Your build's own items that feed the card come first, then the new ones. Marksmage Kai'Sa: Guinsoo's,
            // Nashor's and Rabadon's instead of Kraken Slayer first.
            var picked = cards;
            var swaps = new List<string>();
            var augmentSwapIns = new List<ScoredItem>();
            if (picked.Count > 0)
            {
                double ForCards(ScoredItem s) => picked.Sum(s.PointsFor);
                var useless = build.Where(s => ForCards(s) < AugmentFeedPoints / 2 && !Started(s.Item)).ToList();
                var options = ranked.Where(s => build.All(b => b.Item.Id != s.Item.Id) && ForCards(s) >= AugmentFeedPoints)
                    .OrderByDescending(ForCards).ToList();
                foreach (var gone in useless)
                {
                    // Unique passives don't stack: no second Spellblade item next to Lich Bane.
                    var taken = Passives(build.Where(b => b != gone).Concat(augmentSwapIns));
                    var better = options.FirstOrDefault(s => !augmentSwapIns.Contains(s) && !s.Item.Passives.Any(taken.Contains));
                    if (better is null || Points(better) < Points(gone) + SwapMargin || augmentSwapIns.Count == MaxAugmentSwaps)
                        break;
                    build.Remove(gone);
                    augmentSwapIns.Add(better);
                    var why = better.Reasons.First(r => picked.Contains(r.Situation)).Situation.Description;
                    swaps.Add($"{better.Item.Name} instead of {gone.Item.Name}: {char.ToLowerInvariant(why[0])}{why[1..]}");
                }
                if (augmentSwapIns.Count > 0)
                {
                    var feeders = build.Where(s => ForCards(s) >= AugmentFeedPoints).ToList();
                    build = [.. feeders, .. augmentSwapIns, .. build.Except(feeders)];
                }
            }

            ScoredItem? enemySwap = null;
            if (build.Where(s => !core.Contains(s.Item.Id) && !augmentSwapIns.Contains(s) && !Started(s.Item)).MinBy(Points) is { } weakest)
            {
                var taken = Passives(build.Where(b => b != weakest));
                var options = pool.Where(s => build.All(b => b.Item.Id != s.Item.Id) && !s.Item.Passives.Any(taken.Contains)).ToList();
                var best = options.Where(s => s.Reasons.Any(r => r.Points >= BuildPlanner.MinReasonPoints)).MaxBy(Points);
                // Last time's swap needs only to stay ahead of the item it replaced, unless something clearly better came up.
                var kept = options.FirstOrDefault(s => s.Item.Id == game.KeepSwapIn && Points(s) > Points(weakest));
                var better = kept is not null && (best is null || Points(best) < Points(kept) + SwapMargin) ? kept
                    : best is not null && Points(best) >= Points(weakest) + SwapMargin ? best
                    : null;
                if (better is not null)
                {
                    build[build.IndexOf(weakest)] = better;
                    enemySwap = better;
                    var why = better.Reasons.FirstOrDefault()?.Situation.Description ?? "It still fits this game better";
                    swaps.Add($"{better.Item.Name} instead of {weakest.Item.Name}: {char.ToLowerInvariant(why[0])}{why[1..]}");
                }
            }
            if (swaps.Count > 0)
                meta = choice with { Swap = string.Join("; ", swaps), SwapIn = enemySwap?.Item, AugmentSwapIns = augmentSwapIns.Select(s => s.Item).ToList() };
            ranked = [.. build, .. ranked.Where(s => build.All(b => b.Item.Id != s.Item.Id))];

            // The build's boots, unless the game has a real reason for others (Mercury's against heavy crowd control).
            if (boots.Count > 0 && choice.Build.Boots is { } metaBoots && boots.FirstOrDefault(s => s.Item.Id == metaBoots.Id) is { } theirs
                && !boots[0].Reasons.Any(r => r.Points >= BuildPlanner.MinReasonPoints))
                boots = [theirs, .. boots.Where(s => s != theirs)];
        }

        // With every slot taken, each item is judged as if you sold it: what it answers counts in full again, and the
        // best item you could buy instead gets the same chance. Both are judged against the rest of your build, so an
        // accepted swap never suggests swapping back.
        List<ItemSwap> FindSwaps()
        {
            var swaps = new List<ItemSwap>();
            foreach (var sell in finished.Where(i => i.Kind == ItemKind.Legendary && Sellable(i, game)).DistinctBy(i => i.Id))
            {
                var rest = finished.Where(i => i.Id != sell.Id).ToList();
                var restPassives = rest.SelectMany(i => i.Passives).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var without = WeightsWith(rest);
                var sold = Score(sell, profile, mine, situations, without);
                var best = data.Items.LegendariesFor(game.Mode)
                    .Where(i => !ownedIds.Contains(i.Id) && !i.Passives.Any(restPassives.Contains) && Fits(i))
                    .Select(i => Score(i, profile, mine, situations, without))
                    .MaxBy(s => s.Total);
                if (best is not null && best.Total - sold.Total >= SellMargin)
                    swaps.Add(new ItemSwap(sold, best));
            }
            return swaps.OrderByDescending(s => s.Gain).DistinctBy(s => s.Buy.Item.Id).Take(MaxSwaps).ToList();
        }

        return new BuildRecommendation(game, ranked.Take(maxItems).ToList(), boots.FirstOrDefault(), situations, advice)
        {
            Ranked = ranked, Meta = meta,
            Swaps = finished.Count >= BuildRecommendation.FullBuild ? FindSwaps() : [],
        };
    }

    private static HashSet<string> Passives(IEnumerable<ScoredItem> items) =>
        items.SelectMany(s => s.Item.Passives).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Not an item that grew during the game, or one a Mayhem augment upgrades.</summary>
    private static bool Sellable(ItemInfo item, GameAnalysis game) =>
        !GrowingItems.Contains(item.Name) && !game.Augments.Any(a => a.MentionedItems.Any(m => m.Name == item.Name));

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
