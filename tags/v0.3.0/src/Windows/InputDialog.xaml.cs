#nullable enable
using System.Windows;

namespace SVNFileBox.Windows;

public partial class InputDialog : Window
{
    public string? InputText => InputTextBox.Text;

    public InputDialog()
    {
        InitializeComponent();
    }

    public void SetPrompt(string text)
    {
        PromptText.Text = text;
    }

    public void SetInput(string text)
    {
        InputTextBox.Text = text;
        InputTextBox.SelectAll();
        InputTextBox.Focus();
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