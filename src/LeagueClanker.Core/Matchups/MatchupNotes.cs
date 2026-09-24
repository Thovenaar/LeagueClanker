using System.Text.Json;
using LeagueClanker.Core.StaticData;

namespace LeagueClanker.Core.Matchups;

/// <summary>
/// Your own notes per opponent ("Darius: don't trade at level 2"), shown whenever you face that champion again.
/// Stored as JSON, keyed by the opponent's champion id.
/// </summary>
public sealed class MatchupNotes(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private Dictionary<string, string>? _notes;

    public string Get(ChampionInfo opponent) => Load().GetValueOrDefault(opponent.Id) ?? "";

    /// <summary>Saves the note right away. An empty note removes it.</summary>
    public void Set(ChampionInfo opponent, string text)
    {
        var notes = Load();
        text = text.Trim();
        var changed = text.Length == 0 ? notes.Remove(opponent.Id) : notes.GetValueOrDefault(opponent.Id) != text;
        if (!changed)
            return;
        if (text.Length > 0)
            notes[opponent.Id] = text;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(notes, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving matchup notes", ex);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_notes is not null)
            return _notes;
        try
        {
            _notes = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Reading matchup notes", ex);
            _notes = [];
        }
        return _notes;
    }
}
