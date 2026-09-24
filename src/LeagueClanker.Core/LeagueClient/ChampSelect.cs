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

    /// <summary>Changes when anything that affects the rune page or the matchup changes.</summary>
    public string Fingerprint =>
        $"{Champion?.Id}|{IsLocked}|{Position}|{Mode}|{string.Join(',', Enemies.Select(e => e.Id))}";

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

        IReadOnlyList<ChampionInfo> Picks(IEnumerable<ChampSelectPlayer> players) =>
            players.Select(p => champions.GetByKey(p.ChampionId)).OfType<ChampionInfo>().ToList();

        // Locked once your pick action is done. Modes without pick turns (ARAM) hand you a champion: that counts as locked.
        var myPicks = session.Actions.SelectMany(turn => turn).Where(a => a.Type == "pick" && a.ActorCellId == session.LocalPlayerCellId).ToList();
        var isLocked = myPicks.Count > 0 ? myPicks.Any(a => a.Completed) : me?.ChampionId > 0;

        return new ChampSelectState(
            champions.GetByKey(championKey), position, mode,
            Picks(session.MyTeam.Where(p => p != me)), Picks(session.TheirTeam))
        {
            IsLocked = isLocked,
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
