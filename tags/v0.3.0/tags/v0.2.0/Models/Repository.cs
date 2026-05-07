using CommunityToolkit.Mvvm.ComponentModel;

namespace SVNFileBox.Models;

public partial class Repository : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string _url = "";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _encryptedPassword = "";

    [ObservableProperty]
    private bool _isActive;
}
