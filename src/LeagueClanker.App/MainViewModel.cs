using System.ComponentModel;
using System.Media;
using System.Runtime.CompilerServices;
using System.Windows;
using LeagueClanker.Core;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.App;

public sealed record ItemRow(int Rank, string Name, int Gold, string IconUrl, IReadOnlyList<string> Reasons);

public sealed record PlayerRow(
    string Name, string IconUrl, int Level, bool IsMe, string Note,
    double AttackDamage, double AbilityPower, double Armor, double MagicResist, double Health, double AttackSpeed, double CritChance);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly BuildPlanner _planner = new();
    private StaticGameData? _data;
    private string? _lastPivotSummary;

    private string _status = "Starting...";
    private bool _isLive;
    private bool _showPlayers;
    private string _championLine = "";
    private string _damageSummary = "";
    private GridLength _adShare = new(1, GridUnitType.Star);
    private GridLength _apShare = new(1, GridUnitType.Star);
    private IReadOnlyList<ItemRow> _items = [];
    private ItemRow? _boots;
    private IReadOnlyList<string> _advice = [];
    private string _footer = "";
    private bool _hasPivot;
    private string _pivotSummary = "";
    private IReadOnlyList<string> _pivotReasons = [];
    private bool _showSwitchHint;
    private string _switchHint = "";
    private IReadOnlyList<PlayerRow> _enemies = [];
    private IReadOnlyList<PlayerRow> _team = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }
    public string ChampionLine { get => _championLine; private set => Set(ref _championLine, value); }
    public string DamageSummary { get => _damageSummary; private set => Set(ref _damageSummary, value); }
    public GridLength AdShare { get => _adShare; private set => Set(ref _adShare, value); }
    public GridLength ApShare { get => _apShare; private set => Set(ref _apShare, value); }
    public IReadOnlyList<ItemRow> Items { get => _items; private set => Set(ref _items, value); }
    public ItemRow? Boots { get => _boots; private set => Set(ref _boots, value); }
    public IReadOnlyList<string> Advice { get => _advice; private set => Set(ref _advice, value); }
    public string Footer { get => _footer; set => Set(ref _footer, value); }

    public bool HasPivot { get => _hasPivot; private set => Set(ref _hasPivot, value); }
    public string PivotSummary { get => _pivotSummary; private set => Set(ref _pivotSummary, value); }
    public IReadOnlyList<string> PivotReasons { get => _pivotReasons; private set => Set(ref _pivotReasons, value); }
    public bool ShowSwitchHint { get => _showSwitchHint; private set => Set(ref _showSwitchHint, value); }
    public string SwitchHint { get => _switchHint; private set => Set(ref _switchHint, value); }

    public bool ShowPlayers
    {
        get => _showPlayers;
        set
        {
            Set(ref _showPlayers, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBuild)));
        }
    }

    public bool ShowBuild { get => !_showPlayers; set => ShowPlayers = !value; }
    public IReadOnlyList<PlayerRow> Enemies { get => _enemies; private set => Set(ref _enemies, value); }
    public IReadOnlyList<PlayerRow> Team { get => _team; private set => Set(ref _team, value); }

    public void Apply(AdvisorUpdate update, StaticGameData data)
    {
        _data = data;
        if (update is not { State: AdvisorState.Live, Recommendation: { } rec })
        {
            _planner.Reset();
            _lastPivotSummary = null;
            IsLive = false;
            Status = "Waiting for a game... (Practice Tool works too)";
            return;
        }

        _planner.Update(rec);
        Render();
    }

    public void AcceptPivot()
    {
        _planner.Accept();
        Render();
    }

    public void DeclinePivot()
    {
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
        Status = $"Live · {TimeSpan.FromSeconds(rec.Game.GameTimeSeconds):mm\\:ss}";
        ChampionLine = $"{me.Name} · {me.Archetype.DisplayName()}";
        DamageSummary = rec.DamageSummary;
        AdShare = new GridLength(rec.Game.Enemies.PhysicalShare, GridUnitType.Star);
        ApShare = new GridLength(rec.Game.Enemies.MagicShare, GridUnitType.Star);
        Items = _planner.Upcoming.Select((item, i) => ToRow(item, i + 1, data)).ToList();
        Boots = rec.Boots is { } boots ? ToRow(boots, 0, data) : null;
        Advice = rec.Advice.Count > 0
            ? rec.Advice.Select(a => a.Text).ToList()
            : ["Nothing unusual about this game, so follow your standard build."];

        var pivot = _planner.PendingPivot;
        HasPivot = pivot is not null;
        PivotSummary = pivot?.Summary ?? "";
        PivotReasons = pivot?.Reasons.Select(r => $"Because {char.ToLowerInvariant(r[0])}{r[1..]}").ToList() ?? [];
        if (pivot is not null && pivot.Summary != _lastPivotSummary)
            SystemSounds.Asterisk.Play(); // You're playing, not watching this window: make new suggestions noticeable.
        _lastPivotSummary = pivot?.Summary;

        ShowSwitchHint = pivot is null && _planner.CanSwitch;
        SwitchHint = $"Latest ranking prefers {string.Join(", ", _planner.LatestDifferences.Select(i => i.Name))} next.";

        Team = new[] { me }.Concat(rec.Game.Allies.Players).Select(p => ToRow(p, p == me, data)).ToList();
        Enemies = rec.Game.Enemies.Players.Select(p => ToRow(p, false, data)).ToList();
    }

    private static ItemRow ToRow(ScoredItem item, int rank, StaticGameData data) =>
        new(rank, item.Item.Name, item.Item.TotalGold, data.ItemIconUrl(item.Item.Id), item.Reasons.Select(r => r.Situation.Label).ToList());

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
