using ArchaeaPhoto.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;

namespace ArchaeaPhoto;

/// <summary>
/// The workspace: title bar with menus, tool options, toolbar, document tabs and
/// canvas, right-hand panels and status bar. Commands live in MainWindow.Commands.cs.
/// </summary>
public sealed partial class MainWindow : Window
{
    readonly Editor ed = new();
    readonly CanvasView canvas;
    readonly Avalonia.Controls.Canvas overlayHost = new() { ClipToBounds = true };
    readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel optionsHost = new() { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock toolName = new() { Classes = { "display" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Icon toolIcon = new(null, 18);
    readonly TextBlock statusText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock positionText = Ui.MonoText("", dim: true), sizeText = Ui.MonoText("", dim: true), zoomText = Ui.MonoText("100%");
    readonly TextBlock titleText = new() { Classes = { "label" }, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    readonly Border welcome = new();
    readonly StackPanel brand = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0), IsHitTestVisible = false };
    readonly Dictionary<Tool, ToggleButton> toolButtons = new();
    readonly Menu menu = new();
    readonly DispatcherTimer flashTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly Border rootBorder = new();
    readonly LayersPanel layersPanel;
    ColumnDefinition rightColumn = null!;
    bool closeConfirmed;

    public Editor Editor => ed;

    public static string MarkUri(StyledElement context) =>
        context.ActualThemeVariant == ThemeVariant.Light ? "avares://ArchaeaPhoto/Assets/mark.png" : "avares://ArchaeaPhoto/Assets/mark-inverse.png";

    public MainWindow()
    {
        Title = "ArchaeaPhoto";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ArchaeaPhoto/Assets/icon.ico")));
        var s = App.Settings;
        Width = Math.Max(1000, s.WindowWidth);
        Height = Math.Max(660, s.WindowHeight);
        MinWidth = 980;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.Maximized) WindowState = WindowState.Maximized;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;

        ed.Window = this;
        ed.OverlayHost = overlayHost;
        canvas = new CanvasView(ed);
        ed.Canvas = canvas;
        ed.Tools.AddRange(new Tool[]
        {
            new MoveTool(ed), new MarqueeTool(ed, false), new MarqueeTool(ed, true), new LassoTool(ed), new WandTool(ed), new CropTool(ed),
            new EyedropperTool(ed), new BrushTool(ed), new EraserTool(ed), new CloneTool(ed), new BucketTool(ed), new GradientTool(ed),
            new TextTool(ed), new ShapeTool(ed), new HandTool(ed), new ZoomTool(ed), new TransformTool(ed),
        });
        layersPanel = new LayersPanel(ed);

        BuildMenu();
        var layout = new Grid { RowDefinitions = new RowDefinitions("38,44,*,28") };
        Add(layout, BuildTitleBar(), 0);
        Add(layout, BuildOptionsBar(), 1);
        Add(layout, BuildWorkspace(), 2);
        Add(layout, BuildStatusBar(), 3);
        rootBorder.Child = layout;
        Content = rootBorder;
        // A maximised window with a custom title bar overhangs the screen edge; pad the overhang away.
        this.GetObservable(OffScreenMarginProperty).Subscribe(new AnonymousObserver<Thickness>(m => rootBorder.Padding = m));

        ed.ToolChanged += OnToolChanged;
        ed.DocChanged += OnDocChanged;
        ed.DocListChanged += RefreshTabs;
        ed.Flashed += Flash;
        canvas.ViewChanged += UpdateStatus;
        canvas.PointerInfoChanged += UpdatePointer;
        flashTimer.Tick += (_, _) => { flashTimer.Stop(); statusText.Text = ed.Tool.Hint; };
        ActualThemeVariantChanged += (_, _) => { BuildBrand(); canvas.Redraw(); layersPanel.Refresh(); };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);

        ed.SetTool(ed.Get<BrushTool>());
        BuildBrand();
        RefreshTabs();
        OnDocChanged();
    }

    static void Add(Grid grid, Control child, int row, int column = 0)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    // --------------------------------------------------------------- title bar

