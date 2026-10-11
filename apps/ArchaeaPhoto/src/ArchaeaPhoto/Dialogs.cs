using ArchaeaPhoto.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace ArchaeaPhoto;

// ================================================================ new document

public sealed class NewDocumentDialog : DialogWindow
{
    public sealed record Result(string Name, int Width, int Height, int Background);

    public Result? Value { get; private set; }
    readonly TextBox name = new() { Text = "Untitled", Width = 220 };
    readonly TextBox width, height;
    int background;

    static readonly (string label, int w, int h)[] Presets =
    {
        ("Custom", 0, 0), ("HD 1920 × 1080", 1920, 1080), ("4K 3840 × 2160", 3840, 2160), ("Square 1080 × 1080", 1080, 1080),
        ("Portrait 1080 × 1350", 1080, 1350), ("Story 1080 × 1920", 1080, 1920), ("A4 at 300 ppi 2480 × 3508", 2480, 3508),
        ("Letter at 300 ppi 2550 × 3300", 2550, 3300), ("Icon 512 × 512", 512, 512),
    };

    public NewDocumentDialog(SKColor backgroundColor) : base("New Document", 420)
    {
        AutomationProperties.SetName(name, "Document name");
        width = Ui.NumberBox(1920, _ => { }, 90, "Width in pixels");
        height = Ui.NumberBox(1080, _ => { }, 90, "Height in pixels");
        var preset = Ui.Combo(Presets, Presets[1], p => p.label, p =>
        {
            if (p.w == 0) return;
            width.Text = p.w.ToString();
            height.Text = p.h.ToString();
        }, 240);
        var swap = Ui.IconButton(Icons.Swap, "Swap width and height", () => (width.Text, height.Text) = (height.Text, width.Text));
        Body.Children.Add(Field("Name", name));
        Body.Children.Add(Field("Preset", preset));
        Body.Children.Add(Field("Width", Ui.Row(8, width, Ui.MonoText("px", true))));
        Body.Children.Add(Field("Height", Ui.Row(8, height, Ui.MonoText("px", true), swap)));
        Body.Children.Add(Field("Background", Ui.Combo(new[] { 0, 1, 2 }, 0,
            i => i == 0 ? "White" : i == 1 ? "Transparent" : $"Background colour (#{backgroundColor.Hex()})", i => background = i, 240)));
        AddButton("Cancel", Close);
        AddButton("Create", OnAccept, true);
        Opened += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    protected override void OnAccept()
    {
        if (!int.TryParse(width.Text, out int w) || !int.TryParse(height.Text, out int h) || w < 1 || h < 1 || w > Document.MaxSize || h > Document.MaxSize)
        {
            _ = MessageDialog.Info(this, "Invalid size", $"Width and height must be whole numbers between 1 and {Document.MaxSize} pixels.");
            return;
        }
        Value = new Result(string.IsNullOrWhiteSpace(name.Text) ? "Untitled" : name.Text.Trim(), w, h, background);
        Close();
    }
}

// ============================================================ image/canvas size

public sealed class SizeDialog : DialogWindow
{
    public (int width, int height, float anchorX, float anchorY)? Value { get; private set; }
    readonly TextBox width, height;
    readonly double ratio;
    bool linked = true, updating;
    float ax = 0.5f, ay = 0.5f;

    public SizeDialog(Document doc, bool canvas) : base(canvas ? "Canvas Size" : "Image Size", 400)
    {
        ratio = doc.Width / (double)doc.Height;
        width = Ui.NumberBox(doc.Width, _ => { }, 90, "Width in pixels");
        height = Ui.NumberBox(doc.Height, _ => { }, 90, "Height in pixels");
        Body.Children.Add(Ui.MonoText($"Current size: {doc.Width} × {doc.Height} px", dim: true));
        Body.Children.Add(Field("Width", Ui.Row(8, width, Ui.MonoText("px", true))));
        Body.Children.Add(Field("Height", Ui.Row(8, height, Ui.MonoText("px", true))));
        if (!canvas)
        {
            width.TextChanged += (_, _) => Link(width, height, 1 / ratio);
            height.TextChanged += (_, _) => Link(height, width, ratio);
            Body.Children.Add(Field("", Ui.Check("Keep proportions", true, v => linked = v)));
            Body.Children.Add(new TextBlock { Text = "Every layer is resampled to the new size.", Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            // 3×3 anchor: where the existing image stays when the canvas grows or shrinks.
            var grid = new Avalonia.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3, Width = 84, Height = 84 };
            var cells = new List<Avalonia.Controls.Primitives.ToggleButton>();
            for (int i = 0; i < 9; i++)
            {
                float cx = i % 3 / 2f, cy = i / 3 / 2f;
                var cell = new Avalonia.Controls.Primitives.ToggleButton { Margin = new Thickness(1), IsChecked = i == 4, Focusable = false, Classes = { "seg" }, Height = double.NaN, Padding = new Thickness(0) };
                AutomationProperties.SetName(cell, $"Anchor {(cy == 0 ? "top" : cy == 1 ? "bottom" : "middle")} {(cx == 0 ? "left" : cx == 1 ? "right" : "centre")}");
                cell.Click += (_, _) =>
                {
                    (ax, ay) = (cx, cy);
                    foreach (var other in cells) other.IsChecked = other == cell;
                };
                cells.Add(cell);
                grid.Children.Add(cell);
            }
            Body.Children.Add(Field("Anchor", grid));
            Body.Children.Add(new TextBlock { Text = "Layers are not resampled. Pixels outside the new canvas are kept on their layers.", Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        AddButton("Cancel", Close);
        AddButton("Apply", OnAccept, true);
        Opened += (_, _) => { width.Focus(); width.SelectAll(); };
    }

    void Link(TextBox from, TextBox to, double factor)
    {
        if (!linked || updating || !from.IsFocused || !int.TryParse(from.Text, out int v) || v < 1) return;
        updating = true;
        to.Text = Math.Max(1, (int)Math.Round(v * factor)).ToString();
        updating = false;
    }

    protected override void OnAccept()
    {
        if (!int.TryParse(width.Text, out int w) || !int.TryParse(height.Text, out int h) || w < 1 || h < 1 || w > Document.MaxSize || h > Document.MaxSize)
        {
            _ = MessageDialog.Info(this, "Invalid size", $"Width and height must be whole numbers between 1 and {Document.MaxSize} pixels.");
            return;
        }
        Value = (w, h, ax, ay);
        Close();
    }
}

// =============================================================== export quality

public sealed class QualityDialog : DialogWindow
{
    public int? Value { get; private set; }
    int quality = 90;

    public QualityDialog(ImageFormat format) : base($"Export {format.ToString().ToUpperInvariant()}", 380)
    {
        Body.Children.Add(Ui.Slider("Quality", 1, 100, quality, v => quality = (int)v, v => $"{v:0}", 200).row);
        Body.Children.Add(new TextBlock
        {
            Text = format == ImageFormat.Jpeg ? "JPEG has no transparency; transparent areas become white. Higher quality means a larger file."
                : "Higher quality means a larger file. 100 is not lossless.",
            Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        AddButton("Cancel", Close);
        AddButton("Export", OnAccept, true);
    }

    protected override void OnAccept()
    {
        Value = quality;
        Close();
    }
}

// ================================================ adjustments and filters dialog

public sealed record Param(string Label, double Min, double Max, double Default, double Step = 1, Func<double, string>? Format = null);

/// <summary>
/// Sliders with a live preview on the canvas. <c>build</c> turns the current
/// values into an operation on the untouched pixels (or null for "no change");
/// it runs on a background thread, and only the newest result is shown.
/// </summary>
public sealed class AdjustDialog : DialogWindow
{
    readonly PixelEditSession session;
    readonly string name;
    readonly Param[] parameters;
    readonly double[] values;
    readonly Func<double[], Func<SKBitmap, SKBitmap>?> build;
    readonly List<Action<double>> setters = new();
    readonly TextBlock busy = Ui.MonoText("", dim: true);
    bool preview = true, closed, committed;
    int version;

    /// <summary>Extra reset work for custom content (curves).</summary>
    public Action? OnReset { get; set; }

    public AdjustDialog(string name, PixelEditSession session, Param[] parameters, Func<double[], Func<SKBitmap, SKBitmap>?> build, Control? extra = null, double width = 400)
        : base(name, width)
    {
        this.name = name;
        this.session = session;
        this.parameters = parameters;
        this.build = build;
        values = parameters.Select(p => p.Default).ToArray();
        if (extra is not null) Body.Children.Add(extra);
        for (int i = 0; i < parameters.Length; i++)
        {
            int index = i;
            var p = parameters[i];
            var (row, set) = Ui.Slider(p.Label, p.Min, p.Max, p.Default, v => { values[index] = v; RequestPreview(); }, p.Format, 170, p.Step);
            ((TextBlock)((StackPanel)row).Children[0]).Width = 96;
            setters.Add(set);
            Body.Children.Add(row);
        }
        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        options.Children.Add(Ui.Check("Preview", true, v => { preview = v; RequestPreview(); }));
        Grid.SetColumn(busy, 1);
        busy.Margin = new Thickness(12, 0);
        options.Children.Add(busy);
        var reset = Ui.TextButton("Reset", Reset, "ghost");
        Grid.SetColumn(reset, 2);
        options.Children.Add(reset);
        Body.Children.Add(options);
        AddButton("Cancel", Close);
        AddButton("OK", OnAccept, true);
        Closed += (_, _) =>
        {
            closed = true;
            version++;
            if (!committed) session.Cancel();
        };
        Opened += (_, _) => RequestPreview();
    }

    void Reset()
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            values[i] = parameters[i].Default;
            setters[i](values[i]);
        }
        OnReset?.Invoke();
        RequestPreview();
    }

    public async void RequestPreview()
    {
        int mine = ++version;
        await Task.Delay(30);
        if (mine != version || closed) return;
        var op = preview ? build(values) : null;
        if (op is null)
        {
            session.ShowOriginal();
            return;
        }
        busy.Text = "Working…";
        SKBitmap result;
        try
        {
            result = await Task.Run(() => op(session.Original));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            busy.Text = "Preview failed";
            return;
        }
        if (mine == version && !closed)
        {
            session.Show(result);
            busy.Text = "";
        }
        result.Dispose();
    }

    protected override void OnAccept()
    {
        version++; // drop any preview still being computed
        var op = build(values);
        if (op is null) session.ShowOriginal();
        else
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            using var result = op(session.Original);
            session.Show(result);
        }
        session.Commit(name);
        committed = true;
        Close();
    }
}

/// <summary>Luma histogram shown above the Levels sliders.</summary>
public sealed class HistogramView : Control
{
    readonly int[] data;
    readonly int max;

    public HistogramView(int[] histogram)
    {
        data = histogram;
        // Ignore the two tallest bins so one spike (a flat background) does not flatten everything else.
        max = Math.Max(1, histogram.OrderByDescending(v => v).Skip(2).FirstOrDefault());
        Height = 90;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public override void Render(DrawingContext ctx)
    {
        var area = new Rect(Bounds.Size);
        ctx.DrawRectangle(Ui.Brush("BgBrush"), new Pen(Ui.Brush("LineBrush"), 2), area.Deflate(1), 3, 3);
        var bar = Ui.Brush("TextDimBrush");
        double w = (area.Width - 8) / 256.0, h = area.Height - 8;
        for (int i = 0; i < 256; i++)
        {
            double v = Math.Min(1.0, data[i] / (double)max) * h;
            if (v > 0) ctx.FillRectangle(bar, new Rect(4 + i * w, 4 + h - v, Math.Max(1, w + 0.5), v));
        }
    }
}

/// <summary>Editable tone curve: click to add a point, drag to move it, right-click or drag it out to remove it.</summary>
public sealed class CurveEditor : Control
{
    public Curves Curves { get; } = new();
    public event Action? Changed;
    int channel; // 0 RGB, 1 R, 2 G, 3 B
    int grabbed = -1;
    readonly int[]? histogram;
    readonly int histogramMax;
    const double Pad = 8;

    public CurveEditor(int[]? histogram)
    {
        this.histogram = histogram;
        histogramMax = histogram is null ? 1 : Math.Max(1, histogram.OrderByDescending(v => v).Skip(2).FirstOrDefault());
        Width = Height = 256 + Pad * 2;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        AutomationProperties.SetName(this, "Tone curve");
    }

    List<SKPoint> Points => channel switch { 1 => Curves.Red, 2 => Curves.Green, 3 => Curves.Blue, _ => Curves.Rgb };

    public void SetChannel(int c)
    {
        channel = c;
        InvalidateVisual();
    }

    public void Reset()
    {
        Curves.Rgb = Core.Curves.Identity();
        Curves.Red = Core.Curves.Identity();
        Curves.Green = Core.Curves.Identity();
        Curves.Blue = Core.Curves.Identity();
        InvalidateVisual();
    }

    public bool IsIdentity => new[] { Curves.Rgb, Curves.Red, Curves.Green, Curves.Blue }.All(l => l.All(p => Math.Abs(p.X - p.Y) < 0.002f));

    Point ToScreen(SKPoint p) => new(Pad + p.X * 256, Pad + (1 - p.Y) * 256);
    SKPoint FromScreen(Point p) => new((float)Math.Clamp((p.X - Pad) / 256, 0, 1), (float)Math.Clamp(1 - (p.Y - Pad) / 256, 0, 1));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var pts = Points;
        int hit = pts.FindIndex(p => Math.Abs(ToScreen(p).X - pos.X) < 9 && Math.Abs(ToScreen(p).Y - pos.Y) < 9);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (hit >= 0 && pts.Count > 2) { pts.RemoveAt(hit); Notify(); }
            return;
        }
        if (hit < 0)
        {
            var np = FromScreen(pos);
            hit = pts.FindIndex(p => p.X > np.X);
            if (hit < 0) hit = pts.Count;
            pts.Insert(hit, np);
            Notify();
        }
        grabbed = hit;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (grabbed < 0) return;
        var pts = Points;
        var pos = e.GetPosition(this);
        // Dragging a middle point well outside the graph removes it.
        if (pts.Count > 2 && grabbed > 0 && grabbed < pts.Count - 1 && (pos.Y < -24 || pos.Y > Bounds.Height + 24))
        {
            pts.RemoveAt(grabbed);
            grabbed = -1;
            e.Pointer.Capture(null);
            Notify();
            return;
        }
        var p = FromScreen(pos);
        float lo = grabbed > 0 ? pts[grabbed - 1].X + 0.01f : 0, hi = grabbed < pts.Count - 1 ? pts[grabbed + 1].X - 0.01f : 1;
        pts[grabbed] = new SKPoint(Math.Clamp(p.X, lo, Math.Max(lo, hi)), p.Y);
        Notify();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        grabbed = -1;
        e.Pointer.Capture(null);
    }

    void Notify()
    {
        InvalidateVisual();
        Changed?.Invoke();
    }

    public override void Render(DrawingContext ctx)
    {
        var area = new Rect(Pad, Pad, 256, 256);
        ctx.DrawRectangle(Ui.Brush("BgBrush"), new Pen(Ui.Brush("LineBrush"), 2), area.Inflate(1), 3, 3);
        if (histogram is not null)
        {
            var bar = new SolidColorBrush(((ISolidColorBrush)Ui.Brush("TextFaintBrush")).Color, 0.35);
            for (int i = 0; i < 256; i++)
            {
                double v = Math.Min(1.0, histogram[i] / (double)histogramMax) * 256;
                if (v > 0) ctx.FillRectangle(bar, new Rect(Pad + i, Pad + 256 - v, 1.5, v));
            }
        }
        var gridPen = new Pen(Ui.Brush("LineBrush"), 1);
        for (int i = 1; i < 4; i++)
        {
            ctx.DrawLine(gridPen, new Point(Pad + i * 64, Pad), new Point(Pad + i * 64, Pad + 256));
            ctx.DrawLine(gridPen, new Point(Pad, Pad + i * 64), new Point(Pad + 256, Pad + i * 64));
        }
        ctx.DrawLine(new Pen(Ui.Brush("TextFaintBrush"), 1, new DashStyle(new double[] { 4, 4 }, 0)), new Point(Pad, Pad + 256), new Point(Pad + 256, Pad));

        IBrush line = channel switch
        {
            1 => new SolidColorBrush(Color.Parse("#FF6B5E")), 2 => new SolidColorBrush(Color.Parse("#4ADE80")), 3 => new SolidColorBrush(Color.Parse("#60A5FA")),
            _ => Ui.Brush("TextBrush"),
        };
        var table = Core.Curves.Table(Points);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(Pad, Pad + 256 - table[0] / 255.0 * 256), false);
            for (int i = 1; i < 256; i++) g.LineTo(new Point(Pad + i / 255.0 * 256, Pad + 256 - table[i] / 255.0 * 256));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(line, 2), geometry);
        foreach (var p in Points)
        {
            var s = ToScreen(p);
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#D6FF00")), new Pen(new SolidColorBrush(Color.Parse("#161616")), 1.5), new Rect(s.X - 4.5, s.Y - 4.5, 9, 9));
        }
    }
}

// ============================================================== about, shortcuts

public sealed class AboutDialog : DialogWindow
{
    public AboutDialog() : base("About", 430)
    {
        var mark = new Image { Width = 72, Height = 72, Source = new Bitmap(AssetLoader.Open(new Uri(MainWindow.MarkUri(this)))) };
        var title = new TextBlock { Text = "ARCHAEAPHOTO", Classes = { "display" }, FontSize = 22 };
        var version = Ui.MonoText($"Version {App.Version}", dim: true);
        var head = Ui.Row(16, mark, Ui.Column(6, title, version));
        Body.Children.Add(head);
        Body.Children.Add(new TextBlock
        {
            Text = "Photo editing and image compositing. Part of ArchaeaSuite, a free and open-source creative suite.",
            TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20,
        });
        Body.Children.Add(new TextBlock
        {
            Text = "Licensed under the GNU General Public License v3.0 or later. Built with Avalonia, SkiaSharp and LibTiff.NET; " +
                   "typefaces Archivo and IBM Plex Mono (SIL Open Font License); icons from Lucide (ISC).",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 18, Classes = { "dim" },
        });
        var link = new SelectableTextBlock { Text = "https://github.com/GavinVu/ArchaeaSuite", FontFamily = Ui.Mono, FontSize = 12 };
        Body.Children.Add(link);
        AddButton("Close", Close, true);
    }

    protected override void OnAccept() => Close();
}

public sealed class ShortcutsDialog : DialogWindow
{
    public ShortcutsDialog(IEnumerable<(string group, string action, string keys)> shortcuts) : base("Keyboard Shortcuts", 560)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var columns = new[] { new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 14, 0) }, new StackPanel { Spacing = 3 } };
        var groups = shortcuts.GroupBy(s => s.group).ToList();
        int total = groups.Sum(g => g.Count() + 2), running = 0;
        foreach (var g in groups)
        {
            var column = columns[running * 2 >= total ? 1 : 0];
            running += g.Count() + 2;
            var heading = Ui.Label(g.Key);
            heading.Margin = new Thickness(0, column.Children.Count == 0 ? 0 : 10, 0, 3);
            column.Children.Add(heading);
            foreach (var (_, action, keys) in g)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(new TextBlock { Text = action, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                var k = Ui.MonoText(keys, dim: true);
                Grid.SetColumn(k, 1);
                row.Children.Add(k);
                column.Children.Add(row);
            }
        }
        grid.Children.Add(columns[0]);
        Grid.SetColumn(columns[1], 1);
        grid.Children.Add(columns[1]);
        Body.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 520, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        AddButton("Close", Close, true);
    }

    protected override void OnAccept() => Close();
}
