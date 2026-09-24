using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Opgg;

namespace LeagueClanker.Core.Runes;

/// <summary>
/// The most played rune page on op.gg that fits your playstyle. When op.gg fails, <see cref="RuneAdvisor"/> falls back
/// to the rules.
/// </summary>
public sealed class OpggRuneSource(RuneCatalog catalog, OpggClient opgg) : IRuneSource
{
    public const string SourceName = "op.gg";

    // Pages below this many games are noise.
    private const int MinGames = 50;

    // With fewer games than this in the role you asked for, the champion's main role is a better sample.
    private const int MinGamesInRole = 4000;

    public async Task<RuneRecommendation?> RecommendAsync(RuneRequest request, CancellationToken ct)
    {
        if (request.Champion.Key <= 0)
            return null;

        var aram = request.Mode is GameMode.Aram or GameMode.AramMayhem;
        var role = OpggClient.RoleFor(request.Position, request.Playstyle);
        var data = await opgg.GetChampionAsync(request.Champion.Key, aram, role, ct);

        // No role picked (blind pick, normals) and a guessed role with few games: use the role op.gg says they're played in.
        if (!aram && request.Position == Position.None && data is { MainRole: var main and not Position.None } && main != role
            && data.TotalGames < MinGamesInRole)
        {
            role = main;
            data = await opgg.GetChampionAsync(request.Champion.Key, aram, role, ct);
        }
        if (data is null)
            return null;

        var best = data.Pages
            .Where(p => p.Games >= MinGames && catalog.Validate(p.Page) is null)
            .Where(p => catalog.Get(p.Page.Keystone) is { } keystone && KeystoneFit.Suits(request.Playstyle, keystone))
            .MaxBy(p => p.Games);
        if (best is null)
            return null;

        var where = aram ? "ARAM" : role.DisplayName().ToLowerInvariant();
        var reason = $"Most played {request.Playstyle.DisplayName().ToLowerInvariant()} page for {request.Champion.Name} ({where}) on op.gg.";
        return new RuneRecommendation(best.Page, SourceName, [reason]) { Games = best.Games, WinRate = best.WinRate };
    }
}
