using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace Riffle;

public partial class MainWindow : Window
{
    private readonly Settings S = Settings.Current;
    private bool _mica;
    private readonly ImageCache _cache = ImageCache.Instance;

    // elenco
    private List<MediaItem> _items = [];
    private int _index = -1;
    private string? _folder;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _watchDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private int _dir = 1;
    private DateTime _lastNav = DateTime.MinValue;
    private int _listVersion;

    // visualizzazione corrente
    private MediaItem? Cur => _index >= 0 && _index < _items.Count ? _items[_index] : null;
    private DecodedImage? _shown;          // immagine decodificata visualizzata (null se segnaposto/video)
    private string? _shownPath;
    private bool _placeholder;
    private Orient _viewOrient = Orient.None;
    private DecodedImage? _full;           // versione a piena risoluzione dell'immagine corrente
    private CancellationTokenSource? _fullCts;
    private double _elemW = 1, _elemH = 1; // dimensioni dell'elemento immagine (DIP, al 100%)
    private double _scale = 1, _tx, _ty;
    private bool _fit = true;
    private readonly MatrixTransform _xf = new();

    // pan
    private bool _panning;
    private Point _panStart;
    private double _panTx, _panTy;

    // animazioni GIF
    private readonly DispatcherTimer _animTimer = new(DispatcherPriority.Render);
    private int _animFrame;

    // video
    private VideoSession? _video;
    private readonly DispatcherTimer _videoStart = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _seekDragging, _seekUpdating;
    private DateTime _lastSeek;
    private long _clipIn = -1, _clipOut = -1;
    private float _rate = 1f;
    private static string? _ffmpeg;

