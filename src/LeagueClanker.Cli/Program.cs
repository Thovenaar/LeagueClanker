using LeagueClanker.Core;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.History;
using LeagueClanker.Core.ItemSets;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.Spells;
using LeagueClanker.Core.StaticData;
using LeagueClanker.Vision;

// Usage:
//   LeagueClanker.Cli                         watch the live game and print a new build whenever it changes
//   LeagueClanker.Cli <game.json> [--no-meta] analyze a saved allgamedata snapshot once, following op.gg's builds unless --no-meta
//   LeagueClanker.Cli <folder | a.json b.json> [--decline]
//                                             replay snapshots through the pivot planner, accepting pivots (or declining)
//   LeagueClanker.Cli --items [map]           list the item catalog with detected traits (map 453 = League Classic)
//   LeagueClanker.Cli --augments [arena] [--locale de_DE]
//                                             list Mayhem (or Arena) augments with their tags, and names in that language
//   LeagueClanker.Cli --champions             list healers, shielders, crowd control and true damage, from the hand-kept
//                                             lists and the ability tooltips
//   LeagueClanker.Cli --mayhem <game.json> --offer "A;B;C" [--picked "X;Y"] [--rerolled "A"] [--golden "B"] [--no-community]
//                                             rank an augment offer and say which cards to reroll, with arammayhem.com's win rates
//   LeagueClanker.Cli --scan <image.png | screen> [--verbose] [--locale de_DE]
//                                             read an augment offer from a screenshot or the game, in the client's language
//   LeagueClanker.Cli --runes <champion> [--position support] [--style tank] [--mode aram] [--enemies "A;B"] [--source rules]
//                                             recommend a rune page (from op.gg unless --source rules)
//   LeagueClanker.Cli --champselect [--style tank] [--source rules] [--apply]
//                                             runes and lane matchup for your champ select pick; --apply writes the runes
//   LeagueClanker.Cli --matchup <champion | -> --position top --enemies "A;B;C" [--allies "D;E"] [--hover] [--ban]
//                                             guess enemy roles, show your lane matchup, or counter picks with "-" or --hover;
//                                             --allies adds the team comp check, --ban adds ban suggestions
//   LeagueClanker.Cli --history [games.json]  your Summoner's Rift record per champion and lane opponent, from the League
//                                             client's match history plus the app's saved games (or the given file)

Console.OutputEncoding = System.Text.Encoding.UTF8;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("Loading Data Dragon...");
var data = await new DataDragonClient().LoadAsync(cts.Token);
Console.WriteLine($"Patch {data.Version}: {data.Items.Legendaries.Count} legendary items, {data.Items.Boots.Count} boots.\n");
var opgg = new OpggClient();

if (args is ["--items", ..])
{
    // --items [map]: 11 Summoner's Rift (default), 12 Howling Abyss, 453 League Classic.
    var map = args.Length > 1 ? int.Parse(args[1]) : GameModes.SummonersRiftMap;
    foreach (var item in data.Items.LegendariesOn(map).Concat(data.Items.BootsOn(map)))
        Console.WriteLine($"{item.Id,5} {item.Name,-30} {item.TotalGold,5}g  {item.Traits,-40} {string.Join(", ", item.Stats.Select(s => $"{s.Key} {s.Value}"))}"
                          + string.Concat(item.Scaling.Select(s => $" | {s.Passive}: {s.Stat} {(s.Source == ScalingSource.Stacked ? $"+{s.Amount}" : $"{s.Amount:P1} of {s.Source}")}")));
    return;
}

