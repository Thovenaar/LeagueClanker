using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Recommendation;

/// <param name="Items">What to buy now, biggest parts first. Empty when you should save up.</param>
public sealed record BuyAdvice(string Text, IReadOnlyList<ItemInfo> Items, int Cost);

/// <summary>
/// What to buy with the gold you have, toward the next item of your build. The shop only charges for the parts you don't
/// own yet, so a finished item can be cheaper than its price. Otherwise it picks the biggest parts that fit your gold.
/// </summary>
public static class BuyAdvisor
{
    private sealed record Part(ItemInfo Item, bool Owned, IReadOnlyList<Part> Parts)
    {
        // What the shop charges: the item's own combine cost plus every part you don't own yet.
        public int Cost => Owned ? 0 : Item.TotalGold - Parts.Sum(p => p.Item.TotalGold) + Parts.Sum(p => p.Cost);
    }

    /// <param name="next">The next item of your build.</param>
    /// <param name="owned">Everything in your inventory, parts included.</param>
    public static BuyAdvice? Advise(ItemInfo? next, IReadOnlyList<ItemInfo> owned, double gold, ItemCatalog items)
    {
        if (next is null)
            return null;

        var budget = (int)gold;
        var pool = owned.Select(i => i.Id).ToList();
        var tree = Resolve(next, pool, items);
        if (tree.Cost <= budget)
            return new BuyAdvice($"Buy {next.Name} now: {tree.Cost:N0}g with the parts you have.", [next], tree.Cost);

        // Spend as much of your gold as possible, and on a tie buy fewer, bigger parts.
        var best = Choices(tree).Where(c => c.Cost <= budget).OrderByDescending(c => c.Cost).ThenBy(c => c.Items.Count).First();

        var goal = $"toward {next.Name} ({tree.Cost:N0}g left)";
        if (best.Items.Count == 0)
        {
            var cheapest = Leaves(tree).Where(p => !p.Owned).MinBy(p => p.Cost);
            if (cheapest is null)
                return null;
            // Items without parts (Arena sells finished items outright) are their own cheapest part.
            var text = cheapest.Item.Id == next.Id
                ? $"Save up: {cheapest.Cost - budget:N0}g more for {next.Name} ({tree.Cost:N0}g)."
                : $"Save up: {cheapest.Cost - budget:N0}g more for {cheapest.Item.Name}, {goal}.";
            return new BuyAdvice(text, [], 0);
        }

        var buy = best.Items.OrderByDescending(i => Cost(i, tree)).ToList();
        return new BuyAdvice($"{budget:N0} gold: buy {Join(buy.Select(i => i.Name))} ({best.Cost:N0}g) {goal}.", buy, best.Cost);
    }

    private const int MaxChoices = 5000;

    // Every way to buy some of the item's missing parts: nothing, a whole part, or a mix of its parts' parts. Recipes are
    // at most three levels deep with a few parts each, so this stays small.
    private static List<(int Cost, IReadOnlyList<ItemInfo> Items)> Choices(Part part)
    {
        var combined = new List<(int Cost, IReadOnlyList<ItemInfo> Items)> { (0, []) };
        foreach (var child in part.Parts.Where(p => !p.Owned))
        {
            var options = Choices(child);
            options.Add((child.Cost, [child.Item]));
            combined = combined.SelectMany(c => options.Select(o => (c.Cost + o.Cost, (IReadOnlyList<ItemInfo>)[.. c.Items, .. o.Items])))
                .Take(MaxChoices)
                .ToList();
        }
        return combined;
    }

    private static int Cost(ItemInfo item, Part tree) =>
        tree.Item.Id == item.Id ? tree.Cost : tree.Parts.Select(p => Cost(item, p)).DefaultIfEmpty(0).Max();

    // Owned parts are used up as they're matched, so two Long Swords in your bag cover two in the recipe.
    private static Part Resolve(ItemInfo item, List<int> pool, ItemCatalog items)
    {
        if (pool.Remove(item.Id))
            return new Part(item, true, []);
        var parts = item.BuildsFrom.Select(items.Get).OfType<ItemInfo>().Select(p => Resolve(p, pool, items)).ToList();
        return new Part(item, false, parts);
    }

    private static IEnumerable<Part> Leaves(Part part) => part.Parts.Count == 0 ? [part] : part.Parts.SelectMany(Leaves);

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 1 ? string.Join("", list) : $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
    }
}

/// <summary>Tips once your build is full or the game runs long: swaps, elixirs and control wards.</summary>
public static class LateGameAdvisor
{
    public const int ElixirOfIron = 2138;
    public const int ElixirOfSorcery = 2139;
    public const int ElixirOfWrath = 2140;
    public const int ControlWard = 2055;

    private const int FullBuild = 6;
    private const double SwapMargin = 1.0;
    private static readonly TimeSpan ElixirTime = TimeSpan.FromMinutes(25);

    public static IReadOnlyList<string> Advise(BuildRecommendation rec, double gold, ItemCatalog items)
    {
        var tips = new List<string>();
        var me = rec.Game.Me;
        var finished = me.Items.Count(i => i.Kind is ItemKind.Legendary or ItemKind.Boots);
        var full = finished >= FullBuild;

        // A full build can still improve: swap your weakest item when a new one scores clearly higher for this game.
        if (full && rec.Owned.MinBy(s => s.Total) is { } weakest && rec.Ranked.FirstOrDefault() is { } best && best.Total - weakest.Total >= SwapMargin)
        {
            var why = best.Reasons.FirstOrDefault() is { } reason ? $" ({reason.Situation.Label})" : "";
            tips.Add($"Swap {weakest.Item.Name} for {best.Item.Name}{why}: it fits this game better now.");
        }

        if (full && TimeSpan.FromSeconds(rec.Game.GameTimeSeconds) >= ElixirTime && gold >= 500)
        {
            var elixir = me.Archetype switch
            {
                Archetype.Tank or Archetype.Bruiser => ElixirOfIron,
                Archetype.Marksman or Archetype.AdAssassin => ElixirOfWrath,
                _ => ElixirOfSorcery,
            };
            if (items.Get(elixir) is { } item)
                tips.Add($"Your build is done: buy {item.Name} (500g) before fights.");
        }

        if (!me.AllItemIds.Contains(ControlWard) && gold >= 75 && (me.Position is Position.Support or Position.Jungle || full))
            tips.Add("Carry a Control Ward (75g) for vision around objectives.");
        return tips;
    }
}
