using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BulkPimRoleSettings.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }
}
