using System.Text.Json;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

/// <summary>A small synthetic patch: a handful of items and champions, parsed through the real Data Dragon parsers.</summary>
internal static class TestData
{
    public const int Plate = 3001;      // Armor + Health
    public const int Cloak = 3002;      // Magic Resist + Health
    public const int Veil = 3003;       // Magic Resist + Health, a second MR option
    public const int Cleaver = 3004;    // AD bruiser item
    public const int WoundBlade = 3005; // AD bruiser item with anti-heal
    public const int PenBow = 3006;     // Crit + % armor penetration
    public const int LethBlade = 3007;  // Crit + lethality
    public const int CritSword = 3008;
    public const int Heart = 3009;       // Health only
    public const int GiantSlayer = 3010; // Crit + bonus damage based on the target's health
    public const int ArmorBoots = 3047;
    public const int MagicBoots = 3111;
    public const int WoundComponent = 3123;

    public static readonly StaticGameData Static = new("test", BuildItems(), BuildChampions());

    private static ItemCatalog BuildItems()
    {
        var items = new Dictionary<string, object>
        {
            ["1001"] = Item("Boots", 300, Stats(("Move Speed", "25")), tags: ["Boots"], into: ["3047", "3111"]),
            [$"{Plate}"] = Item("Plate", 2800, Stats(("Armor", "60"), ("Health", "400"))),
            [$"{Cloak}"] = Item("Cloak", 2800, Stats(("Magic Resist", "60"), ("Health", "400"))),
            [$"{Veil}"] = Item("Veil", 2900, Stats(("Magic Resist", "55"), ("Health", "450"))),
            [$"{Cleaver}"] = Item("Cleaver", 3000, Stats(("Attack Damage", "50"), ("Health", "350"), ("Ability Haste", "20"))),
            [$"{WoundBlade}"] = Item("Wound Blade", 3000,
                Stats(("Attack Damage", "45"), ("Health", "350"), ("Ability Haste", "15")) + "<passive>Rend</passive><br>Dealing physical damage applies <keyword>40% Wounds</keyword>.",
                from: [$"{WoundComponent}"]),
            [$"{PenBow}"] = Item("Pen Bow", 3300, Stats(("Attack Damage", "35"), ("35%", "Armor Penetration"), ("25%", "Critical Strike Chance"))),
            [$"{LethBlade}"] = Item("Leth Blade", 3100, Stats(("Attack Damage", "70"), ("Lethality", "18"), ("20%", "Critical Strike Chance"))),
            [$"{CritSword}"] = Item("Crit Sword", 3400, Stats(("Attack Damage", "65"), ("25%", "Critical Strike Chance"))),
            [$"{Heart}"] = Item("Heart", 3000, Stats(("Health", "900"))),
            [$"{GiantSlayer}"] = Item("Giant Slayer", 3100,
                Stats(("Attack Damage", "40"), ("25%", "Critical Strike Chance"), ("15%", "Armor Penetration"))
                + "<passive>Giant Slayer</passive><br>Deal up to 15% bonus damage against champions based on their bonus Health."),
            [$"{ArmorBoots}"] = Item("Armor Boots", 1200, Stats(("Armor", "25"), ("Move Speed", "45")), tags: ["Boots"], from: ["1001"]),
            [$"{MagicBoots}"] = Item("Magic Boots", 1250, Stats(("Magic Resist", "25"), ("Move Speed", "45"), ("30%", "Tenacity")), tags: ["Boots"], from: ["1001"]),
            [$"{WoundComponent}"] = Item("Wound Dagger", 800,
                Stats(("Attack Damage", "15")) + "<passive>Rend</passive><br>Applies <keyword>40% Wounds</keyword>.", into: [$"{WoundBlade}"]),
            ["223001"] = Item("Plate (Arena copy)", 2800, Stats(("Armor", "60"))),
        };
        return ItemCatalog.Parse(JsonSerializer.Serialize(new { data = items }));
    }

    private static ChampionCatalog BuildChampions() => new(
    [
        new("Garen", "Garen", ["Fighter", "Tank"], 7, 7, 1),
        new("Jinx", "Jinx", ["Marksman"], 9, 2, 4),
        new("Caitlyn", "Caitlyn", ["Marksman"], 8, 2, 2),
        new("Zed", "Zed", ["Assassin"], 9, 2, 1),
        new("Annie", "Annie", ["Mage", "Support"], 2, 3, 10),
        new("Syndra", "Syndra", ["Mage"], 2, 3, 9),
        new("Lux", "Lux", ["Mage", "Support"], 2, 4, 9),
        new("Brand", "Brand", ["Mage"], 2, 2, 9),
        new("Xerath", "Xerath", ["Mage"], 1, 3, 10),
        new("Ornn", "Ornn", ["Tank"], 5, 9, 3, new ChampionStats
        {
            Health = 660, HealthPerLevel = 109, Armor = 33, ArmorPerLevel = 5.2, MagicResist = 32, MagicResistPerLevel = 2.05,
        }),
        new("Sejuani", "Sejuani", ["Tank"], 5, 7, 6),
        new("Braum", "Braum", ["Tank", "Support"], 3, 9, 4),
        new("Soraka", "Soraka", ["Support", "Mage"], 2, 5, 7),
        new("Aatrox", "Aatrox", ["Fighter"], 8, 4, 3),
        new("Vladimir", "Vladimir", ["Mage"], 2, 6, 8),
        new("Thresh", "Thresh", ["Support", "Tank"], 5, 6, 6),
    ]);

    /// <summary>Builds a live game: the first ally is "me".</summary>
    public static AllGameData Game(IEnumerable<(string Champion, int[] Items)> allies, IEnumerable<(string Champion, int[] Items)> enemies)
    {
        var players = allies.Select((p, i) => Player(p.Champion, "ORDER", i == 0 ? "Me" : $"Ally{i}", p.Items))
            .Concat(enemies.Select((p, i) => Player(p.Champion, "CHAOS", $"Enemy{i}", p.Items)))
            .ToList();
        return new AllGameData
        {
            ActivePlayer = new ActivePlayer { RiotId = "Me#TEST" },
            AllPlayers = players,
            GameData = new LiveGameInfo { GameTime = 900 },
        };
    }

    private static LivePlayer Player(string champion, string team, string name, int[] items) => new()
    {
        ChampionName = champion,
        RawChampionName = $"game_character_displayname_{champion}",
        RiotId = $"{name}#TEST",
        Team = team,
        Items = items.Select((id, slot) => new LiveItem { ItemID = id, Slot = slot }).ToList(),
    };

    private static string Stats(params (string A, string B)[] lines)
    {
        // ("Armor", "60") → "60 Armor"; ("35%", "Armor Penetration") → "35% Armor Penetration"
        var parts = lines.Select(l => char.IsDigit(l.A[0])
            ? $"<attention>{l.A}</attention> {l.B}"
            : $"<attention>{l.B}</attention> {l.A}");
        return $"<mainText><stats>{string.Join("<br>", parts)}</stats><br><br>";
    }

    private static object Item(string name, int gold, string description, string[]? tags = null, string[]? from = null, string[]? into = null) => new
    {
        name,
        description = description + "</mainText>",
        gold = new { total = gold, purchasable = true },
        maps = new Dictionary<string, bool> { ["11"] = true },
        tags = tags ?? [],
        from = from ?? [],
        into = into ?? [],
    };
}
