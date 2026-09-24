using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;
using static LeagueClanker.Core.Tests.TestData;

namespace LeagueClanker.Core.Tests;

public class OnHitTests
{
    // Passive text as Data Dragon writes it for patch 16.19.
    private static readonly ItemCatalog Items = ItemCatalog.Parse(JsonSerializer.Serialize(new
    {
        data = new Dictionary<string, object>
        {
            ["3115"] = Item("Nashor's Tooth", 3000, Stats(("Ability Power", "80"), ("50%", "Attack Speed"), ("Ability Haste", "15"))
                + "<passive>Icathian Bite</passive><br>Attacks deal bonus magic damage <OnHit>On-Hit</OnHit>."),
            ["3078"] = Item("Trinity Force", 3333, Stats(("Attack Damage", "36"), ("30%", "Attack Speed"), ("Health", "333"))
                + "<passive>Spellblade</passive><br>Your next Attack after using an Ability deals bonus damage <OnHit>On-Hit</OnHit>."),
            ["3089"] = Item("Rabadon's Deathcap", 3500, Stats(("Ability Power", "130"))
                + "<passive>Magical Opus</passive><br>Increases your total <scaleAP>Ability Power by 30%</scaleAP>."),
        },
    }));

    [Fact]
    public void OnHitTrait_MeansAttacksDealExtraDamage_NotASpellblade()
    {
        Assert.True(Items.All.Single(i => i.Name == "Nashor's Tooth").Has(ItemTraits.OnHit));
        Assert.False(Items.All.Single(i => i.Name == "Trinity Force").Has(ItemTraits.OnHit));
    }

    [Fact]
    public void OnHitProfile_PrefersAttackSpeedAndOnHitItems_OverPureAbilityPower()
    {
        var onHit = ArchetypeProfiles.For(Archetype.OnHit);
        var nashors = Items.All.Single(i => i.Name == "Nashor's Tooth");
        var rabadon = Items.All.Single(i => i.Name == "Rabadon's Deathcap");

        Assert.True(onHit.BaseScore(nashors) > onHit.BaseScore(rabadon) + 1);
        Assert.True(ArchetypeProfiles.For(Archetype.ApAssassin).BaseScore(rabadon) > ArchetypeProfiles.For(Archetype.ApAssassin).BaseScore(nashors));
    }

    [Fact]
    public void OnHitDamageType_FollowsTheChampionsDamageSplit()
    {
        var game = GameAnalyzer.Analyze(Game([("Jinx", [])], [("Garen", [])]), Static, playstyle: Archetype.OnHit)!;

        Assert.Equal(Archetype.OnHit, game.Me.Archetype);
        Assert.Equal(game.Me.MagicShare >= 0.5 ? DamageType.Magic : DamageType.Physical, game.Me.DamageType);
        Assert.Equal("On-hit", Archetype.OnHit.DisplayName());
    }
}
