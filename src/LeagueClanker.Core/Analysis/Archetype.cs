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

    /// <summary>Attack speed and on-hit items (Blade of the Ruined King, Nashor's Tooth), physical or magic.</summary>
    OnHit,
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

    /// <summary>On-hit can go either way; <see cref="PlayerProfile.DamageType"/> decides it from the champion and items.</summary>
    public static DamageType DamageTypeOf(this Archetype archetype) => archetype switch
    {
        Archetype.Marksman or Archetype.AdAssassin or Archetype.Bruiser or Archetype.OnHit => DamageType.Physical,
        Archetype.Mage or Archetype.ApAssassin or Archetype.ApBruiser => DamageType.Magic,
        _ => DamageType.None,
    };

    public static bool IsSquishy(this Archetype archetype) =>
        archetype is Archetype.Marksman or Archetype.Mage or Archetype.AdAssassin or Archetype.ApAssassin or Archetype.Enchanter or Archetype.OnHit;

    public static bool IsFrontline(this Archetype archetype) =>
        archetype is Archetype.Tank or Archetype.Bruiser or Archetype.ApBruiser;

    public static bool IsAssassin(this Archetype archetype) =>
        archetype is Archetype.AdAssassin or Archetype.ApAssassin;

    /// <summary>For use inside a sentence: "marksman", "AP assassin" (AD and AP stay capitalized), "on-hit".</summary>
    public static string InText(this Archetype archetype) =>
        string.Join(" ", archetype.DisplayName().Split(' ').Select(w => w is "AD" or "AP" ? w : w.ToLowerInvariant()));

    /// <summary>"a marksman", "an AP assassin", "an on-hit champion".</summary>
    public static string WithArticle(this Archetype archetype)
    {
        var text = archetype == Archetype.OnHit ? "on-hit champion" : archetype.InText();
        return ("aeiou".Contains(char.ToLowerInvariant(text[0])) ? "an " : "a ") + text;
    }

    public static string DisplayName(this Archetype archetype) => archetype switch
    {
        Archetype.AdAssassin => "AD assassin",
        Archetype.ApAssassin => "AP assassin",
        Archetype.ApBruiser => "AP bruiser",
        Archetype.OnHit => "On-hit",
        _ => archetype.ToString(),
    };
}
