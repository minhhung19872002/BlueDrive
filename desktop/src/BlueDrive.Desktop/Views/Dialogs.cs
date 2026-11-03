using Avalonia.Controls;
using Avalonia.Layout;

namespace BlueDrive.Desktop.Views;

public static class Dialogs
{
    /// <summary>Simple "are you sure" dialog; true when the user chose <paramref name="ok"/>.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string text, string ok)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var okButton = new Button { Content = ok, Classes = { "accent" } };
        var cancel = new Button { Content = "Hủy" };
        okButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, okButton } },
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
