using LeagueClanker.Core;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

// Usage:
//   LeagueClanker.Cli                         watch the live game and print a new build whenever it changes
//   LeagueClanker.Cli <game.json>             analyze a saved allgamedata snapshot once
//   LeagueClanker.Cli <folder | a.json b.json> [--decline]
//                                             replay snapshots through the pivot planner, accepting pivots (or declining)
//   LeagueClanker.Cli --items                 list the item catalog with detected traits (for tuning rules)

Console.OutputEncoding = System.Text.Encoding.UTF8;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("Loading Data Dragon...");
var data = await new DataDragonClient().LoadAsync(cts.Token);
Console.WriteLine($"Patch {data.Version}: {data.Items.Legendaries.Count} legendary items, {data.Items.Boots.Count} boots.\n");

if (args is ["--items"])
{
    foreach (var item in data.Items.Legendaries.Concat(data.Items.Boots))
        Console.WriteLine($"{item.Id,5} {item.Name,-30} {item.TotalGold,5}g  {item.Traits,-40} {string.Join(", ", item.Stats.Select(s => $"{s.Key} {s.Value}"))}");
    return;
}

if (args is ["--augments"])
{
    var augments = await new AugmentDataClient().LoadMayhemAsync(data.Items, cts.Token);
    Console.WriteLine($"{augments.All.Count} Mayhem augments ({augments.Offerable.Count()} offerable). {AugmentDataClient.Attribution}\n");
    foreach (var a in augments.All)
    {
        var flags = string.Join(" ", new[] { a.IsDisabled ? "DISABLED" : "", a.IsQuest ? "quest" : "", a.HasDrawback ? "drawback" : "", a.IsRandom ? "random" : "" }.Where(f => f != ""));
        Console.WriteLine($"{a.Tier.ToString()[0]} {a.Name,-28} gives [{a.Effects}] needs [{a.Triggers}]" +
            (a.MentionedItems.Count > 0 ? $" items [{string.Join(", ", a.MentionedItems.Select(i => i.Name))}]" : "") + (flags != "" ? $" {flags}" : ""));
    }
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
    var ctx = new AugmentContext(rec.Game)
    {
        Picked = NamesAfter("--picked").Select(Resolve).ToList(),
        PlannedItems = rec.Items.Select(i => i.Item).ToList(),
        Situations = rec.Situations,
    };

    var advice = new AugmentAdvisor(augments).Rank(offer, ctx);
    var me = rec.Game.Me;
    Console.WriteLine($"=== {me.Name} ({me.Archetype.DisplayName()}), {rec.Game.Mode.DisplayName()} ===");
    Console.WriteLine($"Picked: {(ctx.Picked.Count == 0 ? "nothing yet" : string.Join(", ", ctx.Picked))}");
    Console.WriteLine($"Items: {string.Join(", ", me.Items.Where(i => i.Kind is ItemKind.Legendary or ItemKind.Boots))} (planned: {string.Join(", ", ctx.PlannedItems.Take(3))})\n");
    var rank = 1;
    foreach (var option in advice.Ranked)
    {
        Console.WriteLine($"{rank++}. {option.Augment.Name} ({option.Augment.Tier})  now {option.Now:0.0}  with future picks {option.Expected:0.0}");
        foreach (var reason in option.Reasons.OrderByDescending(r => Math.Abs(r.Points)))
            Console.WriteLine($"     {(reason.Points < 0 ? "-" : "+")} {reason.Text}");
        if (option.Partners.Count > 0)
            Console.WriteLine($"     combos later: {string.Join(", ", option.Partners)}");
    }
    Console.WriteLine($"\n{advice.Text}\n{AugmentDataClient.Attribution}");
    return;
}

var decline = args.Contains("--decline");
var paths = args.Where(a => a != "--decline")
    .SelectMany(a => Directory.Exists(a) ? Directory.GetFiles(a, "*.json").Order().ToArray() : new[] { a })
    .ToList();

if (paths.Count == 1)
{
    Print(await RecommendAsync(paths[0]));
    return;
}

if (paths.Count > 1)
{
    var planner = new BuildPlanner();
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
            Print(update.Recommendation);
    }
}
catch (OperationCanceledException)
{
}

async Task<BuildRecommendation?> RecommendAsync(string path)
{
    var game = await new FileGameDataSource(path).TryGetAsync(cts.Token)
        ?? throw new InvalidOperationException($"{path} does not contain a playable game.");
    return new BuildAdvisor(new FileGameDataSource(path), data).RecommendOnce(game);
}

static void Print(BuildRecommendation? rec)
{
    if (rec is null)
    {
        Console.WriteLine("Could not find the active player in this game.");
        return;
    }

    var me = rec.Game.Me;
    Console.WriteLine($"=== {me.Name} ({me.Archetype.DisplayName()}) @ {TimeSpan.FromSeconds(rec.Game.GameTimeSeconds):mm\\:ss} ===");
    Console.WriteLine(rec.DamageSummary);
    PrintTeam("Your team", [me, .. rec.Game.Allies.Players]);
    PrintTeam("Enemies", rec.Game.Enemies.Players);

    Console.WriteLine("\nBuy next (most important first):");
    var rank = 1;
    foreach (var item in rec.Items)
        Console.WriteLine($"{rank++,3}. {item.Item.Name,-28} {item.Item.TotalGold,5}g  score {item.Total,4:0.0}  {Reasons(item)}");

    if (rec.Boots is { } boots)
        Console.WriteLine($"  Boots: {boots.Item.Name,-28} {boots.Item.TotalGold,5}g  {Reasons(boots)}");

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
    string.Join("  ", item.Reasons.Select(r => $"[{r.Situation.Label} +{r.Points:0.0}]"));
