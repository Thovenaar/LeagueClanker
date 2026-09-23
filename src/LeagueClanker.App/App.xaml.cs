using System.IO;
using System.Windows;
using LeagueClanker.Core;
using LeagueClanker.Core.LiveClient;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.App;

/// <summary>
/// Usage: LeagueClanker.App.exe [--demo samples/ap-heavy.json | --demo samples/pivot-demo]
/// Without --demo it polls the Live Client Data API while a game is running.
/// A demo folder replays its snapshots in order, which shows pivots happening.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = new MainViewModel();
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();

        var demoPath = ParseDemoPath(e.Args);
        IGameDataSource source = demoPath is null ? new LiveClientApi() : SequenceGameDataSource.FromPath(demoPath);

        try
        {
            viewModel.Status = "Loading item data...";
            var data = await new DataDragonClient().LoadAsync(_cts.Token);
            viewModel.Footer = demoPath is null ? $"Patch {data.Version}" : $"Patch {data.Version} · demo: {Path.GetFileName(demoPath)}";

            await foreach (var update in new BuildAdvisor(source, data).RunAsync(PollInterval, _cts.Token))
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

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        base.OnExit(e);
    }

    private static string? ParseDemoPath(string[] args)
    {
        var index = Array.IndexOf(args, "--demo");
        if (index < 0 || index + 1 >= args.Length)
            return null;

        var path = args[index + 1];
        // Samples are copied next to the exe, so "samples/x.json" works from any working directory.
        return File.Exists(path) || Directory.Exists(path) ? Path.GetFullPath(path) : Path.Combine(AppContext.BaseDirectory, path);
    }
}
