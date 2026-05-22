#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SVNFileBox.Models;
using SVNFileBox.Services;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// 全局多仓管理器.
///
/// 职责：
///   - 持有所有 RepoManager 实例列表
///   - 处理仓库创建（CheckoutWindow / AddLocalRepoWindow 回调后创建 RepoManager）
///   - 处理仓库切换（Focus / Dismiss）
///   - 处理仓库删除
///   - 转发来自各 RepoManager 的事件到 MainWindow（ConflictDetected / SyncNotification / CredentialExpired）
///
/// 所有事件回调统一从 RepoGlobalManager 直连到 MainWindow，
/// MainWindow 不再直接订阅 RepoManager 或其内部服务的事件。
/// </summary>
public class RepoGlobalManager : IDisposable
{
    private readonly List<RepoManager> _managers = new();
    private RepoManager? _activeManager;
    private bool _isDisposed;

    /// <summary>所有已创建的 RepoManager。</summary>
    public IReadOnlyList<RepoManager> Managers => _managers.AsReadOnly();

    /// <summary>当前处于前台（Focused）的 RepoManager。</summary>
    public RepoManager? ActiveManager => _activeManager;

    /// <summary>当前仓库是否为空（没有任何已注册仓库）。</summary>
    public bool IsEmpty => _managers.Count == 0;

    // ─────────────────────────────────────────────────────────────────────────
    // 事件：统一转发给 MainWindow
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 任何 RepoManager 上的冲突检测到时触发。
    /// 触发时由 RepoGlobalManager 弹出 ConflictWindow。
    /// </summary>
    public event EventHandler<List<ConflictedFileInfo>>? ConflictDetected;

    /// <summary>任何 RepoManager 的同步通知。</summary>
    public event EventHandler<string>? SyncNotification;

    /// <summary>任何 RepoManager 的 SVN 凭据过期。</summary>
    public event EventHandler<(string repoName, string repoUrl, string username)>? CredentialExpired;

    /// <summary>
    /// 更新当前活跃仓库的凭据（CredentialExpired 回调后由用户输入新凭据触发）。
    /// </summary>
    public void UpdateCredential(string username, string password)
    {
        var manager = _activeManager;
        if (manager == null) return;
        manager.UpdateCredential(username, password);
    }

    /// <summary>
    /// 当前活跃 RepoManager 的文件列表变更（触发表格刷新）。
    /// </summary>
    public event EventHandler? FilesChanged;

    /// <summary>
    /// 活跃仓库切换完成后触发，携带新活跃 manager 的 Executor。
    /// 供 MainWindow 通知 FileCopier 等组件切换 executor。
    /// </summary>
    public event EventHandler<ISvnCommandExecutor>? ActiveExecutorChanged;

