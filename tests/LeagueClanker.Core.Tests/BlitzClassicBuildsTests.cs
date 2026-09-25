using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Blitz;
using LeagueClanker.Core.StaticData;
using static LeagueClanker.Core.Tests.TestData;

namespace LeagueClanker.Core.Tests;

public class BlitzClassicBuildsTests
{
    private const int Berserkers = 773006, Bloodthirster = 773072, InfinityEdge = 773031, PhantomDancer = 773046, LastWhisper = 773035;

    // League Classic's copies of the items, on map 453.
    private static readonly ItemCatalog Items = ItemCatalog.Parse(JsonSerializer.Serialize(new
    {
        data = new Dictionary<string, object>
        {
            [$"{Berserkers}"] = Item("Berserker's Greaves", 900, Stats(("25%", "Attack Speed")), tags: ["Boots"], from: ["771001"], maps: ["453"]),
            [$"{Bloodthirster}"] = Item("The Bloodthirster", 3200, Stats(("Attack Damage", "80"), ("20%", "Life Steal")), maps: ["453"]),
            [$"{InfinityEdge}"] = Item("Infinity Edge", 3800, Stats(("Attack Damage", "70"), ("25%", "Critical Strike Chance")), maps: ["453"]),
            [$"{PhantomDancer}"] = Item("Phantom Dancer", 2800, Stats(("55%", "Attack Speed"), ("30%", "Critical Strike Chance")), maps: ["453"]),
            [$"{LastWhisper}"] = Item("Last Whisper", 2300, Stats(("Attack Damage", "40")), maps: ["453"]),
        },
    }));

    // Trimmed from Blitz's champion_cms_builds for Miss Fortune in League Classic (champion 60021, queue CLASSIC_5x5).
    private const string MissFortune = """
        {"data": [
          {"name_tags": ["AD"], "individual_position": "BOTTOM", "coreItems": [773006, 773072, 773031],
           "slot4": [773031, 773035], "slot5": [773035], "slot6": [], "slot7": [], "optionalItems": [773035, 773046]},
          {"name_tags": ["Crit"], "individual_position": "BOTTOM", "coreItems": [773006, 773031, 773046],
           "slot4": [773072, 773031], "slot5": [773035], "slot6": [], "slot7": []}
        ]}
        """;

    [Fact]
    public void Parse_ReadsBothBuilds_WithLegendariesOnly()
    {
        var builds = BlitzClassicBuilds.Parse(MissFortune, Position.Bottom, Items);

        Assert.Equal(["AD build", "crit build"], builds.Select(b => b.Name));
        Assert.Equal([Bloodthirster, InfinityEdge], builds[0].Core.Select(i => i.Id)); // boots aren't part of the core
        Assert.Equal([LastWhisper], builds[0].Later.Select(i => i.Id));
        Assert.All(builds, b => Assert.Equal(Archetype.Marksman, b.Style));
        Assert.False(builds[0].HasStats); // curated builds: the game chooses
        Assert.Equal([PhantomDancer], builds[0].Situational.Select(i => i.Id)); // Last Whisper is already in the build
        Assert.Equal(Berserkers, builds[0].Boots!.Id);
    }

    [Fact]
    public void Parse_KeepsToYourRole_WhenBlitzHasIt()
    {
        var json = MissFortune.Replace("\"individual_position\": \"BOTTOM\", \"coreItems\": [773006, 773031", "\"individual_position\": \"MIDDLE\", \"coreItems\": [773006, 773031");

        Assert.Equal(["AD build"], BlitzClassicBuilds.Parse(json, Position.Bottom, Items).Select(b => b.Name));
        Assert.Equal(2, BlitzClassicBuilds.Parse(json, Position.None, Items).Count);
    }
}
