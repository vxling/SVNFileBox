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
    private readonly IRepositoryContext _repoContext;
    private readonly IReadOnlyList<Repository> _existingRepos;
    private string? _generatedLocalPath;

    public string? RepoName => RepoNameBox.Text.Trim();
    public string? RepoUrl => RepoUrlBox.Text?.Trim();
    public string? Username => string.IsNullOrWhiteSpace(UsernameBox.Text) ? null : UsernameBox.Text.Trim();
    public string? Password => string.IsNullOrWhiteSpace(PasswordBox.Password) ? null : PasswordBox.Password;
    public string? LocalPath => _generatedLocalPath;

    public CheckoutWindow() : this(new RepositoryContext(), Array.Empty<Repository>()) { }

    public CheckoutWindow(IRepositoryContext repoContext, IEnumerable<Repository> existingRepos)
    {
        _repoContext = repoContext;
        _existingRepos = existingRepos.ToList().AsReadOnly();
        InitializeComponent();
        Loaded += (s, e) => RepoNameBox.Focus();
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

        SetLoading(true, LocalizationService.Instance.GetString("CheckoutInProgress"));

        // Connection test first
        var connResult = await _repoContext.Executor.ExecuteAsync(SvnCommand.TestConnection, RepoUrl!, username: Username, password: Password);
        if (!connResult.Success || connResult.Error != null)
        {
            // TestConnection failure: Success=true but Error field holds the result name or error detail
            var errorName = connResult.Error ?? "Unknown";
            var errorDetail = connResult.Value;  // additional context if any

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
            SetLoading(false);
            return;
        }

        try
        {
            var coResult = await _repoContext.Executor.ExecuteAsync(SvnCommand.Checkout, _generatedLocalPath,
                repoUrl: RepoUrl!, username: Username, password: Password);

            if (!coResult.Success)
            {
                ShowError($"{LocalizationService.Instance.GetString("CheckoutFailed")}: {coResult.Error ?? "unknown error"}");
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