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

    /// <summary>Its rerolls are used up.</summary>
    AlreadyRerolled,

    /// <summary>It has the golden reroll and you won't take it: use it, the card becomes one tier higher.</summary>
    GoldenReroll,

    /// <summary>Your best card has the golden reroll, and a card one tier higher usually beats it.</summary>
    GoldenRerollLast,
}

/// <summary>What rerolls the current offer still has.</summary>
public sealed record RerollState
{
    /// <summary>Rerolls already spent on the slot each offered card is in (the card is the result of those rerolls).</summary>
    public IReadOnlyDictionary<AugmentInfo, int> Used { get; init; } = new Dictionary<AugmentInfo, int>();

    /// <summary>The offered card whose reroll is golden: it rerolls into a card one tier higher.</summary>
    public AugmentInfo? Golden { get; init; }

    /// <summary>1 normally; 2 in the selection after "Stats on Stats on Stats!".</summary>
    public int RerollsPerCard { get; init; } = 1;

    public int RerollsLeft(AugmentInfo card) => Math.Max(0, RerollsPerCard - Used.GetValueOrDefault(card));

    public static RerollState FromRerolled(IEnumerable<AugmentInfo> rerolled) => new() { Used = rerolled.ToDictionary(a => a, _ => int.MaxValue / 2) };
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
/// <param name="augmentSet">Mayhem never offers Silver twice in a row at the start; Arena has no such rule.</param>
public sealed class AugmentAdvisor(AugmentCatalog catalog, AugmentScorer? scorer = null, int simulations = 1500, AugmentSet augmentSet = AugmentSet.Mayhem)
{
    private const int TotalSelections = 4;
    private const int OfferSize = 3;
    private const int PartnerThreshold = 1; // how often a card must be picked alongside to count as a partner
    private const int RerollTrials = 2000;

    // Every card a reroll could produce gets its own simulation; fewer runs each keeps that affordable.
    private readonly int _rerollSimulations = Math.Max(50, simulations / 5);

    private readonly AugmentScorer _scorer = scorer ?? new AugmentScorer();

    /// <param name="rerolled">Offered cards that were already rerolled and can't be rerolled again.</param>
    public AugmentAdvice Rank(IReadOnlyList<AugmentInfo> offer, AugmentContext ctx, IReadOnlySet<AugmentInfo> rerolled) =>
        Rank(offer, ctx, RerollState.FromRerolled(rerolled));

    public AugmentAdvice Rank(IReadOnlyList<AugmentInfo> offer, AugmentContext ctx, RerollState? rerolls = null)
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

        return new AugmentAdvice(ranked) { Reroll = AdviseRerolls(ranked, picked, pools, rerolls ?? new RerollState(), card => Evaluate(card, _rerollSimulations).Expected, seed) };
    }

    /// <summary>One offered card and the rerolls its slot has left.</summary>
    private sealed record Slot(AugmentInfo Augment, double Value, int Left, bool Golden);

    /// <summary>
    /// Each offered card has its own reroll, and unused rerolls are lost when you pick. Rerolling a card you
    /// won't take can only help, so the real questions are which card to keep, and whether even that one is
    /// weak enough to reroll. That last reroll comes last: after the others, and only if it's still your best.
    /// A golden reroll works the same, except it draws from the next tier.
    /// </summary>
    private static RerollAdvice? AdviseRerolls(
        IReadOnlyList<AugmentOption> ranked, IReadOnlyList<AugmentInfo> picked, Dictionary<AugmentTier, IReadOnlyList<AugmentInfo>> pools,
        RerollState state, Func<AugmentInfo, double> value, int seed)
    {
        // A golden reroll may already have lifted one card a tier; the offer's own tier is the lowest one.
        var tier = ranked.Min(o => o.Augment.Tier);
        var slots = ranked.Select(o => new Slot(
            o.Augment,
            o.Expected,
            state.RerollsLeft(o.Augment),
            o.Augment == state.Golden && tier < AugmentTier.Prismatic && state.RerollsLeft(o.Augment) > 0)).ToArray();
        if (slots.All(s => s.Left == 0))
            return null;

        // A reroll gives a card not on screen and not one you have.
        var shown = ranked.Select(o => o.Augment).ToHashSet();
        double[] Pool(AugmentTier t) => pools[t].Where(a => !shown.Contains(a) && !picked.Contains(a)).Select(value).ToArray();
        var sameTier = Pool(tier);
        var nextTier = slots.Any(s => s.Golden) ? Pool(tier + 1) : [];
        double[] PoolOf(Slot slot) => slot.Golden ? nextTier : sameTier;
        if (slots.Where(s => s.Left > 0).All(s => PoolOf(s).Length == 0))
            return null;

        var keeper = slots[0];
        var rerollKeeper = keeper.Left > 0 && PoolOf(keeper).Length > 0 && RerollingKeeperPays(slots, PoolOf, new Random(seed ^ 0x5bd1e995));

        var cards = slots.Select((slot, i) =>
        {
            var pool = PoolOf(slot);
            var action = i == 0
                ? rerollKeeper ? (slot.Golden ? RerollAction.GoldenRerollLast : RerollAction.RerollLast) : RerollAction.Keep
                : slot.Left == 0 || pool.Length == 0 ? RerollAction.AlreadyRerolled
                : slot.Golden ? RerollAction.GoldenReroll
                : RerollAction.Reroll;
            return new CardReroll(slot.Augment, action, pool.Length == 0 ? 0 : pool.Count(v => v > slot.Value) / (double)pool.Length);
        }).ToList();

        return new RerollAdvice(cards, Describe(cards, state.RerollsPerCard, tier + 1));
    }

