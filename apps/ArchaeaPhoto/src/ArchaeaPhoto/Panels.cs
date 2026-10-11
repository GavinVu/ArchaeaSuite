using ArchaeaPhoto.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Transforms = ArchaeaPhoto.Core.Transforms;
using BlendMode = ArchaeaPhoto.Core.BlendMode;

namespace ArchaeaPhoto;

/// <summary>A titled dock panel: uppercase mono header with optional buttons, content below.</summary>
public class DockPanelBox : DockPanel
{
    protected readonly StackPanel HeaderButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    public DockPanelBox(string title)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(Ui.Label(title));
        Grid.SetColumn(HeaderButtons, 1);
        grid.Children.Add(HeaderButtons);
        var header = new Border { Child = grid, Classes = { "panelHeader" } };
        SetDock(header, Dock.Top);
        Children.Add(header);
    }
}

// ====================================================================== colour

/// <summary>Overlapping foreground/background swatches with swap and reset.</summary>
public sealed class ColorSwatches : Avalonia.Controls.Canvas
{
    readonly Editor ed;
    readonly Border fg, bg;
    /// <summary>True when the background swatch is the one being edited.</summary>
    public bool EditBackground { get; private set; }
    public event Action? TargetChanged;

    public ColorSwatches(Editor editor, double size = 26)
    {
        ed = editor;
        Width = size * 1.6 + 2;
        Height = size * 1.6 + 2;
        bg = Swatch(size, "Background colour", true);
        fg = Swatch(size, "Foreground colour", false);
        SetLeft(bg, size * 0.6);
        SetTop(bg, size * 0.6);
        Children.Add(bg);
        Children.Add(fg);
        ed.ColorsChanged += Refresh;
        Refresh();
    }

    Border Swatch(double size, string name, bool background)
    {
        var b = new Border { Width = size, Height = size, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3), Cursor = new Cursor(StandardCursorType.Hand) }
            .Dyn(Border.BorderBrushProperty, "LineStrongBrush");
        ToolTip.SetTip(b, name);
        AutomationProperties.SetName(b, name);
        b.PointerPressed += (_, e) =>
        {
            EditBackground = background;
            Refresh();
            TargetChanged?.Invoke();
            e.Handled = true;
        };
        return b;
    }

    void Refresh()
    {
        fg.Background = new SolidColorBrush(ed.Foreground.ToColor());
        bg.Background = new SolidColorBrush(ed.Background.ToColor());
        // The swatch being edited sits on top.
        fg.ZIndex = EditBackground ? 0 : 1;
        bg.ZIndex = EditBackground ? 1 : 0;
    }
}

public sealed class ColorPanel : DockPanelBox
{
    readonly Editor ed;
    readonly ColorSwatches swatches;
    readonly ColorSpectrum spectrum;
    readonly ColorSlider hue;
    readonly TextBox hex;
    readonly TextBlock rgb;
    bool syncing;

    static readonly string[] Presets =
    {
        "161616", "F5F5F0", "FFFFFF", "D6FF00", "FF6B5E", "FFB020", "FFE14D", "4ADE80",
        "22D3EE", "3B82F6", "8B5CF6", "EC4899", "7A7A72", "B4B4AB", "8B5A2B", "000000",
    };

