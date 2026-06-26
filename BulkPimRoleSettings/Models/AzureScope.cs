namespace BulkPimRoleSettings.Models;

public class AzureScope
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Subscription, ResourceGroup

    public override string ToString() => $"{Type}: {DisplayName}";
}
