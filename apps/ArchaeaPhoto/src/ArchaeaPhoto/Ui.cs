using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace ArchaeaPhoto;

/// <summary>A stroked 24-grid line icon that takes the surrounding text colour.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> DataProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Data));
    static readonly Dictionary<string, Geometry> Cache = new();

    static Icon() => AffectsRender<Icon>(DataProperty, TextElement.ForegroundProperty);

    public Icon() : this(null) { }

    public Icon(string? data, double size = 18)
    {
        Data = data;
        Width = Height = size;
        IsHitTestVisible = false;
    }

    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { Length: > 0 } d) return;
        if (!Cache.TryGetValue(d, out var geometry)) Cache[d] = geometry = Build(d);
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        var pen = new Pen(TextElement.GetForeground(this), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, geometry);
    }

    // Skia parses the SVG path data (it handles every compact form); the verbs are replayed into Avalonia geometry.
    static Geometry Build(string data)
    {
        var g = new StreamGeometry();
        using var path = SKPath.ParseSvgPathData(data);
        using var ctx = g.Open();
        if (path is null) return g;
        using var it = path.CreateRawIterator();
        var p = new SKPoint[4];
        bool open = false;
        static Point P(SKPoint q) => new(q.X, q.Y);
        SKPathVerb verb;
        while ((verb = it.Next(p)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (open) ctx.EndFigure(false);
                    ctx.BeginFigure(P(p[0]), false);
                    open = true;
                    break;
                case SKPathVerb.Line: ctx.LineTo(P(p[1])); break;
                case SKPathVerb.Quad: ctx.QuadraticBezierTo(P(p[1]), P(p[2])); break;
                case SKPathVerb.Cubic: ctx.CubicBezierTo(P(p[1]), P(p[2]), P(p[3])); break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(p[0], p[1], p[2], it.ConicWeight(), 2);
                    for (int i = 1; i + 1 < quads.Length; i += 2) ctx.QuadraticBezierTo(P(quads[i]), P(quads[i + 1]));
                    break;
                case SKPathVerb.Close:
                    ctx.EndFigure(true);
                    open = false;
                    break;
            }
        }
        if (open) ctx.EndFigure(false);
        return g;
    }
}

/// <summary>Builders for the small code-built pieces of UI (tool options, panels, dialogs).</summary>
public static class Ui
{
    public static FontFamily Mono => (FontFamily)Application.Current!.FindResource("MonoFont")!;
    public static FontFamily Display => (FontFamily)Application.Current!.FindResource("DisplayFont")!;

    /// <summary>Binds a property to a theme resource so it follows dark/light switches.</summary>
    public static T Dyn<T>(this T control, AvaloniaProperty property, string resourceKey) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(resourceKey));
        return control;
    }

    public static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Magenta;

    public static Color ToColor(this SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
    public static SKColor ToSk(this Color c) => new(c.R, c.G, c.B, c.A);
    public static string Hex(this SKColor c) => $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    public static TextBlock Label(string text) => new() { Text = text.ToUpperInvariant(), Classes = { "label" } };

    public static TextBlock MonoText(string text, bool dim = false)
    {
        var t = new TextBlock { Text = text, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center };
        if (dim) t.Classes.Add("dim");
        return t;
    }

    public static Button IconButton(string icon, string tip, Action click, double size = 16)
    {
        var b = new Button { Content = new Icon(icon, size), Classes = { "icon" }, Focusable = false };
        ToolTip.SetTip(b, tip);
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static Button TextButton(string text, Action click, string? cls = null)
    {
        var b = new Button { Content = text.ToUpperInvariant() };
        if (cls is not null) b.Classes.Add(cls);
        b.Click += (_, _) => click();
        return b;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        p.Children.AddRange(children);
        return p;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        p.Children.AddRange(children);
        return p;
    }

    /// <summary>A vertical hairline between groups in the options bar.</summary>
    public static Control Divider() =>
        new Border { Width = 2, Height = 20, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center }.Dyn(Border.BackgroundProperty, "LineBrush");

    /// <summary>Label + slider + live value. Returns the row and a setter that does not fire onChange.</summary>
    public static (Control row, Action<double> set) Slider(string label, double min, double max, double value, Action<double> onChange,
        Func<double, string>? format = null, double width = 96, double step = 1)
    {
        format ??= v => Math.Round(v).ToString();
        bool quiet = false;
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, Width = width, SmallChange = step, LargeChange = step * 10,
            TickFrequency = step, IsSnapToTickEnabled = step >= 1, VerticalAlignment = VerticalAlignment.Center, Focusable = false,
        };
        AutomationProperties.SetName(slider, label);
        var readout = MonoText(format(value));
        readout.MinWidth = 34;
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            readout.Text = format(slider.Value);
            if (!quiet) onChange(slider.Value);
        };
        void Set(double v)
        {
            quiet = true;
            slider.Value = v;
            quiet = false;
        }
        return (Row(7, Label(label), slider, readout), Set);
    }

    public static CheckBox Check(string label, bool value, Action<bool> onChange)
    {
        var c = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        c.IsCheckedChanged += (_, _) => onChange(c.IsChecked == true);
        return c;
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, T selected, Func<T, string> label, Action<T> onChange, double width = 130)
    {
        var list = items.ToList();
        var c = new ComboBox { Width = width, ItemsSource = list.Select(label).ToList(), SelectedIndex = list.IndexOf(selected), VerticalAlignment = VerticalAlignment.Center };
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedIndex >= 0 && c.SelectedIndex < list.Count) onChange(list[c.SelectedIndex]);
        };
        return c;
    }

    /// <summary>A text box that commits a number on Enter or when focus leaves.</summary>
    public static TextBox NumberBox(double value, Action<double> onCommit, double width = 64, string? name = null)
    {
        var t = new TextBox { Text = Format(value), Width = width, VerticalAlignment = VerticalAlignment.Center };
        if (name is not null) AutomationProperties.SetName(t, name);
        void Commit()
        {
            if (double.TryParse(t.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) onCommit(v);
        }
        t.LostFocus += (_, _) => Commit();
        t.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        };
        return t;
    }

    public static string Format(double v) => Math.Abs(v - Math.Round(v)) < 0.005 ? Math.Round(v).ToString() : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Copies a (small) Skia bitmap into an Avalonia bitmap for display in the UI.</summary>
    public static unsafe Bitmap ToBitmap(SKBitmap src)
    {
        var wb = new WriteableBitmap(new PixelSize(src.Width, src.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = wb.Lock();
        int bytes = src.Width * 4;
        for (int y = 0; y < src.Height; y++)
            Buffer.MemoryCopy((byte*)src.GetPixels() + (long)y * src.RowBytes, (byte*)fb.Address + (long)y * fb.RowBytes, bytes, bytes);
        return wb;
    }

    /// <summary>A 2×2 checkerboard tile used behind transparent pixels.</summary>
    public static SKShader Checker(float cell, bool dark)
    {
        using var bmp = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque));
        SKColor a = dark ? new SKColor(0x4A, 0x4A, 0x4A) : new SKColor(0xFF, 0xFF, 0xFF), b = dark ? new SKColor(0x3A, 0x3A, 0x3A) : new SKColor(0xD9, 0xD9, 0xD9);
        bmp.SetPixel(0, 0, a);
        bmp.SetPixel(1, 1, a);
        bmp.SetPixel(1, 0, b);
        bmp.SetPixel(0, 1, b);
        return SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateScale(cell, cell));
    }
}

