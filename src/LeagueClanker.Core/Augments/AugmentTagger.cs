using System.Text.RegularExpressions;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Augments;

/// <summary>
/// Derives what an augment gives and what it needs from its description text, the same way items get traits.
/// The patterns follow the wiki's wording; <see cref="Overrides"/> fixes the cards regexes get wrong.
/// </summary>
internal static partial class AugmentTagger
{
    private sealed record StatPattern(AugmentEffect Effect, AugmentTrigger ScalesWith, Regex Pattern);

    // A stat word is "given" when a number or a give-verb leads up to it, and "scaled with" when a ratio does.
    private static readonly StatPattern[] Stats =
    [
        new(AugmentEffect.AttackDamage, AugmentTrigger.AttackDamage, new(@"attack damage|\bAD\b", RegexOptions.Compiled)),
        new(AugmentEffect.AbilityPower, AugmentTrigger.AbilityPower, new(@"ability power|\bAP\b", RegexOptions.Compiled)),
        new(AugmentEffect.AttackSpeed, AugmentTrigger.None, new(@"attack speed", RegexOptions.Compiled)),
        new(AugmentEffect.CritChance, AugmentTrigger.Crits, new(@"critical strike chance", RegexOptions.Compiled)),
        new(AugmentEffect.CritDamage, AugmentTrigger.None, new(@"critical (strike )?damage", RegexOptions.Compiled)),
        new(AugmentEffect.AbilityHaste, AugmentTrigger.AbilityHaste, new(@"(?<!item |summoner spell )ability haste", RegexOptions.Compiled)),
        new(AugmentEffect.Health, AugmentTrigger.MaxHealth, new(@"health(?! regeneration| cost| threshold)", RegexOptions.Compiled)),
        new(AugmentEffect.Armor, AugmentTrigger.BonusResists, new(@"\barmor\b(?! penetration)", RegexOptions.Compiled)),
        new(AugmentEffect.MagicResist, AugmentTrigger.BonusResists, new(@"magic resist(ance)?(?! penetration)", RegexOptions.Compiled)),
        new(AugmentEffect.MoveSpeed, AugmentTrigger.MoveSpeed, new(@"movement speed", RegexOptions.Compiled)),
        new(AugmentEffect.Penetration, AugmentTrigger.None, new(@"penetration|lethality", RegexOptions.Compiled)),
        new(AugmentEffect.Omnivamp, AugmentTrigger.None, new(@"omnivamp|life steal|physical vamp", RegexOptions.Compiled)),
        new(AugmentEffect.HealShieldPower, AugmentTrigger.None, new(@"heal and shield power|increased healing and shielding", RegexOptions.Compiled)),
        new(AugmentEffect.Tenacity, AugmentTrigger.None, new(@"tenacity|crowd control immunity|slow resist", RegexOptions.Compiled)),
        new(AugmentEffect.AttackRange, AugmentTrigger.None, new(@"attack range", RegexOptions.Compiled)),
        new(AugmentEffect.AdaptiveForce, AugmentTrigger.None, new(@"adaptive force", RegexOptions.Compiled)),
    ];

