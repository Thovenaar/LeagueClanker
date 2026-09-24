using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Runes;

/// <summary>
/// LeagueClanker's own rune pages: a standard page per playstyle, adjusted to the enemy picks. No win rates, so it works
/// offline and for off-meta playstyles that stats sites have too few games of. Runes are named rather than numbered;
/// if Riot removes one, the first rune of that row takes its place.
/// </summary>
public sealed class RuleRuneSource(RuneCatalog catalog) : IRuneSource
{
    public const string SourceName = "LeagueClanker rules";

    private const int TanksForCutDown = 2;
    private const int CrowdControlForTenacity = 2;
    private const int RangedForSecondWind = 3;
    private const int AssassinsForBonePlating = 2;

    private sealed record Template(
        string Description, string Primary, string[] PrimaryRunes, string Secondary, string[] SecondaryRunes, int[] Shards);

    private static readonly Dictionary<Archetype, Template> Templates = new()
    {
        [Archetype.Marksman] = new("Lethal Tempo for sustained autoattack damage",
            "Precision", ["Lethal Tempo", "Presence of Mind", "Legend: Bloodline", "Coup de Grace"],
            "Inspiration", ["Magical Footwear", "Biscuit Delivery"],
            [StatShards.AttackSpeed, StatShards.AdaptiveForce, StatShards.HealthScaling]),
        [Archetype.OnHit] = new("Lethal Tempo to stack attack speed for your on-hit items",
            "Precision", ["Lethal Tempo", "Presence of Mind", "Legend: Alacrity", "Cut Down"],
            "Domination", ["Taste of Blood", "Treasure Hunter"],
            [StatShards.AttackSpeed, StatShards.AdaptiveForce, StatShards.HealthScaling]),
        [Archetype.Mage] = new("Arcane Comet to poke with your abilities",
            "Sorcery", ["Arcane Comet", "Manaflow Band", "Transcendence", "Scorch"],
            "Inspiration", ["Magical Footwear", "Biscuit Delivery"],
            [StatShards.AdaptiveForce, StatShards.AdaptiveForce, StatShards.HealthScaling]),
        [Archetype.ApAssassin] = new("Electrocute to burst someone in one combo",
            "Domination", ["Electrocute", "Sudden Impact", "Grisly Mementos", "Relentless Hunter"],
            "Sorcery", ["Transcendence", "Gathering Storm"],
            [StatShards.AdaptiveForce, StatShards.AdaptiveForce, StatShards.HealthScaling]),
        [Archetype.AdAssassin] = new("Electrocute to burst someone in one combo",
            "Domination", ["Electrocute", "Sudden Impact", "Grisly Mementos", "Relentless Hunter"],
            "Precision", ["Triumph", "Legend: Haste"],
            [StatShards.AdaptiveForce, StatShards.AdaptiveForce, StatShards.HealthScaling]),
        [Archetype.Bruiser] = new("Conqueror for long fights",
            "Precision", ["Conqueror", "Triumph", "Legend: Haste", "Last Stand"],
            "Resolve", ["Bone Plating", "Overgrowth"],
            [StatShards.AttackSpeed, StatShards.AdaptiveForce, StatShards.HealthScaling]),
        [Archetype.ApBruiser] = new("Conqueror for long fights",
            "Precision", ["Conqueror", "Triumph", "Legend: Haste", "Last Stand"],
            "Resolve", ["Bone Plating", "Revitalize"],
            [StatShards.AttackSpeed, StatShards.AdaptiveForce, StatShards.HealthScaling]),
        [Archetype.Tank] = new("Grasp of the Undying to win trades in lane and stack health",
            "Resolve", ["Grasp of the Undying", "Demolish", "Bone Plating", "Overgrowth"],
            "Inspiration", ["Magical Footwear", "Biscuit Delivery"],
            [StatShards.AttackSpeed, StatShards.HealthScaling, StatShards.HealthScaling]),
        [Archetype.Enchanter] = new("Summon Aery to shield your team and poke",
            "Sorcery", ["Summon Aery", "Manaflow Band", "Transcendence", "Scorch"],
            "Resolve", ["Font of Life", "Revitalize"],
            [StatShards.AbilityHaste, StatShards.AdaptiveForce, StatShards.HealthScaling]),
    };

    // Support and jungle tanks start the fights instead of trading in lane. So do tanks in ARAM.
    private static readonly Template EngageTank = new("Aftershock to survive the fights you start",
        "Resolve", ["Aftershock", "Font of Life", "Second Wind", "Overgrowth"],
        "Precision", ["Legend: Haste", "Triumph"],
        [StatShards.AbilityHaste, StatShards.HealthScaling, StatShards.HealthScaling]);

