using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

/// <summary>
/// Which way an item takes your build, for the "other options" under your plan: "Vs AP" when a game situation
/// wants it, otherwise its main stats ("Tankier", "More AP").
/// </summary>
public static class ItemDirections
{
    private static readonly (string Direction, string[] Stats)[] ByStats =
    [
        ("Tankier", [Stat.Health, Stat.Armor, Stat.MagicResist]),
        ("More AP", [Stat.AbilityPower, Stat.MagicPen, Stat.MagicPenPercent]),
        ("More AD", [Stat.AttackDamage, Stat.Lethality, Stat.ArmorPenPercent]),
        ("Attack speed", [Stat.AttackSpeed, Stat.CritChance]),
        ("Ability haste", [Stat.AbilityHaste]),
    ];

    public static string Of(ScoredItem item)
    {
        if (item.Reasons.FirstOrDefault() is { } reason)
            return char.ToUpperInvariant(reason.Situation.Label[0]) + reason.Situation.Label[1..];
        return ByStats.MaxBy(d => d.Stats.Sum(s => StatScale.Normalize(s, item.Item.Stat(s)))).Direction;
    }

    /// <summary>The best item for each direction your plan doesn't cover yet, strongest first.</summary>
    /// <param name="shown">Items already in your plan.</param>
    public static IReadOnlyList<(string Direction, ScoredItem Item)> Alternatives(BuildRecommendation rec, IEnumerable<int> shown, int count = 3)
    {
        var skip = shown.ToHashSet();
        return rec.Ranked
            .Where(s => !skip.Contains(s.Item.Id))
            .Select(s => (Direction: Of(s), Item: s))
            .DistinctBy(x => x.Direction)
            .Take(count)
            .ToList();
    }
}
