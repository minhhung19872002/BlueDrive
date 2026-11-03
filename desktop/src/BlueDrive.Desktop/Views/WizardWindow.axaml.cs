using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BlueDrive.Desktop.ViewModels;

namespace BlueDrive.Desktop.Views;

public partial class WizardWindow : Window
{
    public WizardViewModel ViewModel { get; }

    public WizardWindow() : this(new WizardViewModel(new Services.AppHost(Path.Combine(Path.GetTempPath(), "bluedrive-designer")), null)) { }

    public WizardWindow(WizardViewModel vm)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Close += Close;
        Closed += (_, _) => vm.Abandon();
        this.FindControl<Button>("BrowseButton")!.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Chọn vị trí thư mục BlueDrive", AllowMultiple = false });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.Folder = Path.Combine(path, "BlueDrive");
        };
    }
}
