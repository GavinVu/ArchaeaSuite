using ArchaeaPhoto.Core;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;

namespace ArchaeaPhoto;

/// <param name="Pos">Pointer position in document pixels (fractional).</param>
/// <param name="ViewPos">Pointer position in the viewport, in screen pixels.</param>
public readonly record struct ToolEvent(Document Doc, SKPoint Pos, SKPoint ViewPos, KeyModifiers Mods, int Clicks, CanvasView View)
{
    public bool Shift => Mods.HasFlag(KeyModifiers.Shift);
    public bool Alt => Mods.HasFlag(KeyModifiers.Alt);
    public bool Ctrl => Mods.HasFlag(KeyModifiers.Control);
    public SKPointI Pixel => new((int)Math.Floor(Pos.X), (int)Math.Floor(Pos.Y));
}

public abstract class Tool
{
    protected readonly Editor Ed;
    protected Tool(Editor editor) => Ed = editor;

    public abstract string Name { get; }
    public abstract string IconData { get; }
    /// <summary>Single-key shortcut, or Key.None.</summary>
    public virtual Key Shortcut => Key.None;
    /// <summary>One line for the status bar explaining how to use the tool.</summary>
    public virtual string Hint => "";
    public virtual Cursor Cursor => new(StandardCursorType.Cross);
    /// <summary>Temporary modes (free transform) are not shown in the toolbar.</summary>
    public virtual bool Transient => false;

    public virtual void Activate() { }
    public virtual void Deactivate() => Cancel();
    public virtual void Down(ToolEvent e) { }
    public virtual void Move(ToolEvent e) { }
    public virtual void Up(ToolEvent e) { }
    public virtual void Hover(ToolEvent e) { }
    /// <summary>Returns true if the key was used.</summary>
    public virtual bool KeyDown(Key key, KeyModifiers mods) => false;
    /// <summary>Abandons an operation in progress.</summary>
    public virtual void Cancel() { }
    /// <summary>Draws over the image, in screen pixels (use <see cref="CanvasView.ToView(SKPoint)"/>).</summary>
    public virtual void Overlay(SKCanvas c, CanvasView view) { }
    /// <summary>Controls for the options bar.</summary>
    public virtual IEnumerable<Control> Options() => Array.Empty<Control>();

    protected static SKPoint Constrain45(SKPoint from, SKPoint to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        float len = MathF.Sqrt(dx * dx + dy * dy);
        return new SKPoint(from.X + (float)Math.Cos(angle) * len, from.Y + (float)Math.Sin(angle) * len);
    }

    protected static SKPoint ConstrainSquare(SKPoint from, SKPoint to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new SKPoint(from.X + (dx < 0 ? -side : side), from.Y + (dy < 0 ? -side : side));
    }
}

// ============================================================ painting tools

public abstract class PaintTool : Tool
{
    protected PaintTool(Editor editor) : base(editor) { }

    public BrushSettings Settings { get; } = new();
    protected abstract StrokeKind Kind { get; }
    Stroke? stroke;
    SKPoint? lastEnd;
    bool picking;
    Action<double>? setSize;

    // Clone stamp state.
    protected SKPoint? CloneSource;
    SKPointI cloneOffset;
    bool cloneLocked;

    public void Resize(float factor)
    {
        Settings.Size = Math.Clamp(MathF.Round(Settings.Size * factor + (factor > 1 ? 0.5f : -0.5f)), 1, 1000);
        setSize?.Invoke(Settings.Size);
        Ed.Canvas.InvalidateVisual();
    }

    public override void Down(ToolEvent e)
    {
        if (e.Alt && Kind != StrokeKind.Clone)
        {
            picking = true;
            EyedropperTool.Pick(Ed, e, false);
            return;
        }
        if (e.Alt)
        {
            CloneSource = e.Pos;
            cloneLocked = false;
            Ed.Flash("Clone source set. Paint to copy from it.");
            return;
        }
        if (e.Doc.GetPaintTarget() is not { } target)
        {
            Ed.Flash("Select a pixel layer to paint on.");
            return;
        }
        if (Kind == StrokeKind.Clone)
        {
            if (target.IsMask) { Ed.Flash("The Clone Stamp works on layer pixels, not masks."); return; }
            if (CloneSource is not { } src) { Ed.Flash("Alt-click to set the clone source first."); return; }
            if (!cloneLocked)
            {
                cloneOffset = new SKPointI((int)MathF.Round(src.X - e.Pos.X), (int)MathF.Round(src.Y - e.Pos.Y));
                cloneLocked = true;
            }
        }
        stroke = new Stroke(e.Doc, e.Doc.BeginPaint()!, Kind, Settings, Ed.Foreground, cloneOffset);
        if (e.Shift && lastEnd is { } from)
        {
            stroke.MoveTo(from);
            stroke.LineTo(e.Pos);
        }
        else stroke.MoveTo(e.Pos);
    }

