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
    bool CanBeGolden, bool IsGolden, string Scores, string RerollNote, IReadOnlyList<string> Reasons, string Combos)
{
    /// <summary>1 to 5: how well the card fits right now, as a bar. The exact scores are in <see cref="Scores"/>.</summary>
    public int Strength { get; init; }

    /// <summary>"Strong fit", "Good fit", "Okay", "Weak fit", "Poor fit".</summary>
    public string FitWord { get; init; } = "";

    /// <summary>The bar's five segments, lit up to <see cref="Strength"/>.</summary>
    public IReadOnlyList<bool> Segments => Enumerable.Range(0, 5).Select(i => i < Strength).ToList();

    public bool IsSelected { get; init; }

    /// <summary>"TAKE CRITICAL RHYTHM" for the best card, the card's name otherwise.</summary>
    public string Heading => Rank == 1 ? $"TAKE {Name.ToUpperInvariant()}" : Name.ToUpperInvariant();
}

/// <summary>
/// Augment offers for ARAM: Mayhem, ranked. Offers are read from the screen when possible (<see cref="OnScan"/>);
/// typing cards by hand covers misreads, cards you had before the app started, and games where OCR can't see.
/// </summary>
public sealed class AugmentPickerViewModel : INotifyPropertyChanged
{
    private const int OfferSize = 3;
    private const int MaxPicked = 4;
    private const int MaxSuggestions = 8;

    // One catalog per augment list; the game's mode decides which one is in use.
    private readonly Dictionary<AugmentSet, AugmentCatalog?> _catalogs = [];
    private readonly Dictionary<AugmentSet, string?> _errors = [];
    private AugmentSet _set = AugmentSet.Mayhem;
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

    // Where the cards read from the screen are. Which card you took comes from looks at the HUD once they close,
    // with the card you last clicked while they were up as a backup.
    private OfferLayout? _layout;
    private readonly PickVotes _votes = new();

    // The last offer read from the screen, kept after it closes so the HUD can confirm or correct the card you took.
    // The panel left of the portrait sometimes shows your stats, and then your cards only show up later.
    private List<AugmentInfo> _hudOffer = [];
    private int _hudFilled;
    private DateTime? _hudClosedAt;
    private bool _hudDone;
    private static readonly TimeSpan HudWatch = TimeSpan.FromMinutes(5);
    // Rerolls spent on the slot each offered card sits in, the card with the golden reroll, and rerolls
    // pressed by hand whose replacement card hasn't been typed yet.
    private readonly Dictionary<AugmentInfo, int> _rerollsUsed = [];
    private AugmentInfo? _golden;
    private readonly Queue<(int Used, bool Golden)> _pendingReplacements = new();
    private bool _offerFromScreen;
    private bool _offerOnScreen;
    private string _rankedFor = "";
    private int _scansWithoutOffer;

    private string _searchText = "";
    private string _status = "Loading augment data...";
    private string? _scanProblem;
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
    public IReadOnlyList<AugmentOptionRow> Ranked { get => _ranked; private set { Set(ref _ranked, value); ShowCards(); } }

    private IReadOnlyList<AugmentOptionRow> _cards = [];
    private AugmentOptionRow? _selectedCard;
    private string? _selectedName;

    /// <summary>The offered cards as tiles, in the order they sit on screen, the selected one marked.</summary>
    public IReadOnlyList<AugmentOptionRow> Cards { get => _cards; private set => Set(ref _cards, value); }

    /// <summary>The card whose reasons and buttons show under the tiles: the best one until you click another.</summary>
    public AugmentOptionRow? SelectedCard { get => _selectedCard; private set => Set(ref _selectedCard, value); }

    public void SelectCard(string name)
    {
        _selectedName = name;
        ShowCards();
    }

    private void ShowCards()
    {
        var selected = Ranked.Any(r => r.Name == _selectedName) ? _selectedName : Ranked.FirstOrDefault()?.Name;
        var onScreen = _offer.Select(a => a.Name).ToList();
        Cards = Ranked.OrderBy(r => onScreen.IndexOf(r.Name) is var i and >= 0 ? i : int.MaxValue)
            .Select(r => r with { IsSelected = r.Name == selected }).ToList();
        SelectedCard = Cards.FirstOrDefault(r => r.IsSelected);
    }

    // How well a card fits right now, from its score: 6.1 is a strong fit, 1.7 weak, 0.3 poor.
    private static (int Strength, string Word) Fit(double now) => now switch
    {
        >= 5 => (5, "Strong fit"),
        >= 3.5 => (4, "Good fit"),
        >= 2 => (3, "Okay"),
        >= 0.8 => (2, "Weak fit"),
        _ => (1, "Poor fit"),
    };
    public string AdviceText { get => _adviceText; private set => Set(ref _adviceText, value); }

