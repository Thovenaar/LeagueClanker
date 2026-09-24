using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Runes;

public enum Position
{
    None,
    Top,
    Jungle,
    Middle,
    Bottom,
    Support,
}

public static class Positions
{
    /// <summary>
    /// Reads the League client's "top", "jungle", "middle", "bottom", "utility" (any case), the live game's "UTILITY",
    /// and the short forms "mid", "adc" and "support". Anything else, like "FILL" or "", is <see cref="Position.None"/>.
    /// </summary>
    public static Position Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "TOP" => Position.Top,
        "JUNGLE" => Position.Jungle,
        "MIDDLE" or "MID" => Position.Middle,
        "BOTTOM" or "BOT" or "ADC" => Position.Bottom,
        "UTILITY" or "SUPPORT" => Position.Support,
        _ => Position.None,
    };

    public static string DisplayName(this Position position) => position switch
    {
        Position.Middle => "Mid",
        Position.None => "No role",
        _ => position.ToString(),
    };
}

/// <summary>
/// How you want to play your champion: AP Ezreal, AD Thresh and tank Leona are all possible. The playstyle picks the
/// rune page and the item build. It uses the same categories as <see cref="Archetype"/>.
/// </summary>
public static class Playstyles
{
    public static IReadOnlyList<Archetype> All { get; } = Enum.GetValues<Archetype>();

    /// <summary>
    /// The playstyle to preselect: how the champion is normally played, which is what the item advisor assumes anyway.
    /// In support, a fighter who can tank (Taric, Braum) goes tank.
    /// </summary>
    public static Archetype Default(ChampionInfo champion, Position position)
    {
        var natural = ArchetypeClassifier.Classify(champion);
        if (position == Position.Support && natural is Archetype.Bruiser or Archetype.ApBruiser && champion.Tags.Contains("Tank"))
            return Archetype.Tank;
        return natural;
    }
}