    public ColorPanel(Editor editor) : base("Colour")
    {
        ed = editor;
        HeaderButtons.Children.Add(Ui.IconButton(Icons.Swap, "Swap foreground and background (X)", ed.SwapColors));
        HeaderButtons.Children.Add(Ui.IconButton(Icons.Reset, "Reset to black and white (D)", ed.ResetColors));

        swatches = new ColorSwatches(ed, 28);
        swatches.TargetChanged += FromEditor;
        spectrum = new ColorSpectrum
        {
            Components = ColorSpectrumComponents.SaturationValue, Shape = ColorSpectrumShape.Box,
            Width = 148, Height = 148, CornerRadius = new CornerRadius(4),
        };
        AutomationProperties.SetName(spectrum, "Saturation and brightness");
        hue = new ColorSlider
        {
            ColorModel = ColorModel.Hsva, ColorComponent = ColorComponent.Component1, Orientation = Orientation.Horizontal,
            Height = 18, Margin = new Thickness(0, 8, 0, 0), IsAlphaVisible = false,
        };
        AutomationProperties.SetName(hue, "Hue");
        hex = new TextBox { Width = 84, MaxLength = 7 };
        AutomationProperties.SetName(hex, "Hex colour");
        rgb = Ui.MonoText("", dim: true);

        spectrum.ColorChanged += (_, _) =>
        {
            if (syncing) return;
            var hsv = spectrum.HsvColor;
            syncing = true;
            hue.HsvColor = hsv;
            syncing = false;
            Apply(hsv.ToRgb());
        };
        hue.ColorChanged += (_, _) =>
        {
            if (syncing) return;
            // Keep saturation and value from the spectrum; only the hue moves.
            var s = spectrum.HsvColor;
            var hsv = new HsvColor(1, hue.HsvColor.H, s.S, s.V);
            syncing = true;
            spectrum.HsvColor = hsv;
            syncing = false;
            Apply(hsv.ToRgb());
        };
        void CommitHex()
        {
            if (Color.TryParse("#" + (hex.Text ?? "").Trim().TrimStart('#'), out var c)) Apply(c);
            else FromEditor();
        }
        hex.LostFocus += (_, _) => CommitHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitHex(); ed.Canvas.Focus(); e.Handled = true; }
        };

        var presets = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var p in Presets)
        {
            var color = Color.Parse("#" + p);
            var chip = new Border
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(color), Cursor = new Cursor(StandardCursorType.Hand),
            }.Dyn(Border.BorderBrushProperty, "LineBrush");
            ToolTip.SetTip(chip, "#" + p);
            chip.PointerPressed += (_, _) => Apply(color);
            presets.Children.Add(chip);
        }

        // Picker on the left; swatches and values beside it.
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        top.Children.Add(spectrum);
        var fields = Ui.Column(8, swatches, Ui.Column(4, Ui.Label("Hex"), hex), rgb);
        fields.Margin = new Thickness(12, 0, 0, 0);
        swatches.HorizontalAlignment = HorizontalAlignment.Left;
        hex.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(fields, 1);
        top.Children.Add(fields);

        var body = new StackPanel { Margin = new Thickness(10) };
        body.Children.Add(top);
        body.Children.Add(hue);
        body.Children.Add(presets);
        Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        ed.ColorsChanged += FromEditor;
        FromEditor();
    }

    void Apply(Color c)
    {
        syncing = true;
        if (swatches.EditBackground) ed.SetBackground(c.ToSk());
        else ed.SetForeground(c.ToSk());
        syncing = false;
        UpdateText(c.ToSk());
    }

    void UpdateText(SKColor c)
    {
        if (!hex.IsFocused) hex.Text = "#" + c.Hex();
        rgb.Text = $"R {c.Red}\nG {c.Green}\nB {c.Blue}";
    }

    void FromEditor()
    {
        var c = swatches.EditBackground ? ed.Background : ed.Foreground;
        UpdateText(c);
        if (syncing) return;
        syncing = true;
        var hsv = c.ToColor().ToHsv();
        // Greys have no hue; keep the one already showing so the picker does not jump to red.
        if (hsv.S < 0.001 || hsv.V < 0.001) hsv = new HsvColor(1, spectrum.HsvColor.H, hsv.S, hsv.V);
        spectrum.HsvColor = hsv;
        hue.HsvColor = hsv;
        syncing = false;
    }
}

// ====================================================================== layers

public sealed class LayersPanel : DockPanelBox
{
    readonly Editor ed;
    readonly StackPanel rows = new();
    readonly ComboBox blend;
    readonly Slider opacity;
    readonly TextBlock opacityText;
    readonly List<(Layer layer, Border row)> items = new();
    bool refreshing;

    // Drag-to-reorder state.
    Layer? dragLayer;
    Point dragStart;
    bool dragging;
    (Layer target, int where)? drop; // where: -1 above, 0 into group, 1 below

    static readonly BlendMode[] Modes = Enum.GetValues<BlendMode>();

