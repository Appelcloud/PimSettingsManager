namespace BulkPimRoleSettings.Models;

public enum PimCategory
{
    EntraIdRoles,
    AzureResources,
    Groups
}

public static class PimCategoryExtensions
{
    public static string ToDisplayName(this PimCategory category) => category switch
    {
        PimCategory.EntraIdRoles => "Entra ID Roles",
        PimCategory.AzureResources => "Azure Resources",
        PimCategory.Groups => "PIM for Groups",
        _ => category.ToString()
    };
}
