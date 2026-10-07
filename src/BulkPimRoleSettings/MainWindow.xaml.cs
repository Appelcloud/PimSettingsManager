using System;
using System.IO;
using BulkPimRoleSettings.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace BulkPimRoleSettings;

public sealed partial class MainWindow : Window
{
    private const int PreferredWidth = 1300;
    private const int PreferredHeight = 900;
    private const int MinimumWidth = 900;
    private const int MinimumHeight = 600;

    public static IntPtr Hwnd { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        Hwnd = WindowNative.GetWindowHandle(this);

        var appWindow = this.AppWindow;

        // Stop the user from shrinking the window below the point where the
        // step content no longer fits, and keep the maximize/restore cycle usable.
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinimumWidth;
            presenter.PreferredMinimumHeight = MinimumHeight;
        }

        appWindow.Resize(GetInitialSize(appWindow));

        // Resolve against the EXE folder - a relative path breaks when the app
        // is launched from a shortcut whose working directory differs.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "favicon.ico");
        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);

        Title = "PIMSettings Manager";

        RootFrame.Navigate(typeof(MainPage));
    }

    /// <summary>
    /// Clamps the startup size to the current monitor's work area so the window
    /// never opens larger than the screen on smaller or scaled displays.
    /// </summary>
    private static Windows.Graphics.SizeInt32 GetInitialSize(AppWindow appWindow)
    {
        var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)?.WorkArea;
        if (workArea is null)
            return new Windows.Graphics.SizeInt32(PreferredWidth, PreferredHeight);

        var width = Math.Clamp(PreferredWidth, MinimumWidth, Math.Max(MinimumWidth, workArea.Value.Width));
        var height = Math.Clamp(PreferredHeight, MinimumHeight, Math.Max(MinimumHeight, workArea.Value.Height));
        return new Windows.Graphics.SizeInt32(width, height);
    }
}
