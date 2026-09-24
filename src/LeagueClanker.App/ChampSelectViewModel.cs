using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using LeagueClanker.Core;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.App;

public sealed record PlaystyleOption(Archetype Value, string Label, bool IsSelected);

public sealed record RuneRow(string Name, string IconUrl);

public sealed record CounterRow(string Name, string IconUrl, string WinRate, string Games, bool YouPlayIt);

/// <summary>
/// Champ select: pick how you'll play your champion, see the rune page for it, and write that page into the client.
/// The playstyle carries over into the game, where it decides the item build.
/// </summary>
public sealed class ChampSelectViewModel : INotifyPropertyChanged
{
    // Your playstyle per champion this session, so hovering another champion and coming back keeps your choice.
    private readonly Dictionary<string, Archetype> _chosen = [];

    private StaticGameData? _data;
    private RuneAdvisor? _advisor;
    private MatchupAdvisor? _matchups;
    private ChampSelectState? _state;
    private RuneRecommendation? _recommendation;
    private int _requestVersion;
    private int _matchupVersion;

    private bool _isActive;
    private string _championName = "";
    private string _championIconUrl = "";
    private string _situation = "";
    private IReadOnlyList<PlaystyleOption> _playstyles = [];
    private RuneSourceKind _source;
    private string _primaryName = "";
    private IReadOnlyList<RuneRow> _primaryRunes = [];
    private string _secondaryName = "";
    private IReadOnlyList<RuneRow> _secondaryRunes = [];
    private IReadOnlyList<RuneRow> _shards = [];
    private IReadOnlyList<string> _reasons = [];
    private string _sourceLine = "";
    private string _applyStatus = "";
    private bool _canApply;
    private bool _isBusy;
    private bool _hasLane;
    private string _laneTitle = "";
    private string _opponentText = "";
    private string _opponentIconUrl = "";
    private string _matchupText = "";
    private string _counterHeader = "";
    private IReadOnlyList<CounterRow> _counterPicks = [];
    private string _enemyRoles = "";
    private string _laneNote = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the playstyle for your champion changes, by default or by you.</summary>
    public event EventHandler<(string ChampionId, Archetype Playstyle)>? PlaystyleSelected;

    /// <summary>Raised when you switch between op.gg and the rules, so the choice can be saved.</summary>
    public event EventHandler<RuneSourceKind>? SourceChanged;

    /// <summary>Writes a page into the League client. Set by the app; the demo replaces it.</summary>
    public Func<RunePage, string, Task<ApplyResult>>? ApplyHandler { get; set; }

    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }
    public bool HasChampion => _state?.Champion is not null;
    public string ChampionName { get => _championName; private set => Set(ref _championName, value); }
    public string ChampionIconUrl { get => _championIconUrl; private set => Set(ref _championIconUrl, value); }

    /// <summary>"Support · Summoner's Rift", "ARAM", ...</summary>
    public string Situation { get => _situation; private set => Set(ref _situation, value); }

    public IReadOnlyList<PlaystyleOption> Playstyles { get => _playstyles; private set => Set(ref _playstyles, value); }
    public bool UseStatsSite => _source == RuneSourceKind.StatsSite;
    public bool UseOwnRules => _source == RuneSourceKind.OwnRules;
    public string PrimaryName { get => _primaryName; private set => Set(ref _primaryName, value); }
    public IReadOnlyList<RuneRow> PrimaryRunes { get => _primaryRunes; private set => Set(ref _primaryRunes, value); }
    public string SecondaryName { get => _secondaryName; private set => Set(ref _secondaryName, value); }
    public IReadOnlyList<RuneRow> SecondaryRunes { get => _secondaryRunes; private set => Set(ref _secondaryRunes, value); }
    public IReadOnlyList<RuneRow> Shards { get => _shards; private set => Set(ref _shards, value); }
    public IReadOnlyList<string> Reasons { get => _reasons; private set => Set(ref _reasons, value); }
    public string SourceLine { get => _sourceLine; private set => Set(ref _sourceLine, value); }
    public string ApplyStatus { get => _applyStatus; private set => Set(ref _applyStatus, value); }
    public bool CanApply { get => _canApply; private set => Set(ref _canApply, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

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

    public void Configure(StaticGameData data, RuneAdvisor advisor, MatchupAdvisor matchups, RuneSourceKind source)
    {
        _data = data;
        _advisor = advisor;
        _matchups = matchups;
        _source = source;
        Raise(nameof(UseStatsSite), nameof(UseOwnRules));
    }

    /// <summary>Called on every poll. Null means you're not in champ select.</summary>
    public void Update(ChampSelectState? state)
    {
        if (state is null)
        {
            _state = null;
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
        if (state.Champion is not { } champion || _data is null)
        {
            ChampionName = "Pick or hover a champion";
            ChampionIconUrl = "";
            Situation = SituationText(state);
            Playstyles = [];
            CanApply = false;
            return;
        }

        ChampionName = champion.Name;
        ChampionIconUrl = _data.ChampionIconUrl(champion.Id);
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

    public void SelectSource(RuneSourceKind source)
    {
        if (source == _source)
            return;
        _source = source;
        Raise(nameof(UseStatsSite), nameof(UseOwnRules));
        SourceChanged?.Invoke(this, source);
        ApplyStatus = "";
        _ = RecomputeAsync();
    }

    public async Task ApplyAsync()
    {
        if (_recommendation is not { } recommendation || _state?.Champion is not { } champion || ApplyHandler is null)
            return;

        CanApply = false;
        ApplyStatus = "Writing your rune page...";
        try
        {
            var result = await ApplyHandler(recommendation.Page, RunePageWriter.PageName(champion));
            ApplyStatus = result.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            ApplyStatus = $"Couldn't write the page: {ex.Message}";
        }
        finally
        {
            CanApply = true;
        }
    }

    private void ShowPlaystyle(Archetype selected)
    {
        Playstyles = Core.Runes.Playstyles.All.Select(a => new PlaystyleOption(a, a.DisplayName(), a == selected)).ToList();
        if (_state?.Champion is { } champion)
            PlaystyleSelected?.Invoke(this, (champion.Id, selected));
    }

    private async Task RecomputeAsync()
    {
        if (_state is not { Champion: { } champion } state || _advisor is null || _data is null || SelectedPlaystyle is not { } playstyle)
            return;
        if (_data.Runes.IsEmpty)
        {
            Reasons = ["Couldn't load the rune data from Data Dragon. Restart the app when you're online."];
            return;
        }

        var version = ++_requestVersion;
        IsBusy = true;
        CanApply = false;
        var request = new RuneRequest(champion, playstyle, state.Position, state.Mode) { Enemies = state.Enemies };
        var recommendation = await _advisor.RecommendAsync(request, _source);
        if (version != _requestVersion)
            return; // A newer request (another champion, playstyle or source) is on its way.

        IsBusy = false;
        _recommendation = recommendation;
        ShowPage(recommendation, _data.Runes);
        CanApply = ApplyHandler is not null;
    }

    private async Task RecomputeMatchupAsync()
    {
        if (_state is not { } state || _matchups is null || _data is null)
            return;

        var version = ++_matchupVersion;
        var request = new MatchupRequest(state.Champion, state.IsLocked, state.Position, state.Mode, state.Enemies)
        {
            Pickable = state.Pickable.Count > 0 ? state.Pickable : null,
            Mastery = state.Mastery,
        };
        var report = await _matchups.AnalyzeAsync(request);
        if (version != _matchupVersion)
            return;

        ShowMatchup(report, state, _data);
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
              + string.Join(" \u00b7 ", report.EnemyRoles.Select(r => $"{r.Champion.Name} {r.Position.DisplayName().ToLowerInvariant()}"));
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
