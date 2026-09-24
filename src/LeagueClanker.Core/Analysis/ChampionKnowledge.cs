using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Analysis;

/// <summary>
/// Hand-curated champion traits Data Dragon does not expose. Keys are Data Dragon ids.
/// Healers, shielders, crowd control and true damage also come from ability tooltips (<see cref="ChampionAbilities"/>),
/// which catches new champions. Others still work without them: they fall back to Riot's class tags and item-based detection.
/// </summary>
public static class ChampionKnowledge
{
    /// <summary>Champions with strong self or ally healing. Anti-heal is valuable against these.</summary>
    public static readonly IReadOnlySet<string> Healers = Set(
        "Aatrox", "Alistar", "Briar", "DrMundo", "Ekko", "Fiddlesticks", "Fiora", "Gwen", "Hecarim", "Illaoi",
        "Janna", "Kayn", "Kindred", "Maokai", "Milio", "Nami", "Nasus", "Nidalee", "Olaf", "Renekton",
        "Seraphine", "Sona", "Soraka", "Swain", "Sylas", "TahmKench", "Taric", "Trundle", "Viego", "Vladimir",
        "Volibear", "Warwick", "Yuumi", "Zac");

    /// <summary>Champions that generate a lot of shields for themselves or allies.</summary>
    public static readonly IReadOnlySet<string> Shielders = Set(
        "Blitzcrank", "Camille", "Diana", "Ekko", "Galio", "Garen", "Ivern", "JarvanIV", "Janna", "Karma",
        "Lulu", "Lux", "Malphite", "Milio", "Mordekaiser", "Morgana", "Nautilus", "Nilah", "Orianna", "Poppy",
        "Rakan", "Riven", "Rumble", "Senna", "Seraphine", "Sett", "Shen", "Sion", "Sona", "TahmKench",
        "Taric", "Thresh", "Udyr", "Vi", "Yasuo", "Yuumi", "Zeri");

    /// <summary>Champions with significant true damage, which ignores armor and magic resist.</summary>
    public static readonly IReadOnlySet<string> TrueDamage = Set(
        "Camille", "Chogath", "Darius", "Fiora", "Gangplank", "Garen", "Gwen", "KSante", "MasterYi", "Olaf",
        "Pyke", "Sett", "Vayne");

    /// <summary>Champions with lots of hard crowd control (stuns, knock-ups, roots, suppression).</summary>
    public static readonly IReadOnlySet<string> HeavyCrowdControl = Set(
        "Ahri", "Alistar", "Amumu", "Annie", "Ashe", "Bard", "Blitzcrank", "Braum", "Cassiopeia", "Elise",
        "Fiddlesticks", "Galio", "Gragas", "Hecarim", "JarvanIV", "Janna", "Kennen", "Leona", "Lissandra", "Lux",
        "Malphite", "Maokai", "MonkeyKing", "Morgana", "Nami", "Nautilus", "Neeko", "Nunu", "Ornn", "Pantheon",
        "Poppy", "Rakan", "Rammus", "Rell", "Renata", "Sejuani", "Sett", "Sion", "Skarner", "Swain",
        "Syndra", "TahmKench", "Taric", "Thresh", "TwistedFate", "Urgot", "Veigar", "Vex", "Vi", "Warwick",
        "Yasuo", "Yone", "Zac", "Zyra");

    /// <summary>Champions with summons or pets (Tibbers, Daisy, turrets, plants, ...). Augments like Minionmancer need these.</summary>
    public static readonly IReadOnlySet<string> Pets = Set(
        "Annie", "Azir", "Elise", "Heimerdinger", "Illaoi", "Ivern", "Malzahar", "Naafiri", "Shaco", "Yorick", "Zyra");

    /// <summary>Champions with a dash or blink on a basic ability or ultimate.</summary>
    public static readonly IReadOnlySet<string> Dashers = Set(
        "Aatrox", "Ahri", "Akali", "Alistar", "Aurora", "Belveth", "Briar", "Caitlyn", "Camille", "Corki",
        "Diana", "Ekko", "Ezreal", "Fiora", "Fizz", "Galio", "Gnar", "Gragas", "Graves", "Gwen",
        "Hecarim", "Irelia", "JarvanIV", "Jax", "Kaisa", "Kalista", "Katarina", "Kayn", "Khazix", "Kindred",
        "Kled", "Leblanc", "LeeSin", "Leona", "Lucian", "Maokai", "MonkeyKing", "Naafiri", "Nidalee", "Nilah",
        "Ornn", "Pantheon", "Poppy", "Pyke", "Qiyana", "Quinn", "Rakan", "Rell", "Renekton", "Rengar",
        "Riven", "Samira", "Sejuani", "Shaco", "Shen", "Sylas", "Talon", "Tristana", "Tryndamere", "Urgot",
        "Vayne", "Vi", "Viego", "Warwick", "XinZhao", "Yasuo", "Yone", "Zed", "Zeri");

