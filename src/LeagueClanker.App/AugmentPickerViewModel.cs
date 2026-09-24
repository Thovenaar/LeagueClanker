using System.ComponentModel;
using System.Runtime.CompilerServices;
using LeagueClanker.Core;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.App;

public sealed record AugmentRow(string Name, string Tier, string Description);

/// <param name="Badge">KEEP, REROLL, REROLL LAST, GOLDEN REROLL, REROLLED, or BEST when rerolls don't apply.</param>
/// <param name="CanReroll">The card still has a reroll and the advice is to use it.</param>
/// <param name="CanBeGolden">The card still has its reroll, so it can be the one with the golden reroll.</param>
public sealed record AugmentOptionRow(
    int Rank, string Name, string Tier, string Description, string Badge, bool BadgeFilled, bool CanReroll,
    bool CanBeGolden, bool IsGolden, string Scores, string RerollNote, IReadOnlyList<string> Reasons, string Combos);

/// <summary>
/// Augment offers for ARAM: Mayhem, ranked. Offers are read from the screen when possible (<see cref="OnScan"/>);
/// typing cards by hand covers misreads, cards you had before the app started, and games where OCR can't see.
/// </summary>
public sealed class AugmentPickerViewModel : INotifyPropertyChanged
{
    private const int OfferSize = 3;
    private const int MaxPicked = 4;
    private const int MaxSuggestions = 8;

    private AugmentCatalog? _catalog;
    private AugmentAdvisor? _advisor;
    private BuildRecommendation? _game;
    private IReadOnlyList<ItemInfo> _plannedItems = [];
    private readonly List<AugmentInfo> _offer = [];
    private readonly List<AugmentInfo> _picked = [];
    private int _rankVersion;
    private int _level;
    private string? _lastDetected;
    private List<AugmentInfo> _lastDetectedCards = [];
    // Rerolls spent on the slot each offered card sits in, the card with the golden reroll, and rerolls
    // pressed by hand whose replacement card hasn't been typed yet.
    private readonly Dictionary<AugmentInfo, int> _rerollsUsed = [];
    private AugmentInfo? _golden;
    private readonly Queue<(int Used, bool Golden)> _pendingReplacements = new();
    private bool _offerFromScreen;
    private int _scansWithoutOffer;

    private string _searchText = "";
    private string _status = "Loading augment data...";
    private IReadOnlyList<AugmentRow> _suggestions = [];
    private IReadOnlyList<AugmentRow> _offerRows = [];
    private IReadOnlyList<AugmentRow> _pickedRows = [];
    private IReadOnlyList<AugmentOptionRow> _ranked = [];
    private string _adviceText = "";
    private string _rerollText = "";
    private bool _isRanking;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A new offer was read from the screen.</summary>
    public event EventHandler? OfferDetected;

    /// <summary>Your picked augments changed. The item advice takes them into account.</summary>
    public event EventHandler<IReadOnlyList<AugmentInfo>>? PickedChanged;
    private string _lastPickedKey = "";