    /// <summary>Cards are on your screen right now. Compact mode shows the advice while this is true.</summary>
    public bool OfferOnScreen { get => _offerOnScreen; private set => Set(ref _offerOnScreen, value); }

    /// <summary>"Keep X. Reroll Y and Z: ..." Empty when no card can be rerolled.</summary>
    public string RerollText { get => _rerollText; private set => Set(ref _rerollText, value); }
    public bool IsRanking { get => _isRanking; private set => Set(ref _isRanking, value); }
    public bool IsAvailable => _catalog is not null;
    public string Attribution => AugmentDataClient.Attribution;

    /// <summary>
    /// An offer can be on screen. In Mayhem that's once you reach a pick level (3, 7, 11, 15) without taking that many
    /// cards. Arena offers them between rounds, which the game doesn't report, so it watches until you have four.
    /// </summary>
    public bool WantsScan => _catalog is not null && _game is not null
        && _picked.Count < (_set == AugmentSet.Arena ? MaxPicked : GameModes.MayhemAugmentLevels.Count(l => l <= _level));

    /// <summary>The augment list the current game offers. The app reads the screen with this list's card names.</summary>
    public AugmentSet CurrentSet => _set;

    public void SetCatalog(AugmentSet set, AugmentCatalog? catalog, string? error = null)
    {
        _catalogs[set] = catalog;
        _errors[set] = error;
        if (set == _set)
            UseSet(set);
    }

