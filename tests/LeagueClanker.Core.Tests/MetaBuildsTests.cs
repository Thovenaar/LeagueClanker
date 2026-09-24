using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;
using static LeagueClanker.Core.Tests.TestData;

namespace LeagueClanker.Core.Tests;

public class MetaBuildsTests
{
    private const int Kraken = 6672, Botrk = 3153, Nashors = 3115, Wits = 3091, Stormsurge = 4646, Shadowflame = 4645, Rabadon = 3089;

    // A few real items, parsed from Data Dragon-style descriptions, on Summoner's Rift and Howling Abyss.
    private static readonly StaticGameData Data = new("test", ItemCatalog.Parse(JsonSerializer.Serialize(new
    {
        data = new Dictionary<string, object>
        {
            [$"{Kraken}"] = Item("Kraken Slayer", 3000, Stats(("Attack Damage", "45"), ("40%", "Attack Speed"))
                + "<passive>Bring It Down</passive><br>Every third Attack deals bonus physical damage <OnHit>On-Hit</OnHit>.", maps: ["11", "12"]),
            [$"{Botrk}"] = Item("Blade of The Ruined King", 3200, Stats(("Attack Damage", "40"), ("25%", "Attack Speed"), ("10%", "Life Steal"))
                + "<passive>Mist's Edge</passive><br>Attacks deal a percentage of enemy's current Health as bonus physical damage <OnHit>On-Hit</OnHit>.", maps: ["11", "12"]),
            [$"{Nashors}"] = Item("Nashor's Tooth", 2900, Stats(("Ability Power", "80"), ("50%", "Attack Speed"), ("Ability Haste", "15"))
                + "<passive>Icathian Bite</passive><br>Attacks deal bonus magic damage <OnHit>On-Hit</OnHit>.", maps: ["11", "12"]),
            [$"{Wits}"] = Item("Wit's End", 2800, Stats(("50%", "Attack Speed"), ("Magic Resist", "45"), ("20%", "Tenacity"))
                + "<passive>Fray</passive><br>Attacks deal bonus magic damage <OnHit>On-Hit</OnHit>.", maps: ["11", "12"]),
            [$"{Stormsurge}"] = Item("Stormsurge", 2800, Stats(("Ability Power", "90"), ("Magic Penetration", "15")), maps: ["11", "12"]),
            [$"{Shadowflame}"] = Item("Shadowflame", 3200, Stats(("Ability Power", "110"), ("Magic Penetration", "15")), maps: ["11", "12"]),
            [$"{Rabadon}"] = Item("Rabadon's Deathcap", 3500, Stats(("Ability Power", "130")), maps: ["11", "12"]),
        },
    })), Static.Champions);

    // Shaped like op.gg's ARAM data for Katarina: an on-hit core that wins more, an AP core that's played more, and noise.
    private static readonly OpggChampion Katarina = new([], [], Position.Middle)
    {
        CoreItems =
        [
            new([Stormsurge, Shadowflame, Rabadon], 500, 220),
            new([Kraken, Botrk, Nashors], 400, 228),
            new([Kraken, Shadowflame, Botrk], 20, 15),
        ],
        LaterItems = [new([Shadowflame], 1300, 570), new([Rabadon], 900, 400), new([Wits], 300, 170)],
    };

    private static IReadOnlyList<MetaBuild> Builds => MetaBuilds.From(Katarina, Data.Items, GameMode.AramMayhem);

    [Fact]
    public void From_GroupsCoresIntoStyles_WithLaterItemsOfTheSameStyle()
    {
        var builds = Builds;

        Assert.Equal(2, builds.Count); // the 20-game core is noise
        var onHit = builds.Single(b => b.Style == Archetype.OnHit);
        Assert.Equal([Kraken, Botrk, Nashors], onHit.Core.Select(i => i.Id));
        Assert.Equal([Wits], onHit.Later.Select(i => i.Id)); // Shadowflame is popular overall, but it isn't an on-hit item
        Assert.Equal(0.57, onHit.WinRate, 2);
        Assert.Contains(builds, b => b.Style is Archetype.Mage or Archetype.ApAssassin);
    }

    [Fact]
    public void Choose_TakesTheBuildThatWinsMore_UnlessYouOwnTheOthersItems()
    {
        var neutral = Analyze([]);
        var invested = Analyze([Stormsurge, Shadowflame]);

        Assert.Equal(Archetype.OnHit, MetaBuilds.Choose(Builds, neutral, [])!.Build.Style);
        Assert.NotEqual(Archetype.OnHit, MetaBuilds.Choose(Builds, invested, [])!.Build.Style); // two AP items already bought
    }

    [Fact]
    public void Choose_KeepsToThePlaystyleYouPicked()
    {
        var game = Analyze([]) with { ChosenPlaystyle = Archetype.Mage };

        Assert.NotEqual(Archetype.OnHit, MetaBuilds.Choose(Builds, game, [])!.Build.Style);
    }

    [Fact]
    public void Advisor_FollowsTheChosenBuild_AndPlaysItsStyle()
    {
        var game = Game([("Annie", [])], [("Garen", []), ("Braum", []), ("Ornn", [])]);
        var advisor = new BuildAdvisor(new FileGameDataSource("unused.json"), Data) { MetaBuilds = Builds };

        var rec = advisor.RecommendOnce(game)!;

        Assert.Equal(Archetype.OnHit, rec.Game.Me.Archetype); // Annie is a mage, but the on-hit build decided
        Assert.Equal([Kraken, Botrk, Nashors], rec.Items.Take(3).Select(i => i.Item.Id));
        Assert.StartsWith("op.gg's on-hit build (57.0% win rate over 400 games), over the", rec.Meta!.Text);
    }

    [Fact]
    public void Engine_SwapsOneLaterItem_WhenTheGameClearlyAsksForSomethingElse()
    {
        // Garen follows a meta build whose last item is plain health; the enemy heals a lot, and Wound Blade answers that.
        var build = new MetaBuild(Archetype.Bruiser,
            [Static.Items.Get(TestData.Cleaver)!, Static.Items.Get(TestData.Plate)!, Static.Items.Get(TestData.Cloak)!],
            [Static.Items.Get(TestData.Heart)!], 1000, 0.52);
        var game = GameAnalyzer.Analyze(Game([("Garen", [])], [("Soraka", []), ("Vladimir", []), ("Aatrox", [])]), Static)! with { MetaBuilds = [build] };

        var rec = new RecommendationEngine(Static).Recommend(game);

        Assert.Equal([TestData.Cleaver, TestData.Plate, TestData.Cloak], rec.Items.Take(3).Select(i => i.Item.Id)); // the core stays
        Assert.Equal(TestData.WoundBlade, rec.Items[3].Item.Id);
        Assert.StartsWith("Wound Blade instead of Heart: enemy", rec.Meta!.Swap);
    }

    private static GameAnalysis Analyze(int[] items) => GameAnalyzer.Analyze(Game([("Annie", items)], [("Garen", [])]), Data)!;
}
