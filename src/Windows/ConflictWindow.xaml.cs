#nullable enable
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Windows;

public partial class ConflictWindow : Window
{
    public ObservableCollection<ConflictedFileInfo> ConflictFiles { get; set; }

    public ConflictWindow()
    {
        ConflictFiles = new ObservableCollection<ConflictedFileInfo>();
        InitializeComponent();
        DataContext = this;
    }

    public void SetConflicts(IEnumerable<ConflictedFileInfo> conflicts)
    {
        ConflictFiles.Clear();
        foreach (var c in conflicts)
            ConflictFiles.Add(c);
    }

    private void KeepAllLocal_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in ConflictFiles)
            c.SelectedResolution = ConflictResolution.KeepLocal;
        // Force UI refresh
        var list = ConflictFiles;
        ConflictFiles = new ObservableCollection<ConflictedFileInfo>(list);
        DataContext = null;
        DataContext = this;
    }

    private void AcceptAllServer_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in ConflictFiles)
        {
            // Tree conflicts can only be resolved with Working (keep local) in SVN.
            // Force KeepLocal for them regardless of the button label.
            c.SelectedResolution = c.IsTreeConflict
                ? ConflictResolution.KeepLocal
                : ConflictResolution.AcceptServer;
        }
        var list = ConflictFiles;
        ConflictFiles = new ObservableCollection<ConflictedFileInfo>(list);
        DataContext = null;
        DataContext = this;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Log.Information("[ConflictWindow] User deferred — conflicts left unresolved, will retry on next poll");
        Close();
    }
}