if (args is ["--champions", ..])
{
    var byId = data.Abilities.ToDictionary(a => a.ChampionId);
    Console.WriteLine($"{byId.Count} champions with ability data. * = added by the abilities, not the hand-kept lists.\n");
    foreach (var trait in new[] { ChampionTraits.Healer, ChampionTraits.Shielder, ChampionTraits.HeavyCrowdControl, ChampionTraits.TrueDamage })
    {
        var champions = data.Champions.All.Where(c => c.Has(trait)).OrderBy(c => c.Name).ToList();
        Console.WriteLine($"{trait} ({champions.Count}):");
        Console.WriteLine("  " + string.Join(", ", champions.Select(c => ChampionKnowledge.TraitsOf(c.Id).HasFlag(trait) ? c.Name : c.Name + "*")) + "\n");
    }
    Console.WriteLine("Per champion, the abilities that heal (H), shield (S), crowd control (C) and deal true damage (T):");
    foreach (var a in data.Abilities.OrderBy(a => a.ChampionId))
        Console.WriteLine($"  {a.ChampionId,-14} H {a.Healing,-5} S {a.Shielding,-5} C {a.HardCrowdControl,-5} T {a.TrueDamage}");
    return;
}

if (args is ["--augments", ..])
{
    var set = args.Contains("arena") ? AugmentSet.Arena : AugmentSet.Mayhem;
    var augmentData = new AugmentDataClient();
    var augments = await augmentData.LoadAsync(set, data.Items, cts.Token);
    if (Option("--locale") is { } locale)
    {
        augments = augments.WithLocalNames(await augmentData.LoadLocalNamesAsync(locale, cts.Token));
        Console.WriteLine($"{augments.All.Count(a => a.LocalNames.Count > 0)} of {augments.All.Count} have a {locale} name. {AugmentTranslations.Attribution}");
    }
    Console.WriteLine($"{augments.All.Count} {set} augments ({augments.Offerable.Count()} offerable). {AugmentDataClient.Attribution}\n");
    foreach (var a in augments.All)
    {
        var flags = string.Join(" ", new[] { a.IsDisabled ? "DISABLED" : "", a.IsQuest ? "quest" : "", a.HasDrawback ? "drawback" : "", a.IsRandom ? "random" : "" }.Where(f => f != ""));
        Console.WriteLine($"{a.Tier.ToString()[0]} {a.Name,-28}{(a.LocalNames.Count > 0 ? $" ({string.Join(" / ", a.LocalNames)})" : "")} gives [{a.Effects}] needs [{a.Triggers}]" +
            (a.MentionedItems.Count > 0 ? $" items [{string.Join(", ", a.MentionedItems.Select(i => i.Name))}]" : "") + (flags != "" ? $" {flags}" : ""));
    }
    return;
}

string? Option(string flag) => args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault();

if (args is ["--runes", var championName, ..])
{
    var champion = data.Champions.Find(championName) ?? throw new ArgumentException($"Unknown champion '{championName}'.");
    var position = Positions.Parse(Option("--position"));
    var mode = Option("--mode")?.ToLowerInvariant() switch { "aram" => GameMode.Aram, "mayhem" => GameMode.AramMayhem, _ => GameMode.SummonersRift };
    var enemies = Champions(Option("--enemies"));
    var request = new RuneRequest(champion, ParseStyle(Option("--style")) ?? Playstyles.Default(champion, position), position, mode) { Enemies = enemies };
    PrintRunes(request, await RuneAdvisorFor().RecommendAsync(request, RuneSource(), cts.Token));
    await PrintExtrasAsync(new ChampSelectState(champion, position, mode, [], enemies), request.Playstyle);
    return;
}