    public LayersPanel(Editor editor) : base("Layers")
    {
        ed = editor;
        blend = new ComboBox { ItemsSource = Modes.Select(m => m.Label()).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(blend, "Blend mode");
        ToolTip.SetTip(blend, "Blend mode");
        blend.SelectionChanged += (_, _) =>
        {
            if (!refreshing && ed.Doc is { Active: { } l } d && blend.SelectedIndex >= 0) d.SetBlend(l, Modes[blend.SelectedIndex]);
        };
        opacity = new Slider { Minimum = 0, Maximum = 100, Value = 100, Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(opacity, "Layer opacity");
        opacityText = Ui.MonoText("100%");
        opacityText.Width = 38;
        opacityText.TextAlignment = TextAlignment.Right;
        opacity.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            opacityText.Text = $"{opacity.Value:0}%";
            if (!refreshing && ed.Doc is { Active: { } l } d) d.SetOpacity(l, (float)(opacity.Value / 100));
        };

        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(10, 8, 10, 6) };
        var blendLabel = Ui.Label("Blend");
        blendLabel.Margin = new Thickness(0, 0, 8, 0);
        var opacityLabel = Ui.Label("Opacity");
        opacityLabel.Margin = new Thickness(0, 0, 8, 0);
        controls.Children.Add(blendLabel);
        Grid.SetColumn(blend, 1);
        Grid.SetColumnSpan(blend, 2);
        controls.Children.Add(blend);
        Grid.SetRow(opacityLabel, 1);
        controls.Children.Add(opacityLabel);
        Grid.SetRow(opacity, 1);
        Grid.SetColumn(opacity, 1);
        controls.Children.Add(opacity);
        Grid.SetRow(opacityText, 1);
        Grid.SetColumn(opacityText, 2);
        controls.Children.Add(opacityText);
        var controlsBox = new Border { Child = controls, BorderThickness = new Thickness(0, 0, 0, 2) }.Dyn(Border.BorderBrushProperty, "LineBrush");
        SetDock(controlsBox, Dock.Top);
        Children.Add(controlsBox);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3) };
        footer.Children.Add(Ui.IconButton(Icons.Plus, "New layer (Ctrl+Shift+N)", () => ed.Doc?.AddLayer()));
        footer.Children.Add(Ui.IconButton(Icons.Folder, "New group (Ctrl+G)", () => ed.Doc?.AddGroup()));
        footer.Children.Add(Ui.IconButton(Icons.Mask, "Add layer mask", () => ed.Doc?.AddMask()));
        footer.Children.Add(Ui.IconButton(Icons.Copy, "Duplicate layer (Ctrl+J)", () => ed.Doc?.DuplicateActive()));
        footer.Children.Add(Ui.IconButton(Icons.ArrowUp, "Move layer up (Ctrl+])", () => ed.Doc?.MoveActive(1)));
        footer.Children.Add(Ui.IconButton(Icons.ArrowDown, "Move layer down (Ctrl+[)", () => ed.Doc?.MoveActive(-1)));
        footer.Children.Add(Ui.IconButton(Icons.Trash, "Delete layer", () => ed.Doc?.DeleteActive()));
        var footerBox = new Border { Child = footer, BorderThickness = new Thickness(0, 2, 0, 0) }.Dyn(Border.BorderBrushProperty, "LineBrush").Dyn(Border.BackgroundProperty, "BgBrush");
        SetDock(footerBox, Dock.Bottom);
        Children.Add(footerBox);

        Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ed.DocChanged += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        refreshing = true;
        rows.Children.Clear();
        items.Clear();
        var doc = ed.Doc;
        IsEnabled = doc is not null;
        if (doc is not null)
        {
            Build(doc, doc.Root, 0);
            var a = doc.Active;
            blend.SelectedIndex = a is null ? -1 : Array.IndexOf(Modes, a.Blend);
            opacity.Value = a is null ? 100 : Math.Round(a.Opacity * 100);
            blend.IsEnabled = opacity.IsEnabled = a is not null;
        }
        refreshing = false;
    }

    void Build(Document doc, GroupLayer parent, int depth)
    {
        for (int i = parent.Children.Count - 1; i >= 0; i--)
        {
            var layer = parent.Children[i];
            var row = Row(doc, layer, depth);
            items.Add((layer, row));
            rows.Children.Add(row);
            if (layer is GroupLayer { Expanded: true } g) Build(doc, g, depth + 1);
        }
    }

    Border Row(Document doc, Layer layer, int depth)
    {
        bool active = layer == doc.Active;
        bool maskPress = false;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,Auto,Auto,Auto,*"), Height = 38 };

        var eye = new Button { Content = new Icon(layer.Visible ? Icons.Eye : Icons.EyeOff, 15), Classes = { "icon" }, Focusable = false, Opacity = layer.Visible ? 1 : 0.55 };
        ToolTip.SetTip(eye, layer.Visible ? "Hide layer" : "Show layer");
        AutomationProperties.SetName(eye, (layer.Visible ? "Hide " : "Show ") + layer.Name);
        eye.Click += (_, _) => doc.SetVisible(layer, !layer.Visible);
        grid.Children.Add(eye);

        var lead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * 14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (layer is GroupLayer group)
        {
            var twist = Ui.IconButton(group.Expanded ? Icons.ChevronDown : Icons.ChevronRight, group.Expanded ? "Collapse group" : "Expand group",
                () => doc.SetExpanded(group, !group.Expanded), 14);
            twist.Width = 20;
            lead.Children.Add(twist);
            lead.Children.Add(new Icon(group.Expanded ? Icons.FolderOpen : Icons.Folder, 18) { Margin = new Thickness(2, 0, 6, 0) });
        }
        else
        {
            var thumb = Thumb(Thumbs.Layer(doc, (RasterLayer)layer), active && !doc.EditMask, "Layer pixels");
            lead.Children.Add(thumb);
        }
        Grid.SetColumn(lead, 1);
        grid.Children.Add(lead);

        if (layer.Mask is not null)
        {
            var mask = Thumb(Thumbs.Mask(doc, layer), active && doc.EditMask, layer.MaskEnabled ? "Layer mask (click to paint on it)" : "Layer mask (disabled)");
            mask.Opacity = layer.MaskEnabled ? 1 : 0.4;
            mask.PointerPressed += (_, _) => maskPress = true; // the row selects on release
            Grid.SetColumn(mask, 2);
            grid.Children.Add(mask);
        }

        var name = new TextBlock
        {
            Text = layer.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 8, 0),
            FontWeight = active ? FontWeight.Bold : FontWeight.Medium, FontSize = 12.5, Opacity = layer.Visible ? 1 : 0.55,
        };
        if (layer is RasterLayer { Text: not null }) name.FontStyle = FontStyle.Italic;
        Grid.SetColumn(name, 4);
        grid.Children.Add(name);

        var row = new Border { Child = grid, BorderThickness = new Thickness(3, 0, 0, 1), Background = Brushes.Transparent, Tag = layer };
        row.Dyn(Border.BorderBrushProperty, "LineBrush");
        if (active)
        {
            row.Dyn(Border.BackgroundProperty, "Surface2Brush");
            // Lime marker on the left edge of the active layer.
            row.BorderBrush = new SolidColorBrush(Color.Parse("#D6FF00"));
        }
        AutomationProperties.SetName(row, layer.Name);

        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) { BeginRename(doc, layer, grid, name); return; }
            dragLayer = layer;
            dragStart = e.GetPosition(rows);
            dragging = false;
            e.Pointer.Capture(row);
        };
        row.PointerMoved += (_, e) =>
        {
            if (dragLayer != layer) return;
            var p = e.GetPosition(rows);
            if (!dragging && Math.Abs(p.Y - dragStart.Y) < 5) return;
            dragging = true;
            UpdateDrop(p);
        };
        row.PointerReleased += (_, e) =>
        {
            e.Pointer.Capture(null);
            var (wasDragging, target) = (dragging, drop);
            dragLayer = null;
            dragging = false;
            ClearDropMarks();
            if (wasDragging && target is { } t) Drop(doc, layer, t.target, t.where);
            // Selecting rebuilds the rows, so it waits for the release rather than the press.
            else if (!wasDragging) Select(doc, layer, maskPress);
            maskPress = false;
        };
        row.PointerCaptureLost += (_, _) =>
        {
            dragLayer = null;
            dragging = false;
            ClearDropMarks();
        };
        row.ContextMenu = RowMenu(doc, layer, grid, name);
        return row;
    }

    static Border Thumb(Avalonia.Media.Imaging.Bitmap image, bool selected, string tip)
    {
        var b = new Border
        {
            Width = 40, Height = 30, Margin = new Thickness(2, 0, 2, 0), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2), ClipToBounds = true,
            Child = new Image { Source = image, Stretch = Stretch.Fill },
        };
        if (selected) b.BorderBrush = new SolidColorBrush(Color.Parse("#D6FF00"));
        else b.Dyn(Border.BorderBrushProperty, "LineBrush");
        ToolTip.SetTip(b, tip);
        return b;
    }

    void Select(Document doc, Layer layer, bool mask)
    {
        if (doc.Active == layer && doc.EditMask == mask) return;
        ed.Tool.Cancel();
        doc.Active = layer;
        doc.EditMask = mask && layer.Mask is not null;
        doc.NotifyChanged();
    }

    void BeginRename(Document doc, Layer layer, Grid grid, TextBlock name)
    {
        var box = new TextBox { Text = layer.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontFamily = name.FontFamily, FontSize = 12.5, MinHeight = 26 };
        AutomationProperties.SetName(box, "Layer name");
        Grid.SetColumn(box, 4);
        name.IsVisible = false;
        grid.Children.Add(box);
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            string text = box.Text ?? "";
            grid.Children.Remove(box);
            name.IsVisible = true;
            if (commit) doc.Rename(layer, text);
            ed.Canvas.Focus();
        }
        box.LostFocus += (_, _) => Finish(true);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
    }

    /// <summary>Starts renaming the active layer (F2).</summary>
    public void RenameActive()
    {
        if (ed.Doc is not { Active: { } active } doc) return;
        var row = items.FirstOrDefault(i => i.layer == active).row;
        if (row?.Child is Grid grid && grid.Children.OfType<TextBlock>().LastOrDefault() is { } name) BeginRename(doc, active, grid, name);
    }

    ContextMenu RowMenu(Document doc, Layer layer, Grid grid, TextBlock name)
    {
        MenuItem Item(string header, Action run, bool enabled = true)
        {
            var m = new MenuItem { Header = header, IsEnabled = enabled };
            m.Click += (_, _) =>
            {
                Select(doc, layer, false);
                run();
            };
            return m;
        }
        var menu = new ContextMenu();
        menu.Items.Add(Item("Rename", () => BeginRename(doc, layer, grid, name)));
        menu.Items.Add(Item("Duplicate", doc.DuplicateActive));
        menu.Items.Add(Item("Delete", doc.DeleteActive));
        menu.Items.Add(new Separator());
        if (layer.Mask is null) menu.Items.Add(Item("Add Mask", doc.AddMask));
        else
        {
            menu.Items.Add(Item(layer.MaskEnabled ? "Disable Mask" : "Enable Mask", () => doc.SetMaskEnabled(!layer.MaskEnabled)));
            menu.Items.Add(Item("Invert Mask", doc.InvertMask));
            menu.Items.Add(Item("Apply Mask", doc.ApplyMask, layer is RasterLayer));
            menu.Items.Add(Item("Delete Mask", doc.DeleteMask));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Merge Down", doc.MergeDown));
        return menu;
    }

    // ------------------------------------------------------------ drag reorder

    void UpdateDrop(Point p)
    {
        ClearDropMarks();
        drop = null;
        foreach (var (layer, row) in items)
        {
            var top = row.TranslatePoint(new Point(0, 0), rows);
            if (top is null) continue;
            double y = p.Y - top.Value.Y, h = row.Bounds.Height;
            if (y < 0 || y > h) continue;
            if (layer == dragLayer) return;
            int where = layer is GroupLayer && y > h * 0.3 && y < h * 0.7 ? 0 : y < h / 2 ? -1 : 1;
            drop = (layer, where);
            var lime = new SolidColorBrush(Color.Parse("#D6FF00"));
            if (where == 0) row.Background = new SolidColorBrush(Color.Parse("#D6FF00"), 0.25);
            else
            {
                row.BorderBrush = lime;
                row.BorderThickness = where < 0 ? new Thickness(3, 3, 0, 1) : new Thickness(3, 0, 0, 3);
            }
            return;
        }
    }

    void ClearDropMarks()
    {
        if (drop is null) return;
        drop = null;
        // Rows carry several per-state brushes; rebuilding is the simplest way to restore them.
        Dispatcher.UIThread.Post(Refresh);
    }

    static void Drop(Document doc, Layer moving, Layer target, int where)
    {
        var parent = target.Parent!;
        int index = parent.Children.IndexOf(target);
        if (where == 0 && target is GroupLayer into) doc.MoveLayer(moving, into, into.Children.Count);
        else if (where < 0) doc.MoveLayer(moving, parent, index + 1);
        else if (target is GroupLayer { Expanded: true, Children.Count: > 0 } open) doc.MoveLayer(moving, open, open.Children.Count);
        else doc.MoveLayer(moving, parent, index);
    }
}

