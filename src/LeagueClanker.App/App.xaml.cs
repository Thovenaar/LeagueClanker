using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Windows;
using LeagueClanker.Core;
using LeagueClanker.Core.Analysis;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.History;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Recommendation;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.Spells;
using LeagueClanker.Core.StaticData;
using LeagueClanker.Vision;
using System.Windows.Interop;

namespace LeagueClanker.App;

/// <summary>
/// Usage: LeagueClanker.App.exe [--demo samples/ap-heavy.json | --demo samples/pivot-demo] [--scan-image screenshot.png]
///                              [--champselect samples/champselect/leona-support.json] [--games samples/history/games.json]
/// Without --demo it polls the Live Client Data API while a game is running, and the League client during champ select.
/// A demo folder replays its snapshots in order, which shows pivots happening.
/// --games shows a copy of a saved game history instead of your own.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();

    // What champ select reads from: the client, or a saved snapshot in demos. Used for saving snapshots.
    private ISnapshotSource? _champSelectSource;

    // Match history from the client, read once per connection and again after each game.
    private IReadOnlyList<PlayedGame> _matchHistory = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartLogging();

        var viewModel = new MainViewModel(new SettingsViewModel(AppSettings.Load()));
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        var demoPath = ResolvePath(e.Args, "--demo");
        var scanImage = ResolvePath(e.Args, "--scan-image");
        var champSelectDemo = ResolvePath(e.Args, "--champselect");
        var gamesDemo = ResolvePath(e.Args, "--games");
        IGameDataSource source = demoPath is null ? new LiveClientApi() : SequenceGameDataSource.FromPath(demoPath);
        viewModel.SnapshotProvider = () =>
            viewModel.IsLive && (source as ISnapshotSource)?.SnapshotJson is { } game ? (game, "game")
            : viewModel.ShowChampSelect && _champSelectSource?.SnapshotJson is { } champSelect ? (champSelect, "champselect")
            : null;
        if (viewModel.Settings.CheckForUpdates)
            _ = CheckForUpdatesAsync(viewModel);

        try
        {
            viewModel.Status = "Loading item data...";
            var data = await new DataDragonClient().LoadAsync(_cts.Token);
            viewModel.Footer = demoPath is null ? $"v{AppVersion} · Patch {data.Version}" : $"v{AppVersion} · Patch {data.Version} · demo: {Path.GetFileName(demoPath)}";
            var catalogs = new Dictionary<AugmentSet, AugmentCatalog>();
            var readers = new Dictionary<AugmentSet, AugmentScreenReader>();
            foreach (var set in Enum.GetValues<AugmentSet>())
                if (await LoadAugmentsAsync(viewModel.Augments, set, data) is { } augments)
                {
                    catalogs[set] = augments;
                    readers[set] = new AugmentScreenReader(augments);
                }
            _ = RunScannerAsync(viewModel.Augments, () => readers.GetValueOrDefault(viewModel.Augments.CurrentSet), scanImage);

            // Once the League client says it runs in another language, read the cards in that language.
            var readerLocale = "en_US";
            _useClientLocale = async locale =>
            {
                if (locale == readerLocale || catalogs.Count == 0)
                    return;
                readerLocale = locale;
                var names = await new AugmentDataClient().LoadLocalNamesAsync(locale, _cts.Token);
                foreach (var (set, catalog) in catalogs)
                    readers[set] = new AugmentScreenReader(catalog.WithLocalNames(names), locale);
                Log.Write($"Reading augment cards in {locale} ({names.Count} translated names)");
            };

            var advisor = new BuildAdvisor(source, data);
            viewModel.Augments.PickedChanged += (_, picked) => advisor.Augments = picked;
            viewModel.PlaystyleChanged += (_, playstyle) => advisor.Playstyle = playstyle;

            var userAgent = $"LeagueClanker/{AppVersion} (+https://github.com/Thovenaar/LeagueClanker)";
            var opgg = new OpggClient(userAgent: userAgent);
            var runes = new RuneAdvisor(new RuleRuneSource(data.Runes), new OpggRuneSource(data.Runes, opgg));
            var matchups = new MatchupAdvisor(opgg, data.Champions);
            var notes = new MatchupNotes(Path.Combine(AppPaths.DataFolder, "notes.json"));
            viewModel.ChampSelect.Configure(new ChampSelectServices(
                data, runes, matchups, new SpellAdvisor(data.Spells, opgg), opgg, new DraftAdvisor(opgg, data.Champions), notes));
            viewModel.Matchups = matchups;
            viewModel.Notes = notes;
            var recaps = new RecapStore(gamesDemo is null ? Path.Combine(AppPaths.DataFolder, "games.json") : CopyToTemp(gamesDemo));
            viewModel.UseRecaps(recaps, data);
            if (recaps.Games.FirstOrDefault() is { Win: null } unfinished)
                _ = FillInResultAsync(viewModel, recaps, unfinished); // the client still shows that game's end screen
            void RefreshStats() => viewModel.SetStats(PersonalStats.Combine(recaps.Games, _matchHistory, data.Champions));
            RefreshStats();
            _refreshStats = RefreshStats;
            viewModel.GamePlayed += (_, recap) =>
            {
                RefreshStats();
                _historyStale = true;
                if (recap.Win is null)
                    _ = FillInResultAsync(viewModel, recaps, recap);
            };

            // op.gg's data for your champion: popular items for the build, starting items at the start.
            GameAnalysis? liveGame = null;
            async Task LoadLiveOpggAsync()
            {
                if (liveGame is not { } game)
                    return;
                var useOpgg = viewModel.Settings.RuneSource == RuneSourceKind.StatsSite && game.Mode is GameMode.SummonersRift or GameMode.Aram or GameMode.AramMayhem;
                var champion = useOpgg ? await LoadOpggChampionAsync(opgg, game) : null;
                if (liveGame != game)
                    return; // another game or champion by now
                var useMeta = champion is not null && viewModel.Settings.UsePopularItems;
                advisor.PopularItems = useMeta
                    ? champion!.CoreItems.Take(2).SelectMany(c => c.Ids).Where(id => data.Items.Get(id)?.Kind == ItemKind.Legendary).ToHashSet()
                    : new HashSet<int>();
                advisor.MetaBuilds = useMeta ? MetaBuilds.From(champion!, data.Items, game.Mode) : [];
                viewModel.SetOpggChampion(champion);
            }
            // arammayhem.com's win rates for Mayhem cards, and the cards players of your champion take most.
            async Task LoadCommunityAugmentsAsync()
            {
                if (liveGame is not { Mode: GameMode.AramMayhem } game || !viewModel.Settings.UseCommunityAugments)
                {
                    viewModel.Augments.SetCommunity(null);
                    return;
                }
                try
                {
                    var community = await new CommunityAugmentClient().LoadAsync(game.Me.Champion.Name, _cts.Token);
                    if (liveGame == game)
                        viewModel.Augments.SetCommunity(community.Count > 0 ? community : null);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    Log.Error($"Loading augment win rates from {CommunityAugments.Source}", ex);
                    viewModel.Augments.SetCommunity(null);
                }
            }
            viewModel.LiveChampionChanged += (_, game) =>
            {
                liveGame = game;
                _ = LoadLiveOpggAsync();
                _ = LoadCommunityAugmentsAsync();
            };
            viewModel.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SettingsViewModel.UsePopularItems) or nameof(SettingsViewModel.RuneSource))
                    _ = LoadLiveOpggAsync();
                if (e.PropertyName is nameof(SettingsViewModel.UseCommunityAugments))
                    _ = LoadCommunityAugmentsAsync();
            };
            _ = RunChampSelectAsync(viewModel, data, champSelectDemo);

            await foreach (var update in advisor.RunAsync(PollInterval, _cts.Token))
                viewModel.Apply(update, data);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Build advisor stopped", ex);
            viewModel.Status = $"Error: {ex.Message}";
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }

    private static string CopyToTemp(string path)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"leagueclanker-{Guid.NewGuid():N}.json");
        File.Copy(path, copy);
        return copy;
    }

    private Action? _refreshStats;

    // The connected League client, for reading the end-of-game screen.
    private LeagueClientApi? _client;

    /// <summary>
    /// The game closed before its "GameEnd" event reached the app, so the recap has no result. The client's end-of-game
    /// screen does; it takes a few seconds to arrive, so ask for up to a minute and a half.
    /// </summary>
    private async Task FillInResultAsync(MainViewModel viewModel, RecapStore recaps, GameRecap recap)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token);
            if (_client is not { } client || await client.GetEndOfGameAsync(_cts.Token) is not { } result || !result.Matches(recap))
                continue;
            var updated = recap with { Win = result.Win };
            recaps.Replace(recap, updated);
            viewModel.UpdateRecap(updated);
            Log.Write($"Result from the end-of-game screen: {(result.Win ? "win" : "loss")}");
            return;
        }
    }
    private Func<string, Task>? _useClientLocale;

    private async Task UseClientLocaleAsync(LeagueClientApi client)
    {
        try
        {
            if (await client.GetLocaleAsync(_cts.Token) is { Length: > 0 } locale && _useClientLocale is { } use)
                await use(locale);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException or TaskCanceledException)
        {
            Log.Error("Loading augment names in the client's language", ex);
        }
    }
    private bool _historyStale = true;

    // Up to 30 games, with details for the 15 newest to find lane opponents. Slow on purpose: it's a one-off per session.
    private async Task LoadMatchHistoryAsync(LeagueClientApi client)
    {
        try
        {
            _matchHistory = await client.GetMatchHistoryAsync(games: 30, withDetails: 15, _cts.Token);
            Log.Write($"Read {_matchHistory.Count} Summoner's Rift and League Classic games from the match history");
            _refreshStats?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException)
        {
            Log.Error("Reading the match history", ex);
        }
    }

    private static async Task<OpggChampion?> LoadOpggChampionAsync(OpggClient opgg, GameAnalysis game)
    {
        try
        {
            var aram = game.Mode is GameMode.Aram or GameMode.AramMayhem;
            return await opgg.GetChampionAsync(game.Me.Champion.Key, aram, OpggClient.RoleFor(game.Me.Position, game.Me.Archetype), default);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Log.Error("op.gg data for the live game", ex);
            return null;
        }
    }

    // Everything the app recovers from, and anything it doesn't, ends up in %LOCALAPPDATA%\LeagueClanker\log.txt.
    private void StartLogging()
    {
        Log.Sink = FileLog.Write;
        Log.Write($"LeagueClanker {AppVersion} started");
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled error", e.Exception);
            if (MainWindow?.DataContext is MainViewModel viewModel)
                viewModel.Status = $"Error: {e.Exception.Message} (details in the log)";
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Crash: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task error", e.Exception);
            e.SetObserved();
        };
    }

    private async Task CheckForUpdatesAsync(MainViewModel viewModel)
    {
        if (await UpdateChecker.CheckAsync(AppVersion, _cts.Token) is { } update)
            viewModel.ShowUpdate(update.Version, update.Url);
    }

    /// <summary>
    /// Watches the League client for champ select while no game runs. The client's port and password change whenever it
    /// restarts, so the lockfile is read again whenever we're not in champ select. With <paramref name="demoPath"/> it
    /// shows a saved champ select instead, and Apply only says what it would do.
    /// </summary>
    private async Task RunChampSelectAsync(MainViewModel viewModel, StaticGameData data, string? demoPath)
    {
        var champSelect = viewModel.ChampSelect;
        var demo = demoPath is null ? null : new FileClientSource(demoPath);
        Lockfile? lockfile = null;
        LeagueClientApi? client = null;

        champSelect.Writer = demo is not null ? new DemoWriter() : new ClientWriter(() => client);
        _champSelectSource = demo;

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                if (viewModel.IsLive)
                {
                    champSelect.Update(null);
                    continue;
                }

                ClientSnapshot? snapshot;
                if (demo is not null)
                {
                    snapshot = await demo.TryGetChampSelectAsync(_cts.Token);
                }
                else
                {
                    snapshot = client is null ? null : await client.TryGetChampSelectAsync(_cts.Token);
                    if (client is not null && _historyStale)
                    {
                        _historyStale = false;
                        _ = LoadMatchHistoryAsync(client);
                        _ = UseClientLocaleAsync(client);
                    }
                    if (snapshot is null && LeagueClientApi.FindLockfile() is var found && found != lockfile)
                    {
                        client?.Dispose();
                        lockfile = found;
                        client = found is null ? null : new LeagueClientApi(found);
                        _champSelectSource = client;
                        _client = client;
                        _historyStale = client is not null;
                        Log.Write(client is null ? "League client closed" : $"Connected to the League client on port {found!.Port}");
                        snapshot = client is null ? null : await client.TryGetChampSelectAsync(_cts.Token);
                    }
                }
                champSelect.Update(snapshot is null ? null : ChampSelectState.From(snapshot, data.Champions));
            }
            while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Log.Error("Champ select stopped", ex);
            viewModel.Status = $"Champ select error: {ex.Message}";
        }
        finally
        {
            client?.Dispose();
        }
    }

    // Augments only matter in ARAM: Mayhem and Arena, so a failure here must not stop the build advisor.
    private async Task<AugmentCatalog?> LoadAugmentsAsync(AugmentPickerViewModel picker, AugmentSet set, StaticGameData data)
    {
        try
        {
            var catalog = await new AugmentDataClient().LoadAsync(set, data.Items, _cts.Token);
            picker.SetCatalog(set, catalog);
            return catalog;
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or IOException)
        {
            Log.Error($"Loading {set} augment data", ex);
            picker.SetCatalog(set, null, $"Couldn't load augment data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the augment offer off the screen while a pick is due. Capture and OCR run off the UI thread;
    /// results are applied on it. With <paramref name="imagePath"/> it reads that screenshot instead (demo).
    /// </summary>
    private async Task RunScannerAsync(AugmentPickerViewModel picker, Func<AugmentScreenReader?> readerForGame, string? imagePath)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        var ownWindow = new WindowInteropHelper(MainWindow).Handle;
        picker.ScanRequested += async (_, _) =>
        {
            if (readerForGame() is not { } reader)
            {
                picker.OnRequestedScan([], "Augment data isn't loaded, so cards can't be read.");
                return;
            }
            try
            {
                var scan = imagePath is null
                    ? await Task.Run(() => reader.ScanScreenAsync(exclude: ScreenCapture.WindowArea(ownWindow)))
                    : await Task.Run(() => reader.ScanFileAsync(imagePath));
                picker.OnRequestedScan(scan.Offer.Select(d => d.Augment).ToList(), scan.Problem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Augment scan", ex);
                picker.OnRequestedScan([], "Reading the screen failed. Type the cards instead.");
            }
        };
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (!picker.WantsScan || readerForGame() is not { } reader)
                    continue;
                try
                {
                    var scan = imagePath is null
                        ? await Task.Run(() => reader.ScanScreenAsync(exclude: ScreenCapture.WindowArea(ownWindow)))
                        : await Task.Run(() => reader.ScanFileAsync(imagePath));
                    picker.OnScan(scan.Offer.Select(d => d.Augment).ToList(), scan.Problem);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed capture (e.g. the game window closing mid-scan) is retried on the next tick.
                    Log.Error("Augment scan", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        base.OnExit(e);
    }

    // "0.2.0" in releases, "0.0.0-dev" in local builds; the build adds "+<commit>", which isn't shown.
    private static string AppVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    private static string? ResolvePath(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length)
            return null;

        var path = args[index + 1];
        // Samples are copied next to the exe, so "samples/x.json" works from any working directory.
        return File.Exists(path) || Directory.Exists(path) ? Path.GetFullPath(path) : Path.Combine(AppContext.BaseDirectory, path);
    }
}