    void BuildBrand()
    {
        brand.Children.Clear();
        bool light = ActualThemeVariant == ThemeVariant.Light;
        brand.Children.Add(new Image { Width = 24, Height = 24, Source = new Bitmap(AssetLoader.Open(new Uri(MarkUri(this)))) });
        var word = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        word.Children.Add(new TextBlock { Text = "ARCHAEA", Classes = { "display" }, FontSize = 13 });
        // Lime is a text colour only on dark; on paper it becomes a highlight behind ink.
        var photo = new TextBlock { Text = "PHOTO", Classes = { "display" }, FontSize = 13, Foreground = new SolidColorBrush(Color.Parse(light ? "#161616" : "#D6FF00")) };
        word.Children.Add(new Border
        {
            Child = photo, Padding = new Thickness(light ? 3 : 0, 0), Margin = new Thickness(light ? 2 : 0, 0, 0, 0),
            Background = light ? new SolidColorBrush(Color.Parse("#D6FF00")) : Brushes.Transparent,
        });
        brand.Children.Add(word);
    }

    Control BuildTitleBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var drag = new Border { Background = Brushes.Transparent, Child = titleText };
        Grid.SetColumnSpan(drag, 4);
        drag.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) ToggleMaximize();
            else BeginMoveDrag(e);
        };
        grid.Children.Add(drag);
        Add(grid, brand, 0, 0);
        Add(grid, menu, 0, 1);

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var max = Ui.IconButton(Icons.Maximize, "Maximise", ToggleMaximize, 13);
        var min = Ui.IconButton(Icons.Minus, "Minimise", () => WindowState = WindowState.Minimized, 15);
        var close = Ui.IconButton(Icons.Close, "Close", Close, 16);
        foreach (var b in new[] { min, max, close }) b.Classes.Add("caption");
        close.Classes.Add("close");
        this.GetObservable(WindowStateProperty).Subscribe(new AnonymousObserver<WindowState>(state =>
        {
            bool maximised = state == WindowState.Maximized;
            max.Content = new Icon(maximised ? Icons.Restore : Icons.Maximize, 13);
            ToolTip.SetTip(max, maximised ? "Restore" : "Maximise");
        }));
        controls.Children.AddRange(new[] { min, max, close });
        Add(grid, controls, 0, 3);
        return new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 2) }.Dyn(Border.BorderBrushProperty, "LineBrush").Dyn(Border.BackgroundProperty, "BgBrush");
    }

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ------------------------------------------------------------- options bar

    Control BuildOptionsBar()
    {
        var name = Ui.Row(8, toolIcon, toolName);
        name.MinWidth = 150;
        name.Margin = new Thickness(14, 0, 0, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        grid.Children.Add(name);
        Add(grid, Ui.Divider(), 0, 1);
        Add(grid, new ScrollViewer
        {
            Content = optionsHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(10, 0, 10, 0),
        }, 0, 2);
        return new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 2) }.Dyn(Border.BorderBrushProperty, "LineBrush").Dyn(Border.BackgroundProperty, "SurfaceBrush");
    }

    void OnToolChanged()
    {
        foreach (var (tool, button) in toolButtons) button.IsChecked = tool == ed.Tool;
        toolIcon.Data = ed.Tool.IconData;
        toolName.Text = ed.Tool.Name.ToUpperInvariant();
        optionsHost.Children.Clear();
        optionsHost.Children.AddRange(ed.Tool.Options());
        flashTimer.Stop();
        statusText.Text = ed.Tool.Hint;
    }

    // --------------------------------------------------------------- workspace

    Control BuildWorkspace()
    {
        rightColumn = new ColumnDefinition(Math.Clamp(App.Settings.RightPanelWidth, 240, 520), GridUnitType.Pixel) { MinWidth = 240, MaxWidth = 560 };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(rightColumn);
        Add(grid, BuildToolbar(), 0, 0);
        Add(grid, BuildCentre(), 0, 1);
        Add(grid, new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns }, 0, 2);
        Add(grid, BuildRightDock(), 0, 3);
        return grid;
    }

    Control BuildToolbar()
    {
        var stack = new StackPanel { Margin = new Thickness(5, 6), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var tool in ed.Tools.Where(t => !t.Transient))
        {
            string tip = tool.Shortcut == Key.None ? tool.Name : $"{tool.Name} ({tool.Shortcut})";
            var b = new ToggleButton { Content = new Icon(tool.IconData, 18), Classes = { "tool" }, Focusable = false };
            ToolTip.SetTip(b, tip);
            ToolTip.SetPlacement(b, PlacementMode.Right);
            AutomationProperties.SetName(b, tool.Name);
            b.Click += (_, _) =>
            {
                ed.SetTool(tool);
                b.IsChecked = ed.Tool == tool;
            };
            toolButtons[tool] = b;
            stack.Children.Add(b);
        }
        var swatches = new ColorSwatches(ed, 20) { Margin = new Thickness(0, 10, 0, 4), HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new Border { Height = 2, Margin = new Thickness(0, 6, 0, 0) }.Dyn(Border.BackgroundProperty, "LineBrush"));
        stack.Children.Add(swatches);
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        return new Border { Child = scroll, BorderThickness = new Thickness(0, 0, 2, 0) }.Dyn(Border.BorderBrushProperty, "LineBrush").Dyn(Border.BackgroundProperty, "SurfaceBrush");
    }

    Control BuildCentre()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var tabScroll = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Add(grid, new Border { Child = tabScroll, Height = 32, BorderThickness = new Thickness(0, 0, 0, 2) }.Dyn(Border.BorderBrushProperty, "LineBrush").Dyn(Border.BackgroundProperty, "BgBrush"), 0);
        var stage = new Panel();
        stage.Children.Add(canvas);
        stage.Children.Add(overlayHost);
        stage.Children.Add(welcome);
        Add(grid, stage, 1);
        return grid;
    }

    Control BuildRightDock()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("276,Auto,172,Auto,*") };
        grid.RowDefinitions[0].MinHeight = 120;
        grid.RowDefinitions[2].MinHeight = 80;
        grid.RowDefinitions[4].MinHeight = 140;
        Add(grid, new ColorPanel(ed), 0);
        Add(grid, new GridSplitter { Height = 4, ResizeDirection = GridResizeDirection.Rows }, 1);
        Add(grid, BuildTabbedPanel(("Properties", new PropertiesPanel(ed)), ("History", new HistoryPanel(ed))), 2);
        Add(grid, new GridSplitter { Height = 4, ResizeDirection = GridResizeDirection.Rows }, 3);
        Add(grid, layersPanel, 4);
        return new Border { Child = grid }.Dyn(Border.BackgroundProperty, "SurfaceBrush");
    }

    /// <summary>Two or more panels sharing one slot, switched by their header labels.</summary>
    static Control BuildTabbedPanel(params (string title, Control content)[] pages)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new Panel();
        var buttons = new List<ToggleButton>();
        void Show(int index)
        {
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].content.IsVisible = i == index;
                buttons[i].IsChecked = i == index;
            }
        }
        for (int i = 0; i < pages.Length; i++)
        {
            int index = i;
            var b = new ToggleButton
            {
                Content = pages[i].title.ToUpperInvariant(), Focusable = false, Height = 28, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(0), FontFamily = Ui.Mono, FontSize = 10.5, FontWeight = FontWeight.SemiBold,
            };
            b.Click += (_, _) => Show(index);
            buttons.Add(b);
            header.Children.Add(b);
            host.Children.Add(pages[i].content);
        }
        Show(0);
        var bar = new Border { Child = header, Height = 30, BorderThickness = new Thickness(0, 0, 0, 2) }.Dyn(Border.BorderBrushProperty, "LineBrush").Dyn(Border.BackgroundProperty, "BgBrush");
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(host);
        return dock;
    }

    // -------------------------------------------------------------------- tabs

    void RefreshTabs()
    {
        tabs.Children.Clear();
        foreach (var doc in ed.Documents)
        {
            bool active = doc == ed.Doc;
            var label = new TextBlock
            {
                Text = doc.Title + (doc.Dirty ? " •" : ""), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, MaxWidth = 220,
                FontWeight = active ? FontWeight.Bold : FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var close = Ui.IconButton(Icons.Close, $"Close {doc.Title}", () => _ = CloseDocument(doc), 13);
            close.Width = close.Height = 20;
            var tab = new Border
            {
                Child = Ui.Row(8, label, close), Padding = new Thickness(12, 0, 6, 0), BorderThickness = new Thickness(0, 3, 2, 0), Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            tab.Dyn(Border.BorderBrushProperty, "LineBrush");
            if (active)
            {
                tab.Dyn(Border.BackgroundProperty, "SurfaceBrush");
                // Lime top edge marks the active document (drawn as a border brush split).
                tab.BorderBrush = new SolidColorBrush(Color.Parse("#D6FF00"));
            }
            else label.Classes.Add("dim");
            ToolTip.SetTip(tab, doc.FilePath ?? doc.Title);
            tab.PointerPressed += (_, e) =>
            {
                var p = e.GetCurrentPoint(tab).Properties;
                if (p.IsMiddleButtonPressed) _ = CloseDocument(doc);
                else if (p.IsLeftButtonPressed) ed.Activate(doc);
            };
            tabs.Children.Add(tab);
        }
        titleText.Text = ed.Doc is { } d ? $"{d.Title}{(d.Dirty ? " •" : "")}" : "";
        Title = ed.Doc is { } t ? $"{t.Title} — ArchaeaPhoto" : "ArchaeaPhoto";
    }

    void OnDocChanged()
    {
        welcome.IsVisible = ed.Doc is null;
        if (ed.Doc is null) BuildWelcome();
        UpdateStatus();
    }

    void BuildWelcome()
    {
        var column = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460 };
        column.Children.Add(new Image { Width = 104, Height = 104, HorizontalAlignment = HorizontalAlignment.Left, Source = new Bitmap(AssetLoader.Open(new Uri(MarkUri(this)))) });
        column.Children.Add(new TextBlock { Text = "ARCHAEAPHOTO", Classes = { "display" }, FontSize = 34 });
        column.Children.Add(new TextBlock { Text = "Create a new image or open one to start editing. You can also drop files onto this window.", Classes = { "dim" }, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        var buttons = Ui.Row(10, Ui.TextButton("New document", () => _ = NewDocument(), "primary"), Ui.TextButton("Open…", () => _ = OpenDialog()));
        foreach (var b in buttons.Children.OfType<Button>()) { b.MinHeight = 40; b.Padding = new Thickness(20, 0); b.FontSize = 12; }
        buttons.Margin = new Thickness(0, 6, 0, 0);
        column.Children.Add(buttons);
        var recent = App.Settings.RecentFiles.Where(File.Exists).Take(6).ToList();
        if (recent.Count > 0)
        {
            var heading = Ui.Label("Recent");
            heading.Margin = new Thickness(0, 14, 0, 0);
            column.Children.Add(heading);
            foreach (var path in recent)
            {
                var b = new Button { Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Content = Ui.MonoText(Shorten(path)) };
                ToolTip.SetTip(b, path);
                b.Click += (_, _) => _ = OpenPaths(new[] { path });
                column.Children.Add(b);
            }
        }
        welcome.Child = column;
        welcome.Dyn(Border.BackgroundProperty, "BgBrush");
    }

    static string Shorten(string path)
    {
        string name = Path.GetFileName(path), dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return dir.Length > 0 ? $"{dir}\\{name}" : name;
    }

    // -------------------------------------------------------------- status bar

    Control BuildStatusBar()
    {
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        zoomText.Width = 52;
        zoomText.TextAlignment = TextAlignment.Center;
        var fit = Ui.IconButton(Icons.Maximize, "Fit on screen (Ctrl+0)", () => canvas.Fit(), 12);
        var zoomOut = Ui.IconButton(Icons.Minus, "Zoom out (Ctrl+-)", () => canvas.ZoomStep(false), 13);
        var zoomIn = Ui.IconButton(Icons.Plus, "Zoom in (Ctrl++)", () => canvas.ZoomStep(true), 13);
        foreach (var b in new[] { fit, zoomOut, zoomIn }) b.Width = b.Height = 22;
        zoom.Children.AddRange(new Control[] { zoomOut, zoomText, zoomIn, fit });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(12, 0, 6, 0) };
        grid.Children.Add(statusText);
        statusText.Classes.Add("dim");
        positionText.Margin = new Thickness(16, 0);
        positionText.MinWidth = 110;
        sizeText.Margin = new Thickness(0, 0, 16, 0);
        Add(grid, positionText, 0, 1);
        Add(grid, sizeText, 0, 2);
        Add(grid, zoom, 0, 3);
        return new Border { Child = grid, BorderThickness = new Thickness(0, 2, 0, 0) }.Dyn(Border.BorderBrushProperty, "LineBrush").Dyn(Border.BackgroundProperty, "BgBrush");
    }

    void UpdateStatus()
    {
        if (ed.Doc is { } d)
        {
            sizeText.Text = $"{d.Width} × {d.Height} px";
            double z = canvas.Zoom * 100;
            zoomText.Text = z < 10 ? $"{z:0.#}%" : $"{z:0}%";
        }
        else
        {
            sizeText.Text = "";
            zoomText.Text = "—";
            positionText.Text = "";
        }
    }

    void UpdatePointer()
    {
        positionText.Text = ed.Doc is not null && canvas.PointerDoc is { } p ? $"X {Math.Floor(p.X):0}  Y {Math.Floor(p.Y):0}" : "";
    }

    void Flash(string message)
    {
        statusText.Text = message;
        flashTimer.Stop();
        flashTimer.Start();
    }

    // ---------------------------------------------------------------- keyboard

    static bool IsTyping(object? focused) => focused is TextBox;

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        if (IsTyping(focused))
        {
            // Text fields keep every key except the document-level save.
            if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control) { _ = Save(); e.Handled = true; }
            return;
        }
        if (menu.IsOpen || focused is MenuItem) return;
        var mods = e.KeyModifiers;
        if (e.Key == Key.Space && mods == KeyModifiers.None)
        {
            canvas.SetSpaceHeld(true);
            e.Handled = true;
            return;
        }
        if (ed.Doc is not null && ed.Tool.KeyDown(e.Key, mods)) { Handled(e); return; }

        foreach (var command in commands)
        {
            if (command.Gesture is null || !command.Gesture.Matches(e)) continue;
            if (command.Can?.Invoke() ?? true) command.Run();
            Handled(e);
            return;
        }
        if (mods == KeyModifiers.Control && e.Key is Key.Add or Key.Subtract) { canvas.ZoomStep(e.Key == Key.Add); Handled(e); return; }
        if (mods == KeyModifiers.Control && e.Key == Key.Y) { Redo(); Handled(e); return; }
        if (mods != KeyModifiers.None && mods != KeyModifiers.Shift) return;

        switch (e.Key)
        {
            case Key.OemOpenBrackets when ed.Tool is PaintTool p: p.Resize(1 / 1.15f); Handled(e); return;
            case Key.OemCloseBrackets when ed.Tool is PaintTool p: p.Resize(1.15f); Handled(e); return;
            case Key.X when mods == KeyModifiers.None: ed.SwapColors(); Handled(e); return;
            case Key.D when mods == KeyModifiers.None: ed.ResetColors(); Handled(e); return;
            case Key.Escape:
                ed.Tool.Cancel();
                canvas.InvalidateVisual();
                Handled(e);
                return;
        }
        if (mods != KeyModifiers.None) return;
        // Single-key tool shortcuts; tools sharing a key take turns.
        var group = ed.Tools.Where(t => !t.Transient && t.Shortcut == e.Key && t.Shortcut != Key.None).ToList();
        if (group.Count == 0) return;
        int current = group.IndexOf(ed.Tool);
        ed.SetTool(group[(current + 1) % group.Count]);
        Handled(e);
    }

    void Handled(KeyEventArgs e)
    {
        e.Handled = true;
        canvas.InvalidateVisual();
    }

    void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) canvas.SetSpaceHeld(false);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        canvas.SetSpaceHeld(false);
    }

    // ---------------------------------------------------------------- closing

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!closeConfirmed && ed.Documents.Any(d => d.Dirty))
        {
            e.Cancel = true;
            _ = ConfirmCloseAll();
        }
        else
        {
            var s = App.Settings;
            s.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
            s.RightPanelWidth = rightColumn.ActualWidth;
            s.Save();
        }
        base.OnClosing(e);
    }

    async Task ConfirmCloseAll()
    {
        foreach (var doc in ed.Documents.ToList())
        {
            if (!doc.Dirty) continue;
            ed.Activate(doc);
            if (!await ConfirmDiscard(doc)) return;
        }
        closeConfirmed = true;
        Close();
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (files is { Length: > 0 }) await OpenPaths(files);
    }

    public void OpenInitialFiles(string[] args)
    {
        var files = args.Where(File.Exists).ToArray();
        if (files.Length > 0) Opened += async (_, _) => await OpenPaths(files);
    }
}
