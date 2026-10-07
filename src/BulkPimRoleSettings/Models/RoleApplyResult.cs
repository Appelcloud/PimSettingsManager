using CommunityToolkit.Mvvm.ComponentModel;

namespace BulkPimRoleSettings.Models;

public partial class RoleApplyResult : ObservableObject
{
    public string RoleName { get; set; } = string.Empty;
    public PimCategory Category { get; set; }

    [ObservableProperty] public partial RoleApplyStatus Status { get; set; }

    [ObservableProperty] public partial string? ErrorMessage { get; set; }
}

public class CategoryApplyGroup
{
    public string CategoryName { get; set; } = string.Empty;
    public System.Collections.ObjectModel.ObservableCollection<RoleApplyResult> Roles { get; set; } = new();
}

public enum RoleApplyStatus
{
    Pending,
    Success,
    Failed
}
