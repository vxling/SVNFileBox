#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.ComponentModel;
using System.Windows.Media;
using SVNFileBox.Models;
using SVNFileBox.Services;
using Serilog;

namespace SVNFileBox.Windows;

public partial class CheckoutWindow : Window
{
    /// <summary>
    /// Checkout 成功后为非 null；credential renewal 模式下为 null。
    /// </summary>
    public RepoManager? ResultRepoManager { get; private set; }

    private readonly IReadOnlyList<Repository> _existingRepos;

    /// <summary>
    /// 在 OK_Click 中创建，checkout 成功后变为 ResultRepoManager，
    /// 失败或取消时 Dispose。
    /// </summary>
    private RepoManager? _pendingManager;
    private CancellationTokenSource? _checkoutCts;
    private bool _isCheckingOut;

    /// <summary>
    /// true = credential renewal mode (readonly repo name/URL, editable username/password)
    /// </summary>
    public bool IsCredentialRenewalMode { get; private set; }

    public string? RepoName => RepoNameBox.Text.Trim();
    public string? RepoUrl => RepoUrlBox.Text?.Trim();
    public string? Username => string.IsNullOrWhiteSpace(UsernameBox.Text) ? null : UsernameBox.Text.Trim();
    public string? Password => string.IsNullOrWhiteSpace(PasswordBox.Password) ? null : PasswordBox.Password;
    public string? LocalPath => _pendingManager?.Repository.Path;

    public event EventHandler<(string name, string path, string url, string username, string password)>? CheckoutPartial;

    public CheckoutWindow() : this(Array.Empty<Repository>()) { }

    public CheckoutWindow(IEnumerable<Repository> existingRepos)
    {
        _existingRepos = existingRepos.ToList().AsReadOnly();
        InitializeComponent();
        Loaded += (s, e) => RepoNameBox.Focus();
    }

    /// <summary>
    /// Opens the window in credential renewal mode.
    /// RepoName and RepoUrl are readonly; only username and password are editable.
    /// </summary>
    public void OpenCredentialRenewal(string repoName, string repoUrl, string currentUsername)
    {
        IsCredentialRenewalMode = true;
        Title = LocalizationService.Instance.GetString("CredentialRenewalTitle");
        StatusText.Text = LocalizationService.Instance.GetString("CredentialRenewalMessage");
        StatusText.Visibility = Visibility.Visible;

        // RepoName and RepoUrl are readonly
        RepoNameBox.Text = repoName;
        RepoNameBox.IsReadOnly = true;
        RepoNameBox.Background = Brushes.Gray;
        RepoUrlBox.Text = repoUrl;
        RepoUrlBox.IsReadOnly = true;
        RepoUrlBox.Background = Brushes.Gray;

        UsernameBox.Text = currentUsername;
        PasswordBox.Password = "";

        // Hide local path row (not needed in renewal mode)
        LocalPathRow.Visibility = Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => UpdateLocalPath();

    private void UpdateLocalPath()
    {
        var name = RepoNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            LocalPathBox.Text = LocalizationService.Instance.GetString("RepoNameRequired");
            _generatedLocalPath = null;
            return;
        }
        _generatedLocalPath = Path.Combine(AppPaths.WorkCopies, name);
        LocalPathBox.Text = _generatedLocalPath;
    }

    private void SetInputsEnabled(bool enabled)
    {
        RepoNameBox.IsEnabled = enabled;
        RepoUrlBox.IsEnabled = enabled;
        UsernameBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
        OkButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
    }

    private void StartCheckoutProgress()
    {
        _checkoutCts = new CancellationTokenSource();
        _isCheckingOut = true;
        SetInputsEnabled(false);
        CheckoutProgress.IsIndeterminate = false;
        CheckoutProgress.Value = 0;
        CheckoutProgress.Maximum = 100;
        CheckoutProgress.Visibility = Visibility.Visible;
        StatusText.Text = LocalizationService.Instance.GetString("CheckoutInProgress");
        StatusText.Visibility = Visibility.Visible;

        // 绑定 RepoManager 的 SvnService 文件传输进度
        _pendingManager!.SvnService.FileTransferActivity += OnFileTransferActivity;
    }

    private void StopCheckoutProgress()
    {
        _isCheckingOut = false;
        _pendingManager!.SvnService.FileTransferActivity -= OnFileTransferActivity;
        _checkoutCts?.Cancel();
        _checkoutCts?.Dispose();
        _checkoutCts = null;
        CheckoutProgress.Visibility = Visibility.Collapsed;
        SetInputsEnabled(true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isCheckingOut)
        {
            var confirmed = MsgBox.Show(
                this,
                LocalizationService.Instance.GetString("CheckoutInterruptedConfirm"),
                LocalizationService.Instance.GetString("CheckoutInterruptedTitle"),
                MessageBoxButtonType.YesNo,
                MessageBoxIconType.Warning);

            if (confirmed == MessageBoxResult.Yes)
            {
                // User confirmed close — signal partial checkout
                CheckoutPartial?.Invoke(this, (
                    RepoName ?? "",
                    _pendingManager?.Repository.Path ?? "",
                    RepoUrl ?? "",
                    Username ?? "",
                    Password ?? ""));
                e.Cancel = false;
            }
            else
            {
                e.Cancel = true;
            }
        }

        // 如果 checkout 未完成，丢弃 pending manager
        if (_pendingManager != null && ResultRepoManager == null)
        {
            _pendingManager.Dispose();
            _pendingManager = null;
        }

        base.OnClosing(e);
    }