    /// <summary>
    /// Simulates rerolling the other cards first. If a new card beats your keeper, rerolling the keeper costs
    /// nothing. If it's still your best, a reroll pays when it beats it on average. Advises the keeper reroll
    /// when, in most outcomes, it's either free or worth it.
    /// </summary>
    private static bool RerollingKeeperPays(Slot[] slots, Func<Slot, double[]> poolOf, Random rng)
    {
        var keeper = slots[0];
        var keeperPool = poolOf(keeper);
        int stillBest = 0, pays = 0;

        for (var t = 0; t < RerollTrials; t++)
        {
            var secondBest = double.NegativeInfinity;
            foreach (var slot in slots.Skip(1))
            {
                var pool = poolOf(slot);
                if (slot.Left == 0 || pool.Length == 0)
                {
                    secondBest = Math.Max(secondBest, slot.Value);
                    continue;
                }
                // You keep rerolling a card you won't take; what matters is the best card it turns into.
                for (var r = 0; r < slot.Left; r++)
                    secondBest = Math.Max(secondBest, pool[rng.Next(pool.Length)]);
            }

            if (secondBest >= keeper.Value)
                continue; // a reroll already beat it; rerolling it costs nothing then

            stillBest++;
            var expected = 0.0;
            foreach (var card in keeperPool)
                expected += Math.Max(card, secondBest);
            if (expected / keeperPool.Length > keeper.Value)
                pays++;
        }

        var freeOrWorthIt = RerollTrials - stillBest + pays;
        return freeOrWorthIt * 2 >= RerollTrials;
    }

    private static string Describe(IReadOnlyList<CardReroll> cards, int rerollsPerCard, AugmentTier nextTier)
    {
        var keeper = cards[0];
        var golden = cards.Where(c => c.Action == RerollAction.GoldenReroll).Select(c => c.Augment.Name).ToList();
        var normal = cards.Where(c => c.Action == RerollAction.Reroll).Select(c => c.Augment.Name).ToList();
        var chance = $"a {(keeper.Action == RerollAction.GoldenRerollLast ? "golden " : "")}reroll beats it {keeper.RerollBeatsIt:P0} of the time";

        var steps = new List<string>();
        if (golden.Count > 0)
            steps.Add($"golden-reroll {Join(golden)} (it becomes a {nextTier} card)");
        if (normal.Count > 0)
            steps.Add($"reroll {Join(normal)}{(rerollsPerCard > 1 ? $" (up to {rerollsPerCard} times each)" : "")}");
        var first = steps.Count > 0 ? Capitalize(string.Join(" and ", steps)) : "";

        if (keeper.Action is RerollAction.RerollLast or RerollAction.GoldenRerollLast)
        {
            var verb = keeper.Action == RerollAction.GoldenRerollLast ? "golden-reroll" : "reroll";
            return first.Length > 0
                ? $"{first} first. If {keeper.Augment.Name} is still your best card after that, {verb} it too: {chance}."
                : $"{Capitalize(verb)} {keeper.Augment.Name}: {chance}.";
        }

        var count = golden.Count + normal.Count;
        return count > 0
            ? $"Keep {keeper.Augment.Name}. {first}: you won't take {(count == 1 ? "it" : "them")}, so a reroll can only help."
            : "";
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

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
                var tier = SampleTier(selectionIndex + j, firstTier, rng, augmentSet);
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

    /// <param name="selection">0-based selection number (0 = level 3, 3 = level 15 in Mayhem).</param>
    internal static AugmentTier SampleTier(int selection, AugmentTier firstTier, Random rng, AugmentSet set = AugmentSet.Mayhem)
    {
        // Tier odds aren't published; assume they're even. In Mayhem the second selection can't be Silver after a Silver first.
        var noSilver = set == AugmentSet.Mayhem && selection == 1 && firstTier == AugmentTier.Silver;
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
