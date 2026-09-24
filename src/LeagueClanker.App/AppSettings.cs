using System.IO;
using System.Text.Json;
using LeagueClanker.Core.Runes;

namespace LeagueClanker.App;

/// <summary>Choices that outlive a session, stored in %LOCALAPPDATA%\LeagueClanker\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueClanker", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public RuneSourceKind RuneSource { get; set; } = RuneSourceKind.StatsSite;

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(); // A broken settings file shouldn't stop the app.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Couldn't save settings: {ex.Message}");
        }
    }
}
