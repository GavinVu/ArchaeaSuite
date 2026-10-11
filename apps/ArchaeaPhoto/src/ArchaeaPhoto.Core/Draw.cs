using SkiaSharp;

namespace ArchaeaPhoto.Core;

public enum ShapeKind { Rectangle, Ellipse, Line }

/// <summary>Fill, gradient, shape and text operations.</summary>
public static unsafe class Draw
{
    /// <summary>
    /// Draws onto the paint target through the selection (and an optional extra
    /// mask) as one undo step. <paramref name="draw"/> receives a canvas in
    /// document coordinates. With <paramref name="erase"/> the drawn coverage is
    /// removed from the target instead of painted onto it.
    /// </summary>
    public static bool Paint(Document doc, string name, Action<SKCanvas> draw, SKBitmap? clip = null, bool erase = false)
    {
        if (doc.GetPaintTarget() is null) return false;
        using var tmp = Px.NewColor(doc.Width, doc.Height);
        using (var c = new SKCanvas(tmp))
        {
            draw(c);
            if (clip is not null) Px.DrawMask(c, clip, 0, 0, SKBlendMode.DstIn);
            if (doc.Selection is not null) Px.DrawMask(c, doc.Selection, 0, 0, SKBlendMode.DstIn);
        }
        var area = AlphaBounds(tmp);
        if (area.IsEmpty) return false;
        var scope = doc.BeginPaint()!;
        var target = scope.Target;
        var local = SKRectI.Create(area.Left - target.X, area.Top - target.Y, area.Width, area.Height);
        var before = Px.GetRect(target.Bitmap, local);

        if (target.IsMask)
        {
            byte* m = (byte*)target.Bitmap.GetPixels();
            byte* t = (byte*)tmp.GetPixels();
            for (int y = area.Top; y < area.Bottom; y++)
            {
                byte* trow = t + (long)y * tmp.RowBytes;
                byte* mrow = m + (long)(y - target.Y) * target.Bitmap.RowBytes - target.X;
                for (int x = area.Left; x < area.Right; x++)
                {
                    int a = trow[x * 4 + 3];
                    if (a == 0) continue;
                    // Premultiplied luma is already value * coverage.
                    int lumaPremul = (trow[x * 4 + 2] * 54 + trow[x * 4 + 1] * 183 + trow[x * 4] * 19) >> 8;
                    int v = erase ? 0 : lumaPremul;
                    mrow[x] = (byte)Math.Clamp((mrow[x] * (255 - a) + 127) / 255 + v, 0, 255);
                }
            }
        }
        else
        {
            using var c = new SKCanvas(target.Bitmap);
            c.Translate(-target.X, -target.Y);
            using var p = new SKPaint { BlendMode = erase ? SKBlendMode.DstOut : SKBlendMode.SrcOver };
            Px.Draw(c, tmp, 0, 0, p);
        }
        doc.Invalidate(area);
        doc.EndPaint(scope, name, area, before, Px.GetRect(target.Bitmap, local));
        return true;
    }

    /// <summary>Bounding box of every pixel that is not fully transparent.</summary>
    public static SKRectI AlphaBounds(SKBitmap b)
    {
        int w = b.Width, h = b.Height, top = -1, bottom = -1, left = w, right = -1;
        byte* p = (byte*)b.GetPixels();
        for (int y = 0; y < h; y++)
        {
            byte* row = p + (long)y * b.RowBytes;
            int first = -1, last = -1;
            for (int x = 0; x < w; x++)
                if (row[x * 4 + 3] != 0) { first = x; break; }
            if (first < 0) continue;
            for (int x = w - 1; x >= first; x--)
                if (row[x * 4 + 3] != 0) { last = x; break; }
            if (top < 0) top = y;
            bottom = y;
            if (first < left) left = first;
            if (last > right) right = last;
        }
        return top < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
    }

    /// <summary>Fills the selection (or the whole canvas) with a colour.</summary>
    public static bool Fill(Document doc, SKColor color, string name = "Fill") =>
        Paint(doc, name, c => c.Clear(color));

    /// <summary>Erases the selection (or the whole canvas) on the paint target.</summary>
    public static bool Clear(Document doc) =>
        Paint(doc, "Clear", c => c.Clear(SKColors.Black), erase: true);

    /// <summary>Paint-bucket fill from a point.</summary>
    public static bool Bucket(Document doc, int x, int y, SKColor color, int tolerance, bool contiguous, bool sampleAllLayers)
    {
        using var region = FloodRegion(doc, x, y, tolerance, contiguous, sampleAllLayers);
        return region is not null && Paint(doc, "Paint Bucket", c => c.Clear(color), region);
    }

    /// <summary>The canvas-sized mask a flood from (x, y) reaches, or null without a usable source.</summary>
    public static SKBitmap? FloodRegion(Document doc, int x, int y, int tolerance, bool contiguous, bool sampleAllLayers)
    {
        if (!doc.Bounds.Contains(x, y)) return null;
        if (sampleAllLayers || doc.GetPaintTarget() is not { IsMask: false } target)
            return Sel.Flood(doc.GetComposite(), x, y, tolerance, contiguous);
        using var src = Compositor.LayerOnCanvas(doc, (RasterLayer)target.Layer);
        return Sel.Flood(src, x, y, tolerance, contiguous);
    }

