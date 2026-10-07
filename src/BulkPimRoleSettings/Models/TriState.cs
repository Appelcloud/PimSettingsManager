namespace BulkPimRoleSettings.Models;

/// <summary>
/// Represents a tri-state value for bulk editing settings.
/// Unchanged = don't modify, SetTrue = force enable, SetFalse = force disable.
/// </summary>
public enum TriState
{
    Unchanged = 0,
    SetTrue = 1,
    SetFalse = 2
}
