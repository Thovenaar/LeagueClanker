using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LeagueClanker.Core.LiveClient;

namespace LeagueClanker.Core.LeagueClient;

// Shapes of the League client's local API (often called the LCU API). Only the fields LeagueClanker uses are mapped.

public sealed class ChampSelectSession
{
    public int LocalPlayerCellId { get; init; }
    public List<ChampSelectPlayer> MyTeam { get; init; } = [];
    public List<ChampSelectPlayer> TheirTeam { get; init; } = [];
}

public sealed class ChampSelectPlayer
{
    public int CellId { get; init; }

    /// <summary>The picked champion's numeric id, 0 before a pick. Enemy picks stay 0 in blind pick.</summary>
    public int ChampionId { get; init; }

    /// <summary>The champion someone hovers before their turn.</summary>
    public int ChampionPickIntent { get; init; }

    /// <summary>"top", "jungle", "middle", "bottom", "utility", or "" when roles aren't assigned (blind pick, ARAM).</summary>
    public string? AssignedPosition { get; init; }
}

public sealed class GameflowSession
{
    public GameflowGameData? GameData { get; init; }
}

public sealed class GameflowGameData
{
    public GameflowQueue? Queue { get; init; }
}

public sealed class GameflowQueue
{
    public int Id { get; init; }
    public string? GameMode { get; init; }
    public int MapId { get; init; }
}

public sealed class Lobby
{
    public LobbyMember? LocalMember { get; init; }
}

public sealed class LobbyMember
{
    /// <summary>The role you queued for: "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY", "FILL" or "UNSELECTED".</summary>
    public string? FirstPositionPreference { get; init; }
}

public sealed class PerkPage
{
    public int Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>False for the client's preset and recommended pages.</summary>
    public bool IsEditable { get; init; }

    public int PrimaryStyleId { get; init; }
    public int SubStyleId { get; init; }
    public List<int> SelectedPerkIds { get; init; } = [];
}

public sealed record PerkPageRequest(string Name, int PrimaryStyleId, int SubStyleId, IReadOnlyList<int> SelectedPerkIds, bool Current = true);

/// <summary>The rune page calls, behind an interface so the overwrite logic can be tested without a client.</summary>
public interface IRunePageStore
{
    Task<PerkPage?> GetCurrentPageAsync(CancellationToken ct);
    Task<IReadOnlyList<PerkPage>> GetPagesAsync(CancellationToken ct);
    Task<int> GetOwnedPageCountAsync(CancellationToken ct);
    Task UpdatePageAsync(int id, PerkPageRequest page, CancellationToken ct);
    Task<PerkPage> CreatePageAsync(PerkPageRequest page, CancellationToken ct);
    Task SetCurrentPageAsync(int id, CancellationToken ct);
}

/// <summary>What the League client knows before the game starts. Null fields mean "not available right now".</summary>
public sealed record ClientSnapshot(ChampSelectSession? ChampSelect, GameflowSession? Gameflow, Lobby? Lobby);

public interface IClientSource
{
    /// <summary>Null when the client isn't running or you aren't in champ select.</summary>
    Task<ClientSnapshot?> TryGetChampSelectAsync(CancellationToken ct);
}

/// <summary>
/// The League client's local API. The client writes its port and a password to a "lockfile" in the install folder
/// while it runs. It's the same API the client's own interface uses, and what rune importers like Porofessor and Blitz use.
/// Riot doesn't document or support it, so a client update can change it.
/// </summary>
public sealed class LeagueClientApi : IClientSource, IRunePageStore, IDisposable
{
    private readonly HttpClient _http;

    public LeagueClientApi(Lockfile lockfile) : this(lockfile.Port, lockfile.Password)
    {
    }

    private LeagueClientApi(int port, string password)
    {
        var handler = new HttpClientHandler
        {
            // The client serves a self-signed Riot certificate. Only trust it on loopback.
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { IsLoopback: true },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(4) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{password}")));
    }

    /// <summary>Connects to the running League client, or returns null when it isn't running.</summary>
    public static LeagueClientApi? TryConnect() => FindLockfile() is { } lockfile ? new LeagueClientApi(lockfile) : null;

    /// <summary>The running client's port and password. They change every time the client starts.</summary>
    public static Lockfile? FindLockfile()
    {
        foreach (var path in LockfileCandidates())
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (Lockfile.Parse(reader.ReadToEnd()) is { } lockfile)
                    return lockfile;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    // The lockfile sits next to the running client. The default install folder covers clients whose process we can't inspect.
    private static List<string> LockfileCandidates()
    {
        var paths = new List<string>();
        foreach (var name in new[] { "LeagueClientUx", "LeagueClient" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (Path.GetDirectoryName(process.MainModule?.FileName) is { } folder)
                        paths.Add(Path.Combine(folder, "lockfile"));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Access denied or the process just exited.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        paths.Add(@"C:\Riot Games\League of Legends\lockfile");
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
    }

    public async Task<ClientSnapshot?> TryGetChampSelectAsync(CancellationToken ct)
    {
        var session = await GetOrNullAsync<ChampSelectSession>("lol-champ-select/v1/session", ct);
        if (session is null)
            return null;

        var gameflow = await GetOrNullAsync<GameflowSession>("lol-gameflow/v1/session", ct);
        var lobby = await GetOrNullAsync<Lobby>("lol-lobby/v2/lobby", ct);
        return new ClientSnapshot(session, gameflow, lobby);
    }

    public Task<PerkPage?> GetCurrentPageAsync(CancellationToken ct) => GetOrNullAsync<PerkPage>("lol-perks/v1/currentpage", ct);

    public async Task<IReadOnlyList<PerkPage>> GetPagesAsync(CancellationToken ct) =>
        await GetOrNullAsync<List<PerkPage>>("lol-perks/v1/pages", ct) ?? [];

    public async Task<int> GetOwnedPageCountAsync(CancellationToken ct)
    {
        using var doc = await GetOrNullAsync<JsonDocument>("lol-perks/v1/inventory", ct);
        return doc?.RootElement.TryGetProperty("ownedPageCount", out var count) == true ? count.GetInt32() : 0;
    }

    public async Task UpdatePageAsync(int id, PerkPageRequest page, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync($"lol-perks/v1/pages/{id}", page, Json.Options, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<PerkPage> CreatePageAsync(PerkPageRequest page, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("lol-perks/v1/pages", page, Json.Options, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<PerkPage>(Json.Options, ct) ?? throw new InvalidOperationException("The client returned no page.");
    }

    public async Task SetCurrentPageAsync(int id, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("lol-perks/v1/currentpage", id, Json.Options, ct);
        await EnsureSuccessAsync(response, ct);
    }

    // 404 means "not in that state right now" (no champ select, no lobby); a closed client means null too.
    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<T>(Json.Options, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    // The client explains rejected pages in its response body ("invalid perk selection", ...).
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"The League client refused ({(int)response.StatusCode}): {body}", null, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>"LeagueClient:&lt;pid&gt;:&lt;port&gt;:&lt;password&gt;:https"</summary>
public sealed record Lockfile(int Port, string Password)
{
    public static Lockfile? Parse(string content)
    {
        var parts = content.Trim().Split(':');
        return parts.Length >= 5 && int.TryParse(parts[2], out var port) && parts[3].Length > 0 ? new Lockfile(port, parts[3]) : null;
    }
}

/// <summary>A saved <see cref="ClientSnapshot"/> JSON file, for demos and tests without a client.</summary>
public sealed class FileClientSource(string path) : IClientSource
{
    public async Task<ClientSnapshot?> TryGetChampSelectAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ClientSnapshot>(stream, Json.Options, ct);
    }
}
