using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LeagueClanker.Vision;

/// <summary>A screen rectangle in physical pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>A captured image: 32-bit BGRA pixels, top row first. <see cref="Origin"/> is where it sat on screen.</summary>
public sealed class ScreenImage(byte[] pixels, int width, int height, PixelRect origin)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public PixelRect Origin { get; } = origin;

    /// <summary>Paints a screen area black, e.g. our own window so we don't read our own suggestions.</summary>
    public void Blank(PixelRect screenArea)
    {
        var left = Math.Max(0, screenArea.X - Origin.X);
        var top = Math.Max(0, screenArea.Y - Origin.Y);
        var right = Math.Min(Width, screenArea.Right - Origin.X);
        var bottom = Math.Min(Height, screenArea.Bottom - Origin.Y);
        for (var y = top; y < bottom; y++)
            Array.Clear(Pixels, (y * Width + left) * 4, Math.Max(0, right - left) * 4);
    }

    /// <summary>The part between the given fractions of width and height.</summary>
    public ScreenImage Crop(double left, double top, double right, double bottom)
    {
        var x0 = (int)(Width * left);
        var y0 = (int)(Height * top);
        var w = Math.Max(1, (int)(Width * right) - x0);
        var h = Math.Max(1, (int)(Height * bottom) - y0);
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(Pixels, ((y0 + y) * Width + x0) * 4, pixels, y * w * 4, w * 4);
        return new ScreenImage(pixels, w, h, new PixelRect(Origin.X + x0, Origin.Y + y0, w, h));
    }

    /// <summary>Halves both dimensions by averaging 2×2 blocks.</summary>
    public ScreenImage HalfSize()
    {
        var w = Width / 2;
        var h = Height / 2;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var sum = Pixels[((2 * y) * Width + 2 * x) * 4 + c] + Pixels[((2 * y) * Width + 2 * x + 1) * 4 + c]
                        + Pixels[((2 * y + 1) * Width + 2 * x) * 4 + c] + Pixels[((2 * y + 1) * Width + 2 * x + 1) * 4 + c];
                    pixels[(y * w + x) * 4 + c] = (byte)(sum / 4);
                }
            }
        }
        return new ScreenImage(pixels, w, h, Origin);
    }
}

/// <summary>
/// Plain GDI screen capture: copies what's visible on screen, like a screenshot. Works when League runs
/// borderless or windowed; exclusive fullscreen can come back black. Never touches the game process.
/// </summary>
public static class ScreenCapture
{
    private const string LeagueProcessName = "League of Legends";

    /// <summary>The game's client area on screen, or null when the game isn't running or is minimized.</summary>
    public static PixelRect? FindLeagueWindow()
    {
        var handle = Process.GetProcessesByName(LeagueProcessName).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        return handle == IntPtr.Zero ? null : ClientArea(handle);
    }

    public static PixelRect? WindowArea(IntPtr handle) => InPhysicalPixels(() =>
        GetWindowRect(handle, out var r) ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : (PixelRect?)null);

    public static ScreenImage Capture(PixelRect area) => InPhysicalPixels(() =>
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, SrcCopy | CaptureBlt);
            var info = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = area.Width,
                Height = -area.Height, // negative: top row first
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[area.Width * area.Height * 4];
            GetDIBits(memory, bitmap, 0, (uint)area.Height, pixels, ref info, 0);
            return new ScreenImage(pixels, area.Width, area.Height, area);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    });

    private static PixelRect? ClientArea(IntPtr handle) => InPhysicalPixels(() =>
    {
        if (IsIconic(handle) || !GetClientRect(handle, out var client))
            return (PixelRect?)null;
        var origin = new Point();
        ClientToScreen(handle, ref origin);
        return client.Right > 0 ? new PixelRect(origin.X, origin.Y, client.Right, client.Bottom) : null;
    });

    // Coordinates and capture must agree on pixels regardless of Windows display scaling.
    private static T InPhysicalPixels<T>(Func<T> action)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            return action();
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
