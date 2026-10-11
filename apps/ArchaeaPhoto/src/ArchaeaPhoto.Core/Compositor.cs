using SkiaSharp;

namespace ArchaeaPhoto.Core;

/// <summary>Draws the layer stack. The canvas is in document coordinates.</summary>
public static class Compositor
{
    public static void Draw(SKCanvas canvas, Document doc)
    {
        foreach (var layer in doc.Root.Children) DrawLayer(canvas, layer);
    }

    public static void DrawLayer(SKCanvas canvas, Layer layer)
    {
        if (!layer.Visible || layer.Opacity <= 0f) return;
        Draw(canvas, layer, layer.Blend.ToSkia(), layer.Opacity);
    }

    /// <summary>Draws one layer (with its mask) as if it were alone, opaque and in Normal mode.</summary>
    public static void DrawIsolated(SKCanvas canvas, Layer layer) => Draw(canvas, layer, SKBlendMode.SrcOver, 1f);

    static void Draw(SKCanvas canvas, Layer layer, SKBlendMode blend, float opacity)
    {
        using var paint = new SKPaint
        {
            BlendMode = blend,
            Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255f)),
        };
        bool masked = layer.Mask is not null && layer.MaskEnabled;

        if (layer is RasterLayer r)
        {
            if (!masked)
            {
                Px.Draw(canvas, r.Bitmap, r.X, r.Y, paint);
                return;
            }
            canvas.SaveLayer(r.Bounds, paint);
            Px.Draw(canvas, r.Bitmap, r.X, r.Y);
            ApplyMask(canvas, layer);
            canvas.Restore();
        }
        else if (layer is GroupLayer g)
        {
            // Groups composite in isolation, then blend as one image.
            canvas.SaveLayer(paint);
            foreach (var child in g.Children) DrawLayer(canvas, child);
            if (masked) ApplyMask(canvas, layer);
            canvas.Restore();
        }
    }

    static void ApplyMask(SKCanvas canvas, Layer layer)
    {
        canvas.Save();
        canvas.ClipRect(layer.MaskBounds); // outside the mask bitmap everything stays revealed
        Px.DrawMask(canvas, layer.Mask!, layer.MaskX, layer.MaskY, SKBlendMode.DstIn);
        canvas.Restore();
    }

    /// <summary>Renders one layer's pixels (no mask, blend or opacity) into a canvas-sized bitmap.</summary>
    public static SKBitmap LayerOnCanvas(Document doc, RasterLayer layer)
    {
        var bmp = Px.NewColor(doc.Width, doc.Height);
        using var c = new SKCanvas(bmp);
        using var p = new SKPaint { BlendMode = SKBlendMode.Src };
        Px.Draw(c, layer.Bitmap, layer.X, layer.Y, p);
        return bmp;
    }
}
