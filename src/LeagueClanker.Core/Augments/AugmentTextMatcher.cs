namespace LeagueClanker.Core.Augments;

/// <summary>A line of text recognized on screen, with its position in pixels.</summary>
public sealed record TextLine(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public double Bottom => Y + Height;
}

/// <param name="X">The middle of the card's name, in pixels of the image that was read.</param>
/// <param name="Y">The middle of the card's name, in pixels of the image that was read.</param>
public sealed record DetectedAugment(AugmentInfo Augment, double Confidence, double X, double Y = 0);

/// <summary>An offered card's name on screen, in screen pixels.</summary>
public sealed record CardSpot(AugmentInfo Augment, double X, double Y);

/// <summary>
/// Where the offered cards are on screen, to tell which card a click landed on. Measured on a 1440p offer: a card
/// reaches from 23% of the screen height above its name to 26% below it, and the reroll buttons start right under
/// that, so they never count. Sideways a card fills most of the gap to the next one.
/// </summary>
public sealed record OfferLayout(IReadOnlyList<CardSpot> Cards, double ScreenHeight)
{
    private const double Above = 0.22, Below = 0.24, Sideways = 0.42;

    // The gap between cards when only one name was read: 19% of a 16:9 screen's width.
    private const double LoneCardGap = 0.34;

    public AugmentInfo? CardAt(double x, double y)
    {
        var gap = Cards.Count > 1
            ? Cards.Zip(Cards.Skip(1)).Select(p => Math.Abs(p.Second.X - p.First.X)).Min()
            : ScreenHeight * LoneCardGap;
        return Cards.FirstOrDefault(c =>
            Math.Abs(x - c.X) <= gap * Sideways && y >= c.Y - ScreenHeight * Above && y <= c.Y + ScreenHeight * Below)?.Augment;
    }
}

/// <summary>
/// Finds the offered augment cards in text read from the screen. OCR gets letters wrong and wraps long
/// names over two lines, so names are matched by edit distance, including pairs of stacked lines.
/// </summary>
public static class AugmentTextMatcher
{
    public const int OfferSize = 3;
    private const double MinSimilarity = 0.8;
    private const int ShortName = 5; // "Deft", "Stats!": a one-letter slip turns them into other words, so they must match exactly

    public static IReadOnlyList<DetectedAugment> FindOffer(IReadOnlyList<TextLine> lines, AugmentCatalog catalog)
    {
        // English names always, plus the client's language when it isn't English. A quest card shows its name without
        // the "Quest:" the wiki puts in front: "Wooglet's Witchcap" on screen is "Quest: Wooglet's Witchcap".
        var augments = catalog.Offerable
            .SelectMany(a => a.LocalNames.Prepend(a.Name).SelectMany(name => a.IsQuest && name.IndexOf(':') is > 0 and var colon ? [name, name[(colon + 1)..]] : new[] { name })
                .Select(name => (Augment: a, Name: name.Trim(), Key: AugmentCatalog.Key(name))))
            .Where(a => a.Key.Length > 0)
            .ToList();
        var matches = new Dictionary<AugmentInfo, DetectedAugment>();

        foreach (var (text, x, y) in Candidates(lines))
        {
            var key = AugmentCatalog.Key(text);
            if (key.Length < 2 || (key.Length < 3 && key.All(char.IsAscii)))
                continue; // Chinese and Korean names can be two characters

            // Each piece of text names at most one card: the closest one.
            var best = augments
                .Select(a => (a.Augment, a.Name, Score: Similarity(a.Key, key)))
                .Where(m => m.Score >= (m.Name.Length <= ShortName ? 1.0 : MinSimilarity))
                .OrderByDescending(m => m.Score)
                .ThenByDescending(m => m.Augment.Name.Length)
                .FirstOrDefault();
            if (best.Augment is null)
                continue;

            if (!matches.TryGetValue(best.Augment, out var existing) || existing.Confidence < best.Score)
                matches[best.Augment] = new DetectedAugment(best.Augment, best.Score, x, y);
        }

        // All cards in one offer share a tier; stray matches elsewhere on screen usually don't.
        var offer = matches.Values
            .GroupBy(d => d.Augment.Tier)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Sum(d => d.Confidence))
            .FirstOrDefault();

        if (offer is null)
            return [];

        var cards = offer.OrderByDescending(d => d.Confidence).Take(OfferSize).ToList();
        if (cards.Count < OfferSize && offer.Key < AugmentTier.Prismatic)
        {
            // A golden reroll turns one card into a card of the next tier.
            var golden = matches.Values.Where(d => d.Augment.Tier == offer.Key + 1).MaxBy(d => d.Confidence);
            if (golden is not null)
                cards.Add(golden);
        }
        return cards.OrderBy(d => d.X).ToList();
    }

    /// <summary>Every line, plus each line joined with the line right below it (long names wrap).</summary>
    private static IEnumerable<(string Text, double X, double Y)> Candidates(IReadOnlyList<TextLine> lines)
    {
        foreach (var line in lines)
            yield return (line.Text, line.CenterX, line.CenterY);

        foreach (var upper in lines)
        {
            foreach (var lower in lines)
            {
                var gap = lower.Y - upper.Bottom;
                var aligned = Math.Abs(lower.CenterX - upper.CenterX) < Math.Max(upper.Width, lower.Width) * 0.6;
                if (lower != upper && gap >= -upper.Height * 0.3 && gap < upper.Height * 1.2 && aligned)
                    yield return ($"{upper.Text} {lower.Text}", (upper.CenterX + lower.CenterX) / 2, (upper.CenterY + lower.CenterY) / 2);
            }
        }
    }

    /// <summary>1 for identical, falling with each edit. Text that contains a long name counts as a near match.</summary>
    internal static double Similarity(string name, string text)
    {
        if (name == text)
            return 1.0;
        if (name.Length > ShortName && text.Contains(name, StringComparison.Ordinal))
            return 0.9;
        return 1.0 - (double)Levenshtein(name, text) / Math.Max(name.Length, text.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
