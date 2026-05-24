#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SharpSvn;
using Serilog;
using SVNFileBox.Models;

namespace SVNFileBox.Services;

/// <summary>
/// Unified SVN command executor with two-tier background queue.
/// 
/// Execution model:
///   ReadOnly    → direct execution on caller's thread, result via Task
///   LocalWrite  → _localWriteQueue Channel → drained in a "small loop" before each HeavyWrite
///   HeavyWrite  → _heavyWriteQueue Channel → executed one at a time, after all LocalWrite drain
/// 
/// Worker loop pattern (big loop):
///   while (!cancelled)
///     small loop: drain all LocalWrite items (non-blocking)
///     wait for one HeavyWrite item (blocking)
///     execute the HeavyWrite item
/// 
/// Results are delivered via OnCommandCompleted event.
/// </summary>
public sealed class SvnCommandExecutor : ISvnCommandExecutor, IDisposable
{
    private readonly SvnService _svnService = new();
    private Channel<SvnCommandItem>? _localWriteQueue;
    private Channel<SvnCommandItem>? _heavyWriteQueue;
    private readonly ConcurrentDictionary<string, SvnCommandItem> _dedup = new();
    private CancellationTokenSource _cts = new();
    private Task? _workerTask;
    private bool _drainMode;
    private readonly TaskCompletionSource _drainTcs = new();

    private static readonly SvnCommandCategory[] CommandCategoryMap = BuildCategoryMap();

    private static SvnCommandCategory[] BuildCategoryMap()
    {
        var map = new SvnCommandCategory[(int)SvnCommand.Checkout + 1];

        map[(int)SvnCommand.Info]                = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.Status]              = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.GetRevision]         = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.GetHeadRevision]     = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.GetConflictedFiles]  = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.GetLastChangedTime] = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.IsVersioned]         = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.IsValidWorkingCopy]  = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.TestConnection]       = SvnCommandCategory.ReadOnly;
        map[(int)SvnCommand.GetServerUpdatePaths] = SvnCommandCategory.ReadOnly;

        map[(int)SvnCommand.Add]       = SvnCommandCategory.LocalWrite;
        map[(int)SvnCommand.Delete]    = SvnCommandCategory.LocalWrite;
        map[(int)SvnCommand.Move]      = SvnCommandCategory.LocalWrite;
        map[(int)SvnCommand.Revert]    = SvnCommandCategory.LocalWrite;
        map[(int)SvnCommand.Resolve]   = SvnCommandCategory.LocalWrite;
        map[(int)SvnCommand.BreakLock] = SvnCommandCategory.LocalWrite;

        map[(int)SvnCommand.Commit   ] = SvnCommandCategory.HeavyWrite;
        map[(int)SvnCommand.Update   ] = SvnCommandCategory.HeavyWrite;
        map[(int)SvnCommand.Checkout ] = SvnCommandCategory.HeavyWrite;

        return map;
    }

    public event Action<SvnCommandResult>? OnCommandCompleted;

    public SvnCommandExecutor()
    {
        _localWriteQueue = Channel.CreateBounded<SvnCommandItem>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _heavyWriteQueue = Channel.CreateBounded<SvnCommandItem>(new BoundedChannelOptions(100)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        // Start the worker loop immediately so it's ready to accept commands
        Start();
    }

    /// <summary>
    /// Starts the background worker loop.
    /// </summary>
    public void Start()
    {
        if (_workerTask?.IsCompleted == false) return;
        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
        _workerTask = Task.Run(WorkerLoop, _cts.Token);
        Log.Information("[SvnCommandExecutor] Started");
    }

    /// <summary>
    /// Stops the worker and signals cancellation.
    /// Does NOT close the Channels — they are recreated on next Start().
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
        Log.Information("[SvnCommandExecutor] Stop requested");
    }

    /// <summary>
    /// Drains the queue: stops accepting new commands and completes all pending HeavyWrite
    /// tasks before shutting down. Use when switching repositories so in-flight operations
    /// (e.g. Update, Commit) can finish gracefully.
    /// Returns a Task that completes when the drain is done (all tasks processed or cancelled).
    /// </summary>
    public Task DrainAsync()
    {
        _drainMode = true;
        _cts.Cancel();
        Log.Information("[SvnCommandExecutor] Drain mode — will complete pending tasks before shutdown");
        return _drainTcs.Task;
    }

    /// <summary>
    /// Submit a command for execution.
    ///   ReadOnly  → executed immediately, result returned via Task.
    ///   LocalWrite → enqueued to _localWriteQueue, result via OnCommandCompleted.
    ///   HeavyWrite → enqueued to _heavyWriteQueue, result via OnCommandCompleted.
    /// </summary>
    public async Task<SvnQueryResult> ExecuteAsync(
        SvnCommand cmd,
        string path,
        string? fromPath = null,
        string? message = null,
        string? repoUrl = null,
        string? username = null,
        string? password = null,
        bool depth = false,
        SharpSvn.SvnAccept? accept = null)
    {
        var category = CommandCategoryMap[(int)cmd];

        if (category == SvnCommandCategory.ReadOnly)
        {
            return await ExecuteReadOnlyAsync(cmd, path, fromPath, message, repoUrl, username, password, depth);
        }

        var item = SvnCommandItem.New(cmd, path, fromPath, message, repoUrl, username, password, accept: accept);

        if (category == SvnCommandCategory.LocalWrite)
        {
            // Fire-and-forget — result via OnCommandCompleted
            if (!TryEnqueueLocalWrite(item)) return new SvnQueryResult { Success = true };
            await _localWriteQueue!.Writer.WriteAsync(item, _cts.Token);
            return new SvnQueryResult { Success = true };
        }

        // HeavyWrite — enqueue with deduplication check
        if (!TryEnqueueHeavyWrite(item)) return new SvnQueryResult { Success = true };
        var tcs = new TaskCompletionSource<SvnQueryResult>();
        void handler(SvnCommandResult result)
        {
            if (result.Command == item.Command && result.Path == item.Path)
            {
                OnCommandCompleted -= handler;
                tcs.TrySetResult(new SvnQueryResult { Success = result.Success, Value = result.Revision?.ToString(), Error = result.Error });
            }
        }
        OnCommandCompleted += handler;
        await _heavyWriteQueue!.Writer.WriteAsync(item, _cts.Token);
        return await tcs.Task;
    }

    /// <summary>
    /// Enqueues Update commands for multiple sub-paths and returns all result Tasks.
    /// </summary>
    public Task<SvnQueryResult> ExecuteUpdateAsync(
        string workingCopyPath,
        IReadOnlyList<string> updatePaths,
        string? username = null,
        string? password = null)
    {
        var item = SvnCommandItem.New(SvnCommand.Update, workingCopyPath,
            updatePaths: updatePaths, user: username, pwd: password);

        if (!TryEnqueueHeavyWrite(item))
            return Task.FromResult(new SvnQueryResult { Success = false, Error = "Skipped by deduplication" });

        var tcs = new TaskCompletionSource<SvnQueryResult>();
        void handler(SvnCommandResult result)
        {
            if (result.Command == item.Command && result.Path == item.Path)
            {
                OnCommandCompleted -= handler;
                tcs.TrySetResult(new SvnQueryResult { Success = result.Success, Value = result.Revision?.ToString(), Error = result.Error });
            }
        }
        OnCommandCompleted += handler;
        _ = _heavyWriteQueue!.Writer.WriteAsync(item, _cts.Token);
        return tcs.Task;
    }

    // ── Deduplication ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns false if the item was absorbed by dedup (e.g. Delete cancels pending Add).
    /// </summary>
    private bool TryEnqueueLocalWrite(SvnCommandItem item)
    {
        var key = item.Path;

        if (_dedup.TryGetValue(key, out var existing))
            Log.Debug("[SvnCommandExecutor] Dedup: {OldCmd} → {NewCmd} for {Path}", existing.Command, item.Command, key);

        // Simple rule: keep only the latest operation for this path.
        // Channel FIFO order ensures correct execution sequence.
        _dedup[key] = item;
        return true;
    }

    /// <summary>
    /// Returns false if the item was deduplicated away (e.g. duplicate Commit on same path).
    /// </summary>
    private bool TryEnqueueHeavyWrite(SvnCommandItem item)
    {
        if (item.Command == SvnCommand.Checkout)
        {
            // Checkout: always allowed (different repo)
            _dedup[item.Path] = item;
            return true;
        }

        // For Update commands, include UpdatePaths in the dedup key so that
        // different sub-directory updates don't deduplicate each other.
        var key = item.Path;
        if (item.Command == SvnCommand.Update && item.UpdatePaths != null && item.UpdatePaths.Count > 0)
        {
            // Build a composite key: Path + "|" + sorted UpdatePaths (length-limited)
            var pathsStr = string.Join(",", item.UpdatePaths.OrderBy(p => p));
            const int MAX_KEY_LEN = 500;
            if (key.Length + pathsStr.Length + 1 > MAX_KEY_LEN)
                key = key[..(MAX_KEY_LEN - pathsStr.Length - 2)] + "|" + pathsStr;
            else
                key = key + "|" + pathsStr;
        }

        if (item.Command == SvnCommand.Commit)
        {
            // Commit: skip if there's already a pending Commit/Update on the same path
            if (_dedup.TryGetValue(key, out var existing)
                && (existing.Command == SvnCommand.Commit || existing.Command == SvnCommand.Update))
            {
                Log.Debug("[SvnCommandExecutor] Dedup: skipping duplicate {Cmd} on {Path}", item.Command, key);
                return false;
            }
            _dedup[key] = item;
            return true;
        }

        // Update: skip if there's already a pending Update/Commit on the same path
        if (_dedup.TryGetValue(key, out var existing2)
            && (existing2.Command == SvnCommand.Update || existing2.Command == SvnCommand.Commit))
        {
            Log.Debug("[SvnCommandExecutor] Dedup: skipping duplicate {Cmd} on {Path}", item.Command, key);
            return false;
        }
        _dedup[key] = item;
        return true;
    }

    // Remove item from dedup dict after worker executes it (so same file can be re-queued later)
    private void RemoveFromDedup(SvnCommandItem item)
    {
        var key = GetDedupKey(item);
        _dedup.TryRemove(key, out _);
    }

    private string GetDedupKey(SvnCommandItem item)
    {
        var key = item.Path;
        if (item.Command == SvnCommand.Update && item.UpdatePaths != null && item.UpdatePaths.Count > 0)
        {
            var pathsStr = string.Join(",", item.UpdatePaths.OrderBy(p => p));
            const int MAX_KEY_LEN = 500;
            if (key.Length + pathsStr.Length + 1 > MAX_KEY_LEN)
                key = key[..(MAX_KEY_LEN - pathsStr.Length - 2)] + "|" + pathsStr;
            else
                key = key + "|" + pathsStr;
        }
        return key;
    }

    // ─────────────────────────────────────────────────────────────────
    // Worker loop — big loop: drain LocalWrite → execute one HeavyWrite → repeat
    // ─────────────────────────────────────────────────────────────────

    private async Task WorkerLoop()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested || _drainMode)
            {
                // ── Drain all LocalWrite items (non-blocking) ──
                while (_localWriteQueue!.Reader.TryRead(out var localItem))
                {
                    try { await ProcessItemAsync(localItem); }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[SvnCommandExecutor] Worker error {Cmd} {Path}",
                            localItem.Command, localItem.Path);
                        OnCommandCompleted?.Invoke(SvnCommandResult.Fail(localItem.Command, localItem.Path, ex.Message));
                    }
                }

                // ── Try to grab one HeavyWrite (non-blocking unless draining) ──
                if (_heavyWriteQueue!.Reader.TryRead(out var heavyItem))
                {
                    try { await ProcessItemAsync(heavyItem); }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[SvnCommandExecutor] Worker error {Cmd} {Path}",
                            heavyItem.Command, heavyItem.Path);
                        OnCommandCompleted?.Invoke(SvnCommandResult.Fail(heavyItem.Command, heavyItem.Path, ex.Message));
                    }
                }
                else if (_drainMode)
                {
                    // In drain mode: stop immediately when queue is empty — no waiting.
                    Log.Information("[SvnCommandExecutor] Drain complete, worker loop ending");
                    break;
                }
                else
                {
                    // Both queues empty — sleep briefly before polling again.
                    // Do NOT use ReadAsync here; we must not block so LocalWrite stays responsive.
                    try { await Task.Delay(50, _cts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (OperationCanceledException)   { /* shutdown — expected */ }
        catch (InvalidOperationException ex) { Log.Warning(ex, "[SvnCommandExecutor] Worker loop InvalidOperationException"); }
        catch (Exception ex)                { Log.Error(ex, "[SvnCommandExecutor] Worker loop crashed"); }
        finally
        {
            _drainTcs.TrySetResult(); // unblock DrainAsync() callers
            Log.Information("[SvnCommandExecutor] Worker loop ended");
        }
    }

    private async Task ProcessItemAsync(SvnCommandItem item)
    {
        bool success = false;
        string? error = null;
        int? revision = null;

        try
        {
            switch (item.Command)
            {
                // ── LocalWrite ──────────────────────────────────────────
                case SvnCommand.Add:
                    success = await _svnService.AddPathAsync(item.Path);
                    error = success ? null : "svn add failed";
                    break;

                case SvnCommand.Delete:
                    success = await _svnService.DeleteAsync(item.Path);
                    error = success ? null : "svn delete failed";
                    break;

                case SvnCommand.Move:
                    success = await _svnService.MoveAsync(item.FromPath ?? "", item.Path);
                    error = success ? null : "svn move failed";
                    break;

                case SvnCommand.Revert:
                    success = await _svnService.RevertAsync(item.Path);
                    error = success ? null : "svn revert failed";
                    break;

                case SvnCommand.Resolve:
                    success = await _svnService.ResolveAsync(item.Path, item.Accept ?? SharpSvn.SvnAccept.Working);
                    error = success ? null : "svn resolve failed";
                    break;

                case SvnCommand.BreakLock:
                    success = await _svnService.BreakWriteLockAsync(item.Path);
                    error = success ? null : "svn break-lock failed";
                    break;

                // ── HeavyWrite ─────────────────────────────────────────
                case SvnCommand.Commit:
                    success = await _svnService.CommitAsync(item.Path, item.Message ?? "");
                    error = success ? null : "svn commit failed";
                    break;

                case SvnCommand.Update:
                    success = item.UpdatePaths != null
                        ? await _svnService.UpdateAsync(item.UpdatePaths)
                        : await _svnService.UpdateAsync(item.Path);
                    error = success ? null : "svn update failed";
                    break;

                case SvnCommand.Checkout:
                    var (output, exitCode, err) = await _svnService.CheckoutAsync(
                        item.RepoUrl ?? "", item.Path, item.Username, item.Password);
                    success = exitCode == 0;
                    error = success ? null : err;
                    if (success && int.TryParse(output, out var rev)) revision = rev;
                    break;
            }
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
        }

        var result = success
            ? SvnCommandResult.Ok(item.Command, item.Path, revision)
            : SvnCommandResult.Fail(item.Command, item.Path, error ?? "unknown error");

        RemoveFromDedup(item);
        OnCommandCompleted?.Invoke(result);
    }

    // ─────────────────────────────────────────────────────────────────
    // ReadOnly execution
    // ─────────────────────────────────────────────────────────────────

    private async Task<SvnQueryResult> ExecuteReadOnlyAsync(
        SvnCommand cmd,
        string path,
        string? fromPath,
        string? message,
        string? repoUrl,
        string? username,
        string? password,
        bool depth = false)
    {
        try
        {
            return cmd switch
            {
                SvnCommand.Info =>
                    SvnQueryResult.Ok((await _svnService.GetRepoUrlAsync(path))),

                SvnCommand.Status =>
                    SvnQueryResult.Ok(await _svnService.GetStatusAsync(path, depth)
                        .ContinueWith(t => System.Text.Json.JsonSerializer.Serialize(
                            t.Result, typeof(Dictionary<string, FileSvnStatus>)))),

                SvnCommand.GetRevision =>
                    SvnQueryResult.Ok((await _svnService.GetWorkingCopyRevisionAsync(path)).ToString()),

                SvnCommand.GetHeadRevision =>
                    SvnQueryResult.Ok((await _svnService.GetHeadRevisionAsync(repoUrl ?? path, username, password)).ToString()),

                SvnCommand.GetConflictedFiles =>
                    SvnQueryResult.Ok(string.Join(";", await _svnService.GetConflictedFilesAsync(path))),

                SvnCommand.GetLastChangedTime =>
                    SvnQueryResult.Ok((await _svnService.GetLastChangedTimeAsync(path)).ToString("O")),

                SvnCommand.IsVersioned =>
                    SvnQueryResult.Ok(_svnService.IsVersioned(path) ? "true" : "false"),

                SvnCommand.IsValidWorkingCopy =>
                    SvnQueryResult.Ok(_svnService.IsValidWorkingCopy(path) ? "true" : "false"),

                SvnCommand.TestConnection =>
                    await ExecuteTestConnectionAsync(repoUrl ?? path, username, password),

                SvnCommand.GetServerUpdatePaths =>
                    SvnQueryResult.Ok(string.Join(";", await _svnService.GetServerUpdatePathsAsync(path))),

                _ => SvnQueryResult.Fail($"Unknown ReadOnly command: {cmd}")
            };
        }
        catch (Exception ex)
        {
            return SvnQueryResult.Fail(ex.Message);
        }
    }

    private async Task<SvnQueryResult> ExecuteTestConnectionAsync(string url, string? username, string? password)
    {
        var (result, errorMsg) = await _svnService.TestConnectionAsync(url, username, password);
        var desc = result.ToString();
        return result == SvnService.SvnConnectResult.Success
            ? new SvnQueryResult { Success = true }
            : new SvnQueryResult { Success = false, Error = errorMsg != null ? $"{desc}: {errorMsg}" : desc };
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}