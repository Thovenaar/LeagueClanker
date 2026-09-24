namespace LeagueClanker.Core.LiveClient;

// Shape of https://127.0.0.1:2999/liveclientdata/allgamedata (Riot Live Client Data API).
// Only the fields the advisor uses are mapped; everything else is ignored on deserialization.

public sealed class AllGameData
{
    public ActivePlayer? ActivePlayer { get; init; }
    public List<LivePlayer> AllPlayers { get; init; } = [];
    public LiveGameInfo? GameData { get; init; }
    public LiveEvents? Events { get; init; }
}

public sealed class LiveEvents
{
    // The API really does nest a list called "Events" in an object called "events".
    public List<LiveEvent> Events { get; init; } = [];
}

public sealed class LiveEvent
{
    /// <summary>"GameStart", "ChampionKill", "GameEnd", ...</summary>
    public string? EventName { get; init; }

    /// <summary>On "GameEnd": "Win" or "Lose", from your side.</summary>
    public string? Result { get; init; }
}

public sealed class ActivePlayer
{
    public string? RiotId { get; init; }
    public string? SummonerName { get; init; }
    public int Level { get; init; }
    public double CurrentGold { get; init; }

    /// <summary>Your real stats, including runes and buffs. Other players' stats aren't exposed.</summary>
    public LiveChampionStats? ChampionStats { get; init; }
}

/// <summary>
/// Only fields with unambiguous units are mapped. Percentage fields (crit, life steal, penetration)
/// use inconsistent scales across game versions, so the advisor estimates those from items instead.
/// </summary>
public sealed class LiveChampionStats
{
    public double MaxHealth { get; init; }
    public double Armor { get; init; }
    public double MagicResist { get; init; }
    public double AttackDamage { get; init; }
    public double AbilityPower { get; init; }
    public double AttackSpeed { get; init; }
    public double MoveSpeed { get; init; }
    public double AttackRange { get; init; }
    public double PhysicalLethality { get; init; }
    public double MagicPenetrationFlat { get; init; }
    public double? AbilityHaste { get; init; }
}

public sealed class LivePlayer
{
    public string ChampionName { get; init; } = "";

    /// <summary>e.g. "game_character_displayname_MonkeyKing"; the suffix is the Data Dragon champion id.</summary>
    public string? RawChampionName { get; init; }

    public string? RiotId { get; init; }
    public string? SummonerName { get; init; }

    /// <summary>"ORDER" (blue side) or "CHAOS" (red side).</summary>
    public string Team { get; init; } = "";

    public string? Position { get; init; }
    public int Level { get; init; }
    public bool IsDead { get; init; }
    public List<LiveItem> Items { get; init; } = [];
    public LiveScores Scores { get; init; } = new();
    public LiveSummonerSpells? SummonerSpells { get; init; }

    public bool HasSmite => SummonerSpells?.Names.Any(n => n.Contains("Smite", StringComparison.OrdinalIgnoreCase)) == true;
}

public sealed class LiveSummonerSpells
{
    public LiveSummonerSpell? SummonerSpellOne { get; init; }
    public LiveSummonerSpell? SummonerSpellTwo { get; init; }

    public IEnumerable<string> Names =>
        new[] { SummonerSpellOne, SummonerSpellTwo }.OfType<LiveSummonerSpell>().SelectMany(s => new[] { s.DisplayName, s.RawDisplayName }).OfType<string>();
}

public sealed class LiveSummonerSpell
{
    public string? DisplayName { get; init; }

    /// <summary>e.g. "GeneratedTip_SummonerSpell_SummonerSmite_DisplayName", the same in every client language.</summary>
    public string? RawDisplayName { get; init; }
}

public sealed class LiveItem
{
    public int ItemID { get; init; }
    public string DisplayName { get; init; } = "";
    public int Count { get; init; } = 1;
    public int Slot { get; init; }
}

public sealed class LiveScores
{
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int CreepScore { get; init; }
}

public sealed class LiveGameInfo
{
    /// <summary>"CLASSIC", "ARAM", "KIWI" (ARAM: Mayhem), "CHERRY" (Arena), ...</summary>
    public string? GameMode { get; init; }

    /// <summary>11 = Summoner's Rift, 12 = Howling Abyss, 453 = League Classic (probably).</summary>
    public int MapNumber { get; init; }

    public double GameTime { get; init; }
}
