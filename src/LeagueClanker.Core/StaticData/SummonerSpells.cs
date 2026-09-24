using System.Text.Json;

namespace LeagueClanker.Core.StaticData;

/// <param name="Id">The numeric id the client uses, e.g. 4 for Flash.</param>
/// <param name="Modes">Client mode names the spell is available in: "CLASSIC", "ARAM", "KIWI", "JADE", ...</param>
public sealed record SummonerSpell(int Id, string Name, string Image, IReadOnlySet<string> Modes);

/// <summary>Summoner spells from Data Dragon's summoner.json.</summary>
public sealed class SummonerSpellCatalog
{
    public const int Cleanse = 1;
    public const int Exhaust = 3;
    public const int Flash = 4;
    public const int Ghost = 6;
    public const int Heal = 7;
    public const int Smite = 11;
    public const int Teleport = 12;
    public const int Clarity = 13;
    public const int Ignite = 14;
    public const int Barrier = 21;
    public const int Mark = 32;

    public static readonly SummonerSpellCatalog Empty = new([]);

    private readonly Dictionary<int, SummonerSpell> _byId;

    public SummonerSpellCatalog(IEnumerable<SummonerSpell> spells) => _byId = spells.ToDictionary(s => s.Id);

    public IEnumerable<SummonerSpell> All => _byId.Values;

    public SummonerSpell? Get(int id) => _byId.GetValueOrDefault(id);

    public bool IsAvailable(int id, GameMode mode) => Get(id)?.Modes.Contains(mode.ClientModeName()) == true;

    /// <summary>
    /// League Classic has its own copies of the spells (Flash 74 instead of 4). Returns the copy with the same name
    /// that's available in <paramref name="mode"/>, or the spell itself.
    /// </summary>
    public int ForMode(int id, GameMode mode)
    {
        if (IsAvailable(id, mode) || Get(id) is not { } spell)
            return id;
        return All.Where(s => s.Name == spell.Name && s.Modes.Contains(mode.ClientModeName())).Select(s => s.Id).DefaultIfEmpty(id).First();
    }

    public static SummonerSpellCatalog Parse(string summonerJson)
    {
        using var doc = JsonDocument.Parse(summonerJson);
        var spells = doc.RootElement.GetProperty("data").EnumerateObject().Select(entry =>
        {
            var s = entry.Value;
            return new SummonerSpell(
                int.Parse(s.GetStringOrEmpty("key")),
                s.GetStringOrEmpty("name"),
                s.GetProperty("image").GetStringOrEmpty("full"),
                s.GetStringArray("modes").ToHashSet(StringComparer.OrdinalIgnoreCase));
        });
        return new SummonerSpellCatalog(spells);
    }
}
