#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SVNFileBox.Models;
using SVNFileBox.Services;
using Serilog;

namespace SVNFileBox.ViewModels;

public enum SyncStatusType { Idle, Syncing, Success, Failed }

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ConfigService _configService;
    private System.Timers.Timer? _statusClearTimer;
    private string _lastPersistentStatus = "Ready";

    /// <summary>
    /// 全局仓库管理器 — 所有仓库的生命周期由它管理。
    /// MainViewModel 通过它与各 RepoManager 交互。
    /// </summary>
    public RepoGlobalManager GlobalManager { get; }

    [ObservableProperty]
    private ObservableCollection<Repository> _repositories = new();

    [ObservableProperty]
    private Repository? _selectedRepository;

    [ObservableProperty]
    private ObservableCollection<FileItem> _files = new();

    [ObservableProperty]
    private FileItem? _selectedFile;

    [ObservableProperty]
    private string _currentPath = "";

    [ObservableProperty]
    private string _statusText = "Ready";

    // Separate transient (auto-clear) vs persistent status
    // Transient: success operations → 3s timer → restore Ready
    // Persistent: errors/loading → stays until next operation
    public void SetStatus(string message, bool isTransient = false)
    {
        if (isTransient)
        {
            _statusClearTimer?.Stop();
            _statusClearTimer?.Dispose();
            _statusClearTimer = new System.Timers.Timer(3000);
            _statusClearTimer.Elapsed += (_, _) =>
            {
                _statusClearTimer?.Stop();
                StatusText = _lastPersistentStatus;
            };
            _statusClearTimer.AutoReset = false;
            _statusClearTimer.Start();
        }
        else
        {
            _statusClearTimer?.Stop();
            _lastPersistentStatus = message;
        }
        StatusText = message;
    }

    public void SetTransientStatus(string message) => SetStatus(message, isTransient: true);

    [ObservableProperty]
    private string _itemCountText = "";

    [ObservableProperty]
    private SyncStatusType _syncStatus = SyncStatusType.Idle;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canOperate;

    [ObservableProperty]
    private bool _showSyncRecords;

    partial void OnShowSyncRecordsChanged(bool value)
    {
        OnPropertyChanged(nameof(BackButtonText));
    }

    public string BackButtonText => ShowSyncRecords
        ? LocalizationService.Instance.GetString("Back")
        : LocalizationService.Instance.GetString("ParentDirectory");

    [ObservableProperty]
    private ObservableCollection<SyncRecordDisplay> _syncRecords = new();

    public string ConfigDir => _configService.ConfigDir;
    public ConfigService ConfigService => _configService;
    public SyncRecordService RecordService => SyncRecordService.Instance;

    [ObservableProperty]
    private bool _canCopyPath;

    [ObservableProperty]
    private bool _canDelete;

    [ObservableProperty]
    private bool _canPaste;

    partial void OnCanOperateChanged(bool value)
    {
        if (!value) CanPaste = false;
    }

    [ObservableProperty]
    private bool _canRename;

    public MainViewModel()
    {
        _configService = ConfigService.Instance;
        GlobalManager = new RepoGlobalManager();

        // RepoGlobalManager 的 FilesChanged 事件触发表格刷新
        GlobalManager.FilesChanged += async (_, _) => await RefreshAsync();

        // Marshal SyncRecordService.AddRecord calls to the UI thread
        SyncRecordService.Instance.UiDispatcher = Application.Current?.Dispatcher;

        Log.Information("MainViewModel created with RepoGlobalManager");
    }

    public void LoadSyncRecords()
    {
        SyncRecords.Clear();
        if (SelectedRepository == null) return;
        var records = RecordService.GetRecords(SelectedRepository.Name);
        foreach (var r in records)
            SyncRecords.Add(SyncRecordDisplay.FromRecord(r, SelectedRepository.Name));
    }

    public void ToggleSyncRecordsView()
    {
        ShowSyncRecords = !ShowSyncRecords;
        if (ShowSyncRecords)
            LoadSyncRecords();
    }

    public void CloseSyncRecordsView()
    {
        ShowSyncRecords = false;
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        SetStatus("Loading...");
        try
        {
            await _configService.LoadAsync();

            // 从配置恢复所有仓库（不自动 Focus，等用户选择）
            GlobalManager.RestoreFromConfig(_configService.Config.Repositories);

            // 同步 Repositories 集合（供 UI 的 ComboBox 使用）
            foreach (var repo in _configService.Config.Repositories)
                Repositories.Add(repo);

            // 切换到最后活跃的仓库
            if (!string.IsNullOrEmpty(_configService.Config.ActiveRepositoryName))
            {
                var active = Repositories.FirstOrDefault(r => r.Name == _configService.Config.ActiveRepositoryName);
                if (active != null)
                    SelectedRepository = active;
            }

            // 如果还没有切换过但有仓库，切换到第一个
            if (GlobalManager.ActiveManager == null && Repositories.Count > 0)
            {
                SelectedRepository = Repositories[0];
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            Log.Error(ex, "InitializeAsync failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 处理仓库选择器切换仓库。
    /// 触发 RepoGlobalManager.SwitchToAsync()。
    /// </summary>
    partial void OnSelectedRepositoryChanged(Repository? value)
    {
        ConfigService.Instance.CurrentRepository = value;

        if (value == null || !Directory.Exists(value.Path))
        {
            Files.Clear();
            CurrentPath = "";
            CanOperate = false;
            return;
        }

        // 标记 IsActive
        foreach (var repo in Repositories)
            repo.IsActive = repo.Path == value.Path;

        _configService.Config.ActiveRepositoryName = value.Name;
        _ = _configService.SaveAsync();

        // 找到对应的 RepoManager 并切换
        var manager = GlobalManager.Managers.FirstOrDefault(m => m.Repository == value);
        if (manager != null)
        {
            _ = GlobalManager.SwitchToAsync(manager);
            CanOperate = true;
            // 立即加载新仓库的根目录文件列表，不依赖 FilesChanged 事件
            _ = LoadDirectoryAsync(value.Path);
            if (ShowSyncRecords)
                LoadSyncRecords();
        }
        else
        {
            // 理论上不会走到这里——RestoreFromConfig 已经为所有仓库创建了 RepoManager
            CanOperate = false;
        }
    }

    partial void OnSelectedFileChanged(FileItem? value)
    {
        CanCopyPath = value != null;
        CanDelete = value != null && value.Name != "..";
        CanRename = value != null && value.Name != "..";
    }

    public async Task LoadDirectoryAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        IsLoading = true;
        SetStatus($"Loading {path}...");
        try
        {
            CurrentPath = path;
            var items = new List<FileItem>();
            var dirInfo = new DirectoryInfo(path);
            var parentPath = dirInfo.Parent?.FullName;
            var repoRootPath = SelectedRepository?.Path ?? "";

            // Parent directory row
            if (!string.IsNullOrEmpty(parentPath) && path != repoRootPath)
            {
                items.Add(new FileItem
                {
                    Name = "返回上级目录",
                    FullPath = parentPath,
                    IsDirectory = true,
                    SvnStatus = FileSvnStatus.Hidden,
                    LastModified = DateTime.MinValue,
                    IsParentDirectory = true
                });
            }

            // Directories
            foreach (var dir in dirInfo.GetDirectories())
            {
                if (dir.Name.StartsWith(".")) continue;
                items.Add(new FileItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    IsDirectory = true,
                    LastModified = dir.LastWriteTime,
                    SvnStatus = FileSvnStatus.Normal
                });
            }

            // Files
            foreach (var file in dirInfo.GetFiles())
            {
                if (file.Name.StartsWith(".")) continue;
                items.Add(new FileItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    FileSize = file.Length,
                    LastModified = file.LastWriteTime,
                    SvnStatus = FileSvnStatus.Normal
                });
            }

            var disp = Application.Current?.Dispatcher;
            if (disp == null)
                Files = new ObservableCollection<FileItem>(items);
            else if (disp.CheckAccess())
                Files = new ObservableCollection<FileItem>(items);
            else
                disp.Invoke(() => Files = new ObservableCollection<FileItem>(items));

            var itemCount = items.Count;
            ItemCountText = itemCount == 0 ? "" : $"{itemCount} items";
            StatusText = $"Ready - {itemCount} items";

            // Load SVN statuses
            if (SelectedRepository != null && Directory.Exists(SelectedRepository.Path))
            {
                try
                {
                    var activeManager = GlobalManager.ActiveManager;
                    if (activeManager == null) return;

                    var executor = activeManager.Executor;

                    bool currentDirUnversioned = !(await executor.ExecuteAsync(SvnCommand.IsVersioned, path)).Success
                        || (await executor.ExecuteAsync(SvnCommand.IsVersioned, path)).Value != "true";

                    if (currentDirUnversioned)
                    {
                        foreach (var item in items)
                        {
                            if (item.Name == "..") continue;
                            item.SvnStatus = FileSvnStatus.Unversioned;
                        }
                        ItemCountText = itemCount == 0 ? "" : $"{itemCount} items";
                        StatusText = $"Ready - {itemCount} items (unversioned dir)";
                        return;
                    }

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var statusResult = await executor.ExecuteAsync(SvnCommand.Status, path);
                    var statuses = statusResult.Success
                        ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, FileSvnStatus>>(statusResult.Value ?? "{}") ?? new()
                        : new Dictionary<string, FileSvnStatus>();

                    foreach (var item in items)
                    {
                        if (item.Name == "..") continue;
                        item.SvnStatus = FileSvnStatus.Normal;
                        if (statuses.TryGetValue(item.FullPath, out var svnStatus))
                            item.SvnStatus = svnStatus;
                    }
                    ItemCountText = itemCount == 0 ? "" : $"{itemCount} items";
                    StatusText = $"Ready - {itemCount} items";
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("GetStatusAsync timed out after 30s, showing files without SVN status");
                    ItemCountText = itemCount == 0 ? "" : $"{itemCount} items";
                    StatusText = $"Ready - {itemCount} items (SVN status timeout)";
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            Log.Error(ex, "LoadDirectoryAsync failed for {Path}", path);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void NavigateInto(FileItem item)
    {
        if (item.Name == "..")
        {
            _ = LoadDirectoryAsync(item.FullPath);
        }
        else if (item.IsDirectory || Directory.Exists(item.FullPath))
        {
            _ = LoadDirectoryAsync(item.FullPath);
        }
    }

    public async Task RefreshAsync()
    {
        if (!string.IsNullOrEmpty(CurrentPath))
            await LoadDirectoryAsync(CurrentPath);
    }

    /// <summary>
    /// 添加一个新的本地仓库（从 AddLocalRepoWindow 返回后调用）。
    /// 创建 RepoManager → 加入 GlobalManager → 保存配置 → 切换到该仓库。
    /// </summary>
    public async Task<Repository?> AddLocalRepositoryAsync(Repository newRepo)
    {
        try
        {
            var manager = GlobalManager.CreateLocal(newRepo);
            if (manager == null) return null;

            _configService.Config.Repositories.Add(newRepo);
            await _configService.SaveAsync();

            if (!Repositories.Contains(newRepo))
                Repositories.Add(newRepo);

            SelectedRepository = newRepo;  // 触发 SwitchToAsync
            return newRepo;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AddLocalRepositoryAsync failed for {Path}", newRepo.Path);
            return null;
        }
    }

    /// <summary>
    /// 将 CheckoutWindow 已创建好的 RepoManager 接入 GlobalManager 并切换。
    /// </summary>
    public async Task<Repository?> AddNetworkRepositoryAsync(RepoManager manager)
    {
        try
        {
            var repo = manager.Repository;
            _configService.Config.Repositories.Add(repo);
            await _configService.SaveAsync();

            if (!Repositories.Contains(repo))
                Repositories.Add(repo);

            SelectedRepository = repo;  // 触发 SwitchToAsync
            return repo;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AddNetworkRepositoryAsync failed");
            return null;
        }
    }

    /// <summary>
    /// 删除仓库（从 MainWindow 的删除按钮触发）。
    /// </summary>
    public void RemoveRepository(Repository repo)
    {
        var manager = GlobalManager.Managers.FirstOrDefault(m => m.Repository == repo);
        if (manager == null) return;

        GlobalManager.Remove(manager);

        // 更新 UI 列表
        Repositories.Remove(repo);
        _configService.Config.Repositories.Remove(repo);
        _ = _configService.SaveAsync();

        // 切换到第一个剩余仓库
        if (Repositories.Count > 0)
            SelectedRepository = Repositories[0];
        else
        {
            SelectedRepository = null;
            Files.Clear();
            CurrentPath = "";
        }
    }

    public void Dispose()
    {
        GlobalManager.ShutdownAll();
    }
}