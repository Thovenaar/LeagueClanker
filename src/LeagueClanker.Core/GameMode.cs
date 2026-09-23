namespace LeagueClanker.Core;

public enum GameMode
{
    SummonersRift,
    Aram,
    AramMayhem,

    /// <summary>Modes the advisor doesn't understand yet, e.g. Arena. Summoner's Rift items are used as a best effort.</summary>
    Unsupported,
}

public static class GameModes
{
    public const int SummonersRiftMap = 11;
    public const int HowlingAbyssMap = 12;

    /// <summary>Levels at which ARAM: Mayhem offers an augment selection.</summary>
    public static readonly IReadOnlyList<int> MayhemAugmentLevels = [3, 7, 11, 15];

    /// <param name="gameMode">The Live Client API's gameData.gameMode. Mayhem uses its internal codename "KIWI".</param>
    public static GameMode Detect(string? gameMode, int mapNumber) => gameMode?.ToUpperInvariant() switch
    {
        "CLASSIC" => GameMode.SummonersRift,
        "ARAM" => GameMode.Aram,
        "KIWI" => GameMode.AramMayhem,
        null or "" => mapNumber == HowlingAbyssMap ? GameMode.Aram : GameMode.SummonersRift,
        _ => GameMode.Unsupported,
    };

    /// <summary>The Data Dragon map id whose item pool this mode uses.</summary>
    public static int MapId(this GameMode mode) => mode is GameMode.Aram or GameMode.AramMayhem ? HowlingAbyssMap : SummonersRiftMap;

    public static bool HasAugments(this GameMode mode) => mode == GameMode.AramMayhem;

    public static string DisplayName(this GameMode mode) => mode switch
    {
        GameMode.SummonersRift => "Summoner's Rift",
        GameMode.Aram => "ARAM",
        GameMode.AramMayhem => "ARAM: Mayhem",
        _ => "Unsupported mode",
    };
}
