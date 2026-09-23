using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Analysis;

/// <summary>How a champion builds. Decides which items are candidates and what the champion values.</summary>
public enum Archetype
{
    Marksman,
    Mage,
    AdAssassin,
    ApAssassin,
    Bruiser,
    ApBruiser,
    Tank,
    Enchanter,
}

public enum DamageType
{
    Physical,
    Magic,
    None, // Tanks and enchanters: utility first, damage type barely matters for their build.
}

public static class ArchetypeClassifier
{
    public static Archetype Classify(ChampionInfo champion)
    {
        if (ChampionKnowledge.ArchetypeOverrides.TryGetValue(champion.Id, out var overridden))
            return overridden;

        var apLeaning = champion.Magic > champion.Attack;
        return champion.PrimaryTag switch
        {
            "Marksman" => apLeaning && champion.Magic >= 7 ? Archetype.Mage : Archetype.Marksman,
            "Mage" => Archetype.Mage,
            "Assassin" => apLeaning ? Archetype.ApAssassin : Archetype.AdAssassin,
            "Fighter" => apLeaning ? Archetype.ApBruiser : Archetype.Bruiser,
            "Tank" => Archetype.Tank,
            "Support" => champion.SecondaryTag switch
            {
                "Tank" or "Fighter" => Archetype.Tank,
                "Assassin" => apLeaning ? Archetype.ApAssassin : Archetype.AdAssassin,
                "Marksman" => Archetype.Marksman,
                _ => Archetype.Enchanter,
            },
            _ => apLeaning ? Archetype.Mage : Archetype.Bruiser,
        };
    }

    public static DamageType DamageTypeOf(this Archetype archetype) => archetype switch
    {
        Archetype.Marksman or Archetype.AdAssassin or Archetype.Bruiser => DamageType.Physical,
        Archetype.Mage or Archetype.ApAssassin or Archetype.ApBruiser => DamageType.Magic,
        _ => DamageType.None,
    };

    public static bool IsSquishy(this Archetype archetype) =>
        archetype is Archetype.Marksman or Archetype.Mage or Archetype.AdAssassin or Archetype.ApAssassin or Archetype.Enchanter;

    public static bool IsFrontline(this Archetype archetype) =>
        archetype is Archetype.Tank or Archetype.Bruiser or Archetype.ApBruiser;

    public static bool IsAssassin(this Archetype archetype) =>
        archetype is Archetype.AdAssassin or Archetype.ApAssassin;

    public static string DisplayName(this Archetype archetype) => archetype switch
    {
        Archetype.AdAssassin => "AD assassin",
        Archetype.ApAssassin => "AP assassin",
        Archetype.ApBruiser => "AP bruiser",
        _ => archetype.ToString(),
    };
}