/// <summary>Small previews for the layers panel.</summary>
public static class Thumbs
{
    const int W = 80, H = 60;

    static (SKBitmap bmp, SKCanvas canvas) Start(Document doc, bool checker)
    {
        var bmp = Px.NewColor(W, H);
        var c = new SKCanvas(bmp);
        if (checker)
        {
            using var shader = Ui.Checker(6, false);
            using var p = new SKPaint { Shader = shader };
            c.DrawPaint(p);
        }
        // Fit the canvas into the thumbnail, centred.
        float scale = Math.Min((float)W / doc.Width, (float)H / doc.Height);
        c.Translate((W - doc.Width * scale) / 2, (H - doc.Height * scale) / 2);
        c.Scale(scale);
        c.ClipRect(new SKRect(0, 0, doc.Width, doc.Height));
        return (bmp, c);
    }

    public static Avalonia.Media.Imaging.Bitmap Layer(Document doc, RasterLayer layer)
    {
        var (bmp, c) = Start(doc, true);
        using (bmp)
        using (c)
        using (var p = new SKPaint { FilterQuality = SKFilterQuality.Low })
        {
            Px.Draw(c, layer.Bitmap, SKRect.Create(layer.Bitmap.Width, layer.Bitmap.Height), SKRect.Create(layer.X, layer.Y, layer.Bitmap.Width, layer.Bitmap.Height), p);
            return Ui.ToBitmap(bmp);
        }
    }

