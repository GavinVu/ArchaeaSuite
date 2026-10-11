using ArchaeaPhoto.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace ArchaeaPhoto;

/// <summary>
/// The document viewport. The scaled image is kept in a viewport-sized cache
/// that is redrawn only where the document changed; selection outlines and
/// tool overlays are drawn over a copy of that cache every frame. All
/// coordinates here are physical screen pixels unless they say "doc".
/// </summary>
public sealed class CanvasView : Control
{
    readonly Editor ed;
    WriteableBitmap? frame;
    SKBitmap? cache;
    bool cacheStale = true;
    SKRectI dirtyDoc = SKRectI.Empty;
    bool highQuality;
    readonly DispatcherTimer idleTimer, antsTimer;
    float antsPhase;
    SKShader? checker;

    bool panning, toolDown, spaceHeld;
    Point panStart;
    double panStartX, panStartY;

    /// <summary>Last pointer position in document coordinates, or null when outside.</summary>
    public SKPoint? PointerDoc { get; private set; }
    /// <summary>Shifts the selection outline while a tool is dragging the selection.</summary>
    public SKPoint SelectionOffset { get; set; }
    public event Action? ViewChanged;
    public event Action? PointerInfoChanged;

    public CanvasView(Editor editor)
    {
        ed = editor;
        Focusable = true;
        ClipToBounds = true;
        idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        idleTimer.Tick += (_, _) =>
        {
            idleTimer.Stop();
            if (Doc is not null && View.Zoom < 1 && !highQuality)
            {
                highQuality = true;
                cacheStale = true;
                InvalidateVisual();
            }
        };
        antsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        antsTimer.Tick += (_, _) =>
        {
            if (Doc?.HasSelection != true) return;
            antsPhase = (antsPhase + 1) % 8;
            InvalidateVisual();
        };
        ed.Invalidated += r =>
        {
            dirtyDoc = Px.Union(dirtyDoc, r);
            Touch();
        };
        ed.DocChanged += () =>
        {
            cacheStale = true;
            Touch();
        };
        ed.ToolChanged += () => Cursor = ed.Tool.Cursor;
        ActualThemeVariantChanged += (_, _) => Redraw();
    }

    public Document? Doc => ed.Doc;
    public DocView View => ed.ViewOf(ed.Doc!);
    public double Zoom => Doc is null ? 1 : View.Zoom;
    double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    public SKSize ViewportSize => new((float)(Bounds.Width * Scaling), (float)(Bounds.Height * Scaling));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        antsTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        antsTimer.Stop();
        idleTimer.Stop();
    }

    // ------------------------------------------------------------- coordinates

    public SKPoint ToView(SKPoint doc) => new((float)(View.PanX + doc.X * View.Zoom), (float)(View.PanY + doc.Y * View.Zoom));
    public SKPoint ToDoc(SKPoint view) => new((float)((view.X - View.PanX) / View.Zoom), (float)((view.Y - View.PanY) / View.Zoom));
    SKPoint Physical(Point p) => new((float)(p.X * Scaling), (float)(p.Y * Scaling));

    public SKRect ToView(SKRect doc)
    {
        var a = ToView(new SKPoint(doc.Left, doc.Top));
        var b = ToView(new SKPoint(doc.Right, doc.Bottom));
        return new SKRect(a.X, a.Y, b.X, b.Y);
    }

    // ------------------------------------------------------------------- view

    /// <summary>Marks the whole viewport for redrawing (view or theme changed).</summary>
    public void Redraw()
    {
        cacheStale = true;
        checker = null;
        Touch();
    }

    void Touch()
    {
        highQuality = false;
        idleTimer.Stop();
        idleTimer.Start();
        InvalidateVisual();
    }

    void OnViewChanged()
    {
        Redraw();
        ViewChanged?.Invoke();
    }

    public void ZoomAt(double factor, SKPoint anchor) => SetZoom(View.Zoom * factor, anchor);

    public void SetZoom(double zoom, SKPoint? anchor = null)
    {
        if (Doc is null) return;
        var v = View;
        var a = anchor ?? new SKPoint(ViewportSize.Width / 2, ViewportSize.Height / 2);
        zoom = Math.Clamp(zoom, 0.01, 64);
        // Keep the image point under the anchor where it is.
        double dx = (a.X - v.PanX) / v.Zoom, dy = (a.Y - v.PanY) / v.Zoom;
        v.Zoom = zoom;
        v.PanX = a.X - dx * zoom;
        v.PanY = a.Y - dy * zoom;
        v.NeedsFit = false;
        OnViewChanged();
    }

    public void ZoomStep(bool zoomIn, SKPoint? anchor = null)
    {
        double[] stops = { 0.01, 0.02, 0.03, 0.05, 0.0667, 0.0833, 0.125, 0.1667, 0.25, 0.3333, 0.5, 0.6667, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64 };
        double z = Zoom;
        double next = zoomIn ? stops.FirstOrDefault(s => s > z * 1.001, 64) : stops.LastOrDefault(s => s < z * 0.999, 0.01);
        SetZoom(next, anchor);
    }

    public void Fit(bool allowUpscale = true)
    {
        if (Doc is null) return;
        var size = ViewportSize;
        if (size.Width < 8 || size.Height < 8) { View.NeedsFit = true; return; }
        var v = View;
        double margin = 48 * Scaling;
        double zoom = Math.Min((size.Width - margin) / Doc.Width, (size.Height - margin) / Doc.Height);
        if (!allowUpscale) zoom = Math.Min(1, zoom);
        v.Zoom = Math.Clamp(zoom, 0.01, 64);
        v.PanX = Math.Round((size.Width - Doc.Width * v.Zoom) / 2);
        v.PanY = Math.Round((size.Height - Doc.Height * v.Zoom) / 2);
        v.NeedsFit = false;
        OnViewChanged();
    }

    public void ActualSize() => SetZoom(1);

    public void PanBy(double dx, double dy)
    {
        if (Doc is null) return;
        View.PanX += dx;
        View.PanY += dy;
        OnViewChanged();
    }

    // ---------------------------------------------------------------- drawing

    public override unsafe void Render(DrawingContext context)
    {
        double s = Scaling;
        int pw = Math.Max(1, (int)Math.Ceiling(Bounds.Width * s)), ph = Math.Max(1, (int)Math.Ceiling(Bounds.Height * s));
        if (frame is null || frame.PixelSize.Width != pw || frame.PixelSize.Height != ph)
        {
            frame?.Dispose();
            // 96 dpi so the bitmap's size equals its pixel size; the destination rectangle below does the DPI scaling.
            frame = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var fb = frame.Lock())
        using (var surface = SKSurface.Create(Px.ColorInfo(pw, ph), fb.Address, fb.RowBytes))
            Paint(surface.Canvas, pw, ph);
        context.DrawImage(frame, new Rect(0, 0, pw / s, ph / s));
    }

    void Paint(SKCanvas c, int pw, int ph)
    {
        var background = Ui.Brush("CanvasBrush") is ISolidColorBrush b ? b.Color.ToSk() : new SKColor(0x1D, 0x1D, 0x1D);
        var doc = Doc;
        if (doc is null)
        {
            c.Clear(background);
            return;
        }
        if (cache is null || cache.Width != pw || cache.Height != ph)
        {
            cache?.Dispose();
            cache = new SKBitmap(Px.ColorInfo(pw, ph));
            cacheStale = true;
        }
        if (View.NeedsFit) FitQuiet(doc, pw, ph);
        if (cacheStale || !dirtyDoc.IsEmpty)
        {
            using var cc = new SKCanvas(cache);
            if (!cacheStale)
            {
                var r = ToView(SKRect.Create(dirtyDoc.Left, dirtyDoc.Top, dirtyDoc.Width, dirtyDoc.Height));
                r.Inflate(2, 2);
                cc.ClipRect(r);
            }
            DrawImage(cc, doc, background, pw, ph);
            cacheStale = false;
            dirtyDoc = SKRectI.Empty;
        }
        using (var copy = new SKPaint { BlendMode = SKBlendMode.Src })
            Px.Draw(c, cache, 0, 0, copy);
        DrawOverlays(c, doc);
    }

    void FitQuiet(Document doc, int pw, int ph)
    {
        var v = View;
        double margin = 48 * Scaling;
        v.Zoom = Math.Clamp(Math.Min(1, Math.Min((pw - margin) / doc.Width, (ph - margin) / doc.Height)), 0.01, 64);
        v.PanX = Math.Round((pw - doc.Width * v.Zoom) / 2);
        v.PanY = Math.Round((ph - doc.Height * v.Zoom) / 2);
        v.NeedsFit = false;
        Dispatcher.UIThread.Post(() => ViewChanged?.Invoke());
    }

    void DrawImage(SKCanvas c, Document doc, SKColor background, int pw, int ph)
    {
        var v = View;
        c.Clear(background);
        var dst = SKRect.Create((float)v.PanX, (float)v.PanY, (float)(doc.Width * v.Zoom), (float)(doc.Height * v.Zoom));
        var visible = SKRect.Intersect(dst, new SKRect(0, 0, pw, ph));
        if (visible.IsEmpty) return;

        c.Save();
        c.ClipRect(dst);
        checker ??= Ui.Checker((float)(8 * Scaling), false);
        using (var p = new SKPaint { Shader = checker })
            c.DrawRect(visible, p);

        // Draw only the part of the image that is on screen.
        var composite = doc.GetComposite();
        int sl = Math.Max(0, (int)Math.Floor((visible.Left - v.PanX) / v.Zoom)), st = Math.Max(0, (int)Math.Floor((visible.Top - v.PanY) / v.Zoom));
        int sr = Math.Min(doc.Width, (int)Math.Ceiling((visible.Right - v.PanX) / v.Zoom)), sb = Math.Min(doc.Height, (int)Math.Ceiling((visible.Bottom - v.PanY) / v.Zoom));
        if (sr > sl && sb > st)
        {
            var src = new SKRect(sl, st, sr, sb);
            using var p = new SKPaint
            {
                // Crisp pixels when magnified; smooth when reduced, with the better filter once the view is idle.
                FilterQuality = v.Zoom >= 1 ? SKFilterQuality.None : highQuality ? SKFilterQuality.Medium : SKFilterQuality.Low,
            };
            Px.Draw(c, composite, src, ToView(src), p);
        }
        c.Restore();

        using var border = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = (Ui.Brush("LineBrush") as ISolidColorBrush)?.Color.ToSk() ?? SKColors.Gray };
        c.DrawRect(SKRect.Inflate(dst, 0.5f, 0.5f), border);
    }

    void DrawOverlays(SKCanvas c, Document doc)
    {
        var v = View;
        if (doc.SelectionOutline is { } outline)
        {
            c.Save();
            c.Translate((float)(v.PanX + SelectionOffset.X * v.Zoom), (float)(v.PanY + SelectionOffset.Y * v.Zoom));
            c.Scale((float)v.Zoom);
            // Hairlines stay one screen pixel wide at any zoom.
            using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0, Color = SKColors.White };
            c.DrawPath(outline, p);
            float dash = (float)(4 * Scaling / v.Zoom);
            p.Color = SKColors.Black;
            p.PathEffect = SKPathEffect.CreateDash(new[] { dash, dash }, antsPhase * dash / 4);
            c.DrawPath(outline, p);
            p.PathEffect?.Dispose();
            c.Restore();
        }
        ed.Tool.Overlay(c, this);
    }

    /// <summary>Paint for overlay lines: a white line with a dark outline, readable on any image.</summary>
    public void OverlayStroke(SKCanvas c, Action<SKPaint> draw, bool dashed = false)
    {
        float s = (float)Scaling;
        using var p = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 3 * s, Color = new SKColor(0, 0, 0, 150) };
        draw(p);
        p.StrokeWidth = 1.25f * s;
        p.Color = SKColors.White;
        using var effect = dashed ? SKPathEffect.CreateDash(new[] { 5 * s, 4 * s }, 0) : null;
        p.PathEffect = effect;
        draw(p);
    }

    public float OverlayScale => (float)Scaling;

    // ------------------------------------------------------------------ input

    ToolEvent Event(PointerEventArgs e, int clicks = 1)
    {
        var p = Physical(e.GetPosition(this));
        return new ToolEvent(Doc!, ToDoc(p), p, e.KeyModifiers, clicks, this);
    }

    public void SetSpaceHeld(bool held)
    {
        if (spaceHeld == held) return;
        spaceHeld = held;
        Cursor = held ? new Cursor(StandardCursorType.Hand) : ed.Tool.Cursor;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Doc is null) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && (spaceHeld || ed.Tool is HandTool)))
        {
            panning = true;
            panStart = e.GetPosition(this);
            panStartX = View.PanX;
            panStartY = View.PanY;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        toolDown = true;
        e.Pointer.Capture(this);
        ed.Tool.Down(Event(e, e.ClickCount));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Doc is null) return;
        if (panning)
        {
            var p = e.GetPosition(this);
            View.PanX = panStartX + (p.X - panStart.X) * Scaling;
            View.PanY = panStartY + (p.Y - panStart.Y) * Scaling;
            OnViewChanged();
            return;
        }
        var ev = Event(e);
        PointerDoc = ev.Pos;
        PointerInfoChanged?.Invoke();
        if (toolDown)
        {
            // Replay coalesced moves so fast strokes stay smooth.
            foreach (var point in e.GetIntermediatePoints(this))
            {
                var p = Physical(point.Position);
                ed.Tool.Move(new ToolEvent(Doc, ToDoc(p), p, e.KeyModifiers, 1, this));
            }
        }
        else ed.Tool.Hover(ev);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Finish the tool's work before giving up the capture: releasing it raises
        // CaptureLost, which would otherwise cancel the operation.
        if (panning) panning = false;
        else if (toolDown && Doc is not null)
        {
            toolDown = false;
            ed.Tool.Up(Event(e));
            InvalidateVisual();
        }
        toolDown = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        panning = false;
        if (toolDown)
        {
            toolDown = false;
            ed.Tool.Cancel();
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerDoc = null;
        PointerInfoChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Doc is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            ZoomAt(Math.Pow(1.2, e.Delta.Y), Physical(e.GetPosition(this)));
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            PanBy((e.Delta.Y + e.Delta.X) * 80 * Scaling, 0);
        else
            PanBy(e.Delta.X * 80 * Scaling, e.Delta.Y * 80 * Scaling);
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Keep the image centred on the same point as the viewport resizes.
        if (Doc is not null && !View.NeedsFit && e.PreviousSize.Width > 0)
        {
            View.PanX += (e.NewSize.Width - e.PreviousSize.Width) * Scaling / 2;
            View.PanY += (e.NewSize.Height - e.PreviousSize.Height) * Scaling / 2;
        }
        Redraw();
    }
}
