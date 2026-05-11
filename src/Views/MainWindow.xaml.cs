#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.ComponentModel;
using SVNFileBox.Models;
using SVNFileBox.Services;
using SVNFileBox.ViewModels;
using SVNFileBox.Windows;
using Serilog;

namespace SVNFileBox.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private ConfigService? _configService;
    private readonly SvnService _svnService = new();
    private readonly FileAnalyzer _fileAnalyzer = new();
    private readonly FileCopier _fileCopier = new();
    private bool _isExiting;
    /// <summary>Prevents concurrent ExecuteCopyAsync calls (drag/drop + paste simultaneously).</summary>
    private int _isCopying;

    private bool CanPaste => _viewModel != null
        && _viewModel.CanOperate
        && !string.IsNullOrEmpty(_viewModel.CurrentPath)
        && Directory.Exists(_viewModel.CurrentPath)
        && Clipboard.ContainsFileDropList();

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm || _viewModel == null) return;

        // Paste enabled state
        foreach (var item in cm.Items)
        {
            if (item is MenuItem mi && mi.Name == "PasteMenuItem")
            {
                mi.IsEnabled = _viewModel.CanOperate && Clipboard.ContainsFileDropList();
                break;
            }
        }

        // Inject system icons (or emoji fallback) into the "新建" submenu — once
        // 判断条件：第一个子项 Icon 为 null 表示尚未注入
        if (cm.Items.Count > 0 && cm.Items[0] is MenuItem firstRoot && firstRoot.Icon == null)
            InjectIconsOnFirstOpen(cm);
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Removed: no longer auto-fill columns on resize
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = new MainViewModel();
        _configService = _viewModel.ConfigService;
        DataContext = _viewModel;

        _viewModel.SyncNotification += (_, msg) => ShowToast(msg);

        _viewModel.PropertyChanged += (s, ev) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (ev.PropertyName == nameof(MainViewModel.CurrentPath))
                    PathText.Text = _viewModel.CurrentPath;
                else if (ev.PropertyName == nameof(MainViewModel.StatusText))
                    StatusText.Text = _viewModel.StatusText;
                else if (ev.PropertyName == nameof(MainViewModel.Files))
                    FileList.ItemsSource = _viewModel.Files;
                else if (ev.PropertyName == nameof(MainViewModel.Repositories))
                    RepoList.ItemsSource = _viewModel.Repositories;
            });
        };

        // Ctrl+V for paste
        InputBindings.Add(new KeyBinding(
            new RelayCommand(_ => _ = DoPasteAsync()),
            Key.V, ModifierKeys.Control));

        _viewModel!.ConflictDetected += OnConflictDetected;

        await _viewModel.InitializeAsync();
    }

    // ---- Icon injection helpers ----

    private static readonly Dictionary<string, string> _headerToExt = new()
    {
        { "NewTextFile",   ".txt"  },
        { "NewWordDoc",    ".docx" },
        { "NewExcelSheet", ".xlsx" },
        { "NewPowerPoint", ".pptx" },
        { "NewPngImage",   ".png"  },
        { "NewBmpImage",   ".bmp"  },
    };

    /// <summary>
    /// 首次打开右键菜单时注入系统图标（提取失败则降级为 emoji）。
    /// 判断条件：子项 Icon 为 null 表示尚未注入。
    /// </summary>
    private void InjectIconsOnFirstOpen(ItemsControl menu)
    {
        foreach (var item in menu.Items)
        {
            if (item is MenuItem mi)
            {
                // "新建" 子菜单
                if (mi.Header?.ToString()?.Contains("New") == true && mi.Items.Count > 0)
                {
                    mi.Icon = "✨";
                    foreach (var child in mi.Items)
                    {
                        if (child is MenuItem childMi)
                            ApplyIconByExt(childMi);
                    }
                }
            }
        }
    }

    private void ApplyIconByExt(MenuItem mi)
    {
        string? ext = null;
        foreach (var kv in _headerToExt)
        {
            if (mi.Name == kv.Key || mi.Header?.ToString()?.Contains(kv.Key) == true)
            {
                ext = kv.Value;
                break;
            }
        }
        if (ext == null) return;

        var icon = IconExtractor.GetIcon(ext);
        if (icon is System.Windows.Media.ImageSource img)
            mi.Icon = img;
        else if (icon is string emoji)
            mi.Icon = emoji;
    }

    private void OnConflictDetected(object? sender, List<ConflictedFileInfo> conflicts)
    {
        Dispatcher.Invoke(() =>
        {
            var window = new ConflictWindow { Owner = this };
            window.SetConflicts(conflicts);
            var result = window.ShowDialog();
            if (result == true)
            {
                // User confirmed — kick off resolution via SyncService.ApplyConflictResolutionsAsync
                // The event was already raised in SyncService, so the loop is waiting.
                // Actually: ConflictDetected is a synchronous event (not async),
                // and ApplyConflictResolutionsAsync is called in the same flow after this handler returns.
                // So we just need to tell SyncService to proceed — which it already does.
                // But SyncService can't know when the window closes... Let's handle it via a continuation.
                _ = ResolveConflictsAsync(conflicts);
            }
            // If DialogResult == false (cancel), conflicts are not resolved — user explicitly deferred
        });
    }

    private async Task ResolveConflictsAsync(List<ConflictedFileInfo> conflicts)
    {
        if (_viewModel?.SyncService == null) return;
        try
        {
            StatusText.Text = LocalizationService.Instance.GetString("ResolvingConflicts", conflicts.Count);
            var handled = await _viewModel.SyncService.ApplyConflictResolutionsAsync(conflicts);
            _viewModel.RecordService.AddRecord(
                _viewModel.SelectedRepository?.Name ?? "",
                "", "ConflictResolved", "Success", $"Resolved {handled} conflict(s)");
            StatusText.Text = LocalizationService.Instance.GetString("ConflictsResolved", handled);
            await _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Conflict resolution failed");
            StatusText.Text = LocalizationService.Instance.GetString("ConflictResolutionFailed");
        }
    }

    private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is Repository repo)
            _viewModel!.SelectedRepository = repo;
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is FileItem item)
        {
            if (item.Name == "..")
            {
                _viewModel?.NavigateInto(item);
                return;
            }

            if (item.IsDirectory || Directory.Exists(item.FullPath))
            {
                _viewModel?.NavigateInto(item);
            }
            else if (File.Exists(item.FullPath))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = item.FullPath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to open file: {Path}", item.FullPath);
                    ShowToast(LocalizationService.Instance.GetString("OpenFailed", ex.Message),
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                    _viewModel!.StatusText = LocalizationService.Instance.GetString("OpenFailed", ex.Message);
                }
            }
        }
    }

    private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null)
            return;

        // Map column index to sort property (order matches GridView definition)
        string? sortProperty = (header.Column.DisplayMemberBinding as System.Windows.Data.Binding)?.Path.Path;
        if (sortProperty == null)
        {
            // Column indices: 0=Type, 1=Name, 2=Status, 3=Size, 4=Modified
            var cols = (FileList?.View as GridView)?.Columns;
            if (cols != null)
            {
                var idx = cols.IndexOf(header.Column);
                sortProperty = idx switch
                {
                    0 => "TypeDisplay",
                    2 => "SvnStatus",
                    _ => null
                };
            }
        }

        if (sortProperty == null) return;
        ApplySort(FileList, sortProperty);
    }

    private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Removed: no longer auto-fill columns
    }

    private void AdjustNameColumnWidth()
    {
        var gv = FileList?.View as GridView;
        if (gv == null || gv.Columns.Count < 2) return;

        // Fixed columns: Type(50) + Status(auto) + Size(110) + Modified(180)
        // Also reserve space for vertical scrollbar if content overflows
        double fixedWidth = 50 + 110 + 180;
        double contentWidth = FileList.ActualWidth;

        // If total content overflows the visible area, vertical scrollbar appears and eats into width
        // Rough check: if all fixed+name columns would overflow, reserve scrollbar width (~17px)
        // Name min=450, total fixed=340, so overflow if actualWidth < 790
        bool needsScrollbar = contentWidth < 790 && contentWidth > 0;
        if (needsScrollbar) fixedWidth += SystemParameters.VerticalScrollBarWidth;

        double availableWidth = contentWidth - fixedWidth;
        if (availableWidth < 100) availableWidth = 100;

        // Name column gets all remaining space
        gv.Columns[1].Width = Math.Max(100, availableWidth);
    }

    private static void ApplySort(ListView listView, string sortProperty)
    {
        var dataView = CollectionViewSource.GetDefaultView(listView.ItemsSource);
        var currentSort = dataView.SortDescriptions.FirstOrDefault();
        var direction = currentSort.PropertyName == sortProperty && currentSort.Direction == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        dataView.SortDescriptions.Clear();
        dataView.SortDescriptions.Add(new SortDescription(sortProperty, direction));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.RefreshAsync();
    }

    private async void ManualSync_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SyncService == null) return;
        _viewModel!.StatusText = LocalizationService.Instance.GetString("ManualSyncInProgress");
        try
        {
            await _viewModel.SyncService.SyncNowAsync();
            ShowToast(LocalizationService.Instance.GetString("SyncComplete"));
            _viewModel.StatusText = LocalizationService.Instance.GetString("SyncComplete");
            await _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Manual sync failed");
            ShowToast(LocalizationService.Instance.GetString("SyncFailed", ex.Message),
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
            _viewModel.StatusText = LocalizationService.Instance.GetString("SyncFailed", ex.Message);
        }
    }

    private FileItem? GetFileItemFromContextMenu(object sender)
    {
        // Always use SelectedFile — it's kept in sync by ListView.SelectedItem binding
        // No need to try to navigate PlacementTarget which may be a nested element inside DataTemplate
        return _viewModel?.SelectedFile as FileItem;
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var item = GetFileItemFromContextMenu(sender);
        var path = item?.FullPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var targetDir = File.Exists(path) || Directory.Exists(path)
                ? Path.GetDirectoryName(path) : path;
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{targetDir}\"", UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error(ex, "Failed to open explorer"); }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var item = GetFileItemFromContextMenu(sender);
        // Fall back to current directory if no file is selected
        var path = item?.FullPath ?? _viewModel?.CurrentPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Clipboard.SetText(path);
            ShowToast(LocalizationService.Instance.GetString("Copied", path));
            _viewModel!.StatusText = LocalizationService.Instance.GetString("Copied", path);
        }
        catch (Exception ex) { Log.Error(ex, "CopyPath failed"); }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        var targetDir = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        var dialog = new Windows.InputDialog
        {
            Title = LocalizationService.Instance.GetString("NewFolderTitle"),
            Owner = this
        };
        dialog.SetPrompt(LocalizationService.Instance.GetString("NewFolderPrompt"));
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            try
            {
                var newFolderPath = Path.Combine(targetDir, dialog.InputText.Trim());
                if (Directory.Exists(newFolderPath))
                {
                    MsgBox.Show(this,
                        LocalizationService.Instance.GetString("FolderAlreadyExists", dialog.InputText.Trim()),
                        LocalizationService.Instance.GetString("NewFolderTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Directory.CreateDirectory(newFolderPath);

                // svn add (marks new folder), SyncService auto-commits on next FullSync
                await _svnService.AddFileAsync(newFolderPath);

                ShowToast(LocalizationService.Instance.GetString("NewFolderSuccess", dialog.InputText.Trim()));
                _viewModel!.StatusText = LocalizationService.Instance.GetString("NewFolderSuccess", dialog.InputText.Trim());
                _ = _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationService.Instance.GetString("NewFolderFailed", ex.Message),
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                _viewModel!.StatusText = LocalizationService.Instance.GetString("NewFolderFailed", ex.Message);
            }
        }
    }

    // ─── New File submenu ─────────────────────────────────────────────────────

    private string GetUniqueFilePath(string dir, string baseName, string ext)
    {
        string path = Path.Combine(dir, baseName + ext);
        int i = 1;
        while (File.Exists(path) || Directory.Exists(path))
            path = Path.Combine(dir, $"{baseName} ({i++}){ext}");
        return path;
    }

    private async void NewTextFile_Click(object sender, RoutedEventArgs e) =>
        await NewFileAsync("新建文本文档", "New Text Document", ".txt");

    private async void NewWordDoc_Click(object sender, RoutedEventArgs e) =>
        await NewFileAsync("新建 Microsoft Word 文档", "New Microsoft Word Document", ".docx");

    private async void NewExcelSheet_Click(object sender, RoutedEventArgs e) =>
        await NewFileAsync("新建 Microsoft Excel 工作表", "New Microsoft Excel Worksheet", ".xlsx");

    private async void NewPowerPoint_Click(object sender, RoutedEventArgs e) =>
        await NewFileAsync("新建 Microsoft PowerPoint 文档", "New Microsoft PowerPoint", ".pptx");

    private async void NewPngImage_Click(object sender, RoutedEventArgs e) =>
        await NewFileAsync("新建 PNG 图片", "New PNG Image", ".png");

    private async void NewBmpImage_Click(object sender, RoutedEventArgs e) =>
        await NewFileAsync("新建 BMP 图片", "New BMP Image", ".bmp");

    private async Task NewFileAsync(string defaultNameCn, string defaultNameEn, string ext)
    {
        if (_viewModel == null) return;
        var targetDir = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        // Use localized default name — detect current language from CultureInfo
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        var defaultName = isZh ? defaultNameCn : defaultNameEn;
        var newPath = GetUniqueFilePath(targetDir, defaultName, ext);

        try
        {
            if (!NewFileService.Create(newPath))
                throw new Exception("NewFileService returned false");

            await _svnService.AddFileAsync(newPath);
            ShowToast(LocalizationService.Instance.GetString("NewFileSuccess", defaultName));
            _viewModel!.StatusText = LocalizationService.Instance.GetString("NewFileSuccess", defaultName);
            _ = _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowToast(LocalizationService.Instance.GetString("NewFileFailed", ex.Message),
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
            _viewModel!.StatusText = LocalizationService.Instance.GetString("NewFileFailed", ex.Message);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var item = GetFileItemFromContextMenu(sender);
        if (item == null || item.Name == "..") return;

        var dialog = new Windows.InputDialog
        {
            Title = LocalizationService.Instance.GetString("RenameTitle"),
            Owner = this
        };
        dialog.SetPrompt(LocalizationService.Instance.GetString("RenamePrompt"));
        dialog.SetInput(item.Name);
        // Pre-fill with current name, select all for easy replacement
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            var newName = dialog.InputText.Trim();
            if (newName == item.Name) return;

            var parentDir = Path.GetDirectoryName(item.FullPath);
            if (string.IsNullOrEmpty(parentDir)) return;

            var newPath = Path.Combine(parentDir, newName);
            try
            {
                if (Directory.Exists(newPath) || File.Exists(newPath))
                {
                    MsgBox.Show(this,
                        LocalizationService.Instance.GetString("NameAlreadyTaken", newName),
                        LocalizationService.Instance.GetString("RenameTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Physical move first, then let FileWatcher detect and enqueue the change.
                // This avoids issues with svn delete removing the working-copy directory
                // before Directory.Move can access it.
                Directory.Move(item.FullPath, newPath);

                // svn delete old + svn add new (marks the rename in working copy)
                await _svnService.DeleteAsync(item.FullPath);
                await _svnService.AddFileAsync(newPath);

                // Enqueue as Move so QueueCommitProcessor resolves it correctly
                PendingCommitQueue.Instance.EnqueueMove(item.FullPath, newPath);

                ShowToast(LocalizationService.Instance.GetString("RenameSuccess", $"{item.Name} -> {newName}"));
                _viewModel!.StatusText = LocalizationService.Instance.GetString("RenameSuccess", $"{item.Name} -> {newName}");
                _ = _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationService.Instance.GetString("RenameFailed", ex.Message),
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                _viewModel!.StatusText = LocalizationService.Instance.GetString("RenameFailed", ex.Message);
                Log.Error(ex, "Rename failed: {Old} -> {New}", item.FullPath, newPath);
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = GetFileItemFromContextMenu(sender);
        if (item == null) return;

        var result = MsgBox.Show(this,
            LocalizationService.Instance.GetString("DeleteConfirmMessage",
                item.IsDirectory
                    ? LocalizationService.Instance.GetString("Folder")
                    : LocalizationService.Instance.GetString("File"), item.Name),
            LocalizationService.Instance.GetString("DeleteConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                // Physical delete first — let FileWatcher detect the missing file and enqueue.
                // If FileWatcher fires before we reach enqueue below, it handles svn delete too.
                // Either way the Delete operation gets committed.
                if (item.IsDirectory || Directory.Exists(item.FullPath))
                    Directory.Delete(item.FullPath, recursive: true);
                else
                    File.Delete(item.FullPath);

                // svn delete marks the deletion in the working copy (after physical file is gone)
                await _svnService.DeleteAsync(item.FullPath);

                // Enqueue the delete so QueueCommitProcessor can batch-commit it
                PendingCommitQueue.Instance.Enqueue(item.FullPath, CommitOperation.Delete);

                ShowToast(LocalizationService.Instance.GetString("DeleteSuccess", item.Name));
                _viewModel!.StatusText = LocalizationService.Instance.GetString("DeleteSuccess", item.Name);
                _ = _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationService.Instance.GetString("DeleteFailed", ex.Message),
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                _viewModel!.StatusText = LocalizationService.Instance.GetString("DeleteFailed", ex.Message);
            }
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => _ = DoPasteAsync();

    private async Task DoPasteAsync()
    {
        if (_viewModel == null) return;
        var targetDir = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            await ExecuteCopyAsync(files, targetDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Paste failed");
            ShowToast(LocalizationService.Instance.GetString("PasteFailed", ex.Message),
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
            _viewModel.StatusText = LocalizationService.Instance.GetString("PasteFailed", ex.Message);
        }
    }

    private async Task ExecuteCopyAsync(IList<string> sourcePaths, string targetDir)
    {
        // Guard against concurrent calls (e.g. drag/drop + paste at the same time)
        if (Interlocked.CompareExchange(ref _isCopying, 1, 0) == 1)
        {
            Log.Warning("[ExecuteCopyAsync] Copy already in progress, rejecting duplicate call");
            MsgBox.Show(this,
                LocalizationService.Instance.GetString("CopyInProgress"),
                LocalizationService.Instance.GetString("Prompt"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Pause FileWatcher during copy to avoid triggering commit监控 for each new file
        var syncService = _viewModel?.SyncService;
        syncService?.DisableFileWatcher();

        // Show progress window immediately (non-blocking), then run analysis + copy in background
        var progressWindow = new FileCopyProgressWindow(_fileCopier)
        {
            Owner = this
        };

        var cts = new CancellationTokenSource();

        // Cancel button — cancels both analysis and copy phases
        progressWindow.CancelRequested += () =>
        {
            cts.Cancel();
            _fileCopier.Cancel();
        };

        // Handle window close → cancel
        void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_fileCopier.IsRunning)
            {
                e.Cancel = true;
                cts.Cancel();
                _fileCopier.Cancel();
            }
        }
        progressWindow.Closing += OnWindowClosing;

        // Show window first (non-blocking), then analyze + copy
        progressWindow.Show();
        progressWindow.StartAnalysis(); // Initialize once at start

        try
        {
            // Phase 1: Analyze in background, report progress per item
            var analysisProgress = new Progress<string>(item => progressWindow.UpdateAnalysisItem(item));

            FileCopyPlan? plan;
            CancellationToken analysisToken = cts.Token;

            try
            {
                plan = await Task.Run(() => _fileAnalyzer.Analyze(sourcePaths, targetDir, analysisProgress, analysisToken), analysisToken);
            }
            catch (OperationCanceledException)
            {
                progressWindow.Close();
                ShowToast(LocalizationService.Instance.GetString("AnalysisCancelled"));
                _viewModel.StatusText = LocalizationService.Instance.GetString("AnalysisCancelled");
                return;
            }

            if (plan == null)
            {
                progressWindow.Close();
                ShowToast(LocalizationService.Instance.GetString("NoFilesToCopy"));
                _viewModel.StatusText = LocalizationService.Instance.GetString("NoFilesToCopy");
                return;
            }

            if (plan.IsSameLocation)
            {
                progressWindow.Close();
                MsgBox.Show(this,
                    LocalizationService.Instance.GetString("SameLocation"),
                    LocalizationService.Instance.GetString("Prompt"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Phase 2: Copy in background, report progress per file
            progressWindow.StartCopy();
            var copyProgress = new Progress<CopyProgress>(p => progressWindow.UpdateProgress(p));

            var result = await Task.Run(() => _fileCopier.CopyAsync(plan, copyProgress));

            progressWindow.Stop();

            if (result.WasCancelled)
            {
                ShowToast(LocalizationService.Instance.GetString("CopyCancelled"));
                _viewModel.StatusText = LocalizationService.Instance.GetString("CopyCancelled");
            }
            else if (result.HasError)
            {
                ShowToast(LocalizationService.Instance.GetString("CopyFailed", result.ErrorMessage ?? ""),
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                _viewModel.StatusText = LocalizationService.Instance.GetString("CopyFailed", result.ErrorMessage ?? "");
            }
            else
            {
                var summary = result.SkippedCount == 0
                    ? LocalizationService.Instance.GetString("CopiedNItems", result.CopiedCount)
                    : LocalizationService.Instance.GetString("CopiedNItemsSkippedM", result.CopiedCount, result.SkippedCount);
                ShowToast(summary);
                _viewModel.StatusText = summary;
            }

            _ = _viewModel.RefreshAsync();
        }
        finally
        {
            progressWindow.Close();
            progressWindow.Closing -= OnWindowClosing;
            syncService?.ReEnableFileWatcher();
            Interlocked.Exchange(ref _isCopying, 0);
        }
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        if (_viewModel == null) return;
        var targetDir = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null) return;

        await ExecuteCopyAsync(files.ToList(), targetDir);
    }

    private void AddLocalRepo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SVNFileBox.Windows.AddLocalRepoWindow(_viewModel!.Repositories) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultRepository != null)
        {
            var repo = dialog.ResultRepository;
            _viewModel!.Repositories.Add(repo);
            _configService!.Config.Repositories.Add(repo);
            _ = _configService.SaveAsync();
            _viewModel.SelectedRepository = repo;
            RepoList.SelectedItem = repo;
        }
    }

    private void Checkout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SVNFileBox.Windows.CheckoutWindow(_viewModel!.Repositories) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var repo = new Repository
            {
                Name = dialog.RepoName!,
                Path = dialog.LocalPath!,
                Url = dialog.RepoUrl!,
                Username = dialog.Username ?? "",
                IsActive = false,
                RepositoryType = RepositoryType.Network
            };
            _viewModel!.Repositories.Add(repo);
            _configService!.Config.Repositories.Add(repo);
            _ = _configService.SaveAsync();
            _viewModel.SelectedRepository = repo;
            RepoList.SelectedItem = repo;
        }
    }

    private void ViewSyncRecords_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ToggleSyncRecordsView();
    }

    private void BackToFiles_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.CloseSyncRecordsView();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SVNFileBox.Windows.SettingsWindow(_configService!) { Owner = this };
        dialog.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SVNFileBox.Windows.AboutWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void RemoveRepo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is Repository repo)
        {
            if (repo.RepositoryType == RepositoryType.Network)
            {
                var result = MsgBox.Show(this,
                    LocalizationService.Instance.GetString("RemoveNetworkRepoConfirm", repo.Name),
                    LocalizationService.Instance.GetString("ConfirmRemove"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    // Delete local working copy for network repo
                    if (Directory.Exists(repo.Path))
                    {
                        try
                        {
                            ClearReadOnlyAndDelete(repo.Path);
                            Log.Information("Deleted network repo local working copy: {Path}", repo.Path);
                        }
                        catch (Exception ex)
                        {
                            MsgBox.Show(this, $"删除本地文件失败: {ex.Message}",
                                LocalizationService.Instance.GetString("Error"),
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                    _viewModel!.Repositories.Remove(repo);
                    _configService!.Config.Repositories.Remove(repo);
                    _ = _configService.SaveAsync();
                    if (_viewModel.SelectedRepository == repo)
                        _viewModel.SelectedRepository = null;
                }
            }
            else
            {
                var result = MsgBox.Show(this,
                    LocalizationService.Instance.GetString("RemoveRepoConfirm", repo.Name),
                    LocalizationService.Instance.GetString("ConfirmRemove"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _viewModel!.Repositories.Remove(repo);
                    _configService!.Config.Repositories.Remove(repo);
                    _ = _configService.SaveAsync();
                    if (_viewModel.SelectedRepository == repo)
                        _viewModel.SelectedRepository = null;
                }
            }
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
            return;
        // Hide to tray instead of closing
        e.Cancel = true;
        Hide();
        if (TrayIcon != null && !TrayIcon.IsDisposed)
            TrayIcon.ShowBalloonTip("SVNFileBox",
                LocalizationService.Instance.GetString("MinimizedToTray"),
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        ShowMainWindow();
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e)
    {
        ShowMainWindow();
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _isExiting = true;
        TrayIcon?.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Shows a toast notification via the system tray balloon tip.</summary>
    public void ShowToast(string message, Hardcodet.Wpf.TaskbarNotification.BalloonIcon icon = Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info)
    {
        Dispatcher.Invoke(() =>
        {
            if (TrayIcon == null || TrayIcon.IsDisposed) return;
            TrayIcon.ShowBalloonTip("SVNFileBox", message, icon);
        });
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    // Note: CanExecuteChanged is intentionally a no-op because CanExecute always returns true.
    // WPF/ICommand infrastructure will never block command execution, so this is safe.
    private static void ClearReadOnlyAndDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, recursive: true);
    }
    private class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add {} remove {} }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }
}