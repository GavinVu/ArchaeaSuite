using SkiaSharp;

namespace ArchaeaPhoto.Core;

// Undo model
// ----------
// Two kinds of entry cover every edit:
//   * SnapshotItem — the layer tree before and after. Snapshots share bitmap
//     objects with the live document, so an edit recorded this way must REPLACE
//     the bitmaps it changes, never write into them.
//   * PatchItem — the bytes of one rectangle before and after, for edits that
//     write into a bitmap in place (brush strokes, fills, adjustments).
// History is strictly linear, so a bitmap shared by a snapshot always holds the
// content it had at that point by the time the snapshot is restored.

public abstract class HistoryItem
{
    public string Name { get; protected init; } = "";
    public long Bytes { get; protected set; }
    public abstract void Undo(Document d);
    public abstract void Redo(Document d);
}

public sealed class LayerState
{
    public Guid Id;
    public string Name = "";
    public bool Visible;
    public float Opacity;
    public BlendMode Blend;
    public SKBitmap? Mask;
    public int MaskX, MaskY;
    public bool MaskEnabled;
    public bool Expanded;
    public SKBitmap? Bitmap;
    public int X, Y;
    public TextInfo? Text;
    public List<LayerState>? Children;

    public static LayerState From(Layer l)
    {
        var s = new LayerState
        {
            Id = l.Id, Name = l.Name, Visible = l.Visible, Opacity = l.Opacity, Blend = l.Blend,
            Mask = l.Mask, MaskX = l.MaskX, MaskY = l.MaskY, MaskEnabled = l.MaskEnabled,
        };
        if (l is RasterLayer r)
        {
            s.Bitmap = r.Bitmap;
            s.X = r.X;
            s.Y = r.Y;
            s.Text = r.Text;
        }
        else if (l is GroupLayer g)
        {
            s.Expanded = g.Expanded;
            s.Children = g.Children.Select(From).ToList();
        }
        return s;
    }

    public Layer ToLayer()
    {
        Layer l;
        if (Children is not null)
        {
            var g = new GroupLayer { Id = Id, Expanded = Expanded };
            foreach (var c in Children) g.Insert(g.Children.Count, c.ToLayer());
            l = g;
        }
        else
        {
            l = new RasterLayer(Bitmap!, X, Y) { Id = Id, Text = Text };
        }
        l.Name = Name;
        l.Visible = Visible;
        l.Opacity = Opacity;
        l.Blend = Blend;
        l.Mask = Mask;
        l.MaskX = MaskX;
        l.MaskY = MaskY;
        l.MaskEnabled = MaskEnabled;
        return l;
    }

    /// <summary>Puts this state back onto an existing layer with the same shape (used for live previews).</summary>
    public void ApplyTo(Layer l)
    {
        l.Mask = Mask;
        l.MaskX = MaskX;
        l.MaskY = MaskY;
        if (l is RasterLayer r)
        {
            r.Bitmap = Bitmap!;
            r.X = X;
            r.Y = Y;
            r.Text = Text;
        }
        else if (l is GroupLayer g && Children is not null)
        {
            for (int i = 0; i < Math.Min(g.Children.Count, Children.Count); i++) Children[i].ApplyTo(g.Children[i]);
        }
    }

    internal void CollectBitmaps(HashSet<SKBitmap> set)
    {
        if (Bitmap is not null) set.Add(Bitmap);
        if (Mask is not null) set.Add(Mask);
        if (Children is not null)
            foreach (var c in Children) c.CollectBitmaps(set);
    }
}

public sealed class DocState
{
    public int Width, Height;
    public List<LayerState> Layers = new();
    public Guid? Active;
    public bool EditMask;
    public SKBitmap? Selection;

    internal HashSet<SKBitmap> Bitmaps()
    {
        var set = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        foreach (var l in Layers) l.CollectBitmaps(set);
        if (Selection is not null) set.Add(Selection);
        return set;
    }
}

public sealed class SnapshotItem : HistoryItem
{
    public DocState Before { get; }
    public DocState After { get; private set; }
    public string? MergeKey { get; }

    public SnapshotItem(string name, DocState before, DocState after, string? mergeKey = null)
    {
        Name = name;
        Before = before;
        After = after;
        MergeKey = mergeKey;
        Measure();
    }

    public void ReplaceAfter(DocState after)
    {
        After = after;
        Measure();
    }

    void Measure()
    {
        var old = Before.Bitmaps();
        Bytes = After.Bitmaps().Where(b => !old.Contains(b)).Sum(b => (long)b.ByteCount);
    }

    public override void Undo(Document d) => d.Restore(Before);
    public override void Redo(Document d) => d.Restore(After);
}

public enum PatchTarget { Pixels, Mask, Selection }

public sealed class PatchItem : HistoryItem
{
    readonly Guid layerId;
    readonly PatchTarget target;
    readonly SKRectI rect; // document coordinates
    readonly byte[] before, after;

    public PatchItem(string name, Guid layerId, PatchTarget target, SKRectI rect, byte[] before, byte[] after)
    {
        Name = name;
        this.layerId = layerId;
        this.target = target;
        this.rect = rect;
        this.before = before;
        this.after = after;
        Bytes = before.LongLength + after.LongLength;
    }

    void Apply(Document d, byte[] data)
    {
        var layer = d.Find(layerId);
        if (layer is null) return;
        if (target == PatchTarget.Pixels && layer is RasterLayer r)
        {
            r.EnsureCovers(rect);
            Px.SetRect(r.Bitmap, SKRectI.Create(rect.Left - r.X, rect.Top - r.Y, rect.Width, rect.Height), data);
        }
        else if (target == PatchTarget.Mask && layer.Mask is not null)
        {
            layer.EnsureMaskCovers(rect);
            Px.SetRect(layer.Mask, SKRectI.Create(rect.Left - layer.MaskX, rect.Top - layer.MaskY, rect.Width, rect.Height), data);
        }
        d.Invalidate(rect);
    }

    public override void Undo(Document d) => Apply(d, before);
    public override void Redo(Document d) => Apply(d, after);
}

public sealed class History
{
    const int MaxItems = 80;
    const long MaxBytes = 1536L * 1024 * 1024;

    readonly Document doc;
    readonly List<HistoryItem> items = new();

    /// <summary>How many items are currently applied.</summary>
    public int Position { get; private set; }
    public IReadOnlyList<HistoryItem> Items => items;
    public bool CanUndo => Position > 0;
    public bool CanRedo => Position < items.Count;
    public HistoryItem? Top => Position == items.Count && items.Count > 0 ? items[^1] : null;

    /// <summary>Items dropped from the start to stay inside the memory budget.</summary>
    public int Trimmed { get; private set; }

    public History(Document doc) => this.doc = doc;

    public void Push(HistoryItem item)
    {
        if (Position < items.Count) items.RemoveRange(Position, items.Count - Position);
        items.Add(item);
        Position = items.Count;
        long total = items.Sum(i => i.Bytes);
        while (items.Count > 1 && (items.Count > MaxItems || total > MaxBytes))
        {
            total -= items[0].Bytes;
            items.RemoveAt(0);
            Position--;
            Trimmed++;
        }
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        items[--Position].Undo(doc);
        doc.AfterHistoryMove();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        items[Position++].Redo(doc);
        doc.AfterHistoryMove();
        return true;
    }

    public void JumpTo(int position)
    {
        position = Math.Clamp(position, 0, items.Count);
        if (position == Position) return;
        while (Position > position) items[--Position].Undo(doc);
        while (Position < position) items[Position++].Redo(doc);
        doc.AfterHistoryMove();
    }
}
