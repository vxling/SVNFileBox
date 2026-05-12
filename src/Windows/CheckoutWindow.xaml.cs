#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using SVNFileBox.Models;
using SVNFileBox.Services;
using Serilog;

namespace SVNFileBox.Windows;

public partial class CheckoutWindow : Window
{
    private readonly SvnService _svnService = new();
    private readonly IReadOnlyList<Repository> _existingRepos;
    private string? _generatedLocalPath;

    public string? RepoName => RepoNameBox.Text.Trim();
    public string? RepoUrl => RepoUrlBox.Text?.Trim();
    public string? Username => string.IsNullOrWhiteSpace(UsernameBox.Text) ? null : UsernameBox.Text.Trim();
    public string? Password => string.IsNullOrWhiteSpace(PasswordBox.Password) ? null : PasswordBox.Password;
    public string? LocalPath => _generatedLocalPath;

    public CheckoutWindow() : this(Array.Empty<Repository>()) { }

    public CheckoutWindow(IEnumerable<Repository> existingRepos)
    {
        _existingRepos = existingRepos.ToList().AsReadOnly();
        InitializeComponent();
        Loaded += (s, e) => RepoNameBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // Local path is auto-generated, user can't manually choose
        UpdateLocalPath();
    }

    private void UpdateLocalPath()
    {
        var name = RepoNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            LocalPathBox.Text = LocalizationService.Instance.GetString("RepoNameRequired");
            _generatedLocalPath = null;
            return;
        }

        _generatedLocalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNFileBox", "workcopies", name);

        LocalPathBox.Text = _generatedLocalPath;
    }

    private void SetLoading(bool loading, string statusMessage = "")
    {
        OkButton.IsEnabled = !loading;
        CancelButton.IsEnabled = !loading;
        StatusText.Text = statusMessage;
        StatusText.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
    }

    private async void OK_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        // Validation
        var repoName = RepoName;
        if (string.IsNullOrWhiteSpace(repoName))
        {
            ShowError(LocalizationService.Instance.GetString("RepoNameRequired"));
            return;
        }
        // Check invalid path characters
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

        // Auto-generate local path
        UpdateLocalPath();
        if (string.IsNullOrEmpty(_generatedLocalPath)) return;

        // Check if already exists
        if (Directory.Exists(_generatedLocalPath))
        {
            ShowError(LocalizationService.Instance.GetString("LocalPathExists"));
            return;
        }

        // Check duplicate by URL
        if (_existingRepos.Any(r => r.Url.Equals(RepoUrl, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(LocalizationService.Instance.GetString("DuplicateRepoUrl"));
            return;
        }

        // Create parent directory
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_generatedLocalPath)!);
        }
        catch (Exception ex)
        {
            ShowError(LocalizationService.Instance.GetString("CannotCreateDir", ex.Message));
            return;
        }

        SetLoading(true, LocalizationService.Instance.GetString("CheckoutInProgress"));

        // First: lightweight connection test to give specific error feedback
        var (connectResult, connectError) = await _svnService.TestConnectionAsync(RepoUrl!, Username, Password);
        if (connectResult != SvnService.SvnConnectResult.Success)
        {
            string msgKey = connectResult switch
            {
                SvnService.SvnConnectResult.AuthFailed => "ErrAuthFailed",
                SvnService.SvnConnectResult.AccessDenied => "ErrAccessDenied",
                SvnService.SvnConnectResult.RepoNotFound => "ErrRepoNotFound",
                SvnService.SvnConnectResult.NetworkError => "ErrNetworkError",
                SvnService.SvnConnectResult.SslCertError => "ErrSslCertError",
                SvnService.SvnConnectResult.Timeout => "ErrTimeout",
                _ => "ErrUnknown",
            };
            var msg = connectResult == SvnService.SvnConnectResult.Unknown && !string.IsNullOrEmpty(connectError)
                ? string.Format(LocalizationService.Instance.GetString(msgKey), connectError)
                : LocalizationService.Instance.GetString(msgKey);
            ShowError(msg);
            SetLoading(false);
            return;
        }

        try
        {
            var (output, exitCode, error) = await _svnService.CheckoutAsync(
                RepoUrl!, _generatedLocalPath, Username, Password);

            if (exitCode != 0)
            {
                ShowError($"{LocalizationService.Instance.GetString("CheckoutFailed")}: {error}");
                try { Directory.Delete(_generatedLocalPath, recursive: true); } catch { }
                return;
            }

            Log.Information("Checkout successful: {Url} -> {Path}", RepoUrl, _generatedLocalPath);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"{LocalizationService.Instance.GetString("CheckoutFailed")}: {ex.Message}");
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
