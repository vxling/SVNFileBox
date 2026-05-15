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
    private readonly SvnService _svnService = new();
    private SyncService? _syncService;
    private System.Timers.Timer? _statusClearTimer;
    private string _lastPersistentStatus = "Ready";

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
    public SyncService? SyncService => _syncService;

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


    public event EventHandler<List<ConflictedFileInfo>>? ConflictDetected;
    public event EventHandler<string>? SyncNotification;

    public MainViewModel()
    {
        _configService = new ConfigService();
        _syncService = new SyncService(_configService, SyncRecordService.Instance);
        _syncService.SyncNotification += (_, msg) => { SetStatus(msg); SyncNotification?.Invoke(this, msg); };
        _syncService.FilesChanged += async (_, _) => await RefreshAsync();
        _syncService.ConflictDetected += (_, conflicts) => ConflictDetected?.Invoke(this, conflicts);

        // Marshal SyncRecordService.AddRecord calls to the UI thread
        SyncRecordService.Instance.UiDispatcher = Application.Current?.Dispatcher;

        Log.Information("MainViewModel created");
    }

    public void LoadSyncRecords()
    {
        SyncRecords.Clear();
        var records = string.IsNullOrEmpty(SelectedRepository?.Name)
            ? RecordService.Records
            : RecordService.GetRecords(SelectedRepository.Name);
        foreach (var r in records)
            SyncRecords.Add(SyncRecordDisplay.FromRecord(r));
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
            foreach (var repo in _configService.Config.Repositories)
            {
                var disp = Application.Current?.Dispatcher;
                if (disp == null)
                    Repositories.Add(repo);
                else if (disp.CheckAccess())
                    Repositories.Add(repo);
                else
                    disp.Invoke(() => Repositories.Add(repo));
            }

            if (!string.IsNullOrEmpty(_configService.Config.ActiveRepositoryName))
            {
                var active = Repositories.FirstOrDefault(r => r.Name == _configService.Config.ActiveRepositoryName);
                if (active != null)
                    SelectedRepository = active;
            }

            if (SelectedRepository != null && Directory.Exists(SelectedRepository.Path))
            {
                await LoadDirectoryAsync(SelectedRepository.Path);
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

    partial void OnSelectedRepositoryChanged(Repository? value)
    {
        _syncService?.StopSync();
        if (value != null && Directory.Exists(value.Path))
        {
            foreach (var repo in Repositories)
                repo.IsActive = repo.Path == value.Path;
            _configService.Config.ActiveRepositoryName = value.Name;
            _ = _configService.SaveAsync();
            _syncService?.StartSync(value);
            _ = LoadDirectoryAsync(value.Path);
            CanOperate = true;
            if (ShowSyncRecords)
                LoadSyncRecords();
        }
        else
        {
            _syncService?.StopSync();
            Files.Clear();
            CurrentPath = "";
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

            // Parent directory row - only show when not at repository root
            if (!string.IsNullOrEmpty(parentPath) && path != repoRootPath)
            {
                items.Add(new FileItem
                {
                    Name = "返回上级目录",
                    FullPath = parentPath,
                    IsDirectory = true,
                    SvnStatus = FileSvnStatus.Hidden,
                    LastModified = DateTime.MinValue
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
            {
                // No dispatcher (headless/test) — update directly
                Files = new ObservableCollection<FileItem>(items);
            }
            else if (disp.CheckAccess())
            {
                // Already on UI thread — update directly
                Files = new ObservableCollection<FileItem>(items);
            }
            else
            {
                // Marshal to UI thread
                disp.Invoke(() => Files = new ObservableCollection<FileItem>(items));
            }
            var itemCount = items.Count;
            ItemCountText = itemCount == 0 ? "" : $"{itemCount} items";
            StatusText = $"Ready - {itemCount} items";

            // Load real SVN statuses for all items (with 30s timeout)
            if (SelectedRepository != null && Directory.Exists(SelectedRepository.Path))
            {
                try
                {
                    // If the current directory itself is not a versioned SVN working copy,
                    // all its children are unversioned — skip the expensive status call.
                    bool currentDirUnversioned = !_svnService.IsVersioned(path);
                    Log.Debug("LoadDirectoryAsync: path={Path} isVersioned={IsVersioned}", path, !currentDirUnversioned);

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
                    // Run svn status on the CURRENT directory only, not the entire working copy —
                    // recursive scan of large repos is very slow, directory-level status is sufficient.
                    var statuses = await _svnService.GetStatusAsync(path)
                        .WaitAsync(cts.Token);
                    var repoRoot = SelectedRepository.Path;

                    Log.Debug("LoadDirectoryAsync: path={Path} statuses count={Count} entries={@statuses}", path, statuses.Count, statuses);

                    foreach (var item in items)
                    {
                        if (item.Name == "..") continue;
                        // Default to Normal (won't display anything, but marks the item as processed)
                        item.SvnStatus = FileSvnStatus.Normal;
                        // Override with actual status if found in svn status output
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

    public void Dispose()
    {
        _syncService?.StopSync();
    }
}
