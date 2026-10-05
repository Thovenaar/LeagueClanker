namespace LeagueClanker.Core.Augments;

/// <summary>A card's icons, as Community Dragon has them. A name can have more than one when Riot lists the card twice.</summary>
public sealed record CardIcons(string Name, IReadOnlyList<GrayImage> Icons);

/// <summary>A square on screen where the HUD shows a card you took.</summary>
public readonly record struct HudSlot(int X, int Y, int Size);

/// <summary>What one look at the HUD says. <see cref="Card"/> is null when it can't tell, and <see cref="Why"/> says why.</summary>
public sealed record HudRead(string? Card, double Score, double RunnerUp, int Filled, double HudScale, string Why);

/// <summary>
/// Reads which card you took from the ARAM: Mayhem HUD. The cards you took sit in five slots left of your champion's
/// portrait, three on top and two below, and fill in that order. So the last filled slot holds your newest card, and
/// comparing it with the three cards you were offered tells which one you took. The icons are gray shapes the game
/// tints by tier, so they're compared by brightness only. Riot reuses one image for several cards (It's Killing Time,
/// Surge Field and Final Form share one), which is why only the offered cards are compared, and why two offered cards
/// with the same image can't be told apart.
/// </summary>
public static class AugmentHud
{
    // Measured on a 2560x1440 screenshot: 53 pixel icons, 61 pixels apart (62 between rows), the first one 590 pixels
    // left of the middle and 129 above the bottom. The HUD grows with the screen's height and with League's HUD scale.
    private const double Reference = 1440, FirstLeft = -590, FirstTop = 129, Step = 61, RowStep = 62, IconSize = 53;
    private static readonly double[] HudScales = [1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.65, 0.6, 1.05, 1.1];

    public const int SlotCount = 5;

    /// <summary>
    /// A match this good means the icon is there. The three icons in the 1440p sample scored 0.94 to 0.96, and a
    /// different card with the same figure in another frame (Escape Plan for Leg Day) 0.84. That matters when the
    /// offer was misread, so the card you took isn't among the ones compared.
    /// </summary>
    public const double MinScore = 0.88;

    /// <summary>The newest icon has to beat the next offered card by this much. Look-alike frames score within 0.03.</summary>
    public const double MinMargin = 0.04;

    // Empty slots are flat black (spread 0); a filled one spread over 20 even while an offer dims the screen.
    private const double MinSpread = 6;

    // The portrait's ring touches the third slot's right edge, so that slot is compared on its left 85%.
    private const double ThirdSlotWidth = 0.85;

    // Two icons this alike are the same image. Cards sharing an image score within this of each other in a slot.
    private const double SameImage = 0.97;
    private const double SameScore = 0.01;

    public static IReadOnlyList<HudSlot> Slots(int width, int height, double hudScale = 1.0)
    {
        var unit = height / Reference * hudScale;
        var size = (int)Math.Round(IconSize * unit);
        return Enumerable.Range(0, SlotCount)
            .Select(i => new HudSlot(
                (int)Math.Round(width / 2.0 + (FirstLeft + i % 3 * Step) * unit),
                (int)Math.Round(height - (FirstTop - i / 3 * RowStep) * unit),
                size))
            .ToList();
    }

    /// <summary>
    /// Which of the offered cards is in the newest filled slot, trying each HUD scale. With <paramref name="before"/>,
    /// a capture from while the offer was up, the newest slot has to have been empty then. Otherwise hiding the offer
    /// without picking would leave an older card there to be mistaken for a look-alike offered one. With
    /// <paramref name="everyCard"/>, a card that wasn't offered mustn't match better: when the offer was misread, the
    /// card you took isn't among the offered ones, and a look-alike can still score 0.92 (Overextender for Pinball).
    /// </summary>
    public static HudRead Read(GrayImage screen, IReadOnlyList<CardIcons> offered, GrayImage? before = null, IReadOnlyList<CardIcons>? everyCard = null)
    {
        var cards = offered.Where(c => c.Icons.Count > 0).ToList();
        if (cards.Count == 0)
            return new HudRead(null, 0, 0, 0, 1, "no icons for the offered cards");

        HudRead? best = null;
        Placement where = default;
        foreach (var scale in HudScales)
        {
            var slots = Slots(screen.Width, screen.Height, scale);
            if (slots.Any(s => s.X < 3 || s.Y < 3 || s.X + s.Size + 3 > screen.Width || s.Y + s.Size + 3 > screen.Height))
                continue;
            var filled = Filled(screen, slots);
            if (filled == 0)
                continue;
            if (before is not null && before.Width == screen.Width && before.Height == screen.Height && Filled(before, slots) >= filled)
            {
                best ??= new HudRead(null, 0, 0, filled, scale, "no new card in the HUD yet");
                continue;
            }
            var newest = filled - 1;
            var scores = cards.Select(c => (Card: c, Place: c.Icons.Select(icon => Place(screen, slots[newest], newest, icon)).MaxBy(p => p.Score)))
                .Select(s => (s.Card, s.Place, s.Place.Score)).OrderByDescending(s => s.Score).ToList();
            var top = scores[0];
            if (best is not null && top.Score <= best.Score)
                continue;

            where = top.Place;
            var twin = scores.Skip(1).FirstOrDefault(s => SameIcon(s.Card, top.Card));
            var runnerUp = scores.Skip(1).Where(s => s.Card != twin.Card).Select(s => s.Score).DefaultIfEmpty(0).Max();
            best = top.Score < MinScore ? new HudRead(null, top.Score, runnerUp, filled, scale, $"slot {filled} looks like none of the offered cards")
                : twin.Card is not null ? new HudRead(null, top.Score, twin.Score, filled, scale, $"{top.Card.Name} and {twin.Card.Name} have the same icon")
                : top.Score - runnerUp < MinMargin ? new HudRead(null, top.Score, runnerUp, filled, scale, $"slot {filled} looks like {top.Card.Name} and {scores[1].Card.Name}")
                : new HudRead(top.Card.Name, top.Score, runnerUp, filled, scale, $"slot {filled} is {top.Card.Name}");
        }

        // Every other card is only tried where the offered one fit best, which keeps a check of 250 icons quick.
        if (best is { Card: not null } && everyCard is not null)
        {
            var newest = best.Filled - 1;
            var slot = Slots(screen.Width, screen.Height, best.HudScale)[newest];
            var names = offered.Select(c => c.Name).ToHashSet();
            var better = everyCard.Where(c => !names.Contains(c.Name) && c.Icons.Count > 0)
                .Select(c => (c.Name, Score: c.Icons.Max(icon => Place(screen, slot, newest, icon, near: where).Score)))
                .Where(c => c.Score > best.Score + SameScore)
                .OrderByDescending(c => c.Score).FirstOrDefault();
            if (better.Name is not null)
                return best with { Card = null, RunnerUp = better.Score, Why = $"slot {best.Filled} looks more like {better.Name}, which wasn't offered" };
        }
        return best ?? new HudRead(null, 0, 0, 0, 1, "no filled slots");
    }

