using CommunityToolkit.Mvvm.ComponentModel;

namespace BulkPimRoleSettings.Models;

/// <summary>
/// Represents the desired bulk edit settings.
/// TriState: Unchanged = don't modify, SetTrue = enable, SetFalse = disable.
/// All non-Unchanged settings are applied to selected roles.
/// </summary>
public partial class BulkEditSettings : ObservableObject
{
    // Activation
    [ObservableProperty] public partial double ActivationMaxDurationHours { get; set; }

    // "On activation, require" — None means no MFA and no auth context
    [ObservableProperty] public partial bool RequireNoneOnActivation { get; set; }
    [ObservableProperty] public partial bool RequireMfaOnActivation { get; set; }
    [ObservableProperty] public partial bool RequireAuthContextOnActivation { get; set; }
    [ObservableProperty] public partial string AuthContextClaimValue { get; set; }

    [ObservableProperty] public partial bool RequireJustificationOnActivation { get; set; }
    [ObservableProperty] public partial bool RequireTicketOnActivation { get; set; }

    // "Require approval to activate" — consolidated with approver search
    [ObservableProperty] public partial bool RequireApprovalToActivate { get; set; }
    [ObservableProperty] public partial string ApproversRaw { get; set; } // semicolon-separated user IDs

    // Assignment
    [ObservableProperty] public partial bool AllowPermanentEligibleAssignment { get; set; }
    [ObservableProperty] public partial double ExpireEligibleAfterDays { get; set; }

    [ObservableProperty] public partial bool AllowPermanentActiveAssignment { get; set; }
    [ObservableProperty] public partial double ExpireActiveAfterDays { get; set; }

    [ObservableProperty] public partial bool RequireMfaOnActiveAssignment { get; set; }
    [ObservableProperty] public partial bool RequireJustificationOnActiveAssignment { get; set; }

    // Notifications — per-row (Type × Admin/Assignee/Approver)
    // Eligible assignment
    [ObservableProperty] public partial NotificationRowSettings EligibleAssignmentAdmin { get; set; }
    [ObservableProperty] public partial NotificationRowSettings EligibleAssignmentAssignee { get; set; }
    [ObservableProperty] public partial NotificationRowSettings EligibleAssignmentApprover { get; set; }

    // Active assignment
    [ObservableProperty] public partial NotificationRowSettings ActiveAssignmentAdmin { get; set; }
    [ObservableProperty] public partial NotificationRowSettings ActiveAssignmentAssignee { get; set; }
    [ObservableProperty] public partial NotificationRowSettings ActiveAssignmentApprover { get; set; }

    // Activation
    [ObservableProperty] public partial NotificationRowSettings ActivationAdmin { get; set; }
    [ObservableProperty] public partial NotificationRowSettings ActivationRequestor { get; set; }
    [ObservableProperty] public partial NotificationRowSettings ActivationApprover { get; set; }

    public BulkEditSettings()
    {
        ActivationMaxDurationHours = 8;
        RequireNoneOnActivation = true;
        AuthContextClaimValue = string.Empty;
        ApproversRaw = string.Empty;
        AllowPermanentEligibleAssignment = false;
        ExpireEligibleAfterDays = 365;
        AllowPermanentActiveAssignment = false;
        ExpireActiveAfterDays = 365;
        RequireJustificationOnActivation = false;
        RequireTicketOnActivation = false;
        RequireApprovalToActivate = false;
        RequireMfaOnActiveAssignment = false;
        RequireJustificationOnActiveAssignment = false;

        EligibleAssignmentAdmin = new NotificationRowSettings();
        EligibleAssignmentAssignee = new NotificationRowSettings();
        EligibleAssignmentApprover = new NotificationRowSettings();
        ActiveAssignmentAdmin = new NotificationRowSettings();
        ActiveAssignmentAssignee = new NotificationRowSettings();
        ActiveAssignmentApprover = new NotificationRowSettings();
        ActivationAdmin = new NotificationRowSettings();
        ActivationRequestor = new NotificationRowSettings();
        ActivationApprover = new NotificationRowSettings();
    }
}
