using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Recommendation;

namespace LeagueClanker.Core.Tests;

public class RecommendationEngineTests
{
    private static readonly (string, int[])[] FiveMages =
        [("Annie", []), ("Syndra", []), ("Lux", []), ("Brand", []), ("Xerath", [])];

    [Fact]
    public void AllApEnemies_PutsMagicResistFirst()
    {
        var rec = Recommend(TestData.Game(allies: [("Garen", [])], enemies: FiveMages));

        Assert.Contains(rec.Items[0].Item.Id, new[] { TestData.Cloak, TestData.Veil });
        Assert.Equal(TestData.MagicBoots, rec.Boots!.Item.Id);
        var advice = Assert.Single(rec.Advice, a => a.Situation.Label == "vs AP");
        Assert.Matches(@"^Enemy team is 9\d% AP, so I suggest (Cloak|Veil), but .+ is also ok\.$", advice.Text);
    }

    [Fact]
    public void AllApEnemies_SecondMagicResistItemIsWorthLess()
    {
        var rec = Recommend(TestData.Game(allies: [("Garen", [])], enemies: FiveMages));
        var vsAp = rec.Situations.Single(s => s.Label == "vs AP");

        var mrItems = rec.Items.Where(i => i.Item.Id is TestData.Cloak or TestData.Veil).ToList();

        Assert.Equal(2, mrItems.Count);
        Assert.True(mrItems[1].PointsFor(vsAp) < 0.6 * vsAp.Score(mrItems[1].Item),
            "diminishing returns should roughly halve the second MR item's bonus");
    }

    [Fact]
    public void OwningMagicResist_LowersThePriorityOfMore()
    {
        var fresh = Recommend(TestData.Game(allies: [("Garen", [])], enemies: FiveMages));
        var owning = Recommend(TestData.Game(allies: [("Garen", [TestData.Cloak, TestData.MagicBoots])], enemies: FiveMages));

        Assert.DoesNotContain(owning.Items, i => i.Item.Id == TestData.Cloak);
        Assert.Null(owning.Boots);
        Assert.True(Points(owning, TestData.Veil, "vs AP") < Points(fresh, TestData.Veil, "vs AP"));
    }

    [Fact]
    public void ThreeTanks_PrefersArmorPenetrationOverLethality()
    {
        var rec = Recommend(TestData.Game(
            allies: [("Jinx", [])],
            enemies: [("Ornn", []), ("Sejuani", []), ("Braum", []), ("Caitlyn", []), ("Syndra", [])]));

        var ranking = rec.Items.Select(i => i.Item.Id).ToList();
        Assert.True(ranking.IndexOf(TestData.PenBow) < ranking.IndexOf(TestData.LethBlade));

        var advice = Assert.Single(rec.Advice, a => a.Situation.Label == "vs tanks");
        Assert.Equal(TestData.PenBow, advice.Suggested.Id);
        Assert.Contains("3 tanks", advice.Text);
    }

    [Fact]
    public void TanksThatBuiltDamage_AreNotTreatedAsTanks()
    {
        var rec = Recommend(TestData.Game(
            allies: [("Jinx", [])],
            enemies:
            [
                ("Ornn", [TestData.CritSword, TestData.CritSword]),
                ("Sejuani", [TestData.CritSword, TestData.CritSword]),
                ("Braum", [TestData.CritSword, TestData.CritSword]),
                ("Caitlyn", [TestData.CritSword]),
                ("Syndra", []),
            ]));

        Assert.Empty(rec.Game.Enemies.Tanks);
        Assert.DoesNotContain(rec.Situations, s => s.Label == "vs tanks");
    }

    [Fact]
    public void TankRule_AnswersWhatTheTanksActuallyStacked()
    {
        (string, int[])[] Tanks(params int[] items) =>
            [("Ornn", items), ("Sejuani", items), ("Braum", items), ("Caitlyn", []), ("Syndra", [])];

        var armorStack = Recommend(TestData.Game(allies: [("Jinx", [])], enemies: Tanks(TestData.Plate, TestData.Plate)));
        var healthStack = Recommend(TestData.Game(allies: [("Jinx", [])], enemies: Tanks(TestData.Heart, TestData.Heart)));

        // Against armor, % penetration wins; against pure health, %health damage wins.
        Assert.True(RawPoints(armorStack, TestData.PenBow, "vs tanks") > RawPoints(armorStack, TestData.GiantSlayer, "vs tanks"));
        Assert.True(RawPoints(healthStack, TestData.GiantSlayer, "vs tanks") > RawPoints(healthStack, TestData.PenBow, "vs tanks"));
        Assert.Contains("armor", armorStack.Situations.Single(s => s.Label == "vs tanks").Description);
    }

    [Fact]
    public void HeavyHealing_SuggestsAntiHealAndItsComponent()
    {
        var rec = Recommend(TestData.Game(allies: [("Garen", [])], enemies: Healers));

        var advice = Assert.Single(rec.Advice, a => a.Situation.Label == "anti-heal");
        Assert.Equal(TestData.WoundBlade, advice.Suggested.Id);
        Assert.Equal(TestData.WoundComponent, advice.RushComponent?.Id);
        Assert.Contains("rush Wound Dagger early", advice.Text);
    }

    [Fact]
    public void AllyWithAntiHeal_MakesItLessUrgent()
    {
        var alone = Recommend(TestData.Game(allies: [("Garen", []), ("Jinx", [])], enemies: Healers));
        var covered = Recommend(TestData.Game(allies: [("Garen", []), ("Jinx", [TestData.WoundBlade])], enemies: Healers));

        var aloneSituation = alone.Situations.Single(s => s.Label == "anti-heal");
        var coveredSituation = covered.Situations.Single(s => s.Label == "anti-heal");
        Assert.True(coveredSituation.Impact < aloneSituation.Impact / 2);
        Assert.Contains("Jinx already applies anti-heal", coveredSituation.Description);
    }

    [Fact]
    public void OwningAntiHeal_DropsTheSituation()
    {
        var rec = Recommend(TestData.Game(allies: [("Garen", [TestData.WoundComponent])], enemies: Healers));

        Assert.DoesNotContain(rec.Situations, s => s.Label == "anti-heal");
    }

    [Fact]
    public void ItemsThatDontFitTheArchetype_AreNeverSuggested()
    {
        var rec = Recommend(TestData.Game(allies: [("Jinx", [])], enemies: FiveMages));

        // Pure defensive items are below a marksman's minimum fit even against a full AP team.
        Assert.DoesNotContain(rec.Items, i => i.Item.Id is TestData.Plate or TestData.Cloak or TestData.Veil);
    }

    private static readonly (string, int[])[] Healers =
        [("Soraka", []), ("Aatrox", []), ("Vladimir", []), ("Zed", []), ("Caitlyn", [])];

    private static BuildRecommendation Recommend(AllGameData game) =>
        new RecommendationEngine(TestData.Static).Recommend(GameAnalyzer.Analyze(game, TestData.Static)!, maxItems: 10);

    /// <summary>The situation's bonus before diminishing returns, so ranking order doesn't affect the comparison.</summary>
    private static double RawPoints(BuildRecommendation rec, int itemId, string label) =>
        rec.Situations.Single(s => s.Label == label).Score(TestData.Static.Items.Get(itemId)!);

    private static double Points(BuildRecommendation rec, int itemId, string label) =>
        rec.Items.Single(i => i.Item.Id == itemId).PointsFor(rec.Situations.Single(s => s.Label == label));
}
