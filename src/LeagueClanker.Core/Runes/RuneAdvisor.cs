using System.Text.Json;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Runes;

/// <param name="Enemies">Enemy champions visible in champ select. Empty in blind pick until the game loads.</param>
public sealed record RuneRequest(ChampionInfo Champion, Archetype Playstyle, Position Position, GameMode Mode)
{
    public IReadOnlyList<ChampionInfo> Enemies { get; init; } = [];
}

/// <param name="Source">Where the page comes from, e.g. "op.gg" or "LeagueClanker rules".</param>
/// <param name="Reasons">Plain sentences explaining the page.</param>
public sealed record RuneRecommendation(RunePage Page, string Source, IReadOnlyList<string> Reasons)
{
    /// <summary>Games played with this page, for stats-site pages.</summary>
    public int? Games { get; init; }

    public double? WinRate { get; init; }
}

public interface IRuneSource
{
    /// <summary>Null when the source has no page for this champion and playstyle.</summary>
    Task<RuneRecommendation?> RecommendAsync(RuneRequest request, CancellationToken ct);
}

public enum RuneSourceKind
{
    /// <summary>The most played page on op.gg that fits your playstyle, with LeagueClanker's rules as the fallback.</summary>
    StatsSite,

    /// <summary>LeagueClanker's own pages per playstyle, adjusted to the enemy picks.</summary>
    OwnRules,
}

/// <summary>Picks the rune page from the source you chose, and falls back to the rules when that source has nothing.</summary>
public sealed class RuneAdvisor(RuleRuneSource rules, IRuneSource? statsSite = null)
{
    public async Task<RuneRecommendation> RecommendAsync(RuneRequest request, RuneSourceKind source, CancellationToken ct = default)
    {
        string? note = null;
        if (source == RuneSourceKind.StatsSite && statsSite is not null)
        {
            try
            {
                if (await statsSite.RecommendAsync(request, ct) is { } found)
                    return found;
                note = $"op.gg has no {request.Playstyle.DisplayName().ToLowerInvariant()} page for {request.Champion.Name}, so this is LeagueClanker's own page.";
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                note = "op.gg didn't answer, so this is LeagueClanker's own page.";
            }
        }

        var own = rules.Recommend(request);
        return note is null ? own : own with { Reasons = [note, .. own.Reasons] };
    }
}

/// <summary>Which keystones suit a playstyle. Stats-site pages whose keystone doesn't fit are skipped.</summary>
public static class KeystoneFit
{
    private static readonly Dictionary<Archetype, HashSet<string>> Fits = new()
    {
        [Archetype.Marksman] = Names("Lethal Tempo", "Press the Attack", "Fleet Footwork", "Hail of Blades", "First Strike", "Conqueror"),
        [Archetype.Mage] = Names("Arcane Comet", "Summon Aery", "Electrocute", "Dark Harvest", "Deathfire Touch", "First Strike",
            "Unsealed Spellbook", "Stormraider's Surge", "Glacial Augment"),
        [Archetype.ApAssassin] = Names("Electrocute", "Dark Harvest", "First Strike", "Arcane Comet", "Stormraider's Surge", "Hail of Blades",
            "Deathfire Touch", "Conqueror"),
        [Archetype.AdAssassin] = Names("Electrocute", "Dark Harvest", "Hail of Blades", "First Strike", "Fleet Footwork", "Conqueror", "Press the Attack"),
        [Archetype.Bruiser] = Names("Conqueror", "Grasp of the Undying", "Fleet Footwork", "Press the Attack", "Stormraider's Surge", "Lethal Tempo",
            "First Strike", "Hail of Blades"),
        [Archetype.ApBruiser] = Names("Conqueror", "Grasp of the Undying", "Stormraider's Surge", "Deathfire Touch", "Arcane Comet", "Aftershock",
            "Dark Harvest", "Electrocute", "Fleet Footwork"),
        [Archetype.Tank] = Names("Grasp of the Undying", "Aftershock", "Guardian", "Glacial Augment", "Unsealed Spellbook"),
        [Archetype.Enchanter] = Names("Summon Aery", "Guardian", "Glacial Augment", "Arcane Comet", "Unsealed Spellbook", "First Strike"),
    };

    public static bool Suits(Archetype playstyle, RuneInfo keystone) => Fits[playstyle].Contains(keystone.Name);

    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}
