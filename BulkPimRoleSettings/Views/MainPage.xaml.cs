using BulkPimRoleSettings.Models;
using BulkPimRoleSettings.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BulkPimRoleSettings.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = new MainViewModel
        {
            WindowHandle = MainWindow.Hwnd
        };

        InitializeComponent();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentStep) && ViewModel.CurrentStep == 3)
        {
            SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(AboutPage));
    }

    private void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(FeedbackPage));
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        // Close the profile flyout.
        ProfileFlyout.Hide();
    }

    private async void ApproverSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.ApproverSearchQuery = sender.Text;
            await ViewModel.SearchApproversCommand.ExecuteAsync(null);
        }
    }

    private void ApproverSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is DirectoryUser user)
        {
            ViewModel.AddApproverCommand.Execute(user);
            sender.Text = string.Empty;
        }
    }

    private void RemoveApprover_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is DirectoryUser user)
        {
            ViewModel.RemoveApproverCommand.Execute(user);
        }
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView listView) return;

        ViewModel.SelectedGroups.Clear();
        foreach (var item in listView.SelectedItems)
        {
            if (item is AzureScope group)
                ViewModel.SelectedGroups.Add(group);
        }
    }
}
