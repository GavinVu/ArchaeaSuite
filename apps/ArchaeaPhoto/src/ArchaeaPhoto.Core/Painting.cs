using SkiaSharp;

namespace ArchaeaPhoto.Core;

public enum StrokeKind { Brush, Eraser, Clone }

public sealed class BrushSettings
{
    /// <summary>Diameter in pixels.</summary>
    public float Size { get; set; } = 24;
    /// <summary>0 = fully soft edge, 1 = hard edge.</summary>
    public float Hardness { get; set; } = 0.8f;
    /// <summary>Ceiling for the whole stroke, 0..1.</summary>
    public float Opacity { get; set; } = 1f;
    /// <summary>How much each dab adds, 0..1.</summary>
    public float Flow { get; set; } = 1f;
    /// <summary>Distance between dabs as a fraction of the diameter.</summary>
    public float Spacing { get; set; } = 0.12f;

    public BrushSettings Clone() => (BrushSettings)MemberwiseClone();
}

/// <summary>
/// One brush, eraser or clone-stamp stroke. Dabs accumulate into a coverage
/// buffer; the target is recomputed from an untouched copy, so overlapping dabs
/// never exceed the stroke opacity. The stroke is clipped to the canvas and the
/// selection, and becomes a single undo step.
/// </summary>
public sealed unsafe class Stroke : IDisposable
{
    readonly Document doc;
    readonly PaintScope scope;
    readonly PaintTarget target;
    readonly StrokeKind kind;
    readonly BrushSettings brush;
    readonly SKBitmap original;
    readonly SKBitmap coverage;
    readonly SKBitmap? selection;
    readonly float cb, cg, cr, ca; // premultiplied paint colour, 0..255
    readonly float maskValue;      // value painted when the target is a mask
    readonly int cloneDx, cloneDy;
    SKRectI dirty = SKRectI.Empty;
    SKPoint last;
    float carry;
    bool started, finished;

    /// <param name="cloneOffset">For clone strokes: source = destination + offset.</param>
    public Stroke(Document doc, PaintScope scope, StrokeKind kind, BrushSettings brush, SKColor color, SKPointI cloneOffset = default)
    {
        this.doc = doc;
        this.scope = scope;
        target = scope.Target;
        this.kind = kind;
        this.brush = brush.Clone();
        original = Px.Clone(target.Bitmap);
        coverage = Px.NewMask(doc.Width, doc.Height);
        selection = doc.Selection;
        ca = color.Alpha;
        cr = color.Red * ca / 255f;
        cg = color.Green * ca / 255f;
        cb = color.Blue * ca / 255f;
        maskValue = kind == StrokeKind.Eraser ? 0 : Px.Luma(color);
        cloneDx = cloneOffset.X;
        cloneDy = cloneOffset.Y;
    }

    public void MoveTo(SKPoint p)
    {
        last = p;
        carry = 0;
        started = true;
        Dab(p.X, p.Y);
    }

    public void LineTo(SKPoint p)
    {
        if (!started) { MoveTo(p); return; }
        float dx = p.X - last.X, dy = p.Y - last.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist <= 0) return;
        float step = Math.Max(0.5f, brush.Size * brush.Spacing);
        float pos = step - carry;
        while (pos <= dist)
        {
            float t = pos / dist;
            Dab(last.X + dx * t, last.Y + dy * t);
            pos += step;
        }
        carry = dist - (pos - step);
        last = p;
    }

    void Dab(float cx, float cy)
    {
        float radius = Math.Max(0.5f, brush.Size / 2f);
        float feather = Math.Max(1f, radius * (1f - Math.Clamp(brush.Hardness, 0f, 1f)));
        float reach = radius + 0.5f;
        var rect = Px.Intersect(new SKRectI((int)MathF.Floor(cx - reach), (int)MathF.Floor(cy - reach),
            (int)MathF.Ceiling(cx + reach) + 1, (int)MathF.Ceiling(cy + reach) + 1), doc.Bounds);
        rect = Px.Intersect(rect, target.Bounds);
        if (rect.IsEmpty) return;

        float flow = Math.Clamp(brush.Flow, 0.01f, 1f), opacity = Math.Clamp(brush.Opacity, 0f, 1f);
        bool soft = feather > 1.01f;
        byte* cov = (byte*)coverage.GetPixels();
        byte* sel = selection is null ? null : (byte*)selection.GetPixels();
        byte* dst = (byte*)target.Bitmap.GetPixels();
        byte* org = (byte*)original.GetPixels();
        int trb = target.Bitmap.RowBytes, tx = target.X, ty = target.Y, ow = original.Width, oh = original.Height;

        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            byte* crow = cov + (long)y * coverage.RowBytes;
            byte* srow = sel == null ? null : sel + (long)y * selection!.RowBytes;
            float fy = y + 0.5f - cy;
            for (int x = rect.Left; x < rect.Right; x++)
            {
                float fx = x + 0.5f - cx;
                float d = MathF.Sqrt(fx * fx + fy * fy);
                float a = Math.Clamp((reach - d) / feather, 0f, 1f);
                if (a <= 0f) continue;
                if (soft) a = a * a * (3f - 2f * a);
                // Accumulate coverage: each dab closes part of the remaining gap.
                float c0 = crow[x];
                float c1 = c0 + (255f - c0) * a * flow;
                byte cbyte = (byte)(c1 + 0.5f);
                if (cbyte == crow[x]) continue;
                crow[x] = cbyte;

                float m = cbyte / 255f * opacity;
                if (srow != null) m *= srow[x] / 255f;
                int lx = x - tx, ly = y - ty;
                if (target.IsMask)
                {
                    long o = (long)ly * trb + lx;
                    dst[o] = (byte)(org[o] + (maskValue - org[o]) * m + 0.5f);
                    continue;
                }
                byte* op = org + (long)ly * trb + lx * 4;
                byte* dp = dst + (long)ly * trb + lx * 4;
                switch (kind)
                {
                    case StrokeKind.Eraser:
                    {
                        float k = 1f - m;
                        dp[0] = (byte)(op[0] * k + 0.5f);
                        dp[1] = (byte)(op[1] * k + 0.5f);
                        dp[2] = (byte)(op[2] * k + 0.5f);
                        dp[3] = (byte)(op[3] * k + 0.5f);
                        break;
                    }
                    case StrokeKind.Clone:
                    {
                        int sx = lx + cloneDx, sy = ly + cloneDy;
                        float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                        if ((uint)sx < (uint)ow && (uint)sy < (uint)oh)
                        {
                            byte* sp = org + (long)sy * trb + sx * 4;
                            s0 = sp[0]; s1 = sp[1]; s2 = sp[2]; s3 = sp[3];
                        }
                        dp[0] = (byte)(op[0] + (s0 - op[0]) * m + 0.5f);
                        dp[1] = (byte)(op[1] + (s1 - op[1]) * m + 0.5f);
                        dp[2] = (byte)(op[2] + (s2 - op[2]) * m + 0.5f);
                        dp[3] = (byte)(op[3] + (s3 - op[3]) * m + 0.5f);
                        break;
                    }
                    default:
                    {
                        // Source-over with the paint colour at coverage m.
                        float k = 1f - m * ca / 255f;
                        dp[0] = (byte)Math.Min(255f, cb * m + op[0] * k + 0.5f);
                        dp[1] = (byte)Math.Min(255f, cg * m + op[1] * k + 0.5f);
                        dp[2] = (byte)Math.Min(255f, cr * m + op[2] * k + 0.5f);
                        dp[3] = (byte)Math.Min(255f, ca * m + op[3] * k + 0.5f);
                        break;
                    }
                }
            }
        }
        dirty = Px.Union(dirty, rect);
        doc.Invalidate(rect);
    }

    /// <summary>Ends the stroke and records it. Returns false when nothing was painted.</summary>
    public bool Finish(string name)
    {
        if (finished) return false;
        finished = true;
        if (dirty.IsEmpty)
        {
            doc.AbortPaint(scope);
            return false;
        }
        var local = SKRectI.Create(dirty.Left - target.X, dirty.Top - target.Y, dirty.Width, dirty.Height);
        doc.EndPaint(scope, name, dirty, Px.GetRect(original, local), Px.GetRect(target.Bitmap, local));
        return true;
    }

    public void Cancel()
    {
        if (finished) return;
        finished = true;
        if (!dirty.IsEmpty)
        {
            Px.CopyAll(original, target.Bitmap);
            doc.Invalidate(dirty);
        }
        doc.AbortPaint(scope);
    }

    public void Dispose()
    {
        original.Dispose();
        coverage.Dispose();
    }
}

