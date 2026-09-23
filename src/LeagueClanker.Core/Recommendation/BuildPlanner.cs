using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

/// <summary>A proposed change to the accepted build: new items move into your next purchases.</summary>
/// <param name="Reasons">Why, as situation sentences. New situations since you accepted your build are marked.</param>
/// <param name="Gain">How much better the new next purchases score than the accepted ones.</param>
public sealed record Pivot(IReadOnlyList<ItemInfo> Add, IReadOnlyList<ItemInfo> Drop, IReadOnlyList<string> Reasons, double Gain)
{
    internal IReadOnlyList<string> SituationLabels { get; init; } = [];

    public string Summary => Drop.Count == 0
        ? $"Add {Names(Add)} to your next items"
        : $"Swap {Names(Drop)} for {Names(Add)}";

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

    /// <summary>Only changes to this many upcoming purchases can trigger a pivot.</summary>
    public const int NearTerm = 3;

    /// <summary>How much the new next purchases must outscore the accepted ones.</summary>
    public const double MinGain = 0.5;

    /// <summary>An item entering your next purchases needs at least this much situational bonus to justify a pivot.</summary>
    public const double MinReasonPoints = 0.5;

    private readonly HashSet<int> _declined = [];
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

        var dropped = pivot.Drop.Select(i => i.Id).ToHashSet();
        var rank = latest.Ranked.Select((s, index) => (s.Item.Id, index)).ToDictionary(x => x.Id, x => x.index);
        _plan = _plan.Where(i => !dropped.Contains(i.Id))
            .Concat(pivot.Add)
            .DistinctBy(i => i.Id)
            .OrderBy(i => rank.GetValueOrDefault(i.Id, int.MaxValue))
            .Take(PlanLength)
            .ToList();
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
        Upcoming = recommendation.Ranked.Take(PlanLength).ToList();
        _plan = Upcoming.Select(s => s.Item).ToList();
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

        Upcoming = _plan.Select(i => latest.Find(i.Id)!).ToList();
        PendingPivot = DetectPivot(latest);
    }

    private Pivot? DetectPivot(BuildRecommendation latest)
    {
        var planNext = Upcoming.Take(NearTerm).ToList();
        var latestNext = latest.Ranked.Take(NearTerm).ToList();
        var planIds = planNext.Select(s => s.Item.Id).ToHashSet();
        var latestIds = latestNext.Select(s => s.Item.Id).ToHashSet();

        var added = latestNext.Where(s => !planIds.Contains(s.Item.Id)).ToList();
        LatestDifferences = added.Select(s => s.Item).ToList();
        CanSwitch = added.Count > 0;

        // Only items with a real in-game reason that you haven't already turned down.
        var fresh = added
            .Where(s => !_declined.Contains(s.Item.Id) && s.Reasons.Any(r => r.Points >= MinReasonPoints))
            .ToList();
        if (fresh.Count == 0)
            return null;

        // Each new item pushes out the weakest of your next purchases that the latest ranking no longer has up front.
        var dropped = planNext
            .Where(s => !latestIds.Contains(s.Item.Id))
            .OrderBy(s => s.Total)
            .Take(fresh.Count)
            .ToList();
        var gain = fresh.Sum(s => s.Total) - dropped.Sum(s => s.Total);
        if (gain < MinGain)
            return null;

        var situations = fresh
            .SelectMany(s => s.Reasons.Where(r => r.Points >= MinReasonPoints))
            .Select(r => r.Situation)
            .DistinctBy(s => s.Label)
            .ToList();
        var reasons = situations
            .Select(s => _planSituations.Contains(s.Label) ? s.Description : $"{s.Description} (new)")
            .ToList();

        return new Pivot(fresh.Select(s => s.Item).ToList(), dropped.Select(s => s.Item).ToList(), reasons, gain)
        {
            SituationLabels = situations.Select(s => s.Label).ToList(),
        };
    }
}
