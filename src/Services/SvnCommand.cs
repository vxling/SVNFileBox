#nullable enable
using System;
using System.Collections.Generic;

namespace SVNFileBox.Services;

/// <summary>
/// All SVN commands exposed to callers.
/// The executor decides internally whether to run immediately (ReadOnly)
/// or enqueue for background execution (Write/HeavyWrite).
/// Callers just call the command — they don't need to know the category.
///
/// Commands:
///   ReadOnly    — executed immediately, return directly via Task
///   LocalWrite  — enqueued, executed in background, result via event
///   HeavyWrite  — enqueued, executed in background, result via event
/// </summary>
public enum SvnCommand
{
    // ── ReadOnly: immediate execution, no queue ──────────────────────────
    /// <summary>svn info — get repo info (URL, revision, etc.)</summary>
    Info,
    /// <summary>svn status — local status of files</summary>
    Status,
    /// <summary>Get working copy revision number.</summary>
    GetRevision,
    /// <summary>Get HEAD revision of remote repo (requires credentials if private).</summary>
    GetHeadRevision,
    /// <summary>Get list of conflicted files.</summary>
    GetConflictedFiles,
    /// <summary>Get last-changed time from SVN metadata.</summary>
    GetLastChangedTime,
    /// <summary>Check if path is under version control.</summary>
    IsVersioned,
    /// <summary>Check if path is a valid working copy.</summary>
    IsValidWorkingCopy,
    /// <summary>Connection probe — test repo URL reachability.</summary>
    TestConnection,
    /// <summary>Get pending server update paths (files with remote changes).</summary>
    GetServerUpdatePaths,

    // ── LocalWrite: enqueued, background execution ─────────────────────────
    /// <summary>svn add — add file/dir to version control.</summary>
    Add,
    /// <summary>svn delete — remove file/dir from version control.</summary>
    Delete,
    /// <summary>svn move — rename/move file/dir.</summary>
    Move,
    /// <summary>svn revert — discard local changes.</summary>
    Revert,
    /// <summary>svn resolve — resolve a conflicted file.</summary>
    Resolve,
    /// <summary>Break a write lock on a file (admin operation).</summary>
    BreakLock,

    // ── HeavyWrite: enqueued, background execution ───────────────────────
    /// <summary>svn commit — commit local changes to server.</summary>
    Commit,
    /// <summary>svn update — update local WC from server.</summary>
    Update,
    /// <summary>svn checkout — checkout a remote repo to local path.</summary>
    Checkout,
}

/// <summary>
/// Category of a command, determining its execution path.
/// </summary>
public enum SvnCommandCategory
{
    /// <summary>Immediate execution, no queue.</summary>
    ReadOnly,
    /// <summary>Enqueued, background execution via _writeSemaphore.</summary>
    LocalWrite,
    /// <summary>Enqueued, background execution via _writeSemaphore + activity watchdog.</summary>
    HeavyWrite,
}

/// <summary>
/// Result of a ReadOnly command (immediate Task return).
/// </summary>
public readonly struct SvnQueryResult
{
    public bool Success { get; init; }
    public string? Value { get; init; }
    public string? Error { get; init; }

    public SvnQueryResult() { }

    public static SvnQueryResult Ok(string value) => new() { Success = true, Value = value };
    public static SvnQueryResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Result of a background command (delivered via event).
/// </summary>
public readonly struct SvnCommandResult
{
    public SvnCommand Command { get; init; }
    public string Path { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int? Revision { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.Now;

    public SvnCommandResult() { }

    public static SvnCommandResult Ok(SvnCommand cmd, string path, int? rev = null) =>
        new() { Command = cmd, Path = path, Success = true, Revision = rev };
    public static SvnCommandResult Fail(SvnCommand cmd, string path, string error) =>
        new() { Command = cmd, Path = path, Success = false, Error = error };
}

/// <summary>
/// A pending SVN command ready to be executed by the background worker.
/// </summary>
public readonly struct SvnCommandItem
{
    public SvnCommand Command { get; init; }
    public string Path { get; init; }
    public string? FromPath { get; init; }
    public string? Message { get; init; }       // for Commit
    public string? RepoUrl { get; init; }       // for Checkout
    public string? Username { get; init; }
    public string? Password { get; init; }
    /// <summary>For Update: specific sub-paths to update (null = update whole WC).</summary>
    public IReadOnlyList<string>? UpdatePaths { get; init; }

    public static SvnCommandItem New(
        SvnCommand cmd, string path, string? fromPath = null,
        string? message = null, string? repoUrl = null,
        string? user = null, string? pwd = null,
        IReadOnlyList<string>? updatePaths = null) =>
        new()
        {
            Command = cmd, Path = path, FromPath = fromPath,
            Message = message, RepoUrl = repoUrl,
            Username = user, Password = pwd,
            UpdatePaths = updatePaths
        };
}