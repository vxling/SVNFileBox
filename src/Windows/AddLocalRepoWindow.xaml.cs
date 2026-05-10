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

public partial class AddLocalRepoWindow : Window
{
    private readonly SvnService _svnService = new();
    private readonly IReadOnlyList<Repository> _existingRepos;

    public Repository? ResultRepository { get; private set; }

    public AddLocalRepoWindow() : this(Array.Empty<Repository>()) { }

    public AddLocalRepoWindow(IEnumerable<Repository> existingRepos)
    {
        _existingRepos = existingRepos.ToList().AsReadOnly();
        InitializeComponent();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.Instance.GetString("SelectFolderTitle")
        };

        if (dialog.ShowDialog() == true)
        {
            LocalPathBox.Text = dialog.FolderName;
            ValidatePath();
        }
    }

    private void ValidatePath()
    {
        ErrorText.Text = "";
        var path = LocalPathBox.Text?.Trim();

        if (string.IsNullOrEmpty(path))
        {
            ErrorText.Text = LocalizationService.Instance.GetString("PleaseSelectDir");
            return;
        }

        // Check duplicate by local path
        if (_existingRepos.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorText.Text = LocalizationService.Instance.GetString("LocalPathAlreadyAdded");
            return;
        }

        if (!_svnService.IsValidWorkingCopy(path))
        {
            ErrorText.Text = LocalizationService.Instance.GetString("NotValidWorkingCopy");
            return;
        }
    }

    private void SetLoading(bool loading, string statusMessage = "")
    {
        OkButton.IsEnabled = !loading;
        CancelButton.IsEnabled = !loading;
        StatusText.Text = statusMessage;
        StatusText.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = "";
    }

    private async void OK_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var path = LocalPathBox.Text?.Trim();

        if (string.IsNullOrEmpty(path))
        {
            ErrorText.Text = LocalizationService.Instance.GetString("PleaseSelectDir");
            return;
        }

        // Check duplicate by local path
        if (_existingRepos.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorText.Text = LocalizationService.Instance.GetString("LocalPathAlreadyAdded");
            return;
        }

        if (!_svnService.IsValidWorkingCopy(path))
        {
            ErrorText.Text = LocalizationService.Instance.GetString("NotValidWorkingCopy");
            return;
        }

        // Get repo URL — show progress, disable buttons
        SetLoading(true, LocalizationService.Instance.GetString("CheckingRepoUrl"));

        try
        {
            var url = await _svnService.GetRepoUrlAsync(path);
            var name = new DirectoryInfo(path).Name;

            ResultRepository = new Repository
            {
                Name = name,
                Path = path,
                Url = url,
                IsActive = false,
                RepositoryType = RepositoryType.Local
            };

            Log.Information("Added local repo: {Name} at {Path}", name, path);
            DialogResult = true;
            Close();
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