if (args is ["--history", ..])
{
    IReadOnlyList<PlayedGame> history = [];
    using (var client = LeagueClientApi.TryConnect())
    {
        if (client is null)
            Console.WriteLine("The League client isn't running, so only saved games count.");
        else
            history = await client.GetMatchHistoryAsync(games: 30, withDetails: 15, cts.Token);
    }
    var recapsPath = args.Length > 1 ? args[1]
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueClanker", "games.json");
    var recaps = new RecapStore(recapsPath).Games;
    Console.WriteLine($"{history.Count} games from the match history, {recaps.Count} saved by the app.\n");
    foreach (var game in history)
        Console.WriteLine($"  {game.Played:yyyy-MM-dd HH:mm}  {data.Champions.GetByKey(game.ChampionKey)?.Name,-12} {(game.Win ? "win " : "loss")}  "
                          + $"{game.Position.DisplayName(),-8} vs {(game.OpponentKey is { } o ? data.Champions.GetByKey(o)?.Name : "?")}");

    var stats = PersonalStats.Combine(recaps, history, data.Champions);
    Console.WriteLine($"\nYour champions ({stats.Games.Count} games):");
    foreach (var (key, record) in stats.Champions())
        Console.WriteLine($"  {data.Champions.GetByKey(key)?.Name,-12} {record,-8} {record.WinRate:P0}");
    var opponents = stats.Games.Where(g => g.OpponentKey is not null).Select(g => g.OpponentKey!.Value).Distinct()
        .Select(k => (Key: k, Record: stats.Against(k))).Where(o => o.Record.Games >= PersonalStats.MinGames).OrderByDescending(o => o.Record.Games);
    Console.WriteLine("\nLane opponents you've met 3 times or more:");
    foreach (var (key, record) in opponents)
        Console.WriteLine($"  {data.Champions.GetByKey(key)?.Name,-12} {record,-8} {record.WinRate:P0}");
    return;
}

if (args is ["--champselect", ..])
{
    using var client = LeagueClientApi.TryConnect();
    if (client is null)
    {
        Console.WriteLine("The League client isn't running.");
        return;
    }
    var state = await client.TryGetChampSelectAsync(cts.Token) is { } snapshot ? ChampSelectState.From(snapshot, data.Champions) : null;
    if (state?.Champion is not { } champion)
    {
        Console.WriteLine("Not in champ select, or you haven't picked or hovered a champion yet.");
        return;
    }

    var request = new RuneRequest(champion, ParseStyle(Option("--style")) ?? Playstyles.Default(champion, state.Position), state.Position, state.Mode)
    {
        Enemies = state.Enemies,
    };
    var recommendation = await RuneAdvisorFor().RecommendAsync(request, RuneSource(), cts.Token);
    PrintRunes(request, recommendation);
    await PrintExtrasAsync(state, request.Playstyle);
    PrintMatchup(await new MatchupAdvisor(opgg, data.Champions).AnalyzeAsync(
        new MatchupRequest(champion, state.IsLocked, state.Position, state.Mode, state.Enemies) { Pickable = state.Pickable.Count > 0 ? state.Pickable : null, Mastery = state.Mastery },
        cts.Token));
    PrintDraft(await new DraftAdvisor(opgg, data.Champions).AnalyzeAsync(
        new DraftRequest(champion, state.IsLocked, state.Position, state.Mode, state.Allies, state.Enemies)
        {
            HasPendingBan = state.HasPendingBan,
            Unavailable = state.Bans.Concat(state.Allies.Select(a => a.Key)).ToHashSet(),
            Pickable = state.Pickable.Count > 0 ? state.Pickable : null,
            Mastery = state.Mastery,
        },
        cts.Token));
    if (args.Contains("--apply"))
        Console.WriteLine((await new RunePageWriter(client).ApplyAsync(recommendation.Page, RunePageWriter.PageName(champion), cts.Token)).Message);
    return;
}

if (args is ["--matchup", var who, ..])
{
    var me = who == "-" ? null : data.Champions.Find(who) ?? throw new ArgumentException($"Unknown champion '{who}'.");
    var request = new MatchupRequest(me, IsLocked: me is not null && !args.Contains("--hover"), Positions.Parse(Option("--position")),
        GameMode.SummonersRift, Champions(Option("--enemies")));
    PrintMatchup(await new MatchupAdvisor(opgg, data.Champions).AnalyzeAsync(request, cts.Token));
    var allies = Champions(Option("--allies"));
    PrintDraft(await new DraftAdvisor(opgg, data.Champions).AnalyzeAsync(
        new DraftRequest(me, request.IsLocked, request.Position, request.Mode, allies, request.Enemies)
        {
            HasPendingBan = args.Contains("--ban"),
            Unavailable = allies.Select(a => a.Key).ToHashSet(),
        },
        cts.Token));
    return;
}

if (args is ["--mayhem", var gamePath, ..])
{
    string[] NamesAfter(string flag) =>
        args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault()?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];

    var rec = await RecommendAsync(gamePath) ?? throw new InvalidOperationException("Could not find the active player.");
    var augments = await new AugmentDataClient().LoadMayhemAsync(data.Items, cts.Token);
    AugmentInfo Resolve(string name) => augments.Find(name) ?? throw new ArgumentException($"Unknown augment '{name}'. Run --augments for the list.");

    var offer = NamesAfter("--offer").Select(Resolve).ToList();
    CommunityAugments? community = null;
    if (!args.Contains("--no-community"))
    {
        community = await new CommunityAugmentClient().LoadAsync(rec.Game.Me.Champion.Name, cts.Token);
        Console.WriteLine($"{community.Count} cards with win rates from {CommunityAugments.Source}. --no-community leaves them out.");
    }
    var ctx = new AugmentContext(rec.Game)
    {
        Picked = NamesAfter("--picked").Select(Resolve).ToList(),
        PlannedItems = rec.Items.Select(i => i.Item).ToList(),
        Situations = rec.Situations,
        Community = community,
    };

    var rerolls = new RerollState
    {
        Used = NamesAfter("--rerolled").Select(Resolve).ToDictionary(a => a, _ => 1),
        Golden = NamesAfter("--golden").Select(Resolve).FirstOrDefault(),
        RerollsPerCard = ctx.Picked.Count > 0 && ctx.Picked[^1].GrantsExtraRerolls ? 2 : 1,
    };
    var started = DateTime.UtcNow;
    var advice = new AugmentAdvisor(augments).Rank(offer, ctx, rerolls);
    var elapsed = DateTime.UtcNow - started;
    var me = rec.Game.Me;
    Console.WriteLine($"=== {me.Name} ({me.Archetype.DisplayName()}), {rec.Game.Mode.DisplayName()} ===");
    Console.WriteLine($"Picked: {(ctx.Picked.Count == 0 ? "nothing yet" : string.Join(", ", ctx.Picked))}");
    Console.WriteLine($"Items: {string.Join(", ", me.Items.Where(i => i.Kind is ItemKind.Legendary or ItemKind.Boots))} (planned: {string.Join(", ", ctx.PlannedItems.Take(3))})\n");
    var rank = 1;
    foreach (var option in advice.Ranked)
    {
        var reroll = advice.Reroll?.For(option.Augment);
        var action = reroll is null ? "" : $"  [{reroll.Action}, a reroll beats it {reroll.RerollBeatsIt:P0}]";
        Console.WriteLine($"{rank++}. {option.Augment.Name} ({option.Augment.Tier})  now {option.Now:0.0}  with future picks {option.Expected:0.0}{action}");
        foreach (var reason in option.Reasons.OrderByDescending(r => Math.Abs(r.Points)))
            Console.WriteLine($"     {(reason.Points < 0 ? "-" : "+")} {reason.Text}");
        if (option.Partners.Count > 0)
            Console.WriteLine($"     combos later: {string.Join(", ", option.Partners)}");
    }
    Console.WriteLine();
    if (advice.Reroll is { Text.Length: > 0 } rerollAdvice)
        Console.WriteLine(rerollAdvice.Text);
    Console.WriteLine($"{advice.Text}\n(ranked in {elapsed.TotalMilliseconds:0} ms) {AugmentDataClient.Attribution}");
    return;
}

