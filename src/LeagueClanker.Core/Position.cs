namespace LeagueClanker.Core;

/// <summary>A role on Summoner's Rift. <see cref="None"/> when there are no roles (ARAM) or none is known.</summary>
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
