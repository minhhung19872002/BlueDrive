using Avalonia.Controls;
using BlueDrive.Desktop.ViewModels;

namespace BlueDrive.Desktop.Views;

public partial class ShareWindow : Window
{
    public ShareWindow() => InitializeComponent();

    public ShareWindow(ShareViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        vm.CopyToClipboard = async text =>
        {
            if (Clipboard is { } c) await c.SetTextAsync(text);
        };
    }
}
