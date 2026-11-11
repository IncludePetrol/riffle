using System.IO;

namespace Riffle;

public enum MediaKind { Image, Video }

public static class MediaTypes
{
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp",
        ".heic", ".heif", ".avif", ".jxl", ".ico", ".jxr", ".wdp", ".tga", ".psd", ".dds", ".exr", ".hdr", ".qoi",
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef", ".rwl", ".3fr", ".iiq", ".erf", ".mos", ".kdc", ".x3f",
    };

    public static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".qt", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".flv", ".f4v", ".mpg", ".mpeg", ".mpe", ".m1v", ".m2v",
        ".m2ts", ".mts", ".ts", ".tp", ".3gp", ".3g2", ".ogv", ".ogm", ".vob", ".divx", ".xvid", ".rm", ".rmvb", ".mxf", ".dv", ".y4m", ".hevc", ".h264", ".h265", ".264", ".265",
    };

    /// <summary>Formati che possiamo sovrascrivere mantenendo l'estensione.</summary>
    public static readonly HashSet<string> WritableExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".tif", ".tiff", ".webp",
    };

    public static MediaKind? KindOf(string path)
    {
        var e = Path.GetExtension(path);
        if (ImageExt.Contains(e)) return MediaKind.Image;
        if (VideoExt.Contains(e)) return MediaKind.Video;
        return null;
    }

    public static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif";
}

public sealed class MediaItem
{
    public MediaItem(string path, MediaKind kind) { Path = path; Kind = kind; Name = System.IO.Path.GetFileName(path); }
    public string Path { get; }
    public string Name { get; }
    public MediaKind Kind { get; }
    public bool IsVideo => Kind == MediaKind.Video;
    public DateTime Modified { get; set; }
    public long Size { get; set; }
    public override string ToString() => Name;
}
