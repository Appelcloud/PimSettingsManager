namespace BulkPimRoleSettings.Models;

public class SettingChangePreview
{
    public string RoleName { get; set; } = string.Empty;
    public string SettingName { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
}
