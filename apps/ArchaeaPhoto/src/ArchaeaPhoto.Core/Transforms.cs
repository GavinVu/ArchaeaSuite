using SkiaSharp;

namespace ArchaeaPhoto.Core;

/// <summary>Geometric operations on layers and on the whole document.</summary>
public static class Transforms
{
    /// <summary>
    /// Resamples a bitmap through <paramref name="matrix"/> (bitmap pixels → document).
    /// Returns the new bitmap and where its top-left corner sits in the document.
    /// </summary>
    public static (SKBitmap bitmap, int x, int y) Render(SKBitmap src, SKMatrix matrix, byte maskFill = 0, bool fast = false)
    {
        var mapped = matrix.MapRect(new SKRect(0, 0, src.Width, src.Height));
        // Snap bounds that are integral up to rounding noise, so exact flips and
        // quarter turns do not gain a transparent fringe.
        var bounds = new SKRectI((int)Math.Floor(mapped.Left + 0.001f), (int)Math.Floor(mapped.Top + 0.001f),
            (int)Math.Ceiling(mapped.Right - 0.001f), (int)Math.Ceiling(mapped.Bottom - 0.001f));
        if (bounds.Width < 1) bounds.Right = bounds.Left + 1;
        if (bounds.Height < 1) bounds.Bottom = bounds.Top + 1;
        bool color = src.ColorType != SKColorType.Alpha8;
        var dst = color ? Px.NewColor(bounds.Width, bounds.Height) : Px.NewMask(bounds.Width, bounds.Height, maskFill);
        using var c = new SKCanvas(dst);
        c.Translate(-bounds.Left, -bounds.Top);
        c.Concat(ref matrix);
        // Flips, quarter turns and whole-pixel moves copy pixels exactly; anything else is filtered.
        bool exact = IsPixelExact(matrix);
        var quality = exact ? SKFilterQuality.None : fast ? SKFilterQuality.Low : SKFilterQuality.High;
        if (color)
        {
            using var p = new SKPaint { FilterQuality = quality, IsAntialias = !exact, BlendMode = SKBlendMode.Src };
            Px.Draw(c, src, 0, 0, p);
        }
        else Px.DrawMask(c, src, 0, 0, SKBlendMode.Src, quality, !exact);
        return (dst, bounds.Left, bounds.Top);
    }

    static bool IsPixelExact(SKMatrix m)
    {
        static bool Unit(float v) => Math.Abs(Math.Abs(v) - 1f) < 1e-5f;
        static bool Zero(float v) => Math.Abs(v) < 1e-5f;
        static bool Whole(float v) => Math.Abs(v - MathF.Round(v)) < 1e-3f;
        bool axis = Unit(m.ScaleX) && Unit(m.ScaleY) && Zero(m.SkewX) && Zero(m.SkewY);
        bool turned = Zero(m.ScaleX) && Zero(m.ScaleY) && Unit(m.SkewX) && Unit(m.SkewY);
        return (axis || turned) && Whole(m.TransX) && Whole(m.TransY) && Zero(m.Persp0) && Zero(m.Persp1);
    }

    /// <summary>Resamples a layer, its mask and (for groups) its children through a document-space matrix. Replaces bitmaps.</summary>
    public static void TransformLayer(Layer layer, SKMatrix m, bool translationOnly = false, bool fast = false)
    {
        if (layer.Mask is not null)
        {
            var (mask, mx, my) = Render(layer.Mask, SKMatrix.CreateTranslation(layer.MaskX, layer.MaskY).PostConcat(m), 255, fast);
            (layer.Mask, layer.MaskX, layer.MaskY) = (mask, mx, my);
        }
        if (layer is RasterLayer r)
        {
            var (bmp, x, y) = Render(r.Bitmap, SKMatrix.CreateTranslation(r.X, r.Y).PostConcat(m), 0, fast);
            (r.Bitmap, r.X, r.Y) = (bmp, x, y);
            if (!translationOnly) r.Text = null;
        }
        else if (layer is GroupLayer g)
        {
            foreach (var child in g.Children) TransformLayer(child, m, translationOnly, fast);
        }
    }

    /// <summary>Shifts a layer (and its mask, and a group's children) without resampling.</summary>
    public static void Offset(Layer layer, int dx, int dy)
    {
        if (layer.Mask is not null) { layer.MaskX += dx; layer.MaskY += dy; }
        if (layer is RasterLayer r) { r.X += dx; r.Y += dy; }
        else if (layer is GroupLayer g)
            foreach (var child in g.Children) Offset(child, dx, dy);
    }

