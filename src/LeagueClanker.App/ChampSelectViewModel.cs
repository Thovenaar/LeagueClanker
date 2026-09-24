using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LeagueClanker.Core;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.ItemSets;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.Spells;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.App;

public sealed record PlaystyleOption(Archetype Value, string Label, bool IsSelected);

public sealed record RuneRow(string Name, string IconUrl);

public sealed record CounterRow(string Name, string IconUrl, string WinRate, string Games, bool YouPlayIt);

/// <summary>Everything the champ select panel needs, handed over once the static data is loaded.</summary>
public sealed record ChampSelectServices(StaticGameData Data, RuneAdvisor Runes, MatchupAdvisor Matchups, SpellAdvisor Spells, OpggClient Opgg);

/// <summary>
/// Champ select: your lane matchup, how you'll play your champion, and the rune page, spells, skill order and item set
/// for it. Apply writes them into the client. The playstyle carries over into the game, where it decides the build.
/// </summary>
public sealed class ChampSelectViewModel : INotifyPropertyChanged
{
    // Your playstyle per champion this session, so hovering another champion and coming back keeps your choice.
    private readonly Dictionary<string, Archetype> _chosen = [];

    private ChampSelectServices? _services;
    private ChampSelectState? _state;
    private RuneRecommendation? _runes;
    private SpellRecommendation? _spells;
    private OpggChampion? _opgg;
    private int _requestVersion;
    private int _matchupVersion;
    private string? _autoAppliedFor;

    private bool _isActive;
    private string _championName = "";
    private string _championIconUrl = "";
    private string _situation = "";
    private IReadOnlyList<PlaystyleOption> _playstyles = [];
    private string _primaryName = "";
    private IReadOnlyList<RuneRow> _primaryRunes = [];
    private string _secondaryName = "";
    private IReadOnlyList<RuneRow> _secondaryRunes = [];
    private IReadOnlyList<RuneRow> _shards = [];
    private IReadOnlyList<string> _reasons = [];
    private string _sourceLine = "";
    private IReadOnlyList<RuneRow> _spellRows = [];
    private string _spellLine = "";
    private string _skillOrder = "";
    private string _skillLevels = "";
    private string _applyStatus = "";
    private bool _canApply;
    private bool _hasLane;
    private string _laneTitle = "";
    private string _opponentText = "";
    private string _opponentIconUrl = "";
    private string _matchupText = "";
    private string _counterHeader = "";
    private IReadOnlyList<CounterRow> _counterPicks = [];
    private string _enemyRoles = "";
    private string _laneNote = "";

    public ChampSelectViewModel(SettingsViewModel settings)
    {
        Settings = settings;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.RuneSource))
            {
                ApplyStatus = "";
                _ = RecomputeAsync();
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the playstyle for your champion changes, by default or by you.</summary>
    public event EventHandler<(string ChampionId, Archetype Playstyle)>? PlaystyleSelected;

    public SettingsViewModel Settings { get; }

    /// <summary>Writes into the League client. Set by the app; demos use <see cref="DemoWriter"/>.</summary>
    public IChampSelectWriter? Writer { get; set; }

    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }
    public bool HasChampion => _state?.Champion is not null;
    public string ChampionName { get => _championName; private set => Set(ref _championName, value); }
    public string ChampionIconUrl { get => _championIconUrl; private set => Set(ref _championIconUrl, value); }

    /// <summary>"Support · Summoner's Rift", "ARAM", ...</summary>
    public string Situation { get => _situation; private set => Set(ref _situation, value); }

    public IReadOnlyList<PlaystyleOption> Playstyles { get => _playstyles; private set => Set(ref _playstyles, value); }
    public string PrimaryName { get => _primaryName; private set => Set(ref _primaryName, value); }
    public IReadOnlyList<RuneRow> PrimaryRunes { get => _primaryRunes; private set => Set(ref _primaryRunes, value); }
    public string SecondaryName { get => _secondaryName; private set => Set(ref _secondaryName, value); }
    public IReadOnlyList<RuneRow> SecondaryRunes { get => _secondaryRunes; private set => Set(ref _secondaryRunes, value); }
    public IReadOnlyList<RuneRow> Shards { get => _shards; private set => Set(ref _shards, value); }
    public IReadOnlyList<string> Reasons { get => _reasons; private set => Set(ref _reasons, value); }
    public string SourceLine { get => _sourceLine; private set => Set(ref _sourceLine, value); }

    /// <summary>The two summoner spells, first key first.</summary>
    public IReadOnlyList<RuneRow> SpellRows { get => _spellRows; private set => Set(ref _spellRows, value); }
    public string SpellLine { get => _spellLine; private set => Set(ref _spellLine, value); }

    /// <summary>"Max Q > W > E". Empty without op.gg data.</summary>
    public string SkillOrder { get => _skillOrder; private set => Set(ref _skillOrder, value); }

    /// <summary>"Levels 1-6: Q W E Q Q R"</summary>
    public string SkillLevels { get => _skillLevels; private set => Set(ref _skillLevels, value); }

    public string ApplyStatus { get => _applyStatus; private set => Set(ref _applyStatus, value); }
    public bool CanApply { get => _canApply; private set => Set(ref _canApply, value); }

    /// <summary>"Apply runes, spells and item set", depending on the settings.</summary>
    public string ApplyLabel =>
        "Apply " + string.Join(", ", new[] { "runes", Settings.ApplySpells ? "spells" : null, Settings.ApplyItemSet ? "item set" : null }.OfType<string>());

    /// <summary>True on Summoner's Rift with a known role: then there's a lane opponent to show, or to wait for.</summary>
    public bool HasLane { get => _hasLane; private set => Set(ref _hasLane, value); }

    public string LaneTitle { get => _laneTitle; private set => Set(ref _laneTitle, value); }

    /// <summary>"vs Darius", "vs Caitlyn and Lux", or who you're waiting for.</summary>
    public string OpponentText { get => _opponentText; private set => Set(ref _opponentText, value); }

    public string OpponentIconUrl { get => _opponentIconUrl; private set => Set(ref _opponentIconUrl, value); }

    /// <summary>"Garen vs Darius: 50.4% win rate over 2,792 games · even". Empty until you have a champion.</summary>
    public string MatchupText { get => _matchupText; private set => Set(ref _matchupText, value); }

    public string CounterHeader { get => _counterHeader; private set => Set(ref _counterHeader, value); }
    public IReadOnlyList<CounterRow> CounterPicks { get => _counterPicks; private set => Set(ref _counterPicks, value); }

    /// <summary>"Enemy roles (guessed): Darius top · Amumu jungle · ..."</summary>
    public string EnemyRoles { get => _enemyRoles; private set => Set(ref _enemyRoles, value); }

    public string LaneNote { get => _laneNote; private set => Set(ref _laneNote, value); }

    public Archetype? SelectedPlaystyle => Playstyles.FirstOrDefault(p => p.IsSelected)?.Value;

    public void Configure(ChampSelectServices services) => _services = services;

    /// <summary>Called on every poll. Null means you're not in champ select.</summary>
    public void Update(ChampSelectState? state)
    {
        if (state is null)
        {
            _state = null;
            _autoAppliedFor = null;
            IsActive = false;
            return;
        }

        IsActive = true;
        var changed = _state?.Fingerprint != state.Fingerprint;
        var championChanged = _state?.Champion?.Id != state.Champion?.Id || _state?.Position != state.Position;
        _state = state;
        if (!changed)
            return;

        Raise(nameof(HasChampion));
        _ = RecomputeMatchupAsync();
        if (state.Champion is not { } champion || _services is null)
        {
            ChampionName = "Pick or hover a champion";
            ChampionIconUrl = "";
            Situation = SituationText(state);
            Playstyles = [];
            CanApply = false;
            return;
        }

        ChampionName = champion.Name;
        ChampionIconUrl = _services.Data.ChampionIconUrl(champion.Id);
        Situation = SituationText(state);
        if (championChanged)
        {
            ApplyStatus = "";
            ShowPlaystyle(_chosen.TryGetValue(champion.Id, out var chosen) ? chosen : Core.Runes.Playstyles.Default(champion, state.Position));
        }
        _ = RecomputeAsync();
    }

    public void SelectPlaystyle(Archetype playstyle)
    {
        if (_state?.Champion is not { } champion || playstyle == SelectedPlaystyle)
            return;
        _chosen[champion.Id] = playstyle;
        ShowPlaystyle(playstyle);
        ApplyStatus = "";
        _ = RecomputeAsync();
    }

    /// <summary>The settings for spells and the item set changed: the button says what it will write.</summary>
    public void RefreshApplyLabel() => Raise(nameof(ApplyLabel));

    /// <summary>Writes the rune page, and the spells and item set when the settings say so.</summary>
    public async Task ApplyAsync()
    {
        if (_runes is not { } runes || _state is not { Champion: { } champion } state || Writer is null || _services is null)
            return;

        CanApply = false;
        ApplyStatus = "Writing into the client...";
        var messages = new List<string>();
        async Task Try(string what, Func<Task<ApplyResult>> write)
        {
            try
            {
                messages.Add((await write()).Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
            {
                Log.Error($"Writing {what}", ex);
                messages.Add($"Couldn't write the {what}: {ex.Message}");
            }
        }

        await Try("rune page", () => Writer.WriteRunesAsync(runes.Page, RunePageWriter.PageName(champion), default));
        if (Settings.ApplySpells && _spells is { } spells)
            await Try("summoner spells", () => Writer.WriteSpellsAsync(spells.First, spells.Second, default));
        if (Settings.ApplyItemSet && BuildItemSet(state) is { } set)
            await Try("item set", () => Writer.WriteItemSetAsync(set, default));

        ApplyStatus = string.Join(" ", messages);
        CanApply = true;
    }

    /// <summary>A build for the game ahead: everyone at level 1, with your playstyle, against the enemies you can see.</summary>
    private ItemSetDefinition? BuildItemSet(ChampSelectState state)
    {
        if (_services is null || state.ToGameData() is not { } game || GameAnalyzer.Analyze(game, _services.Data, playstyle: SelectedPlaystyle) is not { } analysis)
            return null;
        var recommendation = new RecommendationEngine(_services.Data).Recommend(analysis);
        return ItemSetBuilder.Build(recommendation, _services.Data.Items, _opgg);
    }

    private void ShowPlaystyle(Archetype selected)
    {
        Playstyles = Core.Runes.Playstyles.All.Select(a => new PlaystyleOption(a, a.DisplayName(), a == selected)).ToList();
        if (_state?.Champion is { } champion)
            PlaystyleSelected?.Invoke(this, (champion.Id, selected));
    }

    private async Task RecomputeAsync()
    {
        if (_state is not { Champion: { } champion } state || _services is not { } services || SelectedPlaystyle is not { } playstyle)
            return;
        if (services.Data.Runes.IsEmpty)
        {
            Reasons = ["Couldn't load the rune data from Data Dragon. Restart the app when you're online."];
            return;
        }

        var version = ++_requestVersion;
        CanApply = false;
        var source = Settings.RuneSource;
        var runeRequest = new RuneRequest(champion, playstyle, state.Position, state.Mode) { Enemies = state.Enemies };
        var runes = await services.Runes.RecommendAsync(runeRequest, source);
        var spells = await services.Spells.RecommendAsync(new SpellRequest(champion, playstyle, state.Position, state.Mode, state.Spells), source);
        var opgg = source == RuneSourceKind.StatsSite ? await LoadOpggAsync(champion, playstyle, state, services.Opgg) : null;
        if (version != _requestVersion)
            return; // A newer request (another champion, playstyle or source) is on its way.

        _runes = runes;
        _spells = spells;
        _opgg = opgg;
        ShowPage(runes, services.Data.Runes);
        ShowSpells(spells, services.Data);
        ShowSkills(opgg?.SkillOrder);
        CanApply = Writer is not null;

        // Auto-apply once per champion, playstyle and source after you lock in.
        var applyKey = $"{champion.Id}|{playstyle}|{source}";
        if (Settings.AutoApply && state.IsLocked && Writer is not null && _autoAppliedFor != applyKey)
        {
            _autoAppliedFor = applyKey;
            await ApplyAsync();
        }
    }

    private static async Task<OpggChampion?> LoadOpggAsync(ChampionInfo champion, Archetype playstyle, ChampSelectState state, OpggClient opgg)
    {
        try
        {
            var aram = state.Mode is GameMode.Aram or GameMode.AramMayhem;
            return state.Mode is GameMode.SummonersRift or GameMode.Aram or GameMode.AramMayhem
                ? await opgg.GetChampionAsync(champion.Key, aram, OpggClient.RoleFor(state.Position, playstyle), default)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Log.Error("op.gg build data", ex);
            return null;
        }
    }

    private void ShowSpells(SpellRecommendation? spells, StaticGameData data)
    {
        SpellRows = spells is null
            ? []
            : new[] { spells.First, spells.Second }.Select(id => new RuneRow(data.Spells.Get(id)?.Name ?? id.ToString(), data.SpellIconUrl(id))).ToList();
        SpellLine = spells switch
        {
            null => "",
            { Games: { } games, WinRate: { } rate } => $"{spells.Reason} {rate:P1} win rate over {games:N0} games.",
            _ => spells.Reason,
        };
    }

    private void ShowSkills(OpggSkillOrder? skills)
    {
        SkillOrder = skills is { MaxOrder.Count: > 0 } ? $"Max {string.Join(" > ", skills.MaxOrder)}" : "";
        SkillLevels = skills is { Levels.Count: >= 6 } ? $"Levels 1-6: {string.Join(" ", skills.Levels.Take(6))}" : "";
    }

    private async Task RecomputeMatchupAsync()
    {
        if (_state is not { } state || _services is not { } services)
            return;

        var version = ++_matchupVersion;
        var request = new MatchupRequest(state.Champion, state.IsLocked, state.Position, state.Mode, state.Enemies)
        {
            Pickable = state.Pickable.Count > 0 ? state.Pickable : null,
            Mastery = state.Mastery,
        };
        var report = await services.Matchups.AnalyzeAsync(request);
        if (version != _matchupVersion)
            return;

        ShowMatchup(report, state, services.Data);
    }

    private void ShowMatchup(MatchupReport? report, ChampSelectState state, StaticGameData data)
    {
        HasLane = report is not null;
        if (report is null)
            return;

        LaneTitle = $"{report.Position.DisplayName().ToUpperInvariant()} LANE";
        OpponentIconUrl = report.Opponent is { } opponent ? data.ChampionIconUrl(opponent.Id) : "";
        OpponentText = report.Opponent is null
            ? $"Waiting for the enemy {report.Position.DisplayName().ToLowerInvariant()} to pick."
            : report.Partner is { } partner ? $"vs {report.Opponent.Name} and {partner.Name}" : $"vs {report.Opponent.Name}";
        if (report is { Opponent: not null, RolesAreGuessed: true })
            OpponentText += " (guessed)";

        MatchupText = report.Matchup is { } m && state.Champion is { } me
            ? $"{me.Name} vs {m.Opponent.Name}: {m.WinRate:P1} win rate over {m.Games:N0} games · {m.Verdict}"
            : "";
        CounterHeader = report.Opponent is null ? "" : $"GOOD PICKS VS {report.Opponent.Name.ToUpperInvariant()}";
        CounterPicks = report.CounterPicks
            .Select(c => new CounterRow(c.Champion.Name, data.ChampionIconUrl(c.Champion.Id), $"{c.WinRate:P1}", $"{c.Games:N0} games", c.YouPlayIt))
            .ToList();
        EnemyRoles = report.EnemyRoles.Count == 0
            ? ""
            : $"Enemy roles{(report.RolesAreGuessed ? " (guessed)" : "")}: "
              + string.Join(" · ", report.EnemyRoles.Select(r => $"{r.Champion.Name} {r.Position.DisplayName().ToLowerInvariant()}"));
        LaneNote = report.Note ?? "";
    }

    private void ShowPage(RuneRecommendation recommendation, RuneCatalog runes)
    {
        RuneRow Row(int id) => runes.Get(id) is { } rune ? new RuneRow(rune.Name, RuneCatalog.IconUrl(rune.Icon)) : new RuneRow(id.ToString(), "");

        var page = recommendation.Page;
        PrimaryName = runes.Style(page.PrimaryStyleId)?.Name.ToUpperInvariant() ?? "";
        PrimaryRunes = page.PrimaryRunes.Select(Row).ToList();
        SecondaryName = runes.Style(page.SubStyleId)?.Name.ToUpperInvariant() ?? "";
        SecondaryRunes = page.SecondaryRunes.Select(Row).ToList();
        Shards = page.Shards.Select(Row).ToList();
        Reasons = recommendation.Reasons;
        SourceLine = recommendation.Source == OpggRuneSource.SourceName
            ? $"From op.gg · {recommendation.WinRate:P1} win rate · {recommendation.Games:N0} games"
            : "From LeagueClanker's own rules";
    }

    private static string SituationText(ChampSelectState state) => state.Mode switch
    {
        GameMode.Aram or GameMode.AramMayhem => state.Mode.DisplayName(),
        _ when state.Position == Position.None => $"No role assigned · {state.Mode.DisplayName()}",
        _ => $"{state.Position.DisplayName()} · {state.Mode.DisplayName()}",
    };

    private void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
