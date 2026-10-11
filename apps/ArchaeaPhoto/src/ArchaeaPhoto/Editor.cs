using ArchaeaPhoto.Core;
using Avalonia.Controls;
using SkiaSharp;

namespace ArchaeaPhoto;

/// <summary>Zoom and scroll position of one open document.</summary>
public sealed class DocView
{
    /// <summary>Screen pixels per image pixel.</summary>
    public double Zoom = 1;
    /// <summary>Where the image's top-left corner sits in the viewport, in screen pixels.</summary>
    public double PanX, PanY;
    public bool NeedsFit = true;
}

/// <summary>Application state shared by the window, the canvas, the tools and the panels.</summary>
public sealed class Editor
{
    public List<Document> Documents { get; } = new();
    public Document? Doc { get; private set; }
    readonly Dictionary<Document, DocView> views = new();

    public SKColor Foreground { get; private set; } = new(0x16, 0x16, 0x16);
    public SKColor Background { get; private set; } = SKColors.White;

    public List<Tool> Tools { get; } = new();
    public Tool Tool { get; private set; } = null!;
    Tool? previousTool;

    /// <summary>Copied pixels and where they came from, shared across documents.</summary>
    public SKBitmap? Clipboard { get; set; }
    public SKPointI ClipboardOrigin { get; set; }

    public CanvasView Canvas { get; set; } = null!;
    public Window Window { get; set; } = null!;
    /// <summary>A panel over the canvas where tools can place floating editors (positions in DIPs).</summary>
    public Avalonia.Controls.Canvas OverlayHost { get; set; } = null!;

    /// <summary>The active document was switched, or its layers/selection/history changed.</summary>
    public event Action? DocChanged;
    /// <summary>A document was opened, closed, renamed or its saved state changed.</summary>
    public event Action? DocListChanged;
    public event Action? ColorsChanged;
    public event Action? ToolChanged;
    public event Action<SKRectI>? Invalidated;
    public event Action<string>? Flashed;

    public DocView ViewOf(Document d) => views.TryGetValue(d, out var v) ? v : views[d] = new DocView();

    public void Add(Document doc)
    {
        Documents.Add(doc);
        doc.Changed += () =>
        {
            if (doc == Doc) DocChanged?.Invoke();
            DocListChanged?.Invoke(); // the tab shows the unsaved marker
        };
        doc.Invalidated += r =>
        {
            if (doc == Doc) Invalidated?.Invoke(r);
        };
        Activate(doc);
    }

    public void Activate(Document? doc)
    {
        if (doc == Doc) return;
        Tool?.Cancel();
        Doc = doc;
        DocListChanged?.Invoke();
        DocChanged?.Invoke();
    }

    public void Remove(Document doc)
    {
        int i = Documents.IndexOf(doc);
        if (i < 0) return;
        if (doc == Doc) Tool?.Cancel();
        Documents.RemoveAt(i);
        views.Remove(doc);
        if (doc == Doc)
        {
            Doc = null;
            Activate(Documents.Count == 0 ? null : Documents[Math.Min(i, Documents.Count - 1)]);
        }
        DocListChanged?.Invoke();
        if (Documents.Count == 0) DocChanged?.Invoke();
        GC.Collect(); // layer bitmaps are large and unmanaged; give the memory back promptly
    }

    public void NotifyDocList() => DocListChanged?.Invoke();

    public void SetColors(SKColor foreground, SKColor background)
    {
        if (foreground == Foreground && background == Background) return;
        Foreground = foreground;
        Background = background;
        ColorsChanged?.Invoke();
    }

    public void SetForeground(SKColor c) => SetColors(c.WithAlpha(255), Background);
    public void SetBackground(SKColor c) => SetColors(Foreground, c.WithAlpha(255));
    public void SwapColors() => SetColors(Background, Foreground);
    public void ResetColors() => SetColors(new SKColor(0x16, 0x16, 0x16), SKColors.White);

    public T Get<T>() where T : Tool => Tools.OfType<T>().First();

    public void SetTool(Tool tool)
    {
        if (tool == Tool) return;
        Tool?.Deactivate();
        if (Tool is not null && !Tool.Transient) previousTool = Tool;
        Tool = tool;
        tool.Activate();
        ToolChanged?.Invoke();
        Canvas?.InvalidateVisual();
    }

    /// <summary>Leaves a temporary mode (crop commit, free transform) for the tool used before it.</summary>
    public void RestoreTool() => SetTool(previousTool ?? Tools[0]);

    /// <summary>Shows a short message in the status bar.</summary>
    public void Flash(string message) => Flashed?.Invoke(message);
}
