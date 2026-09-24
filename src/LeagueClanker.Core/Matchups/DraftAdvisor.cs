using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Matchups;

public sealed record BanSuggestion(ChampionInfo Champion, string Reason);

/// <param name="Me">Your pick or hover. Null before either.</param>
public sealed record DraftRequest(ChampionInfo? Me, bool IsLocked, Position Position, GameMode Mode, IReadOnlyList<ChampionInfo> Allies, IReadOnlyList<ChampionInfo> Enemies)
{
    /// <summary>True while your ban is still to come. Ban suggestions only show then.</summary>
    public bool HasPendingBan { get; init; }

    /// <summary>Banned champions and your teammates' hovers: don't suggest those.</summary>
    public IReadOnlySet<int> Unavailable { get; init; } = new HashSet<int>();

    /// <summary>Champions you can pick right now. Null when unknown.</summary>
    public IReadOnlySet<int>? Pickable { get; init; }

    public IReadOnlyDictionary<int, int> Mastery { get; init; } = new Dictionary<int, int>();
}

/// <param name="FillHeader">"PICKS THAT ADD AP", or null without fill picks.</param>
public sealed record DraftReport(IReadOnlyList<BanSuggestion> Bans, IReadOnlyList<string> Warnings, IReadOnlyList<CounterPick> FillPicks, string? FillHeader)
{
    /// <summary>"Enemy so far: 70% AP, 2 tanks, heavy crowd control". Null before they have two picks.</summary>
    public string? EnemySummary { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// The draft around your pick. During bans it lists champions worth banning for your role. Once your team has picks,
/// it warns about gaps (all one damage type, no frontline, no crowd control) and, before you lock in, lists picks in
/// your role that fill the gap.
/// </summary>
public sealed class DraftAdvisor(IMatchupData data, ChampionCatalog champions)
{
    private const int MaxSuggestions = 5;
    private const double MinRoleRate = 0.25;
    private const double OneSidedDamage = 0.8;
    private const double TeamLosesTo = 0.49;

    private enum Gap { None, Frontline, Magic, Physical, CrowdControl }

    public async Task<DraftReport?> AnalyzeAsync(DraftRequest request, CancellationToken ct = default)
    {
        if (request.Mode != GameMode.SummonersRift)
            return null;

        IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>>? stats = null;
        string? note = null;
        try
        {
            stats = await data.GetRoleStatsAsync(ct);
        }
        catch (Exception ex) when (MatchupAdvisor.IsUnreachable(ex, ct))
        {
            Log.Error("op.gg role stats", ex);
            note = "op.gg didn't answer, so there are no ban or pick suggestions right now.";
        }

        var bans = request.HasPendingBan && request.Position != Position.None && stats is not null
            ? await BansAsync(request, stats, ct)
            : [];

        var team = request.Allies.Concat(request.Me is null ? [] : [request.Me]).ToList();
        var (warnings, gap) = CheckTeam(team);
        var fill = !request.IsLocked && gap != Gap.None && request.Position != Position.None && stats is not null
            ? FillPicks(request, stats, gap)
            : [];

        return new DraftReport(bans, warnings, fill, fill.Count == 0 ? null : FillHeader(gap))
        {
            EnemySummary = Summary(request.Enemies),
            Note = note,
        };
    }

    // With a champion in mind: the opponents it loses to most. Without one: the strongest champions in your role.
    private async Task<IReadOnlyList<BanSuggestion>> BansAsync(
        DraftRequest request, IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>> stats, CancellationToken ct)
    {
        bool Available(int key) => !request.Unavailable.Contains(key) && key != request.Me?.Key && !request.Enemies.Any(e => e.Key == key);

        if (request.Me is { Key: > 0 } me)
        {
            try
            {
                var threats = (await data.GetMatchupsAsync(me.Key, request.Position, ct))
                    .Where(m => m.Games >= MatchupAdvisor.MinMatchupGames && m.WinRate <= TeamLosesTo && Available(m.OpponentKey))
                    .OrderBy(m => m.WinRate)
                    .Select(m => champions.GetByKey(m.OpponentKey) is { } c
                        ? new BanSuggestion(c, $"Beats {me.Name}: you win {m.WinRate:P1} over {m.Games:N0} games.")
                        : null)
                    .OfType<BanSuggestion>()
                    .Take(MaxSuggestions)
                    .ToList();
                if (threats.Count > 0)
                    return threats;
            }
            catch (Exception ex) when (MatchupAdvisor.IsUnreachable(ex, ct))
            {
                Log.Error("op.gg matchups for bans", ex);
            }
        }

        var role = request.Position.DisplayName().ToLowerInvariant();
        return InRole(stats, request.Position)
            .Where(c => Available(c.Key))
            .Take(MaxSuggestions)
            .Select(c => champions.GetByKey(c.Key) is { } champion
                ? new BanSuggestion(champion, $"Tier {c.Stats.Tier} in {role}: {c.Stats.WinRate:P1} win rate, banned in {c.Stats.BanRate:P0} of games.")
                : null)
            .OfType<BanSuggestion>()
            .ToList();
    }

    private static (IReadOnlyList<string> Warnings, Gap Gap) CheckTeam(IReadOnlyList<ChampionInfo> team)
    {
        var warnings = new List<string>();
        var gap = Gap.None;
        if (team.Count < 2)
            return (warnings, gap);

        var profiles = team.Select(c => (Champion: c, Archetype: ArchetypeClassifier.Classify(c))).ToList();
        if (team.Count >= 3 && !profiles.Any(p => p.Archetype.IsFrontline()))
        {
            warnings.Add("No frontline yet: nobody on your team is a tank or bruiser.");
            gap = Gap.Frontline;
        }

        var magic = MagicShare(profiles);
        if (team.Count >= 3 && magic <= 1 - OneSidedDamage)
        {
            warnings.Add($"Your team is {1 - magic:P0} AD, so enemies only need armor. An AP pick fixes that.");
            if (gap == Gap.None)
                gap = Gap.Magic;
        }
        else if (team.Count >= 3 && magic >= OneSidedDamage)
        {
            warnings.Add($"Your team is {magic:P0} AP, so enemies only need magic resist. An AD pick fixes that.");
            if (gap == Gap.None)
                gap = Gap.Physical;
        }

        if (team.Count >= 4 && !profiles.Any(p => ChampionKnowledge.HeavyCrowdControl.Contains(p.Champion.Id)))
        {
            warnings.Add("Little crowd control: nobody to lock enemies down.");
            if (gap == Gap.None)
                gap = Gap.CrowdControl;
        }
        return (warnings, gap);
    }

    private IReadOnlyList<CounterPick> FillPicks(DraftRequest request, IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>> stats, Gap gap)
    {
        bool Fills(ChampionInfo c)
        {
            var archetype = ArchetypeClassifier.Classify(c);
            return gap switch
            {
                Gap.Frontline => archetype.IsFrontline(),
                Gap.Magic => GameAnalyzer.EstimateMagicShare(c, archetype, []) >= 0.6,
                Gap.Physical => GameAnalyzer.EstimateMagicShare(c, archetype, []) <= 0.4,
                Gap.CrowdControl => ChampionKnowledge.HeavyCrowdControl.Contains(c.Id),
                _ => false,
            };
        }

        var taken = request.Unavailable.Concat(request.Enemies.Select(e => e.Key)).Concat(request.Allies.Select(a => a.Key)).ToHashSet();
        return InRole(stats, request.Position)
            .Where(c => !taken.Contains(c.Key) && (request.Pickable is null || request.Pickable.Contains(c.Key)))
            .Select(c => (Champion: champions.GetByKey(c.Key), c.Stats))
            .Where(c => c.Champion is not null && Fills(c.Champion))
            .Select(c => new CounterPick(c.Champion!, c.Stats.WinRate, c.Stats.Games, request.Mastery.GetValueOrDefault(c.Champion!.Key)))
            .OrderByDescending(c => c.YouPlayIt)
            .Take(MaxSuggestions)
            .ToList();
    }

    // Champions played in the role, strongest first by op.gg's tier and rank.
    private static IEnumerable<(int Key, OpggRoleStats Stats)> InRole(
        IReadOnlyDictionary<int, IReadOnlyDictionary<Position, OpggRoleStats>> stats, Position role) =>
        stats.Where(c => c.Value.TryGetValue(role, out var s) && s.RoleRate >= MinRoleRate)
            .Select(c => (c.Key, Stats: c.Value[role]))
            .OrderBy(c => c.Stats.Tier)
            .ThenBy(c => c.Stats.Rank);

    private static string FillHeader(Gap gap) => gap switch
    {
        Gap.Frontline => "PICKS THAT ADD A FRONTLINE",
        Gap.Magic => "PICKS THAT ADD AP",
        Gap.Physical => "PICKS THAT ADD AD",
        _ => "PICKS THAT ADD CROWD CONTROL",
    };

    private static string? Summary(IReadOnlyList<ChampionInfo> enemies)
    {
        if (enemies.Count < 2)
            return null;
        var profiles = enemies.Select(c => (Champion: c, Archetype: ArchetypeClassifier.Classify(c))).ToList();
        var parts = new List<string> { $"{MagicShare(profiles):P0} AP" };
        var tanks = profiles.Count(p => p.Archetype == Archetype.Tank);
        if (tanks > 0)
            parts.Add(tanks == 1 ? "1 tank" : $"{tanks} tanks");
        var crowdControl = profiles.Count(p => ChampionKnowledge.HeavyCrowdControl.Contains(p.Champion.Id));
        if (crowdControl >= 2)
            parts.Add("heavy crowd control");
        return $"Enemy so far: {string.Join(", ", parts)}.";
    }

    // Tanks and enchanters deal less of a team's damage, like PlayerProfile.DamageWeight in game.
    private static double MagicShare(IReadOnlyList<(ChampionInfo Champion, Archetype Archetype)> team)
    {
        double Weight(Archetype a) => a switch { Archetype.Tank => 0.5, Archetype.Enchanter => 0.4, _ => 1.0 };
        var total = team.Sum(p => Weight(p.Archetype));
        return total == 0 ? 0.5 : team.Sum(p => GameAnalyzer.EstimateMagicShare(p.Champion, p.Archetype, []) * Weight(p.Archetype)) / total;
    }
}
