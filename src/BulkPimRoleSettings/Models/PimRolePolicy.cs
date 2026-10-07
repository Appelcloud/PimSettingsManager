using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BulkPimRoleSettings.Models;

public partial class PimRolePolicy : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string PolicyId { get; set; } = string.Empty;
    public string RoleDisplayName { get; set; } = string.Empty;
    public string RoleDefinitionId { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string ScopeDisplayName { get; set; } = string.Empty;
    public PimCategory Category { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // Current settings (read from API)
    public PolicySettings CurrentSettings { get; set; } = new();
}

public class PolicySettings
{
    // Activation
    public int ActivationMaxDurationHours { get; set; } = 8;
    public bool RequireMfaOnActivation { get; set; }
    public bool RequireJustificationOnActivation { get; set; }
    public bool RequireTicketOnActivation { get; set; }
    public bool RequireApprovalToActivate { get; set; }
    public List<DirectoryUser> Approvers { get; set; } = new();
    public bool RequireAuthContextOnActivation { get; set; }
    public string? AuthContextClaimValue { get; set; }

    // Assignment
    public bool AllowPermanentEligibleAssignment { get; set; }
    public int? ExpireEligibleAfterDays { get; set; }
    public bool AllowPermanentActiveAssignment { get; set; }
    public int? ExpireActiveAfterDays { get; set; }
    public bool RequireMfaOnActiveAssignment { get; set; }
    public bool RequireJustificationOnActiveAssignment { get; set; }

    // Notifications - Eligible Assignment
    public NotificationSettings EligibleAssignmentAdminNotification { get; set; } = new();
    public NotificationSettings EligibleAssignmentAssigneeNotification { get; set; } = new();
    public NotificationSettings EligibleAssignmentApproverNotification { get; set; } = new();

    // Notifications - Active Assignment
    public NotificationSettings ActiveAssignmentAdminNotification { get; set; } = new();
    public NotificationSettings ActiveAssignmentAssigneeNotification { get; set; } = new();
    public NotificationSettings ActiveAssignmentApproverNotification { get; set; } = new();

    // Notifications - Activation
    public NotificationSettings ActivationAdminNotification { get; set; } = new();
    public NotificationSettings ActivationAssigneeNotification { get; set; } = new();
    public NotificationSettings ActivationApproverNotification { get; set; } = new();
}

public class NotificationSettings
{
    public string[] AdditionalRecipients { get; set; } = [];

    /// <summary>
    /// Whether the built-in recipients (Admin/Assignee/Approver) receive the mail.
    /// Maps to <c>isDefaultRecipientsEnabled</c> on the Graph notification rule.
    /// </summary>
    public bool IsDefaultRecipientsEnabled { get; set; } = true;

    /// <summary>
    /// True when only critical mails are sent. Maps to <c>notificationLevel</c>
    /// being <c>Critical</c> rather than <c>All</c>.
    /// </summary>
    public bool CriticalEmailsOnly { get; set; }
}