/// <summary>
/// An in-place edit of the whole paint target with preview: adjustments and
/// filters compute a result from <see cref="Original"/>, the session blends it
/// through the selection, and Commit records only the rectangle that changed.
/// </summary>
public sealed unsafe class PixelEditSession : IDisposable
{
    readonly Document doc;
    readonly SKBitmap? selection;
    bool done;

    public PaintTarget Target { get; }
    public SKBitmap Original { get; }
    public bool IsMask => Target.IsMask;

    PixelEditSession(Document doc, PaintTarget target)
    {
        this.doc = doc;
        Target = target;
        Original = Px.Clone(target.Bitmap);
        selection = doc.Selection;
    }

    public static PixelEditSession? Begin(Document doc)
    {
        var t = doc.GetPaintTarget();
        if (t is null) return null;
        if (!t.Value.IsMask && t.Value.Layer is RasterLayer { Text: not null } r)
            doc.Edit("Rasterize Text", () => r.Text = null);
        return new PixelEditSession(doc, t.Value);
    }

    /// <summary>Shows a computed result (same geometry as the target). Call on the UI thread.</summary>
    public void Show(SKBitmap result)
    {
        if (done) return;
        var dst = Target.Bitmap;
        if (selection is null) Px.CopyAll(result, dst);
        else
        {
            int bpp = dst.BytesPerPixel, w = dst.Width, h = dst.Height;
            byte* o = (byte*)Original.GetPixels(), r = (byte*)result.GetPixels(), d = (byte*)dst.GetPixels(), s = (byte*)selection.GetPixels();
            int rb = dst.RowBytes, sw = selection.Width, sh = selection.Height, tx = Target.X, ty = Target.Y;
            Parallel.For(0, h, y =>
            {
                int dy = y + ty;
                byte* orow = o + (long)y * rb, rrow = r + (long)y * rb, drow = d + (long)y * rb;
                byte* srow = (uint)dy < (uint)sh ? s + (long)dy * selection.RowBytes : null;
                for (int x = 0; x < w; x++)
                {
                    int dx = x + tx;
                    int m = srow != null && (uint)dx < (uint)sw ? srow[dx] : 0;
                    for (int c = 0; c < bpp; c++)
                    {
                        int i = x * bpp + c;
                        drow[i] = (byte)(orow[i] + ((rrow[i] - orow[i]) * m + 127) / 255);
                    }
                }
            });
        }
        doc.Invalidate(Target.Bounds);
    }

    public void ShowOriginal()
    {
        if (done) return;
        Px.CopyAll(Original, Target.Bitmap);
        doc.Invalidate(Target.Bounds);
    }

    public bool Commit(string name)
    {
        if (done) return false;
        done = true;
        var diff = Px.DiffBounds(Original, Target.Bitmap);
        if (diff.IsEmpty) return false;
        var docRect = SKRectI.Create(diff.Left + Target.X, diff.Top + Target.Y, diff.Width, diff.Height);
        doc.PushPatch(name, Target, docRect, Px.GetRect(Original, diff), Px.GetRect(Target.Bitmap, diff));
        return true;
    }

    public void Cancel()
    {
        if (done) return;
        ShowOriginal();
        done = true;
    }

    public void Dispose() => Original.Dispose();
}
