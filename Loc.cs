using System.Globalization;
using System.Windows.Markup;

namespace Riffle;

/// <summary>
/// Localizzazione minimale: il testo italiano è la chiave, l'inglese la traduzione.
/// "auto" = italiano se Windows è in italiano, altrimenti inglese.
/// </summary>
public static class Loc
{
    public static bool IsItalian { get; private set; }
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

    public static void Init(string? setting)
    {
        IsItalian = setting switch
        {
            "it" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it",
        };
        Culture = IsItalian ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.GetCultureInfo("en-US");
        // le date (es. "11 novembre 2026") seguono la lingua dell'interfaccia
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentCulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
    }

    public static string T(string it) => IsItalian || !En.TryGetValue(it, out var en) ? it : en;
    public static string F(string itFormat, params object?[] args) => string.Format(Culture, T(itFormat), args);

    private static readonly Dictionary<string, string> En = new()
    {
        // generale / schermata iniziale
        ["Foto e video, istantanei."] = "Photos and videos, instantly.",
        ["Apri file…"] = "Open file…",
        ["Apri cartella…"] = "Open folder…",
        ["Riapri {0}"] = "Reopen {0}",
        ["oppure trascina qui foto, video o cartelle"] = "or drop photos, videos or folders here",
        ["{0} di {1}"] = "{0} of {1}",
        ["Caricamento…"] = "Loading…",
        ["File non trovato"] = "File not found",
        ["Impossibile aprire questo file"] = "Can't open this file",
        ["Nessuna foto o video in questa cartella"] = "No photos or videos in this folder",
        ["Premi Spazio per riprodurre"] = "Press Space to play",
        ["Foto e video"] = "Photos and videos",
        ["Tutti i file"] = "All files",
        ["OK"] = "OK",
        ["Annulla"] = "Cancel",

        // barra strumenti e suggerimenti
        ["Precedente (←)"] = "Previous (←)",
        ["Successiva (→)"] = "Next (→)",
        ["Riduci (-)"] = "Zoom out (-)",
        ["Ingrandisci (+)"] = "Zoom in (+)",
        ["Adatta / 100% (0, 1)"] = "Fit / 100% (0, 1)",
        ["Ruota a sinistra (Ctrl+L)"] = "Rotate left (Ctrl+L)",
        ["Ruota a destra (Ctrl+R)"] = "Rotate right (Ctrl+R)",
        ["Modifica: ritaglia, ruota, regola (E)"] = "Edit: crop, rotate, adjust (E)",
        ["Elimina (Canc)"] = "Delete (Del)",
        ["Informazioni (I)"] = "Info (I)",
        ["Striscia miniature (T)"] = "Filmstrip (T)",
        ["Presentazione (F5)"] = "Slideshow (F5)",
        ["Schermo intero (F11)"] = "Full screen (F11)",
        ["Altro"] = "More",
        ["Adatta · {0}%"] = "Fit · {0}%",

        // video
        ["Riproduci/Pausa (Spazio)"] = "Play/Pause (Space)",
        ["Fotogramma successivo (.)"] = "Next frame (.)",
        ["Audio (M)"] = "Audio (M)",
        ["Ripeti (L)"] = "Loop (L)",
        ["Audio e sottotitoli"] = "Audio and subtitles",
        ["Velocità"] = "Speed",
        ["Salva fotogramma come PNG"] = "Save frame as PNG",
        ["Taglia clip: imposta inizio [ e fine ], poi esporta"] = "Trim clip: set start [ and end ], then export",
        ["Audio attivato"] = "Sound on",
        ["Audio disattivato"] = "Sound off",
        ["Ripetizione attiva"] = "Loop on",
        ["Ripetizione disattivata"] = "Loop off",
        ["Volume {0}%"] = "Volume {0}%",
        ["AUDIO"] = "AUDIO",
        ["SOTTOTITOLI"] = "SUBTITLES",
        ["Nessuno"] = "None",
        ["Imposta inizio qui"] = "Set start here",
        ["Imposta fine qui"] = "Set end here",
        ["Esporta clip (veloce, senza ricodifica)"] = "Export clip (fast, no re-encoding)",
        ["Esporta clip (precisa, ricodifica)"] = "Export clip (exact, re-encode)",
        ["Azzera selezione"] = "Clear selection",
        ["Inizio clip: {0}"] = "Clip start: {0}",
        ["Fine clip: {0}"] = "Clip end: {0}",
        ["Clip {0} – {1}"] = "Clip {0} – {1}",
        ["inizio"] = "start",
        ["fine"] = "end",
        ["Selezione non valida"] = "Invalid selection",
        ["Esportazione clip…"] = "Exporting clip…",
        ["Clip salvata: {0}"] = "Clip saved: {0}",
        ["Errore ffmpeg: {0}"] = "ffmpeg error: {0}",
        ["Fotogramma salvato: {0}"] = "Frame saved: {0}",
        ["Impossibile riprodurre questo video"] = "Can't play this video",
        ["Errore video: {0}"] = "Video error: {0}",
        ["_fotogramma_"] = "_frame_",

        // azioni sul file
        ["Mostra in Esplora file"] = "Show in File Explorer",
        ["Apri con…"] = "Open with…",
        ["Copia"] = "Copy",
        ["Copia percorso"] = "Copy path",
        ["Percorso copiato"] = "Path copied",
        ["Copiato negli Appunti"] = "Copied to clipboard",
        ["Impossibile copiare"] = "Couldn't copy",
        ["Rinomina…"] = "Rename…",
        ["Rinomina"] = "Rename",
        ["Nome non valido"] = "Invalid name",
        ["Esiste già un file con questo nome"] = "A file with this name already exists",
        ["Impossibile rinominare: {0}"] = "Couldn't rename: {0}",
        ["Elimina"] = "Delete",
        ["Spostare nel Cestino?"] = "Move to Recycle Bin?",
        ["Spostato nel Cestino"] = "Moved to Recycle Bin",
        ["Impossibile eliminare il file"] = "Couldn't delete the file",
        ["Imposta come sfondo del desktop"] = "Set as desktop background",
        ["Sfondo impostato"] = "Background set",
        ["Impossibile impostare lo sfondo"] = "Couldn't set the background",
        ["Stampa…"] = "Print…",
        ["Rotazione salvata"] = "Rotation saved",
        ["Salvataggio rotazione…"] = "Saving rotation…",
        ["Rotazione solo in visualizzazione: per salvarla nel file serve ffmpeg"] = "Rotation is view-only: saving it to the file requires ffmpeg",
        ["Rotazione solo in visualizzazione: si può salvare solo nei file MP4 e MOV"] = "Rotation is view-only: it can only be saved to MP4 and MOV files",
        ["Impossibile salvare la rotazione: {0}"] = "Couldn't save the rotation: {0}",
        ["Rotazione solo in visualizzazione: questo formato non si può sovrascrivere (usa Modifica → Salva una copia)"] =
            "Rotation is view-only: this format can't be overwritten (use Edit → Save a copy)",
        ["Errore: {0}"] = "Error: {0}",

        // menu
        ["Ordina per"] = "Sort by",
        ["Nome"] = "Name",
        ["Data di modifica"] = "Date modified",
        ["Dimensione"] = "Size",
        ["Tipo"] = "Type",
        ["Decrescente"] = "Descending",
        ["Impostazioni"] = "Settings",
        ["La rotellina del mouse fa zoom (invece di scorrere)"] = "Mouse wheel zooms (instead of browsing)",
        ["Avvia i video automaticamente"] = "Play videos automatically",
        ["Ripeti i video"] = "Loop videos",
        ["Chiedi conferma prima di eliminare"] = "Confirm before deleting",
        ["Sfondo nero"] = "Black background",
        ["Durata presentazione"] = "Slideshow interval",
        ["{0} secondi"] = "{0} seconds",
        ["Lingua"] = "Language",
        ["Automatica (come Windows)"] = "Automatic (same as Windows)",
        ["Registra Riffle nelle app predefinite di Windows…"] = "Register Riffle in Windows default apps…",
        ["Registrata. Scegli Riffle come app predefinita nelle Impostazioni."] = "Registered. Pick Riffle as the default app in Settings.",
        ["Scorciatoie da tastiera"] = "Keyboard shortcuts",
        ["Informazioni su Riffle"] = "About Riffle",
        ["Creato da {0}"] = "Created by {0}",
        ["Licenza CC BY-NC-ND 4.0: uso libero non commerciale, citando l'autore; vietato distribuire versioni modificate."] =
            "Licensed under CC BY-NC-ND 4.0: free for non-commercial use with credit to the author; modified versions may not be distributed.",
        ["Componenti di terze parti"] = "Third-party components",
        ["Presentazione · {0} s · Esc per uscire"] = "Slideshow · {0} s · Esc to exit",

        // scorciatoie
        ["SHORTCUTS"] =
            "← →  Previous / next   ·   Wheel  Browse   ·   Ctrl+wheel  Zoom\n" +
            "Home / End  First / last   ·   + −  Zoom   ·   0  Fit   ·   1  100%   ·   Double-click  Zoom\n" +
            "F / F11 / Enter  Full screen   ·   F5  Slideshow   ·   Esc  Exit\n" +
            "Ctrl+R / Ctrl+L  Rotate   ·   E  Edit   ·   I  Info   ·   T  Filmstrip\n" +
            "Del  Recycle Bin (Shift+Del without confirmation)   ·   F2  Rename   ·   Ctrl+C  Copy\n\n" +
            "VIDEO:  Space  Play/Pause   ·   Shift+← →  ±5 s   ·   Ctrl+← →  ±30 s\n" +
            "↑ ↓  Volume   ·   M  Mute   ·   L  Loop   ·   .  Next frame   ·   [ ]  Clip start/end",

        // informazioni
        ["Informazioni"] = "Info",
        ["Cartella"] = "Folder",
        ["Risoluzione"] = "Resolution",
        ["Dimensione file"] = "File size",
        ["Modificato"] = "Modified",
        ["Durata"] = "Duration",
        ["Video"] = "Video",
        ["Audio"] = "Audio",
        ["Sottotitoli"] = "Subtitles",
        ["Bitrate"] = "Bitrate",
        ["can."] = "ch.",
        ["Formato"] = "Format",
        ["Data scatto"] = "Date taken",
        ["Fotocamera"] = "Camera",
        ["Obiettivo"] = "Lens",
        ["Scatto"] = "Exposure",
        ["Flash"] = "Flash",
        ["Sì"] = "Yes",
        ["No"] = "No",
        ["Posizione"] = "Location",
        ["Software"] = "Software",

        // modifica
        ["Modifica"] = "Edit",
        ["RITAGLIA E RUOTA"] = "CROP AND ROTATE",
        ["REGOLAZIONI"] = "ADJUSTMENTS",
        ["Ruota a sinistra"] = "Rotate left",
        ["Ruota a destra"] = "Rotate right",
        ["Rifletti orizzontalmente"] = "Flip horizontally",
        ["Rifletti verticalmente"] = "Flip vertically",
        ["Libero"] = "Free",
        ["Originale"] = "Original",
        ["Luminosità"] = "Brightness",
        ["Esposizione"] = "Exposure",
        ["Contrasto"] = "Contrast",
        ["Saturazione"] = "Saturation",
        ["Calore"] = "Warmth",
        ["Bianco e nero"] = "Black and white",
        ["Ripristina tutto"] = "Reset all",
        ["Salva una copia"] = "Save a copy",
        ["Sovrascrivi"] = "Overwrite",
        ["Sostituisce il file originale"] = "Replaces the original file",
        ["Formato non sovrascrivibile: salva una copia JPEG"] = "This format can't be overwritten: save a JPEG copy",
        ["Scartare le modifiche?"] = "Discard changes?",
        ["Le modifiche non salvate andranno perse."] = "Unsaved changes will be lost.",
        ["Scarta"] = "Discard",
        ["Sovrascrivere l'originale?"] = "Overwrite the original?",
        ["Il file originale verrà sostituito con la versione modificata."] = "The original file will be replaced with the edited version.",
        ["Salvataggio…"] = "Saving…",
        ["Modifiche salvate"] = "Changes saved",
        ["Copia salvata: {0}"] = "Copy saved: {0}",
        ["Errore nel salvataggio: {0}"] = "Save failed: {0}",
        ["_modificata"] = "_edited",

        // registrazione file
        ["Foto o video (Riffle)"] = "Photo or video (Riffle)",
        ["Foto o video"] = "Photo or video",
        ["Visualizzatore istantaneo di foto e video"] = "Instant photo and video viewer",
    };
}

/// <summary>Uso in XAML: Text="{local:T 'Apri file…'}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) { Text = text; }
    [ConstructorArgument("text")] public string Text { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
