namespace LeagueClanker.Core.Augments;

/// <summary>A line of text recognized on screen, with its position in pixels.</summary>
public sealed record TextLine(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double Bottom => Y + Height;
}

public sealed record DetectedAugment(AugmentInfo Augment, double Confidence, double X);

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
        var augments = catalog.Offerable.Select(a => (Augment: a, Key: AugmentCatalog.Key(a.Name))).Where(a => a.Key.Length > 0).ToList();
        var matches = new Dictionary<AugmentInfo, DetectedAugment>();

        foreach (var (text, x) in Candidates(lines))
        {
            var key = AugmentCatalog.Key(text);
            if (key.Length < 3)
                continue;

            // Each piece of text names at most one card: the closest one.
            var best = augments
                .Select(a => (a.Augment, Score: Similarity(a.Key, key)))
                .Where(m => m.Score >= (m.Augment.Name.Length <= ShortName ? 1.0 : MinSimilarity))
                .OrderByDescending(m => m.Score)
                .ThenByDescending(m => m.Augment.Name.Length)
                .FirstOrDefault();
            if (best.Augment is null)
                continue;

            if (!matches.TryGetValue(best.Augment, out var existing) || existing.Confidence < best.Score)
                matches[best.Augment] = new DetectedAugment(best.Augment, best.Score, x);
        }

        // All cards in one offer share a tier; stray matches elsewhere on screen usually don't.
        var offer = matches.Values
            .GroupBy(d => d.Augment.Tier)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Sum(d => d.Confidence))
            .FirstOrDefault();

        return offer?.OrderByDescending(d => d.Confidence).Take(OfferSize).OrderBy(d => d.X).ToList() ?? [];
    }

    /// <summary>Every line, plus each line joined with the line right below it (long names wrap).</summary>
    private static IEnumerable<(string Text, double X)> Candidates(IReadOnlyList<TextLine> lines)
    {
        foreach (var line in lines)
            yield return (line.Text, line.CenterX);

        foreach (var upper in lines)
        {
            foreach (var lower in lines)
            {
                var gap = lower.Y - upper.Bottom;
                var aligned = Math.Abs(lower.CenterX - upper.CenterX) < Math.Max(upper.Width, lower.Width) * 0.6;
                if (lower != upper && gap >= -upper.Height * 0.3 && gap < upper.Height * 1.2 && aligned)
                    yield return ($"{upper.Text} {lower.Text}", (upper.CenterX + lower.CenterX) / 2);
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