    // interfaccia
    private DateTime _lastMouseMove = DateTime.Now;
    private bool _fullscreen;
    private WindowState _preFsState;
    private bool _slideshow;
    private readonly DispatcherTimer _slideTimer = new();
    private readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _infoDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };

    // modifica
    private EditState? _edit;
    private Editing.Preview? _preview;
    private bool _previewQueued;
    private double? _aspect; // null = libero
    private readonly Dictionary<string, Slider> _adjSliders = [];

    public MainWindow(string? startPath)
    {
        InitializeComponent();
        Img.RenderTransform = _xf;

        if (!double.IsNaN(S.WinLeft) && S.WinWidth > 300)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = S.WinLeft; Top = S.WinTop; Width = S.WinWidth; Height = S.WinHeight;
            // fuori schermo? torna al centro
            if (Left > SystemParameters.VirtualScreenWidth - 100 || Top > SystemParameters.VirtualScreenHeight - 100 || Left < SystemParameters.VirtualScreenLeft - 200)
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (S.WinMaximized) WindowState = WindowState.Maximized;

        SourceInitialized += (_, _) =>
        {
            Native.UseDarkTitleBar(this);
            _mica = Native.TryEnableMica(this);
            Native.HideSystemCaptionButtons(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            ApplyBackground();
        };
        StateChanged += (_, _) => { UpdateFrameMargin(); MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; };
        Activated += (_, _) => { TitleText.Opacity = AppIcon.Opacity = CaptionButtons.Opacity = 1; };
        Deactivated += (_, _) => { TitleText.Opacity = AppIcon.Opacity = CaptionButtons.Opacity = 0.55; };
        _cache.Decoded += OnDecoded;
        StageHost.SizeChanged += (_, _) => { if (_fit) FitView(); else ClampPan(); ApplyXf(); if (_edit != null) UpdateCropVisual(); };

        _animTimer.Tick += AnimTick;
        _infoDelay.Tick += (_, _) => { _infoDelay.Stop(); if (InfoPanel.Visibility == Visibility.Visible) UpdateInfo(); };
        _videoStart.Tick += (_, _) => { _videoStart.Stop(); StartVideoIfCurrent(); };
        _uiTimer.Tick += UiTick;
        _uiTimer.Start();
        _slideTimer.Tick += (_, _) => { if (_slideshow && Cur?.IsVideo != true) Go(_index + 1, wrap: true); };
        _loadingTimer.Tick += (_, _) => { _loadingTimer.Stop(); if (_placeholder || (_shown == null && Cur?.IsVideo == false)) ShowCenter("", Loc.T("Caricamento…"), onlyIfEmpty: true); };
        _watchDebounce.Tick += (_, _) => { _watchDebounce.Stop(); if (_folder != null) LoadFolder(_folder, Cur?.Path, keepCurrent: true); };

        StageHost.MouseWheel += Stage_Wheel;
        StageHost.MouseLeftButtonDown += Stage_LeftDown;
        StageHost.MouseLeftButtonUp += Stage_LeftUp;
        StageHost.MouseMove += Stage_MouseMove;
        StageHost.MouseRightButtonUp += (_, e) => { if (_edit == null) { ShowMoreMenu(null); e.Handled = true; } };
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { Go(_index - 1); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { Go(_index + 1); e.Handled = true; }
        };
        CropLayer.MouseLeftButtonDown += Crop_Down;
        CropLayer.MouseMove += Crop_Move;
        CropLayer.MouseLeftButtonUp += Crop_Up;
        PreviewKeyDown += OnKey;
        Drop += OnDrop;
        Closing += (_, _) => SaveWindow();
        Closed += (_, _) => { _video?.Dispose(); };

        VolBar.Value = S.Volume;
        UpdateMuteIcon();
        UpdateLoopIcon();
        ApplyBackground();
        BuildEditControls();
        _ffmpeg ??= FindOnPath("ffmpeg.exe");
        ClipBtn.Visibility = _ffmpeg != null ? Visibility.Visible : Visibility.Collapsed;

        if (startPath != null) Open(startPath);
        else ShowEmpty();
    }

    // =====================================================================
    //  APERTURA E ELENCO FILE
    // =====================================================================

    private void ShowEmpty()
    {
        EmptyState.Visibility = Visibility.Visible;
        TitleText.Text = "Riffle"; CounterText.Text = "";
        if (S.LastFolder != null && Directory.Exists(S.LastFolder))
        {
            LastFolderBtn.Content = Loc.F("Riapri {0}", Path.GetFileName(S.LastFolder.TrimEnd('\\')));
            LastFolderBtn.Visibility = Visibility.Visible;
        }
        UpdateCommands();
    }

    public void Open(string path)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        if (Directory.Exists(path)) { LoadFolder(path, null, keepCurrent: false); return; }
        if (!File.Exists(path)) { ShowEmpty(); Toast(Loc.T("File non trovato")); return; }
        var kind = MediaTypes.KindOf(path) ?? MediaKind.Image;
        // mostra subito il file, poi carica il resto della cartella in background
        var item = new MediaItem(path, kind);
        SetItems([item], 0);
        LoadFolder(Path.GetDirectoryName(path)!, path, keepCurrent: true);
    }

    private void LoadFolder(string folder, string? select, bool keepCurrent)
    {
        _folder = folder;
        S.LastFolder = folder;
        int ver = ++_listVersion;
        var sort = S.Sort; bool desc = S.SortDescending;
        Task.Run(() =>
        {
            var list = new List<MediaItem>();
            try
            {
                foreach (var fi in new DirectoryInfo(folder).EnumerateFiles())
                {
                    if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    var k = MediaTypes.KindOf(fi.Name);
                    if (k == null) continue;
                    list.Add(new MediaItem(fi.FullName, k.Value) { Modified = fi.LastWriteTime, Size = fi.Length });
                }
            }
            catch { }
            Comparison<MediaItem> cmp = sort switch
            {
                SortBy.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
                SortBy.Size => (a, b) => a.Size.CompareTo(b.Size),
                SortBy.Type => (a, b) => { int c = string.Compare(Path.GetExtension(a.Name), Path.GetExtension(b.Name), StringComparison.OrdinalIgnoreCase); return c != 0 ? c : Native.StrCmpLogicalW(a.Name, b.Name); },
                _ => (a, b) => Native.StrCmpLogicalW(a.Name, b.Name),
            };
            list.Sort(desc ? (a, b) => cmp(b, a) : cmp);
            return list;
        }).ContinueWith(t =>
        {
            if (ver != _listVersion) return;
            var list = t.Result;
            string? want = select ?? (keepCurrent ? Cur?.Path : null);
            int idx = want == null ? 0 : list.FindIndex(i => string.Equals(i.Path, want, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                if (want != null && keepCurrent && Cur != null && File.Exists(Cur.Path)) { list.Insert(0, Cur); idx = 0; }
                else idx = Math.Clamp(_index, 0, Math.Max(0, list.Count - 1));
            }
            bool sameCurrent = Cur != null && idx < list.Count && string.Equals(list[idx].Path, Cur.Path, StringComparison.OrdinalIgnoreCase);
            if (sameCurrent) list[idx] = Cur!; // conserva l'oggetto (video in riproduzione)
            SetItems(list, idx, reloadView: !sameCurrent);
            Watch(folder);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetItems(List<MediaItem> list, int index, bool reloadView = true)
    {
        _items = list;

        if (list.Count == 0)
        {
            _index = -1;
            ClearView();
            ShowEmpty();
            ShowCenter("", Loc.T("Nessuna foto o video in questa cartella"));
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;
        if (reloadView) ShowIndex(index);
        else { _index = index; UpdateTitle(); UpdateCommands(); UpdateStatus(); Prefetch(); }
    }

    private void Watch(string folder)
    {
        try
        {
            if (_watcher?.Path == folder) return;
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler h = (_, e) =>
            {
                if (e.Name != null && (e.Name.EndsWith(".riffle-tmp") || MediaTypes.KindOf(e.Name) == null)) return;
                Dispatcher.BeginInvoke(() => { _watchDebounce.Stop(); _watchDebounce.Start(); });
            };
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Renamed += (s, e) => h(s, e);
            _watcher.Changed += (_, e) =>
            {
                if (MediaTypes.KindOf(e.Name ?? "") == null) return;
                var p = e.FullPath;
                Dispatcher.BeginInvoke(() => OnFileChanged(p));
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    private readonly HashSet<string> _selfWrites = new(StringComparer.OrdinalIgnoreCase);
    private void OnFileChanged(string path)
    {
        if (_selfWrites.Contains(path)) return;
        _cache.Invalidate(path);
        Thumbnails.Instance.Forget(path);
        if (Cur != null && string.Equals(Cur.Path, path, StringComparison.OrdinalIgnoreCase) && !Cur.IsVideo && _edit == null)
        {
            _shownPath = null;
            Prefetch();
        }
    }

    // =====================================================================
    //  NAVIGAZIONE
    // =====================================================================

    private async void Go(int idx, bool wrap = false)
    {
        if (_items.Count == 0) return;
        if (wrap) idx = ((idx % _items.Count) + _items.Count) % _items.Count;
        if (idx < 0 || idx >= _items.Count || idx == _index) return;
        if (_edit != null)
        {
            if (_edit.IsDirty && !await Confirm(Loc.T("Scartare le modifiche?"), Loc.T("Le modifiche non salvate andranno perse."), Loc.T("Scarta"))) return;
            ExitEdit();
        }
        ShowIndex(idx);
    }

    // diagnostica: RIFFLE_TRACE=1 scrive i tempi di visualizzazione in %TEMP%\riffle-trace.log
    private static readonly bool TraceOn = Environment.GetEnvironmentVariable("RIFFLE_TRACE") == "1";
    private readonly Stopwatch _navWatch = new();
    internal static void TraceLog(string s) { if (TraceOn) try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "riffle-trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}"); } catch { } }

    private void ShowIndex(int idx)
    {
        _navWatch.Restart();
        if (idx != _index && _index >= 0) _dir = idx > _index ? 1 : -1;
        var now = DateTime.Now;
        bool rapid = (now - _lastNav).TotalMilliseconds < 220;
        _lastNav = now;

        StopMedia();
        _index = idx;
        var it = Cur!;
        _viewOrient = Orient.None;
        _fit = true;
        _clipIn = _clipOut = -1;
        UpdateClipMarks();
        HideCenter();

        if (it.IsVideo)
        {
            _shown = null; _shownPath = null;
            ShowPlaceholderFor(it, clearIfNone: true);
            VideoBar.Visibility = Visibility.Visible;
            ResetVideoBar();
            if (S.AutoPlayVideo || _slideshow)
            {
                // tenendo premuta la freccia non avviamo VLC per ogni video attraversato
                _videoStart.Interval = TimeSpan.FromMilliseconds(rapid ? 260 : 1);
                _videoStart.Start();
            }
            else ShowCenter("", Loc.T("Premi Spazio per riprodurre"), onlyIfEmpty: false);
        }
        else
        {
            VideoBar.Visibility = Visibility.Collapsed;
            if (_cache.TryGet(it.Path, out var img)) ShowDecoded(img, it.Path);
            else if (_cache.HasFailed(it.Path)) { ClearView(); ShowCenter("", Loc.T("Impossibile aprire questo file")); }
            else
            {
                // tenendo premuta la freccia: solo miniature già in cache, niente lavoro extra per Windows
                if (rapid) { if (Thumbnails.Instance.TryGetCached(it.Path, out var t) && t != null) ShowThumb(t); }
                else ShowPlaceholderFor(it, clearIfNone: false);
                _loadingTimer.Stop(); _loadingTimer.Start();
            }
        }

        UpdateTitle();
        Prefetch();
        UpdateStatus();
        if (InfoPanel.Visibility == Visibility.Visible) { if (rapid) { _infoDelay.Stop(); _infoDelay.Start(); } else UpdateInfo(); }
        UpdateCommands();
        if (_slideshow) { _slideTimer.Stop(); _slideTimer.Interval = TimeSpan.FromSeconds(S.SlideshowSeconds); _slideTimer.Start(); }
    }

    private void Prefetch()
    {
        if (_items.Count == 0 || _index < 0) return;
        var want = new List<(string, int)>();
        void Add(int i, int prio)
        {
            if (i < 0 || i >= _items.Count) return;
            var it = _items[i];
            if (!it.IsVideo) want.Add((it.Path, prio));
        }
        Add(_index, 0);
        int ahead = 14, behind = 3;
        for (int k = 1; k <= Math.Max(ahead, behind); k++)
        {
            if (k <= ahead) Add(_index + _dir * k, k);
            if (k <= behind) Add(_index - _dir * k, k * 2 + 1);
        }
        _cache.SetWanted(want);
        // anche le miniature dei video vicini (per il segnaposto istantaneo)
        if ((DateTime.Now - _lastNav).TotalMilliseconds < 220) return; // scorrimento veloce: niente miniature video
        for (int k = -2; k <= 4; k++)
        {
            int i = _index + _dir * k;
            if (i >= 0 && i < _items.Count && _items[i].IsVideo && !Thumbnails.Instance.TryGetCached(_items[i].Path, out _))
                Thumbnails.Instance.Request(_items[i].Path, _ => { });
        }
    }

    private void OnDecoded(string path, DecodedImage? img)
    {
        var it = Cur;
        if (it == null || it.IsVideo || !string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase)) return;
        if (_edit != null) return;
        if (img == null) { if (_shownPath != path) { ClearView(); ShowCenter("", Loc.T("Impossibile aprire questo file")); } return; }
        if (_shownPath == path && !_placeholder && _shown == img) return;
        bool keep = _shownPath == path && !_placeholder && !_fit;
        ShowDecoded(img, path, keepView: keep);
    }

    private void ShowDecoded(DecodedImage img, string path, bool keepView = false)
    {
        _loadingTimer.Stop();
        HideCenter();
        if (TraceOn && _shownPath != path) TraceLog($"show {Path.GetFileName(path)} {_navWatch.Elapsed.TotalMilliseconds:0.0} ms ({img.Bitmap.PixelWidth}x{img.Bitmap.PixelHeight} {img.Decoder})");
        _shown = img; _shownPath = path; _placeholder = false;
        _full = img.IsFull ? img : null;
        StopAnim();
        SetSource(_viewOrient.Apply(img.Bitmap), img.FullWidth, img.FullHeight, _viewOrient);
        if (img.Animation != null) { _animFrame = 0; _animTimer.Interval = TimeSpan.FromMilliseconds(img.Animation.DelaysMs[0]); _animTimer.Start(); }
        if (!keepView) _fit = true;
        if (_fit) FitView(); else ClampPan();
        ApplyXf();
        UpdateCommands();
        UpdateStatus();
        if (InfoPanel.Visibility == Visibility.Visible && (DateTime.Now - _lastNav).TotalMilliseconds > 220) UpdateInfo();
    }

    private void SetSource(BitmapSource src, int fullW, int fullH, Orient extra)
    {
        if (extra.SwapsAxes) (fullW, fullH) = (fullH, fullW);
        var dpi = VisualTreeHelper.GetDpi(this);
        _elemW = fullW / dpi.DpiScaleX; _elemH = fullH / dpi.DpiScaleY;
        Img.Width = _elemW; Img.Height = _elemH;
        Img.Clip = null;
        Img.Source = src;
    }

    /// <summary>Video: il buffer può avere righe di padding (es. 1088 invece di 1080) e pixel non quadrati.</summary>
    private void SetVideoSource(VideoSession v, BitmapSource bmp)
    {
        var (visW, visH, sar) = v.VisibleSize();
        if (visW <= 0 || visW > v.FrameWidth) visW = v.FrameWidth;
        if (visH <= 0 || visH > v.FrameHeight) visH = v.FrameHeight;
        var dpi = VisualTreeHelper.GetDpi(this);
        _elemW = visW * sar / dpi.DpiScaleX; _elemH = visH / dpi.DpiScaleY;
        Img.Width = _elemW * v.FrameWidth / visW; Img.Height = _elemH * v.FrameHeight / visH;
        Img.Clip = visW == v.FrameWidth && visH == v.FrameHeight ? null : new RectangleGeometry(new Rect(0, 0, _elemW, _elemH));
        Img.Source = bmp;
    }

    private void ShowPlaceholderFor(MediaItem it, bool clearIfNone)
    {
        _placeholder = true;
        if (Thumbnails.Instance.TryGetCached(it.Path, out var t) && t != null) { ShowThumb(t); return; }
        if (clearIfNone) ClearView(keepPlaceholderFlag: true);
        var path = it.Path;
        Thumbnails.Instance.Request(path, b =>
        {
            if (b == null || Cur?.Path != path || !_placeholder) return;
            if (Cur.IsVideo && _video?.HasFirstFrame == true) return;
            ShowThumb(b);
        }, urgent: true);
    }

    private void ShowThumb(BitmapSource t)
    {
        TraceLog($"thumb {Cur?.Name} {_navWatch.Elapsed.TotalMilliseconds:0.0} ms");
        StopAnim();
        _shown = null; _shownPath = null; _placeholder = true;
        // la miniatura viene allargata alla dimensione di adattamento
        double k = Math.Max(StageHost.ActualWidth / t.PixelWidth, StageHost.ActualHeight / t.PixelHeight);
        var dpi = VisualTreeHelper.GetDpi(this);
        SetSource(t, (int)(t.PixelWidth * k * dpi.DpiScaleX), (int)(t.PixelHeight * k * dpi.DpiScaleY), Orient.None);
        _fit = true; FitView(); ApplyXf();
    }

    private void ClearView(bool keepPlaceholderFlag = false)
    {
        StopAnim();
        Img.Source = null;
        _shown = null; _shownPath = null; _full = null;
        if (!keepPlaceholderFlag) _placeholder = false;
    }

    private void StopMedia()
    {
        _videoStart.Stop();
        _fullCts?.Cancel(); _fullCts = null; _full = null;
        StopAnim();
        if (_video != null) { _video.Dispose(); _video = null; }
    }

    private void StopAnim() => _animTimer.Stop();

    private void AnimTick(object? s, EventArgs e)
    {
        var a = _shown?.Animation;
        if (a == null) { _animTimer.Stop(); return; }
        _animFrame = (_animFrame + 1) % a.Frames.Length;
        Img.Source = _viewOrient.Apply(a.Frames[_animFrame]);
        _animTimer.Interval = TimeSpan.FromMilliseconds(a.DelaysMs[_animFrame]);
    }

    private void UpdateTitle()
    {
        var it = Cur;
        if (it == null) { TitleText.Text = "Riffle"; CounterText.Text = ""; Title = "Riffle"; return; }
        TitleText.Text = it.Name;
        CounterText.Text = _items.Count > 1 ? Loc.F("{0} di {1}", _index + 1, _items.Count) : "";
        Title = it.Name + " — Riffle";
    }

    private void UpdateCommands()
    {
        var it = Cur;
        bool img = it != null && !it.IsVideo;
        bool decoded = img && _shown != null && !_placeholder;
        RotLBtn.IsEnabled = RotRBtn.IsEnabled = decoded && _edit == null;
        EditBtn.IsEnabled = decoded && _shown!.Animation == null && _edit == null;
        ZoomInBtn.IsEnabled = ZoomOutBtn.IsEnabled = ZoomLabel.IsEnabled = (decoded || (it?.IsVideo == true && _video?.Bitmap != null)) && _edit == null;
        DeleteBtn.IsEnabled = it != null && _edit == null;
        SlideBtn.IsEnabled = _items.Count > 0;
        PrevBtn.Visibility = NextBtn.Visibility = _items.Count > 1 && _edit == null ? Visibility.Visible : Visibility.Collapsed;
        // senza file aperto resta solo il menu "Altro"
        foreach (UIElement c in Commands.Children)
            if (c != MoreBtn) c.Visibility = it != null ? Visibility.Visible : Visibility.Collapsed;
        if (!_fullscreen) StatusBar.Visibility = it != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    //  ZOOM E PAN
    // =====================================================================

    private double FitScaleValue(bool allowUpscale)
    {
        double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
        if (_edit != null) { w -= 80; h -= 80; }
        if (w <= 0 || h <= 0 || _elemW <= 0) return 1;
        double s = Math.Min(w / _elemW, h / _elemH);
        return allowUpscale ? s : Math.Min(1, s);
    }

    private void FitView()
    {
        bool up = _placeholder || _edit != null || Cur?.IsVideo == true;
        _scale = FitScaleValue(up);
        _tx = (StageHost.ActualWidth - _elemW * _scale) / 2;
        _ty = (StageHost.ActualHeight - _elemH * _scale) / 2;
    }

    private void ClampPan()
    {
        double vw = StageHost.ActualWidth, vh = StageHost.ActualHeight;
        double iw = _elemW * _scale, ih = _elemH * _scale;
        _tx = iw <= vw ? (vw - iw) / 2 : Math.Clamp(_tx, vw - iw, 0);
        _ty = ih <= vh ? (vh - ih) / 2 : Math.Clamp(_ty, vh - ih, 0);
    }

    private void ApplyXf()
    {
        _xf.Matrix = new Matrix(_scale, 0, 0, _scale, _tx, _ty);
        var dpi = VisualTreeHelper.GetDpi(this);
        double pct = _scale * 100;
        // qualità: interpolazione fine in riduzione, pixel netti oltre il 300%
        double srcPx = (Img.Source as BitmapSource)?.PixelWidth ?? 1;
        double screenPx = _elemW * _scale * dpi.DpiScaleX;
        var mode = screenPx < srcPx * 0.98 ? BitmapScalingMode.HighQuality : pct >= 300 && !_placeholder ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear;
        RenderOptions.SetBitmapScalingMode(Img, mode);
        ZoomLabel.Content = _placeholder ? "—" : $"{Math.Round(pct)}%";
        MaybeLoadFull();
    }

    private void ZoomAt(double factor, Point p)
    {
        if (_placeholder || Img.Source == null || _edit != null) return;
        double fit = FitScaleValue(false);
        double ns = Math.Clamp(_scale * factor, Math.Min(fit, 0.05), 40);
        if (factor < 1 && ns <= fit * 1.001) { _fit = true; FitView(); ApplyXf(); ToastZoom(); return; }
        _tx = p.X - (p.X - _tx) * (ns / _scale);
        _ty = p.Y - (p.Y - _ty) * (ns / _scale);
        _scale = ns;
        _fit = false;
        ClampPan(); ApplyXf(); ToastZoom();
    }

    private void ZoomTo(double scale, Point? at = null)
    {
        var p = at ?? new Point(StageHost.ActualWidth / 2, StageHost.ActualHeight / 2);
        ZoomAt(scale / _scale, p);
    }

    private void ToggleFitActual(Point? at = null)
    {
        if (_fit || Math.Abs(_scale - 1) > 0.01) { _fit = false; ZoomTo(1, at); if (Math.Abs(FitScaleValue(false) - 1) < 0.001) { _fit = false; ZoomTo(2, at); } }
        else { _fit = true; FitView(); ApplyXf(); ToastZoom(); }
    }

    private void ToastZoom() => Toast(_fit ? Loc.F("Adatta · {0}%", Math.Round(_scale * 100)) : $"{Math.Round(_scale * 100)}%", 700);

    /// <summary>Se l'utente ingrandisce oltre la risoluzione in cache, carica l'originale a piena risoluzione.</summary>
    private void MaybeLoadFull()
    {
        if (_shown == null || _placeholder || _shown.IsFull || _full != null || _fullCts != null || _edit != null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double needPx = _elemW * _scale * dpi.DpiScaleX;
        double havePx = _viewOrient.SwapsAxes ? _shown.Bitmap.PixelHeight : _shown.Bitmap.PixelWidth;
        if (needPx <= havePx * 1.05) return;
        var path = _shownPath!;
        var cts = _fullCts = new CancellationTokenSource();
        Task.Run(() => ImageDecoder.Decode(path, 0, 0, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (_fullCts == cts) _fullCts = null;
            if (t.IsFaulted || t.IsCanceled || cts.IsCancellationRequested) return;
            if (_shownPath != path || _placeholder || _edit != null) return;
            _full = t.Result;
            Img.Source = _viewOrient.Apply(_full.Bitmap);
            ApplyXf();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // =====================================================================
    //  MOUSE
    // =====================================================================

    private void Stage_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_edit != null) return;
        bool zoom = (Keyboard.Modifiers & ModifierKeys.Control) != 0 || (S.WheelZooms && Cur?.IsVideo != true);
        if (zoom) ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(StageHost));
        else Go(_index + (e.Delta < 0 ? 1 : -1));
        e.Handled = true;
    }

    private void Stage_LeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_edit != null || IsOverControl(e.OriginalSource)) return;
        if (_slideshow) { StopSlideshow(); return; }
        var it = Cur;
        if (e.ClickCount == 2)
        {
            if (it?.IsVideo == true) ToggleFullscreen();
            else ToggleFitActual(e.GetPosition(StageHost));
            e.Handled = true;
            return;
        }
        if (!_fit && !_placeholder)
        {
            _panning = true;
            _panStart = e.GetPosition(StageHost);
            _panTx = _tx; _panTy = _ty;
            StageHost.CaptureMouse();
            StageHost.Cursor = Cursors.SizeAll;
        }
        else if (it?.IsVideo == true) TogglePlay();
    }

    private void Stage_LeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        StageHost.ReleaseMouseCapture();
        StageHost.Cursor = null;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(StageHost);
        if (_panning)
        {
            _tx = _panTx + (p.X - _panStart.X);
            _ty = _panTy + (p.Y - _panStart.Y);
            ClampPan(); ApplyXf();
            return;
        }
        if ((DateTime.Now - _lastMouseMove).TotalMilliseconds > 40) { _lastMouseMove = DateTime.Now; ShowChrome(); }
        double w = StageHost.ActualWidth;
        PrevBtn.Opacity = p.X < 120 ? 1 : 0;
        NextBtn.Opacity = p.X > w - 120 ? 1 : 0;
    }

    private bool IsOverControl(object src)
    {
        for (var d = src as DependencyObject; d != null && d != StageHost; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase or Slider or Thumb || d == VideoBar) return true;
        return false;
    }

    // =====================================================================
    //  TASTIERA
    // =====================================================================

    private async void OnKey(object sender, KeyEventArgs e)
    {
        if (DialogLayer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Enter) { CloseDialog(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseDialog(false); e.Handled = true; }
            return;
        }
        if (BusyLayer.Visibility == Visibility.Visible) { e.Handled = true; return; }
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        bool video = Cur?.IsVideo == true && _video != null;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.F4 && (mods & ModifierKeys.Alt) != 0) { Close(); return; }
        switch (key)
        {
            case Key.Right when video && (shift || ctrl): SeekRel(ctrl ? 30000 : 5000); break;
            case Key.Left when video && (shift || ctrl): SeekRel(ctrl ? -30000 : -5000); break;
            case Key.Right or Key.PageDown or Key.Down when !(key == Key.Down && video): Go(_index + 1, wrap: _slideshow); break;
            case Key.Left or Key.PageUp or Key.Up when !(key == Key.Up && video): Go(_index - 1); break;
            case Key.Up when video: VolBar.Value = Math.Min(100, VolBar.Value + 5); Toast(Loc.F("Volume {0}%", Math.Round(VolBar.Value))); break;
            case Key.Down when video: VolBar.Value = Math.Max(0, VolBar.Value - 5); Toast(Loc.F("Volume {0}%", Math.Round(VolBar.Value))); break;
            case Key.Home: Go(0); break;
            case Key.End: Go(_items.Count - 1); break;
            case Key.Space when Cur?.IsVideo == true: TogglePlay(); break;
            case Key.Space: Go(_index + 1); break;
            case Key.F11 or Key.F: ToggleFullscreen(); break;
            case Key.Enter when !ctrl: ToggleFullscreen(); break;
            case Key.Escape:
                if (_slideshow) StopSlideshow();
                else if (_edit != null) EditCancel_Click(this, new RoutedEventArgs());
                else if (_fullscreen) ToggleFullscreen();
                break;
            case Key.OemPlus or Key.Add: ZoomAt(1.25, Center()); break;
            case Key.OemMinus or Key.Subtract: ZoomAt(1 / 1.25, Center()); break;
            case Key.D0 or Key.NumPad0: if (!_placeholder) { _fit = true; FitView(); ApplyXf(); ToastZoom(); } break;
            case Key.D1 or Key.NumPad1: if (!_placeholder) { _fit = false; ZoomTo(1); } break;
            case Key.R when ctrl: await RotateAndSave(90); break;
            case Key.L when ctrl: await RotateAndSave(-90); break;
            case Key.Delete: await DeleteCurrent(!shift && S.ConfirmDelete); break;
            case Key.E when !ctrl: Edit_Click(this, new RoutedEventArgs()); break;
            case Key.E when ctrl: ShowInExplorer(); break;
            case Key.I: Info_Click(this, new RoutedEventArgs()); break;
            case Key.F5: Slideshow_Click(this, new RoutedEventArgs()); break;
            case Key.O when ctrl && shift: OpenFolder_Click(this, new RoutedEventArgs()); break;
            case Key.O when ctrl: OpenFile_Click(this, new RoutedEventArgs()); break;
            case Key.C when ctrl && shift: if (Cur != null) { Clipboard.SetText(Cur.Path); Toast(Loc.T("Percorso copiato")); } break;
            case Key.C when ctrl: CopyCurrent(); break;
            case Key.P when ctrl: Print(); break;
            case Key.F2: await Rename(); break;
            case Key.M: Mute_Click(this, new RoutedEventArgs()); break;
            case Key.L when !ctrl: Loop_Click(this, new RoutedEventArgs()); break;
            case Key.OemPeriod when video: FrameStep_Click(this, new RoutedEventArgs()); break;
            case Key.OemOpenBrackets when video: SetClipIn(); break;
            case Key.Oem6 when video: SetClipOut(); break;
            default: e.Handled = false; break;
        }
    }

    private Point Center() => new(StageHost.ActualWidth / 2, StageHost.ActualHeight / 2);

    // =====================================================================
    //  VIDEO
    // =====================================================================

    private void StartVideoIfCurrent()
    {
        var it = Cur;
        if (it == null || !it.IsVideo || _video != null) return;
        HideCenter();
        try
        {
            var v = new VideoSession(it.Path, Dispatcher, S.LoopVideo && !_slideshow, (int)VolBar.Value, S.Mute, _rate);
            _video = v;
            v.FormatReady += bmp =>
            {
                if (_video != v) return;
                if (v.HasFirstFrame) ShowVideoFrame(v, bmp);
            };
            v.FirstFrame += () => { if (_video == v && v.Bitmap != null) ShowVideoFrame(v, v.Bitmap); };
            v.EndReached += () =>
            {
                if (_video != v) return;
                if (_slideshow) Go(_index + 1, wrap: true);
                else if (S.LoopVideo) v.Restart();
            };
            v.Error += () => { if (_video == v) ShowCenter("", Loc.T("Impossibile riprodurre questo video")); };
        }
        catch (Exception ex)
        {
            ShowCenter("", Loc.F("Errore video: {0}", ex.Message));
        }
    }

    private void ShowVideoFrame(VideoSession v, BitmapSource bmp)
    {
        if (_placeholder) TraceLog($"video first frame {Cur?.Name} {_navWatch.Elapsed.TotalMilliseconds:0.0} ms ({v.FrameWidth}x{v.FrameHeight})");
        bool first = _placeholder || Img.Source != bmp;
        _placeholder = false; _shown = null; _shownPath = null;
        HideCenter();
        bool keep = !_fit && Img.Source is System.Windows.Interop.InteropBitmap;
        SetVideoSource(v, bmp);
        if (!keep) { _fit = true; FitView(); }
        ClampPan();
        ApplyXf();
        if (first) { UpdateCommands(); UpdateStatus(); }
        if (InfoPanel.Visibility == Visibility.Visible) UpdateInfo();
    }

    private void TogglePlay()
    {
        if (Cur?.IsVideo != true) return;
        if (_video == null) { StartVideoIfCurrent(); return; }
        _video.PlayPause();
        UiTick(null, EventArgs.Empty);
    }

    private void SeekRel(long ms)
    {
        if (_video == null) return;
        _video.Seek(_video.TimeMs + ms);
        Toast((ms > 0 ? "+" : "−") + Math.Abs(ms / 1000) + " s", 600);
    }

    private void ResetVideoBar()
    {
        _seekUpdating = true; SeekBar.Value = 0; _seekUpdating = false;
        TimeText.Text = "0:00"; DurText.Text = "0:00";
        PlayBtn.Content = "";
        SpeedBtn.Content = FormatRate(_rate);
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void UiTick(object? s, EventArgs e)
    {
        var v = _video;
        if (v != null && Cur?.IsVideo == true)
        {
            long len = v.LengthMs, t = v.TimeMs;
            if (!_seekDragging && len > 0) { _seekUpdating = true; SeekBar.Value = t * 1000.0 / len; _seekUpdating = false; }
            TimeText.Text = Fmt(_seekDragging && len > 0 ? (long)(SeekBar.Value / 1000 * len) : t);
            DurText.Text = Fmt(len);
            if (len > 0 && StDurBox.Visibility != Visibility.Visible) UpdateStatus();
            PlayBtn.Content = v.IsPlaying ? "" : "";
        }
        else if (Cur?.IsVideo == true) PlayBtn.Content = "";

        // nasconde i controlli quando il mouse è fermo
        bool idle = (DateTime.Now - _lastMouseMove).TotalSeconds > 2.5 && !_seekDragging && !VideoBar.IsMouseOver && !Toolbar.IsMouseOver;
        bool playing = v?.IsPlaying == true;
        if (idle && (_fullscreen || playing) && _edit == null && DialogLayer.Visibility != Visibility.Visible) HideChrome();
        else if (_chromeHidden && !_fullscreen && !playing) ShowChrome();
    }

    private bool _chromeHidden;
    private void HideChrome()
    {
        if (_chromeHidden) return;
        _chromeHidden = true;
        Fade(VideoBar, 0);
        if (_fullscreen) { Fade(Toolbar, 0); Cursor = Cursors.None; }
        PrevBtn.Opacity = NextBtn.Opacity = 0;
    }

    private void ShowChrome()
    {
        if (!_chromeHidden) return;
        _chromeHidden = false;
        Cursor = null;
        Fade(VideoBar, 1);
        if (_fullscreen && !_slideshow) Fade(Toolbar, 1);
    }

    private static void Fade(UIElement el, double to)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 120 : 300)));
        el.IsHitTestVisible = to > 0;
    }

    private void SeekBar_Down(object sender, MouseButtonEventArgs e) { _seekDragging = true; }
    private void SeekBar_Up(object sender, MouseButtonEventArgs e)
    {
        _seekDragging = false;
        if (_video != null) _video.Seek((long)(SeekBar.Value / 1000 * _video.LengthMs));
    }
    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seekUpdating || _video == null) return;
        // scrubbing in tempo reale, limitato a ~20 seek al secondo
        if ((DateTime.Now - _lastSeek).TotalMilliseconds < 50 && _seekDragging) return;
        _lastSeek = DateTime.Now;
        _video.Seek((long)(SeekBar.Value / 1000 * _video.LengthMs));
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void FrameStep_Click(object sender, RoutedEventArgs e) { _video?.NextFrame(); }

    private void VolBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        S.Volume = (int)VolBar.Value;
        if (S.Mute && e.NewValue > e.OldValue) { S.Mute = false; _video?.SetMute(false); UpdateMuteIcon(); }
        _video?.SetVolume(S.Volume);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        S.Mute = !S.Mute;
        _video?.SetMute(S.Mute);
        UpdateMuteIcon();
        Toast(S.Mute ? Loc.T("Audio disattivato") : Loc.T("Audio attivato"), 700);
    }
    private void UpdateMuteIcon() => MuteBtn.Content = S.Mute ? "" : "";

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        S.LoopVideo = !S.LoopVideo;
        UpdateLoopIcon();
        Toast(S.LoopVideo ? Loc.T("Ripetizione attiva") : Loc.T("Ripetizione disattivata"), 700);
    }
    private void UpdateLoopIcon() => LoopBtn.Foreground = S.LoopVideo ? (Brush)FindResource("Accent") : (Brush)FindResource("Fg");

    private static string FormatRate(float r) => (r % 1 == 0 ? r.ToString("0") : r.ToString("0.##")) + "×";

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        foreach (var r in new[] { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f })
        {
            var mi = new MenuItem { Header = FormatRate(r), IsChecked = Math.Abs(r - _rate) < 0.01 };
            mi.Click += (_, _) => { _rate = r; _video?.SetRate(r); SpeedBtn.Content = FormatRate(r); };
            m.Items.Add(mi);
        }
        OpenMenu(m, (FrameworkElement)sender);
    }

    private void Tracks_Click(object sender, RoutedEventArgs e)
    {
        if (_video == null) return;
        var m = new ContextMenu();
        var audio = _video.AudioTracks().ToList();
        var subs = _video.SubtitleTracks().ToList();
        m.Items.Add(new MenuItem { Header = Loc.T("AUDIO"), IsEnabled = false });
        int at = _video.AudioTrack;
        foreach (var (id, name) in audio)
        {
            var mi = new MenuItem { Header = name, IsChecked = id == at };
            int i = id; mi.Click += (_, _) => _video.AudioTrack = i;
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = Loc.T("SOTTOTITOLI"), IsEnabled = false });
        int st = _video.SubtitleTrack;
        if (subs.Count == 0) m.Items.Add(new MenuItem { Header = Loc.T("Nessuno"), IsEnabled = false });
        foreach (var (id, name) in subs)
        {
            var mi = new MenuItem { Header = name, IsChecked = id == st };
            int i = id; mi.Click += (_, _) => _video.SubtitleTrack = i;
            m.Items.Add(mi);
        }
        OpenMenu(m, (FrameworkElement)sender);
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        var v = _video; var it = Cur;
        if (v == null || it == null) return;
        var bmp = v.Snapshot();
        if (bmp == null) return;
        long t = v.TimeMs;
        var dest = Editing.UniqueName(it.Path, Loc.T("_fotogramma_") + $"{t / 60000:00}m{t / 1000 % 60:00}s", ".png");
        try { Editing.SavePng(bmp, dest); Toast(Loc.F("Fotogramma salvato: {0}", Path.GetFileName(dest)), 2500); }
        catch (Exception ex) { Toast(Loc.F("Errore: {0}", ex.Message), 3000); }
    }

    private void Clip_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        var a = new MenuItem { Header = Loc.T("Imposta inizio qui"), InputGestureText = "[" }; a.Click += (_, _) => SetClipIn(); m.Items.Add(a);
        var b = new MenuItem { Header = Loc.T("Imposta fine qui"), InputGestureText = "]" }; b.Click += (_, _) => SetClipOut(); m.Items.Add(b);
        m.Items.Add(new Separator());
        var x = new MenuItem { Header = Loc.T("Esporta clip (veloce, senza ricodifica)"), IsEnabled = _clipIn >= 0 || _clipOut >= 0 }; x.Click += async (_, _) => await ExportClip(false); m.Items.Add(x);
        var y = new MenuItem { Header = Loc.T("Esporta clip (precisa, ricodifica)"), IsEnabled = _clipIn >= 0 || _clipOut >= 0 }; y.Click += async (_, _) => await ExportClip(true); m.Items.Add(y);
        var z = new MenuItem { Header = Loc.T("Azzera selezione"), IsEnabled = _clipIn >= 0 || _clipOut >= 0 }; z.Click += (_, _) => { _clipIn = _clipOut = -1; UpdateClipMarks(); }; m.Items.Add(z);
        OpenMenu(m, (FrameworkElement)sender);
    }

    private void SetClipIn() { if (_video == null) return; _clipIn = _video.TimeMs; if (_clipOut >= 0 && _clipOut <= _clipIn) _clipOut = -1; UpdateClipMarks(); Toast(Loc.F("Inizio clip: {0}", Fmt(_clipIn))); }
    private void SetClipOut() { if (_video == null) return; _clipOut = _video.TimeMs; if (_clipIn >= _clipOut) _clipIn = -1; UpdateClipMarks(); Toast(Loc.F("Fine clip: {0}", Fmt(_clipOut))); }

    private void UpdateClipMarks()
    {
        ClipMarks.Children.Clear();
        if (_clipIn < 0 && _clipOut < 0) { ClipText.Text = ""; return; }
        long len = _video?.LengthMs ?? 0;
        ClipText.Text = Loc.F("Clip {0} – {1}", _clipIn >= 0 ? Fmt(_clipIn) : Loc.T("inizio"), _clipOut >= 0 ? Fmt(_clipOut) : Loc.T("fine"));
        if (len <= 0) return;
        double w = SeekBar.ActualWidth - 14;
        double a = (_clipIn >= 0 ? _clipIn : 0) / (double)len * w + 7, b = (_clipOut >= 0 ? _clipOut : len) / (double)len * w + 7;
        var r = new Rectangle { Width = Math.Max(2, b - a), Height = 8, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xC8, 0x3D)), RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(r, a); Canvas.SetTop(r, -2);
        ClipMarks.Children.Add(r);
    }

    private async Task ExportClip(bool precise)
    {
        var it = Cur;
        if (it == null || _ffmpeg == null || _video == null) return;
        long len = _video.LengthMs;
        long a = Math.Max(0, _clipIn), b = _clipOut >= 0 ? _clipOut : len;
        if (b <= a) { Toast(Loc.T("Selezione non valida")); return; }
        var ext = Path.GetExtension(it.Path);
        var dest = Editing.UniqueName(it.Path, "_clip", precise ? ".mp4" : ext);
        string ss = (a / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string dur = ((b - a) / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string codec = precise ? "-c:v libx264 -crf 18 -preset veryfast -c:a aac -b:a 192k" : "-c copy -map 0 -avoid_negative_ts make_zero";
        var psi = new ProcessStartInfo(_ffmpeg, $"-hide_banner -loglevel error -y -ss {ss} -i \"{it.Path}\" -t {dur} {codec} \"{dest}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
        ShowBusy(Loc.T("Esportazione clip…"));
        try
        {
            var (code, err) = await Task.Run(() =>
            {
                using var p = Process.Start(psi)!;
                var errText = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, errText);
            });
            HideBusy();
            if (code == 0) Toast(Loc.F("Clip salvata: {0}", Path.GetFileName(dest)), 3000);
            else { try { File.Delete(dest); } catch { } Toast(Loc.F("Errore ffmpeg: {0}", err.Split('\n').FirstOrDefault()), 4000); }
        }
        catch (Exception ex) { HideBusy(); Toast(Loc.F("Errore: {0}", ex.Message), 3000); }
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    // =====================================================================
    //  AZIONI SUL FILE
    // =====================================================================

    private async Task RotateAndSave(int deg)
    {
        var it = Cur;
        if (it == null || it.IsVideo || _shown == null || _placeholder || _edit != null) return;
        var extra = Orient.None.RotateCw(deg);
        _viewOrient = _viewOrient.Then(extra);
        var baseImg = _full ?? _shown;
        SetSource(_viewOrient.Apply(baseImg.Bitmap), baseImg.FullWidth, baseImg.FullHeight, _viewOrient);
        _fit = true; FitView(); ApplyXf();

        bool writable = MediaTypes.WritableExt.Contains(Path.GetExtension(it.Path)) && _shown.Animation == null;
        if (!writable) { Toast(Loc.T("Rotazione solo in visualizzazione: questo formato non si può sovrascrivere (usa Modifica → Salva una copia)"), 3500); return; }

        var path = it.Path;
        _selfWrites.Add(path);
        try
        {
            bool lossless = await Task.Run(() =>
            {
                lock (_saveLock)
                {
                    if (Editing.TryLosslessRotate(path, extra)) return true;
                    Editing.Save(path, path, new EditState { Orient = extra });
                    return false;
                }
            });
            // il file ora contiene la rotazione: la spostiamo dalla vista ai pixel in cache (niente ridecodifica)
            if (Cur?.Path == path && _shownPath == path && _shown != null)
            {
                var src = _shown;
                var rotated = Rotated(src, extra);
                _full = _full == null ? null : _full == src ? rotated : Rotated(_full, extra);
                _shown = rotated;
                _cache.Put(path, rotated);
                _viewOrient = _viewOrient.RotateCw(-extra.Rot);
                var b = _full ?? _shown;
                SetSource(_viewOrient.Apply(b.Bitmap), b.FullWidth, b.FullHeight, _viewOrient);
                if (_fit) FitView(); else ClampPan();
                ApplyXf();
            }
            else _cache.Invalidate(path);
            Thumbnails.Instance.Forget(path);
            if (!lossless) Toast(Loc.T("Rotazione salvata"));
            if (InfoPanel.Visibility == Visibility.Visible && Cur?.Path == path) UpdateInfo();
        }
        catch (Exception ex) { Toast(Loc.F("Impossibile salvare la rotazione: {0}", ex.Message), 3500); }
        finally { _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.BeginInvoke(() => _selfWrites.Remove(path))); }
    }

    private static DecodedImage Rotated(DecodedImage d, Orient o) => new()
    {
        Bitmap = o.Apply(d.Bitmap), FullWidth = o.SwapsAxes ? d.FullHeight : d.FullWidth, FullHeight = o.SwapsAxes ? d.FullWidth : d.FullHeight,
        IsFull = d.IsFull, Bytes = d.Bytes, Decoder = d.Decoder,
    };
    private static readonly object _saveLock = new();

    private async Task DeleteCurrent(bool confirm)
    {
        var it = Cur;
        if (it == null || _edit != null) return;
        if (confirm && !await Confirm(Loc.T("Spostare nel Cestino?"), it.Name, Loc.T("Elimina"))) return;
        if (_video != null) { _video.DisposeAndWait(); _video = null; }
        bool ok = await Task.Run(() => Native.MoveToRecycleBin(it.Path));
        if (!ok || File.Exists(it.Path)) { Toast(Loc.T("Impossibile eliminare il file"), 2500); if (it.IsVideo) StartVideoIfCurrent(); return; }
        _cache.Invalidate(it.Path);
        int i = _index;
        _items.RemoveAt(i);
        _index = -1;
        Toast(Loc.T("Spostato nel Cestino"), 1500);
        if (_items.Count == 0) { ClearView(); VideoBar.Visibility = Visibility.Collapsed; ShowCenter("", Loc.T("Nessuna foto o video in questa cartella")); UpdateTitle(); UpdateCommands(); return; }
        if (_dir < 0 && i > 0) i--;
        ShowIndex(Math.Min(i, _items.Count - 1));
    }

    private async Task Rename()
    {
        var it = Cur;
        if (it == null || _edit != null) return;
        var name = await Prompt(Loc.T("Rinomina"), Path.GetFileNameWithoutExtension(it.Name));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Toast(Loc.T("Nome non valido")); return; }
        var dest = Path.Combine(Path.GetDirectoryName(it.Path)!, name + Path.GetExtension(it.Path));
        if (string.Equals(dest, it.Path, StringComparison.Ordinal)) return;
        if (File.Exists(dest) && !string.Equals(dest, it.Path, StringComparison.OrdinalIgnoreCase)) { Toast(Loc.T("Esiste già un file con questo nome")); return; }
        bool wasVideo = _video != null;
        if (_video != null) { _video.DisposeAndWait(); _video = null; }
        try
        {
            File.Move(it.Path, dest);
            var ni = new MediaItem(dest, it.Kind) { Modified = it.Modified, Size = it.Size };
            _items[_index] = ni;
            if (_cache.TryGet(it.Path, out var img)) { _shownPath = dest; }
            _cache.Invalidate(it.Path);
            UpdateTitle(); UpdateStatus(); Prefetch();
            if (InfoPanel.Visibility == Visibility.Visible) UpdateInfo();
            if (wasVideo) StartVideoIfCurrent();
        }
        catch (Exception ex) { Toast(Loc.F("Impossibile rinominare: {0}", ex.Message), 3000); if (wasVideo) StartVideoIfCurrent(); }
    }

    private void CopyCurrent()
    {
        var it = Cur;
        if (it == null) return;
        try
        {
            var data = new DataObject();
            var files = new System.Collections.Specialized.StringCollection { it.Path };
            data.SetFileDropList(files);
            var bmp = it.IsVideo ? _video?.Snapshot() : (_full ?? _shown)?.Bitmap;
            if (bmp != null) data.SetImage(_viewOrient.Apply(bmp));
            Clipboard.SetDataObject(data, true);
            Toast(Loc.T("Copiato negli Appunti"), 1200);
        }
        catch { Toast(Loc.T("Impossibile copiare")); }
    }

    private void ShowInExplorer()
    {
        if (Cur == null) return;
        Process.Start("explorer.exe", $"/select,\"{Cur.Path}\"");
    }

    private void Print()
    {
        var src = (_full ?? _shown)?.Bitmap;
        if (src == null) return;
        src = _viewOrient.Apply(src);
        var pd = new PrintDialog();
        if (pd.ShowDialog() != true) return;
        double pw = pd.PrintableAreaWidth, ph = pd.PrintableAreaHeight;
        bool landscape = src.PixelWidth > src.PixelHeight;
        if (landscape) (pw, ph) = (ph, pw);
        double m = 36;
        var img = new Image { Source = src, Stretch = Stretch.Uniform, Width = pw - 2 * m, Height = ph - 2 * m };
        var root = new Grid { Width = pw, Height = ph };
        root.Children.Add(img);
        if (landscape) root.LayoutTransform = new RotateTransform(90);
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        img.Margin = new Thickness(m);
        root.UpdateLayout();
        pd.PrintVisual(root, Cur?.Name ?? "Riffle");
    }

    // =====================================================================
    //  SCHERMO INTERO E PRESENTAZIONE
    // =====================================================================

    private Rect _preFsBounds;

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _preFsState = WindowState;
            _preFsBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _fullscreen = true;
            // niente cornice: i pulsanti di sistema spariscono e la finestra copre l'intero monitor (anche la barra delle applicazioni)
            var mon = Native.MonitorBounds(this);
            var dpi = VisualTreeHelper.GetDpi(this);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Left = mon.X / dpi.DpiScaleX; Top = mon.Y / dpi.DpiScaleY;
            Width = mon.Width / dpi.DpiScaleX; Height = mon.Height / dpi.DpiScaleY;
            Chrome.CaptionHeight = 0;
            Chrome.ResizeBorderThickness = new Thickness(0);
            CaptionButtons.Visibility = Visibility.Collapsed;
            Root.RowDefinitions[0].Height = new GridLength(0); // la barra diventa una sovrapposizione
            Grid.SetRow(Toolbar, 1);
            Toolbar.VerticalAlignment = VerticalAlignment.Top;
            Toolbar.Height = 48;
            Toolbar.Background = (Brush)FindResource("Overlay");
            Toolbar.Opacity = 0; Toolbar.IsHitTestVisible = false;
            StatusBar.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = Visibility.Collapsed;
            FullBtn.Content = "\uE73F";
            _chromeHidden = true;
            UpdateFrameMargin();
            ApplyBackground();
        }
        else
        {
            _fullscreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Chrome.CaptionHeight = 44;
            Chrome.ResizeBorderThickness = new Thickness(6);
            CaptionButtons.Visibility = Visibility.Visible;
            Root.RowDefinitions[0].Height = new GridLength(44);
            if (!_preFsBounds.IsEmpty) { Left = _preFsBounds.Left; Top = _preFsBounds.Top; Width = _preFsBounds.Width; Height = _preFsBounds.Height; }
            WindowState = _preFsState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            Grid.SetRow(Toolbar, 0);
            Toolbar.Height = double.NaN;
            Toolbar.BeginAnimation(OpacityProperty, null);
            Toolbar.Opacity = 1; Toolbar.IsHitTestVisible = true;
            Toolbar.Background = Brushes.Transparent;
            StatusBar.Visibility = Cur != null ? Visibility.Visible : Visibility.Collapsed;
            InfoPanel.Visibility = S.ShowInfo && _edit == null ? Visibility.Visible : Visibility.Collapsed;
            FullBtn.Content = "\uE740";
            Cursor = null;
            _chromeHidden = false;
            Fade(VideoBar, 1);
            UpdateFrameMargin();
            ApplyBackground();
            Native.HideSystemCaptionButtons(this); // WPF ripristina lo stile quando cambia WindowStyle
        }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Max_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }

    private bool _maxHover, _maxPressed;
    // congelati: i campi statici possono essere inizializzati da un thread qualsiasi (es. TraceLog dai thread di decodifica)
    private static readonly Brush MaxHoverBrush = Frozen(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MaxPressBrush = Frozen(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private void SetMaxVisual(bool hover, bool pressed)
    {
        if (hover == _maxHover && pressed == _maxPressed) return;
        _maxHover = hover; _maxPressed = pressed;
        MaxBtn.Background = pressed ? MaxPressBrush : hover ? MaxHoverBrush : Brushes.Transparent;
    }

    /// <summary>
    /// Il pulsante Ingrandisci si dichiara a Windows come HTMAXBUTTON: così Windows 11 mostra il menu
    /// "Snap layouts" al passaggio del mouse. Hover e clic vanno quindi gestiti qui (arrivano come messaggi NC).
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2,
                  WM_NCMOUSELEAVE = 0x2A2, WM_MOUSEMOVE = 0x200, HTMAXBUTTON = 9;
        switch (msg)
        {
            case WM_NCHITTEST:
                if (_fullscreen || CaptionButtons.Visibility != Visibility.Visible || !MaxBtn.IsVisible) break;
                int x = unchecked((short)(long)lParam), y = unchecked((short)((long)lParam >> 16));
                var p = MaxBtn.PointFromScreen(new Point(x, y));
                if (p.X >= 0 && p.Y >= 0 && p.X < MaxBtn.ActualWidth && p.Y < MaxBtn.ActualHeight)
                {
                    SetMaxVisual(true, _maxPressed);
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
                SetMaxVisual(false, false);
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                SetMaxVisual(true, true);
                handled = true;
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                bool wasPressed = _maxPressed;
                SetMaxVisual(true, false);
                if (wasPressed) ToggleMaximize();
                handled = true;
                return IntPtr.Zero;
            case WM_NCMOUSEMOVE when wParam.ToInt32() != HTMAXBUTTON:
            case WM_NCMOUSELEAVE:
            case WM_MOUSEMOVE:
                SetMaxVisual(false, false);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Con la barra del titolo personalizzata una finestra massimizzata sborda: compensiamo.</summary>
    private void UpdateFrameMargin()
    {
        Root.Margin = WindowState == WindowState.Maximized && !_fullscreen ? new Thickness(Native.MaximizedOverhang(this)) : new Thickness(0);
    }

    private void Slideshow_Click(object sender, RoutedEventArgs e)
    {
        if (_slideshow) { StopSlideshow(); return; }
        if (_items.Count == 0 || _edit != null) return;
        _slideshow = true;
        if (!_fullscreen) ToggleFullscreen();
        HideChrome();
        _slideTimer.Interval = TimeSpan.FromSeconds(S.SlideshowSeconds);
        _slideTimer.Start();
        Toast(Loc.F("Presentazione · {0} s · Esc per uscire", S.SlideshowSeconds), 1800);
    }

    private void StopSlideshow()
    {
        _slideshow = false;
        _slideTimer.Stop();
        if (_fullscreen) ToggleFullscreen();
    }

    private void ApplyBackground()
    {
        // Mica dietro barra e pannelli; area della foto leggermente più scura (o nera)
        Root.Background = _fullscreen ? Brushes.Black : _mica ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        StageHost.Background = S.BlackBackground || _fullscreen ? Brushes.Black : (Brush)FindResource("StageBg");
    }

    // =====================================================================
    //  PANNELLI E MENU
    // =====================================================================

    private void Prev_Click(object sender, RoutedEventArgs e) => Go(_index - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => Go(_index + 1);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(1.25, Center());
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(1 / 1.25, Center());
    private void ZoomToggle_Click(object sender, RoutedEventArgs e) => ToggleFitActual();
    private async void RotL_Click(object sender, RoutedEventArgs e) => await RotateAndSave(-90);
    private async void RotR_Click(object sender, RoutedEventArgs e) => await RotateAndSave(90);
    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteCurrent(S.ConfirmDelete);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        if (_edit != null) return;
        S.ShowInfo = InfoPanel.Visibility != Visibility.Visible;
        InfoPanel.Visibility = S.ShowInfo ? Visibility.Visible : Visibility.Collapsed;
        if (S.ShowInfo) UpdateInfo();
    }

    private void More_Click(object sender, RoutedEventArgs e) => ShowMoreMenu((FrameworkElement)sender);

    private void ShowMoreMenu(FrameworkElement? anchor)
    {
        var m = new ContextMenu();
        MenuItem Add(ItemsControl parent, string header, Action a, string? gesture = null, bool enabled = true, bool? check = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            if (check != null) mi.IsChecked = check.Value;
            mi.Click += (_, _) => a();
            parent.Items.Add(mi);
            return mi;
        }
        var it = Cur;
        bool has = it != null;
        Add(m, Loc.T("Apri file…"), () => OpenFile_Click(this, new RoutedEventArgs()), "Ctrl+O");
        Add(m, Loc.T("Apri cartella…"), () => OpenFolder_Click(this, new RoutedEventArgs()), "Ctrl+Maiusc+O");
        m.Items.Add(new Separator());
        Add(m, Loc.T("Mostra in Esplora file"), ShowInExplorer, "Ctrl+E", has);
        Add(m, Loc.T("Apri con…"), () => { if (Cur != null) { _video?.Pause(); Native.OpenWith(this, Cur.Path); } }, null, has);
        Add(m, Loc.T("Copia"), CopyCurrent, "Ctrl+C", has);
        Add(m, Loc.T("Copia percorso"), () => { if (Cur != null) { Clipboard.SetText(Cur.Path); Toast(Loc.T("Percorso copiato")); } }, "Ctrl+Maiusc+C", has);
        Add(m, Loc.T("Rinomina…"), async () => await Rename(), "F2", has && _edit == null);
        Add(m, Loc.T("Elimina"), async () => await DeleteCurrent(S.ConfirmDelete), "Canc", has && _edit == null);
        m.Items.Add(new Separator());
        Add(m, Loc.T("Imposta come sfondo del desktop"), () =>
        {
            if (Cur == null) return;
            Toast(Native.SetWallpaper(Cur.Path) ? Loc.T("Sfondo impostato") : Loc.T("Impossibile impostare lo sfondo"));
        }, null, has && !it!.IsVideo);
        Add(m, Loc.T("Stampa…"), Print, "Ctrl+P", has && !it!.IsVideo && _shown != null);
        m.Items.Add(new Separator());

        var sort = new MenuItem { Header = Loc.T("Ordina per") };
        void SortOpt(string h, SortBy s) => Add(sort, h, () => { S.Sort = s; if (_folder != null) LoadFolder(_folder, Cur?.Path, true); }, null, true, S.Sort == s);
        SortOpt(Loc.T("Nome"), SortBy.Name); SortOpt(Loc.T("Data di modifica"), SortBy.Modified); SortOpt(Loc.T("Dimensione"), SortBy.Size); SortOpt(Loc.T("Tipo"), SortBy.Type);
        sort.Items.Add(new Separator());
        Add(sort, Loc.T("Decrescente"), () => { S.SortDescending = !S.SortDescending; if (_folder != null) LoadFolder(_folder, Cur?.Path, true); }, null, true, S.SortDescending);
        m.Items.Add(sort);

        var opt = new MenuItem { Header = Loc.T("Impostazioni") };
        Add(opt, Loc.T("La rotellina del mouse fa zoom (invece di scorrere)"), () => S.WheelZooms = !S.WheelZooms, null, true, S.WheelZooms);
        Add(opt, Loc.T("Avvia i video automaticamente"), () => S.AutoPlayVideo = !S.AutoPlayVideo, null, true, S.AutoPlayVideo);
        Add(opt, Loc.T("Ripeti i video"), () => Loop_Click(this, new RoutedEventArgs()), "L", true, S.LoopVideo);
        Add(opt, Loc.T("Chiedi conferma prima di eliminare"), () => S.ConfirmDelete = !S.ConfirmDelete, null, true, S.ConfirmDelete);
        Add(opt, Loc.T("Sfondo nero"), () => { S.BlackBackground = !S.BlackBackground; if (!_fullscreen) ApplyBackground(); }, null, true, S.BlackBackground);
        var ss = new MenuItem { Header = Loc.T("Durata presentazione") };
        foreach (var sec in new[] { 2, 3, 5, 8, 15 }) { int s = sec; Add(ss, Loc.F("{0} secondi", s), () => S.SlideshowSeconds = s, null, true, S.SlideshowSeconds == s); }
        opt.Items.Add(ss);
        opt.Items.Add(new Separator());
        var lang = new MenuItem { Header = Loc.T("Lingua") + " / Language" };
        Add(lang, Loc.T("Automatica (come Windows)"), () => SetLanguage("auto"), null, true, S.Language == "auto");
        Add(lang, "English", () => SetLanguage("en"), null, true, S.Language == "en");
        Add(lang, "Italiano", () => SetLanguage("it"), null, true, S.Language == "it");
        opt.Items.Add(lang);
        opt.Items.Add(new Separator());
        Add(opt, Loc.T("Registra Riffle nelle app predefinite di Windows…"), RegisterApp);
        m.Items.Add(opt);
        Add(m, Loc.T("Scorciatoie da tastiera"), ShowShortcuts, "?");
        Add(m, Loc.T("Informazioni su Riffle"), ShowAbout);

        if (anchor != null) OpenMenu(m, anchor);
        else { m.Placement = PlacementMode.MousePoint; m.IsOpen = true; }
    }

    private static void OpenMenu(ContextMenu m, FrameworkElement anchor)
    {
        m.PlacementTarget = anchor;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    private const string ShortcutsIt =
        "← →  Precedente / successiva   ·   Rotellina  Scorri   ·   Ctrl+rotellina  Zoom\n" +
        "Home / Fine  Primo / ultimo   ·   + −  Zoom   ·   0  Adatta   ·   1  100%   ·   Doppio clic  Zoom\n" +
        "F / F11 / Invio  Schermo intero   ·   F5  Presentazione   ·   Esc  Esci\n" +
        "Ctrl+R / Ctrl+L  Ruota   ·   E  Modifica   ·   I  Informazioni   ·   T  Miniature\n" +
        "Canc  Cestino (Maiusc+Canc senza conferma)   ·   F2  Rinomina   ·   Ctrl+C  Copia\n\n" +
        "VIDEO:  Spazio  Play/Pausa   ·   Maiusc+← →  ±5 s   ·   Ctrl+← →  ±30 s\n" +
        "↑ ↓  Volume   ·   M  Muto   ·   L  Ripeti   ·   .  Fotogramma   ·   [ ]  Inizio/fine clip";

    private async void ShowShortcuts()
    {
        await Confirm(Loc.T("Scorciatoie da tastiera"), Loc.IsItalian ? ShortcutsIt : Loc.T("SHORTCUTS"), "OK", showCancel: false);
    }

    public const string Author = "Andrea Parolini";
    public const string RepoUrl = "https://github.com/IncludePetrol/riffle";

    private async void ShowAbout()
    {
        var v = typeof(MainWindow).Assembly.GetName().Version;
        var t = Confirm("Riffle " + (v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}"),
            Loc.T("Visualizzatore istantaneo di foto e video") + "\n\n" +
            Loc.F("Creato da {0}", Author) + "\n" +
            Loc.T("Licenza CC BY-NC-ND 4.0: uso libero non commerciale, citando l'autore; vietato distribuire versioni modificate.") + "\n\n" +
            Loc.T("Componenti di terze parti") + ": libVLC (LGPL-2.1), Magick.NET / ImageMagick (Apache-2.0), .NET (MIT)",
            "OK", showCancel: false);
        DlgLink.Content = RepoUrl.Replace("https://", "");
        DlgLink.Visibility = Visibility.Visible;
        await t;
    }

    private void DlgLink_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Cambia lingua e riavvia l'app sullo stesso file.</summary>
    private void SetLanguage(string lang)
    {
        if (S.Language == lang) return;
        S.Language = lang;
        SaveWindow();
        var args = Cur != null ? $"\"{Cur.Path}\"" : (_folder != null ? $"\"{_folder}\"" : "");
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false }); } catch { }
        _video?.Dispose(); _video = null;
        Application.Current.Shutdown();
    }

    private void RegisterApp()
    {
        try
        {
            FileAssoc.Register();
            Toast(Loc.T("Registrata. Scegli Riffle come app predefinita nelle Impostazioni."), 3500);
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception ex) { Toast(Loc.F("Errore: {0}", ex.Message), 3000); }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var exts = string.Join(";", MediaTypes.ImageExt.Concat(MediaTypes.VideoExt).Select(x => "*" + x));
        var dlg = new OpenFileDialog
        {
            Filter = $"{Loc.T("Foto e video")}|{exts}|{Loc.T("Tutti i file")}|*.*",
            InitialDirectory = _folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(this) == true) Open(dlg.FileName);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = _folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
        if (dlg.ShowDialog(this) == true) Open(dlg.FolderName);
    }

    private void LastFolder_Click(object sender, RoutedEventArgs e) { if (S.LastFolder != null) Open(S.LastFolder); }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (_edit != null) ExitEdit();
            Open(files[0]);
        }
    }

    // =====================================================================
    //  INFORMAZIONI
    // =====================================================================

    private int _infoVer;
    private void UpdateInfo()
    {
        var it = Cur;
        InfoList.Children.Clear();
        if (it == null) return;
        int ver = ++_infoVer;
        var rows = new List<(string, string)>
        {
            (Loc.T("Nome"), it.Name),
            (Loc.T("Cartella"), Path.GetDirectoryName(it.Path) ?? ""),
        };
        try
        {
            var fi = new FileInfo(it.Path);
            rows.Add((Loc.T("Dimensione file"), FormatBytes(fi.Length)));
            rows.Add((Loc.T("Modificato"), fi.LastWriteTime.ToString("d MMMM yyyy, HH:mm")));
        }
        catch { }
        if (it.IsVideo)
        {
            if (_video != null)
            {
                if (_video.LengthMs > 0) rows.Add((Loc.T("Durata"), Fmt(_video.LengthMs)));
                rows.AddRange(_video.Info());
            }
            Render(rows);
            return;
        }
        if (_shown != null && !_placeholder)
        {
            rows.Insert(2, (Loc.T("Risoluzione"), $"{_shown.FullWidth} × {_shown.FullHeight}  ({_shown.FullWidth * (double)_shown.FullHeight / 1e6:0.#} MP)"));
        }
        Render(rows);
        var path = it.Path;
        Task.Run(() => Metadata.Read(path)).ContinueWith(t =>
        {
            if (ver != _infoVer || t.IsFaulted) return;
            rows.AddRange(t.Result);
            Render(rows);
        }, TaskScheduler.FromCurrentSynchronizationContext());

        void Render(List<(string k, string v)> r)
        {
            InfoList.Children.Clear();
            foreach (var (k, v) in r)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                InfoList.Children.Add(new TextBlock { Text = k.ToUpperInvariant(), FontSize = 10.5, Foreground = (Brush)FindResource("Fg2"), Margin = new Thickness(0, 12, 0, 2) });
                if (k == Loc.T("Posizione") && v.Contains(','))
                {
                    var link = new TextBlock { Text = v + "  ↗", Foreground = (Brush)FindResource("Accent"), Cursor = Cursors.Hand, TextWrapping = TextWrapping.Wrap };
                    var coords = v;
                    link.MouseLeftButtonUp += (_, _) => Process.Start(new ProcessStartInfo($"https://www.openstreetmap.org/?mlat={coords.Split(',')[0].Trim()}&mlon={coords.Split(',')[1].Trim()}&zoom=15") { UseShellExecute = true });
                    InfoList.Children.Add(link);
                }
                else InfoList.Children.Add(new TextBox { Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = (Brush)FindResource("Fg"), TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Focusable = true });
            }
        }
    }

    /// <summary>Barra in basso a sinistra: formato, risoluzione, durata, peso, data.</summary>
    private void UpdateStatus()
    {
        var it = Cur;
        if (it == null) { StatusBar.Visibility = Visibility.Collapsed; return; }
        StFormat.Text = Path.GetExtension(it.Name).TrimStart('.').ToUpperInvariant();
        int w = 0, h = 0;
        if (!it.IsVideo && _shown != null && _shownPath == it.Path) { w = _shown.FullWidth; h = _shown.FullHeight; if (_viewOrient.SwapsAxes) (w, h) = (h, w); }
        else if (it.IsVideo && _video?.Bitmap != null) { var (vw, vh, sar) = _video.VisibleSize(); w = (int)Math.Round(vw * sar); h = vh; }
        StRes.Text = w > 0 ? $"{w} × {h}" : "";
        StResBox.Visibility = w > 0 ? Visibility.Visible : Visibility.Collapsed;
        long len = it.IsVideo ? _video?.LengthMs ?? 0 : 0;
        StDur.Text = len > 0 ? Fmt(len) : "";
        StDurBox.Visibility = len > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (it.Size <= 0) try { var fi = new FileInfo(it.Path); it.Size = fi.Length; it.Modified = fi.LastWriteTime; } catch { }
        StSize.Text = it.Size > 0 ? FormatBytes(it.Size) : "";
        StSizeBox.Visibility = it.Size > 0 ? Visibility.Visible : Visibility.Collapsed;
        StDate.Text = it.Modified > DateTime.MinValue ? it.Modified.ToString("g", Loc.Culture) : "";
        StDateBox.Visibility = it.Modified > DateTime.MinValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatBytes(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.##} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.#} MB" : $"{b / 1024.0:0} KB";

    // =====================================================================
    //  MODIFICA
    // =====================================================================

    private void BuildEditControls()
    {
        void Slider(string key, string label)
        {
            var head = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var val = new TextBlock { Text = "0", Foreground = (Brush)FindResource("Fg2"), FontSize = 12 };
            DockPanel.SetDock(val, Dock.Right);
            head.Children.Add(val);
            head.Children.Add(new TextBlock { Text = label });
            var s = new Slider { Minimum = -100, Maximum = 100, Value = 0, Margin = new Thickness(0, 4, 0, 0), IsMoveToPointEnabled = true, SmallChange = 1, LargeChange = 10 };
            s.ValueChanged += (_, e) =>
            {
                val.Text = ((int)Math.Round(e.NewValue)).ToString("+0;−0;0");
                if (_edit == null) return;
                double v = Math.Round(e.NewValue);
                switch (key)
                {
                    case "b": _edit.Adjust.Brightness = v; break;
                    case "e": _edit.Adjust.Exposure = v; break;
                    case "c": _edit.Adjust.Contrast = v; break;
                    case "s": _edit.Adjust.Saturation = v; break;
                    case "w": _edit.Adjust.Warmth = v; break;
                }
                QueuePreview();
            };
            s.MouseDoubleClick += (_, _) => s.Value = 0;
            _adjSliders[key] = s;
            AdjustList.Children.Add(head);
            AdjustList.Children.Add(s);
        }
        Slider("b", Loc.T("Luminosità"));
        Slider("e", Loc.T("Esposizione"));
        Slider("c", Loc.T("Contrasto"));
        Slider("s", Loc.T("Saturazione"));
        Slider("w", Loc.T("Calore"));

        foreach (var (name, ratio) in new (string, double?)[] { (Loc.T("Libero"), null), (Loc.T("Originale"), -1), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:5", 0.8) })
        {
            var b = new Button { Content = name, Style = (Style)FindResource("TextBtn"), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 6), FontSize = 12, Tag = ratio };
            b.Click += (_, _) => SetAspect(ratio);
            AspectPanel.Children.Add(b);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_edit != null) return;
        var it = Cur;
        if (it == null || it.IsVideo || _shown == null || _placeholder || _shown.Animation != null) return;
        if (_slideshow) StopSlideshow();
        if (_fullscreen) ToggleFullscreen();
        _edit = new EditState { Orient = _viewOrient };
        _preview = new Editing.Preview(_shown.Bitmap);
        _aspect = null;
        foreach (var s in _adjSliders.Values) s.Value = 0;
        BwCheck.IsChecked = false;
        InfoPanel.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Visible;
        CropLayer.Visibility = Visibility.Visible;
        bool writable = MediaTypes.WritableExt.Contains(Path.GetExtension(it.Path));
        SaveOverBtn.IsEnabled = writable;
        SaveOverBtn.ToolTip = writable ? Loc.T("Sostituisce il file originale") : Loc.T("Formato non sovrascrivibile: salva una copia JPEG");
        HighlightAspect();
        RenderPreview(refit: true);
        UpdateCommands();
    }

    private void ExitEdit()
    {
        _edit = null; _preview = null;
        EditPanel.Visibility = Visibility.Collapsed;
        CropLayer.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = S.ShowInfo ? Visibility.Visible : Visibility.Collapsed;
        if (_shown != null && _shownPath != null) ShowDecoded(_shown, _shownPath);
        UpdateCommands();
    }

    private void QueuePreview()
    {
        if (_previewQueued) return;
        _previewQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { _previewQueued = false; RenderPreview(false); });
    }

    private void RenderPreview(bool refit)
    {
        if (_edit == null || _preview == null || _shown == null) return;
        var bmp = _preview.Render(_edit);
        bool swap = _edit.Orient.SwapsAxes;
        int fw = _shown.FullWidth, fh = _shown.FullHeight;
        StopAnim();
        SetSource(bmp, fw, fh, _edit.Orient);
        if (refit) { _fit = true; FitView(); ApplyXf(); }
        UpdateCropVisual();
    }

    private Rect ImageRectOnStage() => new(_tx, _ty, _elemW * _scale, _elemH * _scale);

    private void UpdateCropVisual()
    {
        if (_edit == null) return;
        var ir = ImageRectOnStage();
        var c = _edit.Crop;
        var r = new Rect(ir.X + c.X * ir.Width, ir.Y + c.Y * ir.Height, c.Width * ir.Width, c.Height * ir.Height);
        CropOuter.Rect = new Rect(0, 0, StageHost.ActualWidth, StageHost.ActualHeight);
        CropInner.Rect = r;
        Canvas.SetLeft(CropFrame, r.X); Canvas.SetTop(CropFrame, r.Y);
        CropFrame.Width = r.Width; CropFrame.Height = r.Height;
        var thirds = new GeometryGroup();
        for (int i = 1; i <= 2; i++)
        {
            thirds.Children.Add(new LineGeometry(new Point(r.X + r.Width * i / 3, r.Y), new Point(r.X + r.Width * i / 3, r.Bottom)));
            thirds.Children.Add(new LineGeometry(new Point(r.X, r.Y + r.Height * i / 3), new Point(r.Right, r.Y + r.Height * i / 3)));
        }
        CropThirds.Data = thirds;
        var h = new GeometryGroup();
        const double L = 18, T = 4;
        foreach (var (px, py, sx, sy) in new[] { (r.Left, r.Top, 1, 1), (r.Right, r.Top, -1, 1), (r.Left, r.Bottom, 1, -1), (r.Right, r.Bottom, -1, -1) })
        {
            h.Children.Add(new RectangleGeometry(new Rect(sx > 0 ? px - T / 2 : px - L + T / 2, sy > 0 ? py - T / 2 : py - T / 2, L, T)));
            h.Children.Add(new RectangleGeometry(new Rect(sx > 0 ? px - T / 2 : px - T / 2, sy > 0 ? py - T / 2 : py - L + T / 2, T, L)));
        }
        if (_aspect == null)
            foreach (var (px, py, horiz) in new[] { (r.Left + r.Width / 2, r.Top, true), (r.Left + r.Width / 2, r.Bottom, true), (r.Left, r.Top + r.Height / 2, false), (r.Right, r.Top + r.Height / 2, false) })
                h.Children.Add(new RectangleGeometry(horiz ? new Rect(px - L / 2, py - T / 2, L, T) : new Rect(px - T / 2, py - L / 2, T, L)));
        CropHandles.Data = h;
    }

    [Flags] private enum Edge { None = 0, L = 1, T = 2, R = 4, B = 8, Move = 16 }
    private Edge _cropDrag;
    private Point _cropStart;
    private Rect _cropStartRect;

    private Edge HitCrop(Point p)
    {
        if (_edit == null) return Edge.None;
        var ir = ImageRectOnStage(); var c = _edit.Crop;
        var r = new Rect(ir.X + c.X * ir.Width, ir.Y + c.Y * ir.Height, c.Width * ir.Width, c.Height * ir.Height);
        const double tol = 14;
        Edge e = Edge.None;
        bool inY = p.Y > r.Top - tol && p.Y < r.Bottom + tol, inX = p.X > r.Left - tol && p.X < r.Right + tol;
        if (inY && Math.Abs(p.X - r.Left) < tol) e |= Edge.L;
        if (inY && Math.Abs(p.X - r.Right) < tol) e |= Edge.R;
        if (inX && Math.Abs(p.Y - r.Top) < tol) e |= Edge.T;
        if (inX && Math.Abs(p.Y - r.Bottom) < tol) e |= Edge.B;
        if (_aspect != null && e is Edge.L or Edge.R or Edge.T or Edge.B) e = Edge.None; // con proporzioni fisse solo gli angoli
        if (e == Edge.None && r.Contains(p)) e = Edge.Move;
        return e;
    }

    private void Crop_Down(object sender, MouseButtonEventArgs e)
    {
        if (_edit == null) return;
        var p = e.GetPosition(CropLayer);
        _cropDrag = HitCrop(p);
        if (_cropDrag == Edge.None) return;
        _cropStart = p; _cropStartRect = _edit.Crop;
        CropLayer.CaptureMouse();
        e.Handled = true;
    }

    private void Crop_Up(object sender, MouseButtonEventArgs e)
    {
        _cropDrag = Edge.None;
        CropLayer.ReleaseMouseCapture();
    }

    private void Crop_Move(object sender, MouseEventArgs e)
    {
        if (_edit == null) return;
        var p = e.GetPosition(CropLayer);
        if (_cropDrag == Edge.None)
        {
            var h = HitCrop(p);
            CropLayer.Cursor = h switch
            {
                Edge.Move => Cursors.SizeAll,
                Edge.L or Edge.R => Cursors.SizeWE,
                Edge.T or Edge.B => Cursors.SizeNS,
                Edge.L | Edge.T or Edge.R | Edge.B => Cursors.SizeNWSE,
                Edge.R | Edge.T or Edge.L | Edge.B => Cursors.SizeNESW,
                _ => Cursors.Arrow,
            };
            return;
        }
        var ir = ImageRectOnStage();
        double dx = (p.X - _cropStart.X) / ir.Width, dy = (p.Y - _cropStart.Y) / ir.Height;
        var s = _cropStartRect;
        double l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        const double min = 0.02;
        if (_cropDrag == Edge.Move)
        {
            double nx = Math.Clamp(l + dx, 0, 1 - s.Width), ny = Math.Clamp(t + dy, 0, 1 - s.Height);
            _edit.Crop = new Rect(nx, ny, s.Width, s.Height);
        }
        else
        {
            if (_cropDrag.HasFlag(Edge.L)) l = Math.Clamp(l + dx, 0, r - min);
            if (_cropDrag.HasFlag(Edge.R)) r = Math.Clamp(r + dx, l + min, 1);
            if (_cropDrag.HasFlag(Edge.T)) t = Math.Clamp(t + dy, 0, b - min);
            if (_cropDrag.HasFlag(Edge.B)) b = Math.Clamp(b + dy, t + min, 1);
            if (_aspect is double asp && asp > 0)
            {
                // proporzione in coordinate normalizzate
                double na = asp * ir.Height / ir.Width;
                double w = r - l, hgt = w / na;
                bool top = _cropDrag.HasFlag(Edge.T);
                double maxH = top ? s.Bottom : 1 - s.Top;
                if (hgt > maxH) { hgt = maxH; w = hgt * na; }
                if (_cropDrag.HasFlag(Edge.L)) l = r - w; else r = l + w;
                if (top) t = b - hgt; else b = t + hgt;
                if (l < 0) { l = 0; w = r - l; hgt = w / na; if (top) t = b - hgt; else b = t + hgt; }
                if (r > 1) { r = 1; w = r - l; hgt = w / na; if (top) t = b - hgt; else b = t + hgt; }
            }
            _edit.Crop = new Rect(l, t, Math.Max(min, r - l), Math.Max(min, b - t));
        }
        UpdateCropVisual();
    }

    private void SetAspect(double? ratio)
    {
        if (_edit == null || _shown == null) return;
        int fw = _edit.Orient.SwapsAxes ? _shown.FullHeight : _shown.FullWidth;
        int fh = _edit.Orient.SwapsAxes ? _shown.FullWidth : _shown.FullHeight;
        if (ratio == -1) ratio = (double)fw / fh;
        _aspect = ratio;
        if (ratio is double a)
        {
            double na = a * fh / fw; // larghezza/altezza in coordinate normalizzate
            double w = 1, h = 1 / na;
            if (h > 1) { h = 1; w = na; }
            w *= 0.96; h *= 0.96;
            _edit.Crop = new Rect((1 - w) / 2, (1 - h) / 2, w, h);
        }
        else _edit.Crop = new Rect(0, 0, 1, 1);
        HighlightAspect();
        UpdateCropVisual();
    }

    private void HighlightAspect()
    {
        foreach (Button b in AspectPanel.Children)
        {
            bool on = Equals(b.Tag, _aspect) || (b.Tag is double d && d == -1 && _aspect != null && _shown != null && false);
            b.BorderBrush = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Stroke");
        }
    }

    private void EditRotL_Click(object sender, RoutedEventArgs e) => EditOrient(_edit?.Orient.RotateCw(-90));
    private void EditRotR_Click(object sender, RoutedEventArgs e) => EditOrient(_edit?.Orient.RotateCw(90));
    private void EditFlipH_Click(object sender, RoutedEventArgs e) => EditOrient(_edit?.Orient.FlipH());
    private void EditFlipV_Click(object sender, RoutedEventArgs e) => EditOrient(_edit?.Orient.FlipV());

    private void EditOrient(Orient? o)
    {
        if (_edit == null || o == null) return;
        _edit.Orient = o.Value;
        _edit.Crop = new Rect(0, 0, 1, 1);
        var a = _aspect; _aspect = null;
        RenderPreview(refit: true);
        if (a != null) SetAspect(a); else HighlightAspect();
    }

    private void Bw_Changed(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        _edit.Adjust.Grayscale = BwCheck.IsChecked == true;
        QueuePreview();
    }

    private void EditReset_Click(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        foreach (var s in _adjSliders.Values) s.Value = 0;
        BwCheck.IsChecked = false;
        _edit.Adjust = new Adjust();
        _aspect = null;
        _edit.Orient = Orient.None;
        _edit.Crop = new Rect(0, 0, 1, 1);
        HighlightAspect();
        RenderPreview(refit: true);
    }

    private async void EditCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        if (_edit.IsDirty && !(_edit.Orient == _viewOrient && _edit.Crop == new Rect(0, 0, 1, 1) && _edit.Adjust.IsNeutral)
            && !await Confirm(Loc.T("Scartare le modifiche?"), Loc.T("Le modifiche non salvate andranno perse."), Loc.T("Scarta"))) return;
        ExitEdit();
    }

    private async void SaveCopy_Click(object sender, RoutedEventArgs e) => await SaveEdit(overwrite: false);
    private async void SaveOver_Click(object sender, RoutedEventArgs e)
    {
        if (!await Confirm(Loc.T("Sovrascrivere l'originale?"), Loc.T("Il file originale verrà sostituito con la versione modificata."), Loc.T("Sovrascrivi"))) return;
        await SaveEdit(overwrite: true);
    }

    private async Task SaveEdit(bool overwrite)
    {
        var it = Cur;
        if (_edit == null || it == null) return;
        var st = new EditState { Orient = _edit.Orient, Crop = _edit.Crop, Adjust = _edit.Adjust.Clone() };
        string ext = Path.GetExtension(it.Path);
        string dest = overwrite ? it.Path : Editing.UniqueName(it.Path, Loc.T("_modificata"), MediaTypes.WritableExt.Contains(ext) ? ext : ".jpg");
        ShowBusy(Loc.T("Salvataggio…"));
        _selfWrites.Add(it.Path);
        try
        {
            await Task.Run(() => { lock (_saveLock) Editing.Save(it.Path, dest, st); });
            HideBusy();
            _viewOrient = Orient.None;
            _cache.Invalidate(dest);
            Thumbnails.Instance.Forget(dest);
            _edit = null; _preview = null;
            EditPanel.Visibility = Visibility.Collapsed;
            CropLayer.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = S.ShowInfo ? Visibility.Visible : Visibility.Collapsed;
            if (overwrite)
            {
                _shown = null; _shownPath = null; _placeholder = true;
                Prefetch();
                Toast(Loc.T("Modifiche salvate"), 1500);
            }
            else
            {
                Toast(Loc.F("Copia salvata: {0}", Path.GetFileName(dest)), 2500);
                if (_folder != null) LoadFolder(_folder, dest, keepCurrent: false);
            }
            UpdateCommands();
        }
        catch (Exception ex) { HideBusy(); Toast(Loc.F("Errore nel salvataggio: {0}", ex.Message), 4000); }
        finally { var p = it.Path; _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.BeginInvoke(() => _selfWrites.Remove(p))); }
    }

    // =====================================================================
    //  DIALOGHI, NOTIFICHE, UTILITÀ
    // =====================================================================

    private TaskCompletionSource<bool>? _dlg;

    private Task<bool> Confirm(string title, string text, string ok, bool showCancel = true)
    {
        _dlg?.TrySetResult(false);
        _dlg = new TaskCompletionSource<bool>();
        DlgTitle.Text = title; DlgText.Text = text; DlgOk.Content = ok;
        DlgText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        DlgInput.Visibility = Visibility.Collapsed;
        DlgLink.Visibility = Visibility.Collapsed;
        DlgCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(DlgOk, showCancel ? 1 : 3);
        DialogLayer.Visibility = Visibility.Visible;
        _video?.Pause();
        return _dlg.Task;
    }

    private async Task<string?> Prompt(string title, string initial)
    {
        var t = Confirm(title, "", Loc.T("Rinomina"));
        DlgInput.Visibility = Visibility.Visible;
        DlgInput.Text = initial;
        await Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { DlgInput.Focus(); DlgInput.SelectAll(); });
        return await t ? DlgInput.Text : null;
    }

    private void CloseDialog(bool ok)
    {
        DialogLayer.Visibility = Visibility.Collapsed;
        var d = _dlg; _dlg = null;
        Keyboard.Focus(this);
        d?.TrySetResult(ok);
    }
    private void DlgOk_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
    private void DlgCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);

    private void ShowBusy(string text) { BusyText.Text = text; BusyLayer.Visibility = Visibility.Visible; }
    private void HideBusy() => BusyLayer.Visibility = Visibility.Collapsed;

    private void Toast(string text, int ms = 1400)
    {
        ToastText.Text = text;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80 + ms))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300 + ms))));
        ToastBox.BeginAnimation(OpacityProperty, a);
    }

    private void ShowCenter(string icon, string text, bool onlyIfEmpty = false)
    {
        if (onlyIfEmpty && Img.Source != null && !_placeholder) return;
        CenterIcon.Text = icon; CenterText.Text = text;
        CenterMsg.Visibility = Visibility.Visible;
    }
    private void HideCenter() => CenterMsg.Visibility = Visibility.Collapsed;

    private void SaveWindow()
    {
        if (_fullscreen) ToggleFullscreen();
        S.WinMaximized = WindowState == WindowState.Maximized;
        var r = RestoreBounds;
        if (!r.IsEmpty) { S.WinLeft = r.Left; S.WinTop = r.Top; S.WinWidth = r.Width; S.WinHeight = r.Height; }
        S.Save();
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }
}