    /// <summary>Applies a matrix (document → document) to the active layer as one undo step.</summary>
    public static void TransformActive(Document doc, SKMatrix m, string name)
    {
        if (doc.Active is not { } layer) return;
        doc.Edit(name, () => TransformLayer(layer, m, false));
    }

    public static void FlipActive(Document doc, bool horizontal)
    {
        if (doc.Active is not { } layer) return;
        var b = Document.ContentBounds(layer);
        if (b.IsEmpty) return;
        float cx = (b.Left + b.Right) / 2f, cy = (b.Top + b.Bottom) / 2f;
        TransformActive(doc, horizontal ? SKMatrix.CreateScale(-1, 1, cx, cy) : SKMatrix.CreateScale(1, -1, cx, cy),
            horizontal ? "Flip Horizontal" : "Flip Vertical");
    }

    public static void RotateActive(Document doc, float degrees)
    {
        if (doc.Active is not { } layer) return;
        var b = Document.ContentBounds(layer);
        if (b.IsEmpty) return;
        TransformActive(doc, SKMatrix.CreateRotationDegrees(degrees, (b.Left + b.Right) / 2f, (b.Top + b.Bottom) / 2f), "Rotate Layer");
    }

    // ---------------------------------------------------------- whole document

    static void TransformDocument(Document doc, string name, SKMatrix m, int width, int height)
    {
        doc.Edit(name, () =>
        {
            foreach (var layer in doc.Root.Children) TransformLayer(layer, m, false);
            doc.SetSizeInEdit(width, height);
            doc.ReplaceSelectionInEdit(null);
        });
    }

    /// <summary>Resamples every layer so the canvas becomes width × height.</summary>
    public static void ResizeImage(Document doc, int width, int height)
    {
        width = Math.Clamp(width, 1, Document.MaxSize);
        height = Math.Clamp(height, 1, Document.MaxSize);
        if (width == doc.Width && height == doc.Height) return;
        TransformDocument(doc, "Image Size", SKMatrix.CreateScale(width / (float)doc.Width, height / (float)doc.Height), width, height);
    }

    /// <summary>
    /// Changes the canvas size without resampling. Anchors are 0, 0.5 or 1: where
    /// the existing image stays put (0 = left/top, 0.5 = centre, 1 = right/bottom).
    /// </summary>
    public static void ResizeCanvas(Document doc, int width, int height, float anchorX, float anchorY)
    {
        width = Math.Clamp(width, 1, Document.MaxSize);
        height = Math.Clamp(height, 1, Document.MaxSize);
        if (width == doc.Width && height == doc.Height) return;
        int dx = (int)Math.Round((width - doc.Width) * anchorX), dy = (int)Math.Round((height - doc.Height) * anchorY);
        Reframe(doc, "Canvas Size", dx, dy, width, height);
    }

    /// <summary>Crops the canvas to a rectangle. Pixels outside it are kept on their layers.</summary>
    public static void Crop(Document doc, SKRectI rect)
    {
        rect = Px.Intersect(rect, doc.Bounds);
        if (rect.IsEmpty || rect == doc.Bounds) return;
        Reframe(doc, "Crop", -rect.Left, -rect.Top, rect.Width, rect.Height);
    }

    static void Reframe(Document doc, string name, int dx, int dy, int width, int height)
    {
        doc.Edit(name, () =>
        {
            foreach (var layer in doc.Root.Children) Offset(layer, dx, dy);
            doc.SetSizeInEdit(width, height);
            doc.ReplaceSelectionInEdit(null);
        });
    }

    /// <summary>Rotates the whole image by a multiple of 90 degrees clockwise.</summary>
    public static void RotateCanvas(Document doc, int quarterTurns)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0) return;
        int w = doc.Width, h = doc.Height;
        var (m, nw, nh) = quarterTurns switch
        {
            1 => (SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(h, 0)), h, w),
            2 => (SKMatrix.CreateRotationDegrees(180).PostConcat(SKMatrix.CreateTranslation(w, h)), w, h),
            _ => (SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, w)), h, w),
        };
        TransformDocument(doc, "Rotate Canvas", m, nw, nh);
    }

    public static void FlipCanvas(Document doc, bool horizontal)
    {
        var m = horizontal ? SKMatrix.CreateScale(-1, 1).PostConcat(SKMatrix.CreateTranslation(doc.Width, 0))
                           : SKMatrix.CreateScale(1, -1).PostConcat(SKMatrix.CreateTranslation(0, doc.Height));
        TransformDocument(doc, horizontal ? "Flip Canvas Horizontal" : "Flip Canvas Vertical", m, doc.Width, doc.Height);
    }
}