    public override void Move(ToolEvent e)
    {
        if (picking) EyedropperTool.Pick(Ed, e, false);
        else stroke?.LineTo(e.Pos);
    }

    public override void Up(ToolEvent e)
    {
        picking = false;
        if (stroke is null) return;
        stroke.Finish(Name);
        stroke.Dispose();
        stroke = null;
        lastEnd = e.Pos;
    }

    public override void Cancel()
    {
        picking = false;
        stroke?.Cancel();
        stroke?.Dispose();
        stroke = null;
    }

    public override void Overlay(SKCanvas c, CanvasView view)
    {
        if (view.PointerDoc is { } p)
        {
            var centre = view.ToView(p);
            float r = Math.Max(1.5f, Settings.Size / 2f * (float)view.Zoom);
            view.OverlayStroke(c, paint => c.DrawCircle(centre, r, paint));
        }
        if (Kind == StrokeKind.Clone && CloneSource is { } src)
        {
            // While painting, the marker follows the pixels being copied.
            var at = cloneLocked && view.PointerDoc is { } q ? new SKPoint(q.X + cloneOffset.X, q.Y + cloneOffset.Y) : src;
            var v = view.ToView(at);
            float s = 7 * view.OverlayScale;
            view.OverlayStroke(c, paint =>
            {
                c.DrawLine(v.X - s, v.Y, v.X + s, v.Y, paint);
                c.DrawLine(v.X, v.Y - s, v.X, v.Y + s, paint);
            });
        }
    }

    public override IEnumerable<Control> Options()
    {
        var (size, set) = Ui.Slider("Size", 1, 400, Settings.Size, v => { Settings.Size = (float)v; Ed.Canvas.InvalidateVisual(); }, v => $"{v:0} px", 130);
        setSize = v => set(Math.Min(400, v));
        yield return size;
        yield return Ui.Slider("Hardness", 0, 100, Settings.Hardness * 100, v => Settings.Hardness = (float)v / 100f, v => $"{v:0}%").row;
        yield return Ui.Slider("Opacity", 1, 100, Settings.Opacity * 100, v => Settings.Opacity = (float)v / 100f, v => $"{v:0}%").row;
        yield return Ui.Slider("Flow", 1, 100, Settings.Flow * 100, v => Settings.Flow = (float)v / 100f, v => $"{v:0}%").row;
    }
}

public sealed class BrushTool : PaintTool
{
    public BrushTool(Editor e) : base(e) { }
    public override string Name => "Brush";
    public override string IconData => Icons.Brush;
    public override Key Shortcut => Key.B;
    public override string Hint => "Drag to paint with the foreground colour. Shift-click draws a straight line; Alt-click picks a colour.";
    protected override StrokeKind Kind => StrokeKind.Brush;
}

public sealed class EraserTool : PaintTool
{
    public EraserTool(Editor e) : base(e) { Settings.Hardness = 1f; }
    public override string Name => "Eraser";
    public override string IconData => Icons.Eraser;
    public override Key Shortcut => Key.E;
    public override string Hint => "Drag to erase to transparency. On a layer mask it hides the layer.";
    protected override StrokeKind Kind => StrokeKind.Eraser;
}

public sealed class CloneTool : PaintTool
{
    public CloneTool(Editor e) : base(e) { Settings.Size = 40; Settings.Hardness = 0.5f; }
    public override string Name => "Clone Stamp";
    public override string IconData => Icons.Stamp;
    public override Key Shortcut => Key.S;
    public override string Hint => "Alt-click to set the source, then paint to copy pixels from it.";
    protected override StrokeKind Kind => StrokeKind.Clone;
}

// =========================================================== selection tools

public abstract class SelectTool : Tool
{
    protected SelectTool(Editor editor) : base(editor) { }
    protected SelectMode ModeOption = SelectMode.Replace;
    protected float Feather;

    protected SelectMode ModeFor(ToolEvent e) =>
        e.Shift && e.Alt ? SelectMode.Intersect : e.Shift ? SelectMode.Add : e.Alt ? SelectMode.Subtract : ModeOption;

