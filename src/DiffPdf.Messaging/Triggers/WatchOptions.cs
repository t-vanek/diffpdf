namespace DiffPdf.Messaging.Triggers;

/// <summary>Folder-watch configuration, bound from the <c>Watches</c> appsettings section.</summary>
public sealed class WatchOptions
{
    public const string SectionName = "Watches";

    /// <summary>Master switch. When false the watcher does not run (default).</summary>
    public bool Enabled { get; set; }

    /// <summary>How often each watched <c>new/</c> folder is scanned.</summary>
    public int PollSeconds { get; set; } = 15;

    public List<FolderWatch> Folders { get; set; } = [];

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollSeconds);
}

/// <summary>Watch one instance's <c>new/</c> folder and launch a batch when a stable drop appears.</summary>
public sealed class FolderWatch
{
    public string BranchKey { get; set; } = "";
    public string InstanceKey { get; set; } = "";

    /// <summary>How long the folder must be unchanged before a batch is launched (lets a drop finish writing).</summary>
    public int StabilitySeconds { get; set; } = 30;

    public bool Enabled { get; set; } = true;

    public TimeSpan Stability => TimeSpan.FromSeconds(StabilitySeconds);
}
