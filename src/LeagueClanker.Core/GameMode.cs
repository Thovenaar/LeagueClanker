namespace LeagueClanker.Core;

public enum GameMode
{
    SummonersRift,
    Aram,
    AramMayhem,

    /// <summary>Summoner's Rift with the old (2014-era) item shop, champions and stats.</summary>
    LeagueClassic,

    /// <summary>Modes the advisor doesn't understand yet, e.g. Arena. Summoner's Rift items are used as a best effort.</summary>
    Unsupported,
}

public static class GameModes
{
    public const int SummonersRiftMap = 11;
    public const int HowlingAbyssMap = 12;

    /// <summary>Data Dragon has no name for map 453, but it sells exactly the League Classic items.</summary>
    public const int LeagueClassicMap = 453;

    /// <summary>Levels at which ARAM: Mayhem offers an augment selection.</summary>
    public static readonly IReadOnlyList<int> MayhemAugmentLevels = [3, 7, 11, 15];

    /// <param name="gameMode">
    /// The Live Client API's gameData.gameMode. Mayhem uses its internal codename "KIWI". League Classic's codename is "Jade"
    /// (its item texts use &lt;jadeUnique&gt;), but the mode string itself hasn't been seen in a real game yet.
    /// </param>
    public static GameMode Detect(string? gameMode, int mapNumber)
    {
        if (mapNumber == LeagueClassicMap)
            return GameMode.LeagueClassic;

        return gameMode?.ToUpperInvariant() switch
        {
            "CLASSIC" => GameMode.SummonersRift,
            "ARAM" => GameMode.Aram,
            "KIWI" => GameMode.AramMayhem,
            "JADE" or "LEAGUECLASSIC" => GameMode.LeagueClassic,
            null or "" => mapNumber == HowlingAbyssMap ? GameMode.Aram : GameMode.SummonersRift,
            _ => GameMode.Unsupported,
        };
    }

    /// <summary>The Data Dragon map id whose item pool this mode uses.</summary>
    public static int MapId(this GameMode mode) => mode switch
    {
        GameMode.Aram or GameMode.AramMayhem => HowlingAbyssMap,
        GameMode.LeagueClassic => LeagueClassicMap,
        _ => SummonersRiftMap,
    };

    public static bool HasAugments(this GameMode mode) => mode == GameMode.AramMayhem;

    /// <summary>League Classic uses its own item copies (77xxxx ids); every other mode uses the standard items.</summary>
    public static bool UsesClassicItems(this GameMode mode) => mode == GameMode.LeagueClassic;

    public static string DisplayName(this GameMode mode) => mode switch
    {
        GameMode.SummonersRift => "Summoner's Rift",
        GameMode.Aram => "ARAM",
        GameMode.AramMayhem => "ARAM: Mayhem",
        GameMode.LeagueClassic => "League Classic",
        _ => "Unsupported mode",
    };
}
