using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

/// <summary>A build players actually use, from op.gg: three core items and the items they finish with.</summary>
/// <param name="Style">The playstyle these items belong to: on-hit, mage, tank, ...</param>
/// <param name="Core">The first three finished items, in buy order.</param>
/// <param name="Later">Items players of this style finish with, most played first.</param>
/// <param name="Games">Games with any core of this style.</param>
public sealed record MetaBuild(Archetype Style, IReadOnlyList<ItemInfo> Core, IReadOnlyList<ItemInfo> Later, int Games, double WinRate)
{
    public IEnumerable<ItemInfo> Items => Core.Concat(Later);

    /// <summary>Where the build comes from: "op.gg", or "Blitz" for League Classic.</summary>
    public string Source { get; init; } = "op.gg";

    /// <summary>False for curated builds without game counts (Blitz): then the game alone chooses.</summary>
    public bool HasStats => Games > 0;

    /// <summary>Items the source lists for when the game asks for them. When there are any, the one swap picks from these.</summary>
    public IReadOnlyList<ItemInfo> Situational { get; init; } = [];

    /// <summary>The build's boots, when the source names them. Null leaves boots to the game's situations.</summary>
    public ItemInfo? Boots { get; init; }

    /// <summary>Identifies the build between updates, so the last choice can be kept.</summary>
    public string Key => $"{Style}:{string.Join(",", Core.Select(i => i.Id))}";

    /// <summary>The source's own name for the build, like Blitz's "AD" or "Crit". Null names it after the style.</summary>
    public string? Label { get; init; }

    /// <summary>"on-hit build", "AP burst build", "crit build".</summary>
    public string Name => Label is { } label ? $"{label} build" : Style switch
    {
        Archetype.Marksman => "crit build",
        Archetype.AdAssassin => "lethality build",
        Archetype.Mage => "AP build",
        Archetype.ApAssassin => "AP burst build",
        Archetype.ApBruiser => "AP bruiser build",
        _ => $"{Style.DisplayName().ToLowerInvariant()} build",
    };
}

/// <summary>Which meta build fits this game, and why.</summary>
/// <param name="Others">The builds it beat, with their scores.</param>
/// <param name="Swap">The one item swapped out of the build for this game, explained. Null when the build is used as is.</param>
public sealed record MetaChoice(MetaBuild Build, double Score, string? Because, IReadOnlyList<(MetaBuild Build, double Score)> Others)
{
    public string? Swap { get; init; }

    /// <summary>The item swapped into the build, or null.</summary>
    public ItemInfo? SwapIn { get; init; }

    /// <summary>Items that belong to this game's build: the meta build's items and the swap.</summary>
    public bool Includes(ItemInfo item) => Build.Items.Any(i => i.Id == item.Id) || SwapIn?.Id == item.Id;

    /// <summary>"op.gg's on-hit build (57.5% win rate over 348 games), over the AP build: enemy has 3 tanks (...)."</summary>
    public string Text
    {
        get
        {
            var text = Build.HasStats
                ? $"{Build.Source}'s {Build.Name} ({Build.WinRate:P1} win rate over {Build.Games:N0} games)"
                : $"{Build.Source}'s {Build.Name}";
            if (Others.Count == 0)
                return text + ".";
            var other = Others[0].Build;
            return Because is null
                ? other.HasStats ? $"{text}, over the {other.Name} ({other.WinRate:P1})." : $"{text}, over the {other.Name}."
                : $"{text}, over the {other.Name}: {char.ToLowerInvariant(Because[0])}{Because[1..]}.";
        }
    }
}

/// <summary>
/// Builds from what players actually build instead of from item scores: op.gg's core builds for your champion are
/// grouped into a few styles, the game decides which style fits (tanks, healing, damage split), and at most one
/// later item is swapped when the game clearly asks for something else.
/// </summary>
public static class MetaBuilds
{
    /// <summary>Cores with fewer games are noise.</summary>
    public const int MinGames = 50;

    public const int MaxBuilds = 4;

    /// <summary>Later items per build, on top of the three core items.</summary>
    public const int LaterCount = 3;

    /// <summary>A build's win rate counts in proportion to games / (games + this): 200 games count half.</summary>
    public const double PriorGames = 200;

    /// <summary>Points per percentage point of win rate above or below 50%, at full confidence.</summary>
    public const double PointsPerWinRatePoint = 0.2;

    /// <summary>For each finished item you own that's in the build: switching builds wastes them.</summary>
    public const double OwnedItemPoints = 1.0;

    /// <summary>For the build chosen last time, so two close builds don't swap back and forth.</summary>
    public const double KeepPoints = 0.5;

