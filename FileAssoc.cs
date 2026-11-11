using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Riffle;

/// <summary>
/// Registra Riffle per l'utente corrente (HKCU, nessun privilegio di amministratore):
/// compare in "Apri con" e in Impostazioni → App predefinite. La scelta finale resta all'utente.
/// </summary>
public static class FileAssoc
{
    private const string ProgId = "Riffle.Media";

    public static void Register()
    {
        string exe = Environment.ProcessPath!;
        string cmd = $"\"{exe}\" \"%1\"";
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            k.SetValue("", Loc.T("Foto o video (Riffle)"));
            k.SetValue("FriendlyTypeName", Loc.T("Foto o video"));
            using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", $"\"{exe}\",0");
            using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", cmd);
        }
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Riffle.exe"))
        {
            app.SetValue("FriendlyAppName", "Riffle");
            using (var c = app.CreateSubKey(@"shell\open\command")) c.SetValue("", cmd);
            using var st = app.CreateSubKey("SupportedTypes");
            foreach (var e in MediaTypes.ImageExt.Concat(MediaTypes.VideoExt)) st.SetValue(e, "");
        }
        using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\Riffle\Capabilities"))
        {
            cap.SetValue("ApplicationName", "Riffle");
            cap.SetValue("ApplicationDescription", Loc.T("Visualizzatore istantaneo di foto e video"));
            using var fa = cap.CreateSubKey("FileAssociations");
            foreach (var e in MediaTypes.ImageExt.Concat(MediaTypes.VideoExt)) fa.SetValue(e, ProgId);
        }
        using (var ra = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            ra.SetValue("Riffle", @"Software\Riffle\Capabilities");
        foreach (var e in MediaTypes.ImageExt.Concat(MediaTypes.VideoExt))
        {
            // alcune chiavi possono essere bloccate da altri programmi: le saltiamo senza interrompere la registrazione
            try
            {
                using var ow = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{e}\OpenWithProgids");
                ow.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
