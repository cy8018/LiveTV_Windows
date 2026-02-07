using System.Windows;
using System.Windows.Input;

namespace IPTVPlayer.Views;

public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; } = false;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        DialogResult = true;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        DialogResult = false;
        Close();
    }

    public static bool Show(Window owner, string title, string message)
    {
        var dialog = new ConfirmDialog(title, message);
        dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }
}
