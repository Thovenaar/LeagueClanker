using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

/// <summary>A proposed change to the accepted build: new items move into your next purchases.</summary>
/// <param name="Add">Items that come into your next purchases.</param>
/// <param name="Drop">Items that leave your build.</param>
/// <param name="Reasons">Why, as situation sentences. New situations since you accepted your build are marked.</param>
/// <param name="Gain">How much better the new next purchases score than the accepted ones.</param>
public sealed record Pivot(IReadOnlyList<ItemInfo> Add, IReadOnlyList<ItemInfo> Drop, IReadOnlyList<string> Reasons, double Gain)
{
    /// <summary>Items that stay in your build, but behind the new ones.</summary>
    public IReadOnlyList<ItemInfo> Later { get; init; } = [];

    internal IReadOnlyList<string> SituationLabels { get; init; } = [];

    /// <summary>The plan accepting gives. Worked out up front, so accepting does exactly what the summary says.</summary>
    internal IReadOnlyList<ItemInfo> Plan { get; init; } = [];

    public string Summary => Drop.Count > 0 ? $"Swap {Names(Drop)} for {Names(Add)}"
        : Later.Count > 0 ? $"Buy {Names(Add)} before {Names(Later)}"
        : $"Add {Names(Add)} to your next items";

    private static string Names(IEnumerable<ItemInfo> items) => string.Join(" + ", items.Select(i => i.Name));
}

/// <summary>
/// Keeps the build the player accepted and compares every new recommendation against it.
/// A pivot is suggested only when your next purchases would change for a real in-game reason
/// and score clearly better; score noise from kills or levels doesn't count.
/// A declined pivot stays quiet for the rest of the game, but <see cref="SwitchToLatest"/> is always available.
/// </summary>
public sealed class BuildPlanner
{
    public const int PlanLength = 6;

    /// <summary>Only changes to this many upcoming purchases can trigger a pivot, fewer when fewer slots are left.</summary>
    public const int NearTerm = 3;

    /// <summary>How much the new next purchases must outscore the accepted ones.</summary>
    public const double MinGain = 0.5;

    /// <summary>An item entering your next purchases needs at least this much situational bonus to justify a pivot.</summary>
    public const double MinReasonPoints = 0.5;

    /// <summary>
    /// Owning parts worth this share of an item's cost means you've started it: it goes first, and pivots don't drop it.
    /// A Needlessly Large Rod starts Rabadon's Deathcap (34%); an Amplifying Tome starts nothing (11%).
    /// </summary>
    public const double StartedShare = 0.25;

    /// <summary>How much an item must outscore the one ahead of it in your plan to move ahead.</summary>
    public const double ReorderMargin = 0.5;

    private readonly HashSet<int> _declined = [];

    // The last pivot you accepted, so the same swap is never suggested again right after.
    private string? _acceptedPivot;
    private List<ItemInfo> _plan = [];
    private HashSet<string> _planSituations = [];
    private string? _championId;

    public BuildRecommendation? Latest { get; private set; }

    /// <summary>A pivot waiting for the player to accept or decline, or null.</summary>
    public Pivot? PendingPivot { get; private set; }

    /// <summary>The accepted build minus what you already own, scored against the current game.</summary>
    public IReadOnlyList<ScoredItem> Upcoming { get; private set; } = [];

    /// <summary>The latest recommendation's next purchases differ from the plan, whether or not a pivot is being suggested.</summary>
    public bool CanSwitch { get; private set; }

    /// <summary>Items the latest recommendation wants next that aren't in your next purchases.</summary>
    public IReadOnlyList<ItemInfo> LatestDifferences { get; private set; } = [];

    /// <summary>Recipes, to tell which items you've started. Without it, parts you own don't change the order.</summary>
    public ItemCatalog? Items { get; set; }

    public void Update(BuildRecommendation latest)
    {
        if (_championId != latest.Game.Me.Champion.Id)
        {
            Reset();
            _championId = latest.Game.Me.Champion.Id;
            Latest = latest;
            Adopt(latest);
            return;
        }

        Latest = latest;
        Refresh(latest);
    }

    /// <summary>Apply the suggested swap. The rest of your build stays, ordered by the latest ranking.</summary>
    public void Accept()
    {
        if (PendingPivot is not { } pivot || Latest is not { } latest)
            return;

        _acceptedPivot = Key(pivot);
        _plan = [.. pivot.Plan];
        _planSituations.UnionWith(pivot.SituationLabels);
        Refresh(latest);
    }

