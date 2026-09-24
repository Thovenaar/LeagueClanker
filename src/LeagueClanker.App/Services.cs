using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LeagueClanker.Core;
using LeagueClanker.Core.ItemSets;
using LeagueClanker.Core.LeagueClient;
using LeagueClanker.Core.Runes;

namespace LeagueClanker.App;

/// <summary>Appends to %LOCALAPPDATA%\LeagueClanker\log.txt. Past 1 MB the log moves to log.old.txt and starts over.</summary>
public static class FileLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                if (File.Exists(AppPaths.LogFile) && new FileInfo(AppPaths.LogFile).Length > MaxBytes)
                    File.Move(AppPaths.LogFile, Path.ChangeExtension(AppPaths.LogFile, ".old.txt"), overwrite: true);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Couldn't write the log: {ex.Message}");
            }
        }
    }
}

/// <summary>Saves what the app sees right now, with player names replaced, so it can be sent along with a bug report.</summary>
public static class SnapshotStore
{
    /// <returns>The file it wrote.</returns>
    public static string Save(string json, string kind)
    {
        Directory.CreateDirectory(AppPaths.SnapshotFolder);
        var path = Path.Combine(AppPaths.SnapshotFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.json");
        File.WriteAllText(path, Snapshots.Anonymize(json));
        return path;
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Couldn't open {target}", ex);
        }
    }

    public static void ShowInExplorer(string file)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{file}\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Couldn't show {file}", ex);
        }
    }
}

/// <summary>Asks GitHub for the latest release once at startup.</summary>
public static class UpdateChecker
{
    private const string LatestRelease = "https://api.github.com/repos/Thovenaar/LeagueClanker/releases/latest";

    /// <returns>The newer version and its page, or null when you're up to date, on a dev build, or offline.</returns>
    public static async Task<(string Version, string Url)?> CheckAsync(string currentVersion, CancellationToken ct)
    {
        if (!Version.TryParse(currentVersion, out var current) || current.Major == 0 && current.Minor == 0)
            return null; // "0.0.0-dev" local builds

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LeagueClanker", currentVersion));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestRelease, ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v');
            var url = doc.RootElement.GetProperty("html_url").GetString();
            return Version.TryParse(tag, out var latest) && latest > current && url is not null ? (tag!, url) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Log.Error("Update check", ex);
            return null;
        }
    }
}

/// <summary>Stands in for the client in demos: says what Apply would write.</summary>
public sealed class DemoWriter : IChampSelectWriter
{
    public Task<ApplyResult> WriteRunesAsync(RunePage page, string pageName, CancellationToken ct) =>
        Task.FromResult(new ApplyResult(true, $"Demo: would overwrite your current rune page as \"{pageName}\"."));

    public Task<ApplyResult> WriteSpellsAsync(int first, int second, CancellationToken ct) =>
        Task.FromResult(new ApplyResult(true, "Demo: would set your summoner spells."));

    public Task<ApplyResult> WriteItemSetAsync(ItemSetDefinition set, CancellationToken ct) =>
        Task.FromResult(new ApplyResult(true, $"Demo: would add the item set \"{set.Title}\" with {set.Blocks.Count} blocks."));
}

/// <summary>Writes through whichever client connection is current; the connection changes when the client restarts.</summary>
public sealed class ClientWriter(Func<IChampSelectWriter?> client) : IChampSelectWriter
{
    private static readonly ApplyResult NoClient = new(false, "The League client isn't running.");

    public Task<ApplyResult> WriteRunesAsync(RunePage page, string pageName, CancellationToken ct) =>
        client()?.WriteRunesAsync(page, pageName, ct) ?? Task.FromResult(NoClient);

    public Task<ApplyResult> WriteSpellsAsync(int first, int second, CancellationToken ct) =>
        client()?.WriteSpellsAsync(first, second, ct) ?? Task.FromResult(NoClient);

    public Task<ApplyResult> WriteItemSetAsync(ItemSetDefinition set, CancellationToken ct) =>
        client()?.WriteItemSetAsync(set, ct) ?? Task.FromResult(NoClient);
}
