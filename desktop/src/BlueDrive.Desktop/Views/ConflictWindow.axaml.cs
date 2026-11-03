using Avalonia.Controls;
using BlueDrive.Desktop.ViewModels;

namespace BlueDrive.Desktop.Views;

public partial class ConflictWindow : Window
{
    public ConflictWindow() => InitializeComponent();

    public ConflictWindow(ConflictViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        vm.Done += Close;
    }
}
