using System.Globalization;
using System.Text.RegularExpressions;
using LeagueClanker.Core.Analysis;

namespace LeagueClanker.Core.StaticData;

/// <summary>What a passive's extra stats depend on.</summary>
public enum ScalingSource
{
    /// <summary>A fixed amount that builds up during the game (Rod of Ages, Yun Tal's crit). Counted fully stacked.</summary>
    Stacked,
    TotalAbilityPower,
    BonusHealth,
    BonusArmor,
    BonusMagicResist,
}

/// <summary>
/// Stats a passive adds on top of the item's own, like Rabadon's "Increases your total Ability Power by 30%"
/// or Riftmaker's "Gain 2% of your bonus Health as Ability Power". Read from the item's description.
/// </summary>
/// <param name="Amount">A fraction of the source (0.3), or the fully stacked amount for <see cref="ScalingSource.Stacked"/>.</param>
public sealed record ItemScaling(string Passive, string Stat, double Amount, ScalingSource Source)
{
    /// <summary>What it adds for you now, with this item's own stats counted in.</summary>
    public double Value(ItemInfo item, StatBlock? mine) => Source switch
    {
        ScalingSource.Stacked => Amount,
        ScalingSource.TotalAbilityPower => Amount * ((mine?.AbilityPower ?? 0) + item.Stat(StaticData.Stat.AbilityPower)),
        ScalingSource.BonusHealth => Amount * ((mine?.BonusHealth ?? 0) + item.Stat(StaticData.Stat.Health)),
        ScalingSource.BonusArmor => Amount * ((mine?.BonusArmor ?? 0) + item.Stat(StaticData.Stat.Armor)),
        ScalingSource.BonusMagicResist => Amount * ((mine?.BonusMagicResist ?? 0) + item.Stat(StaticData.Stat.MagicResist)),
        _ => 0,
    };

    /// <summary>"Magical Opus: +95 AP", or "Timeless: +30 AP when stacked".</summary>
    public string Note(ItemInfo item, StatBlock? mine) =>
        $"{Passive}: +{Value(item, mine):0} {ShortName(Stat)}{(Source == ScalingSource.Stacked ? " when stacked" : "")}";

    private static string ShortName(string stat) => stat switch
    {
        StaticData.Stat.AbilityPower => "AP",
        StaticData.Stat.AttackDamage => "AD",
        StaticData.Stat.MagicResist => "MR",
        StaticData.Stat.CritChance => "% crit",
        _ => stat.ToLowerInvariant(),
    };
}

public static partial class ItemScalingParser
{
    private static readonly Dictionary<string, string> StatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ability Power"] = Stat.AbilityPower,
        ["Attack Damage"] = Stat.AttackDamage,
        ["Health"] = Stat.Health,
        ["Mana"] = Stat.Mana,
        ["Armor"] = Stat.Armor,
        ["Magic Resist"] = Stat.MagicResist,
        ["Critical Strike Chance"] = Stat.CritChance,
    };

    /// <summary>Every passive in the description whose stats this understands. Others, like on-hit damage, are left out.</summary>
    public static IReadOnlyList<ItemScaling> Parse(string description)
    {
        var scaling = new List<ItemScaling>();
        foreach (Match section in PassiveSection().Matches(description))
        {
            var passive = Plain(section.Groups[1].Value).TrimEnd(':', ' ');
            var text = Plain(section.Groups[2].Value);

            // "Increases your total Ability Power by 30%."
            if (TotalAbilityPower().Match(text) is { Success: true } amp)
                scaling.Add(new(passive, Stat.AbilityPower, Percent(amp.Groups[1]), ScalingSource.TotalAbilityPower));

            // "Gain 2% of your bonus Health as Ability Power." and "Gain bonus Health equal to 12% of your Item Health."
            if (HealthConversion().Match(text) is { Success: true } conversion && StatNames.TryGetValue(conversion.Groups[2].Value, out var into))
                scaling.Add(new(passive, into, Percent(conversion.Groups[1]), ScalingSource.BonusHealth));
            if (HealthEqualTo().Match(text) is { Success: true } equal)
                scaling.Add(new(passive, Stat.Health, Percent(equal.Groups[1]), ScalingSource.BonusHealth));

            // "Increase your bonus Armor and Magic Resist by 30% until end of combat."
            if (BonusResists().Match(text) is { Success: true } resists)
            {
                var amount = Percent(resists.Groups[2]);
                if (resists.Groups[1].Value.Contains("Armor", StringComparison.OrdinalIgnoreCase))
                    scaling.Add(new(passive, Stat.Armor, amount, ScalingSource.BonusArmor));
                if (resists.Groups[1].Value.Contains("Magic Resist", StringComparison.OrdinalIgnoreCase))
                    scaling.Add(new(passive, Stat.MagicResist, amount, ScalingSource.BonusMagicResist));
            }

            // "This item gains 10 Health, 30 Mana and 3 Ability Power every 60 seconds up to 10 times."
            if (GrowsOverTime().Match(text) is { Success: true } grows)
            {
                var times = int.Parse(grows.Groups[2].Value, CultureInfo.InvariantCulture);
                foreach (Match gain in AmountOfStat().Matches(grows.Groups[1].Value))
                    if (StatNames.TryGetValue(gain.Groups[2].Value, out var stat))
                        scaling.Add(new(passive, stat, Number(gain.Groups[1]) * times, ScalingSource.Stacked));
            }

            // "On-Attack, gain Critical Strike Chance permanently up to 25%."
            if (PermanentlyUpTo().Match(text) is { Success: true } permanent && StatNames.TryGetValue(permanent.Groups[1].Value, out var stacked))
                scaling.Add(new(passive, stacked, Number(permanent.Groups[2]), ScalingSource.Stacked));
        }
        return scaling;
    }

    private static string Plain(string html) => Regex.Replace(Regex.Replace(html, "<br ?/?>", " "), "<[^>]+>", "").Trim();
    private static double Number(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);
    private static double Percent(Group group) => Number(group) / 100;

    // A passive's name and its text, up to the next passive, active or the end.
    [GeneratedRegex(@"<passive>(.*?)</passive>(.*?)(?=<passive>|<active>|<rules>|</mainText>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PassiveSection();

    [GeneratedRegex(@"increases? your total Ability Power by (\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase)]
    private static partial Regex TotalAbilityPower();

    [GeneratedRegex(@"gain (\d+(?:\.\d+)?)% of your bonus Health as (Ability Power|Attack Damage|Armor|Magic Resist)", RegexOptions.IgnoreCase)]
    private static partial Regex HealthConversion();

    [GeneratedRegex(@"gain bonus Health equal to (\d+(?:\.\d+)?)% of your (?:bonus|item) Health", RegexOptions.IgnoreCase)]
    private static partial Regex HealthEqualTo();

    [GeneratedRegex(@"increase your bonus (Armor(?: and Magic Resist)?|Magic Resist) by (\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase)]
    private static partial Regex BonusResists();

    [GeneratedRegex(@"gains? (.+?) every \d+ seconds,? up to (\d+) times", RegexOptions.IgnoreCase)]
    private static partial Regex GrowsOverTime();

    [GeneratedRegex(@"(\d+(?:\.\d+)?) (Ability Power|Attack Damage|Health|Mana|Armor|Magic Resist)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountOfStat();

    [GeneratedRegex(@"gain (Critical Strike Chance|Ability Power|Attack Damage|Armor|Magic Resist|Health) permanently,? up to (\d+(?:\.\d+)?)%?", RegexOptions.IgnoreCase)]
    private static partial Regex PermanentlyUpTo();
}
