using System.IO;
using TopicMonitor.Viewer.Layout;

namespace TopicMonitor.Viewer.Wpf;

/// <summary>Loads <c>layout.yaml</c>, watches it for external edits, and saves renderer changes back via the targeted text patch.</summary>
public sealed class LayoutFileService : IDisposable
{
    private readonly string _path;
    private readonly LayoutLoader _loader = new();
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public LayoutFileService(string path) => _path = Path.GetFullPath(path);

    public string FilePath => _path;

    /// <summary>The last successfully parsed layout; kept when a later edit has errors.</summary>
    public LayoutDocument? Document
    {
        get { lock (_gate) return _loader.Current; }
    }

    public string? LastError { get; private set; }

    /// <summary>Raised (on a worker thread) when an external edit changed the document or the error state.</summary>
    public event Action? Reloaded;

    public void Start()
    {
        Reload();

        var dir = Path.GetDirectoryName(_path);
        if (dir is null || !Directory.Exists(dir)) return;

        _debounce = new Timer(_ => { if (Reload()) Reloaded?.Invoke(); });
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler schedule = (_, _) => _debounce.Change(200, Timeout.Infinite);
        _watcher.Changed += schedule;
        _watcher.Created += schedule;
        _watcher.Deleted += schedule;
        _watcher.Renamed += (_, _) => _debounce.Change(200, Timeout.Infinite);
    }

    /// <summary>Re-reads the file; returns whether the document or error state changed.</summary>
    public bool Reload()
    {
        lock (_gate)
        {
            var oldText = _loader.Current?.RawText;
            var oldError = LastError;

            var text = ReadWithRetry();
            if (text is null)
            {
                LastError = $"Layout file not found: {_path}";
            }
            else
            {
                var result = _loader.LoadFromText(text);
                LastError = result.Success ? null : $"{Path.GetFileName(_path)} line {result.ErrorLine}, column {result.ErrorColumn}: {result.ErrorMessage}";
            }

            return oldText != _loader.Current?.RawText || oldError != LastError;
        }
    }

    /// <summary>Changes one lane entry's renderer in place and writes the file; <paramref name="laneIndex"/> comes from <see cref="LayoutCatalogCheck.FindLaneIndex"/>.</summary>
    public void SetRenderer(int laneIndex, RendererKind renderer) =>
        Apply(doc => doc.WithLaneRenderer(laneIndex, renderer));

    /// <summary>Appends a group of lanes to the file's <c>groups:</c>.</summary>
    public void AppendGroup(string name, IReadOnlyList<LaneSpec> lanes) =>
        Apply(doc => doc.WithAppendedGroup(name, lanes));

    private void Apply(Func<LayoutDocument, LayoutDocument> edit)
    {
        lock (_gate)
        {
            var updated = edit(_loader.Current ?? throw new InvalidOperationException("No layout loaded."));
            updated.SaveTo(_path);
            _loader.LoadFromText(updated.RawText);
            LastError = null;
        }
    }

    // An editor may still hold the file right after saving.
    private string? ReadWithRetry()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { return File.Exists(_path) ? File.ReadAllText(_path) : null; }
            catch (IOException) { Thread.Sleep(50); }
        }
        return null;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
