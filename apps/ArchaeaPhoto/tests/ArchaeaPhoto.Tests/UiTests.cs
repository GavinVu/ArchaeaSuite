using System.Runtime.CompilerServices;
using ArchaeaPhoto.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ArchaeaPhoto.Tests.TestApp))]

namespace ArchaeaPhoto.Tests;

public static class TestApp
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        // Never read or write the real user's settings from a test run.
        Environment.SetEnvironmentVariable("ARCHAEAPHOTO_DATA_DIR", Path.Combine(Path.GetTempPath(), "archaeaphoto-tests-" + Environment.ProcessId));
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Drives the real window through the headless platform: real layout, real rendering, synthetic input.</summary>
public class UiTests
{
    static MainWindow Open(int width = 1360, int height = 860)
    {
        var w = new MainWindow { Width = width, Height = height, WindowState = WindowState.Normal };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    static Document Sample(MainWindow w)
    {
        var d = Document.Blank(640, 420, SKColors.White, "sample");
        w.Editor.Add(d);
        Dispatcher.UIThread.RunJobs();
        return d;
    }

    static Point OnCanvas(MainWindow w, float docX, float docY)
    {
        var canvas = w.Editor.Canvas;
        var v = canvas.ToView(new SKPoint(docX, docY));
        double s = w.RenderScaling;
        return canvas.TranslatePoint(new Point(v.X / s, v.Y / s), w)!.Value;
    }

    static void Drag(MainWindow w, RawInputModifiers mods, params (float x, float y)[] docPoints)
    {
        var first = OnCanvas(w, docPoints[0].x, docPoints[0].y);
        w.MouseMove(first, mods);
        w.MouseDown(first, MouseButton.Left, mods);
        foreach (var (x, y) in docPoints.Skip(1)) w.MouseMove(OnCanvas(w, x, y), mods);
        var last = docPoints[^1];
        w.MouseUp(OnCanvas(w, last.x, last.y), MouseButton.Left, mods);
        Dispatcher.UIThread.RunJobs();
    }

    static void Key(MainWindow w, PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPressQwerty(key, mods);
        w.KeyReleaseQwerty(key, mods);
        Dispatcher.UIThread.RunJobs();
    }

    static void Shot(MainWindow w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ARCHAEAPHOTO_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        using var frame = w.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name + ".png"));
    }

    static SKColor At(Document d, int x, int y) => Px.GetColor(d.GetComposite(), x, y);

    [AvaloniaFact]
    public void Workspace_opens_and_renders()
    {
        var w = Open();
        Assert.Null(w.Editor.Doc);
        Shot(w, "01-welcome");
        var d = Sample(w);
        Assert.Same(d, w.Editor.Doc);
        using var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(1360, frame!.PixelSize.Width);
        w.Close();
    }

    [AvaloniaFact]
    public void Brush_paints_with_the_pointer_and_undo_redo_use_the_keyboard()
    {
        var w = Open();
        var d = Sample(w);
        var ed = w.Editor;
        Key(w, PhysicalKey.B);
        Assert.IsType<BrushTool>(ed.Tool);
        ed.SetForeground(new SKColor(0xD6, 0xFF, 0x00));
        Drag(w, RawInputModifiers.None, (100, 100), (200, 140), (320, 100));
        Assert.Equal(1, d.History.Position);
        Assert.Equal(new SKColor(0xD6, 0xFF, 0x00), At(d, 100, 100));
        Assert.True(d.Dirty);

        Key(w, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal(SKColors.White, At(d, 100, 100));
        Key(w, PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(new SKColor(0xD6, 0xFF, 0x00), At(d, 100, 100));

        // Bracket keys change the brush size; X swaps colours.
        float size = ((PaintTool)ed.Tool).Settings.Size;
        Key(w, PhysicalKey.BracketRight);
        Assert.True(((PaintTool)ed.Tool).Settings.Size > size);
        var fg = ed.Foreground;
        Key(w, PhysicalKey.X);
        Assert.Equal(fg, ed.Background);
        w.Close();
    }

    [AvaloniaFact]
    public void Selection_tools_fill_and_move_work_end_to_end()
    {
        var w = Open();
        var d = Sample(w);
        var ed = w.Editor;
        Key(w, PhysicalKey.M);
        Assert.IsType<MarqueeTool>(ed.Tool);
        Drag(w, RawInputModifiers.None, (50, 50), (120, 90), (250, 200));
        Assert.True(d.HasSelection);
        Assert.Equal(new SKRectI(50, 50, 250, 200), Sel.Bounds(d.Selection!));

        ed.SetForeground(new SKColor(255, 0, 0));
        Key(w, PhysicalKey.Backspace, RawInputModifiers.Alt); // fill with foreground
        Assert.Equal(new SKColor(255, 0, 0), At(d, 100, 100));
        Assert.Equal(SKColors.White, At(d, 300, 100));

        // Move the selected pixels 100 px right.
        Key(w, PhysicalKey.V);
        Assert.IsType<MoveTool>(ed.Tool);
        Drag(w, RawInputModifiers.None, (100, 100), (150, 100), (200, 100));
        Assert.Equal(new SKColor(255, 0, 0), At(d, 300, 100));
        Assert.Equal(0, At(d, 60, 100).Alpha);
        Assert.Equal(new SKRectI(150, 50, 350, 200), Sel.Bounds(d.Selection!));

        Key(w, PhysicalKey.D, RawInputModifiers.Control);
        Assert.False(d.HasSelection);
        // Undo everything: back to a blank white canvas.
        d.History.JumpTo(0);
        Assert.Equal(SKColors.White, At(d, 300, 100));
        Assert.Equal(SKColors.White, At(d, 60, 100));
        w.Close();
    }

    [AvaloniaFact]
    public void Layers_shapes_crop_and_transform_work_from_the_ui()
    {
        var w = Open();
        var d = Sample(w);
        var ed = w.Editor;
        ed.SetForeground(new SKColor(0, 0, 255));
        Key(w, PhysicalKey.U);
        Assert.IsType<ShapeTool>(ed.Tool);
        Drag(w, RawInputModifiers.None, (200, 100), (260, 160), (300, 200));
        Assert.Equal(2, d.AllLayers().Count());
        Assert.Equal(new SKColor(0, 0, 255), At(d, 250, 150));

        // Free transform: drag inside the box to move the shape layer.
        Key(w, PhysicalKey.T, RawInputModifiers.Control);
        Assert.IsType<TransformTool>(ed.Tool);
        Drag(w, RawInputModifiers.None, (250, 150), (300, 150), (350, 150));
        Key(w, PhysicalKey.Enter);
        Assert.IsType<ShapeTool>(ed.Tool); // back to the previous tool
        Assert.Equal(new SKColor(0, 0, 255), At(d, 350, 150));
        Assert.Equal(SKColors.White, At(d, 210, 150));

        // Crop.
        Key(w, PhysicalKey.C);
        Assert.IsType<CropTool>(ed.Tool);
        Drag(w, RawInputModifiers.None, (100, 50), (300, 200), (500, 350));
        Key(w, PhysicalKey.Enter);
        Assert.Equal((400, 300), (d.Width, d.Height));
        Assert.Equal(new SKColor(0, 0, 255), At(d, 250, 100));

        Key(w, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal((640, 420), (d.Width, d.Height));
        w.Close();
    }

    [AvaloniaFact]
    public void Screenshots_of_a_working_session_in_both_themes()
    {
        var w = Open();
        var d = Sample(w);
        var ed = w.Editor;
        Draw.Gradient(d, new SKPoint(0, 0), new SKPoint(640, 420), new SKColor(0x24, 0x24, 0x24), new SKColor(0x5B, 0x6D, 0x10), false);
        d.AddLayer();
        ed.SetForeground(new SKColor(0xD6, 0xFF, 0x00));
        ed.SetTool(ed.Get<BrushTool>());
        Drag(w, RawInputModifiers.None, (80, 300), (200, 180), (320, 260), (460, 120), (560, 200));
        Draw.Shape(d, ShapeKind.Ellipse, new SKPoint(380, 240), new SKPoint(560, 380), new SKColor(0xF5, 0xF5, 0xF0), new SKColor(0x16, 0x16, 0x16), 8);
        Draw.AddText(d, new TextInfo("ARCHAEA", "Arial", 64, true, false, 0xFFF5F5F0), 40, 30);
        var g = d.AddGroup();
        d.Active = d.AllLayers().OfType<RasterLayer>().First(l => l.Text is null && l.Name.StartsWith("Ellipse"));
        d.AddMask();
        ed.SetTool(ed.Get<MarqueeTool>());
        Drag(w, RawInputModifiers.None, (300, 60), (400, 120), (600, 200));
        w.Editor.Canvas.Fit();
        Shot(w, "02-workspace-dark");

        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        Shot(w, "03-workspace-light");
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, d.AllLayers().Count());
        _ = g;
        w.Close();
    }

    /// <summary>Runs the dispatcher until a condition holds (background previews, file loading).</summary>
    static void Pump(Func<bool> until, int timeoutMs = 8000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && clock.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(until(), "timed out waiting for the UI");
    }

    static T Dialog<T>(MainWindow w) where T : Window
    {
        Pump(() => w.OwnedWindows.OfType<T>().Any());
        return w.OwnedWindows.OfType<T>().First();
    }

    static void Click(MainWindow w, string header)
    {
        IEnumerable<MenuItem> All(IEnumerable<object?> items) =>
            items.OfType<MenuItem>().SelectMany(m => All(m.Items).Prepend(m));
        var menu = w.GetVisualDescendants().OfType<Menu>().First();
        var item = All(menu.Items).First(m => (m.Header as string)?.Replace("_", "") == header);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static void DialogKey(Window dialog, PhysicalKey key)
    {
        // The headless platform does not activate a new window, so keyboard focus is
        // still in the main window; put it where Windows would.
        if (dialog.FocusManager?.GetFocusedElement() is not Control { IsEffectivelyVisible: true } focused || TopLevel.GetTopLevel(focused) != dialog)
            dialog.GetVisualDescendants().OfType<Button>().Last(b => b.Classes.Contains("primary")).Focus();
        dialog.KeyPressQwerty(key, RawInputModifiers.None);
        if (dialog.IsVisible) dialog.KeyReleaseQwerty(key, RawInputModifiers.None); // the key usually closes it
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Adjustment_and_filter_dialogs_preview_apply_and_cancel()
    {
        var w = Open();
        var d = Sample(w);
        Draw.Fill(d, new SKColor(100, 120, 140));
        int steps = d.History.Position;

        // Brightness: move the slider, the canvas previews it, Enter applies it.
        Click(w, "Brightness/Contrast…");
        var dialog = Dialog<AdjustDialog>(w);
        var slider = dialog.GetVisualDescendants().OfType<Slider>().First();
        slider.Value = 60;
        Pump(() => At(d, 10, 10).Red > 110);
        Shot(w, "04-preview-behind-dialog");
        using (var frame = dialog.CaptureRenderedFrame())
            if (Environment.GetEnvironmentVariable("ARCHAEAPHOTO_SHOTS") is { Length: > 0 } dir) frame?.Save(Path.Combine(dir, "05-dialog-brightness.png"));
        DialogKey(dialog, PhysicalKey.Enter);
        Pump(() => !w.OwnedWindows.Any());
        Assert.Equal(steps + 1, d.History.Position);
        Assert.True(At(d, 10, 10).Red > 110);

        // Curves: cancelling leaves the image and the history alone.
        var before = At(d, 10, 10);
        Key(w, PhysicalKey.M, RawInputModifiers.Control);
        var curves = Dialog<AdjustDialog>(w);
        var editor = curves.GetVisualDescendants().OfType<CurveEditor>().First();
        editor.Curves.Rgb.Insert(1, new SKPoint(0.5f, 0.2f));
        curves.RequestPreview();
        Pump(() => At(d, 10, 10) != before);
        using (var frame = curves.CaptureRenderedFrame())
            if (Environment.GetEnvironmentVariable("ARCHAEAPHOTO_SHOTS") is { Length: > 0 } dir) frame?.Save(Path.Combine(dir, "06-dialog-curves.png"));
        DialogKey(curves, PhysicalKey.Escape);
        Pump(() => !w.OwnedWindows.Any());
        Assert.Equal(before, At(d, 10, 10));
        Assert.Equal(steps + 1, d.History.Position);

        // A filter with its default settings.
        Draw.Paint(d, "dot", c => c.DrawRect(new SKRect(300, 200, 302, 202), new SKPaint { Color = SKColors.Black }));
        var sharp = At(d, 300, 200);
        Click(w, "Gaussian Blur…");
        var blur = Dialog<AdjustDialog>(w);
        DialogKey(blur, PhysicalKey.Enter);
        Pump(() => !w.OwnedWindows.Any());
        Assert.NotEqual(sharp, At(d, 300, 200));
        Assert.NotEqual(SKColors.Black, At(d, 300, 200));

        Click(w, "Invert");
        Assert.True(At(d, 10, 10).Red < 110);
        w.Close();
    }

    [AvaloniaFact]
    public void Documents_open_from_disk_in_tabs_and_new_document_dialog_creates_one()
    {
        var w = Open();
        var dir = Path.Combine(Path.GetTempPath(), "archaeaphoto-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Document.Blank(120, 80, new SKColor(10, 200, 30));
            source.AddLayer();
            string png = Path.Combine(dir, "flat.png"), project = Path.Combine(dir, "layers.archa"), bad = Path.Combine(dir, "bad.png");
            ImageIO.Save(source.GetComposite(), png, ImageFormat.Png);
            Archa.Save(source, project);
            _ = w.OpenPaths(new[] { png, project });
            Pump(() => w.Editor.Documents.Count == 2);
            Assert.Equal("layers.archa", w.Editor.Doc!.Title);
            Assert.Equal(2, w.Editor.Doc.AllLayers().Count());
            Assert.Equal(project, w.Editor.Doc.FilePath);
            Assert.Null(w.Editor.Documents[0].FilePath); // a flat image is not a project until saved as one
            Assert.False(w.Editor.Doc.Dirty);

            // Opening the same file again only switches to its tab.
            _ = w.OpenPaths(new[] { png });
            Pump(() => w.Editor.Doc == w.Editor.Documents[0]);
            Assert.Equal(2, w.Editor.Documents.Count);

            // A file that is not an image produces a message, not a crash.
            File.WriteAllText(bad, "nope");
            _ = w.OpenPaths(new[] { bad });
            var message = Dialog<MessageDialog>(w);
            DialogKey(message, PhysicalKey.Enter);
            Pump(() => !w.OwnedWindows.Any());
            Assert.Equal(2, w.Editor.Documents.Count);

            Key(w, PhysicalKey.N, RawInputModifiers.Control);
            var dialog = Dialog<NewDocumentDialog>(w);
            using (var frame = dialog.CaptureRenderedFrame())
                if (Environment.GetEnvironmentVariable("ARCHAEAPHOTO_SHOTS") is { Length: > 0 } shots) frame?.Save(Path.Combine(shots, "07-dialog-new.png"));
            DialogKey(dialog, PhysicalKey.Enter);
            Pump(() => w.Editor.Documents.Count == 3);
            Assert.Equal((1920, 1080), (w.Editor.Doc!.Width, w.Editor.Doc.Height));
        }
        finally
        {
            w.Close();
            Directory.Delete(dir, true);
        }
    }
}
