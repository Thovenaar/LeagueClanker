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

    // Trimmed from arammayhem.com/build/garen/: six cards per rarity on the real page.
    private const string Garen = """
        <h2>Other stuff</h2><div title="Spin To Win">Spin To Win</div>
        <h2 class="x">Best Augments for Garen</h2>
        <div class="px-3 py-2 text-sm font-semibold text-rarity-gold" data-astro-cid-kaiunzud>Gold</div>
        <div class="line-clamp-2 font-medium" title="Tank Engine" data-astro-cid-kaiunzud>Tank Engine</div><div class="mt-1 flex" data-astro-cid-kaiunzud><span data-astro-cid-kaiunzud>Appearance rate: <span class="font-data text-foreground" data-astro-cid-kaiunzud>9.00%</span></span><span>Win rate: <span>59.89%</span></span></div>
        <div class="px-3 py-2 text-sm font-semibold text-rarity-silver" data-astro-cid-kaiunzud>Silver</div>
        <div class="line-clamp-2 font-medium" title="Spin To Win" data-astro-cid-kaiunzud>Spin To Win</div><div class="mt-1 flex" data-astro-cid-kaiunzud><span data-astro-cid-kaiunzud>Appearance rate: <span class="font-data text-foreground" data-astro-cid-kaiunzud>12.63%</span></span><span>Win rate: <span>54.25%</span></span></div>
        <div class="line-clamp-2 font-medium" title="Stay Resolute" data-astro-cid-kaiunzud>Stay Resolute</div><div class="mt-1 flex" data-astro-cid-kaiunzud><span data-astro-cid-kaiunzud>Appearance rate: <span class="font-data text-foreground" data-astro-cid-kaiunzud>6.30%</span></span><span>Win rate: <span>52.87%</span></span></div>
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
    public void ParseChampion_ReadsTheBestAugmentsSection_WithEachCardsStandingInItsRarity()
    {
        var picks = CommunityAugments.ParseChampion(Garen);

        Assert.Equal(["Tank Engine", "Spin To Win", "Stay Resolute"], picks.Keys);
        Assert.Equal(0.1263, picks["Spin To Win"].Appearance, 4);
        Assert.Equal(1, picks["Spin To Win"].Standing, 4);
        Assert.Equal(1, picks["Tank Engine"].Standing, 4); // the top gold card, though taken less than Spin To Win
        Assert.Equal(0.50, picks["Stay Resolute"].Standing, 2);
    }

    [Fact]
    public void Points_FollowTheWinRate_PlusABonusForYourChampionsFavorites()
    {
        var community = new CommunityAugments(CommunityAugments.ParseRanking(Ranking), CommunityAugments.ParseChampion(Garen), "Garen");
        var reasons = new List<ScoreReason>();

        var spin = community.Points(Card("Spin To Win"), reasons);
        var swing = community.Points(Card("Spin Me Right Round"), null);
        var resolute = community.FavoritePoints(Card("Stay Resolute"), reasons);

        // 54.25% is 4.25 points over 50%; the top silver pick gets the full favorite bonus.
        Assert.Equal(0.85 + CommunityAugments.ChampionFavoritePoints + CommunityAugments.ChampionTopPickPoints, spin, 2);
        Assert.Equal(-1.11, swing, 2);
        Assert.Equal(CommunityAugments.ChampionFavoritePoints + 0.5 * CommunityAugments.ChampionTopPickPoints, resolute, 2);
        Assert.Contains(reasons, r => r.Text == "the top pick on Garen (13% of games)");
        Assert.Contains(reasons, r => r.Text == "a top pick on Garen (6% of games)");
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
