using ArchaeaPhoto.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Transforms = ArchaeaPhoto.Core.Transforms;

namespace ArchaeaPhoto;

// ======================================================================= move

/// <summary>Moves the active layer, or the selected pixels when there is a selection.</summary>
public sealed class MoveTool : Tool
{
    Document? doc;
    Layer? layer;
    SKPoint start;
    int dx, dy, appliedX, appliedY;
    bool dragging, floating;
    SKBitmap? original, basePixels, floatPixels, scratch;
    int originalX, originalY;
    SKRectI frame; // where the moved pixels live: the layer and the canvas together

    public MoveTool(Editor e) : base(e) { }
    public override string Name => "Move";
    public override string IconData => Icons.Move;
    public override Key Shortcut => Key.V;
    public override Cursor Cursor => new(StandardCursorType.SizeAll);
    public override string Hint => "Drag to move the active layer, or the selected pixels. Arrow keys nudge; Shift keeps to one axis.";

    public override void Down(ToolEvent e)
    {
        if (e.Doc.Active is not { } active) { Ed.Flash("Select a layer to move."); return; }
        doc = e.Doc;
        layer = active;
        start = e.Pos;
        dx = dy = appliedX = appliedY = 0;
        dragging = true;
        floating = doc.HasSelection && !doc.EditMask && active is RasterLayer { Text: null };
        if (!floating) return;

        // The layer's own bitmap is left alone until the move is committed; the
        // preview is composed in a scratch bitmap big enough for layer and canvas.
        var r = (RasterLayer)active;
        (original, originalX, originalY) = (r.Bitmap, r.X, r.Y);
        frame = Px.Union(r.Bounds, doc.Bounds);
        // What stays behind: the layer with the selected pixels removed.
        basePixels = Px.NewColor(frame.Width, frame.Height);
        using (var c = new SKCanvas(basePixels))
        {
            c.Translate(-frame.Left, -frame.Top);
            Px.Draw(c, original, r.X, r.Y);
            Px.DrawMask(c, doc.Selection!, 0, 0, SKBlendMode.DstOut);
        }
        // What moves: only the selected pixels.
        floatPixels = Px.NewColor(frame.Width, frame.Height);
        using (var c = new SKCanvas(floatPixels))
        {
            c.Translate(-frame.Left, -frame.Top);
            c.ClipRect(doc.Bounds);
            Px.Draw(c, original, r.X, r.Y);
            Px.DrawMask(c, doc.Selection!, 0, 0, SKBlendMode.DstIn);
        }
        scratch = Px.NewColor(frame.Width, frame.Height);
    }

    void ShowOriginal(RasterLayer r) => (r.Bitmap, r.X, r.Y) = (original!, originalX, originalY);

    public override void Move(ToolEvent e)
    {
        if (!dragging || doc is null || layer is null) return;
        dx = (int)MathF.Round(e.Pos.X - start.X);
        dy = (int)MathF.Round(e.Pos.Y - start.Y);
        if (e.Shift)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
            else dx = 0;
        }
        if (floating)
        {
            Px.CopyAll(basePixels!, scratch!);
            using (var c = new SKCanvas(scratch!)) Px.Draw(c, floatPixels!, dx, dy);
            var r = (RasterLayer)layer;
            (r.Bitmap, r.X, r.Y) = (scratch!, frame.Left, frame.Top);
            e.View.SelectionOffset = new SKPoint(dx, dy);
        }
        else
        {
            Transforms.Offset(layer, dx - appliedX, dy - appliedY);
            appliedX = dx;
            appliedY = dy;
        }
        doc.InvalidateAll();
    }

    public override void Up(ToolEvent e)
    {
        if (!dragging || doc is null || layer is null) return;
        dragging = false;
        var (d, l) = (doc, layer);
        if (floating)
        {
            var r = (RasterLayer)l;
            ShowOriginal(r); // history must see the untouched layer as "before"
            e.View.SelectionOffset = default;
            if (dx != 0 || dy != 0)
            {
                var moved = scratch!;
                scratch = null;
                var selection = Px.NewMask(d.Width, d.Height);
                using (var c = new SKCanvas(selection)) Px.Draw(c, d.Selection!, dx, dy);
                var at = frame;
                d.Edit("Move Selection", () =>
                {
                    (r.Bitmap, r.X, r.Y) = (moved, at.Left, at.Top);
                    d.ReplaceSelectionInEdit(selection);
                });
            }
            else d.InvalidateAll();
        }
        else
        {
            Transforms.Offset(l, -appliedX, -appliedY);
            if (dx != 0 || dy != 0) d.Edit("Move", () => Transforms.Offset(l, dx, dy));
        }
        Release();
    }

    public override void Cancel()
    {
        if (!dragging || doc is null || layer is null) return;
        dragging = false;
        if (floating) ShowOriginal((RasterLayer)layer);
        else Transforms.Offset(layer, -appliedX, -appliedY);
        Ed.Canvas.SelectionOffset = default;
        doc.InvalidateAll();
        Release();
    }

    void Release()
    {
        basePixels?.Dispose();
        floatPixels?.Dispose();
        scratch?.Dispose();
        basePixels = floatPixels = scratch = original = null;
        doc = null;
        layer = null;
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (Ed.Doc is not { Active: { } active } d || dragging) return false;
        int step = mods.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var (nx, ny) = key switch
        {
            Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0),
        };
        if (nx == 0 && ny == 0) return false;
        d.Edit("Nudge", () => Transforms.Offset(active, nx, ny), "nudge:" + active.Id);
        return true;
    }
}

// ======================================================================= crop

public sealed class CropTool : Tool
{
    SKRect rect;
    bool has, dragging;
    int grab = -1; // 0..7 handles clockwise from top-left, 8 = move, 9 = new rectangle
    SKPoint start;
    SKRect startRect;
    TextBlock? readout;

    public CropTool(Editor e) : base(e) { }
    public override string Name => "Crop";
    public override string IconData => Icons.Crop;
    public override Key Shortcut => Key.C;
    public override string Hint => "Drag a rectangle, adjust its handles, then press Enter to crop. Esc cancels.";

    public override void Activate()
    {
        has = false;
        // Start from the selection when there is one.
        if (Ed.Doc is { Selection: { } sel })
        {
            var b = Sel.Bounds(sel);
            if (!b.IsEmpty) { rect = b; has = true; }
        }
        UpdateReadout();
    }

    static readonly (float x, float y)[] HandleAt = { (0, 0), (0.5f, 0), (1, 0), (1, 0.5f), (1, 1), (0.5f, 1), (0, 1), (0, 0.5f) };

    SKPoint HandlePoint(int i) => new(rect.Left + rect.Width * HandleAt[i].x, rect.Top + rect.Height * HandleAt[i].y);

    int Hit(ToolEvent e)
    {
        if (!has) return 9;
        float reach = 9 * e.View.OverlayScale;
        for (int i = 0; i < 8; i++)
            if (SKPoint.Distance(e.View.ToView(HandlePoint(i)), e.ViewPos) <= reach) return i;
        return rect.Contains(e.Pos) ? 8 : 9;
    }

    public override void Down(ToolEvent e)
    {
        if (e.Clicks >= 2 && has && rect.Contains(e.Pos)) { Commit(); return; }
        grab = Hit(e);
        start = e.Pos;
        startRect = rect;
        dragging = true;
        if (grab == 9) { rect = SKRect.Create(e.Pos.X, e.Pos.Y, 0, 0); has = true; }
    }

    public override void Move(ToolEvent e)
    {
        if (!dragging || Ed.Doc is not { } doc) return;
        float dx = e.Pos.X - start.X, dy = e.Pos.Y - start.Y;
        if (grab == 9)
        {
            var to = e.Shift ? ConstrainSquare(start, e.Pos) : e.Pos;
            rect = new SKRect(Math.Min(start.X, to.X), Math.Min(start.Y, to.Y), Math.Max(start.X, to.X), Math.Max(start.Y, to.Y));
        }
        else if (grab == 8)
        {
            rect = startRect;
            rect.Offset(dx, dy);
            // Keep the frame on the canvas.
            rect.Offset(Math.Max(0, -rect.Left) - Math.Max(0, rect.Right - doc.Width), Math.Max(0, -rect.Top) - Math.Max(0, rect.Bottom - doc.Height));
        }
        else
        {
            float l = startRect.Left, t = startRect.Top, r = startRect.Right, b = startRect.Bottom;
            var (hx, hy) = HandleAt[grab];
            if (hx == 0) l += dx; else if (hx == 1) r += dx;
            if (hy == 0) t += dy; else if (hy == 1) b += dy;
            rect = new SKRect(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
        }
        rect = SKRect.Intersect(new SKRect(MathF.Round(rect.Left), MathF.Round(rect.Top), MathF.Round(rect.Right), MathF.Round(rect.Bottom)), new SKRect(0, 0, doc.Width, doc.Height));
        UpdateReadout();
    }

    public override void Up(ToolEvent e)
    {
        dragging = false;
        if (rect.Width < 1 || rect.Height < 1) has = false;
        UpdateReadout();
    }

    public override void Cancel()
    {
        dragging = false;
        has = false;
        UpdateReadout();
        Ed.Canvas?.InvalidateVisual();
    }

    void Commit()
    {
        if (!has || Ed.Doc is not { } doc) return;
        var r = Px.RoundOut(rect);
        has = false;
        dragging = false;
        Transforms.Crop(doc, r);
        Ed.Canvas.Fit(false);
        UpdateReadout();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (!has) return false;
        if (key is Key.Enter or Key.Return) { Commit(); return true; }
        if (key == Key.Escape) { Cancel(); return true; }
        return false;
    }

    void UpdateReadout()
    {
        if (readout is not null)
            readout.Text = has ? $"{rect.Width:0} × {rect.Height:0} px" : "Drag on the image to set the crop";
    }

    public override void Overlay(SKCanvas c, CanvasView view)
    {
        if (!has) return;
        var r = view.ToView(rect);
        var size = view.ViewportSize;
        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, 140) })
        {
            c.Save();
            c.ClipRect(r, SKClipOperation.Difference);
            c.DrawRect(0, 0, size.Width, size.Height, dim);
            c.Restore();
        }
        using (var thirds = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 255, 255, 90) })
        {
            for (int i = 1; i < 3; i++)
            {
                c.DrawLine(r.Left + r.Width * i / 3, r.Top, r.Left + r.Width * i / 3, r.Bottom, thirds);
                c.DrawLine(r.Left, r.Top + r.Height * i / 3, r.Right, r.Top + r.Height * i / 3, thirds);
            }
        }
        view.OverlayStroke(c, p => c.DrawRect(r, p));
        float h = 4.5f * view.OverlayScale;
        using var fill = new SKPaint { Color = new SKColor(0xD6, 0xFF, 0x00) };
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * view.OverlayScale, Color = new SKColor(0x16, 0x16, 0x16) };
        for (int i = 0; i < 8; i++)
        {
            var p = view.ToView(HandlePoint(i));
            var box = new SKRect(p.X - h, p.Y - h, p.X + h, p.Y + h);
            c.DrawRect(box, fill);
            c.DrawRect(box, edge);
        }
    }

    public override IEnumerable<Control> Options()
    {
        readout = Ui.MonoText("");
        UpdateReadout();
        yield return readout;
        yield return Ui.Divider();
        yield return Ui.TextButton("Crop", Commit, "primary");
        yield return Ui.TextButton("Cancel", Cancel, "ghost");
    }
}

// ============================================================= free transform

/// <summary>
/// Scale, rotate and move the active layer with on-canvas handles. A temporary
/// mode: Enter applies, Esc cancels, and the previous tool comes back.
/// </summary>
public sealed class TransformTool : Tool
{
    Document? doc;
    Layer? layer;
    LayerState? pristine;
    SKRect box;
    float tx, ty, sx = 1, sy = 1, angle;
    bool active, leaving;

    int grab = -1; // 0..7 handles, 8 move, 9 rotate
    SKPoint start;
    float tx0, ty0, sx0, sy0, angle0;
    TextBlock? readout;

    public TransformTool(Editor e) : base(e) { }
    public override string Name => "Free Transform";
    public override string IconData => Icons.Transform;
    public override bool Transient => true;
    public override Cursor Cursor => new(StandardCursorType.Arrow);
    public override string Hint => "Drag handles to scale (Shift keeps proportions), outside the box to rotate, inside to move. Enter applies, Esc cancels.";

    public override void Activate()
    {
        active = false;
        doc = Ed.Doc;
        layer = doc?.Active;
        var bounds = layer is null ? SKRectI.Empty : Document.ContentBounds(layer);
        if (doc is null || layer is null || bounds.IsEmpty)
        {
            Ed.Flash("Select a layer with content to transform.");
            Dispatcher.UIThread.Post(() => { if (Ed.Tool == this) Ed.RestoreTool(); });
            return;
        }
        pristine = LayerState.From(layer);
        box = bounds;
        tx = ty = angle = 0;
        sx = sy = 1;
        active = true;
        UpdateReadout();
    }

    SKPoint Centre => new(box.MidX + tx, box.MidY + ty);

    SKMatrix Matrix() =>
        SKMatrix.CreateTranslation(-box.MidX, -box.MidY)
            .PostConcat(SKMatrix.CreateScale(sx, sy))
            .PostConcat(SKMatrix.CreateRotationDegrees(angle))
            .PostConcat(SKMatrix.CreateTranslation(box.MidX + tx, box.MidY + ty));

    bool IsIdentity => tx == 0 && ty == 0 && sx == 1 && sy == 1 && angle == 0;
    bool IsTranslation => sx == 1 && sy == 1 && angle == 0;

    void ApplyTo(Layer target, bool fast)
    {
        if (IsTranslation) Transforms.Offset(target, (int)MathF.Round(tx), (int)MathF.Round(ty));
        else Transforms.TransformLayer(target, Matrix(), false, fast);
    }

    void Preview()
    {
        if (!active) return;
        pristine!.ApplyTo(layer!);
        if (!IsIdentity) ApplyTo(layer!, fast: true);
        doc!.InvalidateAll();
        UpdateReadout();
    }

    void Finish(bool apply)
    {
        if (!active) return;
        active = false;
        pristine!.ApplyTo(layer!);
        var (d, l) = (doc!, layer!);
        if (apply && !IsIdentity) d.Edit("Free Transform", () => ApplyTo(l, fast: false));
        else d.InvalidateAll();
        doc = null;
        layer = null;
        pristine = null;
        if (!leaving) Ed.RestoreTool();
    }

    public override void Deactivate()
    {
        // Switching tools mid-transform keeps the work, like pressing Enter.
        leaving = true;
        Finish(true);
        leaving = false;
    }

    public override void Cancel()
    {
        // Called when the document is switched or closed under the tool.
        if (grab >= 0) { grab = -1; return; }
        leaving = true;
        Finish(false);
        leaving = false;
    }

    static readonly (int x, int y)[] HandleSign = { (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0) };

    SKPoint Corner(int hx, int hy) => Matrix().MapPoint(new SKPoint(box.MidX + hx * box.Width / 2, box.MidY + hy * box.Height / 2));

    int Hit(ToolEvent e)
    {
        float reach = 10 * e.View.OverlayScale;
        for (int i = 0; i < 8; i++)
            if (SKPoint.Distance(e.View.ToView(Corner(HandleSign[i].x, HandleSign[i].y)), e.ViewPos) <= reach) return i;
        // Inside the box (tested in the box's own unrotated space) moves it.
        if (!Matrix().TryInvert(out var inverse)) return 8;
        return box.Contains(inverse.MapPoint(e.Pos)) ? 8 : 9;
    }

    public override void Down(ToolEvent e)
    {
        if (!active) return;
        grab = Hit(e);
        start = e.Pos;
        (tx0, ty0, sx0, sy0, angle0) = (tx, ty, sx, sy, angle);
    }

    public override void Move(ToolEvent e)
    {
        if (!active || grab < 0) return;
        var c0 = new SKPoint(box.MidX + tx0, box.MidY + ty0);
        if (grab == 8)
        {
            tx = tx0 + e.Pos.X - start.X;
            ty = ty0 + e.Pos.Y - start.Y;
            if (e.Shift)
            {
                if (Math.Abs(tx - tx0) >= Math.Abs(ty - ty0)) ty = ty0;
                else tx = tx0;
            }
        }
        else if (grab == 9)
        {
            double a0 = Math.Atan2(start.Y - c0.Y, start.X - c0.X), a1 = Math.Atan2(e.Pos.Y - c0.Y, e.Pos.X - c0.X);
            angle = angle0 + (float)((a1 - a0) * 180 / Math.PI);
            if (e.Shift) angle = MathF.Round(angle / 15f) * 15f;
        }
        else
        {
            // Work in the box's unrotated frame, centred on its position at drag start;
            // the corner or edge opposite the handle stays where it is.
            var (hx, hy) = HandleSign[grab];
            double rad = -angle0 * Math.PI / 180;
            float px = e.Pos.X - c0.X, py = e.Pos.Y - c0.Y;
            float lx = (float)(px * Math.Cos(rad) - py * Math.Sin(rad)), ly = (float)(px * Math.Sin(rad) + py * Math.Cos(rad));
            float hw = box.Width / 2, hh = box.Height / 2;
            float ax = -hx * sx0 * hw, ay = -hy * sy0 * hh;
            float nsx = sx0, nsy = sy0, cx = 0, cy = 0;
            if (hx != 0) { nsx = (lx - ax) * hx / (2 * hw); cx = (lx + ax) / 2; }
            if (hy != 0) { nsy = (ly - ay) * hy / (2 * hh); cy = (ly + ay) / 2; }
            if (e.Shift && hx != 0 && hy != 0)
            {
                // Uniform scale from the corner: follow the larger change.
                float k = Math.Max(Math.Abs(nsx / sx0), Math.Abs(nsy / sy0));
                nsx = sx0 * k * Math.Sign(nsx == 0 ? 1 : nsx / sx0);
                nsy = sy0 * k * Math.Sign(nsy == 0 ? 1 : nsy / sy0);
                cx = ax + hx * nsx * hw;
                cy = ay + hy * nsy * hh;
            }
            if (Math.Abs(nsx) < 0.001f) nsx = 0.001f;
            if (Math.Abs(nsy) < 0.001f) nsy = 0.001f;
            sx = nsx;
            sy = nsy;
            double back = angle0 * Math.PI / 180;
            tx = tx0 + (float)(cx * Math.Cos(back) - cy * Math.Sin(back));
            ty = ty0 + (float)(cx * Math.Sin(back) + cy * Math.Cos(back));
        }
        Preview();
    }

    public override void Up(ToolEvent e) => grab = -1;

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (!active) return false;
        if (key is Key.Enter or Key.Return) { Finish(true); return true; }
        if (key == Key.Escape) { Finish(false); return true; }
        return false;
    }

    void UpdateReadout()
    {
        if (readout is not null)
            readout.Text = $"W {Math.Abs(sx) * 100:0.#}%   H {Math.Abs(sy) * 100:0.#}%   ∠ {angle:0.#}°   X {tx:+0;-0;0}   Y {ty:+0;-0;0}";
    }

    public override void Overlay(SKCanvas c, CanvasView view)
    {
        if (!active) return;
        var pts = new[] { Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1) }.Select(view.ToView).ToArray();
        using var path = new SKPath();
        path.AddPoly(pts, true);
        view.OverlayStroke(c, p => c.DrawPath(path, p));
        float h = 4.5f * view.OverlayScale;
        using var fill = new SKPaint { Color = new SKColor(0xD6, 0xFF, 0x00) };
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * view.OverlayScale, Color = new SKColor(0x16, 0x16, 0x16) };
        foreach (var (hx, hy) in HandleSign)
        {
            var p = view.ToView(Corner(hx, hy));
            var r = new SKRect(p.X - h, p.Y - h, p.X + h, p.Y + h);
            c.DrawRect(r, fill);
            c.DrawRect(r, edge);
        }
        var centre = view.ToView(Centre);
        view.OverlayStroke(c, p => c.DrawCircle(centre, 3 * view.OverlayScale, p));
    }

    public override IEnumerable<Control> Options()
    {
        readout = Ui.MonoText("");
        UpdateReadout();
        yield return readout;
        yield return Ui.Divider();
        yield return Ui.TextButton("Apply", () => Finish(true), "primary");
        yield return Ui.TextButton("Cancel", () => Finish(false), "ghost");
    }
}

// ======================================================================= text

public sealed class TextTool : Tool
{
    string family = "Segoe UI";
    float size = 48;
    bool bold, italic;

    Document? doc;
    RasterLayer? layer;
    SKPoint origin;
    int historyStart;
    Border? editor;
    TextBox? box;
    Action<double>? setSize;

    public TextTool(Editor e) : base(e)
    {
        Ed.ColorsChanged += () => { if (editor is not null) Update(); };
    }

    public override string Name => "Text";
    public override string IconData => Icons.Text;
    public override Key Shortcut => Key.T;
    public override Cursor Cursor => new(StandardCursorType.Ibeam);
    public override string Hint => "Click to add text, or click existing text to edit it. Ctrl+Enter finishes; Esc cancels.";
    public bool Editing => editor is not null;

    TextInfo Info(string text) => new(text, family, size, bold, italic, (uint)Ed.Foreground);

    public override void Down(ToolEvent e)
    {
        if (Editing) { Done(); return; }
        doc = e.Doc;
        historyStart = doc.History.Position;
        // Clicking existing text edits it.
        layer = doc.AllLayers().OfType<RasterLayer>().FirstOrDefault(l => l.Text is not null && l.Visible && l.Bounds.Contains(e.Pixel.X, e.Pixel.Y));
        string text = "";
        if (layer?.Text is { } t)
        {
            (family, size, bold, italic) = (t.Family, t.Size, t.Bold, t.Italic);
            Ed.SetForeground(new SKColor(t.Color));
            setSize?.Invoke(size);
            text = t.Text;
            doc.Active = layer;
            doc.NotifyChanged();
        }
        origin = new SKPoint(e.Pos.X, e.Pos.Y - size * 0.6f);
        ShowEditor(e, text);
    }

    void ShowEditor(ToolEvent e, string text)
    {
        box = new TextBox
        {
            Text = text, AcceptsReturn = true, MinWidth = 240, MaxWidth = 420, MinHeight = 64, MaxHeight = 220,
            TextWrapping = TextWrapping.NoWrap, Watermark = "Type your text",
            FontFamily = (FontFamily)Application.Current!.FindResource("SansFont")!, FontSize = 14,
        };
        Avalonia.Automation.AutomationProperties.SetName(box, "Text");
        box.TextChanged += (_, _) => Update();
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Escape) { CancelEdit(); k.Handled = true; }
            else if (k.Key == Key.Enter && k.KeyModifiers.HasFlag(KeyModifiers.Control)) { Done(); k.Handled = true; }
        };
        var buttons = Ui.Row(8, Ui.TextButton("Done", Done, "primary"), Ui.TextButton("Cancel", CancelEdit, "ghost"));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        editor = new Border
        {
            Child = Ui.Column(8, Ui.Label("Text"), box, buttons), Padding = new Thickness(10), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(5),
        }.Dyn(Border.BackgroundProperty, "SurfaceBrush").Dyn(Border.BorderBrushProperty, "LineStrongBrush");

        // Below the insertion point, kept inside the viewport.
        double s = e.View.OverlayScale;
        var host = Ed.OverlayHost;
        double x = e.ViewPos.X / s + 12, y = e.ViewPos.Y / s + size * e.View.Zoom / s * 0.6 + 16;
        x = Math.Clamp(x, 8, Math.Max(8, host.Bounds.Width - 300));
        y = Math.Clamp(y, 8, Math.Max(8, host.Bounds.Height - 190));
        Avalonia.Controls.Canvas.SetLeft(editor, x);
        Avalonia.Controls.Canvas.SetTop(editor, y);
        host.Children.Add(editor);
        Dispatcher.UIThread.Post(() => { box?.Focus(); box?.SelectAll(); });
    }

    void Update()
    {
        if (doc is null || box is null) return;
        string text = box.Text ?? "";
        var info = Info(text);
        if (layer is null)
        {
            if (text.Length == 0) return;
            layer = Draw.AddText(doc, info, origin.X, origin.Y);
        }
        else
        {
            var l = layer;
            doc.Edit("Edit Text", () => Draw.SetText(l, info), "text:" + l.Id);
        }
    }

    void CloseEditor()
    {
        if (editor is not null) Ed.OverlayHost.Children.Remove(editor);
        editor = null;
        box = null;
        Ed.Canvas.Focus();
    }

    public void Done()
    {
        if (!Editing || doc is null) return;
        bool empty = string.IsNullOrWhiteSpace(box!.Text);
        var (d, l) = (doc, layer);
        CloseEditor();
        if (empty && l is not null)
        {
            // Emptying a text layer removes it.
            d.History.JumpTo(historyStart);
            if (d.Find(l.Id) is { } existing)
            {
                d.Active = existing;
                d.DeleteActive();
            }
        }
        doc = null;
        layer = null;
    }

    void CancelEdit()
    {
        if (!Editing || doc is null) return;
        var d = doc;
        CloseEditor();
        d.History.JumpTo(historyStart);
        doc = null;
        layer = null;
    }

    public override void Cancel() => Done();
    public override void Deactivate() => Done();

    void Restyle()
    {
        if (Editing) Update();
    }

    public override IEnumerable<Control> Options()
    {
        var fonts = Draw.FontFamilies();
        if (!fonts.Contains(family)) family = fonts.FirstOrDefault(f => f == "Arial") ?? fonts.FirstOrDefault() ?? family;
        yield return Ui.Label("Font");
        yield return Ui.Combo(fonts, family, f => f, f => { family = f; Restyle(); }, 190);
        var (row, set) = Ui.Slider("Size", 6, 400, size, v => { size = (float)v; Restyle(); }, v => $"{v:0} px", 130);
        setSize = set;
        yield return row;
        yield return Toggle("B", "Bold", bold, v => { bold = v; Restyle(); });
        yield return Toggle("I", "Italic", italic, v => { italic = v; Restyle(); });
        yield return Ui.Divider();
        yield return Ui.MonoText("Colour: foreground swatch", dim: true);
    }

    static Control Toggle(string text, string tip, bool value, Action<bool> onChange)
    {
        var t = new Avalonia.Controls.Primitives.ToggleButton { Content = text, IsChecked = value, Classes = { "seg" }, Width = 30, Focusable = false };
        ToolTip.SetTip(t, tip);
        Avalonia.Automation.AutomationProperties.SetName(t, tip);
        t.IsCheckedChanged += (_, _) => onChange(t.IsChecked == true);
        return t;
    }
}
