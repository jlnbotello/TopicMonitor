namespace Panel.Sources.File;

/// <summary>
/// Thin wrapper over <see cref="FileSystemWatcher"/>: watches one file path and invokes a callback with its full
/// path on any change/create/rename event. Deliberately dumb and not virtual-time-testable - the parse+swap logic
/// it triggers (<see cref="ScenarioFileSource.TryLoad"/>) is the part covered by unit tests.
/// </summary>
public sealed class ScenarioFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;

    public ScenarioFileWatcher(string path, Action<string> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) directory = ".";
        var fileName = Path.GetFileName(fullPath);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };

        void Handler(object sender, FileSystemEventArgs args)
        {
            try { onChanged(fullPath); }
            catch { /* a watcher callback must never crash the process; the caller owns error reporting */ }
        }

        _watcher.Changed += Handler;
        _watcher.Created += Handler;
        _watcher.Renamed += (_, _) => Handler(this, null!);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose() => _watcher.Dispose();
}
