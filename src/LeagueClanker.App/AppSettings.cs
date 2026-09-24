using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LeagueClanker.Core;
using LeagueClanker.Core.Runes;

namespace LeagueClanker.App;

/// <summary>Choices that outlive a session, stored in %LOCALAPPDATA%\LeagueClanker\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataFolder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Where runes, spells, skill order and starting items come from.</summary>
    public RuneSourceKind RuneSource { get; set; } = RuneSourceKind.StatsSite;

    public bool ApplySpells { get; set; } = true;
    public bool ApplyItemSet { get; set; } = true;
    public bool AutoApply { get; set; }
    public bool PlaySounds { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't read settings", ex);
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
            Log.Error("Couldn't save settings", ex);
        }
    }
}

/// <summary>The settings panel and the champ select source chips both bind here. Every change is saved right away.</summary>
public sealed class SettingsViewModel(AppSettings settings) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public RuneSourceKind RuneSource
    {
        get => settings.RuneSource;
        set
        {
            if (Update(settings.RuneSource, value, v => settings.RuneSource = v))
                Raise(nameof(UseStatsSite), nameof(UseOwnRules));
        }
    }

    // Radio buttons bind to these; a radio being unchecked sets false, which is ignored.
    public bool UseStatsSite { get => RuneSource == RuneSourceKind.StatsSite; set { if (value) RuneSource = RuneSourceKind.StatsSite; } }
    public bool UseOwnRules { get => RuneSource == RuneSourceKind.OwnRules; set { if (value) RuneSource = RuneSourceKind.OwnRules; } }

    public bool ApplySpells { get => settings.ApplySpells; set => Update(settings.ApplySpells, value, v => settings.ApplySpells = v); }
    public bool ApplyItemSet { get => settings.ApplyItemSet; set => Update(settings.ApplyItemSet, value, v => settings.ApplyItemSet = v); }
    public bool AutoApply { get => settings.AutoApply; set => Update(settings.AutoApply, value, v => settings.AutoApply = v); }
    public bool PlaySounds { get => settings.PlaySounds; set => Update(settings.PlaySounds, value, v => settings.PlaySounds = v); }
    public bool CheckForUpdates { get => settings.CheckForUpdates; set => Update(settings.CheckForUpdates, value, v => settings.CheckForUpdates = v); }

    private bool Update<T>(T current, T value, Action<T> store, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return false;
        store(value);
        settings.Save();
        Raise(name!);
        return true;
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public static class AppPaths
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueClanker");

    public static string LogFile { get; } = Path.Combine(DataFolder, "log.txt");

    /// <summary>In Documents, so snapshots are easy to find and send.</summary>
    public static string SnapshotFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LeagueClanker", "snapshots");
}
