using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class LeagueClassicTests
{
    private const int StandardPlate = 3002;
    private const int ClassicBoots = 771001;
    private const int IonianBoots = 773158;
    private const int DoransBlade = 771055;
    private const int LongSword = 771036;
    private const int LastWhisper = 773035;
    private const int BlackCleaver = 773071;
    private const int VoidStaff = 773135;
    private const int Mejais = 773041;
    private const int SwordOfTheOccult = 773141;
    private const int Shurelyas = 773069;
    private const int AncientGolem = 773207;
    private const int InfinityEdge = 773031;
    private const int Sunfire = 773068;

    // Descriptions trimmed from Data Dragon 16.19.
    private static readonly ItemCatalog Items = ItemCatalog.Parse(JsonSerializer.Serialize(new
    {
        data = new Dictionary<string, object>
        {
            ["1001"] = Item("Boots", 300, "<stats></stats><br><br>", tags: ["Boots"], into: ["3047"], maps: ["11", "12"]),
            ["3047"] = Item("Plated Steelcaps", 1200, "<stats><attention>25</attention> Armor</stats>", tags: ["Boots"], from: ["1001"], maps: ["11", "12"]),
            // A standard item that is also flagged for map 453. League Classic still shouldn't recommend it.
            [$"{StandardPlate}"] = Item("Plate", 2800, "<stats><attention>60</attention> Armor<br><attention>400</attention> Health</stats>", maps: ["11", "12", "453"]),

            [$"{ClassicBoots}"] = Item("Boots of Speed", 300,
                "<stats></stats>Limited to 1.<br><br><jadeUnique>Enhanced Movement:</jadeUnique> <speed>25 Move Speed</speed>", tags: ["Boots"], into: [$"{IonianBoots}"]),
            [$"{IonianBoots}"] = Item("Ionian Boots of Lucidity", 1000,
                "<stats></stats><br><br><jadeUnique>Light Step:</jadeUnique>15% Cooldown Reduction<br><jadeUnique>Enhanced Movement:</jadeUnique> <speed>45 Move Speed</speed>",
                tags: ["Boots"], from: [$"{ClassicBoots}"]),
            [$"{DoransBlade}"] = Item("Doran's Blade", 475,
                "<stats><attention>80</attention> Health<br><attention>8</attention> Attack Damage</stats><br><br><jadeUnique>Passive:</jadeUnique> Basic Attacks restore 3 Health."),
            [$"{LongSword}"] = Item("Long Sword", 400, "<stats><attention>10</attention> Attack Damage</stats>", into: [$"{LastWhisper}"]),
            [$"{LastWhisper}"] = Item("Last Whisper", 2300,
                "<stats><attention>40</attention> Attack Damage</stats><br><br><jadeUnique>Piercing Volley:</jadeUnique> You ignore <armorPen>35%</armorPen> of your opponent's <scaleArmor>Armor</scaleArmor>.",
                from: [$"{LongSword}"]),
            [$"{BlackCleaver}"] = Item("The Black Cleaver", 3000,
                "<stats><attention>200</attention> Health<br><attention>50</attention> Attack Damage<br><attention>10%</attention> Cooldown Reduction</stats><br><br>"
                + "<jadeUnique>Wicked Edge:</jadeUnique> <armorPen>10 Lethality</armorPen><br><jadeUnique>Cleaving Strike:</jadeUnique> Dealing <physicalDamage>physical damage</physicalDamage> "
                + "to an enemy champion reduces their <scaleArmor>Armor by 5%</scaleArmor> for 4 seconds."),
            [$"{VoidStaff}"] = Item("Void Staff", 2295,
                "<stats><attention>70</attention> Ability Power</stats><br><br><jadeUnique>Touch of the Void:</jadeUnique> Magic damage ignores <status>35%</status> of the target's <scaleMR>Magic Resist</scaleMR>."),
            [$"{Mejais}"] = Item("Mejai's Soulstealer", 1235,
                "<stats><attention>20</attention> Ability Power</stats><br><br><jadeUnique>Soul Capture:</jadeUnique> Grants <scaleAP>8 Ability Power</scaleAP> per stack. At 20 stacks, grants 15% Cooldown Reduction."),
            [$"{SwordOfTheOccult}"] = Item("Sword of the Occult", 1200,
                "<stats><attention>10</attention> Attack Damage</stats><br><br><jadeUnique>Dark Blade:</jadeUnique> Grants 5 Attack Damage per stack."),
            [$"{Shurelyas}"] = Item("Shurelya's Reverie", 2100,
                "<stats><attention>250</attention> Health<br><attention>10</attention> Health Regen per 5 seconds<br><attention>10</attention> Mana Regen per 5 seconds<br>"
                + "<attention>10%</attention> Cooldown Reduction</stats><br><br><jadeUnique>Active</jadeUnique> 60s<br>Grants nearby allies <speed>40% Move Speed</speed> for 3 seconds."),
            [$"{AncientGolem}"] = Item("Spirit of the Ancient Golem", 2000,
                "<stats><attention>500</attention> Health<br><attention>14</attention> Health Regen per 5 seconds<br><attention>10%</attention> Cooldown Reduction</stats><br><br>"
                + "<jadeUnique>Butcher:</jadeUnique> Damage dealt to monsters increased by 30%.<br><jadeUnique>Tenacity:</jadeUnique> Reduces the duration of Stuns, Slows, Taunts, Fears, Silences, Blinds, and Immobilizes by 35%."),
            [$"{InfinityEdge}"] = Item("Infinity Edge", 3800,
                "<stats><attention>70</attention> Attack Damage<br><attention>25%</attention> Critical Strike Chance</stats><br><br><jadeUnique>Infinite Precision:</jadeUnique> Critical strikes deal 250% damage instead of 200%."),
            [$"{Sunfire}"] = Item("Sunfire Cape", 2650,
                "<stats><attention>450</attention> Health<br><attention>45</attention> Armor</stats><br><br><jadeUnique>Searing Heat:</jadeUnique> Deals 40 magic damage per second to nearby enemies."),
        },
    }));

    private static readonly StaticGameData Static = new("test", Items, TestData.Static.Champions);

    private static readonly ChampionStats Marksman = new()
    {
        Health = 630, HealthPerLevel = 105, Armor = 26, ArmorPerLevel = 4, AttackDamage = 60, AttackDamagePerLevel = 3,
        AttackSpeed = 0.625, AttackSpeedPerLevel = 2, MoveSpeed = 325, AttackRange = 525,
    };

    [Theory]
    [InlineData(null, 453)]
    [InlineData("CLASSIC", 453)]
    [InlineData("JADE", 11)]
    public void Detect_RecognizesLeagueClassic(string? gameMode, int map)
    {
        var mode = GameModes.Detect(gameMode, map);

        Assert.Equal(GameMode.LeagueClassic, mode);
        Assert.Equal(GameModes.LeagueClassicMap, mode.MapId());
        Assert.Equal("League Classic", mode.DisplayName());
        Assert.False(mode.HasAugments());
    }

    [Fact]
    public void Detect_FallsBackOnClassicItems_WhenTheGameReportsSummonersRift()
    {
        var game = Game(me: ("Jinx", [DoransBlade]), enemies: [("Annie", [])]);
        var aram = Game(me: ("Jinx", [DoransBlade]), enemies: [("Annie", [])], gameMode: "ARAM", map: 12);
        var normal = Game(me: ("Jinx", [3047]), enemies: [("Annie", [])]);

        Assert.Equal(GameMode.LeagueClassic, GameAnalyzer.DetectMode(game));
        Assert.Equal(GameMode.Aram, GameAnalyzer.DetectMode(aram));
        Assert.Equal(GameMode.SummonersRift, GameAnalyzer.DetectMode(normal));
    }

    [Fact]
    public void ParseStats_ReadsStatsFromClassicPassives()
    {
        var cleaver = Items.Get(BlackCleaver)!;
        var boots = Items.Get(IonianBoots)!;

        Assert.Equal(50, cleaver.Stat(Stat.AttackDamage));
        Assert.Equal(10, cleaver.Stat(Stat.CooldownReduction));
        Assert.Equal(10, cleaver.Stat(Stat.Lethality));
        Assert.Equal(35, Items.Get(LastWhisper)!.Stat(Stat.ArmorPenPercent));
        Assert.Equal(35, Items.Get(VoidStaff)!.Stat(Stat.MagicPenPercent));
        Assert.Equal(15, boots.Stat(Stat.CooldownReduction));
        Assert.Equal(45, boots.Stat(Stat.MoveSpeed));
        Assert.Equal(35, Items.Get(AncientGolem)!.Stat(Stat.Tenacity));
    }

    [Fact]
    public void ParseStats_SkipsConditionalStatsAndConvertsFlatRegen()
    {
        var shurelyas = Items.Get(Shurelyas)!;

        Assert.Equal(0, Items.Get(Mejais)!.Stat(Stat.CooldownReduction)); // only at 20 stacks
        Assert.Equal(100, shurelyas.Stat(Stat.ManaRegen));                // 10 per 5 seconds
        Assert.Equal(100, shurelyas.Stat(Stat.HealthRegen));
        Assert.Equal(0, shurelyas.Stat(Stat.MoveSpeedPercent));           // the active isn't a stat
    }

    [Fact]
    public void ClassicItems_AreFinishedWhenTheyBuildIntoNothing()
    {
        Assert.Equal(ItemKind.Legendary, Items.Get(SwordOfTheOccult)!.Kind); // 1200 gold, below the standard 2200 bar
        Assert.Equal(ItemKind.Other, Items.Get(DoransBlade)!.Kind);          // starting item
        Assert.Equal(ItemKind.Component, Items.Get(LongSword)!.Kind);
        Assert.Equal(ItemKind.Boots, Items.Get(IonianBoots)!.Kind);          // builds from the classic Boots of Speed
        Assert.Contains("Wicked Edge", Items.Get(BlackCleaver)!.Passives);
        Assert.DoesNotContain("Active", Items.Get(Shurelyas)!.Passives);
        Assert.True(Items.Get(AncientGolem)!.IsJungleItem);
    }

    [Fact]
    public void ItemPools_KeepClassicAndStandardItemsApart()
    {
        var classic = Items.LegendariesOn(GameModes.LeagueClassicMap);
        var aram = Items.LegendariesOn(GameModes.HowlingAbyssMap);

        Assert.All(classic, i => Assert.True(i.IsClassic));
        Assert.Contains(classic, i => i.Id == LastWhisper);
        Assert.DoesNotContain(classic, i => i.Id == StandardPlate);
        Assert.Contains(aram, i => i.Id == StandardPlate);
        Assert.DoesNotContain(aram, i => i.IsClassic); // classic items are flagged for Howling Abyss too
        Assert.Equal([IonianBoots], Items.BootsOn(GameModes.LeagueClassicMap).Select(i => i.Id));
    }

    [Theory]
    [InlineData("<jadeUnique>Cold Steel:</jadeUnique> When hit by Basic Attacks, reduces the attacker's <attackSpeed>Attack Speed</attackSpeed> by 15%.", ItemTraits.AttackSpeedSlow)]
    [InlineData("<jadeUnique>Aura - Frostbite:</jadeUnique> Reduces the <attackSpeed>Attack Speed</attackSpeed> of nearby enemies by 20%.", ItemTraits.AttackSpeedSlow)]
    [InlineData("<jadeUnique>Active</jadeUnique> 90s<br>Champion becomes Invulnerable and Untargetable for 2.5 seconds.", ItemTraits.Stasis)]
    [InlineData("<jadeUnique>Guardian's Intervention:</jadeUnique> Upon taking lethal damage, restores 30% of maximum Health after 4 seconds of stasis.", ItemTraits.Stasis)]
    [InlineData("<jadeUnique>Active</jadeUnique> 90s<br>Removes all debuffs.", ItemTraits.Cleanse)]
    [InlineData("<jadeUnique>Seeing Red :</jadeUnique> Attacks deal 4% of the target's maximum Health as bonus magic damage on-hit.", ItemTraits.MaxHealthDamage)]
    [InlineData("<jadeUnique>Active</jadeUnique> 60s<br>Deals 15% of target champion's maximum Health in magic damage.", ItemTraits.MaxHealthDamage)]
    [InlineData("<jadeUnique>Aura - Despair:</jadeUnique> Reduces the <scaleMR>Magic Resist</scaleMR> of nearby enemies by 20", ItemTraits.ResistShred)]
    [InlineData("<jadeUnique>Cursed Blade:</jadeUnique> Basic Attacks remove 4 <scaleMR>Magic Resist</scaleMR> from the target for 8 seconds.", ItemTraits.ResistShred)]
    [InlineData("<jadeUnique>Rending Strike:</jadeUnique> Basic Attacks inflict Grievous Wounds on enemy champions.", ItemTraits.AntiHeal)]
    [InlineData("<stats><attention>20%</attention> Spell Vamp</stats>", ItemTraits.Sustain)]
    public void DetectTraits_RecognizesClassicWording(string description, ItemTraits expected)
    {
        Assert.Equal(expected, ItemCatalog.DetectTraits(description) & expected);
    }

    [Fact]
    public void Estimate_UsesLinearGrowthAndBonusArmorInLeagueClassic()
    {
        var classic = StatEstimator.Estimate(Marksman, 10, [], GameMode.LeagueClassic);
        var rift = StatEstimator.Estimate(Marksman, 10, []);

        Assert.Equal(60 + 3 * 9, classic.AttackDamage, precision: 3);
        Assert.Equal(26 + 4 * 9 + 4, classic.Armor, precision: 3);
        Assert.True(rift.AttackDamage < classic.AttackDamage); // today's curve backloads growth
    }

    [Fact]
    public void Estimate_CapsCooldownReductionAt40Percent()
    {
        var cleaver = Items.Get(BlackCleaver)!;   // 10%
        var boots = Items.Get(IonianBoots)!;      // 15%
        var golem = Items.Get(AncientGolem)!;     // 10%
        var shurelyas = Items.Get(Shurelyas)!;    // 10%

        var stats = StatEstimator.Estimate(Marksman, 18, [cleaver, boots, golem, shurelyas, shurelyas], GameMode.LeagueClassic);

        Assert.Equal(StatEstimator.CooldownReductionCap, stats.CooldownReduction);
    }

    [Fact]
    public void Recommend_OnlySuggestsClassicItems_AndAnswersTanksWithLastWhisper()
    {
        var data = Game(me: ("Jinx", [DoransBlade, InfinityEdge]), enemies: [("Ornn", [Sunfire]), ("Sejuani", [Sunfire]), ("Braum", [Sunfire])]);

        var game = GameAnalyzer.Analyze(data, Static)!;
        var rec = new RecommendationEngine(Static).Recommend(game, maxItems: 10);

        Assert.Equal(GameMode.LeagueClassic, game.Mode);
        Assert.All(rec.Ranked, s => Assert.True(s.Item.IsClassic));
        Assert.Contains(rec.Items, s => s.Item.Id == LastWhisper);
        Assert.Contains(rec.Advice, a => a.Suggested.Id == LastWhisper);
        Assert.Equal(IonianBoots, rec.Boots?.Item.Id);
    }

    [Fact]
    public void Recommend_SuggestsJungleItemsOnlyWithSmite()
    {
        var laner = GameAnalyzer.Analyze(Game(me: ("Sejuani", [DoransBlade]), enemies: [("Zed", [])]), Static)!;
        var jungler = GameAnalyzer.Analyze(Game(me: ("Sejuani", [DoransBlade]), enemies: [("Zed", [])], mySpells: ["Flash", "Smite"]), Static)!;

        Assert.DoesNotContain(new RecommendationEngine(Static).Recommend(laner).Ranked, s => s.Item.Id == AncientGolem);
        Assert.Contains(new RecommendationEngine(Static).Recommend(jungler).Ranked, s => s.Item.Id == AncientGolem);
    }

    private static AllGameData Game(
        (string Champion, int[] Items) me, IEnumerable<(string Champion, int[] Items)> enemies,
        string[]? mySpells = null, string? gameMode = "CLASSIC", int map = 11)
    {
        LivePlayer Player(string champion, string team, string name, int[] items, string[]? spells = null) => new()
        {
            ChampionName = champion,
            RawChampionName = $"game_character_displayname_{champion}",
            RiotId = $"{name}#TEST",
            Team = team,
            Level = 11,
            Items = items.Select((id, slot) => new LiveItem { ItemID = id, Slot = slot }).ToList(),
            SummonerSpells = spells is null ? null : new LiveSummonerSpells
            {
                SummonerSpellOne = new LiveSummonerSpell { DisplayName = spells[0] },
                SummonerSpellTwo = new LiveSummonerSpell { DisplayName = spells[1] },
            },
        };

        return new AllGameData
        {
            ActivePlayer = new ActivePlayer { RiotId = "Me#TEST" },
            AllPlayers = [Player(me.Champion, "ORDER", "Me", me.Items, mySpells), .. enemies.Select((e, i) => Player(e.Champion, "CHAOS", $"Enemy{i}", e.Items))],
            GameData = new LiveGameInfo { GameMode = gameMode, MapNumber = map, GameTime = 1200 },
        };
    }

    private static object Item(string name, int gold, string description, string[]? tags = null, string[]? from = null, string[]? into = null, string[]? maps = null) => new
    {
        name,
        description = "<mainText>" + description + "</mainText>",
        gold = new { total = gold, purchasable = true },
        maps = (maps ?? ["12", "453"]).ToDictionary(m => m, _ => true),
        tags = tags ?? [],
        from = from ?? [],
        into = into ?? [],
    };
}
