namespace LeagueClanker.Core;

public enum GameMode
{
    SummonersRift,
    Aram,
    AramMayhem,

    /// <summary>Summoner's Rift with the old (2014-era) item shop, champions and stats.</summary>
    LeagueClassic,

    /// <summary>ARAM: Mayhem with League Classic's items and stats ("KIWI_JADE").</summary>
    MayhemClassic,

    /// <summary>2v2v2v2 rounds with augments and its own item shop ("CHERRY").</summary>
    Arena,

    /// <summary>Modes the advisor doesn't understand yet. Summoner's Rift items are used as a best effort.</summary>
    Unsupported,
}

/// <summary>Which copies of the items a mode's shop sells. See <see cref="StaticData.ItemCatalog"/>.</summary>
public enum ItemVariant
{
    Standard,
    Classic,
    Arena,

    /// <summary>Copies for modes the advisor doesn't use. Never recommended.</summary>
    Other,
}

/// <summary>Which augment list a mode offers.</summary>
public enum AugmentSet
{
    Mayhem,
    Arena,
}

public static class GameModes
{
    public const int SummonersRiftMap = 11;
    public const int HowlingAbyssMap = 12;
    public const int ArenaMap = 30;

    /// <summary>Data Dragon has no name for map 453, but it sells exactly the League Classic items.</summary>
    public const int LeagueClassicMap = 453;

    /// <summary>Levels at which ARAM: Mayhem offers an augment selection.</summary>
    public static readonly IReadOnlyList<int> MayhemAugmentLevels = [3, 7, 11, 15];

    /// <param name="gameMode">
    /// The Live Client API's gameData.gameMode. Mayhem uses its internal codename "KIWI", Arena "CHERRY", and League
    /// Classic's items carry the codename "Jade" (Data Dragon lists spells for "JADE" and "KIWI_JADE").
    /// </param>
    public static GameMode Detect(string? gameMode, int mapNumber)
    {
        if (mapNumber == LeagueClassicMap)
            return GameMode.LeagueClassic;

        return gameMode?.ToUpperInvariant() switch
        {
            "CLASSIC" or "SWIFTPLAY" => GameMode.SummonersRift,
            "ARAM" => GameMode.Aram,
            "KIWI" => GameMode.AramMayhem,
            "KIWI_JADE" => GameMode.MayhemClassic,
            "JADE" or "LEAGUECLASSIC" => GameMode.LeagueClassic,
            "CHERRY" => GameMode.Arena,
            null or "" => mapNumber switch { HowlingAbyssMap => GameMode.Aram, ArenaMap => GameMode.Arena, _ => GameMode.SummonersRift },
            _ => GameMode.Unsupported,
        };
    }

    /// <summary>The Data Dragon map id whose item pool this mode uses.</summary>
    public static int MapId(this GameMode mode) => mode switch
    {
        GameMode.Aram or GameMode.AramMayhem or GameMode.MayhemClassic => HowlingAbyssMap,
        GameMode.LeagueClassic => LeagueClassicMap,
        GameMode.Arena => ArenaMap,
        _ => SummonersRiftMap,
    };

    public static AugmentSet? Augments(this GameMode mode) => mode switch
    {
        GameMode.AramMayhem or GameMode.MayhemClassic => AugmentSet.Mayhem,
        GameMode.Arena => AugmentSet.Arena,
        _ => null,
    };

    public static bool HasAugments(this GameMode mode) => mode.Augments() is not null;

    /// <summary>League Classic's shop, champions' stat growth and armor, in League Classic and its Mayhem variant.</summary>
    public static bool UsesClassicItems(this GameMode mode) => mode is GameMode.LeagueClassic or GameMode.MayhemClassic;

    public static ItemVariant ItemVariant(this GameMode mode) => mode switch
    {
        GameMode.LeagueClassic or GameMode.MayhemClassic => Core.ItemVariant.Classic,
        GameMode.Arena => Core.ItemVariant.Arena,
        _ => Core.ItemVariant.Standard,
    };

    /// <summary>Modes with lanes and roles. Matchups, bans and the team comp check only make sense there.</summary>
    public static bool HasLanes(this GameMode mode) => mode == GameMode.SummonersRift;

    /// <summary>The client's name for the mode, as Data Dragon lists it for summoner spells.</summary>
    public static string ClientModeName(this GameMode mode) => mode switch
    {
        GameMode.Aram => "ARAM",
        GameMode.AramMayhem => "KIWI",
        GameMode.MayhemClassic => "KIWI_JADE",
        GameMode.LeagueClassic => "JADE",
        GameMode.Arena => "CHERRY",
        _ => "CLASSIC",
    };

    public static string DisplayName(this GameMode mode) => mode switch
    {
        GameMode.SummonersRift => "Summoner's Rift",
        GameMode.Aram => "ARAM",
        GameMode.AramMayhem => "ARAM: Mayhem",
        GameMode.LeagueClassic => "League Classic",
        GameMode.MayhemClassic => "ARAM: Mayhem Classic",
        GameMode.Arena => "Arena",
        _ => "Unsupported mode",
    };
}
