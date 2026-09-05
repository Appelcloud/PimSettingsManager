namespace BulkPimRoleSettings.Models;

public enum PimCategory
{
    EntraIdRoles,
    AzureResources,
    Groups
}

public static class PimCategoryExtensions
{
    // Returns a user-friendly display name for the PimCategory enum values.
    public static string ToDisplayName(this PimCategory category) => category switch
    {
        PimCategory.EntraIdRoles => "Entra ID Roles",
        PimCategory.AzureResources => "Azure Resources",
        PimCategory.Groups => "PIM for Groups",
        _ => category.ToString()
    };
}