    /// <summary>Keep your build. The items this pivot wanted won't trigger another suggestion this game.</summary>
    public void Decline()
    {
        if (PendingPivot is null)
            return;
        _declined.UnionWith(PendingPivot.Add.Select(i => i.Id));
        PendingPivot = null;
    }

    /// <summary>Adopt the latest recommendation now, whether or not a pivot was suggested or declined.</summary>
    public void SwitchToLatest()
    {
        if (Latest is not null)
            Adopt(Latest);
    }

    /// <summary>Forget everything, e.g. when the game ends.</summary>
    public void Reset()
    {
        _declined.Clear();
        _acceptedPivot = null;
        _plan = [];
        _planSituations = [];
        _championId = null;
        Latest = null;
        PendingPivot = null;
        Upcoming = [];
        CanSwitch = false;
        LatestDifferences = [];
    }

    private void Adopt(BuildRecommendation recommendation)
    {
        _plan = FinishStartedFirst(recommendation.Ranked.Take(PlanLength).Select(s => s.Item).ToList(), recommendation);
        Upcoming = _plan.Select(i => recommendation.Find(i.Id)!).ToList();
        _planSituations = recommendation.Situations.Select(s => s.Label).ToHashSet();
        PendingPivot = null;
        CanSwitch = false;
        LatestDifferences = [];
    }

    private void Refresh(BuildRecommendation latest)
    {
        // Bought items and items that stopped being candidates (e.g. a unique passive you now have) leave quietly.
        _plan = _plan.Where(i => latest.Find(i.Id) is not null).ToList();

        // Top up from the latest ranking when the plan runs short. Extending the plan isn't a pivot.
        foreach (var next in latest.Ranked.Select(s => s.Item))
        {
            if (_plan.Count >= PlanLength)
                break;
            if (_plan.All(i => i.Id != next.Id))
                _plan.Add(next);
        }

        // What you buy next only changes through pivots, but the order among those items follows the game:
        // a new augment can make your third item the one to buy first. Promoting a later item into your
        // next purchases is a pivot, so the next few and the rest are ordered separately.
        // With an op.gg build, its order is the buy order players use, so the ranking's order decides, not item scores.
        var score = latest.Meta is null
            ? latest.Ranked.ToDictionary(s => s.Item.Id, s => s.Total)
            : latest.Ranked.Select((s, index) => (s.Item.Id, Score: -index * 10.0)).ToDictionary(x => x.Id, x => x.Score);
        var near = Near(latest);
        _plan = [.. Reorder(_plan.Take(near).ToList(), score), .. Reorder(_plan.Skip(near).ToList(), score)];

        _plan = FinishStartedFirst(_plan, latest);
        Upcoming = _plan.Select(i => latest.Find(i.Id)!).ToList();
        PendingPivot = DetectPivot(latest);
    }

    /// <summary>
    /// Sorts by score, but an item only moves ahead of another when it leads by <see cref="ReorderMargin"/>. Some scores
    /// move with your stats (Rabadon's with your AP), and two close items would otherwise swap on every purchase.
    /// </summary>
    private static List<ItemInfo> Reorder(List<ItemInfo> items, IReadOnlyDictionary<int, double> score)
    {
        var sorted = new List<ItemInfo>();
        foreach (var item in items)
        {
            var at = sorted.Count;
            while (at > 0 && score[item.Id] >= score[sorted[at - 1].Id] + ReorderMargin)
                at--;
            sorted.Insert(at, item);
        }
        return sorted;
    }

    // Finish what you started: an item whose parts you already own a good share of beats a better item from scratch.
    private List<ItemInfo> FinishStartedFirst(List<ItemInfo> plan, BuildRecommendation latest) =>
        plan.Where(i => Started(i, latest)).MaxBy(i => Progress(i, latest)) is { } started
            ? [started, .. plan.Where(i => i.Id != started.Id)]
            : plan;

    private double Progress(ItemInfo item, BuildRecommendation latest) =>
        Items is null || item.TotalGold <= 0 ? 0 : 1 - (double)BuyAdvisor.RemainingCost(item, latest.Game.Me.Items, Items) / item.TotalGold;

