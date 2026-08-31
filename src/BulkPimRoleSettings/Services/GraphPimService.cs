using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BulkPimRoleSettings.Models;

namespace BulkPimRoleSettings.Services;

public sealed class GraphPimService
{
    private const string GraphBetaBase = "https://graph.microsoft.com/beta";
    private const int MaxRetryAttempts = 3;
    private const int MaxFetchConcurrency = 5;

    private readonly HttpClient _http;
    private readonly AuthService _authService;
    private readonly LogService _log = LogService.Instance;

    public GraphPimService(AuthService authService)
    {
        _authService = authService;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// Sends a Graph request using a per-request bearer token (thread-safe for
    /// parallel calls — the shared client's default headers are never mutated)
    /// and retries throttling/transient server errors (429/503/504), honoring
    /// the Retry-After header when present.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpMethod method, string url, string? jsonBody = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await _authService.GetValidTokenAsync();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("No valid access token available.");

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (jsonBody != null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);

            var isTransient = response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

            if (!isTransient || attempt >= MaxRetryAttempts)
                return response;

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            var statusCode = (int)response.StatusCode;
            response.Dispose();

            _log.Log(LogLevel.WARN, LogCategory.API,
                $"Transient error {statusCode}. Retrying in {delay.TotalSeconds:N0}s (attempt {attempt}/{MaxRetryAttempts})...");
            await Task.Delay(delay);
        }
    }

    private async Task<JsonNode?> ExecuteAsync(HttpMethod method, string url, string? jsonBody = null)
    {
        _log.LogApiCall(method.Method, url, jsonBody);

        using var response = await SendWithRetryAsync(method, url, jsonBody);
        var body = await response.Content.ReadAsStringAsync();
        _log.LogApiResponse(url, (int)response.StatusCode, body);

        if (!response.IsSuccessStatusCode)
        {
            var graphError = TryGetGraphErrorMessage(body);
            var message = graphError != null
                ? $"Graph request failed ({(int)response.StatusCode}): {graphError}"
                : $"Graph request failed ({(int)response.StatusCode} {response.StatusCode}).";
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    private static string? TryGetGraphErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task<JsonNode?> GetAsync(string url) => ExecuteAsync(HttpMethod.Get, url);

    private Task<JsonNode?> PostAsync(string url, string jsonBody) => ExecuteAsync(HttpMethod.Post, url, jsonBody);

    private Task<JsonNode?> PatchAsync(string url, string jsonBody) => ExecuteAsync(HttpMethod.Patch, url, jsonBody);

    /// <summary>
    /// Follows @odata.nextLink paging and returns items from every page.
    /// Only follows links that stay on the Microsoft Graph host.
    /// </summary>
    private async Task<List<JsonNode>> GetPagedValuesAsync(string url)
    {
        var items = new List<JsonNode>();
        var nextUrl = url;

        while (!string.IsNullOrEmpty(nextUrl))
        {
            var result = await GetAsync(nextUrl);
            var values = result?["value"]?.AsArray();
            if (values != null)
            {
                foreach (var item in values)
                {
                    if (item != null) items.Add(item);
                }
            }

            nextUrl = result?["@odata.nextLink"]?.GetValue<string>();
            if (nextUrl != null && !nextUrl.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))
            {
                _log.Log(LogLevel.WARN, LogCategory.API, "Ignoring paging link pointing to an unexpected host.");
                break;
            }
        }

        return items;
    }

    /// <summary>Escapes a value for safe use inside an OData string literal.</summary>
    private static string EscapeODataString(string value) => value.Replace("'", "''");

    /// <summary>
    /// Resolves directory object IDs (users or groups) to their display names / UPNs.
    /// Used to enrich approver entries read from a policy, which may only contain IDs.
    /// </summary>
    public async Task<List<DirectoryUser>> ResolveDirectoryObjectsAsync(IEnumerable<string> ids)
    {
        var resolved = new List<DirectoryUser>();
        var idList = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        if (idList.Count == 0) return resolved;

        try
        {
            var payload = new Dictionary<string, object>
            {
                ["ids"] = idList,
                ["types"] = new[] { "user", "group" }
            };
            var body = JsonSerializer.Serialize(payload);
            var result = await PostAsync($"{GraphBetaBase}/directoryObjects/getByIds", body);
            var values = result?["value"]?.AsArray();

            if (values != null)
            {
                foreach (var item in values)
                {
                    if (item == null) continue;
                    var id = item["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id)) continue;

                    var odataType = item["@odata.type"]?.GetValue<string>() ?? string.Empty;
                    var isGroup = odataType.Contains("group", StringComparison.OrdinalIgnoreCase);

                    resolved.Add(new DirectoryUser
                    {
                        Id = id,
                        DisplayName = item["displayName"]?.GetValue<string>() ?? string.Empty,
                        UserPrincipalName = item["userPrincipalName"]?.GetValue<string>() ?? string.Empty,
                        Mail = item["mail"]?.GetValue<string>() ?? string.Empty,
                        IsGroup = isGroup,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to resolve directory objects for approvers.");
        }

        return resolved;
    }

    #region Permission Check

    public async Task<(bool HasAccess, string[] MissingPermissions)> CheckPermissionsAsync()
    {
        try
        {
            // Check if user can read role management policies (basic access test)
            // This endpoint requires a filter - use DirectoryRole scope which is always present
            var url = $"{GraphBetaBase}/policies/roleManagementPolicies?$filter=scopeId eq '/' and scopeType eq 'DirectoryRole'&$top=1";
            _log.LogApiCall("GET", url);

            using var response = await SendWithRetryAsync(HttpMethod.Get, url);
            _log.LogApiResponse(url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                _log.Log(LogLevel.SUCCESS, LogCategory.PERMISSION, "User has required permissions.");
                return (true, Array.Empty<string>());
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _log.Log(LogLevel.WARN, LogCategory.PERMISSION,
                    "User lacks required permissions. Status 403.");
                return (false, new[] { "RoleManagementPolicy.ReadWrite.Directory" });
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.Log(LogLevel.WARN, LogCategory.PERMISSION,
                    "User is unauthorized. Token may be invalid. Status 401.");
                return (false, new[] { "Authentication token is invalid or expired." });
            }

            _log.Log(LogLevel.WARN, LogCategory.PERMISSION,
                $"Unexpected status: {response.StatusCode}.");
            return (false, new[] { $"Unexpected error: {response.StatusCode}" });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Permission check failed.");
            return (false, new[] { ex.Message });
        }
    }

    #endregion

    #region Entra ID Roles

    public async Task<List<PimRolePolicy>> GetEntraIdRolePoliciesAsync()
    {
        var policies = new List<PimRolePolicy>();

        try
        {
            // Role definitions (ID -> name) and policy assignments (policy -> role)
            // are independent lookups — fetch them concurrently.
            var roleDefinitionsTask = GetRoleDefinitionsAsync();
            var policyToRoleMapTask = GetPolicyAssignmentsAsync();
            await Task.WhenAll(roleDefinitionsTask, policyToRoleMapTask);

            var roleDefinitions = roleDefinitionsTask.Result;
            var policyToRoleMap = policyToRoleMapTask.Result;

            var url = $"{GraphBetaBase}/policies/roleManagementPolicies?$filter=scopeId eq '/' and scopeType eq 'DirectoryRole'&$expand=rules";
            var values = await GetPagedValuesAsync(url);

            foreach (var item in values)
            {
                var policyId = item["id"]?.GetValue<string>() ?? string.Empty;

                // Look up role definition ID from policy assignments
                var roleDefinitionId = policyToRoleMap.TryGetValue(policyId, out var roleDefId)
                    ? roleDefId
                    : string.Empty;
                var roleName = roleDefinitions.TryGetValue(roleDefinitionId, out var name)
                    ? name
                    : "Unknown Role";

                var policy = new PimRolePolicy
                {
                    Id = policyId,
                    PolicyId = policyId,
                    RoleDisplayName = roleName,
                    RoleDefinitionId = roleDefinitionId,
                    ScopeId = "/",
                    ScopeDisplayName = "Directory",
                    Category = PimCategory.EntraIdRoles
                };

                ParsePolicyRules(item, policy);
                policies.Add(policy);
            }

            _log.Log(LogLevel.SUCCESS, LogCategory.API, $"Retrieved {policies.Count} Entra ID role policies.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to get Entra ID role policies.");
            throw;
        }

        return policies;
    }

    private async Task<Dictionary<string, string>> GetPolicyAssignmentsAsync()
    {
        // Maps policy ID -> role definition ID
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var url = $"{GraphBetaBase}/policies/roleManagementPolicyAssignments?$filter=scopeId eq '/' and scopeType eq 'DirectoryRole'&$select=policyId,roleDefinitionId";
            var values = await GetPagedValuesAsync(url);

            foreach (var item in values)
            {
                var policyId = item["policyId"]?.GetValue<string>();
                var roleDefinitionId = item["roleDefinitionId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(policyId) && !string.IsNullOrEmpty(roleDefinitionId))
                {
                    map[policyId] = roleDefinitionId;
                }
            }

            _log.Log(LogLevel.INFO, LogCategory.API, $"Retrieved {map.Count} policy assignments for role mapping.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to get policy assignments. Role names may not display correctly.");
        }

        return map;
    }

    private async Task<Dictionary<string, string>> GetRoleDefinitionsAsync()
    {
        var roleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var url = $"{GraphBetaBase}/roleManagement/directory/roleDefinitions?$select=id,displayName";
            var values = await GetPagedValuesAsync(url);

            foreach (var item in values)
            {
                var id = item["id"]?.GetValue<string>();
                var displayName = item["displayName"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(displayName))
                {
                    roleMap[id] = displayName;
                }
            }

            _log.Log(LogLevel.INFO, LogCategory.API, $"Retrieved {roleMap.Count} role definitions for name resolution.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to get role definitions. Role names may not display correctly.");
        }

        return roleMap;
    }

    #endregion

    #region Azure Resources

    public async Task<List<AzureScope>> GetAzureResourceScopesAsync()
    {
        var scopes = new List<AzureScope>();

        try
        {
            // Get subscriptions the user can access via PIM
            var url = $"{GraphBetaBase}/privilegedAccess/azureResources/resources?$filter=type eq 'subscription'&$top=100";
            var result = await GetAsync(url);
            var values = result?["value"]?.AsArray();

            if (values != null)
            {
                foreach (var item in values)
                {
                    if (item == null) continue;
                    scopes.Add(new AzureScope
                    {
                        Id = item["id"]?.GetValue<string>() ?? string.Empty,
                        DisplayName = item["displayName"]?.GetValue<string>() ?? "Unknown",
                        Type = "Subscription"
                    });
                }
            }

            _log.Log(LogLevel.SUCCESS, LogCategory.API, $"Retrieved {scopes.Count} Azure resource scopes.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to get Azure resource scopes.");
            throw;
        }

        return scopes;
    }

    public async Task<List<PimRolePolicy>> GetAzureResourceRolePoliciesAsync(string resourceId)
    {
        var policies = new List<PimRolePolicy>();

        try
        {
            var url = $"{GraphBetaBase}/privilegedAccess/azureResources/resources/{Uri.EscapeDataString(resourceId)}/roleSettings";
            var result = await GetAsync(url);
            var values = result?["value"]?.AsArray();

            if (values == null) return policies;

            foreach (var item in values)
            {
                if (item == null) continue;

                var policy = new PimRolePolicy
                {
                    Id = item["id"]?.GetValue<string>() ?? string.Empty,
                    PolicyId = item["id"]?.GetValue<string>() ?? string.Empty,
                    RoleDisplayName = item["roleDefinition"]?["displayName"]?.GetValue<string>() ?? "Unknown Role",
                    RoleDefinitionId = item["roleDefinition"]?["id"]?.GetValue<string>() ?? string.Empty,
                    ScopeId = resourceId,
                    ScopeDisplayName = item["resourceDisplayName"]?.GetValue<string>() ?? string.Empty,
                    Category = PimCategory.AzureResources
                };

                policies.Add(policy);
            }

            _log.Log(LogLevel.SUCCESS, LogCategory.API,
                $"Retrieved {policies.Count} Azure resource role policies for resource {resourceId}.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, $"Failed to get Azure resource role policies for {resourceId}.");
            throw;
        }

        return policies;
    }

    #endregion

    #region Groups

    public async Task<List<AzureScope>> GetPimGroupResourcesAsync()
    {
        var groups = new List<AzureScope>();

        // Step 1: Discover PIM-onboarded group IDs
        // Equivalent of GET https://api.azrbac.mspim.azure.com/api/v2/privilegedAccess/aadGroups/resources
        var url = $"{GraphBetaBase}/identityGovernance/privilegedAccess/group/resources?$select=id&$top=999";
        var values = await GetPagedValuesAsync(url);

        if (values.Count == 0) return groups;

        var groupIds = new List<string>();
        foreach (var item in values)
        {
            var id = item["id"]?.GetValue<string>();
            // Group IDs are interpolated into $filter clauses below — only accept well-formed GUIDs.
            if (!string.IsNullOrEmpty(id) && Guid.TryParse(id, out _)) groupIds.Add(id);
        }

        _log.Log(LogLevel.INFO, LogCategory.API, $"Discovered {groupIds.Count} PIM-onboarded groups.");

        // Step 2: Resolve display names in batches
        foreach (var batch in groupIds.Chunk(15))
        {
            var idFilter = string.Join(",", batch.Select(id => $"'{id}'"));
            var nameUrl = $"{GraphBetaBase}/groups?$filter=id in ({idFilter})&$select=id,displayName";
            try
            {
                var nameResult = await GetAsync(nameUrl);
                var nameValues = nameResult?["value"]?.AsArray();
                if (nameValues != null)
                {
                    foreach (var g in nameValues)
                    {
                        if (g == null) continue;
                        groups.Add(new AzureScope
                        {
                            Id = g["id"]?.GetValue<string>() ?? string.Empty,
                            DisplayName = g["displayName"]?.GetValue<string>() ?? "Unknown Group",
                            Type = "Group"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to resolve group display names.");
                // Fall back to IDs without display names
                foreach (var id in batch)
                {
                    groups.Add(new AzureScope { Id = id, DisplayName = id, Type = "Group" });
                }
            }
        }

        return groups;
    }

    public async Task<List<PimRolePolicy>> GetGroupPoliciesAsync(IEnumerable<AzureScope> selectedGroups)
    {
        var policies = new List<PimRolePolicy>();
        var policiesLock = new object();

        try
        {
            // Each group is an independent Graph call — run them with bounded
            // parallelism to keep load times flat as group count grows.
            using var throttler = new SemaphoreSlim(MaxFetchConcurrency);
            var tasks = selectedGroups.Select(async group =>
            {
                await throttler.WaitAsync();
                try
                {
                    // Guard against malformed IDs reaching the $filter clause.
                    if (!Guid.TryParse(group.Id, out _))
                    {
                        _log.Log(LogLevel.WARN, LogCategory.API, $"Skipping group with non-GUID ID: '{group.DisplayName}'.");
                        return;
                    }

                    // Per Microsoft Graph docs, PIM for Groups requires scopeId (group ID) in the filter.
                    // GET /policies/roleManagementPolicyAssignments?$filter=scopeId eq '{groupId}' and scopeType eq 'Group'
                    var url = $"{GraphBetaBase}/policies/roleManagementPolicyAssignments?$filter=scopeId eq '{group.Id}' and scopeType eq 'Group'&$expand=policy($expand=rules)&$select=policyId,roleDefinitionId,policy";
                    var result = await GetAsync(url);
                    var assignments = result?["value"]?.AsArray();

                    if (assignments == null) return;

                    foreach (var assignment in assignments)
                    {
                        if (assignment == null) continue;

                        var policyId = assignment["policyId"]?.GetValue<string>() ?? string.Empty;
                        var roleType = assignment["roleDefinitionId"]?.GetValue<string>() ?? "member";
                        var policyNode = assignment["policy"];

                        if (string.IsNullOrEmpty(policyId) || policyNode == null) continue;

                        var policy = new PimRolePolicy
                        {
                            Id = policyId,
                            PolicyId = policyId,
                            RoleDisplayName = $"{group.DisplayName} ({roleType})",
                            RoleDefinitionId = roleType,
                            ScopeId = group.Id,
                            ScopeDisplayName = "Group",
                            Category = PimCategory.Groups
                        };

                        ParsePolicyRules(policyNode, policy);
                        lock (policiesLock)
                        {
                            policies.Add(policy);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, $"Failed to get policies for group '{group.DisplayName}' ({group.Id}).");
                }
                finally
                {
                    throttler.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            // Deterministic ordering regardless of task completion order.
            policies.Sort((a, b) => string.Compare(a.RoleDisplayName, b.RoleDisplayName, StringComparison.OrdinalIgnoreCase));

            _log.Log(LogLevel.SUCCESS, LogCategory.API, $"Retrieved {policies.Count} group policies.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to get group policies.");
            throw;
        }

        return policies;
    }

    #endregion

    #region Directory Search

    public async Task<List<DirectoryUser>> SearchUsersAsync(string query)
    {
        var results = new List<DirectoryUser>();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2) return results;

        // Escape single quotes for the OData string literal first (prevents
        // filter injection), then URL-encode the whole value.
        var encoded = Uri.EscapeDataString(EscapeODataString(query.Trim()));

        // Search users and groups concurrently — independent requests.
        var userTask = SearchDirectoryAsync(
            $"{GraphBetaBase}/users?$filter=startswith(displayName,'{encoded}') or startswith(userPrincipalName,'{encoded}')&$top=10&$select=id,displayName,userPrincipalName",
            isGroup: false, query);
        var groupTask = SearchDirectoryAsync(
            $"{GraphBetaBase}/groups?$filter=startswith(displayName,'{encoded}') or startswith(mail,'{encoded}')&$top=10&$select=id,displayName,mail",
            isGroup: true, query);

        await Task.WhenAll(userTask, groupTask);
        results.AddRange(userTask.Result);
        results.AddRange(groupTask.Result);

        // Users first, then groups; both alphabetical for a predictable list.
        return results
            .OrderBy(r => r.IsGroup)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<DirectoryUser>> SearchDirectoryAsync(string url, bool isGroup, string query)
    {
        var results = new List<DirectoryUser>();
        try
        {
            var result = await GetAsync(url);
            var values = result?["value"]?.AsArray();

            if (values != null)
            {
                foreach (var item in values)
                {
                    if (item == null) continue;
                    results.Add(new DirectoryUser
                    {
                        Id = item["id"]?.GetValue<string>() ?? string.Empty,
                        DisplayName = item["displayName"]?.GetValue<string>() ?? string.Empty,
                        UserPrincipalName = isGroup ? string.Empty : item["userPrincipalName"]?.GetValue<string>() ?? string.Empty,
                        Mail = isGroup ? item["mail"]?.GetValue<string>() ?? string.Empty : string.Empty,
                        IsGroup = isGroup,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, $"{(isGroup ? "Group" : "User")} search failed for query: {query}");
        }
        return results;
    }

    #endregion

    #region Apply Settings

    public async Task<bool> UpdatePolicyAsync(PimRolePolicy rolePolicy, BulkEditSettings settings)
    {
        try
        {
            var rules = BuildPolicyRules(rolePolicy, settings);
            if (rules.Count == 0)
            {
                _log.Log(LogLevel.INFO, LogCategory.SETTINGS,
                    $"No changes to apply for: {rolePolicy.RoleDisplayName}");
                return true;
            }

            // Update all rules in a single PATCH on the policy instead of one
            // request per rule — dramatically fewer round-trips per role/group.
            var url = $"{GraphBetaBase}/policies/roleManagementPolicies/{rolePolicy.PolicyId}";
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["rules"] = rules.Select(r => r.ToJsonObject()).ToList()
            });

            await PatchAsync(url, body);

            _log.Log(LogLevel.SUCCESS, LogCategory.SETTINGS,
                $"Updated {rules.Count} rules for '{rolePolicy.RoleDisplayName}' in a single request");

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, $"Failed to update policy for: {rolePolicy.RoleDisplayName}");
            return false;
        }
    }

    #endregion

    #region Private Helpers

    private void ParsePolicyRules(JsonNode? policyNode, PimRolePolicy policy)
    {
        var rules = policyNode?["rules"]?.AsArray();
        if (rules == null) return;

        foreach (var rule in rules)
        {
            if (rule == null) continue;
            var ruleType = rule["@odata.type"]?.GetValue<string>() ?? string.Empty;
            var ruleId = rule["id"]?.GetValue<string>() ?? string.Empty;

            switch (ruleType)
            {
                case "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule":
                    ParseExpirationRule(rule, ruleId, policy);
                    break;
                case "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule":
                    ParseEnablementRule(rule, ruleId, policy);
                    break;
                case "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule":
                    ParseApprovalRule(rule, policy);
                    break;
                case "#microsoft.graph.unifiedRoleManagementPolicyNotificationRule":
                    ParseNotificationRule(rule, ruleId, policy);
                    break;
                case "#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule":
                    ParseAuthContextRule(rule, policy);
                    break;
            }
        }
    }

    private void ParseExpirationRule(JsonNode rule, string ruleId, PimRolePolicy policy)
    {
        var maxDuration = rule["maximumDuration"]?.GetValue<string>();
        var isPermanent = rule["isExpirationRequired"]?.GetValue<bool>() ?? false;

        if (ruleId.Contains("Activation", StringComparison.OrdinalIgnoreCase) && maxDuration != null)
        {
            // Parse ISO 8601 duration like "PT8H"
            if (maxDuration.StartsWith("PT") && maxDuration.EndsWith("H"))
            {
                if (int.TryParse(maxDuration[2..^1], out var hours))
                    policy.CurrentSettings.ActivationMaxDurationHours = hours;
            }
        }
        else if (ruleId.Contains("Eligibility", StringComparison.OrdinalIgnoreCase))
        {
            policy.CurrentSettings.AllowPermanentEligibleAssignment = !isPermanent;
            if (maxDuration != null)
            {
                var days = ParseDurationDays(maxDuration);
                if (days.HasValue)
                    policy.CurrentSettings.ExpireEligibleAfterDays = days;
            }
        }
        else if (ruleId.Contains("Assignment", StringComparison.OrdinalIgnoreCase))
        {
            policy.CurrentSettings.AllowPermanentActiveAssignment = !isPermanent;
            if (maxDuration != null)
            {
                var days = ParseDurationDays(maxDuration);
                if (days.HasValue)
                    policy.CurrentSettings.ExpireActiveAfterDays = days;
            }
        }
    }

    private void ParseEnablementRule(JsonNode rule, string ruleId, PimRolePolicy policy)
    {
        var enabledRules = rule["enabledRules"]?.AsArray();
        if (enabledRules == null) return;

        var rulesList = enabledRules.Select(r => r?.GetValue<string>() ?? string.Empty).ToList();

        if (ruleId.Contains("Activation", StringComparison.OrdinalIgnoreCase))
        {
            policy.CurrentSettings.RequireMfaOnActivation = rulesList.Contains("MultiFactorAuthentication");
            policy.CurrentSettings.RequireJustificationOnActivation = rulesList.Contains("Justification");
            policy.CurrentSettings.RequireTicketOnActivation = rulesList.Contains("Ticketing");
        }
        else if (ruleId.Contains("Assignment", StringComparison.OrdinalIgnoreCase))
        {
            policy.CurrentSettings.RequireMfaOnActiveAssignment = rulesList.Contains("MultiFactorAuthentication");
            policy.CurrentSettings.RequireJustificationOnActiveAssignment = rulesList.Contains("Justification");
        }
    }

    private void ParseApprovalRule(JsonNode rule, PimRolePolicy policy)
    {
        var isApprovalRequired = rule["setting"]?["isApprovalRequired"]?.GetValue<bool>() ?? false;
        policy.CurrentSettings.RequireApprovalToActivate = isApprovalRequired;

        var approvers = new List<DirectoryUser>();
        var stages = rule["setting"]?["approvalStages"]?.AsArray();
        if (stages != null)
        {
            foreach (var stage in stages)
            {
                var primaryApprovers = stage?["primaryApprovers"]?.AsArray();
                if (primaryApprovers == null) continue;

                foreach (var approver in primaryApprovers)
                {
                    if (approver == null) continue;

                    // Approvers are stored as subjectSet objects (singleUser / groupMembers).
                    // The identifier lives in userId, groupId, or id depending on the type.
                    var odataType = approver["@odata.type"]?.GetValue<string>() ?? string.Empty;
                    var id = approver["userId"]?.GetValue<string>()
                             ?? approver["groupId"]?.GetValue<string>()
                             ?? approver["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id)) continue;

                    var description = approver["description"]?.GetValue<string>();
                    var isGroup = odataType.Contains("group", StringComparison.OrdinalIgnoreCase);

                    approvers.Add(new DirectoryUser
                    {
                        Id = id,
                        DisplayName = string.IsNullOrWhiteSpace(description)
                            ? (isGroup ? "Group" : "User")
                            : description,
                        UserPrincipalName = isGroup ? string.Empty : (description ?? string.Empty),
                        Mail = isGroup ? (description ?? string.Empty) : string.Empty,
                        IsGroup = isGroup,
                    });
                }
            }
        }
        policy.CurrentSettings.Approvers = approvers;
    }

    private void ParseNotificationRule(JsonNode rule, string ruleId, PimRolePolicy policy)
    {
        var recipients = rule["notificationRecipients"]?.AsArray()?
            .Select(r => r?.GetValue<string>() ?? string.Empty)
            .Where(r => !string.IsNullOrEmpty(r))
            .ToArray() ?? Array.Empty<string>();

        var criticalOnly = rule["isDefaultRecipientsEnabled"]?.GetValue<bool>() ?? true;

        // Map notification rules based on ID patterns
        var notification = new NotificationSettings
        {
            AdditionalRecipients = recipients,
            IsDefaultRecipientsEnabled = criticalOnly,
            CriticalEmailsOnly = !(rule["isDefaultRecipientsEnabled"]?.GetValue<bool>() ?? true)
        };

        if (ruleId.Contains("Eligibility", StringComparison.OrdinalIgnoreCase))
        {
            if (ruleId.Contains("Admin", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.EligibleAssignmentAdminNotification = notification;
            else if (ruleId.Contains("Requestor", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.EligibleAssignmentAssigneeNotification = notification;
            else if (ruleId.Contains("Approver", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.EligibleAssignmentApproverNotification = notification;
        }
        else if (ruleId.Contains("Assignment", StringComparison.OrdinalIgnoreCase))
        {
            if (ruleId.Contains("Admin", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.ActiveAssignmentAdminNotification = notification;
            else if (ruleId.Contains("Requestor", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.ActiveAssignmentAssigneeNotification = notification;
            else if (ruleId.Contains("Approver", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.ActiveAssignmentApproverNotification = notification;
        }
        else if (ruleId.Contains("Activation", StringComparison.OrdinalIgnoreCase))
        {
            if (ruleId.Contains("Admin", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.ActivationAdminNotification = notification;
            else if (ruleId.Contains("Requestor", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.ActivationAssigneeNotification = notification;
            else if (ruleId.Contains("Approver", StringComparison.OrdinalIgnoreCase))
                policy.CurrentSettings.ActivationApproverNotification = notification;
        }
    }

    private void ParseAuthContextRule(JsonNode rule, PimRolePolicy policy)
    {
        var isEnabled = rule["isEnabled"]?.GetValue<bool>() ?? false;
        var claimValue = rule["claimValue"]?.GetValue<string>();
        policy.CurrentSettings.RequireAuthContextOnActivation = isEnabled;
        policy.CurrentSettings.AuthContextClaimValue = claimValue;
    }

    public async Task<List<AuthContextItem>> GetAuthenticationContextsAsync()
    {
        var contexts = new List<AuthContextItem>();

        try
        {
            var url = $"{GraphBetaBase}/identity/conditionalAccess/authenticationContextClassReferences";
            var values = await GetPagedValuesAsync(url);

            foreach (var item in values)
            {
                contexts.Add(new AuthContextItem
                {
                    Id = item["id"]?.GetValue<string>() ?? string.Empty,
                    DisplayName = item["displayName"]?.GetValue<string>() ?? string.Empty
                });
            }

            _log.Log(LogLevel.SUCCESS, LogCategory.API, $"Retrieved {contexts.Count} authentication contexts.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to get authentication contexts.");
        }

        return contexts;
    }

    private static int? ParseDurationDays(string duration)
    {
        // Parse ISO 8601 durations like "P365D", "P30D"
        if (duration.StartsWith("P") && duration.EndsWith("D"))
        {
            if (int.TryParse(duration[1..^1], out var days))
                return days;
        }
        return null;
    }

    private List<PolicyRuleUpdate> BuildPolicyRules(PimRolePolicy policy, BulkEditSettings settings)
    {
        var rules = new List<PolicyRuleUpdate>();

        // Activation expiration rule
        {
            _log.LogSettingChange(policy.RoleDisplayName, "ActivationMaxDurationHours",
                policy.CurrentSettings.ActivationMaxDurationHours.ToString(),
                ((int)settings.ActivationMaxDurationHours).ToString());

            rules.Add(new PolicyRuleUpdate
            {
                RuleId = "Expiration_EndUser_Assignment",
                ODataType = "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule",
                Properties = new Dictionary<string, object>
                {
                    ["isExpirationRequired"] = true,
                    ["maximumDuration"] = $"PT{(int)settings.ActivationMaxDurationHours}H",
                    ["target"] = BuildTarget("EndUser", "Assignment")
                }
            });
        }

        // Activation enablement rule (always applied — individual items skip if Unchanged)
        var activationEnabledRules = BuildEnablementRules(policy, settings, isActivation: true);
        rules.Add(new PolicyRuleUpdate
        {
            RuleId = "Enablement_EndUser_Assignment",
            ODataType = "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule",
            Properties = new Dictionary<string, object>
            {
                ["enabledRules"] = activationEnabledRules,
                ["target"] = BuildTarget("EndUser", "Assignment")
            }
        });

        // Assignment enablement rule (always applied — individual items skip if Unchanged)
        var assignmentEnabledRules = BuildEnablementRules(policy, settings, isActivation: false);
        rules.Add(new PolicyRuleUpdate
        {
            RuleId = "Enablement_Admin_Assignment",
            ODataType = "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule",
            Properties = new Dictionary<string, object>
            {
                ["enabledRules"] = assignmentEnabledRules,
                ["target"] = BuildTarget("Admin", "Assignment")
            }
        });

        // Eligible expiration
        {
            var allowPermanent = settings.AllowPermanentEligibleAssignment;
            var props = new Dictionary<string, object>
            {
                ["target"] = BuildTarget("Admin", "Eligibility"),
                ["isExpirationRequired"] = !allowPermanent
            };

            _log.LogSettingChange(policy.RoleDisplayName, "AllowPermanentEligibleAssignment",
                policy.CurrentSettings.AllowPermanentEligibleAssignment.ToString(),
                allowPermanent.ToString());

            if (!allowPermanent)
            {
                props["maximumDuration"] = $"P{(int)settings.ExpireEligibleAfterDays}D";
                _log.LogSettingChange(policy.RoleDisplayName, "ExpireEligibleAfterDays",
                    policy.CurrentSettings.ExpireEligibleAfterDays?.ToString() ?? "N/A",
                    ((int)settings.ExpireEligibleAfterDays).ToString());
            }

            rules.Add(new PolicyRuleUpdate
            {
                RuleId = "Expiration_Admin_Eligibility",
                ODataType = "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule",
                Properties = props
            });
        }

        // Active expiration
        {
            var allowPermanent = settings.AllowPermanentActiveAssignment;
            var props = new Dictionary<string, object>
            {
                ["target"] = BuildTarget("Admin", "Assignment"),
                ["isExpirationRequired"] = !allowPermanent
            };

            _log.LogSettingChange(policy.RoleDisplayName, "AllowPermanentActiveAssignment",
                policy.CurrentSettings.AllowPermanentActiveAssignment.ToString(),
                allowPermanent.ToString());

            if (!allowPermanent)
            {
                props["maximumDuration"] = $"P{(int)settings.ExpireActiveAfterDays}D";
                _log.LogSettingChange(policy.RoleDisplayName, "ExpireActiveAfterDays",
                    policy.CurrentSettings.ExpireActiveAfterDays?.ToString() ?? "N/A",
                    ((int)settings.ExpireActiveAfterDays).ToString());
            }

            rules.Add(new PolicyRuleUpdate
            {
                RuleId = "Expiration_Admin_Assignment",
                ODataType = "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule",
                Properties = props
            });
        }

        // Approval rule
        {
            var approvalRequired = settings.RequireApprovalToActivate;
            _log.LogSettingChange(policy.RoleDisplayName, "RequireApprovalToActivate",
                policy.CurrentSettings.RequireApprovalToActivate.ToString(),
                approvalRequired.ToString());

            var approvalProps = new Dictionary<string, object>
            {
                ["target"] = BuildTarget("EndUser", "Assignment"),
                ["setting"] = new Dictionary<string, object>
                {
                    ["@odata.type"] = "microsoft.graph.approvalSettings",
                    ["isApprovalRequired"] = approvalRequired,
                    ["isApprovalRequiredForExtension"] = false,
                    ["isRequestorJustificationRequired"] = true,
                    ["approvalMode"] = approvalRequired ? "SingleStage" : "NoApproval",
                    ["approvalStages"] = approvalRequired && !string.IsNullOrWhiteSpace(settings.ApproversRaw)
                        ? BuildApprovalStages(settings.ApproversRaw)
                        : Array.Empty<object>()
                }
            };

            rules.Add(new PolicyRuleUpdate
            {
                RuleId = "Approval_EndUser_Assignment",
                ODataType = "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule",
                Properties = approvalProps
            });
        }

        // Authentication context rule (always applied)
        {
            _log.LogSettingChange(policy.RoleDisplayName, "RequireAuthContextOnActivation",
                policy.CurrentSettings.RequireAuthContextOnActivation.ToString(),
                settings.RequireAuthContextOnActivation.ToString());

            var authContextProps = new Dictionary<string, object>
            {
                ["isEnabled"] = settings.RequireAuthContextOnActivation,
                ["claimValue"] = settings.RequireAuthContextOnActivation ? settings.AuthContextClaimValue : (object)string.Empty,
                ["target"] = BuildTarget("EndUser", "Assignment")
            };

            rules.Add(new PolicyRuleUpdate
            {
                RuleId = "AuthenticationContext_EndUser_Assignment",
                ODataType = "#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule",
                Properties = authContextProps
            });
        }

        // Notification rules
        BuildNotificationRule(rules, "Notification_Admin_Admin_Eligibility", "Admin", "Eligibility", settings.EligibleAssignmentAdmin);
        BuildNotificationRule(rules, "Notification_Requestor_Admin_Eligibility", "Requestor", "Eligibility", settings.EligibleAssignmentAssignee);
        BuildNotificationRule(rules, "Notification_Approver_Admin_Eligibility", "Approver", "Eligibility", settings.EligibleAssignmentApprover);

        BuildNotificationRule(rules, "Notification_Admin_Admin_Assignment", "Admin", "Assignment", settings.ActiveAssignmentAdmin);
        BuildNotificationRule(rules, "Notification_Requestor_Admin_Assignment", "Requestor", "Assignment", settings.ActiveAssignmentAssignee);
        BuildNotificationRule(rules, "Notification_Approver_Admin_Assignment", "Approver", "Assignment", settings.ActiveAssignmentApprover);

        BuildNotificationRule(rules, "Notification_Admin_EndUser_Assignment", "Admin", "Assignment", settings.ActivationAdmin, isActivation: true);
        BuildNotificationRule(rules, "Notification_Requestor_EndUser_Assignment", "Requestor", "Assignment", settings.ActivationRequestor, isActivation: true);
        BuildNotificationRule(rules, "Notification_Approver_EndUser_Assignment", "Approver", "Assignment", settings.ActivationApprover, isActivation: true);

        return rules;
    }

    private static Dictionary<string, object> BuildTarget(string caller, string level)
    {
        return new Dictionary<string, object>
        {
            ["@odata.type"] = "microsoft.graph.unifiedRoleManagementPolicyRuleTarget",
            ["caller"] = caller,
            ["operations"] = new[] { "All" },
            ["level"] = level,
            ["inheritableSettings"] = Array.Empty<string>(),
            ["enforcedSettings"] = Array.Empty<string>()
        };
    }

    private List<string> BuildEnablementRules(PimRolePolicy policy, BulkEditSettings settings, bool isActivation)
    {
        var enabledRules = new List<string>();

        if (isActivation)
        {
            if (settings.RequireMfaOnActivation)
                enabledRules.Add("MultiFactorAuthentication");

            if (settings.RequireJustificationOnActivation)
                enabledRules.Add("Justification");

            if (settings.RequireTicketOnActivation)
                enabledRules.Add("Ticketing");

            _log.LogSettingChange(policy.RoleDisplayName, "RequireMfaOnActivation",
                policy.CurrentSettings.RequireMfaOnActivation.ToString(),
                settings.RequireMfaOnActivation.ToString());
            _log.LogSettingChange(policy.RoleDisplayName, "RequireJustificationOnActivation",
                policy.CurrentSettings.RequireJustificationOnActivation.ToString(),
                settings.RequireJustificationOnActivation.ToString());
            _log.LogSettingChange(policy.RoleDisplayName, "RequireTicketOnActivation",
                policy.CurrentSettings.RequireTicketOnActivation.ToString(),
                settings.RequireTicketOnActivation.ToString());
        }
        else
        {
            if (settings.RequireMfaOnActiveAssignment)
                enabledRules.Add("MultiFactorAuthentication");

            if (settings.RequireJustificationOnActiveAssignment)
                enabledRules.Add("Justification");

            _log.LogSettingChange(policy.RoleDisplayName, "RequireMfaOnActiveAssignment",
                policy.CurrentSettings.RequireMfaOnActiveAssignment.ToString(),
                settings.RequireMfaOnActiveAssignment.ToString());
            _log.LogSettingChange(policy.RoleDisplayName, "RequireJustificationOnActiveAssignment",
                policy.CurrentSettings.RequireJustificationOnActiveAssignment.ToString(),
                settings.RequireJustificationOnActiveAssignment.ToString());
        }

        return enabledRules;
    }

    private static object[] BuildApprovalStages(string approversRaw)
    {
        if (string.IsNullOrWhiteSpace(approversRaw))
            return Array.Empty<object>();

        // approversRaw contains semicolon-separated approver entries.
        // Each entry is "user:{id}" or "group:{id}"; a bare "{id}" is treated as a user
        // for backward compatibility.
        var entries = approversRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var approvers = entries.Select(entry =>
        {
            string type = "user";
            string id = entry;

            var separatorIndex = entry.IndexOf(':');
            if (separatorIndex > 0)
            {
                type = entry.Substring(0, separatorIndex).Trim().ToLowerInvariant();
                id = entry.Substring(separatorIndex + 1).Trim();
            }

            return type == "group"
                ? new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.groupMembers",
                    ["groupId"] = id
                }
                : new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.singleUser",
                    ["userId"] = id
                };
        }).ToArray();

        return new object[]
        {
            new Dictionary<string, object>
            {
                ["approvalStageTimeOutInDays"] = 1,
                ["isApproverJustificationRequired"] = true,
                ["escalationTimeInMinutes"] = 0,
                ["primaryApprovers"] = approvers,
                ["isEscalationEnabled"] = false,
                ["escalationApprovers"] = Array.Empty<object>()
            }
        };
    }

    private static void BuildNotificationRule(List<PolicyRuleUpdate> rules, string ruleId, string recipientType, string level, NotificationRowSettings row, bool isActivation = false)
    {
        var additionalRecipients = string.IsNullOrWhiteSpace(row.AdditionalRecipients)
            ? Array.Empty<string>()
            : row.AdditionalRecipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var caller = ruleId.Contains("EndUser") ? "EndUser" : "Admin";

        var props = new Dictionary<string, object>
        {
            ["notificationType"] = "Email",
            ["recipientType"] = recipientType,
            ["notificationLevel"] = row.CriticalOnly ? "Critical" : "All",
            ["isDefaultRecipientsEnabled"] = row.DefaultRecipients,
            ["notificationRecipients"] = additionalRecipients,
            ["target"] = BuildTarget(caller, level)
        };

        rules.Add(new PolicyRuleUpdate
        {
            RuleId = ruleId,
            ODataType = "#microsoft.graph.unifiedRoleManagementPolicyNotificationRule",
            Properties = props
        });
    }

    #endregion
}

public class PolicyRuleUpdate
{
    public string RuleId { get; set; } = string.Empty;
    public string ODataType { get; set; } = string.Empty;
    public Dictionary<string, object> Properties { get; set; } = new();

    public Dictionary<string, object> ToJsonObject()
    {
        return new Dictionary<string, object>(Properties)
        {
            ["@odata.type"] = ODataType,
            ["id"] = RuleId
        };
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(ToJsonObject(), new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
