using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Riffle;

internal static class Native
{
    // ---------- Ordinamento "naturale" come Esplora file ----------
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int StrCmpLogicalW(string a, string b);

    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        public int Compare(string? a, string? b) => StrCmpLogicalW(a ?? "", b ?? "");
    }

    // ---------- Barra del titolo scura ----------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void UseDarkTitleBar(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).EnsureHandle();
            int on = 1;
            DwmSetWindowAttribute(h, 20, ref on, sizeof(int));       // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    /// <summary>
    /// Toglie WS_SYSMENU: così Windows non disegna i propri pulsanti riduci/ingrandisci/chiudi
    /// sopra la barra del titolo personalizzata (li disegna l'app).
    /// </summary>
    public static void HideSystemCaptionButtons(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        long style = GetWindowLongPtr(h, -16).ToInt64();
        const long WS_SYSMENU = 0x00080000;
        if ((style & WS_SYSMENU) != 0) SetWindowLongPtr(h, -16, new IntPtr(style & ~WS_SYSMENU));
    }

    /// <summary>Attiva lo sfondo Mica di Windows 11 (22H2+). false se non disponibile.</summary>
    public static bool TryEnableMica(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).EnsureHandle();
            var src = HwndSource.FromHwnd(h);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            int backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
            return Environment.OSVersion.Version.Build >= 22621 && DwmSetWindowAttribute(h, 38, ref backdrop, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>Colore accento di Windows (versione chiara, adatta al tema scuro).</summary>
    public static System.Windows.Media.Color? SystemAccent()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] pal && pal.Length >= 32)
                return System.Windows.Media.Color.FromRgb(pal[4], pal[5], pal[6]); // "Light 2", quella usata dalle app in tema scuro
            using var d = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (d?.GetValue("AccentColor") is int abgr)
            {
                byte r = (byte)abgr, g = (byte)(abgr >> 8), b = (byte)(abgr >> 16);
                // schiarisce verso il bianco per il tema scuro
                return System.Windows.Media.Color.FromRgb((byte)(r + (255 - r) * 0.45), (byte)(g + (255 - g) * 0.45), (byte)(b + (255 - b) * 0.45));
            }
        }
        catch { }
        return null;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    /// <summary>Area dell'intero monitor della finestra, in pixel fisici.</summary>
    public static Int32Rect MonitorBounds(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(MonitorFromWindow(h, 2), ref mi))
            return new Int32Rect(mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R - mi.rcMonitor.L, mi.rcMonitor.B - mi.rcMonitor.T);
        return new Int32Rect(0, 0, 1920, 1080);
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Spessore del bordo che una finestra massimizzata "sborda" fuori dallo schermo (in DIP).</summary>
    public static double MaximizedOverhang(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            uint dpi = GetDpiForWindow(h);
            int px = GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi); // SM_CXFRAME + SM_CXPADDEDBORDER
            return px * 96.0 / dpi;
        }
        catch { return 8; }
    }

    // ---------- Monitor: risoluzione fisica massima ----------
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);

    public static (int w, int h) LargestMonitorPixels()
    {
        int bw = 0, bh = 0;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(m, ref mi))
                {
                    int w = mi.rcMonitor.R - mi.rcMonitor.L, h = mi.rcMonitor.B - mi.rcMonitor.T;
                    if ((long)w * h > (long)bw * bh) { bw = w; bh = h; }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        if (bw < 640 || bh < 480) { bw = 1920; bh = 1080; }
        return (bw, bh);
    }

    // ---------- Memoria condivisa per i frame video ----------
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attrs, uint protect, uint sizeHigh, uint sizeLow, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(IntPtr hMap, uint access, uint offHigh, uint offLow, UIntPtr bytes);
    [DllImport("kernel32.dll")] public static extern bool UnmapViewOfFile(IntPtr addr);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public const uint PAGE_READWRITE = 0x04, FILE_MAP_ALL_ACCESS = 0xF001F;

    // ---------- Cestino ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public static bool MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    // ---------- Sfondo del desktop ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, string vparam, uint winIni);
    public static bool SetWallpaper(string path) => SystemParametersInfo(0x0014, 0, path, 0x01 | 0x02);

    // ---------- "Apri con..." ----------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwnd, ref OPENASINFO info);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO { public string pcszFile; public string? pcszClass; public int oaifInFlags; }
    public static void OpenWith(Window owner, string path)
    {
        var info = new OPENASINFO { pcszFile = path, oaifInFlags = 0x1 | 0x4 }; // ALLOW_REGISTRATION | EXEC
        SHOpenWithDialog(new WindowInteropHelper(owner).Handle, ref info);
    }

    // ---------- Miniature della shell (cache di Windows) ----------
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    public const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8, SIIGBF_INCACHEONLY = 0x10;

    /// <summary>Deve essere chiamato da un thread STA.</summary>
    public static BitmapSource? ShellThumbnail(string path, int size, int flags)
    {
        IShellItemImageFactory? f = null;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
            if (f.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            try
            {
                var bs = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                // copia in memoria gestita e congela
                var wb = new WriteableBitmap(new FormatConvertedBitmap(bs, System.Windows.Media.PixelFormats.Bgr32, null, 0));
                wb.Freeze();
                return wb;
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
        finally { if (f != null) Marshal.ReleaseComObject(f); }
    }
}
