namespace DiffPdf.Messaging.Triggers;

/// <summary>
/// A cheap fingerprint of a folder's PDF content: how many files, their combined size and
/// the latest write time. Two scans with the same manifest are treated as "the same drop"
/// — so re-writing the same files (even with identical names) changes size/mtime and is
/// detected, while an unchanged folder produces an equal manifest.
/// </summary>
public sealed record FolderManifest(int FileCount, long TotalSize, long LatestWriteTicks)
{
    public static readonly FolderManifest Empty = new(0, 0, 0);

    public bool IsEmpty => FileCount == 0;
}
