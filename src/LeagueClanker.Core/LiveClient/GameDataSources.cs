using System.Net.Http.Json;
using System.Text.Json;

namespace LeagueClanker.Core.LiveClient;

public interface IGameDataSource
{
    /// <summary>Returns the current game state, or null when no game is in progress.</summary>
    Task<AllGameData?> TryGetAsync(CancellationToken ct);
}

/// <summary>Reads the official Live Client Data API that the game exposes on localhost while a match runs.</summary>
public sealed class LiveClientApi : IGameDataSource, IDisposable
{
    public static readonly Uri DefaultBaseAddress = new("https://127.0.0.1:2999/");

    private readonly HttpClient _http;

    public LiveClientApi(Uri? baseAddress = null)
    {
        var handler = new HttpClientHandler
        {
            // The game serves a self-signed Riot certificate. Only trust it on loopback.
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { IsLoopback: true },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = baseAddress ?? DefaultBaseAddress,
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    public async Task<AllGameData?> TryGetAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("liveclientdata/allgamedata", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var data = await response.Content.ReadFromJsonAsync<AllGameData>(Json.Options, ct);
            return IsPlayable(data) ? data : null;
        }
        catch (HttpRequestException)
        {
            return null; // Game not running.
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // Request timed out, e.g. during the loading screen.
        }
        catch (JsonException)
        {
            return null; // Partial payload while the game is loading.
        }
    }

    internal static bool IsPlayable(AllGameData? data) => data?.ActivePlayer is not null && data.AllPlayers.Count > 0;

    public void Dispose() => _http.Dispose();
}

/// <summary>Replays a saved allgamedata JSON file. Used for demo mode and development without a live game.</summary>
public sealed class FileGameDataSource(string path) : IGameDataSource
{
    public async Task<AllGameData?> TryGetAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var data = await JsonSerializer.DeserializeAsync<AllGameData>(stream, Json.Options, ct);
        return LiveClientApi.IsPlayable(data) ? data : null;
    }
}

/// <summary>Replays snapshots in order, moving to the next one every few polls and staying on the last. Used to demo pivots.</summary>
public sealed class SequenceGameDataSource(IReadOnlyList<string> paths, int pollsPerSnapshot = 5) : IGameDataSource
{
    private int _polls;

    public Task<AllGameData?> TryGetAsync(CancellationToken ct)
    {
        var index = Math.Min(_polls++ / pollsPerSnapshot, paths.Count - 1);
        return new FileGameDataSource(paths[index]).TryGetAsync(ct);
    }

    /// <summary>A folder replays every *.json in name order; a file is a single snapshot.</summary>
    public static IGameDataSource FromPath(string path) =>
        Directory.Exists(path)
            ? new SequenceGameDataSource(Directory.GetFiles(path, "*.json").Order().ToList())
            : new FileGameDataSource(path);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
