using System.ComponentModel;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Riffle;

/// <summary>Miniature dalla cache di Windows (veloci, funzionano anche per i video) su thread STA dedicati.</summary>
public sealed class Thumbnails
{
    public static Thumbnails Instance { get; } = new();

    private sealed class Req { public required string Path; public int Size; public int Flags; public required Action<BitmapSource?> Done; public long Seq; public bool Urgent; }

    private readonly object _lock = new();
    private readonly LinkedList<Req> _queue = new();
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private const int MaxCache = 2500;
    private Dispatcher? _dispatcher;
    private long _seq;

    private Thumbnails()
    {
        for (int i = 0; i < 2; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "thumb" + i, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    public void AttachDispatcher(Dispatcher d) => _dispatcher = d;

    public bool TryGetCached(string path, out BitmapSource? bmp)
    {
        lock (_lock) return _cache.TryGetValue(path, out bmp);
    }

    /// <summary>Richiede una miniatura; callback sul thread UI. urgent = salta la coda (anteprima principale).</summary>
    public void Request(string path, Action<BitmapSource?> done, bool urgent = false)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var b)) { done(b); return; }
            var r = new Req { Path = path, Size = 192, Flags = Native.SIIGBF_THUMBNAILONLY | Native.SIIGBF_BIGGERSIZEOK, Done = done, Seq = ++_seq, Urgent = urgent };
            if (urgent) _queue.AddFirst(r); else _queue.AddLast(r);
            // le richieste della striscia più vecchie di 400 vengono scartate (l'utente ha già scorso oltre)
            while (_queue.Count > 400) _queue.RemoveLast();
            Monitor.Pulse(_lock);
        }
    }

    private void Worker()
    {
        while (true)
        {
            Req r;
            lock (_lock)
            {
                while (_queue.Count == 0) Monitor.Wait(_lock);
                // LIFO per le richieste normali: le più recenti sono quelle visibili
                var node = _queue.First!.Value.Urgent ? _queue.First : _queue.Last!;
                r = node.Value;
                _queue.Remove(node);
                if (_cache.TryGetValue(r.Path, out var cached)) { var c = cached; _dispatcher?.BeginInvoke(() => r.Done(c)); continue; }
            }
            var bmp = Native.ShellThumbnail(r.Path, r.Size, r.Flags);
            lock (_lock)
            {
                _cache[r.Path] = bmp;
                _lru.AddLast(r.Path);
                while (_lru.Count > MaxCache) { _cache.Remove(_lru.First!.Value); _lru.RemoveFirst(); }
            }
            _dispatcher?.BeginInvoke(DispatcherPriority.Background, () => r.Done(bmp));
        }
    }

    public void Forget(string path) { lock (_lock) _cache.Remove(path); }
}

/// <summary>Elemento della striscia di miniature (binding WPF).</summary>
public sealed class StripItem : INotifyPropertyChanged
{
    public StripItem(MediaItem item) { Item = item; }
    public MediaItem Item { get; }
    public bool IsVideo => Item.IsVideo;
    private BitmapSource? _thumb;
    private bool _requested;

    public BitmapSource? Thumb
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                if (Thumbnails.Instance.TryGetCached(Item.Path, out var c)) _thumb = c;
                else Thumbnails.Instance.Request(Item.Path, b => { _thumb = b; PropertyChanged?.Invoke(this, new(nameof(Thumb))); });
            }
            return _thumb;
        }
    }

    public void Reset() { _requested = false; _thumb = null; Thumbnails.Instance.Forget(Item.Path); PropertyChanged?.Invoke(this, new(nameof(Thumb))); }

    public event PropertyChangedEventHandler? PropertyChanged;
}
