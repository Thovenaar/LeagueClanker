namespace LeagueClanker.Core.Augments;

/// <param name="Now">What the card adds to the cards you already have.</param>
/// <param name="Expected">Average value of your final set of augments if you take this card, over simulated future offers.</param>
/// <param name="Partners">Cards you can still be offered that combine well with this one.</param>
public sealed record AugmentOption(
    AugmentInfo Augment, double Now, double Expected, IReadOnlyList<ScoreReason> Reasons, IReadOnlyList<AugmentInfo> Partners);

public enum RerollAction
{
    /// <summary>Your best card: take it (after rerolling the others).</summary>
    Keep,

    /// <summary>You won't take it, so rerolling it can only help.</summary>
    Reroll,

    /// <summary>Your best card, but weak: reroll the others first, then this one too if it's still your best.</summary>
    RerollLast,

    /// <summary>Already rerolled once; it can't be rerolled again.</summary>
    AlreadyRerolled,
}

/// <param name="RerollBeatsIt">Chance that a reroll gives a better card than this one (0-1).</param>
public sealed record CardReroll(AugmentInfo Augment, RerollAction Action, double RerollBeatsIt);

public sealed record RerollAdvice(IReadOnlyList<CardReroll> Cards, string Text)
{
    public CardReroll? For(AugmentInfo augment) => Cards.FirstOrDefault(c => c.Augment == augment);
}

public sealed record AugmentAdvice(IReadOnlyList<AugmentOption> Ranked)
{
    public AugmentOption Best => Ranked[0];

    /// <summary>Which cards to reroll before picking. Null when no card can be rerolled.</summary>
    public RerollAdvice? Reroll { get; init; }

    /// <summary>"Take X: pairs with Y, uses your crit items. Z is also ok."</summary>
    public string Text
    {
        get
        {
            var reasons = Best.Reasons.Where(r => r.Points > 0).OrderByDescending(r => r.Points).Take(2).Select(r => r.Text).ToList();
            if (Best.Partners.Count > 0 && Best.Expected - Best.Now > 0.5)
                reasons.Add($"sets up {string.Join(" and ", Best.Partners.Take(2).Select(p => p.Name))}");
            var text = reasons.Count > 0 ? $"Take {Best.Augment.Name}: {string.Join(", ", reasons)}." : $"Take {Best.Augment.Name}.";
            return Ranked.Count > 1 ? $"{text} {Ranked[1].Augment.Name} is also ok." : text;
        }
    }
}

/// <summary>
/// Ranks an augment offer by the best final set it leads to, not just by the card itself, and says which
/// cards to reroll first. Future ARAM: Mayhem selections are simulated with the real rules: same tier for all
/// three offerings, random tier per selection (first two never both Silver), and one reroll per offering.
/// </summary>
public sealed class AugmentAdvisor(AugmentCatalog catalog, AugmentScorer? scorer = null, int simulations = 1500)
{
    private const int TotalSelections = 4;
    private const int OfferSize = 3;
    private const int PartnerThreshold = 1; // how often a card must be picked alongside to count as a partner
    private const int RerollTrials = 2000;

    // Every card a reroll could produce gets its own simulation; fewer runs each keeps that affordable.
    private readonly int _rerollSimulations = Math.Max(50, simulations / 5);

    private readonly AugmentScorer _scorer = scorer ?? new AugmentScorer();

    /// <param name="rerolled">Offered cards that were already rerolled and can't be rerolled again.</param>
    public AugmentAdvice Rank(IReadOnlyList<AugmentInfo> offer, AugmentContext ctx, IReadOnlySet<AugmentInfo>? rerolled = null)
    {
        if (offer.Count == 0)
            throw new ArgumentException("An offer needs at least one augment.", nameof(offer));

        var picked = ctx.Picked;
        var session = _scorer.CreateSession(ctx);
        var pools = Enum.GetValues<AugmentTier>().ToDictionary(t => t, catalog.OfferableOfTier);
        var remaining = Math.Max(0, TotalSelections - picked.Count - 1);
        var firstTier = picked.Count > 0 ? picked[0].Tier : offer[0].Tier;
        var seed = StableSeed(picked.Concat(offer));

        // Every option replays the same random futures, so differences come from the card, not from luck.
        (double Expected, IReadOnlyList<AugmentInfo> Partners) Evaluate(AugmentInfo card, int runs) => remaining == 0
            ? (session.SetValue([.. picked, card]), [])
            : Simulate(card, picked, remaining, firstTier, session, pools, new Random(seed), runs);

        var ranked = offer
            .Select(augment =>
            {
                var reasons = new List<ScoreReason>();
                var now = _scorer.Value(augment, picked, ctx, reasons);
                var (expected, partners) = Evaluate(augment, simulations);
                return new AugmentOption(augment, now, expected, reasons, partners);
            })
            .OrderByDescending(o => o.Expected)
            .ThenByDescending(o => o.Now)
            .ToList();

        return new AugmentAdvice(ranked) { Reroll = AdviseRerolls(ranked, picked, pools, rerolled ?? new HashSet<AugmentInfo>(), card => Evaluate(card, _rerollSimulations).Expected, seed) };
    }

