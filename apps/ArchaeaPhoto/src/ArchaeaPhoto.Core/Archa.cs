using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace ArchaeaPhoto.Core;

/// <summary>
/// The native layered project format. An .archa file is a ZIP archive:
///   document.json        canvas size and the layer tree (see <see cref="DocDto"/>)
///   layers/&lt;id&gt;.png      one RGBA PNG per raster layer, at the layer's own size
///   masks/&lt;id&gt;.png       one greyscale PNG per layer mask
///   preview.png          flattened thumbnail for file browsers
/// Unknown JSON fields are ignored so newer files stay readable where possible;
/// a breaking change bumps "version".
/// </summary>
public static unsafe class Archa
{
    public const string Extension = ".archa";
    public const int Version = 1;

    sealed class DocDto
    {
        public string Format { get; set; } = "archa";
        public int Version { get; set; } = Archa.Version;
        public string? App { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Guid? Active { get; set; }
        public List<LayerDto> Layers { get; set; } = new();
    }

    sealed class LayerDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = "raster";
        public string Name { get; set; } = "";
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1f;
        public string Blend { get; set; } = "Normal";
        public int X { get; set; }
        public int Y { get; set; }
        public MaskDto? Mask { get; set; }
        public TextDto? Text { get; set; }
        public bool? Expanded { get; set; }
        /// <summary>Bottom-most first.</summary>
        public List<LayerDto>? Children { get; set; }
    }

    sealed class MaskDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool Enabled { get; set; } = true;
    }

    sealed class TextDto
    {
        public string Text { get; set; } = "";
        public string Family { get; set; } = "";
        public float Size { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public string Color { get; set; } = "#FF000000";
    }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Save(Document doc, string path, string? appVersion = null)
    {
        var dto = new DocDto
        {
            App = appVersion is null ? "ArchaeaPhoto" : "ArchaeaPhoto " + appVersion,
            Width = doc.Width, Height = doc.Height, Active = doc.Active?.Id,
            Layers = doc.Root.Children.Select(ToDto).ToList(),
        };
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Write(zip, "document.json", JsonSerializer.SerializeToUtf8Bytes(dto, Json), CompressionLevel.Optimal);
                foreach (var layer in doc.AllLayers())
                {
                    // PNG data is already compressed; store it as is.
                    if (layer is RasterLayer r) Write(zip, $"layers/{layer.Id:N}.png", ImageIO.EncodePng(r.Bitmap), CompressionLevel.NoCompression);
                    if (layer.Mask is not null) Write(zip, $"masks/{layer.Id:N}.png", EncodeMask(layer.Mask), CompressionLevel.NoCompression);
                }
                Write(zip, "preview.png", Preview(doc), CompressionLevel.NoCompression);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        using var s = zip.CreateEntry(name, level).Open();
        s.Write(data);
    }

    static LayerDto ToDto(Layer l)
    {
        var d = new LayerDto
        {
            Id = l.Id, Name = l.Name, Visible = l.Visible, Opacity = l.Opacity, Blend = l.Blend.ToString(),
            Mask = l.Mask is null ? null : new MaskDto { X = l.MaskX, Y = l.MaskY, Enabled = l.MaskEnabled },
        };
        if (l is RasterLayer r)
        {
            d.X = r.X;
            d.Y = r.Y;
            if (r.Text is { } t)
                d.Text = new TextDto { Text = t.Text, Family = t.Family, Size = t.Size, Bold = t.Bold, Italic = t.Italic, Color = "#" + t.Color.ToString("X8") };
        }
        else if (l is GroupLayer g)
        {
            d.Type = "group";
            d.Expanded = g.Expanded;
            d.Children = g.Children.Select(ToDto).ToList();
        }
        return d;
    }

    static byte[] EncodeMask(SKBitmap mask)
    {
        using var gray = new SKBitmap(new SKImageInfo(mask.Width, mask.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        for (int y = 0; y < mask.Height; y++)
            Buffer.MemoryCopy((byte*)mask.GetPixels() + (long)y * mask.RowBytes, (byte*)gray.GetPixels() + (long)y * gray.RowBytes, mask.Width, mask.Width);
        return ImageIO.EncodePng(gray);
    }

    static byte[] Preview(Document doc)
    {
        float scale = Math.Min(1f, 512f / Math.Max(doc.Width, doc.Height));
        int w = Math.Max(1, (int)(doc.Width * scale)), h = Math.Max(1, (int)(doc.Height * scale));
        using var bmp = Px.NewColor(w, h);
        using (var c = new SKCanvas(bmp))
        using (var p = new SKPaint { FilterQuality = SKFilterQuality.Medium })
            Px.Draw(c, doc.GetComposite(), new SKRect(0, 0, doc.Width, doc.Height), new SKRect(0, 0, w, h), p);
        return ImageIO.EncodePng(bmp);
    }

    public static Document Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("document.json") ?? throw new InvalidDataException("This is not an ArchaeaPhoto project (document.json is missing).");
        DocDto dto;
        using (var s = entry.Open())
            dto = JsonSerializer.Deserialize<DocDto>(s, Json) ?? throw new InvalidDataException("The project file is empty.");
        if (dto.Format != "archa") throw new InvalidDataException("This is not an ArchaeaPhoto project.");
        if (dto.Version > Version)
            throw new InvalidDataException($"This project was saved by a newer version of ArchaeaPhoto (format {dto.Version}). Update ArchaeaPhoto to open it.");
        if (dto.Width < 1 || dto.Height < 1 || dto.Width > Document.MaxSize || dto.Height > Document.MaxSize)
            throw new InvalidDataException("The project has an invalid canvas size.");

        var doc = new Document(dto.Width, dto.Height);
        int count = 0;
        foreach (var l in dto.Layers) doc.Root.Insert(doc.Root.Children.Count, FromDto(zip, l, ref count, 0));
        if (count == 0) throw new InvalidDataException("The project contains no layers.");
        doc.Active = dto.Active is { } id ? doc.Find(id) : null;
        doc.Active ??= doc.Root.Children[^1];
        doc.SetNameCounter(count);
        return doc;
    }

    static Layer FromDto(ZipArchive zip, LayerDto d, ref int count, int depth)
    {
        if (depth > 32) throw new InvalidDataException("The project's layer groups are nested too deeply.");
        count++;
        var id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id;
        Layer layer;
        if (d.Type == "group")
        {
            var g = new GroupLayer { Id = id, Expanded = d.Expanded ?? true };
            foreach (var c in d.Children ?? new()) g.Insert(g.Children.Count, FromDto(zip, c, ref count, depth + 1));
            layer = g;
        }
        else
        {
            // Entry names are rebuilt from the id, never taken from the file.
            var bmp = DecodeColor(ReadEntry(zip, $"layers/{d.Id:N}.png"));
            TextInfo? text = d.Text is { } t
                ? new TextInfo(t.Text, t.Family, t.Size, t.Bold, t.Italic, uint.TryParse(t.Color.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var col) ? col : 0xFF000000)
                : null;
            layer = new RasterLayer(bmp, d.X, d.Y) { Id = id, Text = text };
        }
        layer.Name = string.IsNullOrWhiteSpace(d.Name) ? "Layer" : d.Name;
        layer.Visible = d.Visible;
        layer.Opacity = Math.Clamp(d.Opacity, 0f, 1f);
        layer.Blend = Enum.TryParse<BlendMode>(d.Blend, out var blend) ? blend : BlendMode.Normal;
        if (d.Mask is { } m)
        {
            layer.Mask = DecodeMask(ReadEntry(zip, $"masks/{d.Id:N}.png"));
            layer.MaskX = m.X;
            layer.MaskY = m.Y;
            layer.MaskEnabled = m.Enabled;
        }
        return layer;
    }

    static byte[] ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name) ?? throw new InvalidDataException($"The project is damaged ({name} is missing).");
        if (e.Length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The project contains an oversized layer.");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static SKBitmap DecodeColor(byte[] png)
    {
        using var codec = SKCodec.Create(new MemoryStream(png)) ?? throw new InvalidDataException("A layer image in the project is damaged.");
        if (codec.Info.Width > Document.MaxSize * 2 || codec.Info.Height > Document.MaxSize * 2)
            throw new InvalidDataException("A layer in the project is too large.");
        var bmp = new SKBitmap(Px.ColorInfo(codec.Info.Width, codec.Info.Height));
        if (codec.GetPixels(bmp.Info, bmp.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("A layer image in the project is damaged.");
        return bmp;
    }

    static SKBitmap DecodeMask(byte[] png)
    {
        using var color = DecodeColor(png);
        var mask = Px.NewMask(color.Width, color.Height);
        for (int y = 0; y < color.Height; y++)
        {
            byte* s = (byte*)color.GetPixels() + (long)y * color.RowBytes;
            byte* d = (byte*)mask.GetPixels() + (long)y * mask.RowBytes;
            for (int x = 0; x < color.Width; x++) d[x] = s[x * 4 + 1];
        }
        return mask;
    }
}
