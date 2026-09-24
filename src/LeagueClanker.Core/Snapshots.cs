using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeagueClanker.Core;

/// <summary>A data source that can hand over the raw JSON it last read, for saving a snapshot.</summary>
public interface ISnapshotSource
{
    /// <summary>The last response as the game or client sent it, or null when there's nothing yet.</summary>
    string? SnapshotJson { get; }
}

public static class Snapshots
{
    // Fields that identify players. Everything else (champions, items, roles) is what makes a snapshot useful.
    private static readonly HashSet<string> NameFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "riotId", "riotIdGameName", "riotIdTagLine", "summonerName", "gameName", "tagLine", "puuid", "summonerId", "accountId",
        "displayName", "internalName", "obfuscatedPuuid", "obfuscatedSummonerId", "nameVisibilityType",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Replaces player names and ids with "Player1", "Player2", ... The same name always gets the same replacement, so
    /// the snapshot still knows which player is you. Summoner spell and champion names aren't touched.
    /// </summary>
    public static string Anonymize(string json)
    {
        var root = JsonNode.Parse(json);
        if (root is null)
            return json;

        var replacements = new Dictionary<string, string>();
        // "Name#TAG" and the bare "Name" map to the same player, so the name part decides.
        string Replace(string value)
        {
            if (value.Length == 0)
                return value;
            var parts = value.Split('#', 2);
            if (!replacements.TryGetValue(parts[0], out var player))
                replacements[parts[0]] = player = $"Player{replacements.Count + 1}";
            return parts.Length == 2 ? $"{player}#LC" : player;
        }

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (NameFields.Contains(key) && value is JsonValue v)
                        {
                            if (v.TryGetValue<string>(out var text))
                                obj[key] = key.Equals("riotIdTagLine", StringComparison.OrdinalIgnoreCase) || key.Equals("tagLine", StringComparison.OrdinalIgnoreCase)
                                    ? "LC"
                                    : Replace(text);
                            else if (v.TryGetValue<long>(out _))
                                obj[key] = 0;
                        }
                        else
                        {
                            Walk(value);
                        }
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array)
                        Walk(item);
                    break;
            }
        }

        Walk(root);
        return root.ToJsonString(Indented);
    }
}