if (args is ["--scan", var source, ..])
{
    var augmentData = new AugmentDataClient();
    var augments = await augmentData.LoadMayhemAsync(data.Items, cts.Token);
    var locale = Option("--locale");
    if (locale is not null)
        augments = augments.WithLocalNames(await augmentData.LoadLocalNamesAsync(locale, cts.Token));
    var reader = new AugmentScreenReader(augments, locale);
    Console.WriteLine($"OCR language: {reader.OcrLanguage}");

    var started = DateTime.UtcNow;
    var scan = source == "screen" ? await reader.ScanScreenAsync() : await reader.ScanFileAsync(source);
    Console.WriteLine($"Scanned in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms.");
    if (scan.Problem is not null)
        Console.WriteLine(scan.Problem);
    if (args.Contains("--verbose"))
        foreach (var line in scan.Lines)
            Console.WriteLine($"   text at ({line.X:0},{line.Y:0}): {line.Text}");

    Console.WriteLine(scan.Offer.Count == 0
        ? "No augment offer found."
        : $"Offer: {string.Join(", ", scan.Offer.Select(d => $"{d.Augment.Name} ({d.Augment.Tier}, {d.Confidence:P0})"))}");
    return;
}

var decline = args.Contains("--decline");
var pickedIndex = Array.IndexOf(args, "--picked");
var paths = args.Where((a, i) => a != "--decline" && (pickedIndex < 0 || (i != pickedIndex && i != pickedIndex + 1)))
    .SelectMany(a => Directory.Exists(a) ? Directory.GetFiles(a, "*.json").Order().ToArray() : new[] { a })
    .ToList();

if (paths.Count == 1)
{
    // --picked "A;B" adds ARAM: Mayhem augments you took, to see how they change the build.
    IReadOnlyList<AugmentInfo> picked = [];
    if (pickedIndex >= 0 && pickedIndex + 1 < args.Length)
    {
        var augments = await new AugmentDataClient().LoadMayhemAsync(data.Items, cts.Token);
        picked = args[pickedIndex + 1].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(n => augments.Find(n) ?? throw new ArgumentException($"Unknown augment '{n}'. Run --augments for the list."))
            .ToList();
    }
    Print(await RecommendAsync(paths[0], picked), (await new FileGameDataSource(paths[0]).TryGetAsync(cts.Token))?.ActivePlayer?.CurrentGold, data.Items);
    return;
}

if (paths.Count > 1)
{
    var planner = new BuildPlanner { Items = data.Items };
    foreach (var path in paths)
    {
        if (await RecommendAsync(path) is not { } rec)
            continue;

        planner.Update(rec);
        Console.WriteLine($"--- {Path.GetFileName(path)} @ {TimeSpan.FromSeconds(rec.Game.GameTimeSeconds):mm\\:ss}: {rec.DamageSummary}");
        Console.WriteLine($"    Your build: {string.Join(" > ", planner.Upcoming.Select(s => s.Item.Name))}");
        if (planner.PendingPivot is { } pivot)
        {
            Console.WriteLine($"    PIVOT SUGGESTED: {pivot.Summary} (+{pivot.Gain:0.0})");
            foreach (var reason in pivot.Reasons)
                Console.WriteLine($"      - {reason}");
            if (decline) planner.Decline(); else planner.Accept();
            Console.WriteLine($"    -> {(decline ? "declined" : "accepted")}. Your build: {string.Join(" > ", planner.Upcoming.Select(s => s.Item.Name))}");
        }
        else if (planner.CanSwitch)
        {
            Console.WriteLine($"    (latest suggestion differs: {string.Join(", ", planner.LatestDifferences.Select(i => i.Name))}; no pivot suggested)");
        }
    }
    return;
}

using var api = new LiveClientApi();
try
{
    await foreach (var update in new BuildAdvisor(api, data).RunAsync(TimeSpan.FromSeconds(2), cts.Token))
    {
        if (update.State == AdvisorState.WaitingForGame)
            Console.WriteLine("Waiting for a game... (start one, Practice Tool works too)");
        else
            Print(update.Recommendation, update.Gold, data.Items);
    }
}
catch (OperationCanceledException)
{
}

async Task<BuildRecommendation?> RecommendAsync(string path, IReadOnlyList<AugmentInfo>? augments = null)
{
    var game = await new FileGameDataSource(path).TryGetAsync(cts.Token)
        ?? throw new InvalidOperationException($"{path} does not contain a playable game.");
    var advisor = new BuildAdvisor(new FileGameDataSource(path), data) { Augments = augments ?? [] };

    // League Classic: Blitz's builds with the classic items.
    if (!args.Contains("--no-meta") && GameAnalyzer.Analyze(game, data) is { Mode: GameMode.LeagueClassic } classic)
    {
        advisor.MetaBuilds = await new LeagueClanker.Core.Blitz.BlitzClassicBuilds().LoadAsync(classic.Me.Champion.Key, classic.Me.Position, data.Items, cts.Token);
        foreach (var build in advisor.MetaBuilds)
            Console.WriteLine($"Blitz {build.Name}: {string.Join(", ", build.Core.Select(i => i.Name))} | later {string.Join(", ", build.Later.Select(i => i.Name))}");
    }

    // op.gg's builds for the champion, unless --no-meta. The same as the app does in a live game.
    if (!args.Contains("--no-meta") && GameAnalyzer.Analyze(game, data) is { Mode: GameMode.SummonersRift or GameMode.Aram or GameMode.AramMayhem } analysis)
    {
        try
        {
            var aram = analysis.Mode is GameMode.Aram or GameMode.AramMayhem;
            var champion = await opgg.GetChampionAsync(analysis.Me.Champion.Key, aram, OpggClient.RoleFor(analysis.Me.Position, analysis.Me.Archetype), cts.Token);
            advisor.MetaBuilds = champion is null ? [] : MetaBuilds.From(champion, data.Items, analysis.Mode);
            foreach (var build in advisor.MetaBuilds)
                Console.WriteLine($"op.gg {build.Name}: {string.Join(", ", build.Core.Select(i => i.Name))} | later {string.Join(", ", build.Later.Select(i => i.Name))} | {build.WinRate:P1} over {build.Games:N0} games");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"op.gg didn't answer ({ex.Message}), so the build comes from item scores alone.");
        }
    }
    return advisor.RecommendOnce(game);
}

RuneAdvisor RuneAdvisorFor() => new(new RuleRuneSource(data.Runes), new OpggRuneSource(data.Runes, opgg));

// Summoner spells, skill order and the shop item set, as champ select's Apply would write them.
async Task PrintExtrasAsync(ChampSelectState state, Archetype playstyle)
{
    var champion = state.Champion!;
    var spells = await new SpellAdvisor(data.Spells, opgg).RecommendAsync(new SpellRequest(champion, playstyle, state.Position, state.Mode, state.Spells), RuneSource(), cts.Token);
    if (spells is not null)
        Console.WriteLine($"  Spells: {data.Spells.Get(spells.First)?.Name} + {data.Spells.Get(spells.Second)?.Name} ({spells.Source})");

    var aram = state.Mode is GameMode.Aram or GameMode.AramMayhem;
    var stats = RuneSource() == RuneSourceKind.StatsSite ? await opgg.GetChampionAsync(champion.Key, aram, OpggClient.RoleFor(state.Position, playstyle), cts.Token) : null;
    if (stats?.SkillOrder is { } skills)
        Console.WriteLine($"  Skills: max {string.Join(" > ", skills.MaxOrder)}, levels 1-6 {string.Join(" ", skills.Levels.Take(6))}");

    if (state.ToGameData() is { } game && GameAnalyzer.Analyze(game, data, playstyle: playstyle) is { } analysis)
    {
        var set = ItemSetBuilder.Build(new RecommendationEngine(data).Recommend(analysis), data.Items, stats);
        Console.WriteLine($"  Item set \"{set.Title}\":");
        foreach (var block in set.Blocks)
            Console.WriteLine($"    {block.Title}: {string.Join(", ", block.Items.Select(i => (i.Count > 1 ? $"{i.Count}x " : "") + data.Items.Get(i.Id)?.Name))}");
    }
    Console.WriteLine();
}

List<ChampionInfo> Champions(string? names) =>
    (names ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(n => data.Champions.Find(n) ?? throw new ArgumentException($"Unknown champion '{n}'.")).ToList();

static void PrintDraft(DraftReport? report)
{
    if (report is null)
        return;

    Console.WriteLine("=== Draft ===");
    foreach (var ban in report.Bans)
        Console.WriteLine($"  Ban {ban.Champion.Name}: {ban.Reason}");
    foreach (var warning in report.Warnings)
        Console.WriteLine($"  ! {warning}");
    if (report.EnemySummary is not null)
        Console.WriteLine($"  {report.EnemySummary}");
    if (report.FillPicks.Count > 0)
    {
        Console.WriteLine($"  {report.FillHeader}:");
        foreach (var pick in report.FillPicks)
            Console.WriteLine($"    {pick.Champion.Name,-14} {pick.WinRate,6:P1}{(pick.YouPlayIt ? "  (you play this)" : "")}");
    }
    if (report.Note is not null)
        Console.WriteLine($"  {report.Note}");
    if (report is { Bans.Count: 0, Warnings.Count: 0, FillPicks.Count: 0, EnemySummary: null, Note: null })
        Console.WriteLine("  Nothing to flag.");
    Console.WriteLine();
}

static void PrintMatchup(MatchupReport? report)
{
    if (report is null)
    {
        Console.WriteLine("No lane matchup: that needs Summoner's Rift and a role.\n");
        return;
    }

    Console.WriteLine($"=== Lane: {report.Position.DisplayName()} ===");
    if (report.EnemyRoles.Count > 0)
        Console.WriteLine($"  Enemy roles{(report.RolesAreGuessed ? " (guessed)" : "")}: "
                          + string.Join(", ", report.EnemyRoles.Select(r => $"{r.Champion.Name} {r.Position.DisplayName().ToLowerInvariant()} ({r.Likelihood:P0})")));
    Console.WriteLine(report.Opponent is null
        ? $"  Your lane opponent hasn't picked yet."
        : $"  Lane opponent: {report.Opponent.Name}{(report.Partner is null ? "" : $" with {report.Partner.Name}")}");
    if (report.Matchup is { } m)
        Console.WriteLine($"  vs {m.Opponent.Name}: {m.WinRate:P1} win rate over {m.Games:N0} games ({m.Verdict})");
    if (report.CounterPicks.Count > 0)
    {
        Console.WriteLine($"  Good picks vs {report.Opponent!.Name}:");
        foreach (var pick in report.CounterPicks)
            Console.WriteLine($"    {pick.Champion.Name,-14} {pick.WinRate,6:P1}  {pick.Games,7:N0} games{(pick.YouPlayIt ? $"  (you play this: {pick.MasteryPoints:N0} mastery)" : "")}");
    }
    if (report.Note is not null)
        Console.WriteLine($"  {report.Note}");
    Console.WriteLine();
}

RuneSourceKind RuneSource() => Option("--source") == "rules" ? RuneSourceKind.OwnRules : RuneSourceKind.StatsSite;

static Archetype? ParseStyle(string? style) =>
    style is null ? null : Playstyles.All.FirstOrDefault(a => Compact(a.DisplayName()).Equals(Compact(style), StringComparison.OrdinalIgnoreCase));

// "AP assassin", "apassassin", "on-hit" and "onhit" all match.
static string Compact(string text) => text.Replace(" ", "").Replace("-", "");

void PrintRunes(RuneRequest request, RuneRecommendation rec)
{
    string Name(int id) => data.Runes.Get(id)?.Name ?? id.ToString();
    var page = rec.Page;
    Console.WriteLine($"=== {request.Champion.Name}, {request.Playstyle.DisplayName()}, {request.Position.DisplayName()}, {request.Mode.DisplayName()} ===");
    Console.WriteLine($"  {data.Runes.Style(page.PrimaryStyleId)?.Name}: {string.Join(", ", page.PrimaryRunes.Select(Name))}");
    Console.WriteLine($"  {data.Runes.Style(page.SubStyleId)?.Name}: {string.Join(", ", page.SecondaryRunes.Select(Name))}");
    Console.WriteLine($"  Shards: {string.Join(", ", page.Shards.Select(Name))}");
    Console.WriteLine(rec.Games is { } games ? $"  Source: {rec.Source}, {rec.WinRate:P1} win rate over {games:N0} games" : $"  Source: {rec.Source}");
    foreach (var reason in rec.Reasons)
        Console.WriteLine($"  - {reason}");
    if (data.Runes.Validate(page) is { } problem)
        Console.WriteLine($"  INVALID: {problem}");
    Console.WriteLine();
}

static void Print(BuildRecommendation? rec, double? gold = null, ItemCatalog? items = null)
{
    if (rec is null)
    {
        Console.WriteLine("Could not find the active player in this game.");
        return;
    }

    var me = rec.Game.Me;
    Console.WriteLine($"=== {me.Name} ({me.Archetype.DisplayName()}) @ {TimeSpan.FromSeconds(rec.Game.GameTimeSeconds):mm\\:ss}, {rec.Game.Mode.DisplayName()} ===");
    Console.WriteLine(rec.DamageSummary);
    PrintTeam("Your team", [me, .. rec.Game.Allies.Players]);
    PrintTeam("Enemies", rec.Game.Enemies.Players);

    if (rec.Meta is { } meta)
    {
        Console.WriteLine($"\nBuild: {meta.Text}");
        foreach (var (other, score) in meta.Others)
            Console.WriteLine($"  (score {meta.Score:0.0} against {score:0.0} for the {other.Name})");
        if (meta.Swap is not null)
            Console.WriteLine($"  Swapped for this game: {meta.Swap}");
    }

    Console.WriteLine("\nBuy next (most important first):");
    var rank = 1;
    foreach (var item in rec.Items)
        Console.WriteLine($"{rank++,3}. {item.Item.Name,-28} {item.Item.TotalGold,5}g  score {item.Total,4:0.0}  {Reasons(item)}");

    if (rec.Boots is { } boots)
        Console.WriteLine($"  Boots: {boots.Item.Name,-28} {boots.Item.TotalGold,5}g  {Reasons(boots)}");

    if (gold is { } g && items is not null)
    {
        if (BuyAdvisor.Advise(rec.Items.FirstOrDefault()?.Item, me.Items, g, items) is { } buy)
            Console.WriteLine($"\nBuy now: {buy.Text}");
        foreach (var tip in LateGameAdvisor.Advise(rec, g, items))
            Console.WriteLine($"Tip: {tip}");
    }

    Console.WriteLine("\nWhy:");
    if (rec.Advice.Count == 0)
        Console.WriteLine("  Nothing unusual about this game, so follow your standard build.");
    foreach (var advice in rec.Advice)
        Console.WriteLine($"  - {advice.Text}");
    Console.WriteLine();
}

static void PrintTeam(string title, IEnumerable<PlayerProfile> players)
{
    Console.WriteLine($"\n  {title,-22} lvl    AD    AP  armor   MR     HP   AS/s  crit  leth  %pen");
    foreach (var p in players)
    {
        var s = p.Stats;
        var tag = s.IsReal ? " (real)" : p.IsTanky ? " tanky" : p.IsSquishy ? " squishy" : "";
        Console.WriteLine($"  {p.Name + tag,-22} {p.Level,3} {s.AttackDamage,5:0} {s.AbilityPower,5:0} {s.Armor,6:0} {s.MagicResist,4:0} {s.Health,6:0} {s.AttackSpeed,6:0.00} {s.CritChance,4:0}% {s.Lethality,5:0} {Math.Max(s.ArmorPenPercent, s.MagicPenPercent),4:0}%");
    }
}

static string Reasons(ScoredItem item) =>
    string.Join("  ", item.Reasons.Select(r => $"[{r.Situation.Label} +{r.Points:0.0}]").Concat(item.Effects.Select(e => $"[{e}]")));
