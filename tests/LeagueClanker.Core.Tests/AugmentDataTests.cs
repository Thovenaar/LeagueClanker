using LeagueClanker.Core.Augments;

namespace LeagueClanker.Core.Tests;

public class AugmentDataTests
{
    [Fact]
    public void LuaTable_ParsesTheWikiModuleShape()
    {
        var data = LuaTable.ParseReturn("""
            -- <pre>
            return {
                ["Dive Bomber"] = {
                    ["description"] = "Deal \"true\" damage.",
                    ["tier"] = "Silver",
                    ["questinfo"] = [=[
            {| class="article-table"
            |}
                    ]=],
                },
                plain = { 1, 2.5, true },
            }
            -- </pre>
            """);

        var bomber = (Dictionary<string, object?>)data["Dive Bomber"]!;
        Assert.Equal("Deal \"true\" damage.", bomber["description"]);
        Assert.Equal("Silver", bomber["tier"]);
        Assert.StartsWith("{| class", (string)bomber["questinfo"]!);
        var plain = (Dictionary<string, object?>)data["plain"]!;
        Assert.Equal(2.5, plain["2"]);
        Assert.Equal(true, plain["3"]);
    }

    [Theory]
    [InlineData("Grants {{as|60% '''bonus''' attack speed}}.", "Grants 60% bonus attack speed.")]
    [InlineData("{{tip|immobilize|Immobilizing}} an enemy", "Immobilizing an enemy")]
    [InlineData("Additionally, gain {{g|250}}.", "Additionally, gain 250 gold.")]
    [InlineData("reduces armor by {{as|{{fd|1.5}}%}}", "reduces armor by 1.5%")]
    [InlineData("deals {{pp|70 to 240|color=physical damage}} damage", "deals 70 to 240 damage")]
    [InlineData("Poltergeist: {{#invoke:SpellData|geteffect|Poltergeist}}", "Poltergeist:")]
    public void WikiText_RemovesMarkup(string markup, string expected)
    {
        Assert.Equal(expected, WikiText.Clean(markup));
    }

    [Theory]
    [InlineData("Grants 60% bonus attack speed.", AugmentEffect.AttackSpeed, AugmentTrigger.None)]
    [InlineData("Increases attack damage by 20%.", AugmentEffect.AttackDamage, AugmentTrigger.None)]
    [InlineData("Deal 1% increased damage per 10 movement speed you have more than the target.", AugmentEffect.None, AugmentTrigger.MoveSpeed)]
    [InlineData("deals 70 to 240 (+ 70% AD) (+ 60% AP) magic damage", AugmentEffect.None, AugmentTrigger.AttackDamage | AugmentTrigger.AbilityPower)]
    [InlineData("Gain ability haste equal to 30% AP.", AugmentEffect.AbilityHaste, AugmentTrigger.AbilityPower)]
    public void TagText_SeparatesGivenStatsFromScaling(string description, AugmentEffect gives, AugmentTrigger scales)
    {
        var (effects, triggers) = AugmentTagger.TagText(description);

        Assert.Equal(gives, effects);
        Assert.Equal(scales, triggers);
    }

    [Fact]
    public void TagText_IgnoresTheTargetsHealthAndLowHealthThresholds()
    {
        var (bomber, _) = AugmentTagger.TagText("Upon death, you explode to deal true damage equal to 20% of the target's maximum health.");
        var (escape, _) = AugmentTagger.TagText("Upon dropping below 35% of your maximum health, gain a shield.");

        Assert.False(bomber.HasFlag(AugmentEffect.Health));
        Assert.False(escape.HasFlag(AugmentEffect.Health));
    }

    [Fact]
    public void Catalog_TagsMechanicsAndResolvesMentionedItems()
    {
        var catalog = TestAugments.Catalog;

        var bomber = catalog.Find("Dive Bomber")!;
        Assert.True(bomber.Gives(AugmentEffect.TrueDamage));
        Assert.True(bomber.Gives(AugmentEffect.MaxHealthDamage));
        Assert.True(bomber.Needs(AugmentTrigger.Death));

        Assert.Equal(["Plate"], catalog.Find("Plated")!.MentionedItems.Select(i => i.Name));
        Assert.True(catalog.Find("Pack Leader")!.Needs(AugmentTrigger.Pets));
    }

