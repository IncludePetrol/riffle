using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Media;

namespace Riffle;

public partial class App : Application
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riffle");

    protected override void OnStartup(StartupEventArgs e)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        // diagnostica: RIFFLE_SOFTWARE=1 disegna senza GPU (utile se il driver video ha problemi)
        if (Environment.GetEnvironmentVariable("RIFFLE_SOFTWARE") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        try { Directory.CreateDirectory(DataDir); } catch { }
        ProfileOptimization.SetProfileRoot(DataDir);
        ProfileOptimization.StartProfile("startup.profile");

        string? arg = e.Args.FirstOrDefault(a => !a.StartsWith("--"));
        if (arg != null) arg = Path.GetFullPath(arg.Trim('"'));

        // La decodifica della prima immagine parte subito, in parallelo con la creazione della finestra.
        var cache = ImageCache.Instance;
        cache.AttachDispatcher(Dispatcher);
        Thumbnails.Instance.AttachDispatcher(Dispatcher);
        if (arg != null && File.Exists(arg) && MediaTypes.KindOf(arg) == MediaKind.Image)
            cache.SetWanted([(arg, 0)]);
        // libVLC si inizializza in background: il primo video partirà senza attese
        VideoSession.Warmup();

        Loc.Init(Settings.Current.Language);
        ApplySystemAccent();

        int logged = 0;
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            // al massimo 20 errori per sessione: un errore ripetuto in un ciclo non deve riempire il disco
            if (logged++ < 20)
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "riffle-errors.log"), DateTime.Now + " " + ex.Exception + "\n\n"); } catch { }
        };

        base.OnStartup(e);
        var w = new MainWindow(arg);
        MainWindow = w;
        w.Show();
    }

    /// <summary>Usa il colore accento scelto dall'utente in Windows.</summary>
    private void ApplySystemAccent()
    {
        if (Native.SystemAccent() is not Color c) return;
        var b = new SolidColorBrush(c); b.Freeze();
        Resources["Accent"] = b;
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        var fg = new SolidColorBrush(lum > 0.55 ? Colors.Black : Colors.White); fg.Freeze();
        Resources["AccentFg"] = fg;
    }
}
