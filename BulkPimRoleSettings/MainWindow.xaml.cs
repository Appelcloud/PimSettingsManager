using System;
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
        appWindow.SetIcon("Assets/AppLogo.png");
        Title = "PIMSettings Manager";

        RootFrame.Navigate(typeof(MainPage));
    }
}
