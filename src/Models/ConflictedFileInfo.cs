using System;

namespace SVNFileBox.Models;

public enum ConflictResolution
{
    KeepLocal,     // MineFull — keep local version, overwrite server
    AcceptServer,  // TheirsFull — discard local, accept server version
    KeepBoth,      // keep local as .local-backup-* copy, then accept server
}

public class ConflictedFileInfo
{
    public required string FilePath { get; init; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public DateTime LocalModifiedTime { get; set; }
    public DateTime ServerModifiedTime { get; set; }
    public ConflictResolution SuggestedResolution { get; set; }
    public ConflictResolution SelectedResolution { get; set; }

    /// <summary>True if local modified time is strictly later than server.</summary>
    public bool LocalIsNewer => LocalModifiedTime > ServerModifiedTime;
}
