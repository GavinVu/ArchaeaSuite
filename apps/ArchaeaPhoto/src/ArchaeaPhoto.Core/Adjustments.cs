using SkiaSharp;

namespace ArchaeaPhoto.Core;

/// <summary>
/// A colour adjustment. Subclasses either fill per-channel lookup tables or
/// transform straight-alpha RGB in 0..1. <see cref="Apply"/> handles
/// premultiplication, transparency and Alpha8 masks.
/// </summary>
public abstract unsafe class Adjustment
{
    public abstract string Name { get; }

    /// <summary>Fill the tables and return true if the adjustment is a per-channel curve.</summary>
    protected virtual bool BuildLut(byte[] r, byte[] g, byte[] b) => false;

    protected virtual void Transform(ref float r, ref float g, ref float b) { }

    /// <summary>Returns an adjusted copy of <paramref name="src"/> (BGRA premultiplied or Alpha8).</summary>
    public SKBitmap Apply(SKBitmap src)
    {
        var dst = new SKBitmap(src.Info);
        byte[] lr = new byte[256], lg = new byte[256], lb = new byte[256];
        bool lut = BuildLut(lr, lg, lb);
        int w = src.Width, h = src.Height, rb = src.RowBytes;
        byte* sp = (byte*)src.GetPixels(), dp = (byte*)dst.GetPixels();

        if (src.ColorType == SKColorType.Alpha8)
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                if (lut) table[i] = (byte)((lr[i] * 54 + lg[i] * 183 + lb[i] * 19) >> 8);
                else
                {
                    float r = i / 255f, g = r, b = r;
                    Transform(ref r, ref g, ref b);
                    table[i] = ToByte(r * 0.2126f + g * 0.7152f + b * 0.0722f);
                }
            }
            for (int y = 0; y < h; y++)
            {
                byte* s = sp + (long)y * rb, d = dp + (long)y * rb;
                for (int x = 0; x < w; x++) d[x] = table[s[x]];
            }
            return dst;
        }

        Parallel.For(0, h, y =>
        {
            byte* s = sp + (long)y * rb, d = dp + (long)y * rb;
            for (int x = 0; x < w; x++, s += 4, d += 4)
            {
                int a = s[3];
                if (a == 0) { *(uint*)d = 0; continue; }
                int b = s[0], g = s[1], r = s[2];
                if (a != 255)
                {
                    b = Math.Min(255, (b * 255 + a / 2) / a);
                    g = Math.Min(255, (g * 255 + a / 2) / a);
                    r = Math.Min(255, (r * 255 + a / 2) / a);
                }
                if (lut)
                {
                    r = lr[r]; g = lg[g]; b = lb[b];
                }
                else
                {
                    float fr = r / 255f, fg = g / 255f, fb = b / 255f;
                    Transform(ref fr, ref fg, ref fb);
                    r = ToByte(fr); g = ToByte(fg); b = ToByte(fb);
                }
                if (a != 255)
                {
                    b = (b * a + 127) / 255;
                    g = (g * a + 127) / 255;
                    r = (r * a + 127) / 255;
                }
                d[0] = (byte)b; d[1] = (byte)g; d[2] = (byte)r; d[3] = (byte)a;
            }
        });
        return dst;
    }

    protected static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    protected static void Fill(byte[] r, byte[] g, byte[] b, Func<float, float> f)
    {
        for (int i = 0; i < 256; i++) r[i] = g[i] = b[i] = ToByte(f(i / 255f));
    }

    /// <summary>Luma histogram of the visible pixels.</summary>
    public static int[] Histogram(SKBitmap src)
    {
        var hist = new int[256];
        byte* p = (byte*)src.GetPixels();
        bool mask = src.ColorType == SKColorType.Alpha8;
        for (int y = 0; y < src.Height; y++)
        {
            byte* s = p + (long)y * src.RowBytes;
            for (int x = 0; x < src.Width; x++)
            {
                if (mask) { hist[s[x]]++; continue; }
                int a = s[x * 4 + 3];
                if (a == 0) continue;
                int l = (s[x * 4 + 2] * 54 + s[x * 4 + 1] * 183 + s[x * 4] * 19) >> 8;
                hist[Math.Min(255, l * 255 / a)]++;
            }
        }
        return hist;
    }
}

public sealed class BrightnessContrast : Adjustment
{
    /// <summary>-100..100</summary>
    public float Brightness, Contrast;
    public override string Name => "Brightness/Contrast";

    protected override bool BuildLut(byte[] r, byte[] g, byte[] b)
    {
        float c = Math.Clamp(Contrast, -100, 100) * 2.55f;
        float factor = 259f * (c + 255f) / (255f * (259f - c));
        float add = Math.Clamp(Brightness, -100, 100) / 100f * 0.5f;
        Fill(r, g, b, v => factor * (v + add - 0.5f) + 0.5f);
        return true;
    }
}

public sealed class Exposure : Adjustment
{
    /// <summary>Stops, -5..5.</summary>
    public float Stops;
    /// <summary>-0.5..0.5, added in linear light.</summary>
    public float Offset;
    /// <summary>0.1..5; 1 leaves midtones alone.</summary>
    public float Gamma = 1f;
    public override string Name => "Exposure";

    protected override bool BuildLut(byte[] r, byte[] g, byte[] b)
    {
        float gain = MathF.Pow(2f, Stops), invGamma = 1f / Math.Clamp(Gamma, 0.1f, 5f);
        Fill(r, g, b, v =>
        {
            float lin = v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
            lin = Math.Max(0f, lin * gain + Offset);
            lin = MathF.Pow(lin, invGamma);
            return lin <= 0.0031308f ? lin * 12.92f : 1.055f * MathF.Pow(lin, 1f / 2.4f) - 0.055f;
        });
        return true;
    }
}

public sealed class Levels : Adjustment
{
    /// <summary>0..255</summary>
    public float InBlack = 0, InWhite = 255, OutBlack = 0, OutWhite = 255;
    /// <summary>0.1..9.99; 1 is neutral.</summary>
    public float Gamma = 1f;
    public override string Name => "Levels";

    protected override bool BuildLut(byte[] r, byte[] g, byte[] b)
    {
        float lo = InBlack / 255f, hi = Math.Max(InBlack + 1, InWhite) / 255f, invGamma = 1f / Math.Clamp(Gamma, 0.1f, 9.99f);
        float olo = OutBlack / 255f, ohi = OutWhite / 255f;
        Fill(r, g, b, v => olo + MathF.Pow(Math.Clamp((v - lo) / (hi - lo), 0f, 1f), invGamma) * (ohi - olo));
        return true;
    }
}

public sealed class Curves : Adjustment
{
    /// <summary>Control points (x, y in 0..1) for the composite curve and each channel.</summary>
    public List<SKPoint> Rgb = Identity(), Red = Identity(), Green = Identity(), Blue = Identity();
    public override string Name => "Curves";

    public static List<SKPoint> Identity() => new() { new(0, 0), new(1, 1) };

    protected override bool BuildLut(byte[] r, byte[] g, byte[] b)
    {
        byte[] all = Table(Rgb), tr = Table(Red), tg = Table(Green), tb = Table(Blue);
        for (int i = 0; i < 256; i++)
        {
            r[i] = all[tr[i]];
            g[i] = all[tg[i]];
            b[i] = all[tb[i]];
        }
        return true;
    }

    /// <summary>Monotone cubic interpolation through the points (no overshoot between them).</summary>
    public static byte[] Table(IReadOnlyList<SKPoint> points)
    {
        var p = points.OrderBy(q => q.X).ToList();
        var table = new byte[256];
        int n = p.Count;
        if (n == 0) { for (int i = 0; i < 256; i++) table[i] = (byte)i; return table; }
        if (n == 1) { Array.Fill(table, ToByte(p[0].Y)); return table; }
        var d = new float[n - 1];
        var m = new float[n];
        for (int i = 0; i < n - 1; i++)
        {
            float dx = Math.Max(1e-5f, p[i + 1].X - p[i].X);
            d[i] = (p[i + 1].Y - p[i].Y) / dx;
        }
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2f;
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = m[i + 1] = 0; continue; }
            float a = m[i] / d[i], b = m[i + 1] / d[i], s = a * a + b * b;
            if (s > 9f)
            {
                float t = 3f / MathF.Sqrt(s);
                m[i] = t * a * d[i];
                m[i + 1] = t * b * d[i];
            }
        }
        int seg = 0;
        for (int i = 0; i < 256; i++)
        {
            float x = i / 255f;
            if (x <= p[0].X) { table[i] = ToByte(p[0].Y); continue; }
            if (x >= p[n - 1].X) { table[i] = ToByte(p[n - 1].Y); continue; }
            while (seg < n - 2 && x > p[seg + 1].X) seg++;
            float h = Math.Max(1e-5f, p[seg + 1].X - p[seg].X), t2 = (x - p[seg].X) / h;
            float tt = t2 * t2, ttt = tt * t2;
            float y = (2 * ttt - 3 * tt + 1) * p[seg].Y + (ttt - 2 * tt + t2) * h * m[seg]
                    + (-2 * ttt + 3 * tt) * p[seg + 1].Y + (ttt - tt) * h * m[seg + 1];
            table[i] = ToByte(y);
        }
        return table;
    }
}

public sealed class HueSaturation : Adjustment
{
    /// <summary>Hue in degrees (-180..180); saturation and lightness -100..100.</summary>
    public float Hue, Saturation, Lightness;
    public override string Name => "Hue/Saturation";

    protected override void Transform(ref float r, ref float g, ref float b)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        float l = (max + min) / 2f, h = 0, s = 0, d = max - min;
        if (d > 1e-6f)
        {
            s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6f;
        }
        h = (h + Hue / 360f) % 1f;
        if (h < 0) h += 1f;
        float sa = Saturation / 100f;
        s = Math.Clamp(sa >= 0 ? s * (1f + sa * 2f) : s * (1f + sa), 0f, 1f);
        float la = Lightness / 100f;
        l = la >= 0 ? l + (1f - l) * la : l * (1f + la);

        if (s <= 0f) { r = g = b = l; return; }
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s, p = 2f * l - q;
        r = Channel(p, q, h + 1f / 3f);
        g = Channel(p, q, h);
        b = Channel(p, q, h - 1f / 3f);
    }

    static float Channel(float p, float q, float t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 0.5f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}

public sealed class ColorBalance : Adjustment
{
    /// <summary>Each -100..100: [cyan–red, magenta–green, yellow–blue].</summary>
    public float[] Shadows = new float[3], Midtones = new float[3], Highlights = new float[3];
    public bool PreserveLuminosity = true;
    public override string Name => "Color Balance";

    protected override void Transform(ref float r, ref float g, ref float b)
    {
        float l = r * 0.2126f + g * 0.7152f + b * 0.0722f;
        float ws = (1f - l) * (1f - l), wh = l * l, wm = 1f - ws - wh;
        const float k = 0.5f / 100f;
        float nr = r + (Shadows[0] * ws + Midtones[0] * wm + Highlights[0] * wh) * k;
        float ng = g + (Shadows[1] * ws + Midtones[1] * wm + Highlights[1] * wh) * k;
        float nb = b + (Shadows[2] * ws + Midtones[2] * wm + Highlights[2] * wh) * k;
        nr = Math.Clamp(nr, 0f, 1f);
        ng = Math.Clamp(ng, 0f, 1f);
        nb = Math.Clamp(nb, 0f, 1f);
        if (PreserveLuminosity)
        {
            float nl = nr * 0.2126f + ng * 0.7152f + nb * 0.0722f;
            float shift = l - nl;
            nr += shift; ng += shift; nb += shift;
        }
        r = nr; g = ng; b = nb;
    }
}

public sealed class Invert : Adjustment
{
    public override string Name => "Invert";
    protected override bool BuildLut(byte[] r, byte[] g, byte[] b)
    {
        Fill(r, g, b, v => 1f - v);
        return true;
    }
}

public sealed class Desaturate : Adjustment
{
    public override string Name => "Desaturate";
    protected override void Transform(ref float r, ref float g, ref float b) =>
        r = g = b = r * 0.2126f + g * 0.7152f + b * 0.0722f;
}
