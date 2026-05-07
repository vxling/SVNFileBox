using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SVNFileBox.Models;

public partial class SyncRecord : ObservableObject
{
    [ObservableProperty]
    private DateTime _timestamp = DateTime.Now;

    [ObservableProperty]
    private string _repoName = "";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _operation = ""; // Add / Update / Delete / Rename / ConflictResolved

    [ObservableProperty]
    private string _result = ""; // Success / Failed / Skipped

    [ObservableProperty]
    private string _message = "";
}
