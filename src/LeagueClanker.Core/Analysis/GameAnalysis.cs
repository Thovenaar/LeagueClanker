using LeagueClanker.Core.Augments;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Analysis;

/// <summary>One champion in the game, combining static champion knowledge with what they have bought so far.</summary>
public sealed class PlayerProfile
{
    public required ChampionInfo Champion { get; init; }
    public required Archetype Archetype { get; init; }
    public required IReadOnlyList<ItemInfo> Items { get; init; }

    /// <summary>Estimated share of this champion's damage that is magic (0-1). Shifts as they buy items.</summary>
    public required double MagicShare { get; init; }

    /// <summary>Relative weight of this champion in team-wide numbers: fed and item-rich champions count more.</summary>
    public required double Threat { get; init; }

    public LiveScores Scores { get; init; } = new();

    /// <summary>Junglers take Smite. League Classic has jungle items that only make sense then.</summary>
    public bool HasSmite { get; init; }

    public string Name => Champion.Name;

    /// <summary>How much this champion contributes to the team's damage. Tanks and enchanters deal less than carries.</summary>
    public double DamageWeight => Threat * Archetype switch { Archetype.Tank => 0.5, Archetype.Enchanter => 0.4, _ => 1.0 };

    public int Level { get; init; } = 1;
    public int ItemGold => Items.Sum(i => i.TotalGold);

    public double BonusArmor => Items.Sum(i => i.Stat(Stat.Armor));
    public double BonusMagicResist => Items.Sum(i => i.Stat(Stat.MagicResist));
    public double BonusHealth => Items.Sum(i => i.Stat(Stat.Health));

    /// <summary>
    /// Current combat stats. Real for the active player; for everyone else base stats at their level plus
    /// items, because the Live Client API only exposes the active player's stats.
    /// </summary>
    public required StatBlock Stats { get; init; }

    /// <summary>Defensive stats bought, with 10 health counting as 1 resist.</summary>
    public double DefenseFromItems => BonusArmor + BonusMagicResist + BonusHealth / 10;

    /// <summary>
    /// 0-1. Before they've bought much, a tank champion is assumed to go tank. After about two items'
    /// worth of gold, only what they actually bought counts, so an AP Malphite stops being a tank.
    /// </summary>
    public double Tankiness
    {
        get
        {
            var itemEvidence = Math.Clamp(ItemGold / 5000.0, 0, 1);
            var classPrior = Archetype == Archetype.Tank ? 1.0 : 0.0;
            var built = Math.Clamp(DefenseFromItems / 120.0, 0, 1);
            return (1 - itemEvidence) * classPrior + itemEvidence * built;
        }
    }

    public IEnumerable<ItemInfo> Legendaries => Items.Where(i => i.Kind == ItemKind.Legendary);
    public int CritItems => Legendaries.Count(i => i.Stat(Stat.CritChance) > 0);
    public int AttackSpeedItems => Legendaries.Count(i => i.Stat(Stat.AttackSpeed) > 0);
    public bool IsTanky => Tankiness >= 0.5;

    // Tank champions that skipped defense are as squishy as a mage. Bruisers aren't, their base stats are too high.
    public bool IsSquishy => Tankiness < 0.2 && DefenseFromItems < 50 && (Archetype.IsSquishy() || Archetype == Archetype.Tank);
    public bool HasAntiHeal => Items.Any(i => i.Has(ItemTraits.AntiHeal));
    public bool DealsTrueDamage => ChampionKnowledge.TrueDamage.Contains(Champion.Id);

    public double HealingScore =>
        (ChampionKnowledge.Healers.Contains(Champion.Id) ? 1 : 0)
        + 0.4 * Legendaries.Count(i => i.Has(ItemTraits.Sustain));

    public double ShieldScore =>
        (ChampionKnowledge.Shielders.Contains(Champion.Id) ? 1 : 0)
        + 0.4 * Legendaries.Count(i => i.Has(ItemTraits.GrantsShield) || i.Stat(Stat.HealShieldPower) > 0);

    public double CrowdControlScore =>
        ChampionKnowledge.HeavyCrowdControl.Contains(Champion.Id) ? 1.0
        : Archetype switch { Archetype.Tank => 0.8, Archetype.Enchanter => 0.5, _ => 0.25 };

    public override string ToString() => Name;
}

public sealed class TeamProfile(IReadOnlyList<PlayerProfile> players)
{
    public IReadOnlyList<PlayerProfile> Players { get; } = players;

    /// <summary>Share of the team's damage that is magic, weighted by each champion's damage contribution.</summary>
    public double MagicShare => Players.Count == 0 ? 0.5 : Players.Sum(p => p.MagicShare * p.DamageWeight) / Players.Sum(p => p.DamageWeight);
    public double PhysicalShare => 1 - MagicShare;

    public IReadOnlyList<PlayerProfile> Tanks => Players.Where(p => p.IsTanky).ToList();
    public IReadOnlyList<PlayerProfile> Squishies => Players.Where(p => p.IsSquishy).ToList();
    // A tank champion who built damage isn't frontline anymore; bruisers always are.
    public IReadOnlyList<PlayerProfile> Frontline =>
        Players.Where(p => p.IsTanky || p.Archetype is Archetype.Bruiser or Archetype.ApBruiser).ToList();
    public IReadOnlyList<PlayerProfile> Assassins => Players.Where(p => p.Archetype.IsAssassin()).ToList();
    public IReadOnlyList<PlayerProfile> TrueDamageDealers => Players.Where(p => p.DealsTrueDamage).ToList();
    public IReadOnlyList<PlayerProfile> AntiHealCarriers => Players.Where(p => p.HasAntiHeal).ToList();