    public string SearchText
    {
        get => _searchText;
        set
        {
            Set(ref _searchText, value);
            RefreshSuggestions();
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public IReadOnlyList<AugmentRow> Suggestions { get => _suggestions; private set => Set(ref _suggestions, value); }
    public IReadOnlyList<AugmentRow> Offer { get => _offerRows; private set => Set(ref _offerRows, value); }
    public IReadOnlyList<AugmentRow> Picked { get => _pickedRows; private set => Set(ref _pickedRows, value); }
    public IReadOnlyList<AugmentOptionRow> Ranked { get => _ranked; private set => Set(ref _ranked, value); }
    public string AdviceText { get => _adviceText; private set => Set(ref _adviceText, value); }

    /// <summary>"Keep X. Reroll Y and Z: ..." Empty when no card can be rerolled.</summary>
    public string RerollText { get => _rerollText; private set => Set(ref _rerollText, value); }
    public bool IsRanking { get => _isRanking; private set => Set(ref _isRanking, value); }
    public bool IsAvailable => _catalog is not null;
    public string Attribution => AugmentDataClient.Attribution;

    /// <summary>You've reached a pick level (3, 7, 11, 15) without taking that many cards, so an offer can be on screen.</summary>
    public bool WantsScan => _catalog is not null && _game is not null && _picked.Count < GameModes.MayhemAugmentLevels.Count(l => l <= _level);

    public void SetCatalog(AugmentCatalog? catalog, string? error = null)
    {
        _catalog = catalog;
        _advisor = catalog is null ? null : new AugmentAdvisor(catalog);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAvailable)));
        UpdateStatus(error);
    }

    /// <summary>Called on every game update: items, levels and enemies change the ranking.</summary>
    public void SetGame(BuildRecommendation game, IReadOnlyList<ItemInfo> plannedItems)
    {
        _game = game;
        _plannedItems = plannedItems;
        _level = game.Game.Me.Level;
        if (_offer.Count == 0)
            UpdateStatus(null);
        _ = RankAsync();
    }

    /// <summary>New game: forget cards.</summary>
    public void Reset()
    {
        _game = null;
        _offer.Clear();
        _picked.Clear();
        _lastDetected = null;
        _lastDetectedCards = [];
        ClearRerolls();
        _offerFromScreen = false;
        _scansWithoutOffer = 0;
        SearchText = "";
        Changed();
    }

    public void AddToOffer(string name)
    {
        if (Find(name) is not { } augment || _offer.Contains(augment) || _picked.Contains(augment))
            return;
        // Every card in one offer shares a tier, except the one a golden reroll lifted a tier.
        var baseTier = _offer.Count > 0 ? _offer.Min(a => a.Tier) : augment.Tier;
        var goldenResult = _pendingReplacements.TryPeek(out var next) && next.Golden && augment.Tier == baseTier + 1;
        if (_offer.Count >= OfferSize || (augment.Tier != baseTier && !goldenResult))
        {
            _offer.Clear();
            ClearRerolls();
        }
        _offer.Add(augment);
        if (_pendingReplacements.TryDequeue(out var replaced))
            _rerollsUsed[augment] = replaced.Used; // it sits in a slot that already spent rerolls
        SearchText = "";
        Changed();
    }

    public void AddToPicked(string name)
    {
        if (Find(name) is not { } augment || _picked.Contains(augment) || _picked.Count >= MaxPicked)
            return;
        _offer.Remove(augment);
        _picked.Add(augment);
        SearchText = "";
        Changed();
    }

    /// <summary>You took this card from the offer: it joins your set and the offer is done.</summary>
    public void Pick(string name)
    {
        if (Find(name) is not { } augment || _picked.Count >= MaxPicked)
            return;
        _picked.Add(augment);
        _offer.Clear();
        ClearRerolls();
        _offerFromScreen = false;
        Changed();
    }

    /// <summary>You rerolled this card in game: it leaves the offer and the card you type next replaces it.</summary>
    public void MarkRerolled(string name)
    {
        if (Find(name) is not { } augment || !_offer.Remove(augment))
            return;
        var golden = augment == _golden;
        _pendingReplacements.Enqueue((golden ? RerollsPerCard : _rerollsUsed.GetValueOrDefault(augment) + 1, golden));
        if (golden)
            _golden = null;
        Changed();
        Status = golden
            ? $"Type the {augment.Tier + 1} card that replaced {augment.Name}."
            : $"Type the card that replaced {augment.Name}.";
    }

    /// <summary>Marks the card whose reroll button is golden in game (it rerolls into the next tier). Press again to unmark.</summary>
    public void ToggleGolden(string name)
    {
        if (Find(name) is not { } augment || !_offer.Contains(augment))
            return;
        _golden = _golden == augment ? null : augment;
        _ = RankAsync();
    }

    /// <summary>2 rerolls per card in the selection right after "Stats on Stats on Stats!", otherwise 1.</summary>
    private int RerollsPerCard => _picked.Count > 0 && _picked[^1].GrantsExtraRerolls ? 2 : 1;

    private void ClearRerolls()
    {
        _rerollsUsed.Clear();
        _golden = null;
        _pendingReplacements.Clear();
    }

    /// <summary>
    /// Result of reading the screen. A new set of cards replaces the offer (a reroll changes one card).
    /// The same cards again are ignored, so a name you corrected by hand stays corrected.
    /// </summary>
    public void OnScan(IReadOnlyList<AugmentInfo> detected)
    {
        if (detected.Count >= 2)
        {
            _scansWithoutOffer = 0;
            var key = string.Join("|", detected.Select(a => a.Name).Order());
            if (key == _lastDetected)
                return;

            // One or two cards changed while others stayed: those were rerolled. Cards are read left to right,
            // so a changed position tells which slot spent a reroll. A card a tier higher came from the golden reroll.
            var sameOffer = _lastDetectedCards.Count > 0 && detected.Any(_lastDetectedCards.Contains);
            if (!sameOffer)
                ClearRerolls();
            else
                RecordRerolls(_lastDetectedCards, detected);

            _lastDetected = key;
            _lastDetectedCards = detected.ToList();
            _offer.Clear();
            _offer.AddRange(detected.Where(a => !_picked.Contains(a)));
            _offerFromScreen = true;
            Changed();
            Status = $"Read from your screen: {string.Join(", ", detected)}. Fix a card by removing it and typing the right one.";
            OfferDetected?.Invoke(this, EventArgs.Empty);
        }
        else if (detected.Count == 0 && _offerFromScreen && _offer.Count > 0 && ++_scansWithoutOffer == 2)
        {
            // We can't see which card was clicked, only that the cards are gone.
            Status = "The offer closed. Which card did you take? Press \"I picked this\" on it.";
        }
    }

    private void RecordRerolls(IReadOnlyList<AugmentInfo> before, IReadOnlyList<AugmentInfo> after)
    {
        var baseTier = before.Min(a => a.Tier);
        for (var i = 0; i < after.Count; i++)
        {
            if (before.Contains(after[i]))
                continue;
            var replaced = before.Count == after.Count ? before[i] : null;
            if (after[i].Tier > baseTier)
            {
                _rerollsUsed[after[i]] = RerollsPerCard;
                _golden = null;
            }
            else
            {
                _rerollsUsed[after[i]] = (replaced is null ? 0 : _rerollsUsed.GetValueOrDefault(replaced)) + 1;
            }
        }
    }

    public void RemoveFromOffer(string name)
    {
        _offer.RemoveAll(a => a.Name == name);
        Changed();
    }

    public void RemoveFromPicked(string name)
    {
        _picked.RemoveAll(a => a.Name == name);
        Changed();
    }

    /// <summary>Enter in the search box: the top suggestion joins the offer.</summary>
    public void AddTopSuggestion()
    {
        if (Suggestions.FirstOrDefault() is { } top)
            AddToOffer(top.Name);
    }

    private void Changed()
    {
        Offer = _offer.Select(ToRow).ToList();
        Picked = _picked.Select(ToRow).ToList();
        var pickedKey = string.Join("|", _picked.Select(a => a.Name));
        if (pickedKey != _lastPickedKey)
        {
            _lastPickedKey = pickedKey;
            PickedChanged?.Invoke(this, _picked.ToList());
        }
        RefreshSuggestions();
        UpdateStatus(null);
        _ = RankAsync();
    }

    private void RefreshSuggestions()
    {
        if (_catalog is null || string.IsNullOrWhiteSpace(_searchText))
        {
            Suggestions = [];
            return;
        }

        var query = AugmentCatalog.Key(_searchText);
        var offerTier = _offer.Count is > 0 and < OfferSize ? _offer[0].Tier : (AugmentTier?)null;
        Suggestions = _catalog.Offerable
            .Where(a => !_offer.Contains(a) && !_picked.Contains(a))
            .Where(a => offerTier is null || a.Tier == offerTier)
            .Select(a => (Augment: a, Key: AugmentCatalog.Key(a.Name)))
            .Where(x => x.Key.Contains(query))
            .OrderBy(x => x.Key.StartsWith(query) ? 0 : 1)
            .ThenBy(x => x.Augment.Name)
            .Take(MaxSuggestions)
            .Select(x => ToRow(x.Augment))
            .ToList();
    }

    private async Task RankAsync()
    {
        var version = ++_rankVersion;
        if (_advisor is null || _game is null || _offer.Count == 0)
        {
            Ranked = [];
            AdviceText = "";
            RerollText = "";
            IsRanking = false;
            return;
        }

        var offer = _offer.ToList();
        var ctx = new AugmentContext(_game.Game)
        {
            Picked = _picked.ToList(),
            PlannedItems = _plannedItems,
            Situations = _game.Situations,
        };

        IsRanking = true;
        var advisor = _advisor;
        var rerolls = new RerollState
        {
            Used = new Dictionary<AugmentInfo, int>(_rerollsUsed),
            Golden = _golden is not null && offer.Contains(_golden) ? _golden : null,
            RerollsPerCard = RerollsPerCard,
        };
        var advice = await Task.Run(() => advisor.Rank(offer, ctx, rerolls));
        if (version != _rankVersion)
            return; // the offer or game changed while simulating; a newer ranking is on its way

        Ranked = advice.Ranked.Select((o, i) =>
        {
            var reroll = advice.Reroll?.For(o.Augment);
            var (badge, filled) = reroll?.Action switch
            {
                RerollAction.Keep => ("KEEP", true),
                RerollAction.Reroll => ("REROLL", false),
                RerollAction.RerollLast => ("REROLL LAST", false),
                RerollAction.GoldenReroll => ("GOLDEN REROLL", false),
                RerollAction.GoldenRerollLast => ("GOLDEN REROLL LAST", false),
                RerollAction.AlreadyRerolled => ("REROLLED", false),
                _ => (i == 0 ? "BEST" : "", true),
            };
            var note = reroll is null || reroll.Action == RerollAction.AlreadyRerolled
                ? ""
                : $"A {(reroll.Action is RerollAction.GoldenReroll or RerollAction.GoldenRerollLast ? "golden " : "")}reroll beats it {reroll.RerollBeatsIt:P0} of the time.";
            return new AugmentOptionRow(
                i + 1,
                o.Augment.Name,
                o.Augment.Tier.ToString(),
                o.Augment.Description,
                badge,
                filled,
                reroll?.Action is RerollAction.Reroll or RerollAction.RerollLast or RerollAction.GoldenReroll or RerollAction.GoldenRerollLast,
                rerolls.RerollsLeft(o.Augment) > 0 && o.Augment.Tier < AugmentTier.Prismatic,
                o.Augment == rerolls.Golden,
                $"now {o.Now:0.0} · with future picks {o.Expected:0.0}",
                note,
                o.Reasons.OrderByDescending(r => Math.Abs(r.Points)).Take(3).Select(r => (r.Points < 0 ? "− " : "+ ") + r.Text).ToList(),
                o.Partners.Count > 0 ? $"Combos later: {string.Join(", ", o.Partners.Take(3))}" : "");
        }).ToList();
        AdviceText = advice.Text;
        RerollText = advice.Reroll?.Text ?? "";
        IsRanking = false;
    }

    private void UpdateStatus(string? error)
    {
        Status = error ?? (_catalog is null ? "Augment data isn't available."
            : _picked.Count >= MaxPicked ? "You have all four augments."
            : _offer.Count == 0 && WantsScan ? "Watching your screen for the augment offer. You can also type the cards."
            : _offer.Count == 0 ? "Type the cards you're offered. Add cards you already have as picked."
            : _offer.Count < OfferSize ? $"Add the other {OfferSize - _offer.Count} offered card{(OfferSize - _offer.Count == 1 ? "" : "s")}, or look at the ranking so far."
            : RerollsPerCard > 1 ? "You have 2 rerolls per card this time (Stats on Stats on Stats!)."
            : "Ranked for your champion, cards and items. If a card's reroll button is golden, mark it with Golden.");
    }

    private AugmentInfo? Find(string name) => _catalog?.Find(name);

    private static AugmentRow ToRow(AugmentInfo a) => new(a.Name, a.Tier.ToString(), a.Description);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
