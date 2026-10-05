using System.Runtime.InteropServices.WindowsRuntime;
using LeagueClanker.Core.Augments;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LeagueClanker.Vision;

/// <summary>
/// Looks at the Mayhem HUD for the card you took (<see cref="AugmentHud"/>): captures the game window and compares the
/// newest card slot with the offered cards' icons. Only reads the screen, like a screenshot.
/// </summary>
public static class AugmentHudReader
{
    /// <summary>Captures the game and reads the HUD. Null when the game isn't on screen.</summary>
    public static (HudRead Read, ScreenImage Image)? ReadScreen(IReadOnlyList<CardIcons> offered)
    {
        if (ScreenCapture.FindLeagueWindow() is not { } game)
            return null;
        var image = ScreenCapture.Capture(game);
        return (Read(image, offered), image);
    }

    public static HudRead Read(ScreenImage image, IReadOnlyList<CardIcons> offered) => AugmentHud.Read(image.ToGray(), offered);

    /// <summary>A Community Dragon icon as brightness on black, the way the HUD draws it before tinting.</summary>
    public static async Task<GrayImage> DecodeIconAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return GrayImage.FromBgra(pixels, bitmap.PixelWidth, bitmap.PixelHeight, useAlpha: true);
    }

    /// <summary>
    /// Saves the part of the screen with the card slots, from left of them to the item slots. It leaves out chat
    /// and the game world above the HUD, so no player names end up in it.
    /// </summary>
    public static async Task SaveSlotsAsync(ScreenImage image, string path)
    {
        var area = image.Crop(0.2, 0.88, 0.55, 1.0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, []);
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)area.Width, (uint)area.Height, 96, 96, area.Pixels);
        await encoder.FlushAsync();
    }
}