    public RepoGlobalManager()
    {
        Log.Information("[RepoGlobalManager] Created");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 仓库创建
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 CheckoutWindow 的结果创建一个新 RepoManager（不切换）。
    /// 调用方完成创建后自行调用 SwitchToAsync 切换到它。
    /// </summary>
    public async Task<RepoManager?> CreateAsync(
        string repoName,
        string repoPath,
        string repoUrl,
        string username,
        string password)
    {
        var repo = new Repository
        {
            Name = repoName,
            Path = repoPath,
            Url = repoUrl,
            Username = username,
            Password = password,
            IsActive = true,
            RepositoryType = RepositoryType.Network
        };

        var manager = new RepoManager(repo);

        // 执行 Checkout（远程仓库 → 本地路径），使用 manager 自带的 Executor
        var coResult = await manager.Executor.ExecuteAsync(
            SvnCommand.Checkout, repoPath,
            repoUrl: repoUrl, username: username, password: password);

        if (!coResult.Success)
        {
            Log.Error("[RepoGlobalManager] Checkout failed for {Name}: {Error}", repoName, coResult.Error ?? "unknown");
            manager.Dispose();
            return null;
        }

        Log.Information("[RepoGlobalManager] Checkout succeeded for {Name} at {Path}", repoName, repoPath);

        _managers.Add(manager);
        return manager;
    }

    /// <summary>
    /// 从 AddLocalRepoWindow 的结果创建（仓库已存在本地，无需 Checkout）。
    /// </summary>
    public RepoManager CreateLocal(Repository repo)
    {
        var manager = new RepoManager(repo);
        _managers.Add(manager);
        return manager;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 仓库切换
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 切换到指定 RepoManager：
    ///   1. 对当前活跃 RepoManager 调用 DismissAsync（排空队列）
    ///   2. 将 newManager Focus（启动 SyncService、绑定事件）
    /// </summary>
    public async Task SwitchToAsync(RepoManager newManager)
    {
        if (_isDisposed) return;
        if (!_managers.Contains(newManager)) return;

        // 1. Dismiss 当前活跃仓库
        if (_activeManager != null && _activeManager != newManager)
        {
            var previous = _activeManager;
            _activeManager = null;
            await previous.DismissAsync();
        }

        // 2. Focus 新仓库
        _activeManager = newManager;
        newManager.Focus();

        // 3. 通知外界切换 executor（FileCopier 等）
        ActiveExecutorChanged?.Invoke(this, newManager.Executor);

        // 4. 绑定新仓库事件到全局事件
        BindManagerEvents(newManager);

        Log.Information("[RepoGlobalManager] Switched to {Name}", newManager.Repository.Name);
    }

    private void BindManagerEvents(RepoManager manager)
    {
        manager.FilesChanged += (s, _) => FilesChanged?.Invoke(this, EventArgs.Empty);
        manager.SyncNotification += (s, msg) => SyncNotification?.Invoke(this, msg);
        manager.ConflictDetected += (s, conflicts) => ConflictDetected?.Invoke(this, conflicts);
        manager.CredentialExpired += (s, info) => CredentialExpired?.Invoke(this, info);
    }

    private void UnbindManagerEvents(RepoManager manager)
    {
        manager.FilesChanged -= (s, _) => FilesChanged?.Invoke(this, EventArgs.Empty);
        manager.SyncNotification -= (s, msg) => SyncNotification?.Invoke(this, msg);
        manager.ConflictDetected -= (s, conflicts) => ConflictDetected?.
            Invoke(this, conflicts);
        manager.CredentialExpired -= (s, info) => CredentialExpired?.Invoke(this, info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 仓库删除
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 删除指定 RepoManager：
    ///   1. 若它是当前活跃仓库，先 Dismiss 并解绑定事件
    ///   2. 从列表中移除
    ///   3. 若列表仍有剩余，切换到第一个；否则 _activeManager = null
    /// </summary>
    public void Remove(RepoManager manager)
    {
        if (!_managers.Contains(manager)) return;

        Log.Information("[RepoGlobalManager] Removing {Name}", manager.Repository.Name);

        // 若为当前活跃仓库，先 dismiss 并解绑定
        if (_activeManager == manager)
        {
            _activeManager = null;
            UnbindManagerEvents(manager);
            _ = manager.DismissAsync();
        }

        _managers.Remove(manager);
        manager.Dispose();

        // 切换到第一个剩余仓库
        if (_managers.Count > 0)
        {
            var first = _managers[0];
            // 同步切换，不用 await（Dismiss 已完成，Focus 很快）
            _activeManager = first;
            first.Focus();
            ActiveExecutorChanged?.Invoke(this, first.Executor);
            BindManagerEvents(first);
            Log.Information("[RepoGlobalManager] Switched to first remaining repo: {Name}", first.Repository.Name);
        }
        else
        {
            _activeManager = null;
            ActiveExecutorChanged?.Invoke(this, null!);
            Log.Information("[RepoGlobalManager] All repos removed, empty state");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 程序关闭
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 关闭所有仓库（立即终止，不等待队列排空）。
    /// 在程序退出时调用。
    /// </summary>
    public void ShutdownAll()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var manager in _managers)
        {
            manager.Shutdown();
            UnbindManagerEvents(manager);
        }

        _managers.Clear();
        _activeManager = null;

        Log.Information("[RepoGlobalManager] ShutdownAll complete");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 程序启动时：从 ConfigService 加载已有仓库
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从已有的 Repository 列表恢复 RepoManager 实例（不切换到任何仓库）。
    /// 不会 Focus，只有用户选择仓库时才 Focus。
    /// </summary>
    public void RestoreFromConfig(IEnumerable<Repository> repositories)
    {
        foreach (var repo in repositories)
        {
            var manager = new RepoManager(repo);
            _managers.Add(manager);
            Log.Information("[RepoGlobalManager] Restored repo from config: {Name}", repo.Name);
        }
    }

    /// <summary>
    /// 从 ConfigService 恢复并切换到最后活跃的仓库。
    /// </summary>
    public async Task RestoreAndSwitchToLastActiveAsync(
        IEnumerable<Repository> repositories,
        string? lastActiveName)
    {
        RestoreFromConfig(repositories);

        if (string.IsNullOrEmpty(lastActiveName))
        {
            if (_managers.Count > 0)
                await SwitchToAsync(_managers[0]);
            return;
        }

        var last = _managers.FirstOrDefault(m => m.Repository.Name == lastActiveName)
                   ?? _managers.FirstOrDefault();
        if (last != null)
            await SwitchToAsync(last);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        ShutdownAll();
        GC.SuppressFinalize(this);
    }
}