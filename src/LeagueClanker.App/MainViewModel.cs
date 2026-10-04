using System.ComponentModel;
using System.Media;
using System.Runtime.CompilerServices;
using System.Windows;
using LeagueClanker.Core;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.History;
using LeagueClanker.Core.ItemSets;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.App;

public sealed record ItemRow(int Rank, string Name, int Gold, string IconUrl, IReadOnlyList<string> Reasons)
{
    public string ReasonText => string.Join(" · ", Reasons);
}

/// <summary>One of your six item slots. <see cref="Kind"/> is Owned, Next, Planned, Switch (a suggested swap on this slot) or Empty.</summary>
/// <param name="Number">Its place in the buy order, or 0 for an item you own.</param>
/// <param name="Tag">The strongest reason for it: "Marksmage +1.8".</param>
public sealed record SlotRow(int Number, string Kind, string Name, string IconUrl, string Tag, string Price)
{
    public string? OldName { get; init; }
    public string? OldIconUrl { get; init; }

    /// <summary>How much of the next item you've bought, as the two parts of its progress bar.</summary>
    public GridLength ProgressDone { get; init; } = new(0, GridUnitType.Star);
    public GridLength ProgressLeft { get; init; } = new(1, GridUnitType.Star);
}

/// <summary>A card you picked and what it does to your build.</summary>
public sealed record CardNote(string Name, string Text);

/// <summary>An op.gg build's win rate, as a bar in "Why this build".</summary>
public sealed record MetaBar(string Name, string Text, GridLength Filled, GridLength Empty, bool Chosen);

/// <param name="Direction">"Tankier", "More AP", "Vs tanks": which way this item takes your build.</param>
public sealed record AlternativeRow(string Direction, string Name, int Gold, string IconUrl);

public sealed record ChampionStatRow(string Name, string IconUrl, string Record, string WinRate);

public sealed record PlayerRow(
    string Name, string IconUrl, int Level, bool IsMe, string Note,
    double AttackDamage, double AbilityPower, double Armor, double MagicResist, double Health, double AttackSpeed, double CritChance);

public enum Tab
{
    Build,
    Augments,
    Players,
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly BuildPlanner _planner = new();
    private StaticGameData? _data;
    private string? _lastPivotSummary;

    private string _status = "Starting...";
    private bool _isLive;
    private Tab _tab = Tab.Build;
    private bool _hasAugments;
    private string _championLine = "";
    private string _damageSummary = "";
    private GridLength _adShare = new(1, GridUnitType.Star);
    private GridLength _apShare = new(1, GridUnitType.Star);
    private IReadOnlyList<ItemRow> _items = [];
    private IReadOnlyList<SlotRow> _slots = [];
    private IReadOnlyList<AlternativeRow> _alternatives = [];
    private string _buildHeader = "YOUR BUILD · MOST IMPORTANT FIRST";
    private IReadOnlyList<string> _advice = [];
    private string _footer = "";
    private bool _hasPivot;
    private string _pivotSummary = "";
    private IReadOnlyList<string> _pivotReasons = [];
    private bool _showSwitchHint;
    private string _switchHint = "";
    private IReadOnlyList<PlayerRow> _enemies = [];
    private IReadOnlyList<PlayerRow> _team = [];
    private string _playstyleLabel = "";
    private IReadOnlyList<PlaystyleOption> _livePlaystyles = [];
    private string _matchupLine = "";
    private string? _matchupKey;

    // The playstyle you chose (or champ select defaulted to), and for which champion. The build advisor uses it.
    private string? _playstyleChampion;
    private Archetype? _playstyle;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the playstyle the build advisor should use changes. Null means the champion's usual one.</summary>
    public event EventHandler<Archetype?>? PlaystyleChanged;

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }
    public string ChampionLine { get => _championLine; private set => Set(ref _championLine, value); }
    public string DamageSummary { get => _damageSummary; private set => Set(ref _damageSummary, value); }

    private string _damageSplit = "";

    /// <summary>"64% AD · 36% AP"</summary>
    public string DamageSplit { get => _damageSplit; private set => Set(ref _damageSplit, value); }
    public GridLength AdShare { get => _adShare; private set => Set(ref _adShare, value); }
    public GridLength ApShare { get => _apShare; private set => Set(ref _apShare, value); }
    public IReadOnlyList<ItemRow> Items { get => _items; private set => Set(ref _items, value); }

    /// <summary>Your finished items and boots, in inventory order.</summary>
    /// <summary>Your six item slots: what you own, then the plan in buy order with boots in it.</summary>
    public IReadOnlyList<SlotRow> Slots { get => _slots; private set => Set(ref _slots, value); }

    private string _slotsHeader = "";
    private string _slotsSummary = "";
    private string _championIconUrl = "";
    private string _cardsText = "";
    private IReadOnlyList<CardNote> _cardNotes = [];
    private string _pivotHeader = "";
    private string _pivotTitle = "";
    private string _pivotAccept = "";
    private string _pivotDecline = "";
    private bool _whyOpen;
    private string _whyHint = "";
    private IReadOnlyList<MetaBar> _metaBars = [];
    private bool _showSwaps;
    private bool _showSellAdvice;

    public string SlotsHeader { get => _slotsHeader; private set => Set(ref _slotsHeader, value); }
    public string SlotsSummary { get => _slotsSummary; private set => Set(ref _slotsSummary, value); }
    public string ChampionIconUrl { get => _championIconUrl; private set => Set(ref _championIconUrl, value); }

    /// <summary>" · card: Marksmage" after your playstyle.</summary>
    public string CardsText { get => _cardsText; private set => Set(ref _cardsText, value); }
    public IReadOnlyList<CardNote> CardNotes { get => _cardNotes; private set => Set(ref _cardNotes, value); }

    /// <summary>"SLOT 4 · SWITCH SUGGESTED · +1.2"</summary>
    public string PivotHeader { get => _pivotHeader; private set => Set(ref _pivotHeader, value); }
    public string PivotTitle { get => _pivotTitle; private set => Set(ref _pivotTitle, value); }
    public string PivotAccept { get => _pivotAccept; private set => Set(ref _pivotAccept, value); }
    public string PivotDecline { get => _pivotDecline; private set => Set(ref _pivotDecline, value); }

    /// <summary>"Why this build" is folded open. Closed by default: the slots already carry the main reasons.</summary>
    public bool WhyOpen { get => _whyOpen; set => Set(ref _whyOpen, value); }
    public string WhyHint { get => _whyHint; private set => Set(ref _whyHint, value); }
    public IReadOnlyList<MetaBar> MetaBars { get => _metaBars; private set => Set(ref _metaBars, value); }

    /// <summary>With a full build: the items worth selling, and what your gold buys after a sale.</summary>
    public bool ShowSwaps { get => _showSwaps; private set => Set(ref _showSwaps, value); }
    public bool ShowSellAdvice { get => _showSellAdvice; private set => Set(ref _showSellAdvice, value); }

    public string BuildHeader { get => _buildHeader; private set => Set(ref _buildHeader, value); }

    /// <summary>Other ways to go than your plan: the best item per direction it doesn't cover.</summary>
    public IReadOnlyList<AlternativeRow> Alternatives { get => _alternatives; private set => Set(ref _alternatives, value); }
    public IReadOnlyList<string> Advice { get => _advice; private set => Set(ref _advice, value); }
    public string Footer { get => _footer; set => Set(ref _footer, value); }

    public bool HasPivot { get => _hasPivot; private set => Set(ref _hasPivot, value); }
    public string PivotSummary { get => _pivotSummary; private set => Set(ref _pivotSummary, value); }
    public IReadOnlyList<string> PivotReasons { get => _pivotReasons; private set => Set(ref _pivotReasons, value); }
    public bool ShowSwitchHint { get => _showSwitchHint; private set => Set(ref _showSwitchHint, value); }
    public string SwitchHint { get => _switchHint; private set => Set(ref _switchHint, value); }

    public MainViewModel(SettingsViewModel settings)
    {
        Settings = settings;
        ChampSelect = new ChampSelectViewModel(settings);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.ApplySpells) or nameof(SettingsViewModel.ApplyItemSet))
                ChampSelect.RefreshApplyLabel();
            if (e.PropertyName == nameof(SettingsViewModel.Compact))
                Raise(nameof(ShowFullLive), nameof(ShowCompactLive));
        };

        // Once you've taken a card from the offer, the build matters again. Typing cards you already have doesn't count.
        Augments.OfferPicked += (_, _) => SelectTab(Tab.Build);

        // A card offer on screen needs your attention now, so bring its tab forward.
        Augments.OfferDetected += (_, _) =>
        {
            SelectTab(Tab.Augments);
            Chime();
        };

        ChampSelect.PlaystyleSelected += (_, choice) => UsePlaystyle(choice.ChampionId, choice.Playstyle);
        ChampSelect.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChampSelectViewModel.IsActive))
                return;
            Raise(nameof(ShowChampSelect), nameof(ShowHistory));
            if (!IsLive)
                Status = ChampSelectStatus;
        };
    }

    private const string WaitingStatus = "Waiting for a game... (Practice Tool works too)";

    private string ChampSelectStatus => !ChampSelect.IsActive ? WaitingStatus : ChampSelect.IsSwiftplay ? "Swiftplay lobby" : "Champ select";

    public SettingsViewModel Settings { get; }

    /// <summary>Playstyle and rune page for champ select. Shown while you're in champ select and no game runs.</summary>
    public ChampSelectViewModel ChampSelect { get; }

    private bool _showSettings;
    private string _notice = "";
    private string _updateText = "";

    public bool ShowSettings { get => _showSettings; private set => Set(ref _showSettings, value); }

    /// <summary>A short message under the content, e.g. where a snapshot went.</summary>
    public string Notice { get => _notice; private set => Set(ref _notice, value); }

    /// <summary>"v0.5.0 is available". Empty when you're up to date.</summary>
    public string UpdateText { get => _updateText; private set => Set(ref _updateText, value); }

    public string? UpdateUrl { get; private set; }

    /// <summary>Hands over the raw JSON of what's on screen: the live game, or champ select. Set by the app.</summary>
    public Func<(string Json, string Kind)?>? SnapshotProvider { get; set; }

    public void ToggleSettings() => ShowSettings = !ShowSettings;

    public void ShowUpdate(string version, string url)
    {
        UpdateUrl = url;
        UpdateText = $"v{version} is available";
    }

    /// <summary>Saves the live game or champ select, with player names replaced, and shows the file.</summary>
    public void SaveSnapshot()
    {
        if (SnapshotProvider?.Invoke() is not { } snapshot)
        {
            Notice = "Nothing to save yet: start a game or enter champ select first.";
            return;
        }

        try
        {
            var path = SnapshotStore.Save(snapshot.Json, snapshot.Kind);
            Notice = $"Saved {System.IO.Path.GetFileName(path)} (player names replaced).";
            Shell.ShowInExplorer(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Error("Saving a snapshot", ex);
            Notice = $"Couldn't save the snapshot: {ex.Message}";
        }
    }

    private void Chime()
    {
        if (Settings.PlaySounds)
            SystemSounds.Asterisk.Play();
    }

    public bool ShowChampSelect => ChampSelect.IsActive && !IsLive;

    /// <summary>"Marksman ▾": opens the playstyle menu in game.</summary>
    public string PlaystyleLabel { get => _playstyleLabel; private set => Set(ref _playstyleLabel, value); }

    private string _buildSource = "";

    /// <summary>Where the build comes from: "Build: op.gg's on-hit build (56.8% win rate over 444 games), over the AP burst build (43.7% over 522 games)."</summary>
    public string BuildSource { get => _buildSource; private set => Set(ref _buildSource, value); }

    public IReadOnlyList<PlaystyleOption> LivePlaystyles { get => _livePlaystyles; private set => Set(ref _livePlaystyles, value); }

    /// <summary>Looks up your lane matchup in game. Set by the app.</summary>
    public MatchupAdvisor? Matchups { get; set; }

    /// <summary>"vs Caitlyn: 53.1% win rate over 2,484 games · favored". Empty without a lane.</summary>
    public string MatchupLine { get => _matchupLine; private set => Set(ref _matchupLine, value); }

    /// <summary>Your own notes, shown for your lane opponent. Set by the app.</summary>
    public MatchupNotes? Notes { get; set; }

    private string _matchupNote = "";

    /// <summary>Your note on the lane opponent, from champ select or an earlier game.</summary>
    public string MatchupNote { get => _matchupNote; private set => Set(ref _matchupNote, value); }

    private double _gold;
    private OpggChampion? _opggChampion;
    private string? _liveChampionKey;
    private string _buyText = "";
    private IReadOnlyList<string> _tips = [];
    private string _startText = "";

    /// <summary>Raised when a game starts or your champion or role changes, so the app can fetch op.gg data for it.</summary>
    public event EventHandler<GameAnalysis>? LiveChampionChanged;

    private readonly GameRecorder _recorder = new();
    private string _lastGameTitle = "";
    private IReadOnlyList<string> _lastGameLines = [];
    private IReadOnlyList<ChampionStatRow> _championStats = [];
    private string _statsNote = "";

    /// <summary>Where recaps are kept. Set by the app; without it, games aren't recorded.</summary>
    public RecapStore? Recaps { get; private set; }

    /// <summary>Starts recording games, and shows the last one until the next game or champ select.</summary>
    public void UseRecaps(RecapStore store, StaticGameData data)
    {
        _data ??= data;
        Recaps = store;
        if (store.Games.Count > 0)
            ShowRecap(store.Games[0]);
    }

    /// <summary>A game ended and its recap was saved, so your stats can be refreshed.</summary>
    public event EventHandler<GameRecap>? GamePlayed;

    /// <summary>"Victory · Garen (bruiser) · 31:24"</summary>
    public string LastGameTitle { get => _lastGameTitle; private set => Set(ref _lastGameTitle, value); }

    public IReadOnlyList<string> LastGameLines { get => _lastGameLines; private set => Set(ref _lastGameLines, value); }
    public IReadOnlyList<ChampionStatRow> ChampionStats { get => _championStats; private set => Set(ref _championStats, value); }
    public string StatsNote { get => _statsNote; private set => Set(ref _statsNote, value); }

    /// <summary>Between games: the last game's recap and your champions.</summary>
    public bool ShowHistory => !IsLive && !ShowChampSelect && (LastGameTitle.Length > 0 || ChampionStats.Count > 0);

    /// <summary>Shows your most played champions, from recaps and match history.</summary>
    public void SetStats(PersonalStats stats)
    {
        if (_data is not { } data)
            return;
        var champions = stats.Champions().Take(6)
            .Select(c => (Champion: data.Champions.GetByKey(c.ChampionKey), c.Record))
            .Where(c => c.Champion is not null)
            .Select(c => new ChampionStatRow(c.Champion!.Name, data.ChampionIconUrl(c.Champion.Id), c.Record.ToString(), $"{c.Record.WinRate:P0}"))
            .ToList();
        ChampionStats = champions;
        StatsNote = stats.Games.Count == 0 ? "" : $"From your last {stats.Games.Count} Summoner's Rift games, League Classic included.";
        ChampSelect.SetStats(stats);
        Raise(nameof(ShowHistory));
    }

    /// <summary>The League client told the result the game didn't: show it.</summary>
    public void UpdateRecap(GameRecap recap)
    {
        ShowRecap(recap);
        GamePlayed?.Invoke(this, recap);
    }

    private void ShowRecap(GameRecap recap)
    {
        if (_data is not { } data)
            return;
        string Name(int id) => data.Items.Get(id)?.Name ?? id.ToString();
        var result = recap.Win switch { true => "Victory", false => "Defeat", null => "Game over" };
        LastGameTitle = $"{result} \u00b7 {recap.ChampionName} ({recap.Playstyle.InText()}) \u00b7 {TimeSpan.FromSeconds(recap.DurationSeconds):mm\\:ss}";

        var lines = new List<string>();
        if (recap.LaneOpponent is { } opponent && data.Champions.Get(opponent) is { } lane)
            lines.Add($"{recap.Position.DisplayName()} vs {lane.Name}.");
        if (recap.FinalItems.Count > 0)
            lines.Add($"You built {string.Join(", ", recap.FinalItems.Select(Name))}. {recap.AdvisedAndBuilt} of {recap.FinalItems.Count} were in LeagueClanker's build.");
        if (recap.Augments.Count > 0)
            lines.Add($"Cards: {string.Join(", ", recap.Augments)}.");
        if (recap.Pivots.Count > 0)
        {
            var taken = recap.Pivots.Count(p => p.Accepted);
            lines.Add($"Pivots: you took {taken} of {recap.Pivots.Count}. "
                      + string.Join(" ", recap.Pivots.Select(p => $"{(p.Accepted ? "Took" : "Kept your build over")} \"{p.Summary}\" at {TimeSpan.FromSeconds(p.GameTime):mm\\:ss}.")));
        }
        LastGameLines = lines;
        Raise(nameof(ShowHistory));
    }

    public string BuyText { get => _buyText; private set => Set(ref _buyText, value); }

    /// <summary>Full-build tips: swaps, elixirs, control wards.</summary>
    public IReadOnlyList<string> Tips { get => _tips; private set => Set(ref _tips, value); }

    /// <summary>"Start with Doran's Blade and 2 Health Potions." Only in the first two minutes.</summary>
    public string StartText { get => _startText; private set => Set(ref _startText, value); }

    public ItemRow? NextItem => Items.FirstOrDefault();

    public bool ShowFullLive => IsLive && !Settings.Compact;
    public bool ShowCompactLive => IsLive && Settings.Compact;

    public void ToggleCompact() => Settings.Compact = !Settings.Compact;

    /// <summary>op.gg's data for your champion in this game (starting items). Set by the app, null without it.</summary>
    public void SetOpggChampion(OpggChampion? champion)
    {
        _opggChampion = champion;
        RenderBuy();
    }

    /// <summary>You picked another playstyle in game. The build follows on the next poll.</summary>
    public void ChangePlaystyle(Archetype playstyle)
    {
        if (_planner.Latest?.Game.Me.Champion.Id is { } championId)
            UsePlaystyle(championId, playstyle);
    }

    private void UsePlaystyle(string championId, Archetype? playstyle)
    {
        if (_playstyleChampion == championId && _playstyle == playstyle)
            return;
        _playstyleChampion = playstyle is null ? null : championId;
        _playstyle = playstyle;
        PlaystyleChanged?.Invoke(this, playstyle);
    }

    /// <summary>Card picker for ARAM: Mayhem. Its tab only shows in modes with augments.</summary>
    public AugmentPickerViewModel Augments { get; } = new();

    public bool HasAugments { get => _hasAugments; private set => Set(ref _hasAugments, value); }

    // Radio buttons bind to these; a radio being unchecked sets false, which is ignored.
    public bool IsBuildTab { get => _tab == Tab.Build; set { if (value) SelectTab(Tab.Build); } }
    public bool IsAugmentsTab { get => _tab == Tab.Augments; set { if (value) SelectTab(Tab.Augments); } }
    public bool IsPlayersTab { get => _tab == Tab.Players; set { if (value) SelectTab(Tab.Players); } }
    public IReadOnlyList<PlayerRow> Enemies { get => _enemies; private set => Set(ref _enemies, value); }
    public IReadOnlyList<PlayerRow> Team { get => _team; private set => Set(ref _team, value); }

    public void Apply(AdvisorUpdate update, StaticGameData data)
    {
        _data = data;
        if (update is not { State: AdvisorState.Live, Recommendation: { } rec })
        {
            _planner.Reset();
            Augments.Reset();
            _lastPivotSummary = null;
            _liveChampionKey = null;
            _opggChampion = null;
            if (IsLive && _recorder.Finish(DateTime.Now) is { } recap)
            {
                Recaps?.Add(recap);
                ShowRecap(recap);
                GamePlayed?.Invoke(this, recap);
            }
            IsLive = false;
            Raise(nameof(ShowChampSelect), nameof(ShowFullLive), nameof(ShowCompactLive), nameof(ShowHistory));
            Status = ChampSelectStatus;
            return;
        }

        _gold = update.Gold;
        if (Recaps is not null)
            _recorder.Observe(rec);
        if (ReferenceEquals(rec, _planner.Latest))
        {
            RenderBuy(); // only the gold changed
            return;
        }

        // A playstyle chosen for another champion (a previous game) doesn't apply to this one.
        if (_playstyleChampion is not null && _playstyleChampion != rec.Game.Me.Champion.Id)
            UsePlaystyle(rec.Game.Me.Champion.Id, null);

        // A new playstyle is your decision, not the game's: take its build right away instead of suggesting a pivot.
        // The same goes for op.gg's popular items arriving just after the game starts, or being switched on or off.
        if (_planner.Latest?.Game is { } previous && previous.Me.Champion.Id == rec.Game.Me.Champion.Id
            && (previous.ChosenPlaystyle != rec.Game.ChosenPlaystyle
                || (previous.ChosenPlaystyle is null && previous.MetaBuilds.Count == 0 && previous.Me.Archetype != rec.Game.Me.Archetype)
                || !previous.PopularItems.SetEquals(rec.Game.PopularItems)
                || previous.MetaBuilds.Count != rec.Game.MetaBuilds.Count))
            _planner.Reset();

        _planner.Items = data.Items;
        _planner.Update(rec);
        Render();
    }

    public void AcceptPivot()
    {
        if (_planner.PendingPivot is { } pivot)
            _recorder.Pivot(pivot.Summary, accepted: true);
        _planner.Accept();
        Render();
    }

    public void DeclinePivot()
    {
        if (_planner.PendingPivot is { } pivot)
            _recorder.Pivot(pivot.Summary, accepted: false);
        _planner.Decline();
        Render();
    }

    public void SwitchToLatest()
    {
        _planner.SwitchToLatest();
        Render();
    }

    private void Render()
    {
        if (_planner.Latest is not { } rec || _data is not { } data)
            return;

        var me = rec.Game.Me;
        IsLive = true;
        Raise(nameof(ShowChampSelect), nameof(ShowFullLive), nameof(ShowCompactLive), nameof(ShowHistory));
        Status = $"Live · {TimeSpan.FromSeconds(rec.Game.GameTimeSeconds):mm\\:ss} · {rec.Game.Mode.DisplayName()}";
        ChampionLine = me.Name;
        // When the app picked the style from op.gg's builds, name the build: "Crit build" says more than "Marksman" for a Garen.
        PlaystyleLabel = rec.Meta is { } picked && rec.Game.ChosenPlaystyle is null
            ? $"{char.ToUpperInvariant(picked.Build.Name[0])}{picked.Build.Name[1..]} ▾"
            : $"{me.Archetype.DisplayName()} ▾";
        BuildSource = rec.Meta is { } meta ? $"Build: {meta.Text}{(meta.Swap is null ? "" : $" For this game: {meta.Swap}.")}" : "";
        LivePlaystyles = Playstyles.All.Select(a => new PlaystyleOption(a, a.DisplayName(), a == me.Archetype)).ToList();
        _ = ShowMatchupAsync(rec.Game);

        var championKey = $"{me.Champion.Id}|{me.Position}|{rec.Game.Mode}";
        if (championKey != _liveChampionKey)
        {
            _liveChampionKey = championKey;
            _opggChampion = null;
            LiveChampionChanged?.Invoke(this, rec.Game);
        }
        DamageSummary = rec.DamageSummary;
        DamageSplit = $"{BuildRules.Percent(rec.Game.Enemies.PhysicalShare)} AD · {BuildRules.Percent(rec.Game.Enemies.MagicShare)} AP";
        AdShare = new GridLength(rec.Game.Enemies.PhysicalShare, GridUnitType.Star);
        ApShare = new GridLength(rec.Game.Enemies.MagicShare, GridUnitType.Star);
        // Six slots: what you own fills some, and the plan in buy order, boots included, fills the rest.
        var finished = me.Items.Where(i => i.Kind is ItemKind.Legendary or ItemKind.Boots).ToList();
        var slotsLeft = rec.SlotsLeft;
        var plan = _planner.Upcoming.ToList();
        // An item a switch brings in shows on the slot it changes, not a second time further down.
        if (_planner.PendingPivot is { Drop.Count: > 0 } switching)
        {
            var adds = switching.Add.Select(a => a.Id).ToHashSet();
            var at = plan.FindIndex(u => switching.Drop.Any(d => d.Id == u.Item.Id));
            if (at >= 0)
                plan = plan.Where((u, i) => i <= at || !adds.Contains(u.Item.Id)).ToList();
        }
        var upcoming = plan.Take(slotsLeft).ToList();
        var planned = upcoming.Select(s => s.Item.Id).Concat(_planner.PendingPivot?.Add.Select(a => a.Id) ?? []).ToList();
        // A full build lists what's worth selling instead: the new item, with the one it replaces as its first tag.
        Items = rec.IsFull
            ? rec.Swaps.Select((s, i) => ToRow(s.Buy, i + 1, data) with { Reasons = [$"sell {s.Sell.Item.Name}", .. ToRow(s.Buy, 0, data).Reasons] }).ToList()
            : _planner.Upcoming.Take(slotsLeft).Select((item, i) => ToRow(item, i + 1, data)).ToList();
        Alternatives = rec.IsFull ? [] : ItemDirections.Alternatives(rec, planned)
            .Select(a => new AlternativeRow(a.Direction, a.Item.Item.Name, a.Item.Item.TotalGold, data.ItemIconUrl(a.Item.Item.Id)))
            .ToList();
        BuildHeader = rec.IsFull ? rec.Swaps.Count > 0 ? "YOUR BUILD IS FULL · WORTH SELLING ONE FOR" : "YOUR BUILD IS FULL · NOTHING BEATS IT"
            : finished.Count == 0 ? "YOUR BUILD · MOST IMPORTANT FIRST"
            : $"STILL TO BUY · {slotsLeft} SLOT{(slotsLeft == 1 ? "" : "S")} LEFT";
        Raise(nameof(NextItem));
        Advice = rec.Advice.Count > 0
            ? rec.Advice.Select(a => a.Text).ToList()
            : ["Nothing unusual about this game, so follow your standard build."];

        var pivot = _planner.PendingPivot;
        HasPivot = pivot is not null;
        PivotSummary = pivot?.Summary ?? "";
        PivotReasons = pivot?.Reasons.Select(r => $"Because {char.ToLowerInvariant(r[0])}{r[1..]}").ToList() ?? [];

        Slots = BuildSlots(rec, finished, upcoming, pivot, data);
        SlotsHeader = !rec.IsFull ? "YOUR SIX SLOTS · IN BUY ORDER" : rec.Swaps.Count > 0 ? "YOUR BUILD IS FULL · WORTH SELLING ONE" : "YOUR BUILD IS FULL";
        SlotsSummary = rec.IsFull ? "" : $"{finished.Count} filled · {slotsLeft} to go";
        ShowSwaps = rec.IsFull && rec.Swaps.Count > 0;
        ChampionIconUrl = data.ChampionIconUrl(me.Champion.Id);
        var cards = rec.Game.Augments;
        CardsText = cards.Count == 0 ? "" : $" · {(cards.Count == 1 ? "card" : "cards")}: {string.Join(", ", cards.Select(a => a.Name))}";
        CardNotes = cards.Select(a => new CardNote(a.Name, rec.Situations.FirstOrDefault(s => s.Label == a.Name)?.Description is { } d ? $"{d}." : "")).ToList();

        // The switch card names the slot it changes and says what it would do.
        var slotNumber = pivot is null ? -1 : pivot.Drop.Concat(pivot.Later).Select(d => upcoming.FindIndex(u => u.Item.Id == d.Id)).FirstOrDefault(i => i >= 0, -1);
        PivotHeader = pivot is null ? "" : $"{(slotNumber >= 0 ? $"SLOT {slotNumber + 1} · " : "")}SWITCH SUGGESTED · +{pivot.Gain:0.0}";
        PivotTitle = pivot?.Title ?? "";
        PivotAccept = pivot is { Add.Count: 1 } ? $"Switch to {pivot.Add[0].Name}" : "Switch build";
        PivotDecline = pivot is { Drop.Count: > 0 } ? $"Keep {pivot.Drop[0].Name}" : "Keep my build";
        PivotNotes = pivot?.Reasons.Select(r => r.EndsWith(" (new)", StringComparison.Ordinal) ? $"{r[..^6]}. New since you took this build." : $"{r}.").ToList() ?? [];

        MetaBars = rec.Meta is { } chosen
            ? new[] { (chosen.Build, true) }.Concat(chosen.Others.Take(2).Select(o => (o.Build, false)))
                .Where(b => b.Item1.HasStats).Select(b => ToBar(b.Item1, b.Item2)).ToList()
            : [];
        WhyHint = $"{Advice.Count + (BuildSource.Length > 0 ? 1 : 0)} reasons";
        if (pivot is not null && pivot.Summary != _lastPivotSummary)
            Chime(); // You're playing, not watching this window: make new suggestions noticeable.
        _lastPivotSummary = pivot?.Summary;

        ShowSwitchHint = pivot is null && _planner.CanSwitch;
        SwitchHint = $"Latest ranking prefers {string.Join(", ", _planner.LatestDifferences.Select(i => i.Name))} next.";

        Team = new[] { me }.Concat(rec.Game.Allies.Players).Select(p => ToRow(p, p == me, data)).ToList();
        Enemies = rec.Game.Enemies.Players.Select(p => ToRow(p, false, data)).ToList();

        HasAugments = rec.Game.Mode.HasAugments();
        if (HasAugments)
            Augments.SetGame(rec, _planner.Upcoming.Select(i => i.Item).ToList());
        else if (_tab == Tab.Augments)
            SelectTab(Tab.Build);

        RenderBuy();
    }

    // What to buy follows your gold; the tips follow the build and the clock.
    private void RenderBuy()
    {
        if (_planner.Latest is not { } rec || _data is not { } data)
            return;

        var me = rec.Game.Me;
        BuyText = BuyAdvisor.ForBuild(rec, _planner.Upcoming.FirstOrDefault()?.Item, _gold, data.Items)?.Text ?? "";
        ShowSellAdvice = rec.IsFull && BuyText.Length > 0;
        Tips = LateGameAdvisor.Advise(rec, _gold, data.Items);

        var starting = rec.Game.Mode == GameMode.SummonersRift && rec.Game.GameTimeSeconds < 120 && me.Items.Count == 0;
        StartText = starting
            ? "Start with " + string.Join(" and ", ItemSetBuilder.StartingItems(me, _opggChampion, rec.Game.Mode)
                .Select(e => data.Items.Get(e.Id) is { } item ? (e.Count > 1 ? $"{e.Count} {item.Name}s" : item.Name) : null)
                .OfType<string>()) + "."
            : "";
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // The lane matchup only changes when champions or roles do, so it's looked up once per combination.
    private async Task ShowMatchupAsync(GameAnalysis game)
    {
        var request = Matchups is null ? null : MatchupRequest.ForGame(game);
        var key = request is null ? null : $"{request.Me?.Id}|{request.Position}|{string.Join(',', request.Enemies.Select(e => e.Id))}";
        if (key == _matchupKey)
            return;
        _matchupKey = key;
        if (request is null)
        {
            MatchupLine = "";
            MatchupNote = "";
            return;
        }

        var report = await Matchups!.AnalyzeAsync(request);
        if (key != _matchupKey)
            return;
        MatchupNote = report?.Opponent is { } lane && Notes?.Get(lane) is { Length: > 0 } note ? $"Your notes: {note}" : "";
        MatchupLine = report switch
        {
            { Matchup: { } m } => $"vs {m.Opponent.Name}: {m.WinRate:P1} win rate over {m.Games:N0} games \u00b7 {m.Verdict}",
            { Opponent: { } opponent } => $"vs {opponent.Name}",
            _ => "",
        };
    }

    private void SelectTab(Tab tab)
    {
        _tab = tab;
        foreach (var name in new[] { nameof(IsBuildTab), nameof(IsAugmentsTab), nameof(IsPlayersTab) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private IReadOnlyList<string> _pivotNotes = [];

    /// <summary>The switch's reasons as sentences: "Enemy has 3 tanks (...). New since you took this build."</summary>
    public IReadOnlyList<string> PivotNotes { get => _pivotNotes; private set => Set(ref _pivotNotes, value); }

    private static List<SlotRow> BuildSlots(BuildRecommendation rec, List<ItemInfo> finished, List<ScoredItem> upcoming, Pivot? pivot, StaticGameData data)
    {
        var slots = finished.Select(i => new SlotRow(0, "Owned", i.Name, data.ItemIconUrl(i.Id), "", "Owned")).ToList();
        for (var i = 0; i < upcoming.Count; i++)
        {
            var item = upcoming[i].Item;
            // A suggested switch shows on the slot it changes.
            var dropAt = pivot?.Drop.ToList().FindIndex(d => d.Id == item.Id) ?? -1;
            if (pivot is not null && dropAt >= 0 && (pivot.Add.ElementAtOrDefault(dropAt) ?? pivot.Add.FirstOrDefault()) is { } replacement)
            {
                slots.Add(new SlotRow(i + 1, "Switch", $"{replacement.Name}?", data.ItemIconUrl(replacement.Id),
                    rec.Find(replacement.Id) is { } scored ? Tag(scored) : "", $"{item.TotalGold:N0}g → {replacement.TotalGold:N0}g")
                {
                    OldName = item.Name, OldIconUrl = data.ItemIconUrl(item.Id),
                });
                continue;
            }
            if (i > 0)
            {
                slots.Add(new SlotRow(i + 1, "Planned", item.Name, data.ItemIconUrl(item.Id), Tag(upcoming[i]), $"{item.TotalGold:N0}g"));
                continue;
            }
            var done = item.TotalGold <= 0 ? 0 : Math.Clamp(1 - (double)BuyAdvisor.RemainingCost(item, rec.Game.Me.Items, data.Items) / item.TotalGold, 0, 1);
            slots.Add(new SlotRow(1, "Next", item.Name, data.ItemIconUrl(item.Id), Tag(upcoming[i]), "")
            {
                ProgressDone = new(done, GridUnitType.Star), ProgressLeft = new(1 - done, GridUnitType.Star),
            });
        }
        while (slots.Count < BuildRecommendation.FullBuild)
            slots.Add(new SlotRow(0, "Empty", "Empty", "", "", ""));
        return slots;
    }

    // The strongest reason, with its points, or else what the item's passive gives you.
    private static string Tag(ScoredItem item) =>
        item.Reasons.FirstOrDefault() is { } reason ? $"{reason.Situation.Label} +{reason.Points:0.0}"
        : item.Effects.FirstOrDefault() is { } effect ? effect[(effect.IndexOf(": ", StringComparison.Ordinal) + 1)..].Trim() // "Magical Opus: +95 AP" is "+95 AP"
        : "";

    private static MetaBar ToBar(MetaBuild build, bool chosen)
    {
        // 40% to 60% fills the bar: that's where builds differ.
        var filled = Math.Clamp((build.WinRate - 0.40) / 0.20, 0.05, 1.0);
        var name = $"{char.ToUpperInvariant(build.Style.DisplayName()[0])}{build.Style.DisplayName()[1..]}";
        return new MetaBar(name, $"{build.WinRate:P1} · {build.Games:N0} games", new(filled, GridUnitType.Star), new(1 - filled, GridUnitType.Star), chosen);
    }

    private static ItemRow ToRow(ScoredItem item, int rank, StaticGameData data) =>
        new(rank, item.Item.Name, item.Item.TotalGold, data.ItemIconUrl(item.Item.Id), [.. item.Reasons.Select(r => r.Situation.Label), .. item.Effects]);

    private static PlayerRow ToRow(PlayerProfile p, bool isMe, StaticGameData data)
    {
        var s = p.Stats;
        var note = s.IsReal ? "Real stats from the game"
            : "Estimated: base stats at this level plus items. Runes and stacking passives aren't visible.";
        if (p.IsTanky) note += "\nBuilt tanky";
        else if (p.IsSquishy) note += "\nSquishy";
        return new PlayerRow(p.Name, data.ChampionIconUrl(p.Champion.Id), p.Level, isMe, note,
            s.AttackDamage, s.AbilityPower, s.Armor, s.MagicResist, s.Health, s.AttackSpeed, s.CritChance);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
