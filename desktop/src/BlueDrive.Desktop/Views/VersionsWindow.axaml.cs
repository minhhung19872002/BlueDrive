using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BlueDrive.Desktop.ViewModels;

namespace BlueDrive.Desktop.Views;

public partial class VersionsWindow : Window
{
    public VersionsWindow() => InitializeComponent();

    public VersionsWindow(VersionsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        vm.SaveAs = async name =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Lưu bản sao phiên bản", SuggestedFileName = name });
            return file?.TryGetLocalPath();
        };
    }
}