    /// <summary>
    /// Each offered card has its own reroll, and unused rerolls are lost when you pick. Rerolling a card you
    /// won't take can only help, so the real questions are which card to keep, and whether even that one is
    /// weak enough to reroll. That last reroll comes last: after the others, and only if it's still your best.
    /// </summary>
    private static RerollAdvice? AdviseRerolls(
        IReadOnlyList<AugmentOption> ranked, IReadOnlyList<AugmentInfo> picked, Dictionary<AugmentTier, IReadOnlyList<AugmentInfo>> pools,
        IReadOnlySet<AugmentInfo> rerolled, Func<AugmentInfo, double> value, int seed)
    {
        var canReroll = ranked.Select(o => !rerolled.Contains(o.Augment)).ToArray();
        if (!canReroll.Any(c => c))
            return null;

        // A reroll gives a different card of the same tier: not one on screen and not one you have.
        var shown = ranked.Select(o => o.Augment).ToHashSet();
        var pool = pools[ranked[0].Augment.Tier].Where(a => !shown.Contains(a) && !picked.Contains(a)).Select(value).ToArray();
        if (pool.Length == 0)
            return null;

        var values = ranked.Select(o => o.Expected).ToArray();
        var rerollKeeper = canReroll[0] && RerollingKeeperPays(values, canReroll, pool, new Random(seed ^ 0x5bd1e995));

        var cards = ranked.Select((o, i) => new CardReroll(
            o.Augment,
            i == 0 ? (rerollKeeper ? RerollAction.RerollLast : RerollAction.Keep)
                : canReroll[i] ? RerollAction.Reroll
                : RerollAction.AlreadyRerolled,
            pool.Count(v => v > values[i]) / (double)pool.Length)).ToList();

        return new RerollAdvice(cards, Describe(cards));
    }

    /// <summary>
    /// Simulates rerolling the other cards first. If a new card beats your keeper, rerolling the keeper costs
    /// nothing. If it's still your best, a reroll pays when it beats it on average. Advises the keeper reroll
    /// when, in most outcomes, it's either free or worth it.
    /// </summary>
    private static bool RerollingKeeperPays(double[] values, bool[] canReroll, double[] pool, Random rng)
    {
        var others = Enumerable.Range(1, values.Length - 1).ToList();
        var rerollable = others.Where(i => canReroll[i]).ToList();
        var fixedBest = others.Where(i => !canReroll[i]).Select(i => values[i]).DefaultIfEmpty(double.NegativeInfinity).Max();

        int stillBest = 0, pays = 0;
        var indices = Enumerable.Range(0, pool.Length).ToArray();
        for (var t = 0; t < RerollTrials; t++)
        {
            // Partial shuffle: the first k entries are the rerolled cards, the rest is what the keeper could become.
            var k = Math.Min(rerollable.Count, pool.Length - 1);
            for (var i = 0; i < k; i++)
            {
                var j = rng.Next(i, indices.Length);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }

            var secondBest = fixedBest;
            for (var i = 0; i < k; i++)
                secondBest = Math.Max(secondBest, pool[indices[i]]);
            if (secondBest >= values[0])
                continue; // a reroll already beat it; rerolling it costs nothing then

            stillBest++;
            var expected = 0.0;
            for (var i = k; i < indices.Length; i++)
                expected += Math.Max(pool[indices[i]], secondBest);
            if (expected / (indices.Length - k) > values[0])
                pays++;
        }
        var freeOrWorthIt = RerollTrials - stillBest + pays;
        return freeOrWorthIt * 2 >= RerollTrials;
    }

