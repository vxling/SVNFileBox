namespace SVNFileBox.Models;

public enum FileSvnStatus
{
    Normal,
    Modified,
    Added,
    Deleted,
    Conflicted,
    Unversioned,
    Missing,
    Replaced,
    Obstructed,
    External,
    Unknown,
    Hidden  // Used for ".." parent directory row — hides the status badge
}