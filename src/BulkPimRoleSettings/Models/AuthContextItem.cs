namespace BulkPimRoleSettings.Models;

public class AuthContextItem
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public override string ToString() => $"{DisplayName} ({Id})";
}
