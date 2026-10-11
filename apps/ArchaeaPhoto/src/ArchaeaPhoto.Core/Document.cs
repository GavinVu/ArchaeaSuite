using SkiaSharp;

namespace ArchaeaPhoto.Core;

/// <summary>What a painting tool writes into: a layer's pixels or its mask.</summary>
public readonly record struct PaintTarget(Layer Layer, bool IsMask)
{
    public SKBitmap Bitmap => IsMask ? Layer.Mask! : ((RasterLayer)Layer).Bitmap;
    public int X => IsMask ? Layer.MaskX : ((RasterLayer)Layer).X;
    public int Y => IsMask ? Layer.MaskY : ((RasterLayer)Layer).Y;
    public SKRectI Bounds => SKRectI.Create(X, Y, Bitmap.Width, Bitmap.Height);
    public PatchTarget Kind => IsMask ? PatchTarget.Mask : PatchTarget.Pixels;
}

/// <summary>An in-place paint in progress; <see cref="Before"/> is set when starting it changed the layer.</summary>
public sealed record PaintScope(PaintTarget Target, DocState? Before);

public sealed class Document
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public SKRectI Bounds => new(0, 0, Width, Height);

    /// <summary>Invisible container of the top-level layers.</summary>
    public GroupLayer Root { get; private set; } = new() { Name = "Root" };
    public Layer? Active { get; set; }
    /// <summary>Painting goes to the active layer's mask instead of its pixels.</summary>
    public bool EditMask { get; set; }

    public SKBitmap? Selection { get; private set; }
    public History History { get; }

    public string? FilePath { get; set; }
    public string Title { get; set; } = "Untitled";
    public bool Dirty { get; set; }

    /// <summary>Pixels in this document rectangle need recompositing.</summary>
    public event Action<SKRectI>? Invalidated;
    /// <summary>Layers, selection, history or size changed; panels should refresh.</summary>
    public event Action? Changed;

    int nameCounter;
    SKBitmap? composite;
    SKRectI dirty;
    SKPath? outline;

    public Document(int width, int height)
    {
        Width = Math.Clamp(width, 1, MaxSize);
        Height = Math.Clamp(height, 1, MaxSize);
        History = new History(this);
        dirty = Bounds;
    }

    public const int MaxSize = 16384;

    public static Document Blank(int width, int height, SKColor? background, string title = "Untitled")
    {
        var d = new Document(width, height) { Title = title };
        var bmp = Px.NewColor(d.Width, d.Height);
        if (background is { } bg) bmp.Erase(bg);
        var layer = new RasterLayer(bmp) { Name = background is null ? "Layer 1" : "Background" };
        d.nameCounter = 1;
        d.Root.Insert(0, layer);
        d.Active = layer;
        return d;
    }

    public static Document FromImage(SKBitmap image, string title)
    {
        var d = new Document(image.Width, image.Height) { Title = title };
        var layer = new RasterLayer(image) { Name = "Background" };
        d.Root.Insert(0, layer);
        d.Active = layer;
        return d;
    }

    // ------------------------------------------------------------- tree access

    /// <summary>Every layer, top-most first (the order the layers panel shows).</summary>
    public IEnumerable<Layer> AllLayers() => Root.Descendants();

    public Layer? Find(Guid id) => AllLayers().FirstOrDefault(l => l.Id == id);

    public string NextName(string prefix) => $"{prefix} {++nameCounter}";

    internal void SetNameCounter(int n) => nameCounter = n;

    public static SKRectI ContentBounds(Layer l)
    {
        if (l is RasterLayer r) return r.Bounds;
        var u = SKRectI.Empty;
        foreach (var c in ((GroupLayer)l).Children) u = Px.Union(u, ContentBounds(c));
        return u;
    }

    public PaintTarget? GetPaintTarget()
    {
        if (Active is null) return null;
        if (EditMask && Active.Mask is not null) return new PaintTarget(Active, true);
        return Active is RasterLayer ? new PaintTarget(Active, false) : null;
    }

    /// <summary>
    /// Starts an in-place paint on the active layer or mask. The target is grown
    /// to cover the whole canvas so tools can write anywhere inside the document,
    /// and a text layer becomes plain pixels. Finish with <see cref="EndPaint"/>
    /// or <see cref="AbortPaint"/>.
    /// </summary>
    public PaintScope? BeginPaint()
    {
        if (GetPaintTarget() is not { } t) return null;
        bool text = !t.IsMask && ((RasterLayer)t.Layer).Text is not null;
        if (t.Bounds.Contains(Bounds) && !text) return new PaintScope(t, null);
        // Growing replaces the bitmap. History must know, or a later redo would
        // paint into a different bitmap than the one later snapshots share.
        var before = Capture();
        if (t.IsMask) t.Layer.EnsureMaskCovers(Bounds);
        else
        {
            var r = (RasterLayer)t.Layer;
            r.Text = null;
            r.EnsureCovers(Bounds);
        }
        return new PaintScope(t, before);
    }

    /// <summary>Records a finished paint: the bytes of <paramref name="rect"/> (document coordinates) before and after.</summary>
    public void EndPaint(PaintScope scope, string name, SKRectI rect, byte[] before, byte[] after)
    {
        if (scope.Before is null) History.Push(new PatchItem(name, scope.Target.Layer.Id, scope.Target.Kind, rect, before, after));
        else History.Push(new SnapshotItem(name, scope.Before, Capture()));
        Dirty = true;
        Changed?.Invoke();
    }

    /// <summary>Abandons a paint whose pixels the caller has already put back.</summary>
    public void AbortPaint(PaintScope scope)
    {
        if (scope.Before is not null) Restore(scope.Before);
    }

    // ------------------------------------------------------------ compositing

    public void Invalidate(SKRectI rect)
    {
        rect = Px.Intersect(rect, Bounds);
        if (rect.IsEmpty) return;
        dirty = Px.Union(dirty, rect);
        Invalidated?.Invoke(rect);
    }

    public void InvalidateAll() => Invalidate(Bounds);

    /// <summary>The flattened image, brought up to date for any invalidated region.</summary>
    public SKBitmap GetComposite()
    {
        if (composite is null || composite.Width != Width || composite.Height != Height)
        {
            composite = Px.NewColor(Width, Height);
            dirty = Bounds;
        }
        if (!dirty.IsEmpty)
        {
            using var canvas = new SKCanvas(composite);
            canvas.ClipRect(dirty);
            canvas.Clear(SKColors.Transparent);
            Compositor.Draw(canvas, this);
            dirty = SKRectI.Empty;
        }
        return composite;
    }

    /// <summary>A fresh flattened copy (for export and thumbnails).</summary>
    public SKBitmap Flattened() => Px.Clone(GetComposite());

    // ---------------------------------------------------------------- history

    public DocState Capture() => new()
    {
        Width = Width,
        Height = Height,
        Layers = Root.Children.Select(LayerState.From).ToList(),
        Active = Active?.Id,
        EditMask = EditMask,
        Selection = Selection,
    };

    public void Restore(DocState s)
    {
        Width = s.Width;
        Height = s.Height;
        Root = new GroupLayer { Name = "Root" };
        foreach (var l in s.Layers) Root.Insert(Root.Children.Count, l.ToLayer());
        Active = s.Active is { } id ? Find(id) : null;
        EditMask = s.EditMask && Active?.Mask is not null;
        Selection = s.Selection;
        outline = null;
        dirty = Bounds;
        Invalidated?.Invoke(Bounds);
    }

    /// <summary>
    /// Runs a structural edit and records it. The action must replace any bitmap
    /// it changes rather than write into it (see History.cs). Edits sharing a
    /// <paramref name="mergeKey"/> collapse into one undo step (slider drags).
    /// </summary>
    public void Edit(string name, Action action, string? mergeKey = null)
    {
        var before = Capture();
        action();
        if (EditMask && Active?.Mask is null) EditMask = false;
        var after = Capture();
        if (mergeKey is not null && History.Top is SnapshotItem top && top.MergeKey == mergeKey)
            top.ReplaceAfter(after);
        else
            History.Push(new SnapshotItem(name, before, after, mergeKey));
        outline = null;
        Dirty = true;
        InvalidateAll();
        Changed?.Invoke();
    }

    /// <summary>Records an in-place pixel edit of one rectangle (document coordinates).</summary>
    public void PushPatch(string name, PaintTarget target, SKRectI rect, byte[] before, byte[] after)
    {
        History.Push(new PatchItem(name, target.Layer.Id, target.Kind, rect, before, after));
        Dirty = true;
        Changed?.Invoke();
    }

    internal void AfterHistoryMove()
    {
        outline = null;
        Dirty = true;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    // -------------------------------------------------------------- selection

    public bool HasSelection => Selection is not null;

    /// <summary>Selection boundary in document coordinates (cached).</summary>
    public SKPath? SelectionOutline => Selection is null ? null : outline ??= Sel.Outline(Selection);

    public void SetSelection(SKBitmap? selection, string name = "Select")
    {
        if (selection is not null && Sel.IsEmpty(selection)) selection = null;
        if (selection is null && Selection is null) return;
        Edit(name, () => Selection = selection);
    }

    public void SelectAll() => SetSelection(Px.NewMask(Width, Height, 255), "Select All");
    public void Deselect() => SetSelection(null, "Deselect");
    public void InvertSelection() => SetSelection(Sel.Invert(Selection, Width, Height), "Invert Selection");

    /// <summary>Replaces the selection inside an enclosing Edit, without its own history entry.</summary>
    public void ReplaceSelectionInEdit(SKBitmap? selection)
    {
        Selection = selection is not null && Sel.IsEmpty(selection) ? null : selection;
        outline = null;
    }

    // ------------------------------------------------------------- layer edits

    void InsertAboveActive(Layer layer)
    {
        if (Active is GroupLayer g) g.Insert(g.Children.Count, layer);
        else if (Active?.Parent is { } parent) parent.Insert(parent.Children.IndexOf(Active) + 1, layer);
        else Root.Insert(Root.Children.Count, layer);
        Active = layer;
        EditMask = false;
    }

    public RasterLayer AddLayer(string? name = null, SKBitmap? bitmap = null, int x = 0, int y = 0, string historyName = "New Layer")
    {
        var layer = new RasterLayer(bitmap ?? Px.NewColor(Width, Height), x, y) { Name = name ?? NextName("Layer") };
        Edit(historyName, () => InsertAboveActive(layer));
        return layer;
    }

    /// <summary>Inserts a ready-made layer above the active one.</summary>
    public T Add<T>(T layer, string historyName) where T : Layer
    {
        Edit(historyName, () => InsertAboveActive(layer));
        return layer;
    }

    public GroupLayer AddGroup()
    {
        var group = new GroupLayer { Name = NextName("Group") };
        Edit("New Group", () =>
        {
            // A new group goes beside the active layer, not inside an active group.
            var anchor = Active;
            var parent = anchor?.Parent ?? Root;
            parent.Insert(anchor is null ? parent.Children.Count : parent.Children.IndexOf(anchor) + 1, group);
            Active = group;
            EditMask = false;
        });
        return group;
    }

    public bool CanDeleteActive => Active is not null && !(Active.Parent == Root && Root.Children.Count == 1);

    public void DeleteActive()
    {
        if (!CanDeleteActive) return;
        var layer = Active!;
        Edit("Delete Layer", () =>
        {
            var parent = layer.Parent!;
            int i = parent.Children.IndexOf(layer);
            parent.Children.RemoveAt(i);
            Active = parent.Children.Count > 0 ? parent.Children[Math.Max(0, i - 1)] : parent == Root ? null : parent;
            EditMask = false;
        });
    }

    public void DuplicateActive()
    {
        if (Active is null) return;
        var src = Active;
        Edit("Duplicate Layer", () =>
        {
            var copy = src.DeepClone();
            copy.Name = src.Name + " copy";
            var parent = src.Parent!;
            parent.Insert(parent.Children.IndexOf(src) + 1, copy);
            Active = copy;
            EditMask = false;
        });
    }

    /// <summary>Moves the active layer one step up (+1) or down (-1) the stack, in and out of groups.</summary>
    public void MoveActive(int delta)
    {
        var l = Active;
        if (l?.Parent is not { } parent) return;
        int i = parent.Children.IndexOf(l);
        GroupLayer target;
        int index;
        if (delta > 0)
        {
            if (i < parent.Children.Count - 1)
            {
                if (parent.Children[i + 1] is GroupLayer { Expanded: true } g) (target, index) = (g, 0);
                else (target, index) = (parent, i + 1);
            }
            else if (parent != Root) (target, index) = (parent.Parent!, parent.Parent!.Children.IndexOf(parent) + 1);
            else return;
        }
        else
        {
            if (i > 0)
            {
                if (parent.Children[i - 1] is GroupLayer { Expanded: true } g) (target, index) = (g, g.Children.Count);
                else (target, index) = (parent, i - 1);
            }
            else if (parent != Root) (target, index) = (parent.Parent!, parent.Parent!.Children.IndexOf(parent));
            else return;
        }
        Edit("Reorder Layer", () =>
        {
            parent.Children.Remove(l);
            target.Insert(index, l);
        });
    }

    /// <summary>Moves a layer to <paramref name="index"/> (counted before removal) in <paramref name="newParent"/>.</summary>
    public void MoveLayer(Layer layer, GroupLayer newParent, int index)
    {
        for (GroupLayer? p = newParent; p is not null; p = p.Parent)
            if (p == layer) return; // a group cannot contain itself
        var old = layer.Parent!;
        int from = old.Children.IndexOf(layer);
        if (old == newParent && (index == from || index == from + 1)) return;
        Edit("Reorder Layer", () =>
        {
            old.Children.RemoveAt(from);
            if (old == newParent && index > from) index--;
            newParent.Insert(index, layer);
        });
    }

    public void Rename(Layer l, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == l.Name) return;
        Edit("Rename Layer", () => l.Name = name);
    }

    public void SetVisible(Layer l, bool visible)
    {
        if (l.Visible != visible) Edit(visible ? "Show Layer" : "Hide Layer", () => l.Visible = visible);
    }

    public void SetOpacity(Layer l, float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        if (Math.Abs(l.Opacity - opacity) > 0.0005f) Edit("Layer Opacity", () => l.Opacity = opacity, "opacity:" + l.Id);
    }

    public void SetBlend(Layer l, BlendMode mode)
    {
        if (l.Blend != mode) Edit("Blend Mode", () => l.Blend = mode);
    }

    public void SetExpanded(GroupLayer g, bool expanded)
    {
        // View state only; not worth an undo step.
        g.Expanded = expanded;
        Changed?.Invoke();
    }

    public void AddMask()
    {
        if (Active is not { Mask: null } l) return;
        Edit("Add Layer Mask", () =>
        {
            l.Mask = Selection is not null ? Px.Clone(Selection) : Px.NewMask(Width, Height, 255);
            l.MaskX = l.MaskY = 0;
            l.MaskEnabled = true;
            EditMask = true;
        });
    }

    public void DeleteMask()
    {
        if (Active is not { Mask: not null } l) return;
        Edit("Delete Layer Mask", () => { l.Mask = null; EditMask = false; });
    }

    public void SetMaskEnabled(bool enabled)
    {
        if (Active is not { Mask: not null } l || l.MaskEnabled == enabled) return;
        Edit(enabled ? "Enable Layer Mask" : "Disable Layer Mask", () => l.MaskEnabled = enabled);
    }

    public void InvertMask()
    {
        if (Active is not { Mask: not null } l) return;
        Edit("Invert Layer Mask", () =>
        {
            l.EnsureMaskCovers(Bounds);
            l.Mask = Sel.Invert(l.Mask, l.Mask!.Width, l.Mask.Height);
        });
    }

    public void ApplyMask()
    {
        if (Active is not RasterLayer { Mask: not null } r) return;
        Edit("Apply Layer Mask", () =>
        {
            var baked = Px.NewColor(r.Bitmap.Width, r.Bitmap.Height);
            using (var c = new SKCanvas(baked))
            {
                c.Translate(-r.X, -r.Y);
                Compositor.DrawIsolated(c, r);
            }
            r.Bitmap = baked;
            r.Mask = null;
            EditMask = false;
        });
    }

    public bool CanMergeDown =>
        Active?.Parent is { } p && p.Children.IndexOf(Active) is > 0 and var i && p.Children[i - 1] is RasterLayer;

    public void MergeDown()
    {
        if (!CanMergeDown) return;
        var upper = Active!;
        var parent = upper.Parent!;
        var lower = (RasterLayer)parent.Children[parent.Children.IndexOf(upper) - 1];
        Edit("Merge Down", () =>
        {
            var u = Px.Union(lower.Bounds, ContentBounds(upper));
            var bmp = Px.NewColor(u.Width, u.Height);
            using (var c = new SKCanvas(bmp))
            {
                c.Translate(-u.Left, -u.Top);
                Compositor.DrawIsolated(c, lower);
                if (lower.Visible) Compositor.DrawLayer(c, upper);
                else Compositor.DrawIsolated(c, upper);
            }
            var merged = new RasterLayer(bmp, u.Left, u.Top)
            {
                Name = lower.Name, Opacity = lower.Opacity, Blend = lower.Blend, Visible = lower.Visible || upper.Visible,
            };
            int i = parent.Children.IndexOf(lower);
            parent.Children.Remove(upper);
            parent.Children.Remove(lower);
            parent.Insert(i, merged);
            Active = merged;
            EditMask = false;
        });
    }

    public void Flatten()
    {
        Edit("Flatten Image", () =>
        {
            var bmp = Px.NewColor(Width, Height);
            using (var c = new SKCanvas(bmp)) Compositor.Draw(c, this);
            var flat = new RasterLayer(bmp) { Name = "Background" };
            Root.Children.Clear();
            Root.Insert(0, flat);
            Active = flat;
            EditMask = false;
        });
    }

    /// <summary>Changes the canvas size inside an enclosing Edit.</summary>
    internal void SetSizeInEdit(int width, int height)
    {
        Width = Math.Clamp(width, 1, MaxSize);
        Height = Math.Clamp(height, 1, MaxSize);
    }
}