    private bool Started(ItemInfo item, BuildRecommendation latest) => Progress(item, latest) >= StartedShare;

    // Your next purchases: three, or fewer when that's all the room you have. A swap further down would change
    // nothing you can see, so accepting it looked like it did nothing.
    private static int Near(BuildRecommendation latest) => Math.Min(NearTerm, latest.SlotsLeft);

    private Pivot? DetectPivot(BuildRecommendation latest)
    {
        var near = Near(latest);
        var planNext = Upcoming.Take(near).ToList();
        // Compare with the latest ranking in the order the plan uses: an item you've started goes first in both.
        var latestNext = FinishStartedFirst(latest.Ranked.Select(s => s.Item).ToList(), latest)
            .Take(near)
            .Select(i => latest.Find(i.Id)!)
            .ToList();
        var planIds = planNext.Select(s => s.Item.Id).ToHashSet();
        var latestIds = latestNext.Select(s => s.Item.Id).ToHashSet();

        var added = latestNext.Where(s => !planIds.Contains(s.Item.Id)).ToList();
        LatestDifferences = added.Select(s => s.Item).ToList();
        CanSwitch = added.Count > 0;

        // Only items with a real in-game reason that you haven't already turned down. With an op.gg build, only its own
        // items: once most of it is bought, filler items fill the next slots, and swapping between fillers isn't worth asking.
        var fresh = added
            .Where(s => !_declined.Contains(s.Item.Id) && s.Reasons.Any(r => r.Points >= MinReasonPoints))
            .Where(s => latest.Meta is not { } meta || meta.Includes(s.Item))
            .ToList();
        if (fresh.Count == 0)
            return null;

        // Each new item pushes out the weakest of your next purchases that the latest ranking no longer has up front.
        var dropped = planNext
            .Where(s => !latestIds.Contains(s.Item.Id) && !Started(s.Item, latest))
            .OrderBy(s => s.Total)
            .Take(fresh.Count)
            .ToList();
        var gain = fresh.Sum(s => s.Total) - dropped.Sum(s => s.Total);
        if (gain < MinGain)
            return null;

        // Work out the plan accepting gives and describe that. "Add Blade of the Ruined King" when it was already fourth
        // only moved it up one place, so accepting looked like it did nothing; now it says what it pushes back.
        // Only the pivot's own items move: the new ones join your next purchases in the latest ranking's order, what
        // no longer fits there goes right behind them, and the rest of your plan keeps its order.
        var rank = latest.Ranked.Select((s, index) => (s.Item.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var freshIds = fresh.Select(s => s.Item.Id).ToHashSet();
        var next = planNext.Select(s => s.Item)
            .Where(i => dropped.All(d => d.Item.Id != i.Id))
            .Concat(fresh.Select(s => s.Item))
            .OrderBy(i => rank.GetValueOrDefault(i.Id, int.MaxValue))
            .ToList();
        var plan = FinishStartedFirst([.. next, .. _plan.Skip(near).Where(i => !freshIds.Contains(i.Id))], latest).Take(PlanLength).ToList();
        var shownAfter = plan.Take(near).ToList();
        var entering = shownAfter.Where(i => !planIds.Contains(i.Id)).ToList();
        if (entering.Count == 0)
            return null; // a started item would push it straight back out: nothing you see would change
        var pushedBack = planNext.Select(s => s.Item)
            .Where(i => shownAfter.All(a => a.Id != i.Id) && dropped.All(d => d.Item.Id != i.Id))
            .ToList();

        var situations = fresh
            .SelectMany(s => s.Reasons.Where(r => r.Points >= MinReasonPoints))
            .Select(r => r.Situation)
            .DistinctBy(s => s.Label)
            .ToList();
        var reasons = situations
            .Select(s => _planSituations.Contains(s.Label) ? s.Description : $"{s.Description} (new)")
            .ToList();

        var pivot = new Pivot(entering, dropped.Select(s => s.Item).ToList(), reasons, gain)
        {
            Later = pushedBack,
            SituationLabels = situations.Select(s => s.Label).ToList(),
            Plan = plan,
        };
        return Key(pivot) == _acceptedPivot ? null : pivot;
    }

    private static string Key(Pivot pivot) =>
        $"{string.Join(",", pivot.Add.Select(i => i.Id).Order())}|{string.Join(",", pivot.Drop.Select(i => i.Id).Order())}";
}
