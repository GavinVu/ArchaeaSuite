using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace ArchaeaPhoto.Core;

public enum ImageFormat { Png, Jpeg, WebP, Tiff }

/// <summary>Reading and writing flat image files.</summary>
public static unsafe class ImageIO
{
    public static readonly string[] OpenExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".tif", ".tiff", ".bmp", ".gif" };

    public static ImageFormat? FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png,
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.WebP,
        ".tif" or ".tiff" => ImageFormat.Tiff,
        _ => null,
    };

    public static string Extension(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.WebP => ".webp",
        ImageFormat.Tiff => ".tif",
        _ => ".png",
    };

    /// <summary>Loads an image as BGRA premultiplied, upright (EXIF orientation applied).</summary>
    public static SKBitmap Load(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".tif" or ".tiff") return LoadTiff(path);

        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("This file is not an image ArchaeaPhoto can read.");
        var info = Px.ColorInfo(codec.Info.Width, codec.Info.Height);
        if (info.Width > Document.MaxSize || info.Height > Document.MaxSize)
            throw new InvalidDataException($"Images larger than {Document.MaxSize} px on a side are not supported.");
        var bmp = new SKBitmap(info);
        var result = codec.GetPixels(info, bmp.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bmp.Dispose();
            throw new InvalidDataException($"The image could not be decoded ({result}).");
        }
        return Orient(bmp, codec.EncodedOrigin);
    }

    static SKBitmap Orient(SKBitmap bmp, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return bmp;
        int w = bmp.Width, h = bmp.Height;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = Px.NewColor(swap ? h : w, swap ? w : h);
        using var c = new SKCanvas(dst);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: c.Scale(-1, 1, w / 2f, 0); break;
            case SKEncodedOrigin.BottomRight: c.RotateDegrees(180, w / 2f, h / 2f); break;
            case SKEncodedOrigin.BottomLeft: c.Scale(1, -1, 0, h / 2f); break;
            case SKEncodedOrigin.LeftTop: c.Translate(h, 0); c.RotateDegrees(90); c.Scale(1, -1, 0, h / 2f); break;
            case SKEncodedOrigin.RightTop: c.Translate(h, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: c.Translate(h, 0); c.RotateDegrees(90); c.Scale(-1, 1, w / 2f, 0); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, w); c.RotateDegrees(270); break;
        }
        Px.Draw(c, bmp, 0, 0);
        bmp.Dispose();
        return dst;
    }

    /// <summary>Writes a flattened image. JPEG has no transparency, so it is flattened onto white.</summary>
    public static void Save(SKBitmap image, string path, ImageFormat format, int quality = 90)
    {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (format == ImageFormat.Tiff) SaveTiff(image, tmp);
            else
            {
                SKBitmap src = image;
                if (format == ImageFormat.Jpeg)
                {
                    src = Px.NewColor(image.Width, image.Height);
                    using var c = new SKCanvas(src);
                    c.Clear(SKColors.White);
                    Px.Draw(c, image, 0, 0);
                }
                try
                {
                    using var pm = src.PeekPixels();
                    using var data = pm.Encode(format switch
                    {
                        ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                        ImageFormat.WebP => SKEncodedImageFormat.Webp,
                        _ => SKEncodedImageFormat.Png,
                    }, Math.Clamp(quality, 1, 100)) ?? throw new IOException("The image could not be encoded.");
                    using var fs = File.Create(tmp);
                    data.SaveTo(fs);
                }
                finally
                {
                    if (!ReferenceEquals(src, image)) src.Dispose();
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>PNG bytes of a bitmap (colour or mask), used inside .archa files.</summary>
    public static byte[] EncodePng(SKBitmap bmp)
    {
        using var pm = bmp.PeekPixels();
        using var data = pm.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("PNG encoding failed.");
        return data.ToArray();
    }

    // ------------------------------------------------------------------- TIFF

    static SKBitmap LoadTiff(string path)
    {
        Tiff.SetErrorHandler(new QuietTiffErrors());
        using var tiff = Tiff.Open(path, "r") ?? throw new InvalidDataException("This TIFF file could not be opened.");
        int w = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt(), h = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        if (w > Document.MaxSize || h > Document.MaxSize)
            throw new InvalidDataException($"Images larger than {Document.MaxSize} px on a side are not supported.");
        var raster = new int[w * h];
        if (!tiff.ReadRGBAImageOriented(w, h, raster, Orientation.TOPLEFT))
            throw new InvalidDataException("This TIFF file uses a format ArchaeaPhoto cannot read.");
        var bmp = new SKBitmap(Px.ColorInfo(w, h));
        byte* p = (byte*)bmp.GetPixels();
        for (int y = 0; y < h; y++)
        {
            byte* row = p + (long)y * bmp.RowBytes;
            for (int x = 0; x < w; x++)
            {
                // The raster is ABGR packed, alpha already associated (premultiplied).
                int v = raster[y * w + x];
                row[x * 4] = (byte)Tiff.GetB(v);
                row[x * 4 + 1] = (byte)Tiff.GetG(v);
                row[x * 4 + 2] = (byte)Tiff.GetR(v);
                row[x * 4 + 3] = (byte)Tiff.GetA(v);
            }
        }
        return bmp;
    }

    static void SaveTiff(SKBitmap image, string path)
    {
        Tiff.SetErrorHandler(new QuietTiffErrors());
        using var tiff = Tiff.Open(path, "w") ?? throw new IOException("The TIFF file could not be created.");
        int w = image.Width, h = image.Height;
        tiff.SetField(TiffTag.IMAGEWIDTH, w);
        tiff.SetField(TiffTag.IMAGELENGTH, h);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 4);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
        tiff.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL);
        tiff.SetField(TiffTag.ROWSPERSTRIP, Math.Max(1, 65536 / (w * 4)));
        tiff.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.ASSOCALPHA });
        var row = new byte[w * 4];
        byte* p = (byte*)image.GetPixels();
        for (int y = 0; y < h; y++)
        {
            byte* s = p + (long)y * image.RowBytes;
            for (int x = 0; x < w; x++)
            {
                row[x * 4] = s[x * 4 + 2];
                row[x * 4 + 1] = s[x * 4 + 1];
                row[x * 4 + 2] = s[x * 4];
                row[x * 4 + 3] = s[x * 4 + 3];
            }
            if (!tiff.WriteScanline(row, y)) throw new IOException("Writing the TIFF file failed.");
        }
    }

    sealed class QuietTiffErrors : TiffErrorHandler
    {
        public override void ErrorHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
    }
}
