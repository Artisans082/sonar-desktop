using System.Windows;
using System.Windows.Input;

namespace Sonar.Views;

public partial class TextViewerWindow : Window
{
    public TextViewerWindow(string title, string body, Window? owner = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BodyText.Text);
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }
}