    [Fact]
    public void Catalog_ReadsGrantedCritAndAttackSpeed()
    {
        Assert.Equal(50, TestAugments.Get("Lucky").CritChanceBonus);
        Assert.Equal(25, TestAugments.Get("Rhythm Plus").CritChanceBonus);
        Assert.Equal(30, TestAugments.Get("Quick").AttackSpeedBonus);
        Assert.Equal(0, TestAugments.Get("Rhythm").CritChanceBonus);
    }

    [Fact]
    public void Catalog_SpotsCardsThatGiveExtraRerolls()
    {
        var catalog = AugmentCatalog.ParseWikiModule("""
            return {
                ["Stats on Stats on Stats!"] = { ["description"] = "Gain 4 Stat Bonus. Additionally, on the next round of augment selection, you gain an additional reroll per augment slot.", ["tier"] = "Prismatic" },
                ["Stats!"] = { ["description"] = "Gain 2 Stat Bonus.", ["tier"] = "Silver" },
            }
            """);

        Assert.True(catalog.Find("Stats on Stats on Stats!")!.GrantsExtraRerolls);
        Assert.False(catalog.Find("Stats!")!.GrantsExtraRerolls);
    }

    [Fact]
    public void Catalog_FindIsForgivingAndSkipsDisabledCards()
    {
        var catalog = TestAugments.Catalog;

        Assert.Equal("Rhythm", catalog.Find("  rhythm! ")?.Name);
        Assert.True(catalog.Find("Broken")!.IsDisabled);
        Assert.DoesNotContain(catalog.Offerable, a => a.Name == "Broken");
    }
}

/// <summary>A tiny augment pool in the wiki's format, tagged against the synthetic test items.</summary>
internal static class TestAugments
{
    public static readonly AugmentCatalog Catalog = AugmentCatalog.ParseWikiModule("""
        return {
            ["Quick"] = { ["description"] = "Grants {{as|30% '''bonus''' attack speed}}.", ["tier"] = "Silver" },
            ["Brute"] = { ["description"] = "Increases {{as|attack damage}} by {{as|20%|AD}}. Additionally, gain 10 {{as|armor}}.", ["tier"] = "Silver" },
            ["Wizard"] = { ["description"] = "Grants {{as|20 to 80 ability power}}.", ["tier"] = "Silver" },
            ["Dive Bomber"] = { ["description"] = "Upon death, you explode to deal {{as|true damage}} equal to {{as|20% of the target's '''maximum''' health}}.", ["tier"] = "Silver" },
            ["Broken"] = { ["description"] = "Grants 10 armor.<br><br>{{as|'''This augment is currently disabled.'''|buzzword3}}", ["tier"] = "Silver" },
            ["Lucky"] = { ["description"] = "Grants {{as|50% critical strike chance}}.", ["tier"] = "Gold" },
            ["Rhythm"] = { ["description"] = "Your critical strikes grant you 6% bonus attack speed.", ["tier"] = "Gold" },
            ["Pack Leader"] = { ["description"] = "Your pets deal 40% increased damage.", ["tier"] = "Gold" },
            ["Plated"] = { ["description"] = "Upgrades Plate, granting 50 {{as|armor}}.", ["tier"] = "Gold" },
            ["Echo I"] = { ["description"] = "Basic attacks deal 30 bonus magic damage.", ["tier"] = "Gold" },
            ["Echo II"] = { ["description"] = "Basic attacks deal 40 bonus magic damage.", ["tier"] = "Prismatic" },
            ["Echo III"] = { ["description"] = "Basic attacks deal 50 bonus magic damage.", ["tier"] = "Prismatic" },
            ["Warded"] = { ["description"] = "Grants 40 bonus {{as|magic resistance}}.", ["tier"] = "Silver" },
            ["Rhythm Plus"] = { ["description"] = "Your critical strikes grant attack speed. Additionally, gain 25% critical strike chance.", ["tier"] = "Prismatic" },
        }
        """, TestData.Static.Items);

    public static AugmentInfo Get(string name) => Catalog.Find(name) ?? throw new ArgumentException(name);
}
