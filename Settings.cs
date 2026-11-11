using System.IO;
using System.Text.Json;

namespace Riffle;

public enum SortBy { Name, Modified, Size, Type }

public sealed class Settings
{
    public SortBy Sort { get; set; } = SortBy.Name;
    public bool SortDescending { get; set; }
    public bool WheelZooms { get; set; }
    public bool AutoPlayVideo { get; set; } = true;
    public bool LoopVideo { get; set; } = true;
    public bool ConfirmDelete { get; set; } = true;
    public bool ShowStrip { get; set; } = true;
    public bool ShowInfo { get; set; }
    public bool BlackBackground { get; set; }
    public int SlideshowSeconds { get; set; } = 3;
    public int Volume { get; set; } = 80;
    public bool Mute { get; set; }
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = 1280;
    public double WinHeight { get; set; } = 800;
    public bool WinMaximized { get; set; }
    public string? LastFolder { get; set; }
    /// <summary>"auto", "it" o "en".</summary>
    public string Language { get; set; } = "auto";

    private static Settings? _current;
    public static Settings Current => _current ??= Load();

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Riffle", "settings.json");

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
