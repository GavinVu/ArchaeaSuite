using SkiaSharp;

namespace ArchaeaPhoto.Core;

public enum SelectMode { Replace, Add, Subtract, Intersect }

/// <summary>Selections are canvas-sized Alpha8 bitmaps; 255 = fully selected.</summary>
public static unsafe class Sel
{
    public static SKBitmap FromPath(int width, int height, SKPath path, bool antialias = true)
    {
        var m = Px.NewMask(width, height);
        using var c = new SKCanvas(m);
        using var p = new SKPaint { IsAntialias = antialias, Color = SKColors.Black };
        c.DrawPath(path, p);
        return m;
    }

    public static SKBitmap FromRect(int width, int height, SKRectI rect)
    {
        using var path = new SKPath();
        path.AddRect(rect);
        return FromPath(width, height, path, false);
    }

    /// <summary>Combines a new shape with the current selection. Always returns a new bitmap.</summary>
    public static SKBitmap Combine(SKBitmap? current, SKBitmap shape, SelectMode mode)
    {
        if (mode == SelectMode.Replace || current is null)
            return mode is SelectMode.Subtract or SelectMode.Intersect ? Px.NewMask(shape.Width, shape.Height) : Px.Clone(shape);
        var result = Px.Clone(current);
        using var c = new SKCanvas(result);
        Px.DrawMask(c, shape, 0, 0, mode switch
        {
            SelectMode.Subtract => SKBlendMode.DstOut,
            SelectMode.Intersect => SKBlendMode.DstIn,
            _ => SKBlendMode.SrcOver,
        });
        return result;
    }

    public static SKBitmap Invert(SKBitmap? sel, int width, int height)
    {
        if (sel is null) return Px.NewMask(width, height, 255);
        var r = Px.Clone(sel);
        byte* p = (byte*)r.GetPixels();
        for (int y = 0; y < r.Height; y++)
        {
            byte* row = p + (long)y * r.RowBytes;
            for (int x = 0; x < r.Width; x++) row[x] = (byte)(255 - row[x]);
        }
        return r;
    }

    public static bool IsEmpty(SKBitmap sel) => Bounds(sel).IsEmpty;

    /// <summary>Bounding box of every pixel with any selection at all.</summary>
    public static SKRectI Bounds(SKBitmap sel)
    {
        int w = sel.Width, h = sel.Height, top = -1, bottom = -1, left = w, right = -1;
        byte* p = (byte*)sel.GetPixels();
        for (int y = 0; y < h; y++)
        {
            var row = new ReadOnlySpan<byte>(p + (long)y * sel.RowBytes, w);
            int first = row.IndexOfAnyExcept((byte)0);
            if (first < 0) continue;
            if (top < 0) top = y;
            bottom = y;
            int last = row.LastIndexOfAnyExcept((byte)0);
            if (first < left) left = first;
            if (last > right) right = last;
        }
        return top < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
    }

    /// <summary>
    /// The boundary between selected (>= 50%) and unselected pixels as axis-aligned
    /// segments along pixel edges, for drawing marching ants.
    /// </summary>
    public static SKPath Outline(SKBitmap sel)
    {
        var path = new SKPath();
        int w = sel.Width, h = sel.Height, rb = sel.RowBytes;
        byte* p = (byte*)sel.GetPixels();
        // Horizontal edges: between row y-1 and row y.
        for (int y = 0; y <= h; y++)
        {
            byte* above = y > 0 ? p + (long)(y - 1) * rb : null;
            byte* below = y < h ? p + (long)y * rb : null;
            int start = -1;
            for (int x = 0; x <= w; x++)
            {
                bool edge = x < w && ((above != null && above[x] >= 128) != (below != null && below[x] >= 128));
                if (edge && start < 0) start = x;
                else if (!edge && start >= 0)
                {
                    path.MoveTo(start, y);
                    path.LineTo(x, y);
                    start = -1;
                }
            }
        }
        // Vertical edges: between column x-1 and column x.
        for (int x = 0; x <= w; x++)
        {
            int start = -1;
            for (int y = 0; y <= h; y++)
            {
                bool edge = false;
                if (y < h)
                {
                    byte* row = p + (long)y * rb;
                    edge = (x > 0 && row[x - 1] >= 128) != (x < w && row[x] >= 128);
                }
                if (edge && start < 0) start = y;
                else if (!edge && start >= 0)
                {
                    path.MoveTo(x, start);
                    path.LineTo(x, y);
                    start = -1;
                }
            }
        }
        return path;
    }

    /// <summary>
    /// Selects pixels of <paramref name="src"/> (BGRA premultiplied) whose colour is
    /// within <paramref name="tolerance"/> (0..255) of the pixel at (x, y).
    /// </summary>
    public static SKBitmap Flood(SKBitmap src, int x, int y, int tolerance, bool contiguous)
    {
        int w = src.Width, h = src.Height, rb = src.RowBytes;
        var mask = Px.NewMask(w, h);
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return mask;
        byte* sp = (byte*)src.GetPixels();
        byte* mp = (byte*)mask.GetPixels();
        int mrb = mask.RowBytes;
        byte* seed = sp + (long)y * rb + x * 4;
        int sb = seed[0], sg = seed[1], sr = seed[2], sa = seed[3];

        bool Match(byte* q) =>
            Math.Abs(q[0] - sb) <= tolerance && Math.Abs(q[1] - sg) <= tolerance &&
            Math.Abs(q[2] - sr) <= tolerance && Math.Abs(q[3] - sa) <= tolerance;

        if (!contiguous)
        {
            for (int yy = 0; yy < h; yy++)
            {
                byte* row = sp + (long)yy * rb;
                byte* mrow = mp + (long)yy * mrb;
                for (int xx = 0; xx < w; xx++)
                    if (Match(row + xx * 4)) mrow[xx] = 255;
            }
            return mask;
        }

        // Scanline flood fill.
        var stack = new Stack<(int x, int y)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            byte* row = sp + (long)cy * rb;
            byte* mrow = mp + (long)cy * mrb;
            if (mrow[cx] != 0 || !Match(row + cx * 4)) continue;
            int l = cx, r = cx;
            while (l > 0 && mrow[l - 1] == 0 && Match(row + (l - 1) * 4)) l--;
            while (r < w - 1 && mrow[r + 1] == 0 && Match(row + (r + 1) * 4)) r++;
            for (int i = l; i <= r; i++) mrow[i] = 255;
            for (int dy = -1; dy <= 1; dy += 2)
            {
                int ny = cy + dy;
                if (ny < 0 || ny >= h) continue;
                byte* nrow = sp + (long)ny * rb;
                byte* nm = mp + (long)ny * mrb;
                bool inRun = false;
                for (int i = l; i <= r; i++)
                {
                    bool ok = nm[i] == 0 && Match(nrow + i * 4);
                    if (ok && !inRun) stack.Push((i, ny));
                    inRun = ok;
                }
            }
        }
        return mask;
    }
}