    /// <summary>Takes ownership of <paramref name="shape"/>.</summary>
    protected void Apply(Document doc, SKBitmap shape, SelectMode mode, string name)
    {
        if (Feather >= 0.5f)
        {
            var soft = Filters.GaussianBlur(shape, Feather);
            shape.Dispose();
            shape = soft;
        }
        var result = Sel.Combine(doc.Selection, shape, mode);
        shape.Dispose();
        doc.SetSelection(result, name);
    }

    protected IEnumerable<Control> ModeOptions()
    {
        var modes = new[] { SelectMode.Replace, SelectMode.Add, SelectMode.Subtract, SelectMode.Intersect };
        var labels = new[] { "New", "Add", "Subtract", "Intersect" };
        yield return Ui.Label("Mode");
        yield return Ui.Combo(modes, ModeOption, m => labels[Array.IndexOf(modes, m)], m => ModeOption = m, 112);
        yield return Ui.Slider("Feather", 0, 100, Feather, v => Feather = (float)v, v => $"{v:0} px").row;
    }
}

public sealed class MarqueeTool : SelectTool
{
    readonly bool ellipse;
    SKPoint start, current;
    SelectMode mode;
    bool dragging;

    public MarqueeTool(Editor e, bool ellipse) : base(e) => this.ellipse = ellipse;
    public override string Name => ellipse ? "Elliptical Marquee" : "Rectangular Marquee";
    public override string IconData => ellipse ? Icons.EllipseSelect : Icons.RectSelect;
    public override Key Shortcut => Key.M;
    public override string Hint => "Drag to select. Shift adds, Alt subtracts, Shift+Alt intersects. Click to deselect.";

    public override void Down(ToolEvent e)
    {
        start = current = e.Pos;
        mode = ModeFor(e);
        dragging = true;
    }

    public override void Move(ToolEvent e)
    {
        if (!dragging) return;
        // Shift is the "add" modifier at press time, so it only constrains plain selections.
        current = e.Shift && mode == SelectMode.Replace ? ConstrainSquare(start, e.Pos) : e.Pos;
    }

    SKRect Rect() => new(MathF.Round(Math.Min(start.X, current.X)), MathF.Round(Math.Min(start.Y, current.Y)),
        MathF.Round(Math.Max(start.X, current.X)), MathF.Round(Math.Max(start.Y, current.Y)));

    public override void Up(ToolEvent e)
    {
        if (!dragging) return;
        dragging = false;
        var rect = Rect();
        if (rect.Width < 1 || rect.Height < 1)
        {
            if (mode == SelectMode.Replace) e.Doc.Deselect();
            return;
        }
        using var path = new SKPath();
        if (ellipse) path.AddOval(rect);
        else path.AddRect(rect);
        Apply(e.Doc, Sel.FromPath(e.Doc.Width, e.Doc.Height, path, ellipse), mode, Name);
    }

    public override void Cancel() => dragging = false;

    public override void Overlay(SKCanvas c, CanvasView view)
    {
        if (!dragging) return;
        var r = view.ToView(Rect());
        view.OverlayStroke(c, p =>
        {
            if (ellipse) c.DrawOval(r, p);
            else c.DrawRect(r, p);
        }, dashed: true);
    }

    public override IEnumerable<Control> Options() => ModeOptions();
}

public sealed class LassoTool : SelectTool
{
    readonly List<SKPoint> points = new();
    SelectMode mode;
    bool dragging;

    public LassoTool(Editor e) : base(e) { }
    public override string Name => "Lasso";
    public override string IconData => Icons.Lasso;
    public override Key Shortcut => Key.L;
    public override string Hint => "Draw freehand around an area to select it. Shift adds, Alt subtracts.";

    public override void Down(ToolEvent e)
    {
        points.Clear();
        points.Add(e.Pos);
        mode = ModeFor(e);
        dragging = true;
    }

    public override void Move(ToolEvent e)
    {
        if (dragging && SKPoint.Distance(points[^1], e.Pos) * e.View.Zoom >= 1.5) points.Add(e.Pos);
    }

    public override void Up(ToolEvent e)
    {
        if (!dragging) return;
        dragging = false;
        if (points.Count < 3)
        {
            if (mode == SelectMode.Replace) e.Doc.Deselect();
            return;
        }
        using var path = new SKPath();
        path.AddPoly(points.ToArray(), true);
        Apply(e.Doc, Sel.FromPath(e.Doc.Width, e.Doc.Height, path), mode, Name);
        points.Clear();
    }