    private static readonly (AugmentEffect Effect, Regex Pattern)[] Mechanics =
    [
        (AugmentEffect.TrueDamage, new(@"true damage", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.MaxHealthDamage, new(@"(target's|their|enemies'|enemy's) (maximum|current|missing) health", RegexOptions.Compiled)),
        (AugmentEffect.Burn, new(@"\bBurn\b", RegexOptions.Compiled)),
        (AugmentEffect.ResistShred, new(@"reduc\w* (their|the target's|its) (armor|magic resist)", RegexOptions.Compiled)),
        (AugmentEffect.Shield, new(@"(gain|grants?)( you)? a shield|a shield (that|for)|shields? that absorbs?", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.Heal, new(@"heals? you|healed for|heal for|restores? \d", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.CrowdControl, new(@"\b(slows?|slowing|stuns?|stunning|polymorphs?|knocks? (up|back)|roots?|disarms?|airborne|fears?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.Execute, new(@"execut", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.SpellShield, new(@"spell shield", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.Invulnerable, new(@"invulnerab|untargetable|\bstasis\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.Stealth, new(@"invisible|camouflage|renders you", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.Gold, new(@"\d gold\b", RegexOptions.Compiled)),
        (AugmentEffect.SummonerSpell, new(@"replace (a|one of your) summoner spell|summoner spell haste", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.Keystones, new(@"keystone", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.OnHit, new(@"appl(y|ies) on-hit effects", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.AntiHeal, new(@"\bWounds\b", RegexOptions.Compiled)),
        (AugmentEffect.Dash, new(@"\b(dash|blink) to\b|cause you to dash", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentEffect.Damage, new(@"\b(deal|deals|dealing)\b[^.]{0,80}\bdamage\b|increased damage|damage (is|are) increased", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    private static readonly (AugmentTrigger Trigger, Regex Pattern)[] Triggers =
    [
        (AugmentTrigger.Attacks, new(@"basic attacks?|on-attack|\battacking\b|attack against|with an attack", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Crits, new(@"your critical strikes|critical strikes (grant|deal|heal)|by your critical strikes|can now critically strike|equal to your critical strike chance", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.AbilityHits, new(@"abilit(y|ies) (hit|hits|damage)|damaging abilit|with an ability|with a basic ability|your (champion )?abilities|cast(ing)? (an |your )?abilit|abilities (deal|apply|can|have)|basic ability|champion's abilities|whenever you cast|cast a different ability|with a specific ability", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Ultimate, new(@"\bultimate\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Healing, new(@"heal(s|ing)? (and health regeneration )?you do|your heals|self and outgoing healing|increased healing and shielding from all sources", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.AllySupport, new(@"(heal|shield|buff)[^.]{0,40}\b(ally|allies|allied)\b|\b(ally|allies|allied)\b[^.]{0,40}(heal|shield)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Shields, new(@"upon gaining a shield|your heals and shields", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.LowHealth, new(@"below \d+% (of your )?maximum health|missing health|dropping below", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Immobilize, new(@"immobiliz|grounding", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Takedowns, new(@"takedown|\bkill", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Dashes, new(@"dashing|blinking|abilities with dashes|dashes or blinks", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Pets, new(@"\bpets?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Spinning, new(@"\bspinning\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.SummonerSpells, new(@"using flash|your flash|your mark\b|using .{0,20}summoner", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Death, new(@"upon death|on death", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Distance, new(@"units away|based on distance", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Stealth, new(@"exiting stealth|while (in )?stealth", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Mana, new(@"maximum mana|mana costs?", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        (AugmentTrigger.Stacking, new(@"permanent stacks", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>Hand-kept corrections for cards whose wording the patterns misread.</summary>
    private static readonly Dictionary<string, (AugmentEffect Add, AugmentTrigger AddTriggers, AugmentEffect Remove)> Overrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Converts bonus AD into AP: it pays off on AD you pick up elsewhere, and gives AP.
            ["ADAPt"] = (AugmentEffect.AbilityPower, AugmentTrigger.AttackDamage, AugmentEffect.AttackDamage),
            ["EscAPADe"] = (AugmentEffect.AttackDamage, AugmentTrigger.AbilityPower, AugmentEffect.AbilityPower),
            // Polymorphing enemies is crowd control; the "movement speed" is theirs, not yours.
            ["Fey Magic"] = (AugmentEffect.CrowdControl, AugmentTrigger.None, AugmentEffect.MoveSpeed),
            // Dragon souls and stat anvils are described by name only.
            ["Infernal Soul"] = (AugmentEffect.AdaptiveForce | AugmentEffect.Damage, AugmentTrigger.None, AugmentEffect.None),
            ["Mountain Soul"] = (AugmentEffect.Shield, AugmentTrigger.None, AugmentEffect.None),
            ["Ocean Soul"] = (AugmentEffect.Heal, AugmentTrigger.None, AugmentEffect.None),
            ["Hextech Soul"] = (AugmentEffect.AttackSpeed | AugmentEffect.AbilityHaste | AugmentEffect.Damage, AugmentTrigger.None, AugmentEffect.None),
            ["Omni Soul"] = (AugmentEffect.AdaptiveForce | AugmentEffect.Shield | AugmentEffect.Heal, AugmentTrigger.None, AugmentEffect.None),
            ["Stats!"] = (AugmentEffect.AdaptiveForce | AugmentEffect.Health, AugmentTrigger.None, AugmentEffect.None),
            ["Stats on Stats!"] = (AugmentEffect.AdaptiveForce | AugmentEffect.Health, AugmentTrigger.None, AugmentEffect.None),
            ["Stats on Stats on Stats!"] = (AugmentEffect.AdaptiveForce | AugmentEffect.Health | AugmentEffect.AbilityHaste, AugmentTrigger.None, AugmentEffect.None),
            // Only worth it for champions whose kit has a shield.
            ["Bolstered"] = (AugmentEffect.Shield, AugmentTrigger.Shields, AugmentEffect.None),
        };

    public static AugmentInfo Tag(string name, Dictionary<string, object?> entry, ItemCatalog? items)
    {
        var rawDescription = entry.GetValueOrDefault("description") as string ?? "";
        var description = WikiText.Clean(rawDescription);
        var tier = Enum.TryParse<AugmentTier>(entry.GetValueOrDefault("tier") as string, ignoreCase: true, out var t) ? t : AugmentTier.Silver;

        var (effects, triggers) = TagText(description);
        effects |= Mechanics.Where(m => m.Pattern.IsMatch(description)).Aggregate(AugmentEffect.None, (acc, m) => acc | m.Effect);
        triggers |= Triggers.Where(tr => tr.Pattern.IsMatch(description)).Aggregate(AugmentTrigger.None, (acc, tr) => acc | tr.Trigger);

        if (Overrides.TryGetValue(name, out var fix))
        {
            effects = (effects | fix.Add) & ~fix.Remove;
            triggers |= fix.AddTriggers;
        }

        return new AugmentInfo
        {
            Name = name,
            Tier = tier,
            Description = description,
            Effects = effects,
            Triggers = triggers,
            MentionedItems = items is null ? [] : FindItems(description, items),
            CritChanceBonus = Amount(CritGrantRegex(), description),
            AttackSpeedBonus = Amount(AttackSpeedGrantRegex(), description),
            HasDrawback = DrawbackRegex().IsMatch(description),
            IsRandom = RandomRegex().IsMatch(description),
            IsQuest = entry.ContainsKey("questinfo") || name.StartsWith("Quest:", StringComparison.OrdinalIgnoreCase),
            IsDisabled = description.Contains("currently disabled", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Walks every stat mention and decides whether the card gives that stat or scales with it.</summary>
    internal static (AugmentEffect Effects, AugmentTrigger Triggers) TagText(string description)
    {
        var effects = AugmentEffect.None;
        var triggers = AugmentTrigger.None;

        foreach (var sentence in SentenceRegex().Split(description))
        {
            foreach (var stat in Stats)
            {
                foreach (Match m in stat.Pattern.Matches(sentence))
                {
                    var before = sentence[..m.Index];
                    var lead = before.Length > 40 ? before[^40..] : before;

                    if (EnemyStatRegex().IsMatch(lead) || LowHealthRegex().IsMatch(lead))
                        continue; // the target's health, reducing *their* armor, "below 35% of your maximum health"
                    if (ScalingRegex().IsMatch(lead))
                        triggers |= stat.ScalesWith;
                    else if (NumberLeadRegex().IsMatch(lead) || GiveVerbRegex().IsMatch(before))
                        effects |= stat.Effect;
                }
            }
        }

        return (effects, triggers);
    }

    private static double Amount(Regex pattern, string description) =>
        pattern.Match(description) is { Success: true } m ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private static List<ItemInfo> FindItems(string description, ItemCatalog items)
    {
        var mentioned = items.All
            .Where(i => i.Id <= 9999 && i.Name.Length > 3 && (i.Kind is ItemKind.Legendary or ItemKind.Boots or ItemKind.Component))
            .Where(i => Regex.IsMatch(description, $@"\b{Regex.Escape(i.Name)}\b"))
            .DistinctBy(i => i.Name)
            .ToList();

        // "Upgrades all Spellblade items" names an item passive rather than an item.
        foreach (var passive in SpellbladeRegex().Matches(description).Select(m => m.Value).Distinct())
            mentioned.AddRange(items.All.Where(i => i.Kind == ItemKind.Legendary && i.Passives.Contains(passive) && mentioned.All(x => x.Name != i.Name)));

        return mentioned;
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"(per [\d.]+ (bonus |maximum )?|equal to [\d.]+% (of your )?(bonus |total |maximum )?|\(\+ [\d.]+% (bonus |of your )?(maximum )?|% of your (bonus |maximum )?|convert all of your (bonus )?|based on your (bonus |missing )?)$")]
    private static partial Regex ScalingRegex();

    [GeneratedRegex(@"([\d.]+%?|[\d.]+ to [\d.]+) (bonus |total |maximum |increased )?$")]
    private static partial Regex NumberLeadRegex();

    [GeneratedRegex(@"\b(grants?|gain|gains|increases?|additionally|upgrades?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GiveVerbRegex();

    [GeneratedRegex(@"(target's|their|enemies'|enemy's|reduces? (the target's |their )?|reduc\w+ their )(maximum |current |missing |base )?$")]
    private static partial Regex EnemyStatRegex();

    [GeneratedRegex(@"below [\d.]+% (of your )?(maximum )?$")]
    private static partial Regex LowHealthRegex();

    [GeneratedRegex(@"\bbut\b|cannot|can no longer|permanently sealed|health cost|costs are doubled|reduce your damage", RegexOptions.IgnoreCase)]
    private static partial Regex DrawbackRegex();

    [GeneratedRegex(@"random [\w-]+ augments?|random augments?|random Prismatic|random Gold", RegexOptions.IgnoreCase)]
    private static partial Regex RandomRegex();

    [GeneratedRegex(@"\b(?:gain|grants?)\s+(\d+)% critical strike chance", RegexOptions.IgnoreCase)]
    private static partial Regex CritGrantRegex();

    [GeneratedRegex(@"\b(?:gain|grants?)\s+(\d+)% bonus attack speed", RegexOptions.IgnoreCase)]
    private static partial Regex AttackSpeedGrantRegex();

    [GeneratedRegex(@"\bSpellblade\b")]
    private static partial Regex SpellbladeRegex();
}
