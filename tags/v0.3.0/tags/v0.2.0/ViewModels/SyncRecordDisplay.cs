using CommunityToolkit.Mvvm.ComponentModel;
using SVNFileBox.Models;

namespace SVNFileBox.ViewModels;

public partial class SyncRecordDisplay : ObservableObject
{
    [ObservableProperty]
    private string _timestampDisplay = "";

    [ObservableProperty]
    private string _repoName = "";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _operationDisplay = "";

    [ObservableProperty]
    private string _resultDisplay = "";

    [ObservableProperty]
    private string _message = "";

    public static SyncRecordDisplay FromRecord(SyncRecord r)
    {
        var opColor = r.Operation switch
        {
            "Add" => "🟢 Add",
            "Update" => "🔵 Update",
            "Delete" => "🔴 Delete",
            "Rename" => "🟡 Rename",
            "ConflictResolved" => "🟠 Conflict",
            _ => r.Operation
        };

        var resultColor = r.Result switch
        {
            "Success" => "✅ Success",
            "Failed" => "❌ Failed",
            "Skipped" => "⏭️ Skipped",
            _ => r.Result
        };

        return new SyncRecordDisplay
        {
            TimestampDisplay = r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            RepoName = r.RepoName,
            FilePath = r.FilePath,
            OperationDisplay = opColor,
            ResultDisplay = resultColor,
            Message = r.Message
        };
    }
}