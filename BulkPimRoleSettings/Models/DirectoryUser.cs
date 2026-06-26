namespace BulkPimRoleSettings.Models;

public class DirectoryUser
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string UserPrincipalName { get; set; } = string.Empty;

    public override string ToString() => $"{DisplayName} ({UserPrincipalName})";
}
