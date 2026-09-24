using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Windows;
using LeagueClanker.Core;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.Matchups;
using LeagueClanker.Core.Opgg;
using LeagueClanker.Core.Runes;
using LeagueClanker.Core.StaticData;
using LeagueClanker.Vision;
using System.Windows.Interop;

namespace LeagueClanker.App;

/// <summary>
/// Usage: LeagueClanker.App.exe [--demo samples/ap-heavy.json | --demo samples/pivot-demo] [--scan-image screenshot.png]
///                              [--champselect samples/champselect/leona-support.json]
/// Without --demo it polls the Live Client Data API while a game is running, and the League client during champ select.
/// A demo folder replays its snapshots in order, which shows pivots happening.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = new MainViewModel();
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        var demoPath = ResolvePath(e.Args, "--demo");
        var scanImage = ResolvePath(e.Args, "--scan-image");
        var champSelectDemo = ResolvePath(e.Args, "--champselect");
        var settings = AppSettings.Load();
        IGameDataSource source = demoPath is null ? new LiveClientApi() : SequenceGameDataSource.FromPath(demoPath);

        try
        {
            viewModel.Status = "Loading item data...";
            var data = await new DataDragonClient().LoadAsync(_cts.Token);
            viewModel.Footer = demoPath is null ? $"v{AppVersion} · Patch {data.Version}" : $"v{AppVersion} · Patch {data.Version} · demo: {Path.GetFileName(demoPath)}";
            if (await LoadAugmentsAsync(viewModel.Augments, data) is { } augments)
                _ = RunScannerAsync(viewModel.Augments, new AugmentScreenReader(augments), scanImage);

            var advisor = new BuildAdvisor(source, data);
            viewModel.Augments.PickedChanged += (_, picked) => advisor.Augments = picked;
            viewModel.PlaystyleChanged += (_, playstyle) => advisor.Playstyle = playstyle;

            var userAgent = $"LeagueClanker/{AppVersion} (+https://github.com/Thovenaar/LeagueClanker)";
            var opgg = new OpggClient(userAgent: userAgent);
            var runes = new RuneAdvisor(new RuleRuneSource(data.Runes), new OpggRuneSource(data.Runes, opgg));
            var matchups = new MatchupAdvisor(opgg, data.Champions);
            viewModel.ChampSelect.Configure(data, runes, matchups, settings.RuneSource);
            viewModel.Matchups = matchups;
            viewModel.ChampSelect.SourceChanged += (_, runeSource) =>
            {
                settings.RuneSource = runeSource;
                settings.Save();
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
            viewModel.Status = $"Error: {ex.Message}";
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Watches the League client for champ select while no game runs. The client's port and password change whenever it
    /// restarts, so the lockfile is read again whenever we're not in champ select. With <paramref name="demoPath"/> it
    /// shows a saved champ select instead, and Apply only says what it would do.
    /// </summary>
    private async Task RunChampSelectAsync(MainViewModel viewModel, StaticGameData data, string? demoPath)
    {
        var champSelect = viewModel.ChampSelect;
        IClientSource? demo = demoPath is null ? null : new FileClientSource(demoPath);
        Lockfile? lockfile = null;
        LeagueClientApi? client = null;

        champSelect.ApplyHandler = demo is not null
            ? (_, name) => Task.FromResult(new ApplyResult(true, $"Demo: this would overwrite your current rune page as \"{name}\"."))
            : (page, name) => client is null
                ? Task.FromResult(new ApplyResult(false, "The League client isn't running."))
                : new RunePageWriter(client).ApplyAsync(page, name, _cts.Token);

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
                    if (snapshot is null && LeagueClientApi.FindLockfile() is var found && found != lockfile)
                    {
                        client?.Dispose();
                        lockfile = found;
                        client = found is null ? null : new LeagueClientApi(found);
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
            viewModel.Status = $"Champ select error: {ex.Message}";
        }
        finally
        {
            client?.Dispose();
        }
    }

    // Augments only matter in ARAM: Mayhem, so a failure here must not stop the build advisor.
    private async Task<AugmentCatalog?> LoadAugmentsAsync(AugmentPickerViewModel picker, StaticGameData data)
    {
        try
        {
            var catalog = await new AugmentDataClient().LoadMayhemAsync(data.Items, _cts.Token);
            picker.SetCatalog(catalog);
            return catalog;
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or IOException)
        {
            picker.SetCatalog(null, $"Couldn't load augment data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the augment offer off the screen while a pick is due. Capture and OCR run off the UI thread;
    /// results are applied on it. With <paramref name="imagePath"/> it reads that screenshot instead (demo).
    /// </summary>
    private async Task RunScannerAsync(AugmentPickerViewModel picker, AugmentScreenReader reader, string? imagePath)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        var ownWindow = new WindowInteropHelper(MainWindow).Handle;
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (!picker.WantsScan)
                    continue;
                try
                {
                    var scan = imagePath is null
                        ? await Task.Run(() => reader.ScanScreenAsync(exclude: ScreenCapture.WindowArea(ownWindow)))
                        : await Task.Run(() => reader.ScanFileAsync(imagePath));
                    picker.OnScan(scan.Offer.Select(d => d.Augment).ToList());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed capture (e.g. the game window closing mid-scan) is retried on the next tick.
                    System.Diagnostics.Debug.WriteLine($"Augment scan failed: {ex.Message}");
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