    private void OnFileTransferActivity(string path, string action)
    {
        if (_checkoutCts == null || _checkoutCts.IsCancellationRequested) return;
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName)) return;
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"{LocalizationService.Instance.GetString("CheckingOut")} {fileName}";
        });
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
    }

    private string? _generatedLocalPath;

    private async void OK_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        // Credential renewal mode: 只验证凭据，不 checkout
        if (IsCredentialRenewalMode)
        {
            if (string.IsNullOrWhiteSpace(Username))
            {
                ShowError(LocalizationService.Instance.GetString("Username") + " required");
                return;
            }
            if (string.IsNullOrWhiteSpace(Password))
            {
                ShowError(LocalizationService.Instance.GetString("Password") + " required");
                return;
            }
            DialogResult = true;
            Close();
            return;
        }

        var repoName = RepoName;
        if (string.IsNullOrWhiteSpace(repoName))
        {
            ShowError(LocalizationService.Instance.GetString("RepoNameRequired"));
            return;
        }
        var invalidChars = Path.GetInvalidFileNameChars();
        if (repoName.IndexOfAny(invalidChars) >= 0)
        {
            ShowError(LocalizationService.Instance.GetString("RepoNameInvalid"));
            return;
        }
        if (string.IsNullOrWhiteSpace(RepoUrl))
        {
            ShowError(LocalizationService.Instance.GetString("RepoUrlRequired"));
            return;
        }

        UpdateLocalPath();
        if (string.IsNullOrEmpty(_generatedLocalPath)) return;

        if (Directory.Exists(_generatedLocalPath))
        {
            ShowError(LocalizationService.Instance.GetString("LocalPathExists"));
            return;
        }

        if (_existingRepos.Any(r => r.Url.Equals(RepoUrl, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(LocalizationService.Instance.GetString("DuplicateRepoUrl"));
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_generatedLocalPath)!);
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Instance.GetString("CannotCreateDir", ex.Message));
            return;
        }

        // ── 创建 RepoManager（使用临时 Repository，之后填入真实数据）────────────
        var tempRepo = new Repository
        {
            Name = repoName,
            Path = _generatedLocalPath,
            Url = RepoUrl!,
            Username = Username ?? "",
            Password = Password ?? "",
            IsActive = false,
            RepositoryType = RepositoryType.Network
        };
        _pendingManager = new RepoManager(tempRepo);

        // Start progress tracking before checkout
        StartCheckoutProgress();

        // ── 测连接 ───────────────────────────────────────────────────────────
        var connResult = await _pendingManager.Executor.ExecuteAsync(
            SvnCommand.TestConnection, RepoUrl!,
            username: Username, password: Password);

        if (!connResult.Success || connResult.Error != null)
        {
            var errorName = connResult.Error ?? "Unknown";
            var errorDetail = connResult.Value;

            string msgKey = errorName switch
            {
                "AuthFailed" => "ErrAuthFailed",
                "AccessDenied" => "ErrAccessDenied",
                "RepoNotFound" => "ErrRepoNotFound",
                "NetworkError" => "ErrNetworkError",
                "SslCertError" => "ErrSslCertError",
                "Timeout" => "ErrTimeout",
                _ => "ErrUnknown",
            };
            var msg = !string.IsNullOrEmpty(errorDetail)
                ? string.Format(LocalizationService.Instance.GetString(msgKey), errorDetail)
                : LocalizationService.Instance.GetString(msgKey);
            ShowError(msg);
            StopCheckoutProgress();
            _pendingManager.Dispose();
            _pendingManager = null;
            return;
        }

        // ── 执行 Checkout ────────────────────────────────────────────────────
        try
        {
            var coResult = await _pendingManager.Executor.ExecuteAsync(
                SvnCommand.Checkout, _generatedLocalPath,
                repoUrl: RepoUrl!, username: Username, password: Password);

            if (!coResult.Success)
            {
                ShowError($"{LocalizationService.Instance.GetString("CheckoutFailed")}: {coResult.Error ?? "unknown error"}");
                try { Directory.Delete(_generatedLocalPath, recursive: true); } catch { }
                StopCheckoutProgress();
                _pendingManager.Dispose();
                _pendingManager = null;
                return;
            }

            Log.Information("Checkout successful: {Url} -> {Path}", RepoUrl, _generatedLocalPath);

            // 真实数据写回 Repository（checkout 过程中 URL 可能被解析成标准形式）
            _pendingManager.Repository.Url = RepoUrl!;
            _pendingManager.Repository.Username = Username ?? "";
            _pendingManager.Repository.Password = Password ?? "";

            // Checkout 成功：将 pending manager 提升为 result
            ResultRepoManager = _pendingManager;
            StopCheckoutProgress();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
                ShowError("Checkout was cancelled.");
            else
                ShowError($"{LocalizationService.Instance.GetString("CheckoutFailed")}: {ex.Message}");
            StopCheckoutProgress();
            _pendingManager.Dispose();
            _pendingManager = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
