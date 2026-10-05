namespace LeagueClanker.Core.Augments;

/// <summary>
/// Which offered card you took, from several looks at the HUD after the offer closes plus the card you clicked.
/// Each look that recognized a card is a vote. The click counts half: it decides when the HUD can't tell, and breaks
/// a tie, but loses to a card the HUD saw.
/// </summary>
public sealed class PickVotes
{
    private const double ClickWeight = 0.5;

    /// <summary>A lead this big settles it without waiting for more looks: two reads that agree, or three against one.</summary>
    private const double SureLead = 2;

    private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);
    private string? _click;

    public int Reads => _reads.Values.Sum();

    public void Read(string card) => _reads[card] = _reads.GetValueOrDefault(card) + 1;

    public void Click(string? card) => _click = card;

    public void Clear()
    {
        _reads.Clear();
        _click = null;
    }

    /// <summary>The card most votes point to, or null with no votes or a tie.</summary>
    public string? Best => Ranked() is [var first, ..] rest && (rest.Length == 1 || first.Votes > rest[1].Votes) ? first.Card : null;

    /// <summary>The leading card once it's far enough ahead that more looks wouldn't change it.</summary>
    public string? Sure => Ranked() is [var first, ..] rest && first.Votes - (rest.Length > 1 ? rest[1].Votes : 0) >= SureLead ? first.Card : null;

    /// <summary>The HUD saw this card at least once, as opposed to only the click pointing at it.</summary>
    public bool Seen(string card) => _reads.ContainsKey(card);

    private (string Card, double Votes)[] Ranked()
    {
        var votes = _reads.ToDictionary(r => r.Key, r => (double)r.Value);
        if (_click is not null)
            votes[_click] = votes.GetValueOrDefault(_click) + ClickWeight;
        return votes.OrderByDescending(v => v.Value).Select(v => (v.Key, v.Value)).ToArray();
    }
}
