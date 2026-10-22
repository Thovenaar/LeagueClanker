using System.Text.Json;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Augments;

/// <summary>
/// Where the wiki is behind Riot. Riot's own card list (Community Dragon's cherry-augments.json) has the tier the game
/// uses, so a wiki tier that differs is replaced: on patch 16.21 the wiki had BONK! as Silver, Riot and arammayhem.com
/// as Gold. Cards Riot added that the wiki doesn't describe yet come from a short hand-kept list, written from the
/// game's own text; the wiki's entry takes over once it has one.
/// </summary>
public static class AugmentSupplement
{
    private const int FirstMayhemId = 1000;

    private static readonly (AugmentSet Set, string Name, AugmentTier Tier, string Description)[] Missing =
    [
        (AugmentSet.Arena, "Death Dealer", AugmentTier.Prismatic,
            "Replace Flee with Death Dealer. Activate: you and the target champion enter the Death Realm for a few seconds, and you turn invisible for part of it. At max level, slaying your target in the Death Realm grants you a revive and delays theirs."),
        (AugmentSet.Arena, "Got That Dog In Em", AugmentTier.Prismatic,
            "Every few seconds a hot dog spawns nearby. Anyone can pick it up to breathe fire, dealing magic damage per second to enemies. If you die while breathing fire, you explode, dealing magic damage per stack."),
    ];

    /// <summary>
    /// Each card's tier in Riot's list, keyed by <see cref="AugmentCatalog.Key"/>. Names Riot lists in more than one
    /// tier of the same set are left out, since those can't tell which one the wiki means.
    /// </summary>
    public static IReadOnlyDictionary<string, AugmentTier> RiotTiers(string englishJson, AugmentSet set)
    {
        using var doc = JsonDocument.Parse(englishJson);
        return doc.RootElement.EnumerateArray()
            .Where(a => a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                && (id.GetInt32() >= FirstMayhemId) == (set == AugmentSet.Mayhem))
            .Select(a => (Key: AugmentCatalog.Key(a.GetStringOrEmpty("nameTRA")), Rarity: a.GetStringOrEmpty("rarity").TrimStart('k')))
            .Where(a => a.Key.Length > 0 && Enum.TryParse<AugmentTier>(a.Rarity, out _))
            .GroupBy(a => a.Key)
            .Where(g => g.Select(a => a.Rarity).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => Enum.Parse<AugmentTier>(g.First().Rarity));
    }

    /// <summary>The wiki's cards with Riot's tiers where they differ, plus the hand-kept cards the wiki lacks.</summary>
    public static AugmentCatalog Apply(AugmentCatalog wiki, AugmentSet set, IReadOnlyDictionary<string, AugmentTier>? riotTiers, ItemCatalog? items = null)
    {
        var cards = wiki.All
            .Select(a => riotTiers is not null && riotTiers.TryGetValue(AugmentCatalog.Key(a.Name), out var tier) && tier != a.Tier ? a with { Tier = tier } : a)
            .ToList();
        cards.AddRange(Missing
            .Where(m => m.Set == set && wiki.Find(m.Name) is null)
            .Select(m => AugmentTagger.Tag(m.Name, new Dictionary<string, object?> { ["description"] = m.Description, ["tier"] = m.Tier.ToString() }, items)));
        return new AugmentCatalog(cards);
    }
}
