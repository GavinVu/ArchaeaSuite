using SkiaSharp;

namespace ArchaeaPhoto.Core;

/// <summary>
/// Image filters. Each returns a new bitmap with the source's geometry and
/// works on BGRA premultiplied bitmaps and Alpha8 masks alike. Edges are
/// clamped, so an opaque photo stays opaque right up to its border.
/// </summary>
public static unsafe class Filters
{
    /// <summary>Gaussian blur approximated by three box blurs; cost is independent of the radius.</summary>
    public static SKBitmap GaussianBlur(SKBitmap src, float radius)
    {
        var dst = Px.Clone(src);
        if (radius < 0.05f) return dst;
        using var tmp = new SKBitmap(src.Info);
        foreach (int box in BoxSizes(radius, 3))
        {
            int r = (box - 1) / 2;
            if (r < 1) continue;
            BoxPass(dst, tmp, r, horizontal: true);
            BoxPass(tmp, dst, r, horizontal: false);
        }
        return dst;
    }

    static int[] BoxSizes(float sigma, int n)
    {
        double ideal = Math.Sqrt(12.0 * sigma * sigma / n + 1.0);
        int wl = (int)Math.Floor(ideal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int m = (int)Math.Round((12.0 * sigma * sigma - n * wl * wl - 4.0 * n * wl - 3.0 * n) / (-4.0 * wl - 4.0));
        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = i < m ? wl : wu;
        return sizes;
    }

    static void BoxPass(SKBitmap src, SKBitmap dst, int r, bool horizontal)
    {
        int w = src.Width, h = src.Height, bpp = src.BytesPerPixel, rb = src.RowBytes;
        byte* sp = (byte*)src.GetPixels(), dp = (byte*)dst.GetPixels();
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        long step = horizontal ? bpp : rb;
        int window = 2 * r + 1;
        Parallel.For(0, lines, line =>
        {
            byte* s0 = horizontal ? sp + (long)line * rb : sp + (long)line * bpp;
            byte* d0 = horizontal ? dp + (long)line * rb : dp + (long)line * bpp;
            for (int c = 0; c < bpp; c++)
            {
                byte* s = s0 + c, d = d0 + c;
                int first = s[0], last = s[(length - 1) * step];
                int sum = first * r;
                for (int i = 0; i < r; i++) sum += i < length ? s[i * step] : last;
                for (int i = 0; i < length; i++)
                {
                    int add = i + r < length ? s[(i + r) * step] : last;
                    sum += add;
                    d[i * step] = (byte)((sum + window / 2) / window);
                    int sub = i - r >= 0 ? s[(i - r) * step] : first;
                    sum -= sub;
                }
            }
        });
    }

    /// <summary>
    /// Unsharp mask. <paramref name="amount"/> is a multiplier (1 = 100%),
    /// <paramref name="threshold"/> the minimum difference (0..255) to sharpen.
    /// </summary>
    public static SKBitmap Sharpen(SKBitmap src, float amount, float radius, int threshold)
    {
        using var blurred = GaussianBlur(src, Math.Max(0.1f, radius));
        var dst = new SKBitmap(src.Info);
        int w = src.Width, h = src.Height, bpp = src.BytesPerPixel, rb = src.RowBytes;
        byte* sp = (byte*)src.GetPixels(), bp = (byte*)blurred.GetPixels(), dp = (byte*)dst.GetPixels();
        bool color = bpp == 4;
        Parallel.For(0, h, y =>
        {
            byte* s = sp + (long)y * rb, b = bp + (long)y * rb, d = dp + (long)y * rb;
            for (int x = 0; x < w; x++, s += bpp, b += bpp, d += bpp)
            {
                // Premultiplied colour may not exceed alpha, and alpha is kept as it was.
                int limit = color ? s[3] : 255;
                int n = color ? 3 : 1;
                for (int c = 0; c < n; c++)
                {
                    int diff = s[c] - b[c];
                    d[c] = Math.Abs(diff) < threshold ? s[c] : (byte)Math.Clamp((int)MathF.Round(s[c] + diff * amount), 0, limit);
                }
                if (color) d[3] = s[3];
            }
        });
        return dst;
    }

    /// <summary>
    /// Edge-preserving noise reduction (bilateral filter).
    /// <paramref name="strength"/> 0..100 controls how different two pixels may be
    /// and still be averaged; <paramref name="radius"/> 1..8 is the neighbourhood.
    /// </summary>
    public static SKBitmap ReduceNoise(SKBitmap src, float strength, int radius)
    {
        radius = Math.Clamp(radius, 1, 8);
        if (strength <= 0) return Px.Clone(src);
        var dst = new SKBitmap(src.Info);
        int w = src.Width, h = src.Height, bpp = src.BytesPerPixel, rb = src.RowBytes;
        byte* sp = (byte*)src.GetPixels(), dp = (byte*)dst.GetPixels();

        float sigmaS = Math.Max(0.8f, radius / 1.6f), sigmaR = 3f + strength * 0.55f;
        int size = 2 * radius + 1;
        var spatial = new float[size * size];
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                spatial[(dy + radius) * size + dx + radius] = MathF.Exp(-(dx * dx + dy * dy) / (2f * sigmaS * sigmaS));
        // Indexed by the squared colour distance summed over the channels.
        int channels = bpp == 4 ? 3 : 1;
        var range = new float[channels * 255 * 255 + 1];
        for (int i = 0; i < range.Length; i++) range[i] = MathF.Exp(-(i / (float)channels) / (2f * sigmaR * sigmaR));

        Parallel.For(0, h, y =>
        {
            byte* d = dp + (long)y * rb;
            byte* centre = sp + (long)y * rb;
            for (int x = 0; x < w; x++, centre += bpp, d += bpp)
            {
                float sum0 = 0, sum1 = 0, sum2 = 0, wsum = 0;
                int c0 = centre[0], c1 = channels == 3 ? centre[1] : 0, c2 = channels == 3 ? centre[2] : 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1);
                    byte* row = sp + (long)yy * rb;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1);
                        byte* q = row + xx * bpp;
                        int e0 = q[0] - c0;
                        int dist = e0 * e0;
                        if (channels == 3)
                        {
                            int e1 = q[1] - c1, e2 = q[2] - c2;
                            dist += e1 * e1 + e2 * e2;
                        }
                        float wgt = spatial[(dy + radius) * size + dx + radius] * range[dist];
                        wsum += wgt;
                        sum0 += q[0] * wgt;
                        if (channels == 3) { sum1 += q[1] * wgt; sum2 += q[2] * wgt; }
                    }
                }
                d[0] = (byte)(sum0 / wsum + 0.5f);
                if (channels == 3)
                {
                    int a = centre[3];
                    d[0] = (byte)Math.Min(a, (int)d[0]);
                    d[1] = (byte)Math.Min(a, (int)(sum1 / wsum + 0.5f));
                    d[2] = (byte)Math.Min(a, (int)(sum2 / wsum + 0.5f));
                    d[3] = (byte)a;
                }
            }
        });
        return dst;
    }
}
