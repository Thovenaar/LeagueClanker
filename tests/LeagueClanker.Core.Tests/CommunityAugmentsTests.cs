using LeagueClanker.Core.Augments;

namespace LeagueClanker.Core.Tests;

public class CommunityAugmentsTests
{
    // Trimmed from arammayhem.com/augments/ (patch 26.19): one ranking row per card.
    private const string Ranking = """
        <a href="/augments/spin-to-win/" class="augment-rank-row grid" data-name="spin to win" data-rarity="silver" data-availability="live" data-live-rank="50"><div><span data-rank-label>50</span></div><div class="flex"><img src="/augments/Spin_To_Win_mayhem_augment.webp" alt="Spin To Win" width="44"><div><span>Spin To Win</span></div><div><span>Silver</span><span class="rounded border sm:hidden">16.59%</span></div></div><div class="text-right font-data text-base font-semibold text-foreground sm:text-lg">54.25%</div><div class="hidden text-right font-data text-sm text-muted-foreground sm:block">16.59%</div></a>
        <a href="/augments/spin-me-right-round/" class="augment-rank-row grid" data-name="spin me right round" data-rarity="silver" data-availability="live" data-live-rank="193"><img src="/x.webp" alt="Spin Me Right Round" width="44"><div class="text-right font-data text-base font-semibold text-foreground sm:text-lg">44.46%</div><div class="hidden text-right font-data text-sm text-muted-foreground sm:block">3.06%</div></a>
        <a href="/augments/old-card/" class="augment-rank-row grid" data-name="old card" data-rarity="gold" data-availability="disabled"><img src="/y.webp" alt="Old Card" width="44"><div class="text-right font-data text-base font-semibold">70.00%</div></a>
        """;

    // Trimmed from arammayhem.com/build/garen/.
    private const string Garen = """
        <h2>Other stuff</h2><div title="Spin To Win">Spin To Win</div>
        <h2 class="x">Best Augments for Garen</h2>
        <div class="line-clamp-2 font-medium" title="Spin To Win" data-astro-cid-kaiunzud>Spin To Win</div><div class="mt-1 flex" data-astro-cid-kaiunzud><span data-astro-cid-kaiunzud>Appearance rate: <span class="font-data text-foreground" data-astro-cid-kaiunzud>12.63%</span></span><span>Win rate: <span>54.25%</span></span></div>
        """;

    [Fact]
    public void ParseRanking_ReadsWinAndPickRates_AndSkipsDisabledCards()
    {
        var stats = CommunityAugments.ParseRanking(Ranking);

        Assert.Equal(["Spin To Win", "Spin Me Right Round"], stats.Select(s => s.Name));
        Assert.Equal(0.5425, stats[0].WinRate, 4);
        Assert.Equal(0.1659, stats[0].PickRate, 4);
        Assert.Equal(0.4446, stats[1].WinRate, 4);
    }

    [Fact]
    public void ParseChampion_ReadsTheBestAugmentsSection()
    {
        var picks = CommunityAugments.ParseChampion(Garen);

        Assert.Equal(0.1263, picks.Single(p => p.Key == "Spin To Win").Value, 4);
    }

    [Fact]
    public void Points_FollowTheWinRate_PlusABonusForYourChampionsFavorites()
    {
        var community = new CommunityAugments(CommunityAugments.ParseRanking(Ranking), CommunityAugments.ParseChampion(Garen), "Garen");
        var reasons = new List<ScoreReason>();

        var spin = community.Points(Card("Spin To Win"), reasons);
        var swing = community.Points(Card("Spin Me Right Round"), null);

        Assert.Equal(0.85 + CommunityAugments.ChampionFavoritePoints, spin, 2); // 54.25% is 4.25 points over 50%
        Assert.Equal(-1.11, swing, 2);
        Assert.Contains(reasons, r => r.Text == "a top pick on Garen (13% of games)");
        Assert.Equal(0, community.Points(Card("Unknown Card"), null));
    }

    [Fact]
    public void ChampionSlug_MatchesTheSitesUrls()
    {
        Assert.Equal("drmundo", CommunityAugments.ChampionSlug("Dr. Mundo"));
        Assert.Equal("kaisa", CommunityAugments.ChampionSlug("Kai'Sa"));
    }

    private static AugmentInfo Card(string name) => new() { Name = name, Tier = AugmentTier.Silver, Description = "" };
}
