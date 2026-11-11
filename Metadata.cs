using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace Riffle;

/// <summary>Lettura dei metadati EXIF per il pannello Informazioni.</summary>
public static class Metadata
{
    public static List<(string, string)> Read(string path)
    {
        var rows = new List<(string, string)>();
        try { ReadWic(path, rows); }
        catch { try { ReadMagick(path, rows); } catch { } }
        return rows;
    }

    private static object? Q(BitmapMetadata md, params string[] queries)
    {
        foreach (var q in queries)
        {
            try { var v = md.GetQuery(q); if (v != null) return v; } catch { }
        }
        return null;
    }

    private static double Rational(object? v)
    {
        switch (v)
        {
            case ulong u: { uint num = (uint)(u & 0xFFFFFFFF), den = (uint)(u >> 32); return den == 0 ? 0 : (double)num / den; }
            case long l: { int num = (int)(l & 0xFFFFFFFF), den = (int)(l >> 32); return den == 0 ? 0 : (double)num / den; }
            case double d: return d;
            case float f: return f;
            case null: return 0;
            default: try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }

    private static double Dms(object? v)
    {
        if (v is ulong[] a && a.Length >= 3) return Rational(a[0]) + Rational(a[1]) / 60 + Rational(a[2]) / 3600;
        if (v is double[] d && d.Length >= 3) return d[0] + d[1] / 60 + d[2] / 3600;
        return 0;
    }

    private static void ReadWic(string path, List<(string, string)> rows)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var f = dec.Frames[0];
        rows.Add((Loc.T("Formato"), dec.CodecInfo?.FriendlyName?.Replace(" Decoder", "").Replace("Decodificatore ", "") ?? Path.GetExtension(path)));
        if (f.Metadata is not BitmapMetadata md) return;

        string? taken = null;
        try { taken = md.DateTaken; } catch { }
        if (taken != null && DateTime.TryParse(taken, out var dt)) rows.Add((Loc.T("Data scatto"), dt.ToString("d MMMM yyyy, HH:mm:ss")));

        string cam = "";
        try { cam = $"{md.CameraManufacturer} {md.CameraModel}".Trim(); } catch { }
        if (cam.Length > 0)
        {
            // evita "Apple Apple iPhone"
            var parts = cam.Split(' ', 2);
            if (parts.Length == 2 && parts[1].StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) cam = parts[1];
            rows.Add((Loc.T("Fotocamera"), cam));
        }
        var lens = Q(md, "/app1/ifd/exif/{ushort=42036}", "/ifd/exif/{ushort=42036}") as string;
        if (!string.IsNullOrWhiteSpace(lens)) rows.Add((Loc.T("Obiettivo"), lens.Trim()));

        var parts2 = new List<string>();
        double exp = Rational(Q(md, "/app1/ifd/exif/{ushort=33434}", "/ifd/exif/{ushort=33434}"));
        if (exp > 0) parts2.Add(exp >= 1 ? $"{exp:0.#} s" : $"1/{Math.Round(1 / exp)} s");
        double fn = Rational(Q(md, "/app1/ifd/exif/{ushort=33437}", "/ifd/exif/{ushort=33437}"));
        if (fn > 0) parts2.Add($"f/{fn:0.#}");
        var iso = Q(md, "/app1/ifd/exif/{ushort=34855}", "/ifd/exif/{ushort=34855}");
        if (iso is ushort[] isoArr && isoArr.Length > 0) iso = isoArr[0];
        if (iso != null) parts2.Add($"ISO {iso}");
        double fl = Rational(Q(md, "/app1/ifd/exif/{ushort=37386}", "/ifd/exif/{ushort=37386}"));
        if (fl > 0) parts2.Add($"{fl:0.#} mm");
        if (parts2.Count > 0) rows.Add((Loc.T("Scatto"), string.Join("   ", parts2)));

        var flash = Q(md, "/app1/ifd/exif/{ushort=37385}", "/ifd/exif/{ushort=37385}");
        if (flash != null) try { rows.Add((Loc.T("Flash"), (Convert.ToInt32(flash) & 1) == 1 ? Loc.T("Sì") : Loc.T("No"))); } catch { }

        double lat = Dms(Q(md, "/app1/ifd/gps/{ushort=2}", "/ifd/gps/{ushort=2}"));
        double lon = Dms(Q(md, "/app1/ifd/gps/{ushort=4}", "/ifd/gps/{ushort=4}"));
        if (lat != 0 || lon != 0)
        {
            if ((Q(md, "/app1/ifd/gps/{ushort=1}", "/ifd/gps/{ushort=1}") as string)?.StartsWith("S") == true) lat = -lat;
            if ((Q(md, "/app1/ifd/gps/{ushort=3}", "/ifd/gps/{ushort=3}") as string)?.StartsWith("W") == true) lon = -lon;
            rows.Add((Loc.T("Posizione"), $"{lat.ToString("0.00000", CultureInfo.InvariantCulture)}, {lon.ToString("0.00000", CultureInfo.InvariantCulture)}"));
        }
        var sw = Q(md, "/app1/ifd/{ushort=305}", "/ifd/{ushort=305}") as string;
        if (!string.IsNullOrWhiteSpace(sw)) rows.Add((Loc.T("Software"), sw.Trim()));
    }

    private static void ReadMagick(string path, List<(string, string)> rows)
    {
        var info = new MagickImageInfo(path);
        rows.Add((Loc.T("Formato"), info.Format.ToString()));
        using var img = new MagickImage();
        img.Ping(path);
        var exif = img.GetExifProfile();
        if (exif == null) return;
        var dt = exif.GetValue(ExifTag.DateTimeOriginal)?.Value;
        if (dt != null && DateTime.TryParseExact(dt, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            rows.Add((Loc.T("Data scatto"), d.ToString("d MMMM yyyy, HH:mm:ss")));
        var cam = $"{exif.GetValue(ExifTag.Make)?.Value} {exif.GetValue(ExifTag.Model)?.Value}".Trim();
        if (cam.Length > 0) rows.Add((Loc.T("Fotocamera"), cam));
    }
}
