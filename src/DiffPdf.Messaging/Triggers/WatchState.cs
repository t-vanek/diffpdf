namespace DiffPdf.Messaging.Triggers;

/// <summary>
/// Per-folder debounce + dedupe state machine. A batch is launched once the folder is
/// non-empty, has stopped changing for a stability window (the report generator finished
/// writing the drop), and differs from whatever was last launched.
/// </summary>
/// <remarks>Not thread-safe; the watch service drives one instance per folder serially.</remarks>
public sealed class WatchState
{
    private FolderManifest _lastSeen = FolderManifest.Empty;
    private DateTimeOffset _stableSince;
    private FolderManifest? _lastLaunched;

    /// <summary>
    /// Records the latest scan and returns true exactly once per stable, not-yet-launched drop.
    /// </summary>
    public bool Observe(FolderManifest current, DateTimeOffset now, TimeSpan stability)
    {
        if (current.IsEmpty)
        {
            _lastSeen = current;
            return false;
        }

        if (current != _lastSeen)
        {
            // Still changing — restart the stability clock and wait.
            _lastSeen = current;
            _stableSince = now;
            return false;
        }

        if (now - _stableSince < stability)
            return false; // stable, but not long enough yet

        if (current == _lastLaunched)
            return false; // this exact drop already launched

        _lastLaunched = current;
        return true;
    }
}
