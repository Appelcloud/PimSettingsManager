namespace BulkPimRoleSettings.Models;

public class DirectoryUser
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string UserPrincipalName { get; set; } = string.Empty;

    // True when this directory object is a group rather than a user.
    public bool IsGroup { get; set; }

    // Group email (when available); users use UserPrincipalName instead.
    public string Mail { get; set; } = string.Empty;

    // Secondary identifying line shown in the UI (UPN for users, mail/"Group" for groups).
    public string SecondaryText => IsGroup
        ? (string.IsNullOrWhiteSpace(Mail) ? "Group" : Mail)
        : UserPrincipalName;

    // Segoe Fluent/MDL2 glyph: single contact for users, people for groups.
    public string TypeGlyph => IsGroup ? "\uE902" : "\uE77B";

    public string TypeLabel => IsGroup ? "Group" : "User";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(SecondaryText) ? DisplayName : $"{DisplayName} ({SecondaryText})";
}
