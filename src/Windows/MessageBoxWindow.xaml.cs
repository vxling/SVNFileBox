#nullable enable

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SVNFileBox.Services;

namespace SVNFileBox.Windows;

public enum MessageBoxIconType { Info, Warning, Error, Question, Success }

public enum MessageBoxButtonType { OK, YesNo, OKCancel, YesNoCancel }

public partial class MsgBox : Window
{
    public new string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public MsgBox()
    {
        DataContext = this;
        InitializeComponent();
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButtonType buttons = MessageBoxButtonType.OK,
        MessageBoxIconType icon = MessageBoxIconType.Info)
    {
        var msgbox = new MsgBox
        {
            Title = title,
            Message = message,
            Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };

        msgbox.SetIcon(icon);
        msgbox.BuildButtons(buttons);
        msgbox.ShowDialog();
        return msgbox.Result;
    }

    /// <summary>
    /// Convenience overload matching System.Windows.MessageBox.Show signature.
    /// </summary>
    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton button = MessageBoxButton.OK,
        System.Windows.MessageBoxImage image = System.Windows.MessageBoxImage.None)
    {
        var (btnType, iconType) = ConvertParams(button, image);
        return Show(null, message, title, btnType, iconType);
    }

    /// <summary>
    /// Convenience overload with owner window.
    /// </summary>
    public static MessageBoxResult Show(
        Window owner,
        string message,
        string title,
        MessageBoxButton button = MessageBoxButton.OK,
        System.Windows.MessageBoxImage image = System.Windows.MessageBoxImage.None)
    {
        var (btnType, iconType) = ConvertParams(button, image);
        return Show(owner, message, title, btnType, iconType);
    }

    private static (MessageBoxButtonType, MessageBoxIconType) ConvertParams(
        MessageBoxButton button, System.Windows.MessageBoxImage image)
    {
        var btn = button switch
        {
            MessageBoxButton.OK => MessageBoxButtonType.OK,
            MessageBoxButton.YesNo => MessageBoxButtonType.YesNo,
            MessageBoxButton.OKCancel => MessageBoxButtonType.OKCancel,
            MessageBoxButton.YesNoCancel => MessageBoxButtonType.YesNoCancel,
            _ => MessageBoxButtonType.OK
        };
        var icon = image switch
        {
            System.Windows.MessageBoxImage.Warning => MessageBoxIconType.Warning,
            System.Windows.MessageBoxImage.Error => MessageBoxIconType.Error,
            System.Windows.MessageBoxImage.Question => MessageBoxIconType.Question,
            System.Windows.MessageBoxImage.Information => MessageBoxIconType.Info,
            System.Windows.MessageBoxImage.None => MessageBoxIconType.Info,
            _ => MessageBoxIconType.Info
        };
        return (btn, icon);
    }

    private void SetIcon(MessageBoxIconType icon)
    {
        var (symbol, color) = icon switch
        {
            MessageBoxIconType.Info => ("i", new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3))),
            MessageBoxIconType.Warning => ("!", new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00))),
            MessageBoxIconType.Error => ("×", new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35))),
            MessageBoxIconType.Question => ("?", new SolidColorBrush(Color.FromRgb(0x0, 0x72, 0xC9))),
            MessageBoxIconType.Success => ("✓", new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))),
            _ => ("i", new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)))
        };

        IconText.Text = symbol;
        IconText.Foreground = color;
    }

    private void BuildButtons(MessageBoxButtonType buttonType)
    {
        ButtonsPanel.Children.Clear();
        var ls = LocalizationService.Instance;

        var btnDefs = buttonType switch
        {
            MessageBoxButtonType.OK => new[] { ("OK", MessageBoxResult.OK) },
            MessageBoxButtonType.YesNo => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No) },
            MessageBoxButtonType.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) },
            MessageBoxButtonType.YesNoCancel => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) },
            _ => new[] { ("OK", MessageBoxResult.OK) }
        };

        foreach (var (key, result) in btnDefs)
        {
            var localizedText = ls.GetString(key);
            var btn = new Button
            {
                Content = localizedText,
                MinWidth = 80,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(6, 0, 0, 0),
                Tag = result
            };
            btn.Click += Button_Click;
            ButtonsPanel.Children.Add(btn);
        }

        if (ButtonsPanel.Children.Count > 0)
            ((Button)ButtonsPanel.Children[0]).Focus();
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        Result = (MessageBoxResult)((Button)sender).Tag;
        DialogResult = true;
    }
}
