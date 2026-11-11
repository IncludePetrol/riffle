using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Riffle;

/// <summary>
/// Una riproduzione video con libVLC (legge praticamente qualsiasi formato, senza installare nulla).
/// I fotogrammi vengono scritti da VLC direttamente in memoria condivisa con un InteropBitmap WPF:
/// zero copie extra e nessun problema di sovrapposizione dei controlli.
/// </summary>
public sealed class VideoSession : IDisposable
{
    private static Task<LibVLC>? _vlcTask;
    public static void Warmup()
    {
        _vlcTask ??= Task.Run(() =>
        {
            Core.Initialize();
            return new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--quiet",
                              "--file-caching=200", "--avcodec-hw=any", "--no-stats");
        });
    }
    public static LibVLC Vlc { get { Warmup(); return _vlcTask!.Result; } }

    private readonly Dispatcher _ui;
    private readonly MediaPlayer _mp;
    private readonly Media _media;
    private volatile bool _disposed;
    private int _framePending;

    // callback mantenuti come campi per non essere raccolti dal GC
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private IntPtr _section, _view;
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public int FramePitch { get; private set; }
    public InteropBitmap? Bitmap { get; private set; }
    public string Path { get; }
    public bool Ended { get; private set; }
    public bool HasFirstFrame { get; private set; }

    /// <summary>Nuovo formato video (thread UI): l'immagine da mostrare.</summary>
    public event Action<InteropBitmap>? FormatReady;
    public event Action? FirstFrame;
    public event Action? EndReached;
    public event Action? Error;

    public VideoSession(string path, Dispatcher ui, bool loop, int volume, bool mute, float rate)
    {
        Path = path;
        _ui = ui;
        var vlc = Vlc;
        _media = new Media(vlc, new Uri(path));
        if (loop) _media.AddOption(":input-repeat=65535");
        _mp = new MediaPlayer(_media) { EnableHardwareDecoding = true };
        _formatCb = OnFormat; _cleanupCb = OnCleanup; _lockCb = OnLock; _displayCb = OnDisplay;
        _mp.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _mp.SetVideoCallbacks(_lockCb, null, _displayCb);
        _mp.EndReached += (_, _) => { Ended = true; _ui.BeginInvoke(() => { if (!_disposed) EndReached?.Invoke(); }); };
        _mp.EncounteredError += (_, _) => _ui.BeginInvoke(() => { if (!_disposed) Error?.Invoke(); });
        _mp.Volume = volume;
        _mp.Mute = mute;
        _mp.Play();
        if (Math.Abs(rate - 1f) > 0.01f) _mp.SetRate(rate);
    }

    private static uint Align(uint v, uint a) => (v + a - 1) / a * a;

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        // limita formati enormi (8K) a 4K per tenere fluida la riproduzione
        if (width > 4096) { height = (uint)(height * 4096.0 / width) & ~1u; width = 4096; }
        uint pitch = Align(width * 4, 32), ln = Align(height, 32);
        pitches = pitch; lines = ln;
        uint size = pitch * ln;
        var section = Native.CreateFileMapping(Native.INVALID_HANDLE_VALUE, IntPtr.Zero, Native.PAGE_READWRITE, 0, size, null);
        var view = Native.MapViewOfFile(section, Native.FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)size);
        _section = section; _view = view;
        int w = (int)width, h = (int)height, p = (int)pitch;
        _ui.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (_disposed) return;
            try
            {
                var bmp = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(section, w, h, PixelFormats.Bgr32, p, 0);
                Bitmap = bmp; FrameWidth = w; FrameHeight = h; FramePitch = p;
                FormatReady?.Invoke(bmp);
            }
            catch { }
        });
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        var view = _view; var section = _section;
        _view = IntPtr.Zero; _section = IntPtr.Zero;
        if (view != IntPtr.Zero) Native.UnmapViewOfFile(view);
        // la chiusura dell'handle va fatta dopo che il thread UI ha creato la bitmap (ordine del dispatcher)
        if (section != IntPtr.Zero) _ui.BeginInvoke(DispatcherPriority.Send, () => Native.CloseHandle(section));
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _view);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _framePending, 1) == 0)
        {
            _ui.BeginInvoke(DispatcherPriority.Render, () =>
            {
                Interlocked.Exchange(ref _framePending, 0);
                if (_disposed || Bitmap == null) return;
                Bitmap.Invalidate();
                if (!HasFirstFrame) { HasFirstFrame = true; FirstFrame?.Invoke(); }
            });
        }
    }

    // ----- controlli -----
    public bool IsPlaying => !_disposed && _mp.IsPlaying;
    public long TimeMs => _disposed ? 0 : Math.Max(0, _mp.Time);
    public long LengthMs => _disposed ? 0 : Math.Max(0, _mp.Length);
    public float Rate => _disposed ? 1 : _mp.Rate;

    public void PlayPause()
    {
        if (_disposed) return;
        if (Ended) { Restart(); return; }
        if (_mp.IsPlaying) _mp.SetPause(true); else _mp.SetPause(false);
        if (_mp.State is VLCState.Stopped or VLCState.NothingSpecial) _mp.Play();
    }

    public void Pause() { if (!_disposed && _mp.IsPlaying) _mp.SetPause(true); }

    public void Restart()
    {
        Ended = false;
        var mp = _mp;
        ThreadPool.QueueUserWorkItem(_ => { try { mp.Stop(); if (!_disposed) mp.Play(); } catch { } });
    }

    public void Seek(long ms)
    {
        if (_disposed) return;
        long len = LengthMs;
        if (len > 0) ms = Math.Clamp(ms, 0, len - 100);
        if (Ended)
        {
            Ended = false;
            var mp = _mp;
            ThreadPool.QueueUserWorkItem(_ => { try { mp.Stop(); mp.Play(); mp.Time = ms; } catch { } });
            return;
        }
        _mp.Time = Math.Max(0, ms);
    }

    public void NextFrame() { if (!_disposed) _mp.NextFrame(); }
    public void SetVolume(int v) { if (!_disposed) _mp.Volume = v; }
    public void SetMute(bool m) { if (!_disposed) _mp.Mute = m; }
    public void SetRate(float r) { if (!_disposed) _mp.SetRate(r); }

    public IEnumerable<(int id, string name)> AudioTracks() => _disposed ? [] : _mp.AudioTrackDescription.Select(t => (t.Id, t.Name ?? t.Id.ToString()));
    public IEnumerable<(int id, string name)> SubtitleTracks() => _disposed ? [] : _mp.SpuDescription.Select(t => (t.Id, t.Name ?? t.Id.ToString()));
    public int AudioTrack { get => _disposed ? -1 : _mp.AudioTrack; set { if (!_disposed) _mp.SetAudioTrack(value); } }
    public int SubtitleTrack { get => _disposed ? -1 : _mp.Spu; set { if (!_disposed) _mp.SetSpu(value); } }

    /// <summary>Dimensioni visibili e rapporto d'aspetto dei pixel (thread UI).</summary>
    public (int w, int h, double sar) VisibleSize()
    {
        try
        {
            foreach (var t in _media.Tracks)
            {
                if (t.TrackType != TrackType.Video) continue;
                var v = t.Data.Video;
                double sar = v.SarNum > 0 && v.SarDen > 0 ? (double)v.SarNum / v.SarDen : 1;
                if (sar < 0.25 || sar > 4) sar = 1;
                int w = (int)v.Width, h = (int)v.Height;
                // se VLC ha ridotto il formato (>4K) scala le dimensioni visibili di conseguenza
                if (w > FrameWidth && FrameWidth > 0) { h = (int)Math.Round(h * (double)FrameWidth / w); w = FrameWidth; }
                return (w, h, sar);
            }
        }
        catch { }
        return (FrameWidth, FrameHeight, 1);
    }

    public IEnumerable<(string k, string v)> Info()
    {
        if (_disposed) yield break;
        MediaTrack[] tracks;
        try { tracks = _media.Tracks; } catch { yield break; }
        foreach (var t in tracks)
        {
            string codec = FourCC(t.Codec);
            if (t.TrackType == TrackType.Video)
            {
                var v = t.Data.Video;
                double fps = v.FrameRateDen > 0 ? (double)v.FrameRateNum / v.FrameRateDen : 0;
                yield return (Loc.T("Video"), $"{codec}  {v.Width}×{v.Height}" + (fps > 0 ? $"  {fps:0.##} fps" : ""));
            }
            else if (t.TrackType == TrackType.Audio)
            {
                var a = t.Data.Audio;
                yield return (Loc.T("Audio"), $"{codec}  {a.Rate / 1000.0:0.#} kHz  {a.Channels} {Loc.T("can.")}" + (string.IsNullOrEmpty(t.Language) ? "" : $"  [{t.Language}]"));
            }
            else if (t.TrackType == TrackType.Text)
                yield return (Loc.T("Sottotitoli"), codec + (string.IsNullOrEmpty(t.Language) ? "" : $"  [{t.Language}]"));
        }
        if (t_bitrateHint(out var br)) yield return (Loc.T("Bitrate"), br);
    }

    private bool t_bitrateHint(out string s)
    {
        s = "";
        try
        {
            long len = LengthMs;
            if (len <= 0) return false;
            long size = new FileInfo(Path).Length;
            s = $"{size * 8.0 / (len / 1000.0) / 1_000_000:0.0} Mbit/s";
            return true;
        }
        catch { return false; }
    }

    private static string FourCC(uint c)
    {
        var s = new string(new[] { (char)(c & 0xFF), (char)((c >> 8) & 0xFF), (char)((c >> 16) & 0xFF), (char)((c >> 24) & 0xFF) }).Trim();
        return s.ToLowerInvariant() switch
        {
            "h264" or "avc1" => "H.264", "hevc" or "h265" or "hvc1" => "HEVC", "av01" => "AV1", "vp90" or "vp09" => "VP9", "vp80" => "VP8",
            "mp4a" => "AAC", "opus" => "Opus", "mpga" => "MP3", "a52" or "ac-3" => "AC-3", "eac3" => "E-AC-3", "mp4v" => "MPEG-4", "mpgv" => "MPEG-2",
            "fl32" => "PCM", "s16l" => "PCM", "vorb" => "Vorbis", "dts" => "DTS", "flac" => "FLAC", "wmv3" => "WMV9", "wma2" => "WMA",
            _ => s.ToUpperInvariant(),
        };
    }

    /// <summary>Copia il fotogramma corrente in una bitmap indipendente.</summary>
    public BitmapSource? Snapshot()
    {
        if (Bitmap == null) return null;
        var copy = new WriteableBitmap(Bitmap);
        copy.Freeze();
        return copy;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FormatReady = null; FirstFrame = null; EndReached = null; Error = null;
        var mp = _mp; var media = _media;
        // Stop() è sincrono e può richiedere decine di ms: lo facciamo fuori dal thread UI
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { mp.Stop(); } catch { }
            try { mp.Dispose(); media.Dispose(); } catch { }
        });
    }

    /// <summary>Ferma e attende (necessario prima di eliminare/spostare il file).</summary>
    public void DisposeAndWait()
    {
        if (_disposed) return;
        _disposed = true;
        FormatReady = null; FirstFrame = null; EndReached = null; Error = null;
        var mp = _mp; var media = _media;
        var t = Task.Run(() => { try { mp.Stop(); mp.Dispose(); media.Dispose(); } catch { } });
        // pompa il dispatcher mentre aspettiamo (i callback di VLC usano BeginInvoke, quindi nessun deadlock)
        var frame = new DispatcherFrame();
        t.ContinueWith(_ => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
