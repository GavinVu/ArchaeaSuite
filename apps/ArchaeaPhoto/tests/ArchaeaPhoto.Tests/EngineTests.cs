using ArchaeaPhoto.Core;
using SkiaSharp;
using Xunit;

namespace ArchaeaPhoto.Tests;

public class EngineTests
{
    static readonly SKColor Red = new(255, 0, 0), Green = new(0, 255, 0), Blue = new(0, 0, 255), White = SKColors.White, Black = SKColors.Black;

    static SKColor At(Document d, int x, int y) => Px.GetColor(d.GetComposite(), x, y);

    static RasterLayer Solid(Document d, SKColor c, string name = "L")
    {
        var bmp = Px.NewColor(d.Width, d.Height);
        bmp.Erase(c);
        return d.AddLayer(name, bmp);
    }

    static void Near(SKColor expected, SKColor actual, int tolerance = 2)
    {
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance &&
                    Math.Abs(expected.Blue - actual.Blue) <= tolerance && Math.Abs(expected.Alpha - actual.Alpha) <= tolerance,
            $"expected {expected} but was {actual}");
    }

    static byte[] Bytes(Document d) => Px.GetRect(d.GetComposite(), d.Bounds);

    // ------------------------------------------------------------ compositing

    [Fact]
    public void Layers_composite_with_opacity_blend_and_visibility()
    {
        var d = Document.Blank(8, 8, White);
        var top = Solid(d, Red);
        Near(Red, At(d, 4, 4));

        d.SetOpacity(top, 0.5f);
        Near(new SKColor(255, 128, 128), At(d, 4, 4));

        d.SetOpacity(top, 1f);
        d.SetBlend(top, BlendMode.Multiply);
        ((RasterLayer)d.Root.Children[0]).Bitmap.Erase(new SKColor(128, 128, 128));
        d.InvalidateAll();
        Near(new SKColor(128, 0, 0), At(d, 4, 4));

        d.SetVisible(top, false);
        Near(new SKColor(128, 128, 128), At(d, 4, 4));
    }

    [Fact]
    public void Mask_hides_pixels_and_can_be_disabled_inverted_and_applied()
    {
        var d = Document.Blank(10, 10, White);
        var top = Solid(d, Red);
        d.SetSelection(Sel.FromRect(10, 10, new SKRectI(0, 0, 5, 10)));
        d.AddMask(); // from the selection: left half revealed
        Near(Red, At(d, 2, 5));
        Near(White, At(d, 7, 5));

        d.SetMaskEnabled(false);
        Near(Red, At(d, 7, 5));
        d.SetMaskEnabled(true);

        d.InvertMask();
        Near(White, At(d, 2, 5));
        Near(Red, At(d, 7, 5));

        d.ApplyMask();
        Assert.Null(top.Mask);
        Near(White, At(d, 2, 5));
        Near(Red, At(d, 7, 5));
        Assert.Equal(0, Px.GetColor(top.Bitmap, 2, 5).Alpha);
    }

    [Fact]
    public void Groups_apply_opacity_to_their_children_as_one()
    {
        var d = Document.Blank(8, 8, White);
        var group = d.AddGroup();
        var a = Solid(d, Red, "a"); // lands inside the active group
        Assert.Same(group, a.Parent);
        d.Active = group;
        d.SetOpacity(group, 0.5f);
        Near(new SKColor(255, 128, 128), At(d, 3, 3));
        d.SetVisible(group, false);
        Near(White, At(d, 3, 3));
    }

    [Fact]
    public void Reordering_moves_layers_through_and_out_of_groups()
    {
        var d = Document.Blank(4, 4, White);
        var red = Solid(d, Red, "red");
        var green = Solid(d, Green, "green");
        Near(Green, At(d, 1, 1));
        d.Active = red;
        d.MoveActive(1);
        Near(Red, At(d, 1, 1));
        d.History.Undo();
        Near(Green, At(d, 1, 1));

        d.Active = d.Find(green.Id);
        var group = d.AddGroup(); // beside the active layer: top of the stack
        d.Active = d.Find(green.Id);
        d.MoveActive(1); // into the expanded group above
        Assert.Same(d.Find(group.Id), d.Active!.Parent);
        d.MoveActive(1); // out of the top of the group
        Assert.Same(d.Root, d.Active!.Parent);

        d.MoveLayer(d.Active!, (GroupLayer)d.Find(group.Id)!, 0);
        Assert.Same(d.Find(group.Id), d.Active!.Parent);
        // A group cannot be moved into itself.
        var g = (GroupLayer)d.Find(group.Id)!;
        d.MoveLayer(g, g, 0);
        Assert.Same(d.Root, g.Parent);
    }

    [Fact]
    public void Merge_down_and_flatten_keep_the_picture()
    {
        var d = Document.Blank(12, 12, White);
        var a = Solid(d, Red, "a");
        d.SetOpacity(a, 0.6f);
        var b = Solid(d, Blue, "b");
        d.SetBlend(b, BlendMode.Screen);
        d.SetOpacity(b, 0.5f);
        var before = Bytes(d);

        d.Flatten();
        Assert.Single(d.Root.Children);
        Assert.Equal(before, Bytes(d));
        d.History.Undo();
        Assert.Equal(3, d.Root.Children.Count);
        Assert.Equal(before, Bytes(d));

        // Merging a Normal layer onto an opaque one is exact.
        var e = Document.Blank(6, 6, White);
        var top = Solid(e, Red);
        e.SetOpacity(top, 0.5f);
        var want = Bytes(e);
        Assert.True(e.CanMergeDown);
        e.MergeDown();
        Assert.Single(e.Root.Children);
        Assert.Equal(want, Bytes(e));
    }

    // --------------------------------------------------------------- painting

    [Fact]
    public void Brush_stroke_paints_undoes_and_redoes()
    {
        var d = Document.Blank(40, 40, White);
        var before = Bytes(d);
        using (var s = new Stroke(d, d.BeginPaint()!, StrokeKind.Brush, new BrushSettings { Size = 10, Hardness = 1 }, Red))
        {
            s.MoveTo(new SKPoint(10, 20));
            s.LineTo(new SKPoint(30, 20));
            Assert.True(s.Finish("Brush"));
        }
        Near(Red, At(d, 20, 20));
        Near(White, At(d, 20, 5));
        var painted = Bytes(d);

        Assert.True(d.History.Undo());
        Assert.Equal(before, Bytes(d));
        Assert.True(d.History.Redo());
        Assert.Equal(painted, Bytes(d));
    }

    [Fact]
    public void Overlapping_dabs_never_exceed_the_stroke_opacity()
    {
        var d = Document.Blank(40, 40, White);
        using var s = new Stroke(d, d.BeginPaint()!, StrokeKind.Brush, new BrushSettings { Size = 12, Hardness = 1, Opacity = 0.5f, Spacing = 0.05f }, Black);
        s.MoveTo(new SKPoint(10, 20));
        for (int i = 0; i < 5; i++) { s.LineTo(new SKPoint(30, 20)); s.LineTo(new SKPoint(10, 20)); }
        s.Finish("Brush");
        Near(new SKColor(128, 128, 128), At(d, 20, 20), 2);
    }

    [Fact]
    public void Strokes_respect_the_selection_and_the_eraser_clears()
    {
        var d = Document.Blank(40, 40, White);
        d.SetSelection(Sel.FromRect(40, 40, new SKRectI(0, 0, 20, 40)));
        using (var s = new Stroke(d, d.BeginPaint()!, StrokeKind.Brush, new BrushSettings { Size = 10, Hardness = 1 }, Red))
        {
            s.MoveTo(new SKPoint(5, 20));
            s.LineTo(new SKPoint(35, 20));
            s.Finish("Brush");
        }
        Near(Red, At(d, 10, 20));
        Near(White, At(d, 30, 20));

        d.Deselect();
        using (var e = new Stroke(d, d.BeginPaint()!, StrokeKind.Eraser, new BrushSettings { Size = 10, Hardness = 1 }, Red))
        {
            e.MoveTo(new SKPoint(10, 20));
            e.Finish("Eraser");
        }
        Assert.Equal(0, At(d, 10, 20).Alpha);
    }

    [Fact]
    public void Clone_stamp_copies_from_the_offset_source()
    {
        var d = Document.Blank(40, 40, White);
        Draw.Paint(d, "dot", c => c.DrawRect(new SKRect(4, 4, 12, 12), new SKPaint { Color = Blue }));
        using var s = new Stroke(d, d.BeginPaint()!, StrokeKind.Clone, new BrushSettings { Size = 8, Hardness = 1 }, Red, new SKPointI(-20, -20));
        s.MoveTo(new SKPoint(28, 28)); // copies from (8, 8)
        s.Finish("Clone");
        Near(Blue, At(d, 28, 28));
    }

    [Fact]
    public void Painting_on_a_small_layer_grows_it_and_history_stays_consistent()
    {
        var d = Document.Blank(60, 60, White);
        var small = Px.NewColor(10, 10);
        small.Erase(Green);
        var layer = d.AddLayer("small", small, 5, 5);
        var s0 = Bytes(d);
        using (var s = new Stroke(d, d.BeginPaint()!, StrokeKind.Brush, new BrushSettings { Size = 8, Hardness = 1 }, Red))
        {
            s.MoveTo(new SKPoint(50, 50)); // outside the layer's 10×10 bitmap
            s.Finish("Brush");
        }
        Near(Red, At(d, 50, 50));
        var s1 = Bytes(d);
        d.SetOpacity(d.Find(layer.Id)!, 0.5f);
        var s2 = Bytes(d);

        // Walk the whole history back and forth; every state must come back exactly.
        d.History.JumpTo(0);
        Assert.Single(d.Root.Children);
        d.History.JumpTo(1);
        Assert.Equal(s0, Bytes(d));
        d.History.JumpTo(2);
        Assert.Equal(s1, Bytes(d));
        d.History.JumpTo(3);
        Assert.Equal(s2, Bytes(d));
        d.History.JumpTo(1);
        Assert.Equal(s0, Bytes(d));
        d.History.JumpTo(3);
        Assert.Equal(s2, Bytes(d));
    }

    [Fact]
    public void Painting_on_a_mask_hides_and_reveals()
    {
        var d = Document.Blank(20, 20, White);
        Solid(d, Red);
        d.AddMask();
        Assert.True(d.EditMask);
        using (var s = new Stroke(d, d.BeginPaint()!, StrokeKind.Brush, new BrushSettings { Size = 8, Hardness = 1 }, Black))
        {
            s.MoveTo(new SKPoint(10, 10));
            s.Finish("Brush");
        }
        Near(White, At(d, 10, 10));
        Near(Red, At(d, 2, 2));
        d.History.Undo();
        Near(Red, At(d, 10, 10));
    }

    // -------------------------------------------------------------- selection

    [Fact]
    public void Selections_combine_invert_and_report_bounds()
    {
        using var a = Sel.FromRect(20, 20, new SKRectI(2, 2, 10, 10));
        using var b = Sel.FromRect(20, 20, new SKRectI(6, 6, 14, 14));
        using var add = Sel.Combine(a, b, SelectMode.Add);
        using var sub = Sel.Combine(a, b, SelectMode.Subtract);
        using var both = Sel.Combine(a, b, SelectMode.Intersect);
        Assert.Equal(new SKRectI(2, 2, 14, 14), Sel.Bounds(add));
        Assert.Equal(new SKRectI(6, 6, 10, 10), Sel.Bounds(both));
        Assert.Equal(new SKRectI(2, 2, 10, 10), Sel.Bounds(sub));
        Assert.Equal(0, sub.GetPixel(8, 8).Alpha);
        Assert.Equal(255, sub.GetPixel(3, 3).Alpha);

        using var inv = Sel.Invert(a, 20, 20);
        Assert.Equal(0, inv.GetPixel(5, 5).Alpha);
        Assert.Equal(255, inv.GetPixel(15, 15).Alpha);

        using var outline = Sel.Outline(a);
        Assert.Equal(new SKRect(2, 2, 10, 10), outline.Bounds);
        Assert.True(Sel.IsEmpty(Px.NewMask(4, 4)));
    }

    [Fact]
    public void Flood_follows_contiguous_colour_only_when_asked()
    {
        var d = Document.Blank(30, 10, White);
        Draw.Paint(d, "bar", c => c.DrawRect(new SKRect(10, 0, 20, 10), new SKPaint { Color = Black }));
        using var contiguous = Draw.FloodRegion(d, 2, 5, 10, true, false)!;
        Assert.Equal(new SKRectI(0, 0, 10, 10), Sel.Bounds(contiguous));
        using var global = Draw.FloodRegion(d, 2, 5, 10, false, false)!;
        Assert.Equal(new SKRectI(0, 0, 30, 10), Sel.Bounds(global));
        Assert.Equal(0, global.GetPixel(15, 5).Alpha);
    }

    [Fact]
    public void Fill_bucket_gradient_and_clear_work_through_the_selection()
    {
        var d = Document.Blank(20, 20, White);
        d.SetSelection(Sel.FromRect(20, 20, new SKRectI(0, 0, 10, 20)));
        Assert.True(Draw.Fill(d, Red));
        Near(Red, At(d, 5, 5));
        Near(White, At(d, 15, 5));

        d.Deselect();
        Assert.True(Draw.Bucket(d, 15, 5, Blue, 10, true, false));
        Near(Blue, At(d, 15, 15));
        Near(Red, At(d, 5, 5));

        Assert.True(Draw.Gradient(d, new SKPoint(0, 0), new SKPoint(20, 0), Black, White, false));
        Assert.True(At(d, 1, 10).Red < 30);
        Assert.True(At(d, 18, 10).Red > 220);

        d.SetSelection(Sel.FromRect(20, 20, new SKRectI(5, 5, 10, 10)));
        Assert.True(Draw.Clear(d));
        Assert.Equal(0, At(d, 7, 7).Alpha);
        Assert.Equal(255, At(d, 12, 7).Alpha);
        d.History.Undo();
        Assert.Equal(255, At(d, 7, 7).Alpha);
    }

    // ------------------------------------------------------------ adjustments

    [Fact]
    public void Neutral_adjustments_change_nothing_and_keep_transparency()
    {
        var src = Px.NewColor(16, 4);
        for (int x = 0; x < 16; x++)
            for (int y = 0; y < 4; y++) src.SetPixel(x, y, new SKColor((byte)(x * 16), (byte)(255 - x * 16), 77, y == 0 ? (byte)0 : (byte)255));
        Adjustment[] neutral = { new BrightnessContrast(), new Levels(), new Curves(), new Exposure(), new HueSaturation(), new ColorBalance() };
        foreach (var a in neutral)
        {
            using var r = a.Apply(src);
            for (int x = 0; x < 16; x++)
            {
                Assert.Equal(0, r.GetPixel(x, 0).Alpha);
                Near(src.GetPixel(x, 2), r.GetPixel(x, 2), 1);
            }
        }
    }

    [Fact]
    public void Adjustments_do_what_they_say()
    {
        var src = Px.NewColor(2, 1);
        src.SetPixel(0, 0, new SKColor(200, 100, 50));
        src.SetPixel(1, 0, new SKColor(255, 0, 0));

        using var inverted = new Invert().Apply(src);
        Near(new SKColor(55, 155, 205), inverted.GetPixel(0, 0), 0);

        using var grey = new Desaturate().Apply(src);
        var g = grey.GetPixel(0, 0);
        Assert.True(g.Red == g.Green && g.Green == g.Blue);

        using var hue = new HueSaturation { Hue = 120 }.Apply(src);
        Near(new SKColor(0, 255, 0), hue.GetPixel(1, 0), 2);

        using var brighter = new BrightnessContrast { Brightness = 40 }.Apply(src);
        Assert.True(brighter.GetPixel(0, 0).Green > 100);
        using var exposed = new Exposure { Stops = 1 }.Apply(src);
        Assert.True(exposed.GetPixel(0, 0).Green > 120);

        using var levels = new Levels { InBlack = 100, InWhite = 200 }.Apply(src);
        Near(new SKColor(255, 0, 0), levels.GetPixel(0, 0), 1);

        var curve = new Curves { Rgb = new List<SKPoint> { new(0, 0), new(0.5f, 0.75f), new(1, 1) } };
        var table = Curves.Table(curve.Rgb);
        Assert.Equal(0, table[0]);
        Assert.Equal(255, table[255]);
        Assert.InRange(table[128], 188, 194);
        for (int i = 1; i < 256; i++) Assert.True(table[i] >= table[i - 1], "curve must be monotone");

        using var warm = new ColorBalance { Midtones = new[] { 100f, 0f, -100f }, PreserveLuminosity = false }.Apply(grey);
        var w = warm.GetPixel(0, 0);
        Assert.True(w.Red > g.Red && w.Blue < g.Blue);

        var hist = Adjustment.Histogram(src);
        Assert.Equal(2, hist.Sum());
    }

    [Fact]
    public void Edit_session_previews_blends_through_selection_and_cancels()
    {
        var d = Document.Blank(20, 20, White);
        d.SetSelection(Sel.FromRect(20, 20, new SKRectI(0, 0, 10, 20)));
        int steps = d.History.Position;
        using (var s = PixelEditSession.Begin(d)!)
        {
            using var r = new Invert().Apply(s.Original);
            s.Show(r);
            Near(Black, At(d, 5, 5));
            Near(White, At(d, 15, 5));
            s.Cancel();
        }
        Near(White, At(d, 5, 5));
        Assert.Equal(steps, d.History.Position);

        using (var s = PixelEditSession.Begin(d)!)
        {
            using var r = new Invert().Apply(s.Original);
            s.Show(r);
            Assert.True(s.Commit("Invert"));
        }
        Near(Black, At(d, 5, 5));
        d.History.Undo();
        Near(White, At(d, 5, 5));
    }

    // ---------------------------------------------------------------- filters

    [Fact]
    public void Blur_keeps_flat_areas_and_spreads_detail_evenly()
    {
        var flat = Px.NewColor(32, 32);
        flat.Erase(new SKColor(90, 140, 200));
        using var blurredFlat = Filters.GaussianBlur(flat, 6);
        Assert.Equal(Px.GetRect(flat, new SKRectI(0, 0, 32, 32)), Px.GetRect(blurredFlat, new SKRectI(0, 0, 32, 32)));

        var dot = Px.NewColor(33, 33);
        dot.Erase(Black);
        dot.SetPixel(16, 16, White);
        using var b = Filters.GaussianBlur(dot, 3);
        Assert.True(b.GetPixel(16, 16).Red < 255);
        Assert.True(b.GetPixel(18, 16).Red > 0);
        Assert.Equal(b.GetPixel(14, 16).Red, b.GetPixel(18, 16).Red);
        Assert.Equal(b.GetPixel(16, 14).Red, b.GetPixel(16, 18).Red);
        Assert.Equal(255, b.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void Sharpen_raises_edge_contrast_and_noise_reduction_smooths_but_keeps_edges()
    {
        var edge = Px.NewColor(40, 10);
        for (int x = 0; x < 40; x++)
            for (int y = 0; y < 10; y++) edge.SetPixel(x, y, x < 20 ? new SKColor(100, 100, 100) : new SKColor(160, 160, 160));
        using var soft = Filters.GaussianBlur(edge, 1.5f);
        using var sharp = Filters.Sharpen(soft, 1.5f, 2f, 0);
        Assert.True(sharp.GetPixel(21, 5).Red > soft.GetPixel(21, 5).Red);
        Assert.True(sharp.GetPixel(18, 5).Red < soft.GetPixel(18, 5).Red);
        Assert.Equal(100, sharp.GetPixel(2, 5).Red);

        var rng = new Random(7);
        var noisy = Px.NewColor(60, 30);
        for (int x = 0; x < 60; x++)
            for (int y = 0; y < 30; y++)
            {
                int v = (x < 30 ? 60 : 200) + rng.Next(-12, 13);
                noisy.SetPixel(x, y, new SKColor((byte)v, (byte)v, (byte)v));
            }
        using var clean = Filters.ReduceNoise(noisy, 50, 3);
        double Spread(SKBitmap b, int x0)
        {
            var values = new List<double>();
            for (int x = x0; x < x0 + 20; x++)
                for (int y = 5; y < 25; y++) values.Add(b.GetPixel(x, y).Red);
            double mean = values.Average();
            return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        }
        Assert.True(Spread(clean, 5) < Spread(noisy, 5) * 0.5, "flat areas should be much smoother");
        Assert.True(clean.GetPixel(40, 15).Red - clean.GetPixel(20, 15).Red > 110, "the edge must survive");
    }

    // ------------------------------------------------------------- transforms

    [Fact]
    public void Crop_and_canvas_size_reframe_without_losing_pixels()
    {
        var d = Document.Blank(40, 30, White);
        Draw.Paint(d, "dot", c => c.DrawRect(new SKRect(10, 10, 12, 12), new SKPaint { Color = Red }));
        Transforms.Crop(d, new SKRectI(8, 8, 28, 24));
        Assert.Equal((20, 16), (d.Width, d.Height));
        Near(Red, At(d, 2, 2));
        d.History.Undo();
        Assert.Equal((40, 30), (d.Width, d.Height));
        Near(Red, At(d, 10, 10));

        Transforms.ResizeCanvas(d, 60, 50, 0.5f, 0.5f);
        Assert.Equal((60, 50), (d.Width, d.Height));
        Near(Red, At(d, 20, 20));
        Assert.Equal(0, At(d, 1, 1).Alpha);
    }

    [Fact]
    public void Rotating_and_flipping_the_canvas_moves_pixels_exactly()
    {
        var d = Document.Blank(40, 20, White);
        Draw.Paint(d, "dot", c => c.DrawRect(new SKRect(2, 3, 4, 5), new SKPaint { Color = Red })); // pixels (2..3, 3..4)
        Transforms.RotateCanvas(d, 1);
        Assert.Equal((20, 40), (d.Width, d.Height));
        Near(Red, At(d, 20 - 1 - 3, 2), 0); // (x, y) -> (h - 1 - y, x)
        Near(White, At(d, 3, 2), 0);
        Transforms.RotateCanvas(d, 3);
        Near(Red, At(d, 2, 3), 0);
        Near(Red, At(d, 3, 4), 0);
        Near(White, At(d, 4, 4), 0);

        Transforms.FlipCanvas(d, true);
        Near(Red, At(d, 40 - 1 - 2, 3), 0);
        Transforms.FlipCanvas(d, false);
        Near(Red, At(d, 40 - 1 - 2, 20 - 1 - 3), 0);
        Assert.Equal(255, At(d, 0, 0).Alpha);
        Assert.Equal(255, At(d, 39, 19).Alpha);
    }

    [Fact]
    public void Image_size_resamples_every_layer()
    {
        var d = Document.Blank(40, 20, White);
        Solid(d, Red);
        Transforms.ResizeImage(d, 80, 40);
        Assert.Equal((80, 40), (d.Width, d.Height));
        Near(Red, At(d, 70, 35));
        Assert.All(d.AllLayers().OfType<RasterLayer>(), l => Assert.Equal((80, 40), (l.Bitmap.Width, l.Bitmap.Height)));
        d.History.Undo();
        Assert.Equal((40, 20), (d.Width, d.Height));
    }

    [Fact]
    public void Layer_transform_scales_about_a_point_and_text_survives_only_moves()
    {
        var d = Document.Blank(100, 100, White);
        var bmp = Px.NewColor(20, 20);
        bmp.Erase(Blue);
        d.AddLayer("box", bmp, 40, 40);
        Transforms.TransformActive(d, SKMatrix.CreateScale(2, 2, 50, 50), "Scale");
        Near(Blue, At(d, 32, 32));
        Near(Blue, At(d, 68, 68));
        Near(White, At(d, 25, 25));

        var text = Draw.AddText(d, new TextInfo("Hi", "Arial", 24, false, false, 0xFF000000), 10, 10);
        Transforms.Offset(text, 5, 5);
        Assert.NotNull(text.Text);
        Transforms.RotateActive(d, 90);
        Assert.Null(((RasterLayer)d.Active!).Text);
    }

    // ------------------------------------------------------------------ files

    static string Temp(string ext) => Path.Combine(Path.GetTempPath(), "archaeaphoto-test-" + Guid.NewGuid().ToString("N") + ext);

    static SKBitmap Sample()
    {
        var b = Px.NewColor(48, 32);
        using var c = new SKCanvas(b);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(48, 32), new[] { Red, Blue }, null, SKShaderTileMode.Clamp);
        using var p = new SKPaint { Shader = shader };
        c.DrawPaint(p);
        using var hole = new SKPaint { BlendMode = SKBlendMode.Clear };
        c.DrawRect(new SKRect(4, 4, 12, 12), hole);
        using var half = new SKPaint { Color = Green.WithAlpha(128), BlendMode = SKBlendMode.Src };
        c.DrawRect(new SKRect(20, 4, 28, 12), half);
        return b;
    }

    [Theory]
    [InlineData(ImageFormat.Png, 0)]
    [InlineData(ImageFormat.Tiff, 1)]
    [InlineData(ImageFormat.WebP, 14)]
    [InlineData(ImageFormat.Jpeg, 30)]
    public void Images_round_trip_through_each_format(ImageFormat format, int tolerance)
    {
        using var src = Sample();
        var path = Temp(ImageIO.Extension(format));
        try
        {
            ImageIO.Save(src, path, format, 95);
            Assert.Equal(format, ImageIO.FormatFromPath(path));
            using var back = ImageIO.Load(path);
            Assert.Equal((src.Width, src.Height), (back.Width, back.Height));
            Near(Px.GetColor(src, 40, 28), Px.GetColor(back, 40, 28), Math.Max(2, tolerance));
            if (format == ImageFormat.Jpeg)
            {
                Assert.Equal(255, Px.GetColor(back, 8, 8).Alpha); // flattened onto white
                Assert.True(Px.GetColor(back, 8, 8).Red > 235);
            }
            else
            {
                Assert.Equal(0, Px.GetColor(back, 8, 8).Alpha);
                Assert.InRange(Px.GetColor(back, 24, 8).Alpha, 126, 130);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Projects_round_trip_layers_groups_masks_and_text()
    {
        var d = Document.Blank(64, 48, White);
        var photo = d.AddLayer("photo", Sample(), 6, 9);
        d.SetOpacity(photo, 0.7f);
        d.SetBlend(photo, BlendMode.Multiply);
        d.SetSelection(Sel.FromRect(64, 48, new SKRectI(0, 0, 30, 48)));
        d.AddMask();
        d.Deselect();
        var group = d.AddGroup();
        d.Rename(group, "Titles");
        var text = Draw.AddText(d, new TextInfo("Archaea\nPhoto", "Arial", 14, true, false, 0xFF112233), 3, 3);
        d.Active = d.Find(photo.Id);
        var want = Bytes(d);

        var path = Temp(Archa.Extension);
        try
        {
            Archa.Save(d, path, "test");
            var back = Archa.Load(path);
            Assert.Equal((64, 48), (back.Width, back.Height));
            Assert.Equal(want, Bytes(back));
            Assert.Equal(d.AllLayers().Select(l => l.Name), back.AllLayers().Select(l => l.Name));
            Assert.Equal(photo.Id, back.Active!.Id);
            var p = (RasterLayer)back.Find(photo.Id)!;
            Assert.Equal((6, 9, BlendMode.Multiply), (p.X, p.Y, p.Blend));
            Assert.Equal(0.7f, p.Opacity, 3);
            Assert.NotNull(p.Mask);
            var t = (RasterLayer)back.Find(text.Id)!;
            Assert.Equal("Archaea\nPhoto", t.Text!.Text);
            Assert.Equal(0xFF112233u, t.Text.Color);
            Assert.IsType<GroupLayer>(t.Parent);
            Assert.Equal("Titles", t.Parent!.Name);
            // New layers must not reuse a name counter that collides.
            Assert.DoesNotContain(back.NextName("Layer"), back.AllLayers().Select(l => l.Name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Damaged_or_foreign_files_are_rejected_with_a_message()
    {
        var path = Temp(Archa.Extension);
        try
        {
            File.WriteAllText(path, "not a zip");
            Assert.ThrowsAny<Exception>(() => Archa.Load(path));
            var png = Temp(".png");
            File.WriteAllText(png, "not a png");
            Assert.Throws<InvalidDataException>(() => ImageIO.Load(png));
            File.Delete(png);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Shapes_and_text_land_on_their_own_layers()
    {
        var d = Document.Blank(80, 60, White);
        var rect = Draw.Shape(d, ShapeKind.Rectangle, new SKPoint(10, 10), new SKPoint(30, 30), Red, null, 1)!;
        Near(Red, At(d, 20, 20));
        Near(White, At(d, 40, 20));
        Assert.True(rect.Bitmap.Width < 30);
        var line = Draw.Shape(d, ShapeKind.Line, new SKPoint(0, 50), new SKPoint(80, 50), null, Blue, 4)!;
        Near(Blue, At(d, 40, 50));
        Assert.Null(Draw.Shape(d, ShapeKind.Ellipse, new SKPoint(5, 5), new SKPoint(5, 5), Red, null, 1));

        var t = Draw.AddText(d, new TextInfo("M", "Arial", 30, true, false, 0xFF000000), 45, 5);
        bool dark = false;
        for (int x = 45; x < 79 && !dark; x++)
            for (int y = 5; y < 45 && !dark; y++) dark = At(d, x, y).Red < 60;
        Assert.True(dark, "the glyph should be visible");
        d.Edit("Edit Text", () => Draw.SetText(t, t.Text! with { Text = "MM" }));
        Assert.Equal("MM", t.Name);
        d.History.Undo();
        Assert.Equal("M", ((RasterLayer)d.Find(t.Id)!).Text!.Text);
        Assert.Equal(4, d.AllLayers().Count());
        _ = line;
    }

    /// <summary>
    /// Writes a small layered project to ARCHAEAPHOTO_SAMPLE_OUT when that is set:
    /// a real file for manual checks (for example that uninstalling keeps projects).
    /// </summary>
    [Fact]
    public void Sample_project_can_be_written_for_manual_checks()
    {
        var d = Document.Blank(640, 420, White);
        Draw.Gradient(d, new SKPoint(0, 0), new SKPoint(640, 420), new SKColor(0x24, 0x24, 0x24), new SKColor(0x5B, 0x6D, 0x10), false);
        Draw.Shape(d, ShapeKind.Ellipse, new SKPoint(380, 240), new SKPoint(560, 380), new SKColor(0xF5, 0xF5, 0xF0), new SKColor(0x16, 0x16, 0x16), 8);
        Draw.AddText(d, new TextInfo("ARCHAEA", "Arial", 64, true, false, 0xFFD6FF00), 40, 30);
        var path = Environment.GetEnvironmentVariable("ARCHAEAPHOTO_SAMPLE_OUT") is { Length: > 0 } wanted ? wanted : Temp(Archa.Extension);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Archa.Save(d, path, "test");
        Assert.Equal(3, Archa.Load(path).AllLayers().Count());
        if (path != Environment.GetEnvironmentVariable("ARCHAEAPHOTO_SAMPLE_OUT")) File.Delete(path);
    }
}
