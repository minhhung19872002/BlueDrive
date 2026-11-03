using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using BlueDrive.Desktop.ViewModels;

namespace BlueDrive.Desktop.Views;

public partial class FlyoutWindow : Window
{
    public FlyoutViewModel ViewModel { get; }

    public FlyoutWindow() : this(new FlyoutViewModel(new Services.AppHost(Path.Combine(Path.GetTempPath(), "bluedrive-designer")), null)) { }

    public FlyoutWindow(FlyoutViewModel vm)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();
        // Like the OneDrive flyout: it goes away when the user clicks elsewhere.
        Deactivated += (_, _) =>
        {
            // Opening from another process may not get the focus at once: do not vanish right after showing.
            if (DateTime.UtcNow - _shownAt > TimeSpan.FromSeconds(1)) Hide();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
        };
        var list = this.FindControl<ListBox>("ActivityList")!;
        list.DoubleTapped += (_, _) => vm.OpenActivityCommand.Execute(list.SelectedItem as ActivityItem);
    }

    /// <summary>Bottom-right of the work area (above the taskbar), like the OneDrive flyout.</summary>
    private DateTime _shownAt;

    public void ShowNearTray()
    {
        _shownAt = DateTime.UtcNow;
        Show();
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            Position = new PixelPoint(area.Right - (int)((Width + 12) * scale), area.Bottom - (int)((Height + 12) * scale));
        }
        Activate();
    }
}