    public Task<RuneRecommendation?> RecommendAsync(RuneRequest request, CancellationToken ct) =>
        Task.FromResult<RuneRecommendation?>(Recommend(request));

    public RuneRecommendation Recommend(RuneRequest request)
    {
        var template = request.Playstyle == Archetype.Tank
                       && (request.Position is Position.Support or Position.Jungle || request.Mode is GameMode.Aram or GameMode.AramMayhem)
            ? EngageTank
            : Templates[request.Playstyle];

        var reasons = new List<string> { $"{request.Playstyle.DisplayName()} page: {template.Description}." };
        var primary = template.PrimaryRunes.ToArray();
        var secondary = template.SecondaryRunes.ToArray();
        var shards = template.Shards.ToArray();
        AdjustToEnemies(request, primary, secondary, shards, reasons);

        return new RuneRecommendation(Build(template.Primary, primary, template.Secondary, secondary, shards), SourceName, reasons);
    }

    private static void AdjustToEnemies(RuneRequest request, string[] primary, string[] secondary, int[] shards, List<string> reasons)
    {
        var enemies = request.Enemies.Select(c => (Champion: c, Archetype: ArchetypeClassifier.Classify(c))).ToList();
        if (enemies.Count == 0)
            return;

        var tanks = enemies.Where(e => e.Archetype == Archetype.Tank).Select(e => e.Champion.Name).ToList();
        var coupDeGrace = Array.IndexOf(primary, "Coup de Grace");
        if (tanks.Count >= TanksForCutDown && coupDeGrace >= 0)
        {
            primary[coupDeGrace] = "Cut Down";
            reasons.Add($"Enemy has {tanks.Count} tanks ({string.Join(", ", tanks)}), so Cut Down instead of Coup de Grace.");
        }

        var crowdControl = enemies.Where(e => e.Champion.Has(ChampionTraits.HeavyCrowdControl)).Select(e => e.Champion.Name).ToList();
        if (crowdControl.Count >= CrowdControlForTenacity && request.Playstyle != Archetype.Tank && shards[2] != StatShards.Tenacity)
        {
            shards[2] = StatShards.Tenacity;
            reasons.Add($"Enemy has heavy crowd control ({string.Join(", ", crowdControl)}), so the tenacity shard.");
        }

        var ranged = enemies.Count(e => e.Champion.Stats.AttackRange >= 350);
        var assassins = enemies.Where(e => e.Archetype.IsAssassin()).Select(e => e.Champion.Name).ToList();
        if (ranged >= RangedForSecondWind && Swap(primary, secondary, "Bone Plating", "Second Wind"))
            reasons.Add($"Enemy has {ranged} ranged champions, so Second Wind against their poke instead of Bone Plating.");
        else if (assassins.Count >= AssassinsForBonePlating && Swap(primary, secondary, "Second Wind", "Bone Plating"))
            reasons.Add($"Enemy has {assassins.Count} assassins ({string.Join(", ", assassins)}), so Bone Plating against their burst.");
    }

    private static bool Swap(string[] primary, string[] secondary, string from, string to)
    {
        foreach (var runes in new[] { primary, secondary })
        {
            var index = Array.IndexOf(runes, from);
            if (index >= 0)
            {
                runes[index] = to;
                return true;
            }
        }
        return false;
    }

    private RunePage Build(string primaryName, string[] primaryRunes, string secondaryName, string[] secondaryRunes, int[] shards)
    {
        var primary = catalog.Style(primaryName) ?? throw new InvalidOperationException($"Rune tree {primaryName} not found.");
        var secondary = catalog.Style(secondaryName) ?? throw new InvalidOperationException($"Rune tree {secondaryName} not found.");

        var ids = new List<int>();
        for (var slot = 0; slot < 4; slot++)
            ids.Add(Pick(primary.Slots[slot], primaryRunes[slot]).Id);

        var usedSlots = new HashSet<int>();
        foreach (var name in secondaryRunes)
        {
            var slot = Enumerable.Range(1, secondary.Slots.Count - 1)
                .Where(s => !usedSlots.Contains(s))
                .OrderByDescending(s => secondary.Slots[s].Any(r => Same(r.Name, name)))
                .First();
            usedSlots.Add(slot);
            ids.Add(Pick(secondary.Slots[slot], name).Id);
        }

        ids.AddRange(shards);
        return new RunePage(primary.Id, secondary.Id, ids);
    }

    private static RuneInfo Pick(IReadOnlyList<RuneInfo> slot, string name) => slot.FirstOrDefault(r => Same(r.Name, name)) ?? slot[0];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
