using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BlueDrive.Desktop.ViewModels;

namespace BlueDrive.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow() : this(new SettingsViewModel(new Services.AppHost(Path.Combine(Path.GetTempPath(), "bluedrive-designer")), null)) { }

    public SettingsWindow(SettingsViewModel vm)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();
        vm.Confirm = (title, text, ok) => Dialogs.ConfirmAsync(this, title, text, ok);
        vm.SaveFile = async name =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Lưu gói chẩn đoán", SuggestedFileName = name, DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("Tệp nén ZIP") { Patterns = ["*.zip"] }],
            });
            return file?.TryGetLocalPath();
        };
        Activated += (_, _) => vm.Refresh();
    }
}
