using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace Riffle;

public sealed class AnimatedFrames
{
    public required BitmapSource[] Frames { get; init; }
    public required int[] DelaysMs { get; init; }
}

public sealed class DecodedImage
{
    public required BitmapSource Bitmap { get; init; }   // già orientata, congelata
    public int FullWidth { get; init; }                  // dimensioni originali (orientate)
    public int FullHeight { get; init; }
    public bool IsFull { get; init; }                    // decodificata a piena risoluzione
    public AnimatedFrames? Animation { get; init; }
    public long Bytes { get; init; }
    public string Decoder { get; init; } = "";
}

/// <summary>Orientamento come (flip orizzontale applicato prima, poi rotazione oraria).</summary>
public readonly record struct Orient(bool Flip, int Rot)
{
    public static readonly Orient None = new(false, 0);
    public static Orient FromExif(int o) => o switch
    {
        2 => new(true, 0), 3 => new(false, 180), 4 => new(true, 180),
        5 => new(true, 270), 6 => new(false, 90), 7 => new(true, 90), 8 => new(false, 270),
        _ => None,
    };
    public int ToExif()
    {
        int r = Norm(Rot);
        return (Flip, r) switch
        {
            (false, 0) => 1, (true, 0) => 2, (false, 180) => 3, (true, 180) => 4,
            (true, 270) => 5, (false, 90) => 6, (true, 90) => 7, _ => 8,
        };
    }
    public static int Norm(int r) => ((r % 360) + 360) % 360;
    public bool SwapsAxes => Norm(Rot) is 90 or 270;
    public bool IsIdentity => !Flip && Norm(Rot) == 0;
    public Orient RotateCw(int deg) => new(Flip, Norm(Rot + deg));
    public Orient FlipH() => new(!Flip, Norm(-Rot));
    public Orient FlipV() => new(!Flip, Norm(180 - Rot));
    /// <summary>Applica prima "this" e poi "after".</summary>
    public Orient Then(Orient after)
    {
        var o = this;
        if (after.Flip) o = o.FlipH();
        return o.RotateCw(after.Rot);
    }

    public BitmapSource ApplyLazy(BitmapSource src)
    {
        if (IsIdentity) return src;
        var g = new TransformGroup();
        if (Flip) g.Children.Add(new ScaleTransform(-1, 1));
        if (Norm(Rot) != 0) g.Children.Add(new RotateTransform(Norm(Rot)));
        return new TransformedBitmap(src, g);
    }

    public BitmapSource Apply(BitmapSource src)
    {
        if (IsIdentity) return src;
        var g = new TransformGroup();
        if (Flip) g.Children.Add(new ScaleTransform(-1, 1));
        if (Norm(Rot) != 0) g.Children.Add(new RotateTransform(Norm(Rot)));
        var tb = new TransformedBitmap(src, g);
        var cb = new CachedBitmap(tb, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cb.Freeze();
        return cb;
    }
}

public static class ImageDecoder
{
    public const int MaxTexture = 16384;

    /// <summary>
    /// Decodifica l'immagine ridimensionandola per stare in maxW x maxH (0 = piena risoluzione).
    /// Usa WIC (velocissimo, con riduzione DCT nativa per i JPEG) e ripiega su Magick per i formati esotici.
    /// </summary>
    public static DecodedImage Decode(string path, int maxW, int maxH, CancellationToken ct)
    {
        byte[] buf;
        int len;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
        {
            long l = fs.Length;
            if (l > int.MaxValue - 64) throw new IOException("file troppo grande");
            len = (int)l;
            buf = ArrayPool<byte>.Shared.Rent(Math.Max(len, 1));
            int read = 0;
            while (read < len) { int n = fs.Read(buf, read, len - read); if (n <= 0) break; read += n; }
            len = read;
        }
        try { return DecodeBuffer(path, buf, len, maxW, maxH, ct); }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static DecodedImage DecodeBuffer(string path, byte[] data, int len, int maxW, int maxH, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext is ".gif" || (ext is ".webp" && IsAnimatedWebp(data)))
        {
            var anim = TryDecodeAnimation(data, len, maxW, maxH, ct);
            if (anim != null) return anim;
        }

        if (ext is not (".psd" or ".tga" or ".exr" or ".hdr" or ".qoi" or ".jxl"))
        {
            try { return DecodeWic(data, len, maxW, maxH, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* ripiega su Magick */ }
        }
        ct.ThrowIfCancellationRequested();
        return DecodeMagick(data, len, maxW, maxH);
    }

    private static double FitScale(int w, int h, int maxW, int maxH)
    {
        double s = 1;
        if (maxW > 0 && maxH > 0) s = Math.Min(1.0, Math.Min((double)maxW / w, (double)maxH / h));
        // limite texture GPU
        s = Math.Min(s, Math.Min((double)MaxTexture / w, (double)MaxTexture / h));
        return s;
    }

    public static int ReadOrientation(BitmapMetadata? md)
    {
        if (md == null) return 1;
        try
        {
            object? v = null;
            try { v = md.GetQuery("System.Photo.Orientation"); } catch { }
            if (v == null) try { v = md.GetQuery("/app1/ifd/{ushort=274}"); } catch { }
            if (v == null) try { v = md.GetQuery("/ifd/{ushort=274}"); } catch { }
            return v != null ? Convert.ToInt32(v) : 1;
        }
        catch { return 1; }
    }

    private static readonly Guid JpegContainer = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    private static DecodedImage DecodeWic(byte[] data, int len, int maxW, int maxH, CancellationToken ct)
    {
        var ms = new MemoryStream(data, 0, len, false);
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = dec.Frames[0];
        int pw = frame.PixelWidth, ph = frame.PixelHeight;
        var orient = Orient.FromExif(ReadOrientation(frame.Metadata as BitmapMetadata));
        int ow = orient.SwapsAxes ? ph : pw, oh = orient.SwapsAxes ? pw : ph;
        double s = FitScale(ow, oh, maxW, maxH);
        ct.ThrowIfCancellationRequested();

        int decodeW = 0;
        if (s < 1)
        {
            decodeW = Math.Max(1, (int)Math.Round(pw * s));
            // JPEG: il decoder riduce gratis di 1/2, 1/4, 1/8 (scalatura DCT). Se una di queste frazioni è
            // abbastanza vicina allo schermo la usiamo esatta e saltiamo il ridimensionamento successivo.
            bool jpeg = false;
            try { jpeg = dec.CodecInfo?.ContainerFormat == JpegContainer; } catch { }
            if (jpeg)
                for (int k = 3; k >= 1; k--)
                {
                    int c = (pw + (1 << k) - 1) >> k;
                    if (c >= decodeW * 0.9) { decodeW = c; break; }
                }
        }

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(data, 0, len, false);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeW > 0) bi.DecodePixelWidth = decodeW;
        bi.EndInit();
        bi.Freeze();
        ct.ThrowIfCancellationRequested();

        // rotazione + conversione di formato in un solo passaggio sui pixel
        var bmp = ToRenderFormat(orient.ApplyLazy(bi));
        return new DecodedImage
        {
            Bitmap = bmp, FullWidth = ow, FullHeight = oh, IsFull = s >= 1,
            Bytes = (long)bmp.PixelWidth * bmp.PixelHeight * 4, Decoder = "WIC",
        };
    }

    /// <summary>Converte in un formato che la GPU carica senza conversioni sul thread di rendering.</summary>
    private static BitmapSource ToRenderFormat(BitmapSource b)
    {
        var f = b.Format;
        if (f == PixelFormats.Bgr32 || f == PixelFormats.Pbgra32)
        {
            if (b is TransformedBitmap) { var c = new CachedBitmap(b, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); c.Freeze(); return c; }
            if (!b.IsFrozen) b.Freeze();
            return b;
        }
        bool alpha = f == PixelFormats.Bgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64 || f == PixelFormats.Rgba128Float
                     || f == PixelFormats.Prgba128Float || f == PixelFormats.Indexed8 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed2
                     || f == PixelFormats.Indexed1 || f == PixelFormats.Bgra32;
        var conv = new FormatConvertedBitmap(b, alpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, 0);
        var cb = new CachedBitmap(conv, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cb.Freeze();
        return cb;
    }

    private static DecodedImage DecodeMagick(byte[] data, int len, int maxW, int maxH)
    {
        using var img = new MagickImage(new MemoryStream(data, 0, len, false));
        img.AutoOrient();
        int ow = (int)img.Width, oh = (int)img.Height;
        double s = FitScale(ow, oh, maxW, maxH);
        if (s < 1)
        {
            img.FilterType = FilterType.Triangle;
            img.Resize((uint)Math.Max(1, ow * s), (uint)Math.Max(1, oh * s));
        }
        var bmp = ToBitmapSource(img);
        return new DecodedImage
        {
            Bitmap = bmp, FullWidth = ow, FullHeight = oh, IsFull = s >= 1,
            Bytes = (long)bmp.PixelWidth * bmp.PixelHeight * 4, Decoder = "Magick",
        };
    }

    public static BitmapSource ToBitmapSource(IMagickImage<byte> img)
    {
        int w = (int)img.Width, h = (int)img.Height;
        img.ColorSpace = ColorSpace.sRGB;
        var px = img.ToByteArray(MagickFormat.Bgra);
        var bs = BitmapSource.Create(w, h, 96, 96, img.HasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, px, w * 4);
        if (img.HasAlpha) bs = new CachedBitmap(new FormatConvertedBitmap(bs, PixelFormats.Pbgra32, null, 0), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        bs.Freeze();
        return bs;
    }

    private static bool IsAnimatedWebp(byte[] d)
    {
        // RIFF....WEBPVP8X con flag animazione (bit 1 del byte 20)
        return d.Length > 21 && d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'X' && (d[20] & 0x02) != 0;
    }

    private static DecodedImage? TryDecodeAnimation(byte[] data, int len, int maxW, int maxH, CancellationToken ct)
    {
        try
        {
            using var coll = new MagickImageCollection(new MemoryStream(data, 0, len, false));
            if (coll.Count <= 1) return null;
            coll.Coalesce();
            int ow = (int)coll[0].Width, oh = (int)coll[0].Height;
            double s = FitScale(ow, oh, maxW, maxH);
            int n = Math.Min(coll.Count, 1500);
            var frames = new BitmapSource[n];
            var delays = new int[n];
            long bytes = 0;
            for (int i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = coll[i];
                if (s < 1) f.Resize((uint)Math.Max(1, ow * s), (uint)Math.Max(1, oh * s));
                frames[i] = ToBitmapSource(f);
                int d = (int)f.AnimationDelay * 10;
                delays[i] = d <= 10 ? 100 : d;
                bytes += (long)frames[i].PixelWidth * frames[i].PixelHeight * 4;
                if (bytes > 800L * 1024 * 1024) { n = i + 1; Array.Resize(ref frames, n); Array.Resize(ref delays, n); break; }
            }
            return new DecodedImage
            {
                Bitmap = frames[0], FullWidth = ow, FullHeight = oh, IsFull = true,
                Animation = new AnimatedFrames { Frames = frames, DelaysMs = delays }, Bytes = bytes, Decoder = "Magick (animata)",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
