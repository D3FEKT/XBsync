using System.Runtime.InteropServices;
using System.IO;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using XBsync.Services;
using XBsync.ViewModels;
using XBsync.Views;

namespace XBsync;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, DesktopUiService desktop)
    {
        InitializeComponent();
        Title = "XBsync";

        var page = new MainPage(viewModel);
        Content = page;
        desktop.Attach(this, page);
        page.ActualThemeChanged += (_, _) => ApplyCaptionTheme(page);
        ApplyCaptionTheme(page);

        AppWindow.Resize(new SizeInt32(1800, 1200));
        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            ApiInformation.IsPropertyPresent("Microsoft.UI.Windowing.OverlappedPresenter", "PreferredMinimumWidth") &&
            ApiInformation.IsPropertyPresent("Microsoft.UI.Windowing.OverlappedPresenter", "PreferredMinimumHeight"))
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 650;
        }

        TrySetIcon();

        if (MicaController.IsSupported())
        {
            page.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else
        {
            SystemBackdrop = null;
        }
    }

    private void TrySetIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Cosmetic only; never block startup on a missing or unreadable icon.
        }
    }

    private void ApplyCaptionTheme(FrameworkElement root)
    {
        var dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
