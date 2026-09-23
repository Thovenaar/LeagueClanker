using System.Runtime.CompilerServices;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core;

public enum AdvisorState
{
    WaitingForGame,
    Live,
}

public sealed record AdvisorUpdate(AdvisorState State, BuildRecommendation? Recommendation = null);

/// <summary>Polls the game and yields a new recommendation whenever champions or items change.</summary>
public sealed class BuildAdvisor(IGameDataSource source, StaticGameData data)
{
    private readonly RecommendationEngine _engine = new(data);

    public async IAsyncEnumerable<AdvisorUpdate> RunAsync(TimeSpan interval, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? lastFingerprint = null;
        using var timer = new PeriodicTimer(interval);

        do
        {
            var game = await source.TryGetAsync(ct);
            var analysis = game is null ? null : GameAnalyzer.Analyze(game, data);

            if (analysis is null)
            {
                if (lastFingerprint != "")
                    yield return new AdvisorUpdate(AdvisorState.WaitingForGame);
                lastFingerprint = "";
                continue;
            }

            var fingerprint = Fingerprint(game!);
            if (fingerprint != lastFingerprint)
            {
                lastFingerprint = fingerprint;
                yield return new AdvisorUpdate(AdvisorState.Live, _engine.Recommend(analysis));
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    public BuildRecommendation? RecommendOnce(AllGameData game) =>
        GameAnalyzer.Analyze(game, data) is { } analysis ? _engine.Recommend(analysis) : null;

    // Recompute whenever something that moves stats or threat changes: items, levels, kills and deaths.
    // Gold ticks alone don't change the build.
    private static string Fingerprint(AllGameData game) =>
        string.Join('|', game.AllPlayers.Select(p =>
            $"{p.ChampionName}:{p.Level}:{p.Scores.Kills}/{p.Scores.Deaths}:{string.Join(',', p.Items.Select(i => i.ItemID).Order())}"));
}
