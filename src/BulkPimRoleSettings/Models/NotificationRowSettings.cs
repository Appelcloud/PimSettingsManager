using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BulkPimRoleSettings.Models;

public partial class NotificationRowSettings : ObservableObject
{
    private static readonly Regex EmailListRegex = new(
        @"^([a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,})(;\s*[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,})*$",
        RegexOptions.Compiled);

    [ObservableProperty] public partial bool DefaultRecipients { get; set; }
    [ObservableProperty] public partial string AdditionalRecipients { get; set; }
    [ObservableProperty] public partial bool CriticalOnly { get; set; }
    [ObservableProperty] public partial bool IsAdditionalRecipientsInvalid { get; set; }

    public NotificationRowSettings()
    {
        DefaultRecipients = true;
        AdditionalRecipients = string.Empty;
    }

    partial void OnAdditionalRecipientsChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            IsAdditionalRecipientsInvalid = false;
            return;
        }

        IsAdditionalRecipientsInvalid = !EmailListRegex.IsMatch(value.Trim());
    }
}
