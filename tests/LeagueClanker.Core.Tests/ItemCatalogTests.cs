using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class ItemCatalogTests
{
    [Fact]
    public void ParseStats_ReadsFlatAndPercentStatsFromDescription()
    {
        var stats = ItemCatalog.ParseStats(
            "<mainText><stats><attention>95</attention> Ability Power<br><attention>40%</attention> Magic Penetration</stats><br><br></mainText>");

        Assert.Equal(95, stats[Stat.AbilityPower]);
        Assert.Equal(40, stats[Stat.MagicPenPercent]);
        Assert.False(stats.ContainsKey(Stat.MagicPen));
    }

    // Descriptions trimmed from Data Dragon 16.18.
    [Theory]
    [InlineData("<passive>Grievous Wounds</passive><br>Dealing magic damage to champions applies <keyword>40% Wounds</keyword> for 3 seconds.", ItemTraits.AntiHeal)]
    [InlineData("<passive>Shield Reaver</passive><br>Damaging an enemy champion reduces Shields they gain by % for 3 seconds.", ItemTraits.AntiShield)]
    [InlineData("<passive>Resilience</passive><br>Receive 30% less damage from Critical Strikes.", ItemTraits.CritReduction)]
    [InlineData("<passive>Winter's Caress</passive><br>Reduce the <attackSpeed>Attack Speed</attackSpeed> of nearby champions by 20%.", ItemTraits.AttackSpeedSlow)]
    [InlineData("<active>Time Stop</active><br>Enter <keyword>Stasis</keyword> for 2.5 seconds.", ItemTraits.Stasis)]
    [InlineData("<passive>Annul</passive><br>Grants a Spell Shield that blocks the next enemy Ability.", ItemTraits.SpellShield)]
    [InlineData("<active>Quicksilver</active><br>Removes all crowd control debuffs and grants Move Speed.", ItemTraits.Cleanse)]
    [InlineData("Damaging Abilities burn enemies for <magicDamage>2% max Health magic damage</magicDamage> per second.", ItemTraits.MaxHealthDamage)]
    [InlineData("Dealing <physicalDamage>physical damage</physicalDamage> to champions reduces the target's <scaleArmor>Armor by 6%</scaleArmor>.", ItemTraits.ResistShred)]
    [InlineData("Taking damage below 30% Health grants a <shield>decaying Shield</shield>.", ItemTraits.GrantsShield)]
    public void DetectTraits_RecognizesItemEffects(string description, ItemTraits expected)
    {
        Assert.Equal(expected, ItemCatalog.DetectTraits(description) & expected);
    }

    [Fact]
    public void DetectTraits_IgnoresSelfBuffsThatMentionResistances()
    {
        // Jak'Sho buffs its owner's resistances; that's not shredding the enemy's.
        var traits = ItemCatalog.DetectTraits("After 5 seconds of champion combat, increase your bonus <scaleArmor>Armor</scaleArmor> and Magic Resist by 30%.");

        Assert.False(traits.HasFlag(ItemTraits.ResistShred));
    }

    [Fact]
    public void Parse_ClassifiesItemKinds()
    {
        var items = TestData.Static.Items;

        Assert.Equal(ItemKind.Legendary, items.Get(TestData.Plate)!.Kind);
        Assert.Equal(ItemKind.Boots, items.Get(TestData.ArmorBoots)!.Kind);
        Assert.Equal(ItemKind.Other, items.Get(1001)!.Kind); // Basic boots are never a recommendation
        Assert.Equal(ItemKind.Component, items.Get(TestData.WoundComponent)!.Kind);
        Assert.Equal(ItemKind.Other, items.Get(223001)!.Kind); // Arena copy
        Assert.DoesNotContain(items.Legendaries, i => i.Id == 223001);
    }
}
