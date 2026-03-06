using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace HandyPlaylistPlayer.App.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
    }

    public InputDialog(string prompt, string defaultValue = "") : this()
    {
        PromptText.Text = prompt;
        InputText.Text = defaultValue;
        InputText.SelectAll();
        InputText.AttachedToVisualTree += (_, _) => InputText.Focus();
        InputText.KeyDown += OnInputKeyDown;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Close(InputText.Text?.Trim());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(InputText.Text?.Trim());
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