    public static bool Gradient(Document doc, SKPoint from, SKPoint to, SKColor start, SKColor end, bool radial)
    {
        if (from == to) return false;
        return Paint(doc, "Gradient", c =>
        {
            var colors = new[] { start, end };
            using var shader = radial
                ? SKShader.CreateRadialGradient(from, SKPoint.Distance(from, to), colors, null, SKShaderTileMode.Clamp)
                : SKShader.CreateLinearGradient(from, to, colors, null, SKShaderTileMode.Clamp);
            using var p = new SKPaint { Shader = shader, IsDither = true };
            c.DrawPaint(p);
        });
    }

    /// <summary>Draws a shape on a new layer sized to fit it.</summary>
    public static RasterLayer? Shape(Document doc, ShapeKind kind, SKPoint a, SKPoint b, SKColor? fill, SKColor? stroke, float strokeWidth)
    {
        strokeWidth = Math.Max(1f, strokeWidth);
        if (kind == ShapeKind.Line) { fill = null; stroke ??= SKColors.Black; }
        if (fill is null && stroke is null) return null;
        var rect = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        if (kind != ShapeKind.Line && (rect.Width < 1 || rect.Height < 1)) return null;
        if (kind == ShapeKind.Line && SKPoint.Distance(a, b) < 1) return null;
        float pad = stroke is null ? 1 : strokeWidth / 2f + 2;
        var bounds = Px.RoundOut(SKRect.Inflate(rect, pad, pad));
        var bmp = Px.NewColor(bounds.Width, bounds.Height);
        using (var c = new SKCanvas(bmp))
        {
            c.Translate(-bounds.Left, -bounds.Top);
            using var p = new SKPaint { IsAntialias = true };
            void Render()
            {
                switch (kind)
                {
                    case ShapeKind.Rectangle: c.DrawRect(rect, p); break;
                    case ShapeKind.Ellipse: c.DrawOval(rect, p); break;
                    default: c.DrawLine(a, b, p); break;
                }
            }
            if (fill is { } f)
            {
                p.Style = SKPaintStyle.Fill;
                p.Color = f;
                Render();
            }
            if (stroke is { } s)
            {
                p.Style = SKPaintStyle.Stroke;
                p.StrokeWidth = strokeWidth;
                p.StrokeCap = SKStrokeCap.Butt;
                p.Color = s;
                Render();
            }
        }
        return doc.AddLayer(doc.NextName(kind.ToString()), bmp, bounds.Left, bounds.Top, "Draw " + kind);
    }

    // ------------------------------------------------------------------- text

    const int TextPad = 4;

    /// <summary>Rasterises text. The bitmap is padded by <see cref="TextPad"/> on every side.</summary>
    public static SKBitmap RenderText(TextInfo t)
    {
        using var typeface = SKTypeface.FromFamilyName(t.Family,
            t.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            t.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright) ?? SKTypeface.Default;
        using var p = new SKPaint
        {
            Typeface = typeface, TextSize = Math.Max(1f, t.Size), IsAntialias = true, Color = new SKColor(t.Color),
            SubpixelText = true,
        };
        var lines = t.Text.Replace("\r", "").Split('\n');
        var m = p.FontMetrics;
        float lineHeight = p.FontSpacing;
        float width = lines.Max(l => p.MeasureText(l));
        // Italic glyphs lean past their advance; leave room.
        int w = (int)Math.Ceiling(width + t.Size * 0.3f) + TextPad * 2;
        int h = (int)Math.Ceiling(lineHeight * lines.Length) + TextPad * 2;
        var bmp = Px.NewColor(w, h);
        using var c = new SKCanvas(bmp);
        float y = TextPad - m.Ascent;
        foreach (var line in lines)
        {
            c.DrawText(line, TextPad, y, p);
            y += lineHeight;
        }
        return bmp;
    }

    /// <summary>Adds a text layer whose text block starts at (x, y).</summary>
    public static RasterLayer AddText(Document doc, TextInfo text, float x, float y)
    {
        var layer = new RasterLayer(RenderText(text), (int)Math.Round(x) - TextPad, (int)Math.Round(y) - TextPad)
        {
            Name = TextLayerName(text.Text), Text = text,
        };
        return doc.Add(layer, "Add Text");
    }

    /// <summary>Re-renders an existing text layer in place (keeps its position).</summary>
    public static void SetText(RasterLayer layer, TextInfo text)
    {
        layer.Bitmap = RenderText(text);
        layer.Text = text;
        layer.Name = TextLayerName(text.Text);
    }

    static string TextLayerName(string text)
    {
        var first = text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Text";
        return first.Length > 24 ? first[..24] + "…" : first;
    }

    public static IReadOnlyList<string> FontFamilies() =>
        SKFontManager.Default.FontFamilies.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
}
