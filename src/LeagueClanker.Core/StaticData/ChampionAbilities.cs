using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeagueClanker.Core.StaticData;

/// <summary>What a champion does that changes what to build against them.</summary>
[Flags]
public enum ChampionTraits
{
    None = 0,
    Healer = 1,
    Shielder = 2,
    HeavyCrowdControl = 4,
    TrueDamage = 8,
}

/// <summary>
/// Which of a champion's abilities heal, shield, hard crowd control or deal true damage, as slot letters
/// ("Q", "W", "E", "R", "P" for the passive). Read from the markup in Data Dragon's ability tooltips.
/// </summary>
public sealed record AbilityProfile(string ChampionId, string Healing, string Shielding, string HardCrowdControl, string TrueDamage)
{
    /// <summary>
    /// The traits the abilities clearly show. The thresholds are strict, because the hand-kept lists in
    /// <c>ChampionKnowledge</c> already cover most champions well, and a small heal on one ability doesn't make a healer.
    /// </summary>
    public ChampionTraits Traits =>
        (Healing.Length >= ChampionAbilities.MinHealing ? ChampionTraits.Healer : 0)
        | (Shielding.Length >= ChampionAbilities.MinShielding ? ChampionTraits.Shielder : 0)
        | (HardCrowdControl.Length >= ChampionAbilities.MinHardCrowdControl ? ChampionTraits.HeavyCrowdControl : 0)
        | (TrueDamage.Length >= ChampionAbilities.MinTrueDamage ? ChampionTraits.TrueDamage : 0);
}

/// <summary>Reads Data Dragon's championFull.json into an <see cref="AbilityProfile"/> per champion.</summary>
public static class ChampionAbilities
{
    /// <summary>Abilities needed for each trait.</summary>
    public const int MinHealing = 2, MinShielding = 2, MinHardCrowdControl = 3, MinTrueDamage = 1;

    // Stuns, knock-ups, roots, suppression and so on. Slows and silences don't count: they don't stop you moving.
    private static readonly Regex HardCrowdControlWords = new(
        @"\b(stun\w*|knock\w*\s+(up|back|aside|away)|knocked\s+(up|back)|airborne|root\w*|snare\w*|suppress\w*|charm\w*|fear\w*|flee\w*|taunt\w*|sleep\w*|asleep|polymorph\w*|pull\w*|grounded|immobiliz\w*|stasis)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HealingWords = new(@"\b(heal(s|ing)?|restor(es|ing))\b[^.]{0,40}\bhealth\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // True damage that doesn't hit champions in a fight: to monsters or minions, to yourself, or after dying.
    private static readonly Regex NotAgainstChampions = new(@"monster|minion|suffers|after dying", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Sentence = new(@"(?<=[.!?])\s+|<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Status = new(@"<status>(.*?)</status>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Markup = new(@"<[^>]+>", RegexOptions.Compiled);

    public static IReadOnlyList<AbilityProfile> Parse(string championFullJson)
    {
        using var doc = JsonDocument.Parse(championFullJson);
        return doc.RootElement.GetProperty("data").EnumerateObject().Select(entry => Profile(entry.Value)).ToList();
    }

    private static AbilityProfile Profile(JsonElement champion)
    {
        // The passive has only a description. Spells have a tooltip with markup like <healing> and <status>Stuns</status>.
        var abilities = new List<(char Slot, string Tooltip, string Description)>();
        if (champion.TryGetProperty("spells", out var spells))
            abilities.AddRange(spells.EnumerateArray().Take(4).Select((s, i) => ("QWER"[i], s.GetStringOrEmpty("tooltip"), s.GetStringOrEmpty("description"))));
        if (champion.TryGetProperty("passive", out var passive))
            abilities.Add(('P', "", passive.GetStringOrEmpty("description")));

        string Slots(Func<string, string, bool> test) =>
            new(abilities.Where(a => test(a.Tooltip, Markup.Replace(a.Description, " "))).Select(a => a.Slot).ToArray());

        return new AbilityProfile(
            champion.GetStringOrEmpty("id"),
            Healing: Slots((tip, text) => tip.Contains("<healing>", StringComparison.OrdinalIgnoreCase) || HealingWords.IsMatch(text)),
            // Only the markup: descriptions name things like "Shield of Daybreak" that aren't shields.
            Shielding: Slots((tip, _) => tip.Contains("<shield>", StringComparison.OrdinalIgnoreCase)),
            // Tooltips can split one status over tags ("<status>Knocks</status> <status>Back</status>"), so join them.
            HardCrowdControl: Slots((tip, text) => HardCrowdControlWords.IsMatch(string.Join(' ', Status.Matches(tip).Select(m => m.Groups[1].Value)))
                                                   || HardCrowdControlWords.IsMatch(text)),
            TrueDamage: Slots((tip, text) => DealsTrueDamage(tip) || DealsTrueDamage(text)));
    }

    private static bool DealsTrueDamage(string text) =>
        Sentence.Split(text).Any(s => Markup.Replace(s, "").Contains("true damage", StringComparison.OrdinalIgnoreCase) && !NotAgainstChampions.IsMatch(s));
}
