using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Matchups;

/// <param name="Likelihood">Share of this champion's games in that role, 0-1.</param>
public sealed record RoleGuess(ChampionInfo Champion, Position Position, double Likelihood);

/// <summary>
/// The client doesn't tell you the enemy roles, so they're guessed: each visible enemy gets a different role, and the
/// guess picks the combination their play rates make most likely. Garen goes top, and Lux support if their Syndra is
/// already mid.
/// </summary>
public static class RoleGuesser
{
    private static readonly Position[] Roles = [Position.Top, Position.Jungle, Position.Middle, Position.Bottom, Position.Support];

    // A role the champion is never played in still gets a sliver, so an odd pick doesn't break the guess.
    private const double MinRate = 0.005;

    /// <param name="rates">Share of games per role, by champion key. Champions without rates use a guess from their class.</param>
    public static IReadOnlyList<RoleGuess> Guess(
        IReadOnlyList<ChampionInfo> champions, IReadOnlyDictionary<int, IReadOnlyDictionary<Position, double>> rates)
    {
        var picks = champions.Take(Roles.Length).ToList();
        if (picks.Count == 0)
            return [];

        var table = picks.Select(c => rates.TryGetValue(c.Key, out var known) && known.Count > 0 ? known : ClassRates(c)).ToList();
        double Rate(int champion, Position role) => Math.Max(MinRate, table[champion].GetValueOrDefault(role));

        // At most 5 champions and 5 roles: 120 combinations, so try them all.
        Position[]? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var assignment in Permutations(Roles, picks.Count))
        {
            var score = assignment.Select((role, i) => Math.Log(Rate(i, role))).Sum();
            if (score > bestScore)
            {
                bestScore = score;
                best = assignment;
            }
        }

        return picks.Select((c, i) => new RoleGuess(c, best![i], table[i].GetValueOrDefault(best[i]))).ToList();
    }

    // Offline fallback: roughly where each class is played.
    private static IReadOnlyDictionary<Position, double> ClassRates(ChampionInfo champion) => ArchetypeClassifier.Classify(champion) switch
    {
        Archetype.Marksman => Rates((Position.Bottom, 0.85), (Position.Middle, 0.1)),
        Archetype.OnHit => Rates((Position.Bottom, 0.5), (Position.Top, 0.3), (Position.Middle, 0.2)),
        Archetype.Enchanter => Rates((Position.Support, 0.9)),
        Archetype.Mage => Rates((Position.Middle, 0.65), (Position.Support, 0.25)),
        Archetype.ApAssassin => Rates((Position.Middle, 0.6), (Position.Jungle, 0.3)),
        Archetype.AdAssassin => Rates((Position.Middle, 0.5), (Position.Jungle, 0.4)),
        Archetype.Bruiser => Rates((Position.Top, 0.6), (Position.Jungle, 0.35)),
        Archetype.ApBruiser => Rates((Position.Top, 0.5), (Position.Jungle, 0.3), (Position.Middle, 0.2)),
        Archetype.Tank => Rates((Position.Top, 0.35), (Position.Support, 0.35), (Position.Jungle, 0.3)),
        _ => Rates(),
    };

    private static Dictionary<Position, double> Rates(params (Position Role, double Rate)[] rates) => rates.ToDictionary(r => r.Role, r => r.Rate);

    private static IEnumerable<Position[]> Permutations(Position[] roles, int length)
    {
        if (length == 0)
        {
            yield return [];
            yield break;
        }
        foreach (var role in roles)
            foreach (var rest in Permutations(roles.Where(r => r != role).ToArray(), length - 1))
                yield return [role, .. rest];
    }
}
