using System.Text.Json;
using LeagueClanker.Core.LiveClient;

namespace LeagueClanker.Core.StaticData;

/// <param name="Id">Data Dragon id, e.g. "MonkeyKing" for Wukong.</param>
/// <param name="Tags">Riot's class tags, primary first: Mage, Assassin, Fighter, Tank, Marksman, Support.</param>
/// <param name="Attack">Riot's 0-10 rating of physical damage.</param>
/// <param name="Magic">Riot's 0-10 rating of magic damage.</param>
/// <param name="BaseStats">Level 1 stats and growth. Null for unknown champions.</param>
/// <param name="Resource">"Mana", "Energy", "None", "Fury", ...</param>
/// <param name="Key">Riot's numeric champion id, e.g. 222 for Jinx. The League client and stats sites use this one.</param>
public sealed record ChampionInfo(
    string Id, string Name, IReadOnlyList<string> Tags, int Attack, int Defense, int Magic,
    ChampionStats? BaseStats = null, string Resource = "Mana", int Key = 0)
{
    /// <summary>From the hand-kept lists, plus what the ability tooltips show once <see cref="ChampionCatalog.WithAbilities"/> ran.</summary>
    public ChampionTraits Traits { get; init; } = Analysis.ChampionKnowledge.TraitsOf(Id);

    public bool Has(ChampionTraits trait) => (Traits & trait) == trait;

    public bool UsesMana => Resource.Equals("Mana", StringComparison.OrdinalIgnoreCase);

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

    /// <param name="linear">League Classic grows stats by the same amount every level, like the game did before 2015.</param>
    public double HealthAt(int level, bool linear = false) => Health + HealthPerLevel * Growth(level, linear);
    public double ArmorAt(int level, bool linear = false) => Armor + ArmorPerLevel * Growth(level, linear);
    public double MagicResistAt(int level, bool linear = false) => MagicResist + MagicResistPerLevel * Growth(level, linear);
    public double AttackDamageAt(int level, bool linear = false) => AttackDamage + AttackDamagePerLevel * Growth(level, linear);
    public double BonusAttackSpeedAt(int level, bool linear = false) => AttackSpeedPerLevel * Growth(level, linear);

    /// <summary>How many levels' worth of per-level growth a champion has. League's curve gives later levels slightly more.</summary>
    public static double Growth(int level, bool linear = false)
    {
        var levelsGained = Math.Clamp(level, 1, 18) - 1;
        return linear ? levelsGained : levelsGained * (0.7025 + 0.0175 * levelsGained);
    }
}

public sealed class ChampionCatalog
{
    private const string RawNamePrefix = "game_character_displayname_";

    private readonly Dictionary<string, ChampionInfo> _byId;
    private readonly Dictionary<string, ChampionInfo> _byNormalizedName;
    private readonly Dictionary<int, ChampionInfo> _byKey;

    public ChampionCatalog(IEnumerable<ChampionInfo> champions)
    {
        var list = champions.ToList();
        _byId = list.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        _byNormalizedName = list.GroupBy(c => Normalize(c.Name)).ToDictionary(g => g.Key, g => g.First());
        _byKey = list.Where(c => c.Key > 0).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
    }

    public IEnumerable<ChampionInfo> All => _byId.Values;

    public ChampionInfo? Get(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Looks up a champion by Riot's numeric id, as the League client reports it in champ select.</summary>
    public ChampionInfo? GetByKey(int key) => _byKey.GetValueOrDefault(NormalizeKey(key));

    /// <summary>League Classic numbers its champions from 60000 (60021 is Miss Fortune, 21). Other modes use the plain key.</summary>
    public const int LeagueClassicKeyOffset = 60000;

    public static int NormalizeKey(int key) => key > LeagueClassicKeyOffset ? key - LeagueClassicKeyOffset : key;

    /// <summary>Finds a champion by name or id, ignoring case, spaces and punctuation ("kaisa" finds Kai'Sa).</summary>
    public ChampionInfo? Find(string name) => _byId.GetValueOrDefault(name) ?? _byNormalizedName.GetValueOrDefault(Normalize(name));

    /// <summary>Maps a live-game player to static champion data. Unknown champions get a neutral placeholder.</summary>
    public ChampionInfo Resolve(LivePlayer player)
    {
        // League Classic names its champions "Jade_MissFortune"; the display name is in the client's language, so use the id.
        var rawId = player.RawChampionName?.StartsWith(RawNamePrefix, StringComparison.OrdinalIgnoreCase) == true
            ? player.RawChampionName[RawNamePrefix.Length..]
            : null;
        if (rawId?.StartsWith("Jade_", StringComparison.OrdinalIgnoreCase) == true)
            rawId = rawId[5..];
        if (rawId is not null && _byId.TryGetValue(rawId, out var byId))
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
                },
                c.GetStringOrEmpty("partype"),
                int.TryParse(c.GetStringOrEmpty("key"), out var key) ? key : 0);
        });
        return new ChampionCatalog(champions);
    }

    /// <summary>Adds the traits each champion's abilities show to the hand-kept ones.</summary>
    public ChampionCatalog WithAbilities(IEnumerable<AbilityProfile> abilities)
    {
        var byId = abilities.ToDictionary(a => a.ChampionId, StringComparer.OrdinalIgnoreCase);
        return new ChampionCatalog(All.Select(c => byId.TryGetValue(c.Id, out var a) ? c with { Traits = c.Traits | a.Traits } : c));
    }

    private static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
