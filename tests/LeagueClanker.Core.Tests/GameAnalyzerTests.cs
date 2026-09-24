using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class GameAnalyzerTests
{
    // Data Dragon tags and attack/magic ratings as of 16.18.
    [Theory]
    [InlineData("Annie", new[] { "Mage", "Support" }, 2, 10, Archetype.Mage)]
    [InlineData("Pyke", new[] { "Support", "Assassin" }, 9, 1, Archetype.AdAssassin)]
    [InlineData("Thresh", new[] { "Support", "Tank" }, 5, 6, Archetype.Tank)]
    [InlineData("Lulu", new[] { "Support", "Mage" }, 4, 7, Archetype.Enchanter)]
    [InlineData("Teemo", new[] { "Marksman", "Mage" }, 5, 7, Archetype.Mage)]
    [InlineData("KogMaw", new[] { "Marksman", "Mage" }, 8, 5, Archetype.OnHit)] // override: on-hit, not crit
    [InlineData("Caitlyn", new[] { "Marksman" }, 8, 2, Archetype.Marksman)]
    [InlineData("Mordekaiser", new[] { "Fighter", "Mage" }, 4, 7, Archetype.ApBruiser)]
    [InlineData("Fizz", new[] { "Assassin", "Fighter" }, 6, 7, Archetype.ApAssassin)]
    [InlineData("Gwen", new[] { "Fighter" }, 7, 5, Archetype.ApBruiser)] // override: builds AP
    [InlineData("Yasuo", new[] { "Fighter", "Assassin" }, 8, 4, Archetype.Marksman)] // override: builds crit
    public void Classify_MapsChampionsToHowTheyBuild(string id, string[] tags, int attack, int magic, Archetype expected)
    {
        Assert.Equal(expected, ArchetypeClassifier.Classify(new ChampionInfo(id, id, tags, attack, 5, magic)));
    }

    [Fact]
    public void Analyze_SplitsTeamsAroundTheActivePlayer()
    {
        var game = TestData.Game(
            allies: [("Garen", []), ("Jinx", [])],
            enemies: [("Annie", []), ("Ornn", [])]);

        var analysis = GameAnalyzer.Analyze(game, TestData.Static)!;

        Assert.Equal("Garen", analysis.Me.Champion.Id);
        Assert.Equal(["Jinx"], analysis.Allies.Players.Select(p => p.Champion.Id));
        Assert.Equal(["Annie", "Ornn"], analysis.Enemies.Players.Select(p => p.Champion.Id));
    }

    [Fact]
    public void Analyze_ReturnsNullWhenActivePlayerIsNotInTheGame()
    {
        var game = TestData.Game(allies: [("Garen", [])], enemies: [("Annie", [])]);
        var spectating = new AllGameData { ActivePlayer = new ActivePlayer { RiotId = "Spectator#TEST" }, AllPlayers = game.AllPlayers };

        Assert.Null(GameAnalyzer.Analyze(spectating, TestData.Static));
    }

    [Fact]
    public void EstimatedStats_UseBaseStatsAtLevelPlusItems()
    {
        var level1 = Profile("Ornn", level: 1, TestData.Plate);
        var level18 = Profile("Ornn", level: 18, TestData.Plate);

        Assert.Equal(33 + 60, level1.Stats.Armor, precision: 1);
        Assert.Equal(33 + 5.2 * 17 + 60, level18.Stats.Armor, precision: 1); // growth curve reaches exactly 17 levels' worth at 18
        Assert.Equal(660 + 109 * 17 + 400, level18.Stats.Health, precision: 1);
    }

    [Fact]
    public void Tankiness_TrustsTheChampionClassUntilTheyBuyItems()
    {
        Assert.True(Profile("Ornn", level: 3).IsTanky); // nothing bought yet, assume a tank goes tank

        var damageOrnn = Profile("Ornn", level: 11, TestData.CritSword, TestData.CritSword);
        Assert.False(damageOrnn.IsTanky);
        Assert.True(damageOrnn.IsSquishy);

        Assert.True(Profile("Ornn", level: 11, TestData.Plate, TestData.Cloak).IsTanky);
        Assert.True(Profile("Garen", level: 11, TestData.Plate, TestData.Cloak, TestData.Heart).IsTanky); // a bruiser can build tank too
        Assert.False(Profile("Garen", level: 11, TestData.Cleaver, TestData.CritSword).IsSquishy); // but isn't squishy without it
    }

    private static PlayerProfile Profile(string champion, int level, params int[] items) =>
        GameAnalyzer.Profile(new LivePlayer
        {
            ChampionName = champion,
            RawChampionName = $"game_character_displayname_{champion}",
            Level = level,
            Items = items.Select(id => new LiveItem { ItemID = id }).ToList(),
        }, TestData.Static);

    [Fact]
    public void MagicShare_FollowsItemsNotJustTheChampion()
    {
        var champion = new ChampionInfo("Kaisa", "Kai'Sa", ["Marksman", "Mage"], 8, 5, 3);
        var apItem = TestData.Static.Items.Get(TestData.Cloak)! with
        {
            Stats = new Dictionary<string, double> { [Stat.AbilityPower] = 100, [Stat.AttackSpeed] = 50 },
        };

        var noItems = GameAnalyzer.EstimateMagicShare(champion, Archetype.Marksman, []);
        var apBuild = GameAnalyzer.EstimateMagicShare(champion, Archetype.Marksman, [apItem, apItem]);

        Assert.True(apBuild > noItems + 0.15, $"AP build {apBuild:0.00} should be well above base {noItems:0.00}");
    }
}
