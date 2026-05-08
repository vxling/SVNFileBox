#nullable enable
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows;
using SVNFileBox.Models;

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
            c.SelectedResolution = ConflictResolution.AcceptServer;
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
        Close();
    }
}
