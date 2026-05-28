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
    private readonly FileAnalyzer _fileAnalyzer = new();
    private FileCopier _fileCopier;
    private bool _isExiting;
    private readonly List<(string Message, Hardcodet.Wpf.TaskbarNotification.BalloonIcon Icon)> _pendingToasts = new();
    private bool _toastIconReady;
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
            }
            else if (item is MenuItem mi2 && mi2.Name == "OpenMenuItem")
            {
                mi2.IsEnabled = GetFileItemFromContextMenu(mi2) != null;
            }
            else if (item is MenuItem mi3 && mi3.Name == "AddToZipMenuItem")
            {
                // Enable when a file/folder is selected (not "..")
                var fi = GetFileItemFromContextMenu(mi3) as FileItem;
                mi3.IsEnabled = fi != null && fi.Name != "..";
            }
        }


    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = new MainViewModel();
        _configService = _viewModel.ConfigService;
        _fileCopier = new FileCopier();
        DataContext = _viewModel;

        // Sync FileTransferTimeout from config to SvnService static cache
        if (_configService.Config.FileTransferTimeoutSeconds > 0)
            SvnService.FileTransferTimeoutMs = _configService.Config.FileTransferTimeoutSeconds * 1000;

        _viewModel.GlobalManager.SyncNotification += (_, msg) => ShowToast(msg);

        _viewModel.PropertyChanged += (s, ev) =>
        {
            Dispatcher.Invoke(() =>
            {
                    if (ev.PropertyName == nameof(MainViewModel.CurrentPath))
                    {
                        var repoRoot = _viewModel.SelectedRepository?.Path ?? "";
                        if (!string.IsNullOrEmpty(repoRoot) && _viewModel.CurrentPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
                            PathTextBox.Text = _viewModel.CurrentPath== repoRoot? _viewModel.SelectedRepository?.Name : _viewModel.CurrentPath.Substring(repoRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        else
                            PathTextBox.Text = _viewModel.CurrentPath;
                    }
                else if (ev.PropertyName == nameof(MainViewModel.StatusText))
                    StatusTextBlock.Text = _viewModel.StatusText;
                else if (ev.PropertyName == nameof(MainViewModel.ItemCountText))
                    ItemCountText.Text = _viewModel.ItemCountText;
                else if (ev.PropertyName == nameof(MainViewModel.SyncStatus))
                    UpdateSyncIndicator(_viewModel.SyncStatus);
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

        _viewModel!.GlobalManager.ConflictDetected += OnConflictDetected;
        _viewModel!.GlobalManager.CredentialExpired += OnCredentialExpired;
        _viewModel!.GlobalManager.ActiveExecutorChanged += (_, executor) => _fileCopier.SetExecutor(executor);

        await _viewModel.InitializeAsync();
    }

    private void UpdateSyncIndicator(SyncStatusType status)
    {
        var color = status switch
        {
            SyncStatusType.Idle => (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
            SyncStatusType.Syncing => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0xA0, 0xE0)),
            SyncStatusType.Success => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB0, 0x50)),
            SyncStatusType.Failed => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x30, 0x30)),
            _ => (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush")
        };
        SyncIndicator.Fill = color;
    }

    // ---- Icon injection helpers ----

    



    private async void OnConflictDetected(object? sender, List<ConflictedFileInfo> conflicts)
    {
        var deferred = false;
        Dispatcher.Invoke(() =>
        {
            var window = new ConflictWindow { Owner = this };
            window.SetConflicts(conflicts);
            var result = window.ShowDialog();
            deferred = result != true;
        });

        if (deferred)
        {
            Log.Information("[ConflictWindow] User deferred conflict resolution");
            return;
        }

        // Await resolution — ExecuteAsync HeavyWrite commands return TCS that fires
        // when WorkerLoop finishes the operation, so this properly waits for SVN result.
        var ok = await ResolveConflictsAsync(conflicts);
        await _viewModel!.RefreshAsync();
        ShowToast(ok
            ? LocalizationService.Instance.GetString("ConflictsResolved", conflicts.Count)
            : LocalizationService.Instance.GetString("ConflictResolutionFailed"));
    }

    private void OnCredentialExpired(object? sender, (string repoName, string repoUrl, string username) info)
    {
        Dispatcher.Invoke(() =>
        {
            var dialog = new CheckoutWindow(_viewModel!.Repositories)
            {
                Owner = this
            };
            dialog.OpenCredentialRenewal(info.repoName, info.repoUrl, info.username);
            if (dialog.ShowDialog() == true)
            {
                _viewModel!.GlobalManager.UpdateCredential(dialog.Username ?? "", dialog.Password ?? "");
                ShowToast(LocalizationService.Instance.GetString("CredentialRenewalSuccess", info.repoName));
            }
        });
    }

    private async Task<bool> ResolveConflictsAsync(List<ConflictedFileInfo> conflicts)
    {
        var syncService = _viewModel?.GlobalManager.ActiveManager?.SyncService;
        if (syncService == null) return false;
        try
        {
            _viewModel.SetStatus(LocalizationService.Instance.GetString("ResolvingConflicts", conflicts.Count));
            var handled = await syncService.ApplyConflictResolutionsAsync(conflicts);
            _viewModel.RecordService.AddRecord(
                _viewModel.SelectedRepository?.Name ?? "",
                "", "ConflictResolved", "Success", $"Resolved {handled}/{conflicts.Count} conflict(s)");
            _viewModel.SetTransientStatus(LocalizationService.Instance.GetString("ConflictsResolved", handled));
            return handled > 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Conflict resolution failed");
            _viewModel.SetStatus(LocalizationService.Instance.GetString("ConflictResolutionFailed"));
            _viewModel.RecordService.AddRecord(
                _viewModel.SelectedRepository?.Name ?? "",
                "", "ConflictResolved", "Failed", ex.Message);
            return false;
        }
    }

    private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is Repository repo)
            _viewModel!.SelectedRepository = repo;
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null) return;

        // Get the list of currently selected items from the ListView
        var selectedItems = FileList.SelectedItems.Cast<FileItem>().ToList();

        // Update IsSelected flag on each FileItem to match ListView selection
        foreach (var item in _viewModel.Files)
        {
            item.IsSelected = selectedItems.Contains(item);
        }

        _viewModel.SelectedItems.Clear();
        foreach (var item in selectedItems)
            _viewModel.SelectedItems.Add(item);
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
                    _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("OpenFailed", ex.Message));
                }
            }
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (GetFileItemFromContextMenu(sender) is not FileItem item)
            return;

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
                var psi = new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to open file: {Path}", item.FullPath);
                ShowToast(LocalizationService.Instance.GetString("OpenFailed", ex.Message),
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("OpenFailed", ex.Message));
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
        ApplySort(FileList!, sortProperty);
    }

    private void AdjustFillColumn()
    {
        // Handle whichever list is currently visible (FileList or SyncRecordList)
        ListView? activeList = FileList;
        if (SyncRecordList.Visibility == System.Windows.Visibility.Visible)
            activeList = SyncRecordList;

        var gv = activeList?.View as GridView;
        if (gv == null) return;

        // Find the fill column
        GridViewColumn? fillCol = null;
        int fillIndex = -1;
        for (int i = 0; i < gv.Columns.Count; i++)
        {
            if (SVNFileBox.Converters.GridViewColumnAttach.GetIsFillColumn(gv.Columns[i]))
            {
                fillCol = gv.Columns[i];
                fillIndex = i;
                break;
            }
        }
        if (fillCol == null || fillIndex < 0) return;

        // Sum widths of all other columns
        double otherWidth = 0;
        for (int i = 0; i < gv.Columns.Count; i++)
        {
            if (i == fillIndex) continue;
            otherWidth += gv.Columns[i].Width;
        }

        // Account for scrollbar if content overflows
        double scrollbar = activeList!.Items.Count > 0
            ? SystemParameters.VerticalScrollBarWidth : 0;

        double available = activeList!.ActualWidth - otherWidth - scrollbar;
        fillCol.Width = Math.Max(100, available);
    }

    private void AdjustNameColumnWidth()
    {
        var gv = FileList?.View as GridView;
        if (gv == null || gv.Columns.Count < 2) return;

        // Fixed columns: Type(50) + Status(auto) + Size(110) + Modified(180)
        // Also reserve space for vertical scrollbar if content overflows
        double fixedWidth = 50 + 110 + 180;
        double contentWidth = FileList!.ActualWidth;

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
        // SortGroup=0 (parent dir row) always on top, then user-selected sort within each group
        dataView.SortDescriptions.Add(new SortDescription("SortGroup", ListSortDirection.Ascending));
        dataView.SortDescriptions.Add(new SortDescription(sortProperty, direction));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.RefreshAsync();
    }

    private async void ManualSync_Click(object sender, RoutedEventArgs e)
    {
        var syncService = _viewModel?.GlobalManager.ActiveManager?.SyncService;
        if (syncService == null) return;
        _viewModel.SyncStatus = SyncStatusType.Syncing;
        _viewModel.SetStatus(LocalizationService.Instance.GetString("ManualSyncInProgress"));
        try
        {
            await syncService.SyncNowAsync();
            ShowToast(LocalizationService.Instance.GetString("SyncComplete"));
            _viewModel.SetTransientStatus(LocalizationService.Instance.GetString("SyncComplete"));
            _viewModel.SyncStatus = SyncStatusType.Success;
            await _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Manual sync failed");
            ShowToast(LocalizationService.Instance.GetString("SyncFailed", ex.Message),
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
            _viewModel.SetStatus(LocalizationService.Instance.GetString("SyncFailed", ex.Message));
            _viewModel.SyncStatus = SyncStatusType.Failed;
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

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        // Use selected items if multi-select is active, otherwise use the right-clicked item
        var items = _viewModel!.GetSelectedItemsForOperation().ToList();
        if (items.Count == 0)
        {
            var item = GetFileItemFromContextMenu(sender);
            if (item == null) return;
            items.Add(item);
        }
        try
        {
            var files = new System.Collections.Specialized.StringCollection();
            foreach (var item in items)
                files.Add(item.FullPath);
            Clipboard.SetFileDropList(files);
            var name = items.Count > 1 ? $"{items.Count} {LocalizationService.Instance.GetString("Files")}" : items[0].Name;
            ShowToast(LocalizationService.Instance.GetString("CopiedToClipboard", name));
            _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("CopiedToClipboard", name));
        }
        catch (Exception ex) { Log.Error(ex, "Copy failed"); }
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
            _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("Copied", path));
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
        dialog.SetInput(LocalizationService.Instance.GetString("NewFolder"));
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
                await _viewModel!.GlobalManager.ActiveManager!.Executor.ExecuteAsync(SvnCommand.Add, newFolderPath);

                ShowToast(LocalizationService.Instance.GetString("NewFolderSuccess", dialog.InputText.Trim()));
                _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("NewFolderSuccess", dialog.InputText.Trim()));
                _ = _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationService.Instance.GetString("NewFolderFailed", ex.Message),
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                _viewModel!.SetStatus(LocalizationService.Instance.GetString("NewFolderFailed", ex.Message));
            }
        }
    }

    // ─── Add to ZIP ─────────────────────────────────────────────────────────────

    private async void AddToZip_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        var items = _viewModel.GetSelectedItemsForOperation().ToList();
        if (items.Count == 0)
        {
            var item = GetFileItemFromContextMenu(sender) as FileItem;
            if (item == null || item.Name == "..") return;
            items.Add(item);
        }
        var targetDir = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        // Default filename: first item's name (strip extension)
        var baseName = Path.GetFileNameWithoutExtension(items[0].Name);
        var dialog = new Windows.InputDialog
        {
            Title = LocalizationService.Instance.GetString("AddToZipTitle"),
            Owner = this
        };
        dialog.SetPrompt(LocalizationService.Instance.GetString("AddToZipPrompt"));
        dialog.SetInput(baseName + ".zip");

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText))
            return;

        var zipName = dialog.InputText.Trim();
        if (!zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            zipName += ".zip";
        var zipPath = Path.Combine(targetDir, zipName);

        // Confirm overwrite if exists
        if (File.Exists(zipPath))
        {
            var confirmMsg = LocalizationService.Instance.GetString("AddToZipFileExists", zipName);
            var confirmTitle = LocalizationService.Instance.GetString("AddToZipConfirmTitle");
            var result = MsgBox.Show(this, confirmMsg, confirmTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;
        }

        var progressWindow = new Windows.ProgressWindow
        {
            Title = LocalizationService.Instance.GetString("AddToZipInProgress"),
            Owner = this,
            CanCancel = true
        };

        CancellationTokenSource? cts = null;
        progressWindow.CancelRequested += (s, ev) => cts?.Cancel();

        progressWindow.Show();
        progressWindow.UpdateProgress(0, LocalizationService.Instance.GetString("AddToZipInProgress"));

        try
        {
            cts = new CancellationTokenSource();

            // Collect all files from selected items
            var allFiles = new List<string>();
            foreach (var item in items)
            {
                if (Directory.Exists(item.FullPath))
                    allFiles.AddRange(Directory.GetFiles(item.FullPath, "*", SearchOption.AllDirectories));
                else if (File.Exists(item.FullPath))
                    allFiles.Add(item.FullPath);
            }

            int total = allFiles.Count;
            int current = 0;

            // Use first selected item as base path for relative paths
            var basePath = items[0].IsDirectory ? items[0].FullPath : Path.GetDirectoryName(items[0].FullPath) ?? targetDir;

            using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var file in allFiles)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        progressWindow.Close();
                        ShowToast(LocalizationService.Instance.GetString("AddToZipCancelled"));
                        return;
                    }

                    current++;
                    var relativePath = Path.GetRelativePath(basePath, file);

                    var entry = archive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                    await fileStream.CopyToAsync(entryStream);
                    var percent = total == 1 ? 100 : (int)((double)current / total * 100);
                    var statusText = $"正在压缩: {relativePath}";
                    Dispatcher.Invoke(() => progressWindow.UpdateProgress(percent, statusText));
                    await Task.Yield();
                }
            }

            var vz = await _viewModel!.GlobalManager.ActiveManager!.Executor.ExecuteAsync(SvnCommand.IsVersioned, zipPath);
            if (!(vz.Success && vz.Value == "true"))
                await _viewModel!.GlobalManager.ActiveManager!.Executor.ExecuteAsync(SvnCommand.Add, zipPath);

            progressWindow.Close();
            ShowToast(LocalizationService.Instance.GetString("AddToZipSuccess", zipName));
            _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("AddToZipSuccess", zipName));
            await _viewModel.RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            progressWindow.Close();
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ShowToast(LocalizationService.Instance.GetString("AddToZipCancelled"));
        }
        catch (Exception ex)
        {
            progressWindow.Close();
            if (File.Exists(zipPath)) File.Delete(zipPath);
            Log.Error(ex, "AddToZip failed");
            ShowToast(LocalizationService.Instance.GetString("AddToZipFailed", ex.Message),
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
            _viewModel!.SetStatus(LocalizationService.Instance.GetString("AddToZipFailed", ex.Message));
        }
        finally
        {
            cts?.Dispose();
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

            await _viewModel!.GlobalManager.ActiveManager!.Executor.ExecuteAsync(SvnCommand.Add, newPath);
            ShowToast(LocalizationService.Instance.GetString("NewFileSuccess", defaultName));
            _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("NewFileSuccess", defaultName));
            _ = _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowToast(LocalizationService.Instance.GetString("NewFileFailed", ex.Message),
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
            _viewModel!.SetStatus(LocalizationService.Instance.GetString("NewFileFailed", ex.Message));
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



                // Enqueue as Move so QueueCommitProcessor resolves it correctly
                var syncService = _viewModel?.GlobalManager.ActiveManager?.SyncService;
                if (syncService != null)
                    syncService.EnqueueDeleteAsync(item.FullPath);
                if (syncService != null)
                    syncService.EnqueueAddAsync(newPath);

                // svn delete old + svn add new (marks the rename in working copy)
                // await _svnService.DeleteAsync(item.FullPath);
                // await _executor.ExecuteAsync(SvnCommand.Add, newPath);

                ShowToast(LocalizationService.Instance.GetString("RenameSuccess", $"{item.Name} -> {newName}"));
                _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("RenameSuccess", $"{item.Name} -> {newName}"));
                _ = _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                ShowToast(LocalizationService.Instance.GetString("RenameFailed", ex.Message),
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                _viewModel!.SetStatus(LocalizationService.Instance.GetString("RenameFailed", ex.Message));
                Log.Error(ex, "Rename failed: {Old} -> {New}", item.FullPath, newPath);
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        // Use selected items if multi-select is active, otherwise use the right-clicked item
        var items = _viewModel!.GetSelectedItemsForOperation().ToList();
        if (items.Count == 0)
        {
            var item = GetFileItemFromContextMenu(sender);
            if (item == null) return;
            items.Add(item);
        }
        await DeleteFilesAsync(items);
    }

    private async Task DeleteFilesAsync(List<FileItem> items)
    {
        if (items.Count == 0) return;
        var names = string.Join(", ", items.Select(i => i.Name));
        var result = MsgBox.Show(this,
            LocalizationService.Instance.GetString("DeleteConfirmMessage",
                items.Count > 1
                    ? $"{items.Count} {LocalizationService.Instance.GetString("Files")}"
                    : (items[0].IsDirectory
                        ? LocalizationService.Instance.GetString("Folder")
                        : LocalizationService.Instance.GetString("File")), names),
            LocalizationService.Instance.GetString("DeleteConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;


        try
        {
            var syncService = _viewModel!.GlobalManager.ActiveManager?.SyncService;
            foreach (var item in items)
            {
                if (item.IsDirectory || Directory.Exists(item.FullPath))
                    Directory.Delete(item.FullPath, recursive: true);
                else
                    File.Delete(item.FullPath);
                syncService?.EnqueueDeleteAsync(item.FullPath);
            }
            _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("DeleteSuccess", names));
            _viewModel.ClearSelection();
            _ = _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowToast(LocalizationService.Instance.GetString("DeleteFailed", ex.Message),
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
            _viewModel!.SetStatus(LocalizationService.Instance.GetString("DeleteFailed", ex.Message));
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
            _viewModel.SetStatus(LocalizationService.Instance.GetString("PasteFailed", ex.Message));
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
        var syncService = _viewModel?.GlobalManager.ActiveManager?.SyncService;
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
                _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("AnalysisCancelled"));
                return;
            }

            if (plan == null)
            {
                progressWindow.Close();
                ShowToast(LocalizationService.Instance.GetString("NoFilesToCopy"));
                _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("NoFilesToCopy"));
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
                _viewModel!.SetTransientStatus(LocalizationService.Instance.GetString("CopyCancelled"));
            }
            else if (result.HasError)
            {
                ShowToast(LocalizationService.Instance.GetString("CopyFailed", result.ErrorMessage ?? ""),
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                _viewModel!.SetStatus(LocalizationService.Instance.GetString("CopyFailed", result.ErrorMessage ?? ""));
            }
            else
            {
                var summary = result.SkippedCount == 0
                    ? LocalizationService.Instance.GetString("CopiedNItems", result.CopiedCount)
                    : LocalizationService.Instance.GetString("CopiedNItemsSkippedM", result.CopiedCount, result.SkippedCount);
                ShowToast(summary);
                _viewModel!.SetTransientStatus(summary);
            }

            // Trigger immediate commit (instead of waiting for next FullSync timer)
            _ = _viewModel!.RefreshAsync();
            syncService?.SyncNowAsync();
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
        var dialog = new SVNFileBox.Windows.AddLocalRepoWindow( _viewModel!.Repositories) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultRepository != null)
        {
            var repo = dialog.ResultRepository;
            _ = _viewModel.AddLocalRepositoryAsync(repo);
        }
    }

    private void Checkout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CheckoutWindow(_viewModel!.Repositories)
        {
            Owner = this
        };
        dialog.CheckoutPartial += (_, info) =>
        {
            // Partial checkout: save partial repo data so it isn't lost if user closes mid-checkout.
            var partialRepo = new Repository
            {
                Name = info.name,
                Path = info.path,
                Url = info.url,
                Username = info.username,
                IsActive = false,
                RepositoryType = RepositoryType.Network
            };
            _configService!.Config.Repositories.Add(partialRepo);
            _ = _configService.SaveAsync();
        };
        if (dialog.ShowDialog() == true)
        {
            var manager = dialog.ResultRepoManager;
            if (manager != null)
                _ = _viewModel!.AddNetworkRepositoryAsync(manager);
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
                    _viewModel!.RemoveRepository(repo);
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
                    SyncRecordService.Instance.DeleteRepoRecords(repo.Name);
                    _viewModel!.RemoveRepository(repo);
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

    /// <summary>Shows a toast notification via the system tray balloon tip.
    /// Queues notifications until TrayIcon is ready, then flushes them.</summary>
    public void ShowToast(string message, Hardcodet.Wpf.TaskbarNotification.BalloonIcon icon = Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info)
    {
        Dispatcher.Invoke(() =>
        {
            if (TrayIcon == null || TrayIcon.IsDisposed)
            {
                Log.Debug("[ShowToast] TrayIcon not ready, queueing notification: {Message}", message);
                _pendingToasts.Add((message, icon));
                return;
            }

            // TrayIcon is ready — mark it and flush any queued notifications
            if (!_toastIconReady)
            {
                _toastIconReady = true;
                Log.Information("[ShowToast] TrayIcon ready, flushing {Count} queued notifications", _pendingToasts.Count);
                foreach (var (queuedMsg, queuedIcon) in _pendingToasts)
                    TrayIcon.ShowBalloonTip("SVNFileBox", queuedMsg, queuedIcon);
                _pendingToasts.Clear();
            }

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