/// <summary>
/// Modal dialog with the suite's chrome: custom header, 2px frame, action row.
/// Subclasses or callers fill <see cref="Body"/> and add buttons.
/// </summary>
public class DialogWindow : Window
{
    protected readonly StackPanel Body = new() { Spacing = 12, Margin = new Thickness(18, 16, 18, 4) };
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 14, 18, 18) };

    public DialogWindow(string title, double width = 380)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;

        var heading = new TextBlock { Text = title.ToUpperInvariant(), Classes = { "display" }, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var close = Ui.IconButton(Icons.Close, "Close", () => Close());
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(heading);
        Grid.SetColumn(close, 1);
        headerGrid.Children.Add(close);
        var header = new Border { Child = headerGrid, Padding = new Thickness(18, 0, 6, 0), Height = 40, BorderThickness = new Thickness(0, 0, 0, 2) }
            .Dyn(Border.BorderBrushProperty, "LineBrush").Dyn(Border.BackgroundProperty, "BgBrush");
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(Buttons, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(Buttons);
        root.Children.Add(Body);
        Content = new Border { Child = root, BorderThickness = new Thickness(2) }
            .Dyn(Border.BorderBrushProperty, "LineStrongBrush").Dyn(Border.BackgroundProperty, "SurfaceBrush");

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { OnCancel(); e.Handled = true; }
            else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() is not TextBox { AcceptsReturn: true }) { OnAccept(); e.Handled = true; }
        };
    }

    protected virtual void OnAccept() { }
    protected virtual void OnCancel() => Close();

    protected Button AddButton(string text, Action click, bool primary = false)
    {
        var b = Ui.TextButton(text, click, primary ? "primary" : "ghost");
        b.MinWidth = 96;
        Buttons.Children.Add(b);
        return b;
    }

    /// <summary>A "label: control" line for forms.</summary>
    protected static Control Field(string label, Control control, double labelWidth = 110)
    {
        var l = Ui.Label(label);
        l.Width = labelWidth;
        return Ui.Row(8, l, control);
    }
}

/// <summary>A message with one to three buttons; returns the index of the button chosen, or -1.</summary>
public sealed class MessageDialog : DialogWindow
{
    int result = -1;

    MessageDialog(string title, string message, string[] buttons, int primary) : base(title, 420)
    {
        Body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 });
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            AddButton(buttons[i], () => { result = index; Close(); }, i == primary);
        }
        acceptIndex = primary;
    }

    readonly int acceptIndex;

    protected override void OnAccept()
    {
        result = acceptIndex;
        Close();
    }

    public static async Task<int> Show(Window owner, string title, string message, string[] buttons, int primary = 0)
    {
        var d = new MessageDialog(title, message, buttons, primary);
        await d.ShowDialog(owner);
        return d.result;
    }

    public static Task Info(Window owner, string title, string message) => Show(owner, title, message, new[] { "OK" });
}