    private static string Describe(IReadOnlyList<CardReroll> cards)
    {
        var keeper = cards[0];
        var now = cards.Where(c => c.Action == RerollAction.Reroll).Select(c => c.Augment.Name).ToList();
        var chance = $"a reroll beats it {keeper.RerollBeatsIt:P0} of the time";

        if (keeper.Action == RerollAction.RerollLast)
            return now.Count > 0
                ? $"Reroll {Join(now)} first. If {keeper.Augment.Name} is still your best card after that, reroll it too: {chance}."
                : $"Reroll {keeper.Augment.Name}: {chance}.";

        return now.Count > 0
            ? $"Keep {keeper.Augment.Name}. Reroll {Join(now)}: you won't take {(now.Count == 1 ? "it" : "them")}, so a reroll can only help."
            : "";
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    private (double Expected, IReadOnlyList<AugmentInfo> Partners) Simulate(
        AugmentInfo option, IReadOnlyList<AugmentInfo> picked, int remaining, AugmentTier firstTier,
        AugmentScorer.Session session, Dictionary<AugmentTier, IReadOnlyList<AugmentInfo>> pools, Random rng, int runs)
    {
        var total = 0.0;
        var partnerCounts = new Dictionary<AugmentInfo, int>();
        var selectionIndex = picked.Count + 1;

        for (var s = 0; s < runs; s++)
        {
            var set = new List<AugmentInfo>(picked) { option };
            for (var j = 0; j < remaining; j++)
            {
                var tier = SampleTier(selectionIndex + j, firstTier, rng);
                var choice = PickFromOffer(pools[tier], set, session, rng);
                if (choice is null)
                    continue;
                set.Add(choice);
                if (session.Synergy(option, choice) >= 0.4)
                    partnerCounts[choice] = partnerCounts.GetValueOrDefault(choice) + 1;
            }
            total += session.SetValue(set);
        }

        var partners = partnerCounts
            .Where(kv => kv.Value >= PartnerThreshold)
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();
        return (total / runs, partners);
    }

    /// <summary>Three cards of one tier; keep the best, reroll the other two, take the best of what you see.</summary>
    private static AugmentInfo? PickFromOffer(IReadOnlyList<AugmentInfo> tierPool, List<AugmentInfo> set, AugmentScorer.Session session, Random rng)
    {
        var pool = tierPool.Where(a => !set.Contains(a)).ToList();
        if (pool.Count == 0)
            return null;

        var seen = Draw(pool, OfferSize, rng);
        var best = seen.MaxBy(a => session.Value(a, set))!;
        var rerolls = Draw(pool.Except(seen).ToList(), OfferSize - 1, rng);
        return rerolls.Append(best).MaxBy(a => session.Value(a, set));
    }

    /// <param name="selection">0-based selection number (0 = level 3, 3 = level 15).</param>
    internal static AugmentTier SampleTier(int selection, AugmentTier firstTier, Random rng)
    {
        // Tier odds aren't published; assume they're even. The second selection can't be Silver after a Silver first.
        var noSilver = selection == 1 && firstTier == AugmentTier.Silver;
        return noSilver
            ? (rng.Next(2) == 0 ? AugmentTier.Gold : AugmentTier.Prismatic)
            : (AugmentTier)rng.Next(3);
    }

    private static List<AugmentInfo> Draw(List<AugmentInfo> pool, int count, Random rng)
    {
        var copy = new List<AugmentInfo>(pool);
        var drawn = new List<AugmentInfo>(count);
        for (var i = 0; i < count && copy.Count > 0; i++)
        {
            var index = rng.Next(copy.Count);
            drawn.Add(copy[index]);
            copy.RemoveAt(index);
        }
        return drawn;
    }

    // Same cards, same answer: string.GetHashCode is randomized per process, so hash the names ourselves.
    private static int StableSeed(IEnumerable<AugmentInfo> augments)
    {
        var hash = 2166136261u;
        foreach (var c in string.Join("|", augments.Select(a => a.Name)))
            hash = (hash ^ c) * 16777619u;
        return (int)hash;
    }
}