    public static Avalonia.Media.Imaging.Bitmap Mask(Document doc, Layer layer)
    {
        var (bmp, c) = Start(doc, false);
        using (bmp)
        using (c)
        using (var p = new SKPaint { FilterQuality = SKFilterQuality.Low, Color = SKColors.White })
        {
            var m = layer.Mask!;
            var area = SKRect.Create(layer.MaskX, layer.MaskY, m.Width, m.Height);
            // White where the layer shows, black where it is hidden; outside the mask everything shows.
            c.Clear(SKColors.White);
            using (var black = new SKPaint { Color = SKColors.Black }) c.DrawRect(area, black);
            Px.Draw(c, m, SKRect.Create(m.Width, m.Height), area, p);
            return Ui.ToBitmap(bmp);
        }
    }
}

// ===================================================================== history

public sealed class HistoryPanel : DockPanel
{
    readonly Editor ed;
    readonly StackPanel rows = new();
    readonly ScrollViewer scroll;

    public HistoryPanel(Editor editor)
    {
        ed = editor;
        scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Children.Add(scroll);
        ed.DocChanged += Refresh;
        Refresh();
    }

    void Refresh()
    {
        rows.Children.Clear();
        if (ed.Doc is not { } doc) return;
        var h = doc.History;
        Control? current = null;
        for (int i = 0; i <= h.Items.Count; i++)
        {
            int position = i;
            string text = i == 0 ? (h.Trimmed > 0 ? "Earlier steps (discarded)" : "Start") : h.Items[i - 1].Name;
            bool isCurrent = position == h.Position, undone = position > h.Position;
            var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Medium, Opacity = undone ? 0.45 : 1, VerticalAlignment = VerticalAlignment.Center };
            var row = new Border { Child = label, Padding = new Thickness(10, 0), Height = 26, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), BorderThickness = new Thickness(3, 0, 0, 0) };
            if (isCurrent)
            {
                row.Dyn(Border.BackgroundProperty, "Surface2Brush");
                row.BorderBrush = new SolidColorBrush(Color.Parse("#D6FF00"));
                current = row;
            }
            row.PointerPressed += (_, _) =>
            {
                ed.Tool.Cancel();
                doc.History.JumpTo(position);
            };
            rows.Children.Add(row);
        }
        if (current is not null) Dispatcher.UIThread.Post(() => current.BringIntoView(), DispatcherPriority.Background);
    }
}