    public double HealingScore => Players.Sum(p => p.HealingScore);
    public double ShieldScore => Players.Sum(p => p.ShieldScore);
    public double CrowdControlScore => Players.Sum(p => p.CrowdControlScore);
    public int CritItems => Players.Sum(p => p.CritItems);
    public int AttackSpeedItems => Players.Sum(p => p.AttackSpeedItems);
}

public sealed record GameAnalysis(PlayerProfile Me, TeamProfile Allies, TeamProfile Enemies, double GameTimeSeconds)
{
    public GameMode Mode { get; init; } = GameMode.SummonersRift;

    /// <summary>ARAM: Mayhem augments you picked. They shape the item advice like any other game fact.</summary>
    public IReadOnlyList<AugmentInfo> Augments { get; init; } = [];

    /// <summary>My team including me.</summary>
    public TeamProfile MyTeam => new([Me, .. Allies.Players]);
}

public static class GameAnalyzer
{
    /// <summary>Returns null when the active player can't be found (e.g. spectating).</summary>
    public static GameAnalysis? Analyze(AllGameData data, StaticGameData staticData, IReadOnlyList<AugmentInfo>? augments = null)
    {
        if (data.ActivePlayer is not { } active)
            return null;

        var me = data.AllPlayers.FirstOrDefault(p => SameName(p.RiotId, active.RiotId))
            ?? data.AllPlayers.FirstOrDefault(p => SameName(p.SummonerName, active.SummonerName));
        if (me is null)
            return null;

        var mode = DetectMode(data);
        var allies = data.AllPlayers.Where(p => p != me && p.Team == me.Team).Select(p => Profile(p, staticData, mode: mode)).ToList();
        var enemies = data.AllPlayers.Where(p => p.Team != me.Team).Select(p => Profile(p, staticData, mode: mode)).ToList();
        var myProfile = Profile(me, staticData, active.ChampionStats, mode);
        return new GameAnalysis(myProfile, new TeamProfile(allies), new TeamProfile(enemies), data.GameData?.GameTime ?? 0)
        {
            Mode = mode,
            Augments = augments ?? [],
        };
    }

    /// <summary>
    /// The mode the game reports. League Classic's mode string is unconfirmed, so a Summoner's Rift or unknown game where
    /// anyone holds a Classic item (77xxxx id, including the starting Doran's items) counts as League Classic too.
    /// </summary>
    public static GameMode DetectMode(AllGameData data)
    {
        var mode = GameModes.Detect(data.GameData?.GameMode, data.GameData?.MapNumber ?? 0);
        var holdsClassicItems = data.AllPlayers.Any(p => p.Items.Any(i => ItemCatalog.IsClassicId(i.ItemID)));
        return mode is GameMode.SummonersRift or GameMode.Unsupported && holdsClassicItems ? GameMode.LeagueClassic : mode;
    }

    public static PlayerProfile Profile(LivePlayer player, StaticGameData staticData, LiveChampionStats? realStats = null, GameMode mode = GameMode.SummonersRift)
    {
        var champion = staticData.Champions.Resolve(player);
        var archetype = ArchetypeClassifier.Classify(champion);
        var items = player.Items
            .SelectMany(i => Enumerable.Repeat(staticData.Items.Get(i.ItemID), Math.Max(1, i.Count)))
            .OfType<ItemInfo>()
            .Where(i => !i.Tags.Contains("Consumable") && !i.Tags.Contains("Trinket"))
            .ToList();

        return new PlayerProfile
        {
            Champion = champion,
            Archetype = archetype,
            Items = items,
            MagicShare = EstimateMagicShare(champion, archetype, items),
            Threat = EstimateThreat(items, player.Scores),
            Scores = player.Scores,
            HasSmite = player.HasSmite,
            Level = Math.Max(1, player.Level),
            Stats = StatEstimator.Estimate(champion.Stats, Math.Max(1, player.Level), items, mode).WithRealStats(realStats),
        };
    }

    /// <summary>
    /// Starts from the champion's natural damage split and lets purchased items pull it toward AP or AD.
    /// The champion prior counts as two items' worth of evidence, so by 2-3 items the build dominates.
    /// </summary>
    internal static double EstimateMagicShare(ChampionInfo champion, Archetype archetype, IReadOnlyList<ItemInfo> items)
    {
        const double priorWeight = 2.0;

        var prior = ChampionKnowledge.MagicShareOverrides.TryGetValue(champion.Id, out var known)
            ? known
            : archetype switch
            {
                Archetype.Marksman or Archetype.AdAssassin => 0.1,
                Archetype.Bruiser => 0.15,
                Archetype.Mage => 0.9,
                Archetype.ApAssassin or Archetype.ApBruiser or Archetype.Enchanter => 0.85,
                _ => Math.Clamp(champion.Magic / (double)Math.Max(1, champion.Magic + champion.Attack), 0.2, 0.8),
            };

        var apPower = items.Sum(i => i.Stat(Stat.AbilityPower)) / 90.0;
        var adPower = items.Sum(i => i.Stat(Stat.AttackDamage)) / 55.0
            + 0.4 * items.Count(i => (i.Stat(Stat.AttackSpeed) > 0 || i.Stat(Stat.CritChance) > 0) && i.Stat(Stat.AbilityPower) == 0 && !i.IsBoots);

        return (prior * priorWeight + apPower) / (priorWeight + apPower + adPower);
    }

    internal static double EstimateThreat(IReadOnlyList<ItemInfo> items, LiveScores scores)
    {
        var itemGold = items.Sum(i => i.TotalGold);
        return Math.Clamp(1 + itemGold / 3000.0 + 0.15 * (scores.Kills - scores.Deaths), 0.5, 4);
    }

    private static bool SameName(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
