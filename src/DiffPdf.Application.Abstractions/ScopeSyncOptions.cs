namespace DiffPdf.Application.Abstractions;

/// <summary>
/// Configures scope synchronization: reconciling a filesystem root laid out as
/// <c>&lt;root&gt;/&lt;branch&gt;/&lt;instance&gt;/{old,new,reports}</c> with the branch/instance records in the database.
/// When <see cref="RootPath"/> is empty the feature is a no-op.
/// </summary>
public sealed class ScopeSyncOptions
{
    public const string SectionName = "ScopeSync";

    /// <summary>Run the periodic + startup background sync. The on-demand endpoint works regardless.</summary>
    public bool Enabled { get; set; }

    /// <summary>Root folder scanned for <c>&lt;branch&gt;/&lt;instance&gt;</c>. Local path, UNC, or a configured <c>share:</c> alias.</summary>
    public string? RootPath { get; set; }

    /// <summary>Optional credential profile used to reach <see cref="RootPath"/>.</summary>
    public string? CredentialProfile { get; set; }

    /// <summary>Interval of the background sync (seconds).</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Create branch/instance DB records for folders found on disk. When false, such folders are only reported.</summary>
    public bool AutoRegister { get; set; } = true;

    /// <summary>Create the <c>old/new/reports</c> skeleton for discovered/registered instances. When false, it is only inspected.</summary>
    public bool AutoCreateFolders { get; set; } = true;
}
