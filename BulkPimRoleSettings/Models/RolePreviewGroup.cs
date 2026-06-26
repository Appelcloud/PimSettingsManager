using System.Collections.ObjectModel;

namespace BulkPimRoleSettings.Models;

public class RolePreviewGroup
{
    public string RoleName { get; set; } = string.Empty;
    public PimCategory Category { get; set; }
    public ObservableCollection<SettingChangePreview> Changes { get; set; } = new();
}

public class CategoryPreviewGroup
{
    public string CategoryName { get; set; } = string.Empty;
    public ObservableCollection<RolePreviewGroup> Roles { get; set; } = new();
}
