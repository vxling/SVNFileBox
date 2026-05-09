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
using System.Windows.Input;
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

    private bool CanPaste => _viewModel != null
        && _viewModel.CanOperate
        && !string.IsNullOrEmpty(_viewModel.CurrentPath)
        && Directory.Exists(_viewModel.CurrentPath)
        && Clipboard.ContainsFileDropList();

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Only paste needs clipboard check — all other items use XAML bindings
        if (sender is ContextMenu cm && _viewModel != null)
        {
            foreach (var item in cm.Items)
            {
                if (item is MenuItem mi && mi.Name == "PasteMenuItem")
                {
                    mi.IsEnabled = _viewModel.CanOperate && Clipboard.ContainsFileDropList();
                    break;
                }
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
        DataContext = _viewModel;

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
            StatusText.Text = $"正在处理 {conflicts.Count} 个冲突文件...";
            var handled = await _viewModel.SyncService.ApplyConflictResolutionsAsync(conflicts);
            _viewModel.RecordService.AddRecord(
                _viewModel.SelectedRepository?.Name ?? "",
                "", "ConflictResolved", "Success", $"Resolved {handled} conflict(s)");
            StatusText.Text = $"冲突处理完成：{handled} 个";
            await _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Conflict resolution failed");
            StatusText.Text = "冲突处理失败";
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
                    _viewModel!.StatusText = $"打开失败: {ex.Message}";
                }
            }
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.RefreshAsync();
    }

    private async void ManualSync_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SyncService == null) return;
        _viewModel!.StatusText = "正在手工同步...";
        try
        {
            await _viewModel.SyncService.SyncNowAsync();
            _viewModel.StatusText = "同步完成";
            await _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Manual sync failed");
            _viewModel.StatusText = $"同步失败: {ex.Message}";
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
            _viewModel!.StatusText = $"已复制: {path}";
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
                    System.Windows.MessageBox.Show(this, $"文件夹 \"{dialog.InputText.Trim()}\" 已存在", "新建文件夹", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                Directory.CreateDirectory(newFolderPath);

                // svn add
                await _svnService.AddFileAsync(newFolderPath);
                var msg = $"Auto-sync: [Add] {Path.GetFileName(newFolderPath)}";
                await _svnService.CommitAsync(targetDir, msg);

                _viewModel!.StatusText = LocalizationService.Instance.GetString("NewFolderSuccess", dialog.InputText.Trim());
                _ = _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                _viewModel!.StatusText = LocalizationService.Instance.GetString("NewFolderFailed", ex.Message);
            }
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var item = GetFileItemFromContextMenu(sender);
        if (item == null || item.Name == "..") return;

        var dialog = new Windows.InputDialog
        {
            Title = "重命名",
            Owner = this
        };
        dialog.SetPrompt("新名称:");
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
                    System.Windows.MessageBox.Show(this, $"名称 \"{newName}\" 已被占用", "重命名", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (Directory.Exists(item.FullPath) || File.Exists(item.FullPath))
                    Directory.Move(item.FullPath, newPath);
                else
                    return;

                // svn rename
                var msg = $"Auto-sync: [Rename] {item.Name} -> {newName}";
                _ = _svnService.CommitAsync(parentDir, msg);

                _viewModel!.StatusText = $"已重命名: {item.Name} -> {newName}";
                _ = _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                _viewModel!.StatusText = $"重命名失败: {ex.Message}";
                Log.Error(ex, "Rename failed: {Old} -> {New}", item.FullPath, newPath);
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = GetFileItemFromContextMenu(sender);
        if (item == null) return;

        var result = MessageBox.Show(
            LocalizationService.Instance.GetString("DeleteConfirmMessage",
                item.IsDirectory ? "文件夹" : "文件", item.Name),
            LocalizationService.Instance.GetString("DeleteConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var parentDir = Path.GetDirectoryName(item.FullPath);
                if (item.IsDirectory || Directory.Exists(item.FullPath))
                    Directory.Delete(item.FullPath, recursive: true);
                else
                    File.Delete(item.FullPath);

                // svn delete
                if (!string.IsNullOrEmpty(parentDir))
                {
                    var msg = $"Auto-sync: [Delete] {item.Name}";
                    await _svnService.CommitAsync(parentDir, msg);
                }

                _viewModel!.StatusText = LocalizationService.Instance.GetString("DeleteSuccess", item.Name);
                _ = _viewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
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
            _viewModel.StatusText = LocalizationService.Instance.GetString("PasteFailed", ex.Message);
        }
    }

    private async Task ExecuteCopyAsync(IList<string> sourcePaths, string targetDir)
    {
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

        try
        {
            // Phase 1: Analyze in background, report progress per item
            var analysisProgress = new Progress<string>(item => progressWindow.StartAnalysis(item));

            FileCopyPlan? plan;
            CancellationToken analysisToken = cts.Token;

            try
            {
                plan = await Task.Run(() => _fileAnalyzer.Analyze(sourcePaths, targetDir, analysisProgress, analysisToken), analysisToken);
            }
            catch (OperationCanceledException)
            {
                progressWindow.Close();
                _viewModel.StatusText = "已取消分析";
                return;
            }

            if (plan == null)
            {
                progressWindow.Close();
                _viewModel.StatusText = "没有文件可复制";
                return;
            }

            if (plan.IsSameLocation)
            {
                progressWindow.Close();
                MessageBox.Show(this, "源和目标位置相同，无法复制。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Phase 2: Copy in background, report progress per file
            progressWindow.StartCopy();
            var copyProgress = new Progress<CopyProgress>(p => progressWindow.UpdateProgress(p));

            var result = await Task.Run(() => _fileCopier.CopyAsync(plan, copyProgress));

            progressWindow.Stop();

            if (result.WasCancelled)
            {
                _viewModel.StatusText = "已取消复制";
            }
            else if (result.HasError)
            {
                _viewModel.StatusText = $"复制失败: {result.ErrorMessage}";
            }
            else
            {
                var summary = result.SkippedCount == 0
                    ? $"已复制 {result.CopiedCount} 个项目"
                    : $"已复制 {result.CopiedCount} 个，跳过 {result.SkippedCount} 个";
                _viewModel.StatusText = summary;
            }

            _ = _viewModel.RefreshAsync();
        }
        finally
        {
            progressWindow.Close();
            progressWindow.Closing -= OnWindowClosing;
            syncService?.ReEnableFileWatcher();
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
            var result = MessageBox.Show(
                $"确定要移除仓库 \"{repo.Name}\"？\n本地文件不会删除。",
                "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question);

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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
            return;
        // Hide to tray instead of closing
        e.Cancel = true;
        Hide();
        if (TrayIcon != null && !TrayIcon.IsDisposed)
            TrayIcon.ShowBalloonTip("SVNFileBox", "已最小化到托盘，双击恢复", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
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

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    // Note: CanExecuteChanged is intentionally a no-op because CanExecute always returns true.
    // WPF/ICommand infrastructure will never block command execution, so this is safe.
    private class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add {} remove {} }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }
}