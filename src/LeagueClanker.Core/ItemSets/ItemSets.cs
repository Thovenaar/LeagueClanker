using System.Text.Json.Nodes;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.ItemSets;

public sealed record ItemSetEntry(int Id, int Count = 1);

public sealed record ItemSetBlock(string Title, IReadOnlyList<ItemSetEntry> Items);

/// <summary>An item set for the in-game shop: the blocks you see under "Recommended" when you press P.</summary>
public sealed record ItemSetDefinition(string Title, int ChampionKey, int MapId, IReadOnlyList<ItemSetBlock> Blocks);

public static class ItemSetBuilder
{
    public const string TitlePrefix = "LeagueClanker";
    private const int SituationalItems = 5;

    private static readonly int[] Potions = [2003, 2003];

    /// <summary>
    /// Builds the shop set from a recommendation made before the game (see <c>ChampSelectState.ToGameData</c>):
    /// starting items, the core build, boots, situational items and, when available, op.gg's most played core.
    /// </summary>
    public static ItemSetDefinition Build(BuildRecommendation rec, ItemCatalog items, OpggChampion? opgg)
    {
        var me = rec.Game.Me;
        var blocks = new List<ItemSetBlock>();
        void Add(string title, IEnumerable<ItemSetEntry> entries)
        {
            var known = entries.Where(e => items.Get(e.Id) is not null).ToList();
            if (known.Count > 0)
                blocks.Add(new ItemSetBlock(title, known));
        }

        if (rec.Game.Mode is not (GameMode.Aram or GameMode.AramMayhem))
            Add("Starting items", StartingItems(me, opgg, rec.Game.Mode));

        var core = rec.Items.Select(i => i.Item.Id).ToList();
        Add($"{TitlePrefix}: core build, most important first", core.Select(id => new ItemSetEntry(id)));

        var boots = new[] { rec.Boots?.Item.Id }.OfType<int>()
            .Concat(opgg?.Boots.Take(2).SelectMany(b => b.Ids) ?? [])
            .Distinct();
        Add("Boots", boots.Select(id => new ItemSetEntry(id)));

        var situational = rec.Advice.SelectMany(a => new[] { a.Suggested.Id, a.Alternative?.Id }).OfType<int>()
            .Concat(rec.Ranked.Skip(core.Count).Select(s => s.Item.Id))
            .Where(id => !core.Contains(id) && items.Get(id)?.Kind == ItemKind.Legendary)
            .Distinct()
            .Take(SituationalItems);
        Add("Situational", situational.Select(id => new ItemSetEntry(id)));

        if (opgg?.CoreItems.FirstOrDefault() is { } popular)
            Add($"Most played on op.gg ({popular.WinRate:P0} win rate)", popular.Ids.Select(id => new ItemSetEntry(id)));

        return new ItemSetDefinition($"{TitlePrefix} {me.Name}", me.Champion.Key, rec.Game.Mode.MapId(), blocks);
    }

    /// <summary>op.gg's most played start when there is one. Otherwise a Doran's item, a jungle pet or the support item.</summary>
    public static IEnumerable<ItemSetEntry> StartingItems(PlayerProfile me, OpggChampion? opgg, GameMode mode)
    {
        if (mode == GameMode.SummonersRift && opgg?.StarterItems.FirstOrDefault() is { } start)
            return start.Ids.GroupBy(id => id).Select(g => new ItemSetEntry(g.Key, g.Count()));

        int[] ids = me.HasSmite ? [1101, 1102, 1103]
            : me.Position == Position.Support ? [3865]
            : me.Archetype switch
            {
                Archetype.Mage or Archetype.ApAssassin or Archetype.Enchanter or Archetype.ApBruiser => [1056],
                Archetype.Tank => [1054],
                _ => [1055],
            };
        return ids.Concat(Potions).GroupBy(id => id).Select(g => new ItemSetEntry(g.Key, g.Count()));
    }

    /// <summary>
    /// Adds <paramref name="set"/> to the client's item sets (the JSON from /lol-item-sets), replacing a set LeagueClanker
    /// wrote for the same champion before. Your own sets, and everything the client stores with them, stay as they are.
    /// </summary>
    public static JsonNode Merge(JsonNode existing, ItemSetDefinition set)
    {
        var root = existing.DeepClone();
        if (root["itemSets"] is not JsonArray sets)
            root["itemSets"] = sets = [];

        foreach (var old in sets.Where(IsOurs).ToList())
            sets.Remove(old);
        sets.Add(ToJson(set));
        return root;

        bool IsOurs(JsonNode? node) =>
            node?["title"]?.GetValue<string>()?.StartsWith(TitlePrefix, StringComparison.Ordinal) == true
            && node["associatedChampions"] is JsonArray champions && champions.Any(c => c?.GetValue<int>() == set.ChampionKey);
    }

    private static JsonObject ToJson(ItemSetDefinition set) => new()
    {
        ["title"] = set.Title,
        ["type"] = "custom",
        ["map"] = "any",
        ["mode"] = "any",
        ["sortrank"] = 0,
        ["startedFrom"] = "blank",
        ["uid"] = Guid.NewGuid().ToString(),
        ["associatedChampions"] = new JsonArray(set.ChampionKey),
        ["associatedMaps"] = new JsonArray(set.MapId),
        ["preferredItemSlots"] = new JsonArray(),
        ["blocks"] = new JsonArray(set.Blocks.Select(b => (JsonNode)new JsonObject
        {
            ["type"] = b.Title,
            ["items"] = new JsonArray(b.Items.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id.ToString(), ["count"] = i.Count }).ToArray()),
        }).ToArray()),
    };
}