// ================================================================== properties

public sealed class PropertiesPanel : ScrollViewer
{
    readonly Editor ed;
    readonly StackPanel body = new() { Margin = new Thickness(10), Spacing = 8 };

    public PropertiesPanel(Editor editor)
    {
        ed = editor;
        Content = body;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ed.DocChanged += Refresh;
        Refresh();
    }

    static Control Line(string label, Control value)
    {
        var l = Ui.Label(label);
        l.Width = 68;
        return Ui.Row(6, l, value);
    }

    void Refresh()
    {
        body.Children.Clear();
        if (ed.Doc is not { } doc)
        {
            body.Children.Add(Ui.MonoText("No document open", dim: true));
            return;
        }
        body.Children.Add(Line("Canvas", Ui.MonoText($"{doc.Width} × {doc.Height} px")));
        if (doc.Active is not { } layer)
        {
            body.Children.Add(Ui.MonoText("No layer selected", dim: true));
            return;
        }
        string kind = layer switch { GroupLayer g => $"Group · {g.Children.Count} item{(g.Children.Count == 1 ? "" : "s")}", RasterLayer { Text: not null } => "Text layer", _ => "Pixel layer" };
        body.Children.Add(Line("Layer", new TextBlock { Text = layer.Name, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 }));
        body.Children.Add(Line("Type", Ui.MonoText(kind)));
        var b = Document.ContentBounds(layer);
        if (!b.IsEmpty)
        {
            var x = Ui.NumberBox(b.Left, v => Nudge(doc, layer, (int)Math.Round(v) - Document.ContentBounds(layer).Left, 0), 62, "Layer X position");
            var y = Ui.NumberBox(b.Top, v => Nudge(doc, layer, 0, (int)Math.Round(v) - Document.ContentBounds(layer).Top), 62, "Layer Y position");
            body.Children.Add(Line("Position", Ui.Row(6, Ui.MonoText("X", true), x, Ui.MonoText("Y", true), y)));
            body.Children.Add(Line("Size", Ui.MonoText($"{b.Width} × {b.Height} px")));
        }
        if (layer.Mask is not null)
        {
            body.Children.Add(new Border { Height = 2, Margin = new Thickness(0, 2) }.Dyn(Border.BackgroundProperty, "LineBrush"));
            body.Children.Add(Line("Mask", Ui.Check("Enabled", layer.MaskEnabled, v => doc.SetMaskEnabled(v))));
            body.Children.Add(Line("Paint on", Ui.Combo(new[] { false, true }, doc.EditMask, v => v ? "Mask" : "Layer pixels", v =>
            {
                if (doc.EditMask == v) return;
                doc.EditMask = v;
                doc.NotifyChanged();
            }, 130)));
            var buttons = Ui.Row(6, Ui.TextButton("Invert", doc.InvertMask, "ghost"), Ui.TextButton("Apply", doc.ApplyMask, "ghost"), Ui.TextButton("Delete", doc.DeleteMask, "ghost"));
            body.Children.Add(buttons);
        }
    }

    static void Nudge(Document doc, Layer layer, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        doc.Edit("Move", () => Transforms.Offset(layer, dx, dy));
    }
}
