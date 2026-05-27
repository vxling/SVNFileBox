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
    Incomplete,
    Hidden,         // Used for ".." parent directory row — hides the status badge
    TreeConflicted  // Tree conflict: local edit vs incoming delete/move on a directory
}