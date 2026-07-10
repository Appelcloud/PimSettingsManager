using System;
using System.IO;
using BulkPimRoleSettings.Views;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace BulkPimRoleSettings;

public sealed partial class MainWindow : Window
{
    public static IntPtr Hwnd { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        Hwnd = WindowNative.GetWindowHandle(this);

        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1300, 900));

        // Resolve against the EXE folder — a relative path breaks when the app
        // is launched from a shortcut whose working directory differs.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "favicon.ico");
        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);

        Title = "PIMSettings Manager";

        RootFrame.Navigate(typeof(MainPage));
    }
}
