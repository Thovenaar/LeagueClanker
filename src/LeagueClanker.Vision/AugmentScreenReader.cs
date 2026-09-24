using System.Runtime.InteropServices.WindowsRuntime;
using LeagueClanker.Core.Augments;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace LeagueClanker.Vision;

public sealed record ScanResult(IReadOnlyList<DetectedAugment> Offer, IReadOnlyList<TextLine> Lines, string? Problem = null);

/// <summary>
/// Reads the augment offer off the screen: capture the game, keep the middle (where the cards are, away from
/// chat and the HUD), run Windows' built-in OCR, and match the text against the known augment names.
/// </summary>
public sealed class AugmentScreenReader
{
    // The cards sit in the middle of the screen. Leaving out the edges skips chat, the scoreboard and the shop.
    private const double CropLeft = 0.12, CropTop = 0.10, CropRight = 0.88, CropBottom = 0.80;

    private readonly AugmentCatalog _catalog;
    private readonly OcrEngine? _ocr;

    /// <param name="locale">The League client's language, like "de_DE". Cards are read in that language when Windows can.</param>
    public AugmentScreenReader(AugmentCatalog catalog, string? locale = null)
    {
        _catalog = catalog;
        var english = new Language("en-US");
        var tag = locale?.Replace('_', '-');
        var wanted = AugmentTranslations.IsEnglish(locale) || !Language.IsWellFormed(tag) ? english : new Language(tag);
        if (OcrEngine.IsLanguageSupported(wanted))
            _ocr = OcrEngine.TryCreateFromLanguage(wanted);
        else
        {
            // English still reads names that stay the same in other languages ("ADAPt", "Goliath").
            MissingLanguage = wanted.DisplayName;
            _ocr = OcrEngine.IsLanguageSupported(english) ? OcrEngine.TryCreateFromLanguage(english) : OcrEngine.TryCreateFromUserProfileLanguages();
        }
    }

    public string OcrLanguage => _ocr?.RecognizerLanguage.DisplayName ?? "none";

    /// <summary>The client's language when Windows has no text recognition for it, like "German (Germany)".</summary>
    public string? MissingLanguage { get; }

    /// <summary>Scans the League window. <paramref name="exclude"/> is blanked first (our own window).</summary>
    public async Task<ScanResult> ScanScreenAsync(PixelRect? exclude = null)
    {
        if (ScreenCapture.FindLeagueWindow() is not { } game)
            return new ScanResult([], [], "League isn't running (or it's minimized).");

        var image = ScreenCapture.Capture(game);
        if (exclude is { } own)
            image.Blank(own);
        return await ScanAsync(image);
    }

    /// <summary>Scans a saved screenshot, for testing and demos.</summary>
    public async Task<ScanResult> ScanFileAsync(string path) => await ScanAsync(await LoadAsync(path));

    public async Task<ScanResult> ScanAsync(ScreenImage image)
    {
        if (_ocr is null)
            return new ScanResult([], [], "Windows text recognition isn't available. Add an OCR language in Windows settings.");

        var area = image.Crop(CropLeft, CropTop, CropRight, CropBottom);
        var scale = 1.0;
        while (Math.Max(area.Width, area.Height) > OcrEngine.MaxImageDimension)
        {
            area = area.HalfSize();
            scale *= 2;
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(area.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, area.Width, area.Height, BitmapAlphaMode.Ignore);
        var result = await _ocr.RecognizeAsync(bitmap);

        var lines = result.Lines
            .Where(l => l.Words.Count > 0)
            .Select(l =>
            {
                var left = l.Words.Min(w => w.BoundingRect.X);
                var top = l.Words.Min(w => w.BoundingRect.Y);
                var right = l.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                var bottom = l.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                return new TextLine(l.Text, left * scale, top * scale, (right - left) * scale, (bottom - top) * scale);
            })
            .ToList();

        var problem = MissingLanguage is null ? null
            : $"Windows can't read {MissingLanguage} text. Add the language in Windows settings (Time & language > Language & region) to read cards.";
        return new ScanResult(AugmentTextMatcher.FindOffer(lines, _catalog), lines, problem);
    }

    private static async Task<ScreenImage> LoadAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return new ScreenImage(pixels, bitmap.PixelWidth, bitmap.PixelHeight, new PixelRect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
    }
}
