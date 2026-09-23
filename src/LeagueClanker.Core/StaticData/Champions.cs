using System.Text.Json;
using LeagueClanker.Core.LiveClient;

namespace LeagueClanker.Core.StaticData;

/// <param name="Id">Data Dragon id, e.g. "MonkeyKing" for Wukong.</param>
/// <param name="Tags">Riot's class tags, primary first: Mage, Assassin, Fighter, Tank, Marksman, Support.</param>
/// <param name="Attack">Riot's 0-10 rating of physical damage.</param>
/// <param name="Magic">Riot's 0-10 rating of magic damage.</param>
/// <param name="BaseStats">Level 1 stats and growth. Null for unknown champions.</param>
public sealed record ChampionInfo(string Id, string Name, IReadOnlyList<string> Tags, int Attack, int Defense, int Magic, ChampionStats? BaseStats = null)
{
    public string PrimaryTag => Tags.Count > 0 ? Tags[0] : "";
    public string SecondaryTag => Tags.Count > 1 ? Tags[1] : "";
    public ChampionStats Stats => BaseStats ?? ChampionStats.Typical;
}

/// <summary>Level 1 stats and per-level growth from Data Dragon. Defaults are roughly an average champion.</summary>
public sealed record ChampionStats
{
    public static readonly ChampionStats Typical = new();

    public double Health { get; init; } = 640;
    public double HealthPerLevel { get; init; } = 105;
    public double Armor { get; init; } = 32;
    public double ArmorPerLevel { get; init; } = 4.7;
    public double MagicResist { get; init; } = 30;
    public double MagicResistPerLevel { get; init; } = 1.8;
    public double AttackDamage { get; init; } = 60;
    public double AttackDamagePerLevel { get; init; } = 3;
    public double AttackSpeed { get; init; } = 0.65;

    /// <summary>Bonus attack speed in percent gained per level.</summary>
    public double AttackSpeedPerLevel { get; init; } = 2.5;

    public double MoveSpeed { get; init; } = 335;
    public double AttackRange { get; init; } = 175;

    public double HealthAt(int level) => Health + HealthPerLevel * Growth(level);
    public double ArmorAt(int level) => Armor + ArmorPerLevel * Growth(level);
    public double MagicResistAt(int level) => MagicResist + MagicResistPerLevel * Growth(level);
    public double AttackDamageAt(int level) => AttackDamage + AttackDamagePerLevel * Growth(level);
    public double BonusAttackSpeedAt(int level) => AttackSpeedPerLevel * Growth(level);

    /// <summary>How many levels' worth of per-level growth a champion has. League's curve gives later levels slightly more.</summary>
    public static double Growth(int level)
    {
        var levelsGained = Math.Clamp(level, 1, 18) - 1;
        return levelsGained * (0.7025 + 0.0175 * levelsGained);
    }
}

public sealed class ChampionCatalog
{
    private const string RawNamePrefix = "game_character_displayname_";

    private readonly Dictionary<string, ChampionInfo> _byId;
    private readonly Dictionary<string, ChampionInfo> _byNormalizedName;

    public ChampionCatalog(IEnumerable<ChampionInfo> champions)
    {
        var list = champions.ToList();
        _byId = list.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        _byNormalizedName = list.GroupBy(c => Normalize(c.Name)).ToDictionary(g => g.Key, g => g.First());
    }

    public ChampionInfo? Get(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Maps a live-game player to static champion data. Unknown champions get a neutral placeholder.</summary>
    public ChampionInfo Resolve(LivePlayer player)
    {
        if (player.RawChampionName?.StartsWith(RawNamePrefix, StringComparison.OrdinalIgnoreCase) == true
            && _byId.TryGetValue(player.RawChampionName[RawNamePrefix.Length..], out var byId))
            return byId;

        return _byNormalizedName.GetValueOrDefault(Normalize(player.ChampionName))
            ?? new ChampionInfo(player.ChampionName, player.ChampionName, [], Attack: 5, Defense: 5, Magic: 5);
    }

    /// <summary>Parses Data Dragon's champion.json.</summary>
    public static ChampionCatalog Parse(string championJson)
    {
        using var doc = JsonDocument.Parse(championJson);
        var champions = doc.RootElement.GetProperty("data").EnumerateObject().Select(entry =>
        {
            var c = entry.Value;
            var info = c.GetProperty("info");
            var stats = c.GetProperty("stats");
            return new ChampionInfo(
                c.GetStringOrEmpty("id"),
                c.GetStringOrEmpty("name"),
                c.GetStringArray("tags").ToList(),
                info.GetProperty("attack").GetInt32(),
                info.GetProperty("defense").GetInt32(),
                info.GetProperty("magic").GetInt32(),
                new ChampionStats
                {
                    Health = stats.GetProperty("hp").GetDouble(),
                    HealthPerLevel = stats.GetProperty("hpperlevel").GetDouble(),
                    Armor = stats.GetProperty("armor").GetDouble(),
                    ArmorPerLevel = stats.GetProperty("armorperlevel").GetDouble(),
                    MagicResist = stats.GetProperty("spellblock").GetDouble(),
                    MagicResistPerLevel = stats.GetProperty("spellblockperlevel").GetDouble(),
                    AttackDamage = stats.GetProperty("attackdamage").GetDouble(),
                    AttackDamagePerLevel = stats.GetProperty("attackdamageperlevel").GetDouble(),
                    AttackSpeed = stats.GetProperty("attackspeed").GetDouble(),
                    AttackSpeedPerLevel = stats.GetProperty("attackspeedperlevel").GetDouble(),
                    MoveSpeed = stats.GetProperty("movespeed").GetDouble(),
                    AttackRange = stats.GetProperty("attackrange").GetDouble(),
                });
        });
        return new ChampionCatalog(champions);
    }

    private static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
