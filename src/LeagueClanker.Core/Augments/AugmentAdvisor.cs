namespace LeagueClanker.Core.Augments;

/// <param name="Now">What the card adds to the cards you already have.</param>
/// <param name="Expected">Average value of your final set of augments if you take this card, over simulated future offers.</param>
/// <param name="Partners">Cards you can still be offered that combine well with this one.</param>
public sealed record AugmentOption(
    AugmentInfo Augment, double Now, double Expected, IReadOnlyList<ScoreReason> Reasons, IReadOnlyList<AugmentInfo> Partners);

public sealed record AugmentAdvice(IReadOnlyList<AugmentOption> Ranked)
{
    public AugmentOption Best => Ranked[0];

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
/// Ranks an augment offer by the best final set it leads to, not just by the card itself.
/// Future ARAM: Mayhem selections are simulated with the real rules: same tier for all three offerings,
/// random tier per selection (first two never both Silver), and one reroll per offering.
/// </summary>
public sealed class AugmentAdvisor(AugmentCatalog catalog, AugmentScorer? scorer = null, int simulations = 1500)
{
    private const int TotalSelections = 4;
    private const int OfferSize = 3;
    private const int PartnerThreshold = 1; // how often a card must be picked alongside to count as a partner

    private readonly AugmentScorer _scorer = scorer ?? new AugmentScorer();

    public AugmentAdvice Rank(IReadOnlyList<AugmentInfo> offer, AugmentContext ctx)
    {
        if (offer.Count == 0)
            throw new ArgumentException("An offer needs at least one augment.", nameof(offer));

        var picked = ctx.Picked;
        var remaining = Math.Max(0, TotalSelections - picked.Count - 1);
        var firstTier = picked.Count > 0 ? picked[0].Tier : offer[0].Tier;
        var seed = StableSeed(picked.Concat(offer));

        var options = offer.Select(augment =>
        {
            var reasons = new List<ScoreReason>();
            var now = _scorer.Value(augment, picked, ctx, reasons);
            // Every option replays the same random futures, so differences come from the card, not from luck.
            var (expected, partners) = remaining == 0
                ? (_scorer.SetValue([.. picked, augment], ctx), [])
                : Simulate(augment, picked, remaining, firstTier, ctx, new Random(seed));
            return new AugmentOption(augment, now, expected, reasons, partners);
        });

        return new AugmentAdvice(options.OrderByDescending(o => o.Expected).ThenByDescending(o => o.Now).ToList());
    }

    private (double Expected, IReadOnlyList<AugmentInfo> Partners) Simulate(
        AugmentInfo option, IReadOnlyList<AugmentInfo> picked, int remaining, AugmentTier firstTier, AugmentContext ctx, Random rng)
    {
        var total = 0.0;
        var partnerCounts = new Dictionary<AugmentInfo, int>();
        var selectionIndex = picked.Count + 1;

        for (var s = 0; s < simulations; s++)
        {
            var set = new List<AugmentInfo>(picked) { option };
            for (var j = 0; j < remaining; j++)
            {
                var tier = SampleTier(selectionIndex + j, firstTier, rng);
                var choice = PickFromOffer(tier, set, ctx, rng);
                if (choice is null)
                    continue;
                set.Add(choice);
                if (_scorer.Synergy(option, choice, ctx, null) >= 0.4)
                    partnerCounts[choice] = partnerCounts.GetValueOrDefault(choice) + 1;
            }
            total += _scorer.SetValue(set, ctx);
        }

        var partners = partnerCounts
            .Where(kv => kv.Value >= PartnerThreshold)
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();
        return (total / simulations, partners);
    }

    /// <summary>Three cards of one tier; keep the best, reroll the other two, take the best of what you see.</summary>
    private AugmentInfo? PickFromOffer(AugmentTier tier, List<AugmentInfo> set, AugmentContext ctx, Random rng)
    {
        var pool = catalog.OfferableOfTier(tier).Where(a => !set.Contains(a)).ToList();
        if (pool.Count == 0)
            return null;

        var seen = Draw(pool, OfferSize, rng);
        var best = seen.MaxBy(a => _scorer.Value(a, set, ctx))!;
        var rerolls = Draw(pool.Except(seen).ToList(), OfferSize - 1, rng);
        return rerolls.Append(best).MaxBy(a => _scorer.Value(a, set, ctx));
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
