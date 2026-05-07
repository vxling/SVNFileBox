namespace SVNFileBox.Models;

public enum SvnStatus
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