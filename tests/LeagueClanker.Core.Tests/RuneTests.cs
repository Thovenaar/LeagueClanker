using System.Net;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Tests;

public class RuneTests
{
    private static readonly RuneCatalog Runes = RuneCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "runesReforged.json")));
    private static readonly RuleRuneSource Rules = new(Runes);

    private static readonly ChampionInfo Jinx = new("Jinx", "Jinx", ["Marksman"], 9, 2, 4, Key: 222);
    private static readonly ChampionInfo Leona = new("Leona", "Leona", ["Tank", "Support"], 4, 8, 3, Key: 89);
    private static readonly ChampionInfo Garen = new("Garen", "Garen", ["Fighter", "Tank"], 7, 7, 1, Key: 86);
    private static readonly ChampionInfo Lux = new("Lux", "Lux", ["Mage", "Support"], 2, 4, 9, Key: 99);
    private static readonly ChampionInfo Ornn = new("Ornn", "Ornn", ["Tank"], 5, 9, 3, Key: 516);
    private static readonly ChampionInfo Sejuani = new("Sejuani", "Sejuani", ["Tank"], 5, 7, 6, Key: 113);
    private static readonly ChampionInfo Zed = new("Zed", "Zed", ["Assassin"], 9, 2, 1, Key: 238);
    private static readonly ChampionInfo Talon = new("Talon", "Talon", ["Assassin"], 9, 3, 1, Key: 91);

    private static ChampionInfo Ranged(string name) =>
        new(name, name, ["Mage"], 2, 3, 9, new ChampionStats { AttackRange = 550 });

    private static string Name(int id) => Runes.Get(id)!.Name;

    [Fact]
    public void Parse_ReadsTheFiveTreesAndKnowsTheShards()
    {
        Assert.Equal(["Domination", "Inspiration", "Precision", "Resolve", "Sorcery"], Runes.Styles.Select(s => s.Name).Order());
        Assert.Contains(Runes.Style("Precision")!.Keystones, r => r.Name == "Conqueror");
        Assert.Equal("Adaptive Force", Name(StatShards.AdaptiveForce));
    }

    [Fact]
    public void Validate_AcceptsARealPage_AndExplainsBrokenOnes()
    {
        var page = Rules.Recommend(new RuneRequest(Jinx, Archetype.Marksman, Position.Bottom, GameMode.SummonersRift)).Page;
        var ids = page.PerkIds.ToList();

        Assert.Null(Runes.Validate(page));
        Assert.Contains("row 1", Runes.Validate(page with { PerkIds = [ids[1], ids[0], .. ids[2..]] }));
        Assert.Contains("different rows", Runes.Validate(page with { PerkIds = [.. ids[..5], ids[4], .. ids[6..]] }));
        Assert.Contains("shard", Runes.Validate(page with { PerkIds = [.. ids[..8], StatShards.AttackSpeed] }));
        Assert.Contains("9 runes", Runes.Validate(page with { PerkIds = ids[..8] }));
    }

    [Fact]
    public void Rules_GiveEveryPlaystyleAValidPage_InEveryRoleAndMode()
    {
        foreach (var playstyle in Playstyles.All)
        foreach (var position in Enum.GetValues<Position>())
        foreach (var mode in new[] { GameMode.SummonersRift, GameMode.Aram, GameMode.AramMayhem })
        {
            var page = Rules.Recommend(new RuneRequest(Leona, playstyle, position, mode) { Enemies = [Ornn, Sejuani, Zed, Talon, Ranged("Xerath")] }).Page;
            Assert.True(Runes.Validate(page) is null, $"{playstyle} {position} {mode}: {Runes.Validate(page)}");
        }
    }

    [Fact]
    public void Rules_UseAftershockForEngageTanks_AndGraspInLane()
    {
        var support = Rules.Recommend(new RuneRequest(Leona, Archetype.Tank, Position.Support, GameMode.SummonersRift));
        var top = Rules.Recommend(new RuneRequest(Leona, Archetype.Tank, Position.Top, GameMode.SummonersRift));
        var aram = Rules.Recommend(new RuneRequest(Leona, Archetype.Tank, Position.None, GameMode.Aram));

        Assert.Equal("Aftershock", Name(support.Page.Keystone));
        Assert.Equal("Grasp of the Undying", Name(top.Page.Keystone));
        Assert.Equal("Aftershock", Name(aram.Page.Keystone));
    }

    [Fact]
    public void Rules_AdjustToTheEnemyPicks()
    {
        var vsTanks = Rules.Recommend(new RuneRequest(Jinx, Archetype.Marksman, Position.Bottom, GameMode.SummonersRift) { Enemies = [Ornn, Sejuani] });
        var vsPoke = Rules.Recommend(new RuneRequest(Garen, Archetype.Bruiser, Position.Top, GameMode.SummonersRift)
        {
            Enemies = [Ranged("Xerath"), Ranged("Ziggs"), Ranged("Lux")],
        });
        var noEnemies = Rules.Recommend(new RuneRequest(Jinx, Archetype.Marksman, Position.Bottom, GameMode.SummonersRift));

        Assert.Contains(vsTanks.Page.PrimaryRunes, id => Name(id) == "Cut Down");
        Assert.Equal(StatShards.Tenacity, vsTanks.Page.PerkIds[8]); // Ornn and Sejuani are heavy crowd control
        Assert.Contains(vsPoke.Page.SecondaryRunes, id => Name(id) == "Second Wind");
        Assert.Contains(vsPoke.Reasons, r => r.Contains("3 ranged champions"));
        Assert.Contains(noEnemies.Page.PrimaryRunes, id => Name(id) == "Coup de Grace");
        Assert.Single(noEnemies.Reasons);
    }

    [Fact]
    public void Rules_FallBackToTheFirstRuneOfARow_WhenRiotRemovesOne()
    {
        var withoutLethalTempo = new RuneCatalog(Runes.Styles
            .Select(s => s.Name != "Precision" ? s : s with { Slots = [s.Slots[0].Where(r => r.Name != "Lethal Tempo").ToList(), .. s.Slots.Skip(1)] })
            .ToList());

        var page = new RuleRuneSource(withoutLethalTempo).Recommend(new RuneRequest(Jinx, Archetype.Marksman, Position.Bottom, GameMode.SummonersRift)).Page;

        Assert.Equal("Press the Attack", Name(page.Keystone));
        Assert.Null(withoutLethalTempo.Validate(page));
    }

    [Theory]
    [InlineData("utility", Position.Support)]
    [InlineData("MIDDLE", Position.Middle)]
    [InlineData("bottom", Position.Bottom)]
    [InlineData("FILL", Position.None)]
    [InlineData("", Position.None)]
    [InlineData(null, Position.None)]
    public void Positions_ReadClientAndGameNames(string? value, Position expected)
    {
        Assert.Equal(expected, Positions.Parse(value));
    }

    [Fact]
    public void DefaultPlaystyle_FollowsTheChampionAndRole()
    {
        Assert.Equal(Archetype.Tank, Playstyles.Default(Leona, Position.Support));
        Assert.Equal(Archetype.Mage, Playstyles.Default(Lux, Position.Support));
        Assert.Equal(Archetype.Marksman, Playstyles.Default(Jinx, Position.None));
        Assert.Equal(Archetype.Bruiser, Playstyles.Default(Garen, Position.Top));
        Assert.Equal(Archetype.Tank, Playstyles.Default(Garen, Position.Support)); // a fighter who can tank goes tank in support
    }

    [Fact]
    public async Task Opgg_PicksTheMostPlayedPageThatFitsThePlaystyle()
    {
        var handler = new FakeHandler(_ => OpggJson(mainRole: "ADC"));
        var opgg = new OpggRuneSource(Runes, new HttpClient(handler));

        var marksman = await opgg.RecommendAsync(new RuneRequest(Jinx, Archetype.Marksman, Position.Bottom, GameMode.SummonersRift), default);
        var mage = await opgg.RecommendAsync(new RuneRequest(Jinx, Archetype.Mage, Position.Bottom, GameMode.SummonersRift), default);
        var tank = await opgg.RecommendAsync(new RuneRequest(Jinx, Archetype.Tank, Position.Bottom, GameMode.SummonersRift), default);

        Assert.Equal("Lethal Tempo", Name(marksman!.Page.Keystone));
        Assert.Equal(12000, marksman.Games);
        Assert.Equal(0.53, marksman.WinRate!.Value, precision: 2);
        Assert.Equal("Arcane Comet", Name(mage!.Page.Keystone)); // the rarely played AP page still counts
        Assert.Null(tank);                                        // its only Aftershock page has too few games
        Assert.Equal(["ranked/222/adc"], handler.Paths.Distinct());  // one fetch, cached for the other two
    }

    [Fact]
    public async Task Opgg_AsksForTheRightRoleAndMode()
    {
        var handler = new FakeHandler(path => OpggJson(mainRole: "ADC", games: path.Contains("support") ? 100 : 12000));
        var opgg = new OpggRuneSource(Runes, new HttpClient(handler));

        await opgg.RecommendAsync(new RuneRequest(Leona, Archetype.Tank, Position.Support, GameMode.SummonersRift), default);
        await opgg.RecommendAsync(new RuneRequest(Jinx, Archetype.Marksman, Position.None, GameMode.Aram), default);
        // No role and a guessed role with few games: ask again for the role op.gg says Jinx is played in.
        await opgg.RecommendAsync(new RuneRequest(Jinx, Archetype.Enchanter, Position.None, GameMode.SummonersRift), default);

        Assert.Equal(["ranked/89/support", "aram/222/none", "ranked/222/support", "ranked/222/adc"], handler.Paths);
    }

    [Fact]
    public async Task Advisor_FallsBackToTheRules_WhenOpggHasNothingOrFails()
    {
        var empty = new RuneAdvisor(Rules, new OpggRuneSource(Runes, new HttpClient(new FakeHandler(_ => OpggJson("ADC")))));
        var down = new RuneAdvisor(Rules, new OpggRuneSource(Runes, new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline")))));
        var request = new RuneRequest(Jinx, Archetype.Tank, Position.Bottom, GameMode.SummonersRift);

        var noPage = await empty.RecommendAsync(request, RuneSourceKind.StatsSite);
        var offline = await down.RecommendAsync(request, RuneSourceKind.StatsSite);
        var rulesOnly = await down.RecommendAsync(request, RuneSourceKind.OwnRules);

        Assert.Equal(RuleRuneSource.SourceName, noPage.Source);
        Assert.StartsWith("op.gg has no tank page for Jinx", noPage.Reasons[0]);
        Assert.StartsWith("op.gg didn't answer", offline.Reasons[0]);
        Assert.DoesNotContain(rulesOnly.Reasons, r => r.Contains("op.gg"));
    }

    [Fact]
    public void Lockfile_ReadsPortAndPassword()
    {
        Assert.Equal(new Lockfile(51234, "s3cr3t"), Lockfile.Parse("LeagueClient:1234:51234:s3cr3t:https"));
        Assert.Null(Lockfile.Parse("garbage"));
    }

    [Fact]
    public void ChampSelectState_ReadsPickRoleModeAndVisibleEnemies()
    {
        var champions = new ChampionCatalog([Jinx, Leona, Ornn, Zed]);
        var snapshot = new ClientSnapshot(
            new ChampSelectSession
            {
                LocalPlayerCellId = 1,
                MyTeam = [new() { CellId = 0, ChampionId = 222, AssignedPosition = "bottom" }, new() { CellId = 1, ChampionPickIntent = 89, AssignedPosition = "" }],
                TheirTeam = [new() { CellId = 5, ChampionId = 516 }, new() { CellId = 6, ChampionId = 0 }],
            },
            new GameflowSession { GameData = new GameflowGameData { Queue = new GameflowQueue { GameMode = "CLASSIC", MapId = 11 } } },
            new Lobby { LocalMember = new LobbyMember { FirstPositionPreference = "UTILITY" } });

        var state = ChampSelectState.From(snapshot, champions)!;

        Assert.Equal(Leona, state.Champion);           // hovered, not locked yet
        Assert.Equal(Position.Support, state.Position); // no assigned role, so the role you queued for
        Assert.Equal(GameMode.SummonersRift, state.Mode);
        Assert.Equal([Jinx], state.Allies);
        Assert.Equal([Ornn], state.Enemies);           // the second enemy hasn't picked
    }

    [Fact]
    public async Task Writer_OverwritesTheCurrentPage_WhenItsEditable()
    {
        var store = new FakeStore(current: new PerkPage { Id = 7, Name = "My page", IsEditable = true });

        var result = await new RunePageWriter(store).ApplyAsync(SomePage(), "LeagueClanker Jinx");

        Assert.True(result.Success);
        Assert.Equal([(7, "LeagueClanker Jinx")], store.Updated);
        Assert.Empty(store.Created);
    }

    [Fact]
    public async Task Writer_UsesItsOwnPageOrAFreeSlot_WhenTheCurrentPageIsAPreset()
    {
        var preset = new PerkPage { Id = 1, Name = "Preset", IsEditable = false };
        var ours = new PerkPage { Id = 9, Name = "LeagueClanker Lux", IsEditable = true };
        var mine = new PerkPage { Id = 8, Name = "Mine", IsEditable = true };

        var reuse = new FakeStore(preset, [preset, mine, ours], owned: 2);
        var create = new FakeStore(preset, [preset, mine], owned: 2);
        var full = new FakeStore(preset, [preset, mine], owned: 1);

        await new RunePageWriter(reuse).ApplyAsync(SomePage(), "LeagueClanker Jinx");
        await new RunePageWriter(create).ApplyAsync(SomePage(), "LeagueClanker Jinx");
        var refused = await new RunePageWriter(full).ApplyAsync(SomePage(), "LeagueClanker Jinx");

        Assert.Equal([(9, "LeagueClanker Jinx")], reuse.Updated);
        Assert.Equal([9], reuse.SelectedIds);
        Assert.Equal(["LeagueClanker Jinx"], create.Created);
        Assert.False(refused.Success);
        Assert.Empty(full.Updated); // never overwrite one of your own pages you didn't select
    }

    [Fact]
    public void PageName_FitsTheClientLimit()
    {
        Assert.Equal("LeagueClanker Jinx", RunePageWriter.PageName(Jinx));
        Assert.True(RunePageWriter.PageName(new ChampionInfo("X", "Aurelion Sol and Friends", [], 1, 1, 1)).Length <= 25);
    }

    [Fact]
    public void Analyze_UsesTheChosenPlaystyleForYourBuild()
    {
        var game = TestData.Game(allies: [("Garen", [])], enemies: [("Annie", [])]);

        var natural = GameAnalyzer.Analyze(game, TestData.Static)!;
        var ap = GameAnalyzer.Analyze(game, TestData.Static, playstyle: Archetype.ApBruiser)!;

        Assert.Equal(Archetype.Bruiser, natural.Me.Archetype);
        Assert.Equal(Archetype.ApBruiser, ap.Me.Archetype);
        Assert.True(ap.Me.MagicShare > natural.Me.MagicShare);
    }

    private static RunePage SomePage() => Rules.Recommend(new RuneRequest(Jinx, Archetype.Marksman, Position.Bottom, GameMode.SummonersRift)).Page;

    // Three pages: a popular Lethal Tempo page, a rare but valid Arcane Comet page, and an Aftershock page below the game minimum.
    private static string OpggJson(string mainRole, int games = 12000) => $$$"""
        {"data": {
          "summary": {"positions": [{"name": "{{{mainRole}}}"}]},
          "runes": [
            {"primary_page_id": 8000, "primary_rune_ids": [8008, 8009, 9103, 8017], "secondary_page_id": 8300, "secondary_rune_ids": [8313, 8321],
             "stat_mod_ids": [5005, 5008, 5011], "play": {{{games}}}, "win": {{{games * 53 / 100}}}},
            {"primary_page_id": 8200, "primary_rune_ids": [8229, 8226, 8210, 8237], "secondary_page_id": 8300, "secondary_rune_ids": [8304, 8345],
             "stat_mod_ids": [5008, 5008, 5001], "play": 60, "win": 30}
          ],
          "rune_pages": [{"builds": [
            {"primary_page_id": 8400, "primary_rune_ids": [8439, 8446, 8473, 8451], "secondary_page_id": 8000, "secondary_rune_ids": [9111, 9105],
             "stat_mod_ids": [5007, 5001, 5001], "play": 30, "win": 20}
          ]}]
        }}
        """;

    private sealed class FakeHandler(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/global/champions/", "");
            Paths.Add(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(path)) });
        }
    }

    private sealed class FakeStore(PerkPage? current, IReadOnlyList<PerkPage>? pages = null, int owned = 0) : IRunePageStore
    {
        public List<(int Id, string Name)> Updated { get; } = [];
        public List<string> Created { get; } = [];
        public List<int> SelectedIds { get; } = [];

        public Task<PerkPage?> GetCurrentPageAsync(CancellationToken ct) => Task.FromResult(current);
        public Task<IReadOnlyList<PerkPage>> GetPagesAsync(CancellationToken ct) => Task.FromResult(pages ?? []);
        public Task<int> GetOwnedPageCountAsync(CancellationToken ct) => Task.FromResult(owned);

        public Task UpdatePageAsync(int id, PerkPageRequest page, CancellationToken ct)
        {
            Updated.Add((id, page.Name));
            return Task.CompletedTask;
        }

        public Task<PerkPage> CreatePageAsync(PerkPageRequest page, CancellationToken ct)
        {
            Created.Add(page.Name);
            return Task.FromResult(new PerkPage { Id = 99, Name = page.Name, IsEditable = true });
        }

        public Task SetCurrentPageAsync(int id, CancellationToken ct)
        {
            SelectedIds.Add(id);
            return Task.CompletedTask;
        }
    }
}
