using System.Windows.Threading;

namespace Riffle;

/// <summary>
/// Cache LRU delle immagini già decodificate + pool di thread che pre-carica le immagini vicine
/// a quella corrente, in ordine di priorità. È il cuore della velocità di scorrimento.
/// </summary>
public sealed class ImageCache
{
    private sealed class Entry { public required DecodedImage Img; public long Stamp; }
    private sealed class Job { public required string Path; public int Priority; public long Seq; public CancellationTokenSource Cts = new(); }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Job> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Job> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes, _stamp, _seq;
    private readonly long _budget;
    private Dispatcher? _dispatcher;

    public int TargetW { get; private set; }
    public int TargetH { get; private set; }

    /// <summary>Chiamato sul thread UI quando un'immagine è pronta (path, null se errore).</summary>
    public event Action<string, DecodedImage?>? Decoded;

    public static ImageCache Instance { get; } = new();

    private ImageCache()
    {
        (TargetW, TargetH) = Native.LargestMonitorPixels();
        long ram = (long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _budget = Math.Clamp(ram / 8, 256L << 20, 1536L << 20);
        int n = Math.Clamp(Environment.ProcessorCount - 2, 2, 14);
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "decode" + i, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA); // alcuni codec WIC lo preferiscono
            t.Start();
        }
    }

    public void AttachDispatcher(Dispatcher d) => _dispatcher = d;

    public bool TryGet(string path, out DecodedImage img)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(path, out var e)) { e.Stamp = ++_stamp; img = e.Img; return true; }
        }
        img = null!;
        return false;
    }

    public void Put(string path, DecodedImage img)
    {
        lock (_lock)
        {
            if (_entries.Remove(path, out var old)) _bytes -= old.Img.Bytes;
            if (_running.TryGetValue(path, out var j)) j.Cts.Cancel();
            _pending.Remove(path);
            _entries[path] = new Entry { Img = img, Stamp = ++_stamp };
            _bytes += img.Bytes;
        }
    }

    public bool HasFailed(string path) { lock (_lock) return _failed.Contains(path); }

    public void Invalidate(string path)
    {
        lock (_lock)
        {
            if (_entries.Remove(path, out var e)) _bytes -= e.Img.Bytes;
            _failed.Remove(path);
            if (_running.TryGetValue(path, out var j)) j.Cts.Cancel();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear(); _bytes = 0; _failed.Clear();
            foreach (var j in _running.Values) j.Cts.Cancel();
        }
    }

    /// <summary>Imposta l'insieme di immagini desiderate (path, priorità: 0 = massima). Le richieste vecchie vengono scartate.</summary>
    public void SetWanted(IReadOnlyList<(string path, int prio)> wanted)
    {
        lock (_lock)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, _) in wanted) set.Add(p);
            _wanted = set;

            // scarta i lavori non più utili
            foreach (var k in _pending.Keys.Where(k => !set.Contains(k)).ToList()) _pending.Remove(k);
            foreach (var (p, j) in _running) if (!set.Contains(p)) j.Cts.Cancel();

            foreach (var (p, prio) in wanted)
            {
                if (_entries.ContainsKey(p) || _failed.Contains(p)) continue;
                if (_running.TryGetValue(p, out var r) && !r.Cts.IsCancellationRequested) { r.Priority = prio; continue; }
                if (_pending.TryGetValue(p, out var j)) { j.Priority = prio; j.Seq = ++_seq; }
                else _pending[p] = new Job { Path = p, Priority = prio, Seq = ++_seq };
            }
            Monitor.PulseAll(_lock);
        }
    }

    private void Worker()
    {
        while (true)
        {
            Job job;
            lock (_lock)
            {
                while (_pending.Count == 0) Monitor.Wait(_lock);
                job = _pending.Values.OrderBy(j => j.Priority).ThenByDescending(j => j.Seq).First();
                _pending.Remove(job.Path);
                _running[job.Path] = job;
            }

            DecodedImage? img = null;
            bool cancelled = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { img = ImageDecoder.Decode(job.Path, TargetW, TargetH, job.Cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            catch { img = null; }

            MainWindow.TraceLog($"  decode {System.IO.Path.GetFileName(job.Path)} p{job.Priority} {sw.ElapsedMilliseconds} ms {(cancelled ? "annullata" : img == null ? "ERRORE" : img.Decoder)}");
            lock (_lock)
            {
                if (_running.TryGetValue(job.Path, out var cur) && cur == job) _running.Remove(job.Path);
                if (cancelled || job.Cts.IsCancellationRequested) continue;
                if (img != null)
                {
                    if (_entries.Remove(job.Path, out var old)) _bytes -= old.Img.Bytes;
                    _entries[job.Path] = new Entry { Img = img, Stamp = ++_stamp };
                    _bytes += img.Bytes;
                    Evict();
                }
                else _failed.Add(job.Path);
            }
            var path = job.Path;
            _dispatcher?.BeginInvoke(DispatcherPriority.Send, () => Decoded?.Invoke(path, img));
        }
    }

    private void Evict()
    {
        if (_bytes <= _budget) return;
        foreach (var (k, e) in _entries.OrderBy(kv => kv.Value.Stamp).ToList())
        {
            if (_bytes <= _budget * 0.85) break;
            if (_wanted.Contains(k)) continue;
            _entries.Remove(k);
            _bytes -= e.Img.Bytes;
        }
    }
}
