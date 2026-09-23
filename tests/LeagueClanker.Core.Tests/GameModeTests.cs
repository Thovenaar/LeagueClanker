using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;

namespace LeagueClanker.Core.Tests;

public class GameModeTests
{
    [Theory]
    [InlineData("CLASSIC", 11, GameMode.SummonersRift)]
    [InlineData("ARAM", 12, GameMode.Aram)]
    [InlineData("KIWI", 12, GameMode.AramMayhem)]
    [InlineData(null, 12, GameMode.Aram)]
    [InlineData("CHERRY", 30, GameMode.Unsupported)]
    public void Detect_ReadsTheLiveGameMode(string? gameMode, int map, GameMode expected)
    {
        Assert.Equal(expected, GameModes.Detect(gameMode, map));
    }

    [Fact]
    public void Items_AreFilteredPerMap()
    {
        var aram = TestData.Static.Items.LegendariesOn(GameModes.HowlingAbyssMap);

        Assert.Contains(aram, i => i.Id == TestData.Plate);
        Assert.DoesNotContain(aram, i => i.Id == TestData.Cloak);
    }

    [Fact]
    public void Mayhem_UsesHowlingAbyssItems()
    {
        var game = TestData.Game(allies: [("Garen", [])], enemies: [("Annie", []), ("Syndra", []), ("Lux", [])]);
        var mayhem = new AllGameData
        {
            ActivePlayer = game.ActivePlayer,
            AllPlayers = game.AllPlayers,
            GameData = new LiveGameInfo { GameMode = "KIWI", MapNumber = 12 },
        };

        var analysis = GameAnalyzer.Analyze(mayhem, TestData.Static)!;
        var rec = new RecommendationEngine(TestData.Static).Recommend(analysis, maxItems: 10);

        Assert.Equal(GameMode.AramMayhem, analysis.Mode);
        Assert.True(analysis.Mode.HasAugments());
        Assert.DoesNotContain(rec.Items, i => i.Item.Id == TestData.Cloak); // strong vs AP, but not sold on Howling Abyss
        Assert.Contains(rec.Items, i => i.Item.Id == TestData.Plate);
    }
}
