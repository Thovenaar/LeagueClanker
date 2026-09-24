using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Spells;

/// <param name="First">The spell on your first key (D by default).</param>
/// <param name="Second">The spell on your second key (F by default).</param>
public sealed record SpellRecommendation(int First, int Second, string Source, string Reason)
{
    public int? Games { get; init; }
    public double? WinRate { get; init; }
}

/// <param name="Current">The spells you have selected now. Flash stays on the key you keep it on.</param>
public sealed record SpellRequest(ChampionInfo Champion, Archetype Playstyle, Position Position, GameMode Mode, (int First, int Second) Current);

/// <summary>
/// Summoner spells: op.gg's most played pair for your champion and role, or a standard pair per role. A spell that
/// isn't available in the mode is skipped, and League Classic gets its own copies of the spells.
/// </summary>
public sealed class SpellAdvisor(SummonerSpellCatalog spells, OpggClient? opgg = null)
{
    private const int MinGames = 50;

    public async Task<SpellRecommendation?> RecommendAsync(SpellRequest request, RuneSourceKind source, CancellationToken ct = default)
    {
        if (spells.Get(SummonerSpellCatalog.Flash) is null)
            return null;

        var fromStats = source == RuneSourceKind.StatsSite && opgg is not null ? await FromOpggAsync(request, ct) : null;
        var (first, second, recommendation) = fromStats is { } s ? (s.First, s.Second, s) : Rules(request);
        var (key1, key2) = KeepKeys(spells.ForMode(first, request.Mode), spells.ForMode(second, request.Mode), request.Current);
        return recommendation with { First = key1, Second = key2 };
    }

    private async Task<SpellRecommendation?> FromOpggAsync(SpellRequest request, CancellationToken ct)
    {
        if (request.Mode is not (GameMode.SummonersRift or GameMode.Aram or GameMode.AramMayhem) || request.Champion.Key <= 0)
            return null;

        try
        {
            var aram = request.Mode is GameMode.Aram or GameMode.AramMayhem;
            var data = await opgg!.GetChampionAsync(request.Champion.Key, aram, OpggClient.RoleFor(request.Position, request.Playstyle), ct);
            var pair = data?.Spells.FirstOrDefault(p =>
                p.Ids.Count == 2 && p.Games >= MinGames && p.Ids.All(id => spells.IsAvailable(id, request.Mode)) && FitsRole(p.Ids, request));
            if (pair is null)
                return null;
            var names = string.Join(" + ", pair.Ids.Select(Name));
            return new SpellRecommendation(pair.Ids[0], pair.Ids[1], OpggRuneSource.SourceName, $"{names} is the most played pair on op.gg.")
            {
                Games = pair.Games,
                WinRate = pair.WinRate,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Log.Error("op.gg summoner spells", ex);
            return null;
        }
    }

    // Smite only for the jungle, and the jungle always needs it. Guards against op.gg's odd pairs in a role you asked for.
    private static bool FitsRole(IReadOnlyList<int> ids, SpellRequest request) =>
        request.Position == Position.None || (request.Position == Position.Jungle) == ids.Contains(SummonerSpellCatalog.Smite);

    private (int, int, SpellRecommendation) Rules(SpellRequest request)
    {
        var role = request.Mode is GameMode.Aram or GameMode.AramMayhem ? Position.None : OpggClient.RoleFor(request.Position, request.Playstyle);
        var (second, why) = request.Mode is GameMode.Aram or GameMode.AramMayhem
            ? (SummonerSpellCatalog.Mark, "Mark to start fights from range")
            : role switch
            {
                Position.Jungle => (SummonerSpellCatalog.Smite, "Smite for the jungle camps and objectives"),
                Position.Top => (SummonerSpellCatalog.Teleport, "Teleport to get back to lane and join fights"),
                Position.Middle when request.Playstyle.IsFrontline() => (SummonerSpellCatalog.Teleport, "Teleport to get back to lane and join fights"),
                Position.Middle => (SummonerSpellCatalog.Ignite, "Ignite to finish kills in lane"),
                Position.Bottom => request.Playstyle == Archetype.Marksman
                    ? (SummonerSpellCatalog.Heal, "Heal for you and your support")
                    : (SummonerSpellCatalog.Barrier, "Barrier to survive burst"),
                Position.Support when request.Playstyle == Archetype.Enchanter => (SummonerSpellCatalog.Exhaust, "Exhaust to shut down a diving enemy"),
                Position.Support => (SummonerSpellCatalog.Ignite, "Ignite for early kills in lane"),
                _ => (SummonerSpellCatalog.Ignite, "Ignite for kill pressure"),
            };
        var first = SummonerSpellCatalog.Flash;
        if (!spells.IsAvailable(spells.ForMode(second, request.Mode), request.Mode))
            second = SummonerSpellCatalog.Ghost;
        return (first, second, new SpellRecommendation(first, second, RuleRuneSource.SourceName, $"Flash, plus {why}."));
    }

    /// <summary>Puts the spells on the keys you have them on now, so Flash doesn't move from D to F.</summary>
    public static (int First, int Second) KeepKeys(int a, int b, (int First, int Second) current)
    {
        var kept = (a == current.First ? 1 : 0) + (b == current.Second ? 1 : 0);
        var swapped = (b == current.First ? 1 : 0) + (a == current.Second ? 1 : 0);
        return swapped > kept ? (b, a) : (a, b);
    }

    private string Name(int id) => spells.Get(id)?.Name ?? id.ToString();
}