    public override void Cancel()
    {
        dragging = false;
        points.Clear();
    }

    public override void Overlay(SKCanvas c, CanvasView view)
    {
        if (!dragging || points.Count < 2) return;
        using var path = new SKPath();
        path.AddPoly(points.Select(view.ToView).ToArray(), false);
        view.OverlayStroke(c, p => c.DrawPath(path, p));
    }

    public override IEnumerable<Control> Options() => ModeOptions();
}

public sealed class WandTool : SelectTool
{
    int tolerance = 32;
    bool contiguous = true, sampleAll;

    public WandTool(Editor e) : base(e) { }
    public override string Name => "Magic Wand";
    public override string IconData => Icons.Wand;
    public override Key Shortcut => Key.W;
    public override string Hint => "Click to select an area of similar colour. Shift adds, Alt subtracts.";

    public override void Down(ToolEvent e)
    {
        var region = Draw.FloodRegion(e.Doc, e.Pixel.X, e.Pixel.Y, tolerance, contiguous, sampleAll);
        if (region is null) return;
        Apply(e.Doc, region, ModeFor(e), Name);
    }

    public override IEnumerable<Control> Options()
    {
        foreach (var c in ModeOptions()) yield return c;
        yield return Ui.Divider();
        yield return Ui.Slider("Tolerance", 0, 255, tolerance, v => tolerance = (int)v).row;
        yield return Ui.Check("Contiguous", contiguous, v => contiguous = v);
        yield return Ui.Check("All layers", sampleAll, v => sampleAll = v);
    }
}

// ================================================================ simple tools

public sealed class EyedropperTool : Tool
{
    public EyedropperTool(Editor e) : base(e) { }
    public override string Name => "Eyedropper";
    public override string IconData => Icons.Eyedropper;
    public override Key Shortcut => Key.I;
    public override string Hint => "Click the image to set the foreground colour. Alt-click sets the background colour.";

    public static void Pick(Editor ed, ToolEvent e, bool background)
    {
        var c = Px.GetColor(e.Doc.GetComposite(), e.Pixel.X, e.Pixel.Y);
        if (c.Alpha == 0) return;
        if (background) ed.SetBackground(c);
        else ed.SetForeground(c);
    }

    public override void Down(ToolEvent e) => Pick(Ed, e, e.Alt);
    public override void Move(ToolEvent e) => Pick(Ed, e, e.Alt);
}

public sealed class BucketTool : Tool
{
    int tolerance = 32;
    bool contiguous = true, sampleAll;

    public BucketTool(Editor e) : base(e) { }
    public override string Name => "Paint Bucket";
    public override string IconData => Icons.Bucket;
    public override Key Shortcut => Key.G;
    public override string Hint => "Click to fill an area of similar colour with the foreground colour.";

    public override void Down(ToolEvent e)
    {
        if (e.Doc.GetPaintTarget() is null) { Ed.Flash("Select a pixel layer to fill."); return; }
        Draw.Bucket(e.Doc, e.Pixel.X, e.Pixel.Y, Ed.Foreground, tolerance, contiguous, sampleAll);
    }

    public override IEnumerable<Control> Options()
    {
        yield return Ui.Slider("Tolerance", 0, 255, tolerance, v => tolerance = (int)v).row;
        yield return Ui.Check("Contiguous", contiguous, v => contiguous = v);
        yield return Ui.Check("All layers", sampleAll, v => sampleAll = v);
    }
}

public sealed class GradientTool : Tool
{
    bool radial, toTransparent, dragging;
    SKPoint start, current;

    public GradientTool(Editor e) : base(e) { }
    public override string Name => "Gradient";
    public override string IconData => Icons.Gradient;
    public override Key Shortcut => Key.G;
    public override string Hint => "Drag to draw a gradient from the foreground colour. Shift snaps to 45°.";

    public override void Down(ToolEvent e)
    {
        if (e.Doc.GetPaintTarget() is null) { Ed.Flash("Select a pixel layer to draw a gradient on."); return; }
        start = current = e.Pos;
        dragging = true;
    }

    public override void Move(ToolEvent e)
    {
        if (dragging) current = e.Shift ? Constrain45(start, e.Pos) : e.Pos;
    }

    public override void Up(ToolEvent e)
    {
        if (!dragging) return;
        dragging = false;
        if (SKPoint.Distance(start, current) < 1) return;
        Draw.Gradient(e.Doc, start, current, Ed.Foreground, toTransparent ? Ed.Foreground.WithAlpha(0) : Ed.Background, radial);
    }

