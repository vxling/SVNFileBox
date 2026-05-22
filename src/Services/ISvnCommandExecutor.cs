#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SVNFileBox.Models;

namespace SVNFileBox.Services;

/// <summary>
/// Unified SVN command executor.
/// Callers submit commands and get results differently based on command category:
///
///   ReadOnly   → await ExecuteAsync() returns SvnQueryResult directly
///   LocalWrite → ExecuteAsync() returns Task.CompletedTask immediately;
///                result delivered via OnCommandCompleted event
///   HeavyWrite → same as LocalWrite
///
/// All SVN operations route through here. No direct SharpSvn calls outside this layer.
/// </summary>
public interface ISvnCommandExecutor
{
    // ── ReadOnly: immediate Task return ──────────────────────────────────
    Task<SvnQueryResult> ExecuteAsync(SvnCommand cmd, string path,
        string? fromPath = null, string? message = null, string? repoUrl = null,
        string? username = null, string? password = null, bool depth=false );

    // ── Update with specific sub-paths ───────────────────────────────────
    Task<SvnQueryResult> ExecuteUpdateAsync(string workingCopyPath,
        IReadOnlyList<string> updatePaths,
        string? username = null, string? password = null);

    // ── Events: background command completion ────────────────────────────
    event Action<SvnCommandResult>? OnCommandCompleted;

    // ── Lifecycle ────────────────────────────────────────────────────────
    void Start();
    void Stop();
    void Dispose();
}

/// <summary>
/// Extension methods for ISvnCommandExecutor to provide higher-level shortcuts.
/// </summary>
public static class SvnCommandExecutorExtensions
{
    public static async Task<string?> GetRepoUrlAsync(this ISvnCommandExecutor executor, string path)
    {
        var result = await executor.ExecuteAsync(SvnCommand.Info, path);
        return result.Success ? result.Value : null;
    }

    public static async Task<int> GetRevisionAsync(this ISvnCommandExecutor executor, string path)
    {
        var result = await executor.ExecuteAsync(SvnCommand.GetRevision, path);
        return result.Success && int.TryParse(result.Value, out var rev) ? rev : -1;
    }

    public static async Task<Dictionary<string, FileSvnStatus>> GetStatusAsync(
        this ISvnCommandExecutor executor, string path)
    {
        var result = await executor.ExecuteAsync(SvnCommand.Status, path);
        if (!result.Success || string.IsNullOrEmpty(result.Value))
            return new Dictionary<string, FileSvnStatus>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, FileSvnStatus>>(
                result.Value) ?? new Dictionary<string, FileSvnStatus>();
        }
        catch { return new Dictionary<string, FileSvnStatus>(); }
    }
}