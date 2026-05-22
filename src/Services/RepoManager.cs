#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SVNFileBox.Models;
using SVNFileBox.Services;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// 单个仓库的完整生命周期管理器.
///
/// 持有该仓库的所有资源：
///   - Repository          仓库数据模型
///   - SvnCommandExecutor  SVN 命令执行器
///   - FileWatcherService  文件系统监听（本地变化检测）
///   - SvnService          SVN 底层操作（Tier1/2/3）
///   - SyncService         双向同步引擎
///
/// Focus / Dismiss / Shutdown 三个生命周期方法由 RepoGlobalManager 调用，
/// 实现了在多仓之间切换时的完整挂起/恢复/终止语义。
/// </summary>
public class RepoManager : IDisposable
{
    private readonly object _lock = new();
    private bool _isDisposed;

    public Repository Repository { get; }

    public SvnCommandExecutor Executor { get; }
    public FileWatcherService FileWatcher { get; }
    public SvnService SvnService { get; }
    public SyncService SyncService { get; }

    /// <summary>
    /// RepoManager 的三种运行状态。
    ///   None       — 初始态或已 Dismiss/Shutdown
    ///   Focused    — 正在前台运行（SyncService 已 StartSync，事件已绑定）
    ///   Dismissed — 已调用 Dismiss，正在等待队列排空，未Shutdown
    /// </summary>
    public RepoState State { get; private set; } = RepoState.None;

    // ---- 暴露给 RepoGlobalManager 的事件 ----

    /// <summary>转发自 SyncService.FilesChanged → 触发 UI 刷新。</summary>
    public event EventHandler? FilesChanged;

    /// <summary>转发自 SyncService.SyncNotification。</summary>
    public event EventHandler<string>? SyncNotification;

    /// <summary>转发自 SyncService.ConflictDetected → 触发冲突处理窗口。</summary>
    public event EventHandler<List<ConflictedFileInfo>>? ConflictDetected;

    /// <summary>
    /// 转发自 SvnService（通过 RepositoryContext.CredentialExpired）。
    /// 通知 UI 弹出一个凭据更新窗口。
    /// </summary>
    public event EventHandler<(string repoName, string repoUrl, string username)>? CredentialExpired;

    /// <summary>
    /// 更新此仓库的 SVN 凭据（凭据更新窗口回调后调用）。
    /// </summary>
    public void UpdateCredential(string username, string password)
    {
        Repository.Username = username;
        Repository.Password = password;
        Log.Information("[RepoManager] Credential updated for {Name}", Repository.Name);
    }

    public RepoManager(Repository repository)
    {
        Repository = repository;

        Executor = new SvnCommandExecutor();
        Executor.Start();

        FileWatcher = new FileWatcherService();

        SvnService = new SvnService();

        SyncService = new SyncService(
            executor: Executor,
            recordService: SyncRecordService.Instance,
            svnService: SvnService,
            fileWatcher: FileWatcher,
            repository: repository);

        // 事件桥接：SyncService → RepoManager
        SyncService.FilesChanged += (s, _) => FilesChanged?.Invoke(this, EventArgs.Empty);
        SyncService.SyncNotification += (s, msg) => SyncNotification?.Invoke(this, msg);
        SyncService.ConflictDetected += (s, conflicts) => ConflictDetected?.Invoke(this, conflicts);

        // SvnService 实例事件桥接到 SyncService（用于进度跟踪）
        SvnService.FileTransferActivity += (path, action) =>
        {
            SyncService.RecordFileTransfer(path, action);
        };


        // 转发 SvnService.CredentialExpired → RepoManager.CredentialExpired
        SvnService.CredentialExpired += path =>
        {
            CredentialExpired?.Invoke(this, (Repository.Name, Repository.Url ?? "", Repository.Username ?? ""));
        };

        Log.Information("[RepoManager] Created for {Name} at {Path}", repository.Name, repository.Path);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 公开生命周期方法
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Focus：将此仓库切换到前台。
    ///
    /// 执行操作：
    ///   1. 启动 FileWatcher（监听本地文件变化）
    ///   2. 启动 SyncService（开启轮询 Timer、触发立即扫描）
    ///   3. 将 CredentialExpired 绑定到 FileWatcher
    ///
    /// 调用者：RepoGlobalManager.SwitchTo()
    /// </summary>
    public void Focus()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            if (State == RepoState.Focused) return;

            Log.Information("[RepoManager] Focusing {Name}", Repository.Name);

            FileWatcher.FilesChanged += OnFileWatcherFilesChanged;
            FileWatcher.StartWatching(Repository.Path);

            SyncService.StartSync(Repository);

            State = RepoState.Focused;
            Log.Information("[RepoManager] Focused {Name}", Repository.Name);
        }
    }

    /// <summary>
    /// Dismiss：将此仓库从前台挂起。
    ///
    /// 执行操作：
    ///   1. 停止 FileWatcher（不再接收本地变化事件）
    ///   2. 停止 SyncService 的轮询 Timer
    ///   3. 调用 DrainAsync() 等待队列中的任务（ScanAndCommit/HeavyWrite）完成
    ///   4. 解绑定 FileWatcher 事件
    ///
    /// 调用者：RepoGlobalManager.SwitchTo()（切换到另一个仓库时，先对原仓库调用 Dismiss）
    /// </summary>
    public async Task DismissAsync()
    {
        RepoState previousState;
        lock (_lock)
        {
            previousState = State;
            if (State == RepoState.Dismissed || State == RepoState.None) return;
            State = RepoState.Dismissed;
        }

        Log.Information("[RepoManager] Dismissing {Name} (previous state: {Prev})", Repository.Name, previousState);

        // 1. 停止 FileWatcher
        FileWatcher.StopWatching();
        FileWatcher.FilesChanged -= OnFileWatcherFilesChanged;

        // 2. 停止 SyncService 轮询（但队列中已入队的任务会继续执行）
        SyncService.StopSync();

        // 3. 等待队列排空（最多 30s，超时则放弃）
        await SyncService.DrainAsync();

        Log.Information("[RepoManager] Dismissed {Name}", Repository.Name);
    }

    /// <summary>
    /// Shutdown：立即终止此仓库的所有活动。
    ///
    /// 执行操作：
    ///   1. 发起 Cancel 信号，立即中断正在等待的 SVN 操作
    ///   2. 停止 SyncService（StopSync 内部会 Cancel 所有挂起任务）
    ///   3. 停止 FileWatcher
    ///   4. 释放 Executor（强制 Stop）
    ///
    /// 调用者：RepoGlobalManager.ShutdownAll()（程序退出时）
    /// </summary>
    public void Shutdown()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            Log.Information("[RepoManager] Shutting down {Name}", Repository.Name);
        }

        SyncService.StopSync();
        FileWatcher.StopWatching();
        Executor.Stop();

        lock (_lock)
        {
            State = RepoState.None;
            _isDisposed = true;
        }

        Log.Information("[RepoManager] Shut down {Name}", Repository.Name);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 内部事件处理
    // ─────────────────────────────────────────────────────────────────────────

    private void OnFileWatcherFilesChanged(object? sender, string[] files)
    {
        // FileWatcher 检测到变化 → 通知 SyncService 入队操作
        foreach (var file in files)
        {
            SyncService.EnqueueFileChangeAsync(file);
        }
        // 触发立即同步（不再等 FullSync 定时器）
        _ = SyncService.SyncNowAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        Shutdown();
        FileWatcher.Dispose();
        Executor.Dispose();
        SyncService.Dispose();
        GC.SuppressFinalize(this);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RepoState
// ─────────────────────────────────────────────────────────────────────────────

public enum RepoState
{
    None,
    Focused,
    Dismissed,
}