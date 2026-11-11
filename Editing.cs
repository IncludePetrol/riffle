using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace Riffle;

public sealed class Adjust
{
    public double Brightness, Contrast, Saturation, Warmth, Exposure; // -100..100
    public bool Grayscale;
    public bool IsNeutral => Brightness == 0 && Contrast == 0 && Saturation == 0 && Warmth == 0 && Exposure == 0 && !Grayscale;
    public Adjust Clone() => (Adjust)MemberwiseClone();

    /// <summary>Applica le regolazioni a pixel BGRA/RGBA (stride = w*bpp). rI/gI/bI = posizione dei canali.</summary>
    public void ApplyTo(byte[] px, int w, int h, int bpp, int rI, int gI, int bI)
    {
        if (IsNeutral) return;
        var lr = new byte[256]; var lg = new byte[256]; var lb = new byte[256];
        double gamma = Math.Pow(2, -Brightness / 100.0 * 1.1);
        double expo = Math.Pow(2, Exposure / 100.0 * 1.5);
        double cf = Contrast >= 0 ? 1 + Contrast / 100.0 * 1.2 : 1 + Contrast / 100.0 * 0.8;
        double wr = 1 + Warmth / 100.0 * 0.18, wb = 1 - Warmth / 100.0 * 0.18, wg = 1 + Warmth / 100.0 * 0.03;
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            lr[i] = Curve(v * wr); lg[i] = Curve(v * wg); lb[i] = Curve(v * wb);
        }
        byte Curve(double v)
        {
            v = Math.Clamp(v * expo, 0, 1);
            v = Math.Pow(v, gamma);
            v = (v - 0.5) * cf + 0.5;
            return (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        }
        int sf = Grayscale ? 0 : (int)Math.Round((1 + Saturation / 100.0) * 256);
        bool doSat = sf != 256;
        int stride = w * bpp;
        Parallel.For(0, h, y =>
        {
            int o = y * stride, end = o + stride;
            for (; o < end; o += bpp)
            {
                int r = lr[px[o + rI]], g = lg[px[o + gI]], b = lb[px[o + bI]];
                if (doSat)
                {
                    int l = (r * 77 + g * 150 + b * 29) >> 8;
                    r = l + (((r - l) * sf) >> 8); g = l + (((g - l) * sf) >> 8); b = l + (((b - l) * sf) >> 8);
                    r = r < 0 ? 0 : r > 255 ? 255 : r; g = g < 0 ? 0 : g > 255 ? 255 : g; b = b < 0 ? 0 : b > 255 ? 255 : b;
                }
                px[o + rI] = (byte)r; px[o + gI] = (byte)g; px[o + bI] = (byte)b;
            }
        });
    }
}

/// <summary>Stato di una sessione di modifica.</summary>
public sealed class EditState
{
    public Orient Orient = Orient.None;      // trasformazione aggiuntiva dell'utente
    public Rect Crop = new(0, 0, 1, 1);      // normalizzato, nello spazio già ruotato
    public Adjust Adjust = new();
    public bool IsDirty => !Orient.IsIdentity || Crop != new Rect(0, 0, 1, 1) || !Adjust.IsNeutral;
}

public static class Editing
{
    /// <summary>Anteprima veloce: lavora sulla bitmap già in cache (dimensione schermo).</summary>
    public sealed class Preview
    {
        private readonly BitmapSource _base;
        private Orient _orient = new(false, -1);
        private byte[] _pixels = [];
        private int _w, _h;
        private WriteableBitmap? _out;

        public Preview(BitmapSource baseBmp) { _base = baseBmp; }
        public int Width => _w;
        public int Height => _h;

        public WriteableBitmap Render(EditState st)
        {
            if (st.Orient != _orient)
            {
                _orient = st.Orient;
                var src = new FormatConvertedBitmap(st.Orient.Apply(_base), PixelFormats.Bgra32, null, 0);
                _w = src.PixelWidth; _h = src.PixelHeight;
                _pixels = new byte[_w * _h * 4];
                src.CopyPixels(_pixels, _w * 4, 0);
                _out = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgra32, null);
            }
            var work = (byte[])_pixels.Clone();
            st.Adjust.ApplyTo(work, _w, _h, 4, 2, 1, 0);
            _out!.WritePixels(new Int32Rect(0, 0, _w, _h), work, _w * 4, 0);
            return _out;
        }
    }

    /// <summary>Applica le modifiche all'originale a piena risoluzione e salva.</summary>
    public static void Save(string source, string dest, EditState st, int jpegQuality = 95)
    {
        using var img = new MagickImage(source);
        img.AutoOrient();
        if (st.Orient.Flip) img.Flop();
        if (Orient.Norm(st.Orient.Rot) != 0) img.Rotate(Orient.Norm(st.Orient.Rot));
        img.ResetPage();

        var c = st.Crop;
        if (c != new Rect(0, 0, 1, 1))
        {
            int W = (int)img.Width, H = (int)img.Height;
            int x = (int)Math.Round(c.X * W), y = (int)Math.Round(c.Y * H);
            int cw = Math.Max(1, (int)Math.Round(c.Width * W)), ch = Math.Max(1, (int)Math.Round(c.Height * H));
            cw = Math.Min(cw, W - x); ch = Math.Min(ch, H - y);
            img.Crop(new MagickGeometry(x, y, (uint)cw, (uint)ch));
            img.ResetPage();
        }

        if (!st.Adjust.IsNeutral)
        {
            int w = (int)img.Width, h = (int)img.Height;
            string map = img.HasAlpha ? "RGBA" : "RGB";
            int bpp = map.Length;
            byte[] arr;
            using (var pixels = img.GetPixels()) arr = pixels.ToByteArray(map)!;
            st.Adjust.ApplyTo(arr, w, h, bpp, 0, 1, 2);
            img.ImportPixels(arr, new PixelImportSettings((uint)w, (uint)h, StorageType.Char, map));
            if (st.Adjust.Grayscale) { /* resta RGB per compatibilità */ }
        }

        var ext = Path.GetExtension(dest).ToLowerInvariant();
        img.Format = ext switch
        {
            ".png" => MagickFormat.Png, ".bmp" => MagickFormat.Bmp, ".tif" or ".tiff" => MagickFormat.Tiff,
            ".webp" => MagickFormat.WebP, _ => MagickFormat.Jpeg,
        };
        if (img.Format == MagickFormat.Jpeg) img.Quality = (uint)jpegQuality;
        var exif = img.GetExifProfile();
        if (exif != null)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)1);
            exif.RemoveThumbnail();
            img.SetProfile(exif);
        }

        WriteSafely(dest, tmp => img.Write(tmp));
    }

    /// <summary>Scrive su un file temporaneo e poi sostituisce: mai un file a metà.</summary>
    public static void WriteSafely(string dest, Action<string> write)
    {
        var dir = Path.GetDirectoryName(dest)!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(dest) + ".riffle-tmp");
        try
        {
            write(tmp);
            if (File.Exists(dest))
            {
                var created = File.GetCreationTimeUtc(dest);
                File.Replace(tmp, dest, null, true);
                try { File.SetCreationTimeUtc(dest, created); } catch { }
            }
            else File.Move(tmp, dest);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    /// <summary>
    /// Rotazione senza perdita per i JPEG: aggiorna solo il tag EXIF Orientation sul posto.
    /// Restituisce false se non possibile (allora si ricodifica).
    /// </summary>
    public static bool TryLosslessRotate(string path, Orient extra)
    {
        if (!MediaTypes.IsJpeg(path)) return false;
        try
        {
            int cur;
            using (var fs = File.OpenRead(path))
            {
                var d = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                cur = ImageDecoder.ReadOrientation(d.Frames[0].Metadata as BitmapMetadata);
            }
            ushort next = (ushort)Orient.FromExif(cur).Then(extra).ToExif();
            var mod = File.GetLastWriteTimeUtc(path);
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var d = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                var wr = d.Frames[0].CreateInPlaceBitmapMetadataWriter();
                wr.SetQuery("/app1/ifd/{ushort=274}", next);
                if (wr.TrySave()) return true;
            }
            catch { }
            // nessun blocco EXIF: ne inseriamo uno minimo con il solo tag Orientation (sempre senza perdita)
            return TryInsertExifOrientation(path, next);
        }
        catch { return false; }
    }

    private static bool TryInsertExifOrientation(string path, ushort orientation)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;
        int pos = 2, insertAt = 2;
        // salta APP0 (JFIF); se esiste già un APP1 Exif non tocchiamo nulla
        while (pos + 4 < d.Length && d[pos] == 0xFF && d[pos + 1] >= 0xE0 && d[pos + 1] <= 0xEF)
        {
            int len = (d[pos + 2] << 8) | d[pos + 3];
            if (d[pos + 1] == 0xE1 && pos + 10 < d.Length && d[pos + 4] == 'E' && d[pos + 5] == 'x' && d[pos + 6] == 'i' && d[pos + 7] == 'f') return false;
            if (d[pos + 1] == 0xE0) insertAt = pos + 2 + len;
            pos += 2 + len;
        }
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 34, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,          // header TIFF little-endian, IFD0 a offset 8
            1, 0,                                              // 1 voce
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, // Orientation, SHORT, 1
            0, 0, 0, 0,                                        // nessun IFD successivo
        ];
        var outp = new byte[d.Length + app1.Length];
        Buffer.BlockCopy(d, 0, outp, 0, insertAt);
        Buffer.BlockCopy(app1, 0, outp, insertAt, app1.Length);
        Buffer.BlockCopy(d, insertAt, outp, insertAt + app1.Length, d.Length - insertAt);
        WriteSafely(path, tmp => File.WriteAllBytes(tmp, outp));
        return true;
    }

    public static void SavePng(BitmapSource bmp, string dest)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(dest);
        enc.Save(fs);
    }

    public static string UniqueName(string path, string suffix, string? newExt = null)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = newExt ?? Path.GetExtension(path);
        var p = Path.Combine(dir, name + suffix + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name}{suffix} ({i}){ext}");
        return p;
    }
}
