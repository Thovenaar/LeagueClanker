using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.LeagueClient;

/// <param name="Champion">Your pick, or the champion you hover before your turn. Null before either.</param>
/// <param name="Position">Your assigned role, or the role you queued for when champ select doesn't assign one.</param>
/// <param name="Enemies">Enemy picks you can see: all of them in draft, none in blind pick.</param>
public sealed record ChampSelectState(
    ChampionInfo? Champion, Position Position, GameMode Mode, IReadOnlyList<ChampionInfo> Allies, IReadOnlyList<ChampionInfo> Enemies)
{
    /// <summary>True once you've locked in your pick. Before that, <see cref="Champion"/> may just be a hover.</summary>
    public bool IsLocked { get; init; }

    /// <summary>Champions you can pick. Empty when unknown.</summary>
    public IReadOnlySet<int> Pickable { get; init; } = new HashSet<int>();

    /// <summary>Your mastery points per champion key.</summary>
    public IReadOnlyDictionary<int, int> Mastery { get; init; } = new Dictionary<int, int>();

    /// <summary>The summoner spells you have selected now, on your first and second key.</summary>
    public (int First, int Second) Spells { get; init; }

    /// <summary>True while your ban is still to come.</summary>
    public bool HasPendingBan { get; init; }

    /// <summary>Banned champions, by key.</summary>
    public IReadOnlySet<int> Bans { get; init; } = new HashSet<int>();

    /// <summary>
    /// The game as it will look at the start: everyone at level 1 without items. The item advisor can then recommend a
    /// build before the game, for the shop's item set. Your playstyle is passed to the analyzer separately.
    /// </summary>
    public AllGameData? ToGameData()
    {
        if (Champion is not { } me)
            return null;

        LivePlayer Player(ChampionInfo champion, string team, string name, Position position = Position.None) => new()
        {
            ChampionName = champion.Name,
            RawChampionName = $"game_character_displayname_{champion.Id}",
            RiotId = name,
            Team = team,
            Level = 1,
            Position = position == Position.Support ? "UTILITY" : position == Position.None ? "" : position.ToString().ToUpperInvariant(),
            SummonerSpells = position == Position.Jungle
                ? new LiveSummonerSpells { SummonerSpellOne = new LiveSummonerSpell { DisplayName = "Smite" } }
                : null,
        };

        return new AllGameData
        {
            ActivePlayer = new ActivePlayer { RiotId = "You#LC", Level = 1 },
            AllPlayers =
            [
                Player(me, "ORDER", "You#LC", Position),
                .. Allies.Select((c, i) => Player(c, "ORDER", $"Ally{i}#LC")),
                .. Enemies.Select((c, i) => Player(c, "CHAOS", $"Enemy{i}#LC")),
            ],
            GameData = new LiveGameInfo { GameMode = Mode.ClientModeName(), MapNumber = Mode.MapId() },
        };
    }

    /// <summary>Changes when anything that affects the rune page, the matchup or the draft advice changes.</summary>
    public string Fingerprint =>
        $"{Champion?.Id}|{IsLocked}|{Position}|{Mode}|{HasPendingBan}|{string.Join(',', Enemies.Select(e => e.Id))}"
        + $"|{string.Join(',', Allies.Select(a => a.Id))}|{string.Join(',', Bans.Order())}";

    public static ChampSelectState? From(ClientSnapshot snapshot, ChampionCatalog champions)
    {
        if (snapshot.ChampSelect is not { } session)
            return null;

        var me = session.MyTeam.FirstOrDefault(p => p.CellId == session.LocalPlayerCellId);
        var championKey = me is null ? 0 : me.ChampionId != 0 ? me.ChampionId : me.ChampionPickIntent;

        var position = Positions.Parse(me?.AssignedPosition);
        if (position == Position.None)
            position = Positions.Parse(snapshot.Lobby?.LocalMember?.FirstPositionPreference);

        var queue = snapshot.Gameflow?.GameData?.Queue;
        var mode = queue is null ? GameMode.SummonersRift : GameModes.Detect(queue.GameMode, queue.MapId);

        // Teammates count with their hover, so the team check works during planning. Enemies only once locked.
        IReadOnlyList<ChampionInfo> Picks(IEnumerable<ChampSelectPlayer> players, bool withHovers) =>
            players.Select(p => champions.GetByKey(p.ChampionId != 0 || !withHovers ? p.ChampionId : p.ChampionPickIntent))
                .OfType<ChampionInfo>().ToList();

        // Locked once your pick action is done. Modes without pick turns (ARAM) hand you a champion: that counts as locked.
        var actions = session.Actions.SelectMany(turn => turn).ToList();
        var myPicks = actions.Where(a => a.Type == "pick" && a.ActorCellId == session.LocalPlayerCellId).ToList();
        var isLocked = myPicks.Count > 0 ? myPicks.Any(a => a.Completed) : me?.ChampionId > 0;
        var myBans = actions.Where(a => a.Type == "ban" && a.ActorCellId == session.LocalPlayerCellId).ToList();
        var bans = actions.Where(a => a.Type == "ban" && a.Completed && a.ChampionId > 0).Select(a => a.ChampionId)
            .Concat(session.Bans?.MyTeamBans ?? []).Concat(session.Bans?.TheirTeamBans ?? [])
            .Where(id => id > 0)
            .ToHashSet();

        return new ChampSelectState(
            champions.GetByKey(championKey), position, mode,
            Picks(session.MyTeam.Where(p => p != me), withHovers: true), Picks(session.TheirTeam, withHovers: false))
        {
            IsLocked = isLocked,
            HasPendingBan = myBans.Count > 0 && !myBans.Any(a => a.Completed),
            Bans = bans,
            Spells = (me?.Spell1Id ?? 0, me?.Spell2Id ?? 0),
            Pickable = snapshot.PickableChampionIds.ToHashSet(),
            Mastery = snapshot.Mastery.GroupBy(m => m.ChampionId).ToDictionary(g => g.Key, g => g.Max(m => m.ChampionPoints)),
        };
    }
}

public sealed record ApplyResult(bool Success, string Message);

/// <summary>
/// Writes a rune page into the client. It overwrites your current page when the client allows that. Preset pages can't
/// be edited, so then it reuses a page LeagueClanker wrote before, or makes a new one if you have a free page slot.
/// </summary>
public sealed class RunePageWriter(IRunePageStore store)
{
    public const string PagePrefix = "LeagueClanker";
    private const int MaxPageNameLength = 25;

    public static string PageName(ChampionInfo champion)
    {
        var name = $"{PagePrefix} {champion.Name}";
        return name.Length <= MaxPageNameLength ? name : name[..MaxPageNameLength];
    }

    public async Task<ApplyResult> ApplyAsync(RunePage page, string pageName, CancellationToken ct = default)
    {
        var request = new PerkPageRequest(pageName, page.PrimaryStyleId, page.SubStyleId, page.PerkIds);

        var current = await store.GetCurrentPageAsync(ct);
        if (current is { IsEditable: true })
        {
            await store.UpdatePageAsync(current.Id, request, ct);
            return new ApplyResult(true, $"Overwrote your rune page \"{current.Name}\".");
        }

        var pages = await store.GetPagesAsync(ct);
        if (pages.FirstOrDefault(p => p.IsEditable && p.Name.StartsWith(PagePrefix, StringComparison.Ordinal)) is { } ours)
        {
            await store.UpdatePageAsync(ours.Id, request, ct);
            await store.SetCurrentPageAsync(ours.Id, ct);
            return new ApplyResult(true, $"Your current page is a preset, so I updated \"{ours.Name}\" and selected it.");
        }

        if (pages.Count(p => p.IsEditable) < await store.GetOwnedPageCountAsync(ct))
        {
            await store.CreatePageAsync(request, ct);
            return new ApplyResult(true, $"Your current page is a preset, so I made a new page \"{pageName}\".");
        }

        return new ApplyResult(false, "Your current rune page is a preset, which apps can't edit. Select one of your own pages and press Apply again.");
    }
}