    /// <summary>Groups op.gg's cores (with at least <see cref="MinGames"/> games) into styles, most played first.</summary>
    public static IReadOnlyList<MetaBuild> From(OpggChampion champion, ItemCatalog items, GameMode mode)
    {
        ItemInfo? Legendary(int id) => items.Get(id) is { Kind: ItemKind.Legendary } item && item.Maps.Contains(mode.MapId()) ? item : null;

        var cores = champion.CoreItems
            .Where(c => c.Games >= MinGames)
            .Select(c => (Choice: c, Items: c.Ids.Select(Legendary).OfType<ItemInfo>().DistinctBy(i => i.Id).ToList()))
            .Where(c => c.Items.Count >= 2)
            .Select(c => (c.Choice, c.Items, Style: StyleOf(c.Items)))
            .ToList();
        var laterPool = champion.LaterItems.Where(l => l.Ids.Count == 1).Select(l => Legendary(l.Ids[0])).OfType<ItemInfo>().ToList();

        return cores
            .GroupBy(c => Family(c.Style))
            .Select(g =>
            {
                var main = g.MaxBy(c => c.Choice.Games);
                var style = g.GroupBy(c => c.Style).MaxBy(s => s.Sum(c => c.Choice.Games))!.Key;
                var profile = ArchetypeProfiles.For(style);
                // Later items are counted over all of the champion's games, so keep the ones that belong to this style:
                // Stormsurge is popular on Katarina, but not with on-hit Katarinas.
                var later = laterPool
                    .Where(i => main.Items.All(c => c.Id != i.Id) && profile.BaseScore(i) >= profile.MinFit && Family(StyleOf([i])) == Family(style))
                    .Take(LaterCount)
                    .ToList();
                var games = g.Sum(c => c.Choice.Games);
                return new MetaBuild(style, main.Items, later, games, (double)g.Sum(c => c.Choice.Wins) / games);
            })
            .OrderByDescending(b => b.Games)
            .Take(MaxBuilds)
            .ToList();
    }

    /// <summary>
    /// The build that fits this game best: op.gg's win rate (trusted by sample size), how well its items answer this
    /// game's situations, the items you already own, and a nudge for the build chosen last time.
    /// </summary>
    /// <param name="keep">The <see cref="MetaBuild.Key"/> chosen on the previous update.</param>
    public static MetaChoice? Choose(IReadOnlyList<MetaBuild> builds, GameAnalysis game, IReadOnlyList<Situation> situations, string? keep = null)
    {
        // A playstyle you picked yourself limits the choice to builds of that kind.
        var allowed = game.ChosenPlaystyle is { } chosen ? builds.Where(b => Family(b.Style) == Family(chosen)).ToList() : builds;
        if (allowed.Count == 0)
            return null;

        var owned = game.Me.Items.Where(i => i.Kind == ItemKind.Legendary).Select(i => i.Id).ToHashSet();
        double Fit(MetaBuild b) => b.Items.Average(i => situations.Sum(s => s.Score(i)));
        double Score(MetaBuild b) =>
            (b.WinRate - 0.5) * 100 * PointsPerWinRatePoint * (b.Games / (b.Games + PriorGames))
            + Fit(b)
            + OwnedItemPoints * b.Items.Count(i => owned.Contains(i.Id))
            + (b.Key == keep ? KeepPoints : 0);

        var ranked = allowed.Select(b => (Build: b, Score: Score(b)))
            .OrderByDescending(x => x.Build.Key == game.ForcedMeta)
            .ThenByDescending(x => x.Score)
            .ToList();
        var best = ranked[0];

        // Explain it with the situation where the chosen build's items do most better than the runner-up's.
        string? because = null;
        if (ranked.Count > 1)
        {
            var other = ranked[1].Build;
            because = situations
                .Select(s => (Situation: s, Edge: best.Build.Items.Average(s.Score) - other.Items.Average(s.Score)))
                .Where(x => x.Edge >= 0.2)
                .OrderByDescending(x => x.Edge)
                .Select(x => x.Situation.Description)
                .FirstOrDefault();
        }
        return new MetaChoice(best.Build, best.Score, because, ranked.Skip(1).ToList());
    }

    /// <summary>The playstyle whose profile likes these items best.</summary>
    internal static Archetype StyleOf(IReadOnlyList<ItemInfo> items) =>
        Enum.GetValues<Archetype>().MaxBy(a => items.Average(i => ArchetypeProfiles.For(a).BaseScore(i)));

    // Mage and AP assassin cores are the same kind of build for choosing between styles, and so on.
    private static int Family(Archetype style) => style switch
    {
        Archetype.Mage or Archetype.ApAssassin => 1,
        Archetype.Marksman or Archetype.AdAssassin => 2,
        Archetype.Bruiser or Archetype.ApBruiser => 3,
        Archetype.Tank => 4,
        Archetype.Enchanter => 5,
        _ => 6,
    };
}
