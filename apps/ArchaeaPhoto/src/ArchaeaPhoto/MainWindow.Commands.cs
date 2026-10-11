using ArchaeaPhoto.Core;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace ArchaeaPhoto;

public sealed partial class MainWindow
{
    sealed record Command(string Group, string Title, KeyGesture? Gesture, Action Run, Func<bool>? Can);

    readonly List<Command> commands = new();
    /// <summary>Where an opened flat image came from, to avoid opening it twice and to suggest export names.</summary>
    readonly Dictionary<Document, string> sourcePaths = new();

    // ------------------------------------------------------------------ menus

    bool HasDoc() => ed.Doc is not null;
    bool HasLayer() => ed.Doc?.Active is not null;
    bool CanPaint() => ed.Doc?.GetPaintTarget() is not null;
    bool HasSelection() => ed.Doc?.HasSelection == true;
    bool HasMask() => ed.Doc?.Active?.Mask is not null;

    MenuItem Cmd(string group, string title, string? keys, Action run, Func<bool>? can = null)
    {
        var gesture = keys is null ? null : KeyGesture.Parse(keys);
        commands.Add(new Command(group, title.Replace("…", ""), gesture, run, can));
        var item = new MenuItem { Header = title, InputGesture = gesture, Tag = can };
        item.Click += (_, _) =>
        {
            if (can?.Invoke() ?? true) run();
        };
        return item;
    }

    static MenuItem Sub(string header, params object?[] items)
    {
        var parent = new MenuItem { Header = header };
        foreach (var i in items) parent.Items.Add(i ?? new Separator());
        // Enabled state is worked out when the menu opens, not tracked continuously.
        parent.SubmenuOpened += (_, e) =>
        {
            if (e.Source != parent) return;
            foreach (var child in parent.Items.OfType<MenuItem>())
                if (child.Tag is Func<bool> can) child.IsEnabled = can();
        };
        return parent;
    }

    void BuildMenu()
    {
        var recent = Sub("Open _Recent");
        recent.SubmenuOpened += (_, e) =>
        {
            if (e.Source != recent) return;
            recent.Items.Clear();
            foreach (var path in App.Settings.RecentFiles.Where(File.Exists))
            {
                var item = new MenuItem { Header = path.Replace("_", "__") };
                item.Click += (_, _) => _ = OpenPaths(new[] { path });
                recent.Items.Add(item);
            }
            if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });
        };
        recent.Items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });

        menu.Items.Add(Sub("_File",
            Cmd("File", "_New…", "Ctrl+N", () => _ = NewDocument()),
            Cmd("File", "_Open…", "Ctrl+O", () => _ = OpenDialog()),
            recent,
            null,
            Cmd("File", "_Save", "Ctrl+S", () => _ = Save(), HasDoc),
            Cmd("File", "Save _As…", "Ctrl+Shift+S", () => _ = SaveAs(), HasDoc),
            Cmd("File", "_Export As…", "Ctrl+Shift+E", () => _ = Export(), HasDoc),
            null,
            Cmd("File", "_Close", "Ctrl+W", () => { if (ed.Doc is { } d) _ = CloseDocument(d); }, HasDoc),
            Cmd("File", "E_xit", "Ctrl+Q", Close)));

        menu.Items.Add(Sub("_Edit",
            Cmd("Edit", "_Undo", "Ctrl+Z", Undo, () => ed.Doc?.History.CanUndo == true),
            Cmd("Edit", "_Redo", "Ctrl+Shift+Z", Redo, () => ed.Doc?.History.CanRedo == true),
            null,
            Cmd("Edit", "Cu_t", "Ctrl+X", Cut, CanPaint),
            Cmd("Edit", "_Copy", "Ctrl+C", () => Copy(false), HasLayer),
            Cmd("Edit", "Copy _Merged", "Ctrl+Shift+C", () => Copy(true), HasDoc),
            Cmd("Edit", "_Paste", "Ctrl+V", Paste, () => HasDoc() && ed.Clipboard is not null),
            null,
            Cmd("Edit", "_Fill with Foreground", "Alt+Back", () => Paint(d => Draw.Fill(d, ed.Foreground)), CanPaint),
            Cmd("Edit", "Fill with _Background", "Ctrl+Back", () => Paint(d => Draw.Fill(d, ed.Background)), CanPaint),
            Cmd("Edit", "C_lear", "Delete", () => Paint(d => Draw.Clear(d)), CanPaint),
            null,
            Cmd("Edit", "Free _Transform", "Ctrl+T", () => ed.SetTool(ed.Get<TransformTool>()), HasLayer),
            Sub("Tr_ansform",
                Cmd("Edit", "Flip _Horizontal", null, () => WithDoc(d => Transforms.FlipActive(d, true)), HasLayer),
                Cmd("Edit", "Flip _Vertical", null, () => WithDoc(d => Transforms.FlipActive(d, false)), HasLayer),
                null,
                Cmd("Edit", "Rotate 90° _Clockwise", null, () => WithDoc(d => Transforms.RotateActive(d, 90)), HasLayer),
                Cmd("Edit", "Rotate 90° C_ounter-clockwise", null, () => WithDoc(d => Transforms.RotateActive(d, -90)), HasLayer),
                Cmd("Edit", "Rotate _180°", null, () => WithDoc(d => Transforms.RotateActive(d, 180)), HasLayer))));

        menu.Items.Add(Sub("_Image",
            Sub("_Adjustments",
                Cmd("Adjustments", "_Brightness/Contrast…", null, BrightnessContrastDialog, CanPaint),
                Cmd("Adjustments", "_Levels…", "Ctrl+L", LevelsDialog, CanPaint),
                Cmd("Adjustments", "Cur_ves…", "Ctrl+M", CurvesDialog, CanPaint),
                Cmd("Adjustments", "_Exposure…", null, ExposureDialog, CanPaint),
                null,
                Cmd("Adjustments", "_Hue/Saturation…", "Ctrl+U", HueSaturationDialog, CanPaint),
                Cmd("Adjustments", "_Color Balance…", "Ctrl+B", ColorBalanceDialog, CanPaint),
                null,
                Cmd("Adjustments", "_Invert", "Ctrl+I", () => Instant(new Invert()), CanPaint),
                Cmd("Adjustments", "_Desaturate", "Ctrl+Shift+U", () => Instant(new Desaturate()), CanPaint)),
            null,
            Cmd("Image", "_Image Size…", "Ctrl+Alt+I", () => _ = SizeDialogFor(false), HasDoc),
            Cmd("Image", "Canvas _Size…", "Ctrl+Alt+C", () => _ = SizeDialogFor(true), HasDoc),
            Cmd("Image", "_Crop to Selection", null, CropToSelection, HasSelection),
            null,
            Sub("_Rotate Canvas",
                Cmd("Image", "90° _Clockwise", null, () => Reframe(d => Transforms.RotateCanvas(d, 1)), HasDoc),
                Cmd("Image", "90° C_ounter-clockwise", null, () => Reframe(d => Transforms.RotateCanvas(d, 3)), HasDoc),
                Cmd("Image", "_180°", null, () => Reframe(d => Transforms.RotateCanvas(d, 2)), HasDoc),
                null,
                Cmd("Image", "Flip Canvas _Horizontal", null, () => Reframe(d => Transforms.FlipCanvas(d, true)), HasDoc),
                Cmd("Image", "Flip Canvas _Vertical", null, () => Reframe(d => Transforms.FlipCanvas(d, false)), HasDoc))));

        menu.Items.Add(Sub("_Layer",
            Cmd("Layer", "_New Layer", "Ctrl+Shift+N", () => WithDoc(d => d.AddLayer()), HasDoc),
            Cmd("Layer", "New _Group", "Ctrl+G", () => WithDoc(d => d.AddGroup()), HasDoc),
            Cmd("Layer", "_Duplicate Layer", "Ctrl+J", () => WithDoc(d => d.DuplicateActive()), HasLayer),
            Cmd("Layer", "D_elete Layer", null, () => WithDoc(d => d.DeleteActive()), () => ed.Doc?.CanDeleteActive == true),
            Cmd("Layer", "_Rename Layer", "F2", () => layersPanel.RenameActive(), HasLayer),
            null,
            Cmd("Layer", "Add Layer _Mask", null, () => WithDoc(d => d.AddMask()), () => HasLayer() && !HasMask()),
            Cmd("Layer", "_Invert Mask", null, () => WithDoc(d => d.InvertMask()), HasMask),
            Cmd("Layer", "A_pply Mask", null, () => WithDoc(d => d.ApplyMask()), () => HasMask() && ed.Doc?.Active is RasterLayer),
            Cmd("Layer", "Dele_te Mask", null, () => WithDoc(d => d.DeleteMask()), HasMask),
            null,
            Cmd("Layer", "Bring _Forward", "Ctrl+OemCloseBrackets", () => WithDoc(d => d.MoveActive(1)), HasLayer),
            Cmd("Layer", "Send _Backward", "Ctrl+OemOpenBrackets", () => WithDoc(d => d.MoveActive(-1)), HasLayer),
            null,
            Cmd("Layer", "Merge Do_wn", "Ctrl+E", () => WithDoc(d => d.MergeDown()), () => ed.Doc?.CanMergeDown == true),
            Cmd("Layer", "_Flatten Image", null, () => WithDoc(d => d.Flatten()), HasDoc)));

        menu.Items.Add(Sub("_Select",
            Cmd("Select", "_All", "Ctrl+A", () => WithDoc(d => d.SelectAll()), HasDoc),
            Cmd("Select", "_Deselect", "Ctrl+D", () => WithDoc(d => d.Deselect()), HasSelection),
            Cmd("Select", "_Inverse", "Ctrl+Shift+I", () => WithDoc(d => d.InvertSelection()), HasDoc)));

        menu.Items.Add(Sub("Fil_ter",
            Cmd("Filter", "_Gaussian Blur…", null, BlurDialog, CanPaint),
            Cmd("Filter", "_Sharpen (Unsharp Mask)…", null, SharpenDialog, CanPaint),
            Cmd("Filter", "_Reduce Noise…", null, NoiseDialog, CanPaint)));

        menu.Items.Add(Sub("_View",
            Cmd("View", "Zoom _In", "Ctrl+OemPlus", () => canvas.ZoomStep(true), HasDoc),
            Cmd("View", "Zoom _Out", "Ctrl+OemMinus", () => canvas.ZoomStep(false), HasDoc),
            Cmd("View", "_Fit on Screen", "Ctrl+D0", () => canvas.Fit(), HasDoc),
            Cmd("View", "_Actual Size (100%)", "Ctrl+D1", canvas.ActualSize, HasDoc),
            null,
            Cmd("View", "_Dark Theme", null, () => SetTheme("dark")),
            Cmd("View", "_Light Theme", null, () => SetTheme("light"))));

        menu.Items.Add(Sub("_Help",
            Cmd("Help", "_Keyboard Shortcuts", "F1", () => _ = new ShortcutsDialog(ShortcutList()).ShowDialog(this)),
            Cmd("Help", "_About ArchaeaPhoto", null, () => _ = new AboutDialog().ShowDialog(this))));
    }

    IEnumerable<(string, string, string)> ShortcutList()
    {
        foreach (var t in ed.Tools.Where(t => !t.Transient && t.Shortcut != Key.None)) yield return ("Tools", t.Name, t.Shortcut.ToString());
        yield return ("Tools", "Pan the view", "Space + drag");
        yield return ("Tools", "Brush size", "[  ]");
        yield return ("Tools", "Swap / reset colours", "X / D");
        yield return ("Tools", "Zoom at pointer", "Ctrl + scroll");
        foreach (var c in commands.Where(c => c.Gesture is not null))
            yield return (c.Group, c.Title.Replace("_", ""), Pretty(c.Gesture!));
    }

    static string Pretty(KeyGesture g) => g.ToString()
        .Replace("OemPlus", "+").Replace("OemMinus", "-").Replace("OemCloseBrackets", "]").Replace("OemOpenBrackets", "[")
        .Replace("D0", "0").Replace("D1", "1").Replace("Back", "Backspace");

    void SetTheme(string theme)
    {
        App.Settings.Theme = theme;
        App.Settings.Save();
        App.ApplyTheme(theme);
    }

    void WithDoc(Action<Document> action)
    {
        if (ed.Doc is not { } d) return;
        ed.Tool.Cancel();
        action(d);
    }

    /// <summary>Runs an operation that paints on the active layer, explaining when there is nothing to paint on.</summary>
    void Paint(Func<Document, bool> action)
    {
        if (ed.Doc is not { } d) return;
        ed.Tool.Cancel();
        if (d.GetPaintTarget() is null) Flash("Select a pixel layer first.");
        else action(d);
    }

    /// <summary>Runs an operation that changes the canvas size, then re-fits the view.</summary>
    void Reframe(Action<Document> action)
    {
        WithDoc(action);
        canvas.Fit(false);
    }

    void Undo()
    {
        if (ed.Doc is not { } d) return;
        ed.Tool.Cancel();
        if (!d.History.Undo()) Flash("Nothing to undo.");
    }

    void Redo()
    {
        if (ed.Doc is not { } d) return;
        ed.Tool.Cancel();
        if (!d.History.Redo()) Flash("Nothing to redo.");
    }

    // -------------------------------------------------------------- documents

    static readonly FilePickerFileType ProjectType = new("ArchaeaPhoto project") { Patterns = new[] { "*.archa" } };
    static readonly FilePickerFileType AllOpenable = new("Images and projects")
    {
        Patterns = ImageIO.OpenExtensions.Select(e => "*" + e).Prepend("*.archa").ToArray(),
    };

    async Task<IStorageFolder?> StartFolder()
    {
        var folder = App.Settings.LastFolder;
        if (folder is null || !Directory.Exists(folder))
        {
            folder = AppSettings.ProjectsFolder;
            try { Directory.CreateDirectory(folder); }
            catch { return null; }
        }
        return await StorageProvider.TryGetFolderFromPathAsync(folder);
    }

    void RememberFolder(string path)
    {
        App.Settings.LastFolder = Path.GetDirectoryName(path);
        App.Settings.Save();
    }

    async Task Busy(string message, Func<Task> work)
    {
        var previous = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        rootBorder.IsEnabled = false;
        statusText.Text = message;
        try
        {
            await work();
        }
        finally
        {
            rootBorder.IsEnabled = true;
            Cursor = previous;
            statusText.Text = ed.Tool.Hint;
            canvas.Focus();
        }
    }

    async Task NewDocument()
    {
        var dialog = new NewDocumentDialog(ed.Background);
        await dialog.ShowDialog(this);
        if (dialog.Value is not { } v) return;
        SKColor? background = v.Background switch { 0 => SKColors.White, 2 => ed.Background, _ => null };
        try
        {
            ed.Add(Document.Blank(v.Width, v.Height, background, v.Name));
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException)
        {
            await MessageDialog.Info(this, "Could not create the document", "There is not enough memory for an image of that size.");
        }
    }

    async Task OpenDialog()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open", AllowMultiple = true, FileTypeFilter = new[] { AllOpenable, ProjectType, FilePickerFileTypes.All },
            SuggestedStartLocation = await StartFolder(),
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0) await OpenPaths(paths);
    }

    public async Task OpenPaths(IEnumerable<string> paths)
    {
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            var open = ed.Documents.FirstOrDefault(d => string.Equals(d.FilePath ?? sourcePaths.GetValueOrDefault(d), path, StringComparison.OrdinalIgnoreCase));
            if (open is not null) { ed.Activate(open); continue; }
            bool project = string.Equals(Path.GetExtension(path), Archa.Extension, StringComparison.OrdinalIgnoreCase);
            Document? doc = null;
            string? error = null;
            await Busy($"Opening {Path.GetFileName(path)}…", async () =>
            {
                try
                {
                    doc = await Task.Run(() => project ? Archa.Load(path) : Document.FromImage(ImageIO.Load(path), Path.GetFileName(path)));
                }
                catch (Exception ex)
                {
                    error = ex is OutOfMemoryException ? "There is not enough memory to open this file." : ex.Message;
                }
            });
            if (doc is null)
            {
                await MessageDialog.Info(this, "Could not open the file", $"{Path.GetFileName(path)}\n\n{error}");
                continue;
            }
            doc.Title = Path.GetFileName(path);
            if (project) doc.FilePath = path;
            else sourcePaths[doc] = path;
            ed.Add(doc);
            App.Settings.AddRecent(path);
            RememberFolder(path);
        }
    }

    Task<bool> Save() => ed.Doc is { } d ? Save(d) : Task.FromResult(false);

    async Task<bool> Save(Document doc)
    {
        ed.Tool.Cancel();
        if (doc.FilePath is null) return await SaveAs(doc);
        return await WriteProject(doc, doc.FilePath);
    }

    Task<bool> SaveAs() => ed.Doc is { } d ? SaveAs(d) : Task.FromResult(false);

    async Task<bool> SaveAs(Document doc)
    {
        ed.Tool.Cancel();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Project", SuggestedFileName = Path.GetFileNameWithoutExtension(doc.Title) + Archa.Extension, DefaultExtension = "archa",
            FileTypeChoices = new[] { ProjectType }, ShowOverwritePrompt = true, SuggestedStartLocation = await StartFolder(),
        });
        if (file?.TryGetLocalPath() is not { } path) return false;
        if (!path.EndsWith(Archa.Extension, StringComparison.OrdinalIgnoreCase)) path += Archa.Extension;
        return await WriteProject(doc, path);
    }

    async Task<bool> WriteProject(Document doc, string path)
    {
        string? error = null;
        doc.GetComposite(); // bring the cache up to date here so the worker only reads it
        await Busy($"Saving {Path.GetFileName(path)}…", async () =>
        {
            try { await Task.Run(() => Archa.Save(doc, path, App.Version)); }
            catch (Exception ex) { error = ex.Message; }
        });
        if (error is not null)
        {
            await MessageDialog.Info(this, "Could not save the project", error);
            return false;
        }
        doc.FilePath = path;
        doc.Title = Path.GetFileName(path);
        doc.Dirty = false;
        sourcePaths.Remove(doc);
        ed.NotifyDocList();
        App.Settings.AddRecent(path);
        RememberFolder(path);
        Flash($"Saved {Path.GetFileName(path)}");
        return true;
    }

    async Task Export()
    {
        if (ed.Doc is not { } doc) return;
        ed.Tool.Cancel();
        var types = new[]
        {
            new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
            new FilePickerFileType("JPEG image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
            new FilePickerFileType("WebP image") { Patterns = new[] { "*.webp" } },
            new FilePickerFileType("TIFF image") { Patterns = new[] { "*.tif", "*.tiff" } },
        };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export As", SuggestedFileName = Path.GetFileNameWithoutExtension(doc.Title), DefaultExtension = "png",
            FileTypeChoices = types, ShowOverwritePrompt = true, SuggestedStartLocation = await StartFolder(),
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        if (ImageIO.FormatFromPath(path) is not { } format)
        {
            format = ImageFormat.Png;
            path += ".png";
        }
        int quality = 90;
        if (format is ImageFormat.Jpeg or ImageFormat.WebP)
        {
            var q = new QualityDialog(format);
            await q.ShowDialog(this);
            if (q.Value is not { } chosen) return;
            quality = chosen;
        }
        string? error = null;
        using var flat = doc.Flattened();
        await Busy($"Exporting {Path.GetFileName(path)}…", async () =>
        {
            try { await Task.Run(() => ImageIO.Save(flat, path, format, quality)); }
            catch (Exception ex) { error = ex.Message; }
        });
        if (error is not null) await MessageDialog.Info(this, "Could not export the image", error);
        else
        {
            RememberFolder(path);
            Flash($"Exported {Path.GetFileName(path)}");
        }
    }

    /// <summary>Asks about unsaved changes. Returns false if the user cancelled.</summary>
    async Task<bool> ConfirmDiscard(Document doc)
    {
        if (!doc.Dirty) return true;
        int choice = await MessageDialog.Show(this, "Unsaved changes", $"Save changes to “{doc.Title}” before closing?", new[] { "Save", "Don't save", "Cancel" });
        return choice switch { 0 => await Save(doc), 1 => true, _ => false };
    }

    async Task CloseDocument(Document doc)
    {
        ed.Activate(doc);
        if (!await ConfirmDiscard(doc)) return;
        sourcePaths.Remove(doc);
        ed.Remove(doc);
    }

    // -------------------------------------------------------------- clipboard

    void Copy(bool merged)
    {
        if (ed.Doc is not { } doc) return;
        ed.Tool.Cancel();
        SKBitmap? src = merged ? doc.Flattened() : doc.Active is RasterLayer r ? Compositor.LayerOnCanvas(doc, r) : null;
        if (src is null) { Flash("Select a pixel layer to copy from."); return; }
        using (src)
        {
            if (doc.Selection is not null)
            {
                using var c = new SKCanvas(src);
                Px.DrawMask(c, doc.Selection, 0, 0, SKBlendMode.DstIn);
            }
            var bounds = Draw.AlphaBounds(src);
            if (bounds.IsEmpty) { Flash("There is nothing to copy there."); return; }
            var copy = Px.NewColor(bounds.Width, bounds.Height);
            using (var c = new SKCanvas(copy))
            using (var p = new SKPaint { BlendMode = SKBlendMode.Src })
                Px.Draw(c, src, -bounds.Left, -bounds.Top, p);
            ed.Clipboard?.Dispose();
            ed.Clipboard = copy;
            ed.ClipboardOrigin = new SKPointI(bounds.Left, bounds.Top);
            Flash($"Copied {bounds.Width} × {bounds.Height} px");
        }
    }

    void Cut()
    {
        if (ed.Doc is not { } doc || doc.GetPaintTarget() is not { IsMask: false }) { Flash("Select a pixel layer to cut from."); return; }
        Copy(false);
        Draw.Clear(doc);
    }

    void Paste()
    {
        if (ed.Doc is not { } doc || ed.Clipboard is not { } clip) return;
        ed.Tool.Cancel();
        var at = ed.ClipboardOrigin;
        // Keep the original position when it fits; otherwise centre the pasted pixels.
        if (!doc.Bounds.IntersectsWith(SKRectI.Create(at.X, at.Y, clip.Width, clip.Height)) || clip.Width > doc.Width || clip.Height > doc.Height)
            at = new SKPointI((doc.Width - clip.Width) / 2, (doc.Height - clip.Height) / 2);
        doc.AddLayer(doc.NextName("Layer"), Px.Clone(clip), at.X, at.Y, "Paste");
    }

    // ------------------------------------------------------------------ image

    async Task SizeDialogFor(bool canvasSize)
    {
        if (ed.Doc is not { } doc) return;
        ed.Tool.Cancel();
        var dialog = new SizeDialog(doc, canvasSize);
        await dialog.ShowDialog(this);
        if (dialog.Value is not { } v) return;
        try
        {
            if (canvasSize) Transforms.ResizeCanvas(doc, v.width, v.height, v.anchorX, v.anchorY);
            else Transforms.ResizeImage(doc, v.width, v.height);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException)
        {
            await MessageDialog.Info(this, "Could not resize", "There is not enough memory for an image of that size.");
        }
        canvas.Fit(false);
    }

    void CropToSelection()
    {
        if (ed.Doc is not { Selection: { } sel } doc) return;
        ed.Tool.Cancel();
        Transforms.Crop(doc, Sel.Bounds(sel));
        canvas.Fit(false);
    }

    // ---------------------------------------------------- adjustments, filters

    PixelEditSession? BeginPixelEdit()
    {
        if (ed.Doc is not { } doc) return null;
        ed.Tool.Cancel();
        var session = PixelEditSession.Begin(doc);
        if (session is null) Flash("Select a pixel layer first.");
        return session;
    }

    void Instant(Adjustment adjustment)
    {
        using var session = BeginPixelEdit();
        if (session is null) return;
        using var result = adjustment.Apply(session.Original);
        session.Show(result);
        session.Commit(adjustment.Name);
    }

    async void RunDialog(string name, Param[] parameters, Func<double[], Func<SKBitmap, SKBitmap>?> build,
        Func<PixelEditSession, (Control control, Action<AdjustDialog> attach)>? extra = null, double width = 400)
    {
        using var session = BeginPixelEdit();
        if (session is null) return;
        var custom = extra?.Invoke(session);
        var dialog = new AdjustDialog(name, session, parameters, build, custom?.control, width);
        custom?.attach(dialog);
        await dialog.ShowDialog(this);
        canvas.Focus();
    }

    static Func<double[], Func<SKBitmap, SKBitmap>?> Adjust(Func<double[], Adjustment?> make) => v => make(v) is { } a ? a.Apply : null;

    static string Signed(double v) => v.ToString("+0;-0;0");

    void BrightnessContrastDialog() => RunDialog("Brightness/Contrast",
        new[] { new Param("Brightness", -100, 100, 0, 1, Signed), new Param("Contrast", -100, 100, 0, 1, Signed) },
        Adjust(v => v[0] == 0 && v[1] == 0 ? null : new BrightnessContrast { Brightness = (float)v[0], Contrast = (float)v[1] }));

    void ExposureDialog() => RunDialog("Exposure",
        new[]
        {
            new Param("Exposure", -5, 5, 0, 0.05, v => v.ToString("+0.00;-0.00;0.00")),
            new Param("Offset", -0.5, 0.5, 0, 0.005, v => v.ToString("+0.000;-0.000;0.000")),
            new Param("Gamma", 0.1, 3, 1, 0.01, v => v.ToString("0.00")),
        },
        Adjust(v => v[0] == 0 && v[1] == 0 && Math.Abs(v[2] - 1) < 0.001 ? null : new Exposure { Stops = (float)v[0], Offset = (float)v[1], Gamma = (float)v[2] }));

    void HueSaturationDialog() => RunDialog("Hue/Saturation",
        new[] { new Param("Hue", -180, 180, 0, 1, v => Signed(v) + "°"), new Param("Saturation", -100, 100, 0, 1, Signed), new Param("Lightness", -100, 100, 0, 1, Signed) },
        Adjust(v => v.All(x => x == 0) ? null : new HueSaturation { Hue = (float)v[0], Saturation = (float)v[1], Lightness = (float)v[2] }));

    void LevelsDialog() => RunDialog("Levels",
        new[]
        {
            new Param("Input black", 0, 253, 0), new Param("Input white", 2, 255, 255), new Param("Midtones", 0.1, 5, 1, 0.01, v => v.ToString("0.00")),
            new Param("Output black", 0, 255, 0), new Param("Output white", 0, 255, 255),
        },
        Adjust(v => v[0] == 0 && v[1] == 255 && Math.Abs(v[2] - 1) < 0.001 && v[3] == 0 && v[4] == 255 ? null
            : new Levels { InBlack = (float)v[0], InWhite = (float)v[1], Gamma = (float)v[2], OutBlack = (float)v[3], OutWhite = (float)v[4] }),
        session => (new HistogramView(Adjustment.Histogram(session.Original)), _ => { }));

    void ColorBalanceDialog()
    {
        var parameters = new List<Param>();
        foreach (var tone in new[] { "Shadows", "Midtones", "Highlights" })
        {
            parameters.Add(new Param($"{tone} C–R", -100, 100, 0, 1, Signed));
            parameters.Add(new Param($"{tone} M–G", -100, 100, 0, 1, Signed));
            parameters.Add(new Param($"{tone} Y–B", -100, 100, 0, 1, Signed));
        }
        RunDialog("Color Balance", parameters.ToArray(), Adjust(v => v.All(x => x == 0) ? null : new ColorBalance
        {
            Shadows = new[] { (float)v[0], (float)v[1], (float)v[2] },
            Midtones = new[] { (float)v[3], (float)v[4], (float)v[5] },
            Highlights = new[] { (float)v[6], (float)v[7], (float)v[8] },
        }), session => (new TextBlock
        {
            Text = "Each slider shifts one tonal range between a colour and its opposite: cyan–red, magenta–green, yellow–blue.",
            Classes = { "dim" }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        }, _ => { }), 440);
    }

    void CurvesDialog()
    {
        CurveEditor? editor = null;
        RunDialog("Curves", Array.Empty<Param>(), _ =>
        {
            if (editor is null || editor.IsIdentity) return null;
            // Copy the points: the worker thread must not see later edits.
            var snapshot = new Curves
            {
                Rgb = editor.Curves.Rgb.ToList(), Red = editor.Curves.Red.ToList(), Green = editor.Curves.Green.ToList(), Blue = editor.Curves.Blue.ToList(),
            };
            return snapshot.Apply;
        }, session =>
        {
            editor = new CurveEditor(session.IsMask ? null : Adjustment.Histogram(session.Original));
            var channel = Ui.Combo(new[] { 0, 1, 2, 3 }, 0, i => new[] { "RGB", "Red", "Green", "Blue" }[i], editor.SetChannel, 110);
            var help = new TextBlock
            {
                Text = "Click the line to add a point and drag it. Right-click a point to remove it.", Classes = { "dim" }, FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 280,
            };
            var column = Ui.Column(10, Ui.Row(8, Ui.Label("Channel"), channel), editor, help);
            column.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            return (column, dialog =>
            {
                editor.Changed += dialog.RequestPreview;
                dialog.OnReset = editor.Reset;
            });
        }, 340);
    }

    void BlurDialog() => RunDialog("Gaussian Blur",
        new[] { new Param("Radius", 0.1, 100, 4, 0.1, v => $"{v:0.0} px") },
        v => { float r = (float)v[0]; return src => Filters.GaussianBlur(src, r); });

    void SharpenDialog() => RunDialog("Sharpen",
        new[] { new Param("Amount", 0, 300, 100, 1, v => $"{v:0}%"), new Param("Radius", 0.1, 20, 1.5, 0.1, v => $"{v:0.0} px"), new Param("Threshold", 0, 50, 0) },
        v =>
        {
            float amount = (float)v[0] / 100f, radius = (float)v[1];
            int threshold = (int)v[2];
            return amount <= 0 ? null : src => Filters.Sharpen(src, amount, radius, threshold);
        });

    void NoiseDialog() => RunDialog("Reduce Noise",
        new[] { new Param("Strength", 0, 100, 40), new Param("Radius", 1, 8, 3, 1, v => $"{v:0} px") },
        v =>
        {
            float strength = (float)v[0];
            int radius = (int)v[1];
            return strength <= 0 ? null : src => Filters.ReduceNoise(src, strength, radius);
        });
}