    private void UseSet(AugmentSet set)
    {
        _set = set;
        _catalog = _catalogs.GetValueOrDefault(set);
        _advisor = _catalog is null ? null : new AugmentAdvisor(_catalog, augmentSet: set);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAvailable)));
        UpdateStatus(_errors.GetValueOrDefault(set));
    }

    private CommunityAugments? _community;

    /// <summary>Win rates from a stats site for this game's champion, or null to score from card effects alone.</summary>
    public void SetCommunity(CommunityAugments? community)
    {
        _community = community;
        _rankedFor = "";
        _ = RankAsync();
    }

    /// <summary>Called on every game update: items, levels and enemies change the ranking.</summary>
    public void SetGame(BuildRecommendation game, IReadOnlyList<ItemInfo> plannedItems)
    {
        if (game.Game.Mode.Augments() is { } set && set != _set)
            UseSet(set);
        _game = game;
        _plannedItems = plannedItems;
        _level = game.Game.Me.Level;
        if (_offer.Count == 0)
            UpdateStatus(null);

        // Live updates come every 2 seconds and a ranking simulates 1,500 games per card, so only rank again
        // when something the ranking uses changed.
        var key = $"{_level}|{string.Join(",", game.Game.Me.Items.Select(i => i.Id))}|{string.Join(",", plannedItems.Select(i => i.Id))}"
                  + $"|{string.Join(",", game.Situations.Select(s => s.Label))}";
        if (key == _rankedFor)
            return;
        _rankedFor = key;
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
        _layout = null;
        _votes.Clear();
        _hudOffer = [];
        _hudClosedAt = null;
        ClearRerolls();
        _offerFromScreen = false;
        OfferOnScreen = false;
        _rankedFor = "";
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
        _hudDone = true; // you said which card you have, so the HUD shouldn't change it
        _offer.Remove(augment);
        _picked.Add(augment);
        SearchText = "";
        Changed();
    }

    /// <summary>You took a card from the offer ("I picked this"), so the pick is done.</summary>
    public event EventHandler? OfferPicked;

    /// <summary>You took this card from the offer: it joins your set and the offer is done.</summary>
    public void Pick(string name)
    {
        if (Find(name) is not { } augment || _picked.Count >= MaxPicked)
            return;
        _picked.Add(augment);
        _offer.Clear();
        ClearRerolls();
        _offerFromScreen = false;
        OfferOnScreen = false;
        Changed();
        OfferPicked?.Invoke(this, EventArgs.Empty);
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
    /// <summary>Raised by the "Read cards now" button: scan once, even when no pick is due.</summary>
    public event EventHandler? ScanRequested;

    public void RequestScan()
    {
        Status = "Reading your screen...";
        ScanRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Result of a scan you asked for. Says so when nothing was found, instead of waiting quietly.</summary>
    public void OnRequestedScan(IReadOnlyList<AugmentInfo> detected, string? problem, OfferLayout? layout = null)
    {
        if (detected.Count >= 2)
        {
            _lastDetected = null; // show it even if the same cards were read before
            OnScan(detected, problem, layout);
        }
        else
            Status = problem ?? "No cards found on your screen. Open the offer in game, or type the cards.";
    }

    /// <summary>The cards on screen while an offer read from it is up, so clicks can be matched to them. Null otherwise.</summary>
    public OfferLayout? ClickLayout => OfferOnScreen ? _layout : null;

    /// <summary>You clicked in the game while the offer was up: a click on a card is the card you took.</summary>
    public void NoteClick(double x, double y)
    {
        if (ClickLayout?.CardAt(x, y) is { } card && _offer.Contains(card))
            _votes.Click(card.Name);
    }

    /// <summary>
    /// A Mayhem offer read from the screen closed in the last 5 minutes and the HUD hasn't settled which card you
    /// took, so the HUD should be checked. Arena's HUD isn't measured yet.
    /// </summary>
    public bool WantsHudRead => _set == AugmentSet.Mayhem && _hudOffer.Count > 0 && !_hudDone
        && _hudClosedAt is { } closed && DateTime.UtcNow - closed < HudWatch;

    /// <summary>The cards to look for in the HUD: the offer that closed last.</summary>
    public IReadOnlyList<AugmentInfo> HudCandidates => _hudOffer;

    /// <summary>How many cards the HUD shows once you've taken one from that offer.</summary>
    public int HudFilledAfterPick => _hudFilled;

    /// <summary>
    /// One look at the HUD after the offer closed: the offered card it recognized, or null when it couldn't tell.
    /// Before the pick is decided it's a vote; after, two agreeing looks confirm the pick or replace it.
    /// </summary>
    public void OnHudRead(string? card)
    {
        if (!WantsHudRead)
            return;
        if (card is not null && _hudOffer.Any(a => a.Name == card))
            _votes.Read(card);
        if (_votes.Sure is not { } sure)
            return;

        _hudDone = true;
        var taken = _hudOffer.FirstOrDefault(_picked.Contains);
        if (taken is null)
            TakeVoted(sure);
        else if (taken.Name == sure)
            Status = $"You took {sure}: confirmed in your HUD.";
        else if (Find(sure) is { } shown)
        {
            _picked[_picked.IndexOf(taken)] = shown;
            Changed();
            Status = $"Your HUD shows you took {sure}, not {taken.Name}, so I changed it.";
        }
    }

    private void TakeVoted(string card)
    {
        var how = _votes.Seen(card) ? "read from the cards in your HUD" : "read from your click";
        _hudDone |= _votes.Sure == card;
        Pick(card);
        Status = $"You took {card}: {how}. Wrong card? Remove it under Picked and press the right one.";
    }

    public void OnScan(IReadOnlyList<AugmentInfo> detected, string? problem = null, OfferLayout? layout = null)
    {
        if (detected.Count >= 2 && layout is not null)
            _layout = layout;
        if (problem != _scanProblem)
        {
            _scanProblem = problem;
            if (_offer.Count == 0)
                UpdateStatus(null);
        }
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
            _votes.Clear(); // a click on a card closes the offer; one on a card that's still there was something else
            _offer.Clear();
            _offer.AddRange(detected.Where(a => !_picked.Contains(a)));
            if (!sameOffer)
                _hudFilled = _picked.Count + 1;
            _hudOffer = _offer.ToList();
            _hudClosedAt = null;
            _hudDone = false;
            _offerFromScreen = true;
            OfferOnScreen = true;
            Changed();
            Status = $"Read from your screen: {string.Join(", ", detected)}. Fix a card by removing it and typing the right one.";
            OfferDetected?.Invoke(this, EventArgs.Empty);
        }
        else if (detected.Count == 0 && _offerFromScreen && _offer.Count > 0 && ++_scansWithoutOffer >= 1)
        {
            _hudClosedAt ??= DateTime.UtcNow;
            if (_scansWithoutOffer != 2)
                return;
            // The cards are gone. Looks at the HUD since the first empty scan, and the card you clicked, say which you took.
            if (_votes.Best is { } card)
            {
                TakeVoted(card);
                return;
            }
            OfferOnScreen = false;
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
        _hudDone = true; // you fixed it yourself, so the HUD shouldn't change it back
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
            Community = _community,
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
                o.Partners.Count > 0 ? $"Combos later: {string.Join(", ", o.Partners.Take(3))}" : "")
            {
                Strength = Fit(o.Now).Strength,
                FitWord = Fit(o.Now).Word,
            };
        }).ToList();
        AdviceText = advice.Text;
        RerollText = advice.Reroll?.Text ?? "";
        IsRanking = false;
    }

    private void UpdateStatus(string? error)
    {
        Status = error ?? (_catalog is null ? "Augment data isn't available."
            : _picked.Count >= MaxPicked ? "You have all four augments."
            : _offer.Count == 0 && WantsScan ? $"Watching your screen for the augment offer. You can also type the cards.{(_scanProblem is null ? "" : " " + _scanProblem)}"
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
