using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BulkPimRoleSettings.Models;
using BulkPimRoleSettings.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BulkPimRoleSettings.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AuthService _authService;
    private readonly GraphPimService _graphService;
    private readonly LogService _log = LogService.Instance;

    public MainViewModel()
    {
        _authService = new AuthService();
        _graphService = new GraphPimService(_authService);

        StatusMessage = string.Empty;
        ErrorMessage = string.Empty;
        LoggedInUser = string.Empty;
        IncludeEntraIdRoles = false;
        RoleSearchFilter = string.Empty;
        EditSettings = new BulkEditSettings();
        ApproverSearchQuery = string.Empty;
        GroupsErrorMessage = string.Empty;
        ApplyStatus = string.Empty;
    }

    // Navigation
    [ObservableProperty] public partial int CurrentStep { get; set; } // 0=Login, 1=Category, 2=Roles, 3=Settings, 4=Preview, 5=Apply
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; }

    // Phase tracking: which category we're currently configuring
    [ObservableProperty] public partial int CurrentPhase { get; set; } // index into ActivePhases
    public List<PimCategory> ActivePhases { get; } = new();
    public PimCategory CurrentPhaseCategory => ActivePhases.Count > CurrentPhase ? ActivePhases[CurrentPhase] : PimCategory.EntraIdRoles;
    public string CurrentPhaseTitle => CurrentPhaseCategory.ToDisplayName();
    public string NextSettingsButtonText => CurrentPhase < ActivePhases.Count - 1
        ? $"Next: {ActivePhases[CurrentPhase + 1].ToDisplayName()}"
        : "Preview Changes";

    // Per-category settings storage
    public Dictionary<PimCategory, BulkEditSettings> PhaseSettings { get; } = new();
    public Dictionary<PimCategory, List<DirectoryUser>> PhaseApprovers { get; } = new();

    partial void OnCurrentPhaseChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPhaseTitle));
        OnPropertyChanged(nameof(NextSettingsButtonText));
    }

    // Auth
    [ObservableProperty] public partial string LoggedInUser { get; set; }
    [ObservableProperty] public partial bool IsAuthenticated { get; set; }

    // Category selection
    [ObservableProperty] public partial bool IncludeEntraIdRoles { get; set; }
    [ObservableProperty] public partial bool IncludeAzureResources { get; set; }
    [ObservableProperty] public partial bool IncludeGroups { get; set; }

    partial void OnIncludeGroupsChanged(bool value)
    {
        if (value && AvailableGroups.Count == 0)
        {
            _ = LoadGroupsAsync();
        }
    }

    // Azure Resource scope
    public ObservableCollection<AzureScope> AvailableScopes { get; } = new();
    public ObservableCollection<AzureScope> SelectedScopes { get; } = new();

    // Group scope
    public ObservableCollection<AzureScope> AvailableGroups { get; } = new();
    public ObservableCollection<AzureScope> SelectedGroups { get; } = new();
    [ObservableProperty] public partial string GroupsErrorMessage { get; set; }

    // Roles
    public ObservableCollection<PimRolePolicy> RolePolicies { get; } = new();
    public ObservableCollection<PimRolePolicy> FilteredRolePolicies { get; } = new();
    [ObservableProperty] public partial string RoleSearchFilter { get; set; }
    [ObservableProperty] public partial bool SelectAllRoles { get; set; }

    // Settings
    [ObservableProperty] public partial BulkEditSettings EditSettings { get; set; }

    // Indicates the settings editor is pre-populated from a single role/group's current
    // configuration. When multiple are selected, defaults are shown instead.
    [ObservableProperty] public partial bool IsSingleSelectionSettings { get; set; }
    [ObservableProperty] public partial string SettingsSourceHint { get; set; } = string.Empty;

    // Expiration dropdowns — index maps: 0=15d, 1=30d, 2=90d, 3=180d, 4=365d
    [ObservableProperty] public partial int EligibleExpirationIndex { get; set; }
    [ObservableProperty] public partial int ActiveExpirationIndex { get; set; }

    // Show expiration picker when permanent is unchecked (disabled)
    public bool IsEligibleExpirationVisible => !EditSettings.AllowPermanentEligibleAssignment;
    public bool IsActiveExpirationVisible => !EditSettings.AllowPermanentActiveAssignment;

    // Show approver search when RequireApprovalToActivate is checked
    public bool IsApproverSearchVisible => EditSettings.RequireApprovalToActivate;

    // Approver search
    public ObservableCollection<DirectoryUser> ApproverSearchResults { get; } = new();
    [ObservableProperty] public partial string ApproverSearchQuery { get; set; }

    // Selected approvers (displayed in a list)
    public ObservableCollection<DirectoryUser> SelectedApprovers { get; } = new();

    // Authentication contexts
    public ObservableCollection<AuthContextItem> AuthContextItems { get; } = new();
    [ObservableProperty] public partial AuthContextItem? SelectedAuthContext { get; set; }

    // Preview
    public ObservableCollection<SettingChangePreview> PreviewChanges { get; } = new();
    public ObservableCollection<RolePreviewGroup> GroupedPreviewChanges { get; } = new();
    public ObservableCollection<CategoryPreviewGroup> CategoryPreviewGroups { get; } = new();

    // Apply
    [ObservableProperty] public partial double ApplyProgress { get; set; }
    [ObservableProperty] public partial int ApplyTotal { get; set; }
    [ObservableProperty] public partial int ApplyCompleted { get; set; }
    [ObservableProperty] public partial int ApplyFailed { get; set; }
    [ObservableProperty] public partial string ApplyStatus { get; set; }
    [ObservableProperty] public partial bool IsApplyComplete { get; set; }

    // Applied roles summary
    public ObservableCollection<RoleApplyResult> AppliedRoleResults { get; } = new();
    public ObservableCollection<CategoryApplyGroup> AppliedCategoryGroups { get; } = new();
    public ObservableCollection<string> ApplyErrors { get; } = new();

    // Window handle for MSAL
    public IntPtr WindowHandle { get; set; }

    // Version
    public string VersionString { get; } = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}";

    #region Login

    // Indicates an interactive sign-in is in progress (used to show a Cancel button).
    [ObservableProperty] public partial bool IsSigningIn { get; set; }

    private CancellationTokenSource? _loginCts;

    [RelayCommand]
    private async Task LoginAsync()
    {
        // Guard against double-clicks starting a second interactive flow.
        if (IsSigningIn) return;

        _loginCts?.Dispose();
        _loginCts = new CancellationTokenSource();

        IsBusy = true;
        IsSigningIn = true;
        ErrorMessage = string.Empty;
        StatusMessage = "Signing in...";

        try
        {
            var success = await _authService.LoginAsync(WindowHandle, _loginCts.Token);

            // The interactive sign-in is finished at this point (success or not).
            // Hide the Cancel button — there is nothing left to cancel; the rest is
            // just a local permission check.
            IsSigningIn = false;

            if (success)
            {
                LoggedInUser = _authService.UserDisplayName ?? "Unknown";
                IsAuthenticated = true;
                StatusMessage = $"Signed in as: {LoggedInUser}";
                _log.Log(LogLevel.SUCCESS, LogCategory.AUTH, $"Login successful: {LoggedInUser}");

                // Check permissions
                StatusMessage = "Checking permissions...";
                var (hasAccess, missing) = await _graphService.CheckPermissionsAsync();
                if (!hasAccess)
                {
                    ErrorMessage = $"Insufficient permissions. Missing: {string.Join(", ", missing)}";
                    _log.Log(LogLevel.ERROR, LogCategory.PERMISSION, ErrorMessage);
                    return;
                }

                CurrentStep = 1;
                StatusMessage = "Ready. Select PIM categories.";
            }
            else
            {
                // Covers user cancellation, closing the browser (Cancel button), or timeout.
                StatusMessage = string.Empty;
                ErrorMessage = "Sign-in was cancelled or did not complete. Please try again.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Login error: {ex.Message}";
            _log.LogError(ex, "Login failed.");
        }
        finally
        {
            IsSigningIn = false;
            IsBusy = false;
            _loginCts?.Dispose();
            _loginCts = null;
        }
    }

    [RelayCommand]
    private void CancelLogin()
    {
        // Only meaningful while the interactive sign-in is actually in progress.
        // Once the token has been acquired, sign-in is complete and cannot be undone
        // here (use Sign out instead).
        if (!IsSigningIn) return;

        _log.Log(LogLevel.INFO, LogCategory.AUTH, "User cancelled the sign-in process.");
        _loginCts?.Cancel();
        StatusMessage = "Cancelling sign-in...";
    }

    #endregion

    #region Category & Scope

    [RelayCommand]
    private async Task LoadScopesAsync()
    {
        if (!IncludeAzureResources) return;

        IsBusy = true;
        StatusMessage = "Loading Azure resource scopes...";

        try
        {
            var scopes = await _graphService.GetAzureResourceScopesAsync();
            AvailableScopes.Clear();
            foreach (var s in scopes) AvailableScopes.Add(s);
            StatusMessage = $"Found {scopes.Count} Azure resource scopes.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load scopes: {ex.Message}";
            _log.LogError(ex, "Load scopes failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadGroupsAsync()
    {
        IsBusy = true;
        GroupsErrorMessage = string.Empty;
        StatusMessage = "Loading PIM groups...";

        try
        {
            var groups = await _graphService.GetPimGroupResourcesAsync();
            AvailableGroups.Clear();
            SelectedGroups.Clear();
            foreach (var g in groups) AvailableGroups.Add(g);

            StatusMessage = $"Found {groups.Count} PIM-enabled group(s).";

            if (groups.Count == 0)
            {
                GroupsErrorMessage = "No PIM-enabled groups found. Ensure PIM is enabled for groups in the Entra ID portal under ID Governance > Privileged Identity Management > Groups.";
            }
        }
        catch (Exception ex)
        {
            GroupsErrorMessage = $"Failed to load PIM groups: {ex.Message}";
            _log.LogError(ex, "Failed to discover PIM-onboarded groups.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProceedToRolesAsync()
    {
        if (!IncludeEntraIdRoles && !IncludeAzureResources && !IncludeGroups)
        {
            ErrorMessage = "Please select at least one category.";
            return;
        }

        // Build phase list in order
        ActivePhases.Clear();
        PhaseSettings.Clear();
        PhaseApprovers.Clear();
        CurrentPhase = 0;

        if (IncludeEntraIdRoles) ActivePhases.Add(PimCategory.EntraIdRoles);
        if (IncludeAzureResources) ActivePhases.Add(PimCategory.AzureResources);
        if (IncludeGroups) ActivePhases.Add(PimCategory.Groups);

        OnPropertyChanged(nameof(CurrentPhaseTitle));
        OnPropertyChanged(nameof(NextSettingsButtonText));

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = "Loading role policies...";
        RolePolicies.Clear();
        var errors = new List<string>();

        if (IncludeEntraIdRoles)
        {
            try
            {
                StatusMessage = "Loading Entra ID role policies...";
                var policies = await _graphService.GetEntraIdRolePoliciesAsync();
                foreach (var p in policies
                    .Where(p => !string.Equals(p.RoleDisplayName, "User", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.RoleDisplayName, StringComparer.OrdinalIgnoreCase))
                    RolePolicies.Add(p);
            }
            catch (Exception ex)
            {
                errors.Add($"Entra ID Roles: {ex.Message}");
                _log.LogError(ex, "Failed to load Entra ID role policies.");
            }
        }

        if (IncludeAzureResources)
        {
            foreach (var scope in SelectedScopes)
            {
                try
                {
                    StatusMessage = $"Loading Azure resource policies for {scope.DisplayName}...";
                    var policies = await _graphService.GetAzureResourceRolePoliciesAsync(scope.Id);
                    foreach (var p in policies) RolePolicies.Add(p);
                }
                catch (Exception ex)
                {
                    errors.Add($"Azure Resource '{scope.DisplayName}': {ex.Message}");
                    _log.LogError(ex, $"Failed to load Azure resource policies for {scope.DisplayName}.");
                }
            }
        }

        if (IncludeGroups)
        {
            if (SelectedGroups.Count == 0)
            {
                errors.Add("PIM for Groups: No groups selected. Load PIM groups and select at least one.");
            }
            else
            {
                try
                {
                    StatusMessage = "Loading group policies...";
                    var policies = await _graphService.GetGroupPoliciesAsync(SelectedGroups);
                    if (policies.Count > 0)
                    {
                        foreach (var p in policies) RolePolicies.Add(p);
                    }
                    else
                    {
                        errors.Add("PIM for Groups: The selected groups have no PIM policies. Ensure PIM is enabled for these groups in the Entra ID portal under ID Governance > Privileged Identity Management > Groups.");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"PIM for Groups: {ex.Message}");
                    _log.LogError(ex, "Failed to load group policies.");
                }
            }
        }

        if (RolePolicies.Count > 0)
        {
            StatusMessage = $"Loaded {RolePolicies.Count} role policies.";
            if (errors.Count > 0)
                ErrorMessage = $"Some categories failed to load: {string.Join(" | ", errors)}";

            // Show roles for the first phase
            ApplyRoleFilterForPhase();
            CurrentStep = 2;
        }
        else if (errors.Count > 0)
        {
            ErrorMessage = $"Failed to load roles: {string.Join(" | ", errors)}";
        }
        else
        {
            ErrorMessage = "No role policies found for the selected categories.";
        }

        IsBusy = false;
    }

    #endregion

    #region Role Selection

    partial void OnSelectAllRolesChanged(bool value)
    {
        foreach (var role in FilteredRolePolicies)
            role.IsSelected = value;
    }

    partial void OnRoleSearchFilterChanged(string value)
    {
        ApplyRoleFilter();
    }

    private void ApplyRoleFilter()
    {
        FilteredRolePolicies.Clear();
        var filter = RoleSearchFilter?.Trim() ?? string.Empty;
        var phaseCategory = CurrentPhaseCategory;
        foreach (var role in RolePolicies)
        {
            if (role.Category != phaseCategory) continue;
            if (string.IsNullOrEmpty(filter) ||
                role.RoleDisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                role.ScopeDisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredRolePolicies.Add(role);
            }
        }
    }

    private void ApplyRoleFilterForPhase()
    {
        SelectAllRoles = false;
        RoleSearchFilter = string.Empty;
        ApplyRoleFilter();
        StatusMessage = $"{CurrentPhaseTitle}: Select roles to configure ({FilteredRolePolicies.Count} available).";
    }

    [RelayCommand]
    private async Task ProceedToSettingsAsync()
    {
        var phaseCategory = CurrentPhaseCategory;
        var selectedCount = RolePolicies.Count(r => r.IsSelected && r.Category == phaseCategory);
        if (selectedCount == 0)
        {
            ErrorMessage = "Please select at least one role.";
            return;
        }

        ErrorMessage = string.Empty;
        StatusMessage = $"{CurrentPhaseTitle}: {selectedCount} role(s) selected. Loading authentication contexts...";
        EditSettings = new BulkEditSettings();
        SelectedApprovers.Clear();
        SelectedAuthContext = null;
        PrePopulateSettingsFromCurrentValues();

        // Load auth contexts
        if (AuthContextItems.Count == 0)
        {
            try
            {
                var contexts = await _graphService.GetAuthenticationContextsAsync();
                AuthContextItems.Clear();
                foreach (var ctx in contexts) AuthContextItems.Add(ctx);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to load authentication contexts.");
            }
        }

        // Reflect the current authentication context in the dropdown (single selection only)
        if (!string.IsNullOrEmpty(EditSettings.AuthContextClaimValue))
        {
            SelectedAuthContext = AuthContextItems.FirstOrDefault(c => c.Id == EditSettings.AuthContextClaimValue);
        }

        // Populate current approvers when a single role/group is selected and approval is required.
        await LoadCurrentApproversAsync();

        StatusMessage = $"{CurrentPhaseTitle}: {selectedCount} role(s) selected. Configure settings.";
        CurrentStep = 3;
    }

    private async Task LoadCurrentApproversAsync()
    {
        SelectedApprovers.Clear();

        var phaseCategory = CurrentPhaseCategory;
        var selected = RolePolicies.Where(r => r.IsSelected && r.Category == phaseCategory).ToList();

        // Only pre-load approvers for a single selection with approval required.
        if (selected.Count != 1 || !EditSettings.RequireApprovalToActivate)
            return;

        var currentApprovers = selected[0].CurrentSettings.Approvers;
        if (currentApprovers == null || currentApprovers.Count == 0)
            return;

        // Resolve display details for approvers that only carry an ID (no name or
        // identifying secondary text such as UPN/email).
        var needsResolution = currentApprovers
            .Where(a => string.IsNullOrWhiteSpace(a.DisplayName)
                        || string.IsNullOrWhiteSpace(a.SecondaryText)
                        || a.DisplayName == "User" || a.DisplayName == "Group")
            .Select(a => a.Id)
            .ToList();

        var resolvedLookup = new Dictionary<string, DirectoryUser>();
        if (needsResolution.Count > 0)
        {
            try
            {
                var resolved = await _graphService.ResolveDirectoryObjectsAsync(needsResolution);
                foreach (var user in resolved)
                    resolvedLookup[user.Id] = user;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to resolve approver display names.");
            }
        }

        foreach (var approver in currentApprovers)
        {
            var display = approver;
            if (resolvedLookup.TryGetValue(approver.Id, out var resolved))
            {
                display = new DirectoryUser
                {
                    Id = approver.Id,
                    IsGroup = resolved.IsGroup || approver.IsGroup,
                    DisplayName = !string.IsNullOrWhiteSpace(resolved.DisplayName) ? resolved.DisplayName : approver.DisplayName,
                    UserPrincipalName = !string.IsNullOrWhiteSpace(resolved.UserPrincipalName)
                        ? resolved.UserPrincipalName
                        : approver.UserPrincipalName,
                    Mail = !string.IsNullOrWhiteSpace(resolved.Mail) ? resolved.Mail : approver.Mail,
                };
            }

            SelectedApprovers.Add(display);
        }

        RebuildApproversRaw();
        OnPropertyChanged(nameof(IsApproverSearchVisible));
    }

    private void PrePopulateSettingsFromCurrentValues()
    {
        var phaseCategory = CurrentPhaseCategory;
        var selected = RolePolicies.Where(r => r.IsSelected && r.Category == phaseCategory).ToList();

        // Only pre-populate from current values when exactly one role/group is selected.
        // For multiple selections, keep default settings to avoid conflicting values.
        if (selected.Count != 1)
        {
            IsSingleSelectionSettings = false;
            SettingsSourceHint = selected.Count > 1
                ? $"{selected.Count} items selected. Showing default settings — configure the values you want applied to all selected items."
                : "Showing default settings.";
            OnPropertyChanged(nameof(IsEligibleExpirationVisible));
            OnPropertyChanged(nameof(IsActiveExpirationVisible));
            OnPropertyChanged(nameof(IsApproverSearchVisible));
            return;
        }

        IsSingleSelectionSettings = true;
        SettingsSourceHint = $"Showing the current PIM policy configured for \"{selected[0].RoleDisplayName}\". Adjust any values before applying.";

        var current = selected[0].CurrentSettings;

        // Activation max duration
        EditSettings.ActivationMaxDurationHours = current.ActivationMaxDurationHours;

        // Activation "On activation, require" radio buttons
        if (current.RequireMfaOnActivation)
        {
            EditSettings.RequireMfaOnActivation = true;
            EditSettings.RequireNoneOnActivation = false;
            EditSettings.RequireAuthContextOnActivation = false;
        }
        else if (current.RequireAuthContextOnActivation)
        {
            EditSettings.RequireAuthContextOnActivation = true;
            EditSettings.RequireNoneOnActivation = false;
            EditSettings.RequireMfaOnActivation = false;
            EditSettings.AuthContextClaimValue = current.AuthContextClaimValue ?? string.Empty;
        }
        else
        {
            EditSettings.RequireNoneOnActivation = true;
            EditSettings.RequireMfaOnActivation = false;
            EditSettings.RequireAuthContextOnActivation = false;
        }

        // Other activation requirements
        EditSettings.RequireJustificationOnActivation = current.RequireJustificationOnActivation;
        EditSettings.RequireTicketOnActivation = current.RequireTicketOnActivation;
        EditSettings.RequireApprovalToActivate = current.RequireApprovalToActivate;

        // Assignment
        EditSettings.AllowPermanentEligibleAssignment = current.AllowPermanentEligibleAssignment;
        if (current.ExpireEligibleAfterDays.HasValue)
        {
            EditSettings.ExpireEligibleAfterDays = current.ExpireEligibleAfterDays.Value;
            EligibleExpirationIndex = DaysToExpirationIndex(current.ExpireEligibleAfterDays.Value);
        }

        EditSettings.AllowPermanentActiveAssignment = current.AllowPermanentActiveAssignment;
        if (current.ExpireActiveAfterDays.HasValue)
        {
            EditSettings.ExpireActiveAfterDays = current.ExpireActiveAfterDays.Value;
            ActiveExpirationIndex = DaysToExpirationIndex(current.ExpireActiveAfterDays.Value);
        }

        EditSettings.RequireMfaOnActiveAssignment = current.RequireMfaOnActiveAssignment;
        EditSettings.RequireJustificationOnActiveAssignment = current.RequireJustificationOnActiveAssignment;

        // Notify visibility
        OnPropertyChanged(nameof(IsEligibleExpirationVisible));
        OnPropertyChanged(nameof(IsActiveExpirationVisible));
        OnPropertyChanged(nameof(IsApproverSearchVisible));
    }

    #endregion

    #region Settings & Approver Search

    private int _approverSearchVersion;

    [RelayCommand]
    private async Task SearchApproversAsync()
    {
        // Version stamp guards against out-of-order results: if a newer search
        // starts while this one is in flight, the stale response is discarded.
        var version = Interlocked.Increment(ref _approverSearchVersion);

        if (string.IsNullOrWhiteSpace(ApproverSearchQuery) || ApproverSearchQuery.Length < 2)
        {
            ApproverSearchResults.Clear();
            return;
        }

        try
        {
            var results = await _graphService.SearchUsersAsync(ApproverSearchQuery);
            if (version != Volatile.Read(ref _approverSearchVersion)) return;

            ApproverSearchResults.Clear();
            foreach (var user in results) ApproverSearchResults.Add(user);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Approver search failed.");
        }
    }

    [RelayCommand]
    private void AddApprover(DirectoryUser? user)
    {
        if (user == null) return;

        // Avoid duplicates
        if (SelectedApprovers.Any(a => a.Id == user.Id))
        {
            ApproverSearchQuery = string.Empty;
            ApproverSearchResults.Clear();
            return;
        }

        SelectedApprovers.Add(user);
        RebuildApproversRaw();
        ApproverSearchQuery = string.Empty;
        ApproverSearchResults.Clear();
    }

    [RelayCommand]
    private void RemoveApprover(DirectoryUser? user)
    {
        if (user == null) return;
        SelectedApprovers.Remove(user);
        RebuildApproversRaw();
    }

    private void RebuildApproversRaw()
    {
        // Encode each approver with its type so groups are applied as groupMembers
        // and users as singleUser. Format: "user:{id}" or "group:{id}".
        EditSettings.ApproversRaw = string.Join(";",
            SelectedApprovers.Select(a => $"{(a.IsGroup ? "group" : "user")}:{a.Id}"));
    }

    partial void OnSelectedAuthContextChanged(AuthContextItem? value)
    {
        if (value != null)
            EditSettings.AuthContextClaimValue = value.Id;
    }

    partial void OnEligibleExpirationIndexChanged(int value)
    {
        EditSettings.ExpireEligibleAfterDays = ExpirationIndexToDays(value);
    }

    partial void OnActiveExpirationIndexChanged(int value)
    {
        EditSettings.ExpireActiveAfterDays = ExpirationIndexToDays(value);
    }

    partial void OnEditSettingsChanged(BulkEditSettings? oldValue, BulkEditSettings newValue)
    {
        if (oldValue != null)
            oldValue.PropertyChanged -= EditSettings_PropertyChanged;
        if (newValue != null)
            newValue.PropertyChanged += EditSettings_PropertyChanged;
    }

    private void EditSettings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BulkEditSettings.AllowPermanentEligibleAssignment))
            OnPropertyChanged(nameof(IsEligibleExpirationVisible));
        else if (e.PropertyName == nameof(BulkEditSettings.AllowPermanentActiveAssignment))
            OnPropertyChanged(nameof(IsActiveExpirationVisible));
        else if (e.PropertyName == nameof(BulkEditSettings.RequireApprovalToActivate))
            OnPropertyChanged(nameof(IsApproverSearchVisible));
    }

    private static double ExpirationIndexToDays(int index) => index switch
    {
        0 => 15,
        1 => 30,
        2 => 90,
        3 => 180,
        4 => 365,
        _ => 365
    };

    private static int DaysToExpirationIndex(double days) => days switch
    {
        <= 15 => 0,
        <= 30 => 1,
        <= 90 => 2,
        <= 180 => 3,
        _ => 4
    };

    [RelayCommand]
    private void ProceedToPreview()
    {
        ErrorMessage = string.Empty;

        // Validate: approval requires at least one approver
        if (EditSettings.RequireApprovalToActivate && SelectedApprovers.Count == 0)
        {
            ErrorMessage = "Please select at least one approver when 'Require approval to activate' is enabled.";
            return;
        }

        // Validate: auth context requires a selection
        if (EditSettings.RequireAuthContextOnActivation && SelectedAuthContext == null)
        {
            ErrorMessage = "Please select an authentication context when 'Conditional Access authentication context' is enabled.";
            return;
        }

        // Save settings for the current phase
        PhaseSettings[CurrentPhaseCategory] = EditSettings;
        PhaseApprovers[CurrentPhaseCategory] = SelectedApprovers.ToList();

        // Check if there are more phases to configure
        if (CurrentPhase < ActivePhases.Count - 1)
        {
            // Advance to next phase
            CurrentPhase++;
            ApplyRoleFilterForPhase();
            CurrentStep = 2;
        }
        else
        {
            // All phases configured — show combined preview
            GeneratePreview();
            CurrentStep = 4;
        }
    }

    #endregion

    #region Preview

    private void GeneratePreview()
    {
        PreviewChanges.Clear();
        var selectedRoles = RolePolicies.Where(r => r.IsSelected).ToList();

        foreach (var role in selectedRoles)
        {
            // Use the settings for this role's category
            var settings = PhaseSettings.TryGetValue(role.Category, out var s) ? s : EditSettings;

            // Activation max duration
            PreviewChanges.Add(new SettingChangePreview
            {
                RoleName = role.RoleDisplayName,
                SettingName = "Activation Max Duration (hours)",
                CurrentValue = role.CurrentSettings.ActivationMaxDurationHours.ToString(),
                NewValue = ((int)settings.ActivationMaxDurationHours).ToString()
            });

            AddBoolPreview(role, "Require MFA on Activation",
                role.CurrentSettings.RequireMfaOnActivation, settings.RequireMfaOnActivation);
            AddBoolPreview(role, "Require Justification on Activation",
                role.CurrentSettings.RequireJustificationOnActivation, settings.RequireJustificationOnActivation);
            AddBoolPreview(role, "Require Ticket on Activation",
                role.CurrentSettings.RequireTicketOnActivation, settings.RequireTicketOnActivation);
            AddBoolPreview(role, "Require Approval to Activate",
                role.CurrentSettings.RequireApprovalToActivate, settings.RequireApprovalToActivate);
            AddBoolPreview(role, "Require Auth Context on Activation",
                role.CurrentSettings.RequireAuthContextOnActivation, settings.RequireAuthContextOnActivation);

            // Authentication context — show the selected context whenever it is (or was) required.
            if (settings.RequireAuthContextOnActivation || role.CurrentSettings.RequireAuthContextOnActivation)
            {
                var currentAuth = role.CurrentSettings.RequireAuthContextOnActivation
                    ? FormatAuthContext(role.CurrentSettings.AuthContextClaimValue)
                    : "None";

                var newAuth = settings.RequireAuthContextOnActivation
                    ? FormatAuthContext(settings.AuthContextClaimValue)
                    : "None";

                if (currentAuth != newAuth)
                {
                    PreviewChanges.Add(new SettingChangePreview
                    {
                        RoleName = role.RoleDisplayName,
                        SettingName = "Authentication Context",
                        CurrentValue = currentAuth,
                        NewValue = newAuth
                    });
                }
            }

            // Approver(s) — show names/details whenever approval is (or was) required.
            if (settings.RequireApprovalToActivate || role.CurrentSettings.RequireApprovalToActivate)
            {
                var currentApprovers = role.CurrentSettings.RequireApprovalToActivate
                    ? role.CurrentSettings.Approvers
                    : new List<DirectoryUser>();

                var newApprovers = settings.RequireApprovalToActivate
                    && PhaseApprovers.TryGetValue(role.Category, out var approvers)
                        ? approvers
                        : new List<DirectoryUser>();

                var currentText = FormatApprovers(currentApprovers);
                var newText = FormatApprovers(newApprovers);

                if (currentText != newText)
                {
                    PreviewChanges.Add(new SettingChangePreview
                    {
                        RoleName = role.RoleDisplayName,
                        SettingName = "Approver(s)",
                        CurrentValue = currentText,
                        NewValue = newText
                    });
                }
            }

            AddBoolPreview(role, "Allow Permanent Eligible Assignment",
                role.CurrentSettings.AllowPermanentEligibleAssignment, settings.AllowPermanentEligibleAssignment);
            AddBoolPreview(role, "Allow Permanent Active Assignment",
                role.CurrentSettings.AllowPermanentActiveAssignment, settings.AllowPermanentActiveAssignment);
            AddBoolPreview(role, "Require MFA on Active Assignment",
                role.CurrentSettings.RequireMfaOnActiveAssignment, settings.RequireMfaOnActiveAssignment);
            AddBoolPreview(role, "Require Justification on Active Assignment",
                role.CurrentSettings.RequireJustificationOnActiveAssignment, settings.RequireJustificationOnActiveAssignment);

            if (!settings.AllowPermanentEligibleAssignment)
            {
                PreviewChanges.Add(new SettingChangePreview
                {
                    RoleName = role.RoleDisplayName,
                    SettingName = "Expire Eligible After (days)",
                    CurrentValue = role.CurrentSettings.ExpireEligibleAfterDays?.ToString() ?? "N/A",
                    NewValue = ((int)settings.ExpireEligibleAfterDays).ToString()
                });
            }

            if (!settings.AllowPermanentActiveAssignment)
            {
                PreviewChanges.Add(new SettingChangePreview
                {
                    RoleName = role.RoleDisplayName,
                    SettingName = "Expire Active After (days)",
                    CurrentValue = role.CurrentSettings.ExpireActiveAfterDays?.ToString() ?? "N/A",
                    NewValue = ((int)settings.ExpireActiveAfterDays).ToString()
                });
            }
        }

        StatusMessage = $"Preview: {PreviewChanges.Count} change(s) across {selectedRoles.Count} role(s).";

        // Build grouped preview
        GroupedPreviewChanges.Clear();
        CategoryPreviewGroups.Clear();

        foreach (var group in PreviewChanges.GroupBy(c => c.RoleName))
        {
            var role = selectedRoles.FirstOrDefault(r => r.RoleDisplayName == group.Key);
            var roleGroup = new RolePreviewGroup
            {
                RoleName = group.Key,
                Category = role?.Category ?? PimCategory.EntraIdRoles
            };
            foreach (var change in group)
                roleGroup.Changes.Add(change);
            GroupedPreviewChanges.Add(roleGroup);
        }

        // Build category-level grouping
        var categoryOrder = new[] { PimCategory.EntraIdRoles, PimCategory.Groups, PimCategory.AzureResources };
        foreach (var cat in categoryOrder)
        {
            var rolesInCategory = GroupedPreviewChanges.Where(r => r.Category == cat).ToList();
            if (rolesInCategory.Count == 0) continue;

            var catGroup = new CategoryPreviewGroup { CategoryName = cat.ToDisplayName() };
            foreach (var r in rolesInCategory)
                catGroup.Roles.Add(r);
            CategoryPreviewGroups.Add(catGroup);
        }
    }

    private void AddBoolPreview(PimRolePolicy role, string settingName, bool currentValue, bool newValue)
    {
        if (currentValue == newValue) return;

        PreviewChanges.Add(new SettingChangePreview
        {
            RoleName = role.RoleDisplayName,
            SettingName = settingName,
            CurrentValue = currentValue ? "Yes" : "No",
            NewValue = newValue ? "Yes" : "No"
        });
    }

    private string FormatAuthContext(string? claimValue)
    {
        if (string.IsNullOrWhiteSpace(claimValue))
            return "None";

        var match = AuthContextItems.FirstOrDefault(c => c.Id == claimValue);
        return match != null ? match.ToString() : claimValue;
    }

    private static string FormatApprovers(List<DirectoryUser>? approvers)
    {
        if (approvers == null || approvers.Count == 0)
            return "None";

        // One approver per line: "Display Name (detail) — User/Group".
        return string.Join(Environment.NewLine, approvers.Select(a =>
        {
            var detail = string.IsNullOrWhiteSpace(a.SecondaryText) ? string.Empty : $" ({a.SecondaryText})";
            return $"{a.DisplayName}{detail} — {a.TypeLabel}";
        }));
    }

    #endregion

    #region Apply

    [RelayCommand]
    private async Task ApplyChangesAsync()
    {
        var selectedRoles = RolePolicies.Where(r => r.IsSelected).ToList();

        // Sort: Entra ID roles first, then Groups, then Azure Resources
        var orderedRoles = selectedRoles
            .OrderBy(r => r.Category switch
            {
                PimCategory.EntraIdRoles => 0,
                PimCategory.Groups => 1,
                PimCategory.AzureResources => 2,
                _ => 3
            })
            .ToList();

        ApplyTotal = orderedRoles.Count;
        ApplyCompleted = 0;
        ApplyFailed = 0;
        ApplyProgress = 0;
        IsApplyComplete = false;
        AppliedRoleResults.Clear();
        AppliedCategoryGroups.Clear();
        ApplyErrors.Clear();

        foreach (var role in orderedRoles)
            AppliedRoleResults.Add(new RoleApplyResult { RoleName = role.RoleDisplayName, Category = role.Category, Status = RoleApplyStatus.Pending });

        // Build category-grouped apply results
        var categoryOrder = new[] { PimCategory.EntraIdRoles, PimCategory.Groups, PimCategory.AzureResources };
        foreach (var cat in categoryOrder)
        {
            var rolesInCat = AppliedRoleResults.Where(r => r.Category == cat).ToList();
            if (rolesInCat.Count == 0) continue;
            var group = new CategoryApplyGroup { CategoryName = cat.ToDisplayName() };
            foreach (var r in rolesInCat) group.Roles.Add(r);
            AppliedCategoryGroups.Add(group);
        }

        CurrentStep = 5;
        IsBusy = true;

        _log.LogSeparator("BULK APPLY START");
        _log.Log(LogLevel.INFO, LogCategory.SETTINGS,
            $"Starting bulk apply to {ApplyTotal} roles...");

        // Apply to roles concurrently (bounded) — each role is a single PATCH,
        // so parallelizing cuts total time dramatically vs. one-at-a-time.
        const int maxConcurrency = 5;
        using var throttler = new SemaphoreSlim(maxConcurrency);

        ApplyStatus = $"Applying changes... 0/{ApplyTotal}";

        var tasks = new List<Task>(orderedRoles.Count);
        for (int i = 0; i < orderedRoles.Count; i++)
        {
            var role = orderedRoles[i];
            var result = AppliedRoleResults[i];
            var roleSettings = PhaseSettings.TryGetValue(role.Category, out var ps) ? ps : EditSettings;
            tasks.Add(ApplyToRoleAsync(role, result, roleSettings, throttler));
        }

        await Task.WhenAll(tasks);

        IsBusy = false;
        IsApplyComplete = true;
        ApplyStatus = $"Complete! {ApplyCompleted} succeeded, {ApplyFailed} failed.";
        _log.Log(LogLevel.SUCCESS, LogCategory.SETTINGS,
            $"Bulk apply complete. Success: {ApplyCompleted}, Failed: {ApplyFailed}");
        _log.LogSeparator("BULK APPLY END");
    }

    private async Task ApplyToRoleAsync(PimRolePolicy role, RoleApplyResult result, BulkEditSettings roleSettings, SemaphoreSlim throttler)
    {
        await throttler.WaitAsync();
        try
        {
            _log.Log(LogLevel.INFO, LogCategory.SETTINGS,
                $"[{role.Category.ToDisplayName()}] Applying to: {role.RoleDisplayName}...");

            var success = await _graphService.UpdatePolicyAsync(role, roleSettings);
            if (success)
            {
                ApplyCompleted++;
                result.Status = RoleApplyStatus.Success;
            }
            else
            {
                ApplyFailed++;
                result.Status = RoleApplyStatus.Failed;
                result.ErrorMessage = "Update returned failure.";
                ApplyErrors.Add($"{role.RoleDisplayName}: Update returned failure.");
            }
        }
        catch (Exception ex)
        {
            ApplyFailed++;
            result.Status = RoleApplyStatus.Failed;
            result.ErrorMessage = ex.Message;
            ApplyErrors.Add($"{role.RoleDisplayName}: {ex.Message}");
            _log.LogError(ex, $"Failed applying to: {role.RoleDisplayName}");
        }
        finally
        {
            throttler.Release();
            var done = ApplyCompleted + ApplyFailed;
            ApplyProgress = (double)done / ApplyTotal * 100;
            ApplyStatus = $"Applying changes... {done}/{ApplyTotal}";
        }
    }

    #endregion

    #region Navigation

    [RelayCommand]
    private void GoBack()
    {
        if (CurrentStep > 0)
        {
            ErrorMessage = string.Empty;

            // If we're at step 2 (role selection) and not on the first phase,
            // go back to settings of the previous phase
            if (CurrentStep == 2 && CurrentPhase > 0)
            {
                CurrentPhase--;
                OnPropertyChanged(nameof(CurrentPhaseTitle));
                // Restore previous phase settings
                if (PhaseSettings.TryGetValue(CurrentPhaseCategory, out var prevSettings))
                {
                    EditSettings = prevSettings;
                    SelectedApprovers.Clear();
                    if (PhaseApprovers.TryGetValue(CurrentPhaseCategory, out var prevApprovers))
                        foreach (var a in prevApprovers) SelectedApprovers.Add(a);
                }
                CurrentStep = 3;
                return;
            }

            // If going back from preview to settings, go to last phase settings
            if (CurrentStep == 4)
            {
                CurrentPhase = ActivePhases.Count - 1;
                OnPropertyChanged(nameof(CurrentPhaseTitle));
                if (PhaseSettings.TryGetValue(CurrentPhaseCategory, out var lastSettings))
                {
                    EditSettings = lastSettings;
                    SelectedApprovers.Clear();
                    if (PhaseApprovers.TryGetValue(CurrentPhaseCategory, out var lastApprovers))
                        foreach (var a in lastApprovers) SelectedApprovers.Add(a);
                }
                ApplyRoleFilterForPhase();
                CurrentStep = 3;
                return;
            }

            CurrentStep--;

            // Refresh role filter when going back to step 2
            if (CurrentStep == 2)
            {
                OnPropertyChanged(nameof(CurrentPhaseTitle));
                ApplyRoleFilter();
            }
        }
    }

    [RelayCommand]
    private void GoHome()
    {
        ErrorMessage = string.Empty;

        // Deselect all categories
        IncludeEntraIdRoles = false;
        IncludeAzureResources = false;
        IncludeGroups = false;

        // Clear role selections
        foreach (var role in RolePolicies)
            role.IsSelected = false;

        CurrentStep = 1;
        StatusMessage = "Ready. Select PIM categories.";
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            await _authService.LogoutAsync();
            IsAuthenticated = false;
            LoggedInUser = string.Empty;
            CurrentStep = 0;
            StatusMessage = string.Empty;
            ErrorMessage = string.Empty;
            RolePolicies.Clear();
            FilteredRolePolicies.Clear();
            _log.Log(LogLevel.INFO, LogCategory.AUTH, "User signed out.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Logout failed.");
        }
    }

    #endregion
}
