using SkiaSharp;

namespace ArchaeaPhoto.Core;

/// <summary>
/// Pixel-buffer helpers. Every colour bitmap in the engine is BGRA8888
/// premultiplied; masks and selections are Alpha8 (255 = fully revealed/selected).
/// </summary>
public static unsafe class Px
{
    public static SKImageInfo ColorInfo(int w, int h) =>
        new(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

    public static SKImageInfo MaskInfo(int w, int h) =>
        new(w, h, SKColorType.Alpha8, SKAlphaType.Premul);

    public static SKBitmap NewColor(int w, int h)
    {
        var b = new SKBitmap(ColorInfo(Math.Max(1, w), Math.Max(1, h)));
        Clear(b, 0);
        return b;
    }

    public static SKBitmap NewMask(int w, int h, byte fill = 0)
    {
        var b = new SKBitmap(MaskInfo(Math.Max(1, w), Math.Max(1, h)));
        Clear(b, fill);
        return b;
    }

    public static void Clear(SKBitmap b, byte value) =>
        new Span<byte>((void*)b.GetPixels(), b.RowBytes * b.Height).Fill(value);

    public static SKBitmap Clone(SKBitmap src)
    {
        var b = new SKBitmap(src.Info);
        CopyAll(src, b);
        return b;
    }

    /// <summary>Copies pixels between two bitmaps with identical geometry.</summary>
    public static void CopyAll(SKBitmap src, SKBitmap dst)
    {
        long bytes = (long)src.RowBytes * src.Height;
        Buffer.MemoryCopy((void*)src.GetPixels(), (void*)dst.GetPixels(), bytes, bytes);
    }

    public static byte[] GetRect(SKBitmap b, SKRectI r)
    {
        int bpp = b.BytesPerPixel, stride = r.Width * bpp;
        var data = new byte[stride * r.Height];
        byte* p = (byte*)b.GetPixels();
        fixed (byte* d = data)
            for (int y = 0; y < r.Height; y++)
                Buffer.MemoryCopy(p + (long)(r.Top + y) * b.RowBytes + r.Left * bpp, d + y * stride, stride, stride);
        return data;
    }

    public static void SetRect(SKBitmap b, SKRectI r, byte[] data)
    {
        int bpp = b.BytesPerPixel, stride = r.Width * bpp;
        byte* p = (byte*)b.GetPixels();
        fixed (byte* d = data)
            for (int y = 0; y < r.Height; y++)
                Buffer.MemoryCopy(d + y * stride, p + (long)(r.Top + y) * b.RowBytes + r.Left * bpp, stride, stride);
    }

    /// <summary>Smallest rectangle containing every pixel that differs, or empty.</summary>
    public static SKRectI DiffBounds(SKBitmap a, SKBitmap b)
    {
        int w = a.Width, h = a.Height, bpp = a.BytesPerPixel, rb = a.RowBytes;
        byte* pa = (byte*)a.GetPixels(), pb = (byte*)b.GetPixels();
        int top = -1, bottom = -1, left = w, right = -1;
        for (int y = 0; y < h; y++)
        {
            var ra = new ReadOnlySpan<byte>(pa + (long)y * rb, w * bpp);
            var rbw = new ReadOnlySpan<byte>(pb + (long)y * rb, w * bpp);
            if (ra.SequenceEqual(rbw)) continue;
            if (top < 0) top = y;
            bottom = y;
            int first = ra.CommonPrefixLength(rbw) / bpp;
            int last = w - 1;
            while (last > first && ra.Slice(last * bpp, bpp).SequenceEqual(rbw.Slice(last * bpp, bpp))) last--;
            if (first < left) left = first;
            if (last > right) right = last;
        }
        return top < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
    }

    /// <summary>Draws a bitmap without the full-pixel copy SKCanvas.DrawBitmap makes.</summary>
    public static void Draw(SKCanvas canvas, SKBitmap bmp, float x, float y, SKPaint? paint = null)
    {
        using var pm = bmp.PeekPixels();
        using var img = SKImage.FromPixels(pm);
        canvas.DrawImage(img, x, y, paint);
    }

    public static void Draw(SKCanvas canvas, SKBitmap bmp, SKRect src, SKRect dst, SKPaint? paint = null)
    {
        using var pm = bmp.PeekPixels();
        using var img = SKImage.FromPixels(pm);
        canvas.DrawImage(img, src, dst, paint);
    }

    /// <summary>
    /// Draws an Alpha8 bitmap as a real source whose alpha is the mask value.
    /// Drawing it as an image would treat the mask as coverage instead, which
    /// leaves the destination untouched wherever the mask is zero — wrong for
    /// DstIn and Src, the blend modes masks and selections are applied with.
    /// </summary>
    public static void DrawMask(SKCanvas canvas, SKBitmap mask, float x, float y, SKBlendMode mode, SKFilterQuality quality = SKFilterQuality.None, bool antialias = false)
    {
        using var pm = mask.PeekPixels();
        using var img = SKImage.FromPixels(pm);
        using var shader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateTranslation(x, y));
        using var paint = new SKPaint { Shader = shader, BlendMode = mode, Color = SKColors.Black, FilterQuality = quality, IsAntialias = antialias };
        canvas.DrawRect(SKRect.Create(x, y, mask.Width, mask.Height), paint);
    }

    public static SKRectI Union(SKRectI a, SKRectI b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new SKRectI(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    }

    public static SKRectI Intersect(SKRectI a, SKRectI b)
    {
        var r = new SKRectI(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
        return r.Width <= 0 || r.Height <= 0 ? SKRectI.Empty : r;
    }

    public static SKRectI RoundOut(SKRect r) =>
        new((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));

    /// <summary>Reads one pixel as a straight-alpha colour; transparent outside the bitmap.</summary>
    public static SKColor GetColor(SKBitmap b, int x, int y)
    {
        if ((uint)x >= (uint)b.Width || (uint)y >= (uint)b.Height) return SKColors.Transparent;
        byte* p = (byte*)b.GetPixels() + (long)y * b.RowBytes + x * 4;
        byte a = p[3];
        if (a == 0) return SKColors.Transparent;
        if (a == 255) return new SKColor(p[2], p[1], p[0], 255);
        return new SKColor((byte)Math.Min(255, p[2] * 255 / a), (byte)Math.Min(255, p[1] * 255 / a), (byte)Math.Min(255, p[0] * 255 / a), a);
    }

    public static byte Luma(SKColor c) => (byte)((c.Red * 54 + c.Green * 183 + c.Blue * 19) >> 8);
}