    /// <summary>Champions with a spinning ability (Garen E, Darius Q, Katarina R, ...).</summary>
    public static readonly IReadOnlySet<string> Spinners = Set(
        "Darius", "Draven", "Garen", "Katarina", "MonkeyKing", "Samira", "Tryndamere");

    /// <summary>Champions that go invisible or camouflaged.</summary>
    public static readonly IReadOnlySet<string> Stealthers = Set(
        "Akali", "Evelynn", "Khazix", "MonkeyKing", "Pyke", "Qiyana", "Rengar", "Senna", "Shaco", "Talon",
        "Twitch", "Vayne");

    /// <summary>Champions that gain permanent stacks from their abilities.</summary>
    public static readonly IReadOnlySet<string> Stackers = Set(
        "AurelionSol", "Bard", "Chogath", "Kindred", "Nasus", "Senna", "Sion", "Smolder", "Swain", "Thresh",
        "Veigar");

    /// <summary>Champions whose usual build doesn't match what their Riot class tags suggest.</summary>
    public static readonly IReadOnlyDictionary<string, Archetype> ArchetypeOverrides =
        new Dictionary<string, Archetype>(StringComparer.OrdinalIgnoreCase)
        {
            ["Gwen"] = Archetype.ApBruiser,
            ["Kalista"] = Archetype.OnHit,
            ["Kayle"] = Archetype.OnHit,
            ["KogMaw"] = Archetype.OnHit,
            ["MasterYi"] = Archetype.OnHit,
            ["Vayne"] = Archetype.OnHit,
            ["Nilah"] = Archetype.Marksman,
            ["Pantheon"] = Archetype.AdAssassin,
            ["Tryndamere"] = Archetype.Marksman,
            ["Yasuo"] = Archetype.Marksman,
            ["Yone"] = Archetype.Marksman,
        };

    /// <summary>Share of damage dealt as magic (0-1) before looking at items, where the archetype default is wrong.</summary>
    public static readonly IReadOnlyDictionary<string, double> MagicShareOverrides =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Akali"] = 0.85, ["Alistar"] = 0.9, ["Amumu"] = 0.9, ["Blitzcrank"] = 0.8, ["Braum"] = 0.9,
            ["Chogath"] = 0.8, ["Corki"] = 0.6, ["Diana"] = 0.95, ["DrMundo"] = 0.4, ["Ekko"] = 0.95,
            ["Elise"] = 0.9, ["Evelynn"] = 0.95, ["Fizz"] = 0.9, ["Galio"] = 0.9, ["Gragas"] = 0.9,
            ["Gwen"] = 0.85, ["Hecarim"] = 0.1, ["Jax"] = 0.35, ["Kaisa"] = 0.4, ["Katarina"] = 0.75,
            ["Kayle"] = 0.6, ["Kennen"] = 0.9, ["KogMaw"] = 0.45, ["KSante"] = 0.2, ["Leona"] = 0.85,
            ["Lillia"] = 0.95, ["Malphite"] = 0.85, ["Maokai"] = 0.9, ["Mordekaiser"] = 0.95, ["Nautilus"] = 0.55,
            ["Nidalee"] = 0.85, ["Nunu"] = 0.9, ["Ornn"] = 0.45, ["Poppy"] = 0.15, ["Pyke"] = 0.1,
            ["Rammus"] = 0.8, ["Rell"] = 0.85, ["Rumble"] = 0.95, ["Sejuani"] = 0.8, ["Shaco"] = 0.45,
            ["Shen"] = 0.45, ["Shyvana"] = 0.4, ["Singed"] = 0.95, ["Sion"] = 0.3, ["Skarner"] = 0.4,
            ["Sylas"] = 0.9, ["Taric"] = 0.8, ["Teemo"] = 0.9, ["Thresh"] = 0.8, ["TwistedFate"] = 0.8,
            ["Udyr"] = 0.5, ["Varus"] = 0.25, ["Volibear"] = 0.5, ["Warwick"] = 0.5, ["Zac"] = 0.9,
            ["Ezreal"] = 0.25,
        };

    /// <summary>The traits the lists above give a champion.</summary>
    public static ChampionTraits TraitsOf(string id) =>
        (Healers.Contains(id) ? ChampionTraits.Healer : 0)
        | (Shielders.Contains(id) ? ChampionTraits.Shielder : 0)
        | (HeavyCrowdControl.Contains(id) ? ChampionTraits.HeavyCrowdControl : 0)
        | (TrueDamage.Contains(id) ? ChampionTraits.TrueDamage : 0);

    private static HashSet<string> Set(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);
}
