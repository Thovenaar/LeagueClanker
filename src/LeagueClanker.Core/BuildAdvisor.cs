using System.Runtime.CompilerServices;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core;

public enum AdvisorState
{
    WaitingForGame,
    Live,
}

/// <param name="Gold">Your gold right now. Updates also arrive when only the gold changed, for the "buy now" advice.</param>
public sealed record AdvisorUpdate(AdvisorState State, BuildRecommendation? Recommendation = null, double Gold = 0);

/// <summary>Polls the game and yields a new recommendation whenever champions or items change.</summary>
public sealed class BuildAdvisor(IGameDataSource source, StaticGameData data)
{
    private readonly RecommendationEngine _engine = new(data);

    /// <summary>Augments you picked (ARAM: Mayhem). Changing them triggers a new recommendation on the next poll.</summary>
    public IReadOnlyList<AugmentInfo> Augments { get; set; } = [];

    /// <summary>How you play your champion, from the playstyle picker. Null uses the champion's usual archetype.</summary>
    public Archetype? Playstyle { get; set; }

    /// <summary>op.gg's most played items for your champion, or empty. Changing them triggers a new recommendation.</summary>
    public IReadOnlySet<int> PopularItems { get; set; } = new HashSet<int>();

    /// <summary>op.gg's builds for your champion. The build then follows one of them, and so does your playstyle.</summary>
    public IReadOnlyList<MetaBuild> MetaBuilds { get; set; } = [];

    private string? _keepMeta;

    // Gold changes a little every second; buying advice only needs to follow it in steps.
    private const int GoldStep = 50;

    public async IAsyncEnumerable<AdvisorUpdate> RunAsync(TimeSpan interval, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? lastFingerprint = null;
        BuildRecommendation? last = null;
        var lastGoldStep = -1;
        using var timer = new PeriodicTimer(interval);

        do
        {
            var game = await source.TryGetAsync(ct);
            var augments = Augments;
            var playstyle = Playstyle;
            var popular = PopularItems;
            var analysis = game is null ? null : GameAnalyzer.Analyze(game, data, augments, playstyle, popular);

            if (analysis is null)
            {
                if (lastFingerprint != "")
                    yield return new AdvisorUpdate(AdvisorState.WaitingForGame);
                lastFingerprint = "";
                continue;
            }

            var gold = game!.ActivePlayer?.CurrentGold ?? 0;
            var metaBuilds = MetaBuilds;
            var fingerprint = Fingerprint(game) + "|" + string.Join(",", augments.Select(a => a.Name)) + "|" + playstyle + "|" + string.Join(",", popular.Order())
                              + "|" + string.Join(",", metaBuilds.Select(b => b.Key));
            if (fingerprint != lastFingerprint)
            {
                lastFingerprint = fingerprint;
                last = Recommend(game, analysis, augments, playstyle, popular, metaBuilds);
                lastGoldStep = (int)gold / GoldStep;
                yield return new AdvisorUpdate(AdvisorState.Live, last, gold);
            }
            else if ((int)gold / GoldStep != lastGoldStep)
            {
                // Same build, new gold: the same recommendation again, so the view only refreshes what to buy.
                lastGoldStep = (int)gold / GoldStep;
                yield return new AdvisorUpdate(AdvisorState.Live, last, gold);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    public BuildRecommendation? RecommendOnce(AllGameData game) =>
        GameAnalyzer.Analyze(game, data, Augments, Playstyle, PopularItems) is { } analysis
            ? Recommend(game, analysis, Augments, Playstyle, PopularItems, MetaBuilds)
            : null;

    /// <summary>
    /// Picks the meta build for this game, then analyzes the game again as that build's playstyle (on-hit Katarina),
    /// so augments and later updates see the style the build is for. A playstyle you picked yourself stays.
    /// </summary>
    private BuildRecommendation Recommend(AllGameData game, GameAnalysis analysis, IReadOnlyList<AugmentInfo> augments, Archetype? playstyle,
        IReadOnlySet<int> popular, IReadOnlyList<MetaBuild> metaBuilds)
    {
        var withMeta = analysis with { MetaBuilds = metaBuilds, ChosenPlaystyle = playstyle, KeepMeta = _keepMeta };
        var rec = _engine.Recommend(withMeta);
        if (playstyle is null && rec.Meta is { } meta && meta.Build.Style != analysis.Me.Archetype
            && GameAnalyzer.Analyze(game, data, augments, meta.Build.Style, popular) is { } restyled)
        {
            var again = _engine.Recommend(restyled with { MetaBuilds = metaBuilds, KeepMeta = _keepMeta, ForcedMeta = meta.Build.Key });
            rec = again with { Meta = again.Meta is { } m ? meta with { Swap = m.Swap } : meta };
        }
        _keepMeta = rec.Meta?.Build.Key;
        return rec;
    }

    // Recompute whenever something that moves stats or threat changes: items, levels, kills and deaths.
    // Gold ticks alone don't change the build.
    // The game's result counts too, so the recap sees the win or loss in the last seconds.
    private static string Fingerprint(AllGameData game) =>
        string.Join('|', game.AllPlayers.Select(p =>
            $"{p.ChampionName}:{p.Level}:{p.Scores.Kills}/{p.Scores.Deaths}:{string.Join(',', p.Items.Select(i => i.ItemID).Order())}"))
        + "|" + game.Events?.Events.LastOrDefault(e => e.EventName == "GameEnd")?.Result;
}
