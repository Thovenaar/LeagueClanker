using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Windows;
using LeagueClanker.Core;
using LeagueClanker.Core.Augments;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.StaticData;
using LeagueClanker.Vision;
using System.Windows.Interop;

namespace LeagueClanker.App;

/// <summary>
/// Usage: LeagueClanker.App.exe [--demo samples/ap-heavy.json | --demo samples/pivot-demo] [--scan-image screenshot.png]
/// Without --demo it polls the Live Client Data API while a game is running.
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