    /// <summary>How many slots hold a card. They fill in order, so this counts up to the first empty one.</summary>
    public static int Filled(GrayImage screen, IReadOnlyList<HudSlot> slots) =>
        slots.TakeWhile((s, i) => Spread(screen, s, i) >= MinSpread).Count();

    /// <summary>How well an icon matches a slot, from -1 to 1, trying sizes and positions a pixel or two off.</summary>
    public static double Match(GrayImage screen, HudSlot slot, int index, GrayImage icon) => Place(screen, slot, index, icon).Score;

    private readonly record struct Placement(double Score, int Size, int X, int Y);

    // The size and position where an icon fits a slot best. Near an earlier placement, only a pixel around it is tried.
    private static Placement Place(GrayImage screen, HudSlot slot, int index, GrayImage icon, Placement? near = null)
    {
        var best = new Placement(-1, slot.Size, slot.X, slot.Y);
        var shift = near is null ? Math.Max(2, (int)Math.Round(slot.Size * 2.0 / IconSize)) : 1;
        var sizes = near is { } n ? [n.Size - 1, n.Size, n.Size + 1] : new[] { 0.96, 0.98, 1.0, 1.02, 1.04 }.Select(f => (int)Math.Round(slot.Size * f)).Distinct().ToArray();
        foreach (var size in sizes)
        {
            var scaled = icon.Square(size);
            var columns = index == 2 ? (int)(size * ThirdSlotWidth) : size;
            var left = near?.X ?? slot.X + (slot.Size - size) / 2;
            var top = near?.Y ?? slot.Y + (slot.Size - size) / 2;
            for (var dy = -shift; dy <= shift; dy++)
                for (var dx = -shift; dx <= shift; dx++)
                    if (Correlation(screen, left + dx, top + dy, scaled, columns) is var score && score > best.Score)
                        best = new Placement(score, size, left + dx, top + dy);
        }
        return best;
    }

    // Normalized cross-correlation: 1 when the two have the same shape, whatever the brightness and tint.
    private static double Correlation(GrayImage screen, int left, int top, GrayImage icon, int columns)
    {
        if (left < 0 || top < 0 || left + columns > screen.Width || top + icon.Height > screen.Height)
            return -1;
        double sumA = 0, sumB = 0, sumAa = 0, sumBb = 0, sumAb = 0;
        var n = columns * icon.Height;
        for (var y = 0; y < icon.Height; y++)
        {
            var row = (top + y) * screen.Width + left;
            for (var x = 0; x < columns; x++)
            {
                double a = screen.Pixels[row + x], b = icon.Pixels[y * icon.Width + x];
                sumA += a; sumB += b; sumAa += a * a; sumBb += b * b; sumAb += a * b;
            }
        }
        var covariance = sumAb - sumA * sumB / n;
        var spread = Math.Sqrt((sumAa - sumA * sumA / n) * (sumBb - sumB * sumB / n));
        return spread > 1e-6 ? covariance / spread : 0;
    }

    // The standard deviation inside a slot, away from its border.
    private static double Spread(GrayImage screen, HudSlot slot, int index)
    {
        var inset = slot.Size / 8;
        var right = index == 2 ? slot.X + (int)(slot.Size * ThirdSlotWidth) - inset : slot.X + slot.Size - inset;
        double sum = 0, squares = 0;
        var n = 0;
        for (var y = slot.Y + inset; y < slot.Y + slot.Size - inset; y++)
            for (var x = slot.X + inset; x < right; x++)
            {
                double v = screen[x, y];
                sum += v; squares += v * v; n++;
            }
        return n == 0 ? 0 : Math.Sqrt(Math.Max(0, squares / n - sum * sum / n / n));
    }

    private static bool SameIcon(CardIcons a, CardIcons b) =>
        a.Icons.Any(x => b.Icons.Any(y => Correlation(x.Square(32), 0, 0, y.Square(32), 32) >= SameImage));
}
