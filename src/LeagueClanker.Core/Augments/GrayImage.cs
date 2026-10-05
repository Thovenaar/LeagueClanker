using System.Collections.Concurrent;

namespace LeagueClanker.Core.Augments;

/// <summary>A grayscale image, top row first, one value per pixel from 0 (black) to 255.</summary>
public sealed class GrayImage(int width, int height, float[] pixels)
{
    private readonly ConcurrentDictionary<int, GrayImage> _squares = new();

    public int Width { get; } = width;
    public int Height { get; } = height;
    public float[] Pixels { get; } = pixels;

    public float this[int x, int y] => Pixels[y * Width + x];

    /// <summary>
    /// From 32-bit BGRA pixels. Transparent pixels count as black, which is what an icon sits on in the HUD.
    /// </summary>
    public static GrayImage FromBgra(byte[] bgra, int width, int height, bool useAlpha = false)
    {
        var pixels = new float[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var gray = (bgra[i * 4] + bgra[i * 4 + 1] + bgra[i * 4 + 2]) / 3f;
            pixels[i] = useAlpha ? gray * bgra[i * 4 + 3] / 255f : gray;
        }
        return new GrayImage(width, height, pixels);
    }

    /// <summary>This image scaled to a square of the given size. Kept, since every read asks for the same few sizes.</summary>
    public GrayImage Square(int size) => _squares.GetOrAdd(size, s => Resize(s, s));

    /// <summary>A copy at another size, averaging the pixels each new pixel covers.</summary>
    public GrayImage Resize(int width, int height)
    {
        var rows = new float[width * Height];
        var (columnFrom, columnWeights) = Weights(Width, width);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var k = 0; k < columnWeights[x].Length; k++)
                    sum += Pixels[y * Width + columnFrom[x] + k] * columnWeights[x][k];
                rows[y * width + x] = sum;
            }

        var result = new float[width * height];
        var (rowFrom, rowWeights) = Weights(Height, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var k = 0; k < rowWeights[y].Length; k++)
                    sum += rows[(rowFrom[y] + k) * width + x] * rowWeights[y][k];
                result[y * width + x] = sum;
            }
        return new GrayImage(width, height, result);
    }

    // A triangle filter as wide as one new pixel, like a bilinear resize that also smooths when it shrinks.
    private static (int[] From, float[][] Weights) Weights(int from, int to)
    {
        var scale = (double)from / to;
        var support = Math.Max(1.0, scale);
        var starts = new int[to];
        var weights = new float[to][];
        for (var i = 0; i < to; i++)
        {
            var center = (i + 0.5) * scale;
            var first = Math.Max(0, (int)Math.Floor(center - support));
            var last = Math.Min(from - 1, (int)Math.Ceiling(center + support));
            var w = new float[last - first + 1];
            var total = 0.0;
            for (var j = first; j <= last; j++)
            {
                var weight = Math.Max(0, 1 - Math.Abs(j + 0.5 - center) / support);
                w[j - first] = (float)weight;
                total += weight;
            }
            for (var j = 0; j < w.Length; j++)
                w[j] = total > 0 ? (float)(w[j] / total) : 0;
            starts[i] = first;
            weights[i] = w;
        }
        return (starts, weights);
    }
}
