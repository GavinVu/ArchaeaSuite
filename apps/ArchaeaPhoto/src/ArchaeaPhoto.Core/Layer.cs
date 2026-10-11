using SkiaSharp;

namespace ArchaeaPhoto.Core;

public enum BlendMode
{
    Normal, Darken, Multiply, ColorBurn, Lighten, Screen, ColorDodge, Add,
    Overlay, SoftLight, HardLight, Difference, Exclusion, Hue, Saturation, Color, Luminosity,
}

public static class BlendModes
{
    public static SKBlendMode ToSkia(this BlendMode m) => m switch
    {
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.Add => SKBlendMode.Plus,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };

    public static string Label(this BlendMode m) => m switch
    {
        BlendMode.ColorBurn => "Color Burn",
        BlendMode.ColorDodge => "Color Dodge",
        BlendMode.SoftLight => "Soft Light",
        BlendMode.HardLight => "Hard Light",
        _ => m.ToString(),
    };
}

/// <summary>Editable text carried by a raster layer until it is painted on or transformed.</summary>
public sealed record TextInfo(string Text, string Family, float Size, bool Bold, bool Italic, uint Color);

public abstract class Layer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;
    /// <summary>0..1</summary>
    public float Opacity { get; set; } = 1f;
    public BlendMode Blend { get; set; } = BlendMode.Normal;
    public GroupLayer? Parent { get; internal set; }

    /// <summary>Alpha8 mask in document coordinates at (MaskX, MaskY). Outside it the layer is fully revealed.</summary>
    public SKBitmap? Mask { get; set; }
    public int MaskX { get; set; }
    public int MaskY { get; set; }
    public bool MaskEnabled { get; set; } = true;

    public SKRectI MaskBounds => Mask is null ? SKRectI.Empty : SKRectI.Create(MaskX, MaskY, Mask.Width, Mask.Height);

    /// <summary>Grows the mask (with "revealed") so it covers <paramref name="rect"/>. Replaces the bitmap.</summary>
    public void EnsureMaskCovers(SKRectI rect)
    {
        if (Mask is null || MaskBounds.Contains(rect)) return;
        var u = Px.Union(MaskBounds, rect);
        var grown = Px.NewMask(u.Width, u.Height, 255);
        using (var c = new SKCanvas(grown))
            Px.DrawMask(c, Mask, MaskX - u.Left, MaskY - u.Top, SKBlendMode.Src);
        Mask = grown;
        MaskX = u.Left;
        MaskY = u.Top;
    }

    public abstract Layer DeepClone();

    protected void CopyBaseTo(Layer l)
    {
        l.Name = Name;
        l.Visible = Visible;
        l.Opacity = Opacity;
        l.Blend = Blend;
        l.Mask = Mask is null ? null : Px.Clone(Mask);
        l.MaskX = MaskX;
        l.MaskY = MaskY;
        l.MaskEnabled = MaskEnabled;
    }
}

public sealed class RasterLayer : Layer
{
    public SKBitmap Bitmap { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public TextInfo? Text { get; set; }

    public RasterLayer(SKBitmap bitmap, int x = 0, int y = 0)
    {
        Bitmap = bitmap;
        X = x;
        Y = y;
    }

    public SKRectI Bounds => SKRectI.Create(X, Y, Bitmap.Width, Bitmap.Height);

    /// <summary>Grows the bitmap (with transparency) so it covers <paramref name="rect"/>. Replaces the bitmap.</summary>
    public void EnsureCovers(SKRectI rect)
    {
        if (Bounds.Contains(rect)) return;
        var u = Px.Union(Bounds, rect);
        var grown = Px.NewColor(u.Width, u.Height);
        using (var c = new SKCanvas(grown))
        using (var p = new SKPaint { BlendMode = SKBlendMode.Src })
            Px.Draw(c, Bitmap, X - u.Left, Y - u.Top, p);
        Bitmap = grown;
        X = u.Left;
        Y = u.Top;
    }

    public override Layer DeepClone()
    {
        var l = new RasterLayer(Px.Clone(Bitmap), X, Y) { Text = Text };
        CopyBaseTo(l);
        return l;
    }
}

public sealed class GroupLayer : Layer
{
    /// <summary>Bottom-most first.</summary>
    public List<Layer> Children { get; } = new();
    public bool Expanded { get; set; } = true;

    public void Insert(int index, Layer l)
    {
        l.Parent = this;
        Children.Insert(Math.Clamp(index, 0, Children.Count), l);
    }

    public override Layer DeepClone()
    {
        var g = new GroupLayer { Expanded = Expanded };
        CopyBaseTo(g);
        foreach (var c in Children) g.Insert(g.Children.Count, c.DeepClone());
        return g;
    }

    /// <summary>This group's descendants, top-most first, depth-first.</summary>
    public IEnumerable<Layer> Descendants()
    {
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            yield return Children[i];
            if (Children[i] is GroupLayer g)
                foreach (var d in g.Descendants()) yield return d;
        }
    }
}