    public override void Cancel() => dragging = false;

    public override void Overlay(SKCanvas c, CanvasView view)
    {
        if (!dragging) return;
        SKPoint a = view.ToView(start), b = view.ToView(current);
        view.OverlayStroke(c, p =>
        {
            c.DrawLine(a, b, p);
            c.DrawCircle(a, 4 * view.OverlayScale, p);
        });
    }

    public override IEnumerable<Control> Options()
    {
        yield return Ui.Label("Type");
        yield return Ui.Combo(new[] { false, true }, radial, v => v ? "Radial" : "Linear", v => radial = v, 104);
        yield return Ui.Label("Colours");
        yield return Ui.Combo(new[] { false, true }, toTransparent, v => v ? "Foreground to transparent" : "Foreground to background", v => toTransparent = v, 210);
    }
}

public sealed class ShapeTool : Tool
{
    ShapeKind kind = ShapeKind.Rectangle;
    int style; // 0 fill, 1 outline, 2 both
    float strokeWidth = 6;
    bool dragging;
    SKPoint start, current;

    public ShapeTool(Editor e) : base(e) { }
    public override string Name => "Shape";
    public override string IconData => Icons.Shape;
    public override Key Shortcut => Key.U;
    public override string Hint => "Drag to draw a shape on a new layer. Shift keeps it square, round or at 45°.";

    SKColor? Fill => kind != ShapeKind.Line && style != 1 ? Ed.Foreground : null;
    SKColor? Stroke => kind == ShapeKind.Line ? Ed.Foreground : style == 1 ? Ed.Foreground : style == 2 ? Ed.Background : null;

    public override void Down(ToolEvent e)
    {
        start = current = e.Pos;
        dragging = true;
    }

    public override void Move(ToolEvent e)
    {
        if (!dragging) return;
        current = !e.Shift ? e.Pos : kind == ShapeKind.Line ? Constrain45(start, e.Pos) : ConstrainSquare(start, e.Pos);
    }

    public override void Up(ToolEvent e)
    {
        if (!dragging) return;
        dragging = false;
        Draw.Shape(e.Doc, kind, start, current, Fill, Stroke, strokeWidth);
    }

    public override void Cancel() => dragging = false;

    public override void Overlay(SKCanvas c, CanvasView view)
    {
        if (!dragging) return;
        SKPoint a = view.ToView(start), b = view.ToView(current);
        var r = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        using var p = new SKPaint { IsAntialias = true };
        void Render()
        {
            if (kind == ShapeKind.Rectangle) c.DrawRect(r, p);
            else if (kind == ShapeKind.Ellipse) c.DrawOval(r, p);
            else c.DrawLine(a, b, p);
        }
        if (Fill is { } f) { p.Style = SKPaintStyle.Fill; p.Color = f; Render(); }
        if (Stroke is { } s)
        {
            p.Style = SKPaintStyle.Stroke;
            p.StrokeWidth = Math.Max(1, strokeWidth * (float)view.Zoom);
            p.Color = s;
            Render();
        }
    }

    public override IEnumerable<Control> Options()
    {
        yield return Ui.Label("Shape");
        yield return Ui.Combo(Enum.GetValues<ShapeKind>(), kind, k => k.ToString(), k => kind = k, 116);
        yield return Ui.Label("Style");
        yield return Ui.Combo(new[] { 0, 1, 2 }, style, s => s == 0 ? "Fill" : s == 1 ? "Outline" : "Fill + outline", s => style = s, 124);
        yield return Ui.Slider("Line width", 1, 100, strokeWidth, v => strokeWidth = (float)v, v => $"{v:0} px").row;
    }
}

public sealed class HandTool : Tool
{
    public HandTool(Editor e) : base(e) { }
    public override string Name => "Hand";
    public override string IconData => Icons.Hand;
    public override Key Shortcut => Key.H;
    public override string Hint => "Drag to move around the image. Hold Space with any tool to do the same.";
    public override Cursor Cursor => new(StandardCursorType.Hand);
}

public sealed class ZoomTool : Tool
{
    public ZoomTool(Editor e) : base(e) { }
    public override string Name => "Zoom";
    public override string IconData => Icons.Zoom;
    public override Key Shortcut => Key.Z;
    public override string Hint => "Click to zoom in, Alt-click to zoom out. Ctrl+scroll zooms with any tool.";
    public override void Down(ToolEvent e) => e.View.ZoomStep(!e.Alt, e.ViewPos);
}
