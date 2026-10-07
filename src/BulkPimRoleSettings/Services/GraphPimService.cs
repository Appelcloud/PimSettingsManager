using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// parallel calls - the shared client's default headers are never mutated)
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

            // PIM policies must always reflect the live tenant state, so prevent
            // any intermediate proxy or handler from serving a cached response.
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.Pragma.ParseAdd("no-cache");

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

    #region Graph Error Parsing

    /// <summary>
    /// A parsed Microsoft Graph OData error envelope.
    /// See https://learn.microsoft.com/graph/errors
    /// </summary>
    public sealed record GraphError(
        int StatusCode,
        string Code,
        string Message,
        string? RequestId,
        string? InnerCode)
    {
        /// <summary>
        /// One-line, support-ready summary. Contains only the service-supplied
        /// error code, message and request id - never tokens or user data.
        /// </summary>
        public string ToDisplayString()
        {
            var text = $"HTTP {StatusCode} {Code}: {Message}";
            if (!string.IsNullOrWhiteSpace(InnerCode) && !string.Equals(InnerCode, Code, StringComparison.OrdinalIgnoreCase))
                text += $" (inner: {InnerCode})";
            if (!string.IsNullOrWhiteSpace(RequestId))
                text += $" [request-id: {RequestId}]";
            return text;
        }
    }

    /// <summary>
    /// Reads the Graph error envelope from a failed response. Falls back to the
    /// status code when the body is missing or is not the expected shape, so a
    /// malformed error can never mask the original failure.
    /// </summary>
    private static async Task<GraphError> ReadGraphErrorAsync(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var code = response.StatusCode.ToString();
        var message = response.ReasonPhrase ?? "No error details were returned by Microsoft Graph.";
        string? requestId = null;
        string? innerCode = null;

        // Graph echoes the request id in a header even when the body is empty.
        if (response.Headers.TryGetValues("request-id", out var headerIds))
            requestId = headerIds.FirstOrDefault();

        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body) && JsonNode.Parse(body) is JsonObject root
                && root["error"] is JsonObject error)
            {
                code = error["code"]?.GetValue<string>() ?? code;
                message = error["message"]?.GetValue<string>() ?? message;

                // Graph is inconsistent about the casing of this property.
                var inner = error["innerError"] as JsonObject ?? error["innererror"] as JsonObject;
                if (inner != null)
                {
                    innerCode = inner["code"]?.GetValue<string>();
                    requestId = inner["request-id"]?.GetValue<string>()
                        ?? inner["requestId"]?.GetValue<string>()
                        ?? inner["client-request-id"]?.GetValue<string>()
                        ?? requestId;
                }
            }
        }
        catch (Exception)
        {
            // Keep the status-code fallback; the caller still reports a precise
            // HTTP status even when the body cannot be read or parsed.
        }

        return new GraphError(status, code, message, requestId, innerCode);
    }

    #endregion

    #region Permission Check

    /// <summary>
    /// Outcome of the startup permission check. <see cref="Reason"/> is shown to
    /// the user; <see cref="Detail"/> is the verbatim service error for the log.
    /// </summary>
    public sealed record PermissionCheckResult(bool HasAccess, string Reason, string Detail)
    {
        public static PermissionCheckResult Success() => new(true, string.Empty, string.Empty);
        public static PermissionCheckResult Failure(string reason, string detail) => new(false, reason, detail);
    }

    /// <summary>
    /// Verifies that the signed-in user can actually read PIM role management
    /// policies. On failure the exact Graph error code, message and request id
    /// are returned and logged so the cause is unambiguous.
    /// </summary>
    public async Task<PermissionCheckResult> CheckPermissionsAsync()
    {
        var url = $"{GraphBetaBase}/policies/roleManagementPolicies?$filter=scopeId eq '/' and scopeType eq 'DirectoryRole'&$top=1";

        try
        {
            _log.LogApiCall("GET", url);

            using var response = await SendWithRetryAsync(HttpMethod.Get, url);
            _log.LogApiResponse(url, (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                _log.Log(LogLevel.SUCCESS, LogCategory.PERMISSION,
                    "Permission check passed: the signed-in user can read PIM role management policies.");
                return PermissionCheckResult.Success();
            }

            var error = await ReadGraphErrorAsync(response);

            // Always record the verbatim service error so the log explains exactly
            // why access was refused.
            _log.Log(LogLevel.ERROR, LogCategory.PERMISSION,
                $"Permission check failed for GET /policies/roleManagementPolicies. {error.ToDisplayString()}");

            var reason = DescribePermissionFailure(error);
            _log.Log(LogLevel.ERROR, LogCategory.PERMISSION, $"Interpretation: {reason}");

            return PermissionCheckResult.Failure(reason, error.ToDisplayString());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Permission check could not be completed.");
            return PermissionCheckResult.Failure(
                $"The permission check could not be completed: {ex.Message}",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns a Graph error into an actionable explanation. The service code is
    /// authoritative, so it drives the message rather than the HTTP status alone.
    /// </summary>
    private static string DescribePermissionFailure(GraphError error)
    {
        var requiredScopes = string.Join(", ", AuthService.RequiredScopes);

        return error.Code switch
        {
            "Authorization_RequestDenied" =>
                "Microsoft Graph denied the request. Your account is missing either the delegated permission "
                + $"({requiredScopes}) or an Entra ID role that grants PIM access. Privileged Role Administrator "
                + "or Global Administrator is normally required to read and modify PIM role settings.",

            "Authorization_IdentityNotFound" =>
                "The signed-in identity could not be resolved in this tenant. Confirm you signed in with an account "
                + "that exists in the tenant you intend to manage.",

            "InvalidAuthenticationToken" or "TokenNotFound" =>
                "The access token was rejected by Microsoft Graph. Sign out and sign in again.",

            "AadPremiumLicenseRequired" or "PimLicenseRequired" =>
                "Privileged Identity Management requires a Microsoft Entra ID P2 or Microsoft Entra ID Governance licence, "
                + "which this tenant does not appear to have.",

            _ when error.StatusCode == 403 =>
                "Microsoft Graph refused the request (403 Forbidden). This is normally missing consent for "
                + $"({requiredScopes}) or a missing Entra ID role such as Privileged Role Administrator.",

            _ when error.StatusCode == 401 =>
                "Microsoft Graph rejected the credentials (401 Unauthorized). The token is invalid or expired; sign in again.",

            _ when error.StatusCode == 404 =>
                "The PIM endpoint was not found for this tenant, which usually means PIM is not enabled or licensed.",

            _ => $"Microsoft Graph returned an unexpected error ({error.Code})."
        };
    }

    #endregion

    #region Entra ID Roles

    public async Task<List<PimRolePolicy>> GetEntraIdRolePoliciesAsync()
    {
        var policies = new List<PimRolePolicy>();

        try
        {
            // Role definitions (ID -> name) and policy assignments (policy -> role)
            // are independent lookups - fetch them concurrently.
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

            _log.Log(LogLevel.DEBUG, LogCategory.API, $"Retrieved {policies.Count} Entra ID role policies.");
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

            _log.Log(LogLevel.DEBUG, LogCategory.API, $"Retrieved {map.Count} policy assignments for role mapping.");
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

            _log.Log(LogLevel.DEBUG, LogCategory.API, $"Retrieved {roleMap.Count} role definitions for name resolution.");
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

            _log.Log(LogLevel.DEBUG, LogCategory.API, $"Retrieved {scopes.Count} Azure resource scopes.");
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

            _log.Log(LogLevel.DEBUG, LogCategory.API,
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
            // Group IDs are interpolated into $filter clauses below - only accept well-formed GUIDs.
            if (!string.IsNullOrEmpty(id) && Guid.TryParse(id, out _)) groupIds.Add(id);
        }

        _log.Log(LogLevel.DEBUG, LogCategory.API, $"Discovered {groupIds.Count} PIM-onboarded groups.");

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
            // Each group is an independent Graph call - run them with bounded
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

            _log.Log(LogLevel.DEBUG, LogCategory.API, $"Retrieved {policies.Count} group policies.");
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

        // Search users and groups concurrently - independent requests.
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
            var changes = new List<string>();
            var rules = BuildPolicyRules(rolePolicy, settings, changes);
            if (rules.Count == 0)
            {
                _log.Log(LogLevel.DEBUG, LogCategory.SETTINGS,
                    $"No changes to apply for: {rolePolicy.RoleDisplayName}");
                return true;
            }

            // Update all rules in a single PATCH on the policy instead of one
            // request per rule - dramatically fewer round-trips per role/group.
            var url = $"{GraphBetaBase}/policies/roleManagementPolicies/{rolePolicy.PolicyId}";
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["rules"] = rules.Select(r => r.ToJsonObject()).ToList()
            });

            await PatchAsync(url, body);

            if (changes.Count == 0)
            {
                _log.Log(LogLevel.SUCCESS, LogCategory.SETTINGS,
                    $"Applied to '{rolePolicy.RoleDisplayName}': no settings differed from the current configuration.");
                return true;
            }

            _log.Log(LogLevel.SUCCESS, LogCategory.SETTINGS,
                $"Applied {changes.Count} setting(s) to '{rolePolicy.RoleDisplayName}':");

            foreach (var change in changes)
            {
                _log.Log(LogLevel.SUCCESS, LogCategory.SETTINGS,
                    $"    {rolePolicy.RoleDisplayName} | {change}");
            }

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
                    ParseApprovalRule(rule, ruleId, policy);
                    break;
                case "#microsoft.graph.unifiedRoleManagementPolicyNotificationRule":
                    ParseNotificationRule(rule, ruleId, policy);
                    break;
                case "#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule":
                    ParseAuthContextRule(rule, policy);
                    break;
                case "#microsoft.graph.unifiedRoleManagementPolicyCustomExtensionRule":
                    // Known Graph rule type for Logic App callouts on approval
                    // (CustomExtension_PreApproval/PostApproval). This tool does not
                    // edit custom extensions, so the rule is intentionally ignored
                    // and left untouched by the PATCH payload.
                    break;
                default:
                    // Surfaces schema changes instead of silently showing defaults.
                    _log.Log(LogLevel.DEBUG, LogCategory.SETTINGS,
                        $"Unhandled policy rule type '{ruleType}' (id '{ruleId}') for '{policy.RoleDisplayName}'.");
                    break;
            }
        }

        var s = policy.CurrentSettings;
        _log.Log(LogLevel.DEBUG, LogCategory.SETTINGS,
            $"Parsed policy for '{policy.RoleDisplayName}': maxDuration={s.ActivationMaxDurationHours}h, "
            + $"mfaOnActivation={s.RequireMfaOnActivation}, authContext={s.RequireAuthContextOnActivation}, "
            + $"justification={s.RequireJustificationOnActivation}, ticket={s.RequireTicketOnActivation}, "
            + $"approval={s.RequireApprovalToActivate} ({s.Approvers.Count} approver(s)), "
            + $"permanentEligible={s.AllowPermanentEligibleAssignment}, expireEligible={s.ExpireEligibleAfterDays?.ToString() ?? "none"}d, "
            + $"permanentActive={s.AllowPermanentActiveAssignment}, expireActive={s.ExpireActiveAfterDays?.ToString() ?? "none"}d, "
            + $"mfaOnActiveAssignment={s.RequireMfaOnActiveAssignment}, justificationOnActiveAssignment={s.RequireJustificationOnActiveAssignment}");
    }

    // Graph rule IDs follow the pattern <Category>_<Caller>_<Scope>, for example
    // Enablement_EndUser_Assignment or Expiration_Admin_Eligibility. Matching on
    // substrings such as "Assignment" is ambiguous because it appears in both the
    // activation rules (EndUser) and the active-assignment rules (Admin), so the
    // parsers below compare the full rule ID documented in the PIM rules mapping.
    // See https://learn.microsoft.com/graph/identity-governance-pim-rules-overview

    private void ParseExpirationRule(JsonNode rule, string ruleId, PimRolePolicy policy)
    {
        var maxDuration = rule["maximumDuration"]?.GetValue<string>();
        var isExpirationRequired = rule["isExpirationRequired"]?.GetValue<bool>() ?? false;

        switch (ruleId)
        {
            // Activation maximum duration (hours).
            case "Expiration_EndUser_Assignment":
                var hours = ParseDurationHours(maxDuration);
                if (hours.HasValue)
                    policy.CurrentSettings.ActivationMaxDurationHours = hours.Value;
                break;

            // Allow permanent eligible assignment / Expire eligible assignments after.
            case "Expiration_Admin_Eligibility":
                policy.CurrentSettings.AllowPermanentEligibleAssignment = !isExpirationRequired;
                policy.CurrentSettings.ExpireEligibleAfterDays = ParseDurationDays(maxDuration);
                break;

            // Allow permanent active assignment / Expire active assignments after.
            case "Expiration_Admin_Assignment":
                policy.CurrentSettings.AllowPermanentActiveAssignment = !isExpirationRequired;
                policy.CurrentSettings.ExpireActiveAfterDays = ParseDurationDays(maxDuration);
                break;
        }
    }

    private void ParseEnablementRule(JsonNode rule, string ruleId, PimRolePolicy policy)
    {
        // An empty enabledRules collection is meaningful: it means "None" is
        // selected. Treat a missing collection the same way rather than leaving
        // the previous values untouched.
        var rulesList = rule["enabledRules"]?.AsArray()?
            .Select(r => r?.GetValue<string>() ?? string.Empty)
            .ToList() ?? new List<string>();

        switch (ruleId)
        {
            // On activation, require: None / Azure MFA / justification / ticketing.
            case "Enablement_EndUser_Assignment":
                policy.CurrentSettings.RequireMfaOnActivation = rulesList.Contains("MultiFactorAuthentication");
                policy.CurrentSettings.RequireJustificationOnActivation = rulesList.Contains("Justification");
                policy.CurrentSettings.RequireTicketOnActivation = rulesList.Contains("Ticketing");
                break;

            // Require MFA / justification on active assignment.
            case "Enablement_Admin_Assignment":
                policy.CurrentSettings.RequireMfaOnActiveAssignment = rulesList.Contains("MultiFactorAuthentication");
                policy.CurrentSettings.RequireJustificationOnActiveAssignment = rulesList.Contains("Justification");
                break;
        }
    }

    private void ParseApprovalRule(JsonNode rule, string ruleId, PimRolePolicy policy)
    {
        // "Require approval to activate" is the end-user rule. A policy can also
        // carry Approval_Admin_Assignment / Approval_Admin_Eligibility, and
        // without this guard whichever rule is parsed last would win.
        if (ruleId != "Approval_EndUser_Assignment")
            return;

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

        var notification = new NotificationSettings
        {
            AdditionalRecipients = recipients,
            // Controls whether the built-in recipient receives the mail.
            IsDefaultRecipientsEnabled = rule["isDefaultRecipientsEnabled"]?.GetValue<bool>() ?? true,
            // Separate property on the Graph rule; "Critical" means critical only.
            CriticalEmailsOnly = string.Equals(
                rule["notificationLevel"]?.GetValue<string>(),
                "Critical",
                StringComparison.OrdinalIgnoreCase)
        };

        _log.Log(LogLevel.DEBUG, LogCategory.SETTINGS,
            $"Notification rule '{ruleId}': defaultRecipients={notification.IsDefaultRecipientsEnabled}, "
            + $"criticalOnly={notification.CriticalEmailsOnly}, "
            + $"additional=[{string.Join("; ", notification.AdditionalRecipients)}]");

        // Notification rule IDs are Notification_<Recipient>_<Caller>_<Scope>,
        // for example Notification_Admin_Admin_Eligibility. The caller segment
        // distinguishes the three blocks shown in the portal.
        switch (ruleId)
        {
            // Members assigned as eligible.
            case "Notification_Admin_Admin_Eligibility":
                policy.CurrentSettings.EligibleAssignmentAdminNotification = notification;
                break;
            case "Notification_Requestor_Admin_Eligibility":
                policy.CurrentSettings.EligibleAssignmentAssigneeNotification = notification;
                break;
            case "Notification_Approver_Admin_Eligibility":
                policy.CurrentSettings.EligibleAssignmentApproverNotification = notification;
                break;

            // Members assigned as active.
            case "Notification_Admin_Admin_Assignment":
                policy.CurrentSettings.ActiveAssignmentAdminNotification = notification;
                break;
            case "Notification_Requestor_Admin_Assignment":
                policy.CurrentSettings.ActiveAssignmentAssigneeNotification = notification;
                break;
            case "Notification_Approver_Admin_Assignment":
                policy.CurrentSettings.ActiveAssignmentApproverNotification = notification;
                break;

            // Eligible members activating the role.
            case "Notification_Admin_EndUser_Assignment":
                policy.CurrentSettings.ActivationAdminNotification = notification;
                break;
            case "Notification_Requestor_EndUser_Assignment":
                policy.CurrentSettings.ActivationAssigneeNotification = notification;
                break;
            case "Notification_Approver_EndUser_Assignment":
                policy.CurrentSettings.ActivationApproverNotification = notification;
                break;
            default:
                _log.Log(LogLevel.DEBUG, LogCategory.SETTINGS,
                    $"Unrecognized notification rule id '{ruleId}'; its values are not shown in the UI.");
                break;
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

            _log.Log(LogLevel.DEBUG, LogCategory.API, $"Retrieved {contexts.Count} authentication contexts.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to get authentication contexts.");
        }

        return contexts;
    }

    /// <summary>
    /// Parses the hour component of an ISO 8601 duration such as "PT8H".
    /// </summary>
    private static int? ParseDurationHours(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return null;

        try
        {
            var span = System.Xml.XmlConvert.ToTimeSpan(duration);
            var hours = (int)Math.Round(span.TotalHours);
            return hours > 0 ? hours : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an ISO 8601 duration into whole days. Graph returns year and month
    /// based durations such as "P1Y" for the "1 year(s)" option in the portal, so
    /// a plain "P365D" check would silently drop the value.
    /// </summary>
    private static int? ParseDurationDays(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return null;

        // XmlConvert rejects year and month designators because their length is
        // not fixed, so normalize those to days first.
        var match = System.Text.RegularExpressions.Regex.Match(
            duration, @"^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)D)?$");

        if (match.Success && match.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).Any(g => g.Success))
        {
            var years = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
            var months = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
            var days = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

            // PIM treats a year as 365 days and a month as 30 days.
            var total = (years * 365) + (months * 30) + days;
            return total > 0 ? total : null;
        }

        try
        {
            var span = System.Xml.XmlConvert.ToTimeSpan(duration);
            var totalDays = (int)Math.Round(span.TotalDays);
            return totalDays > 0 ? totalDays : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private List<PolicyRuleUpdate> BuildPolicyRules(PimRolePolicy policy, BulkEditSettings settings, List<string> changes)
    {
        var rules = new List<PolicyRuleUpdate>();
        var current = policy.CurrentSettings;

        // Records a setting only when the new value actually differs, so the
        // log lists exactly what was changed and nothing else.
        void Track(string settingName, string? oldValue, string? newValue)
        {
            var before = string.IsNullOrEmpty(oldValue) ? "(none)" : oldValue;
            var after = string.IsNullOrEmpty(newValue) ? "(none)" : newValue;
            if (string.Equals(before, after, StringComparison.Ordinal)) return;
            changes.Add($"{settingName}: '{before}' -> '{after}'");
        }

        // Activation expiration rule
        {
            Track("Activation max duration (hours)",
                current.ActivationMaxDurationHours.ToString(CultureInfo.InvariantCulture),
                ((int)settings.ActivationMaxDurationHours).ToString(CultureInfo.InvariantCulture));

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

        // Activation enablement rule (always applied - individual items skip if Unchanged)
        var activationEnabledRules = BuildEnablementRules(policy, settings, isActivation: true, changes);
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

        // Assignment enablement rule (always applied - individual items skip if Unchanged)
        var assignmentEnabledRules = BuildEnablementRules(policy, settings, isActivation: false, changes);
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

            Track("Allow permanent eligible assignment",
                current.AllowPermanentEligibleAssignment.ToString(),
                allowPermanent.ToString());

            if (!allowPermanent)
            {
                props["maximumDuration"] = $"P{(int)settings.ExpireEligibleAfterDays}D";
                Track("Expire eligible assignment after (days)",
                    current.ExpireEligibleAfterDays?.ToString(CultureInfo.InvariantCulture),
                    ((int)settings.ExpireEligibleAfterDays).ToString(CultureInfo.InvariantCulture));
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

            Track("Allow permanent active assignment",
                current.AllowPermanentActiveAssignment.ToString(),
                allowPermanent.ToString());

            if (!allowPermanent)
            {
                props["maximumDuration"] = $"P{(int)settings.ExpireActiveAfterDays}D";
                Track("Expire active assignment after (days)",
                    current.ExpireActiveAfterDays?.ToString(CultureInfo.InvariantCulture),
                    ((int)settings.ExpireActiveAfterDays).ToString(CultureInfo.InvariantCulture));
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
            Track("Require approval to activate",
                current.RequireApprovalToActivate.ToString(),
                approvalRequired.ToString());

            Track("Approvers",
                string.Join(", ", current.Approvers.Select(a => a.DisplayName)),
                approvalRequired ? DescribeApprovers(settings.ApproversRaw) : string.Empty);

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
            Track("Require authentication context on activation",
                current.RequireAuthContextOnActivation.ToString(),
                settings.RequireAuthContextOnActivation.ToString());

            if (settings.RequireAuthContextOnActivation)
            {
                Track("Authentication context claim",
                    current.AuthContextClaimValue,
                    settings.AuthContextClaimValue);
            }

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
        BuildNotificationRule(rules, "Notification_Admin_Admin_Eligibility", "Admin", "Eligibility", settings.EligibleAssignmentAdmin,
            "Eligible assignment - admin notification", current.EligibleAssignmentAdminNotification, changes);
        BuildNotificationRule(rules, "Notification_Requestor_Admin_Eligibility", "Requestor", "Eligibility", settings.EligibleAssignmentAssignee,
            "Eligible assignment - assignee notification", current.EligibleAssignmentAssigneeNotification, changes);
        BuildNotificationRule(rules, "Notification_Approver_Admin_Eligibility", "Approver", "Eligibility", settings.EligibleAssignmentApprover,
            "Eligible assignment - approver notification", current.EligibleAssignmentApproverNotification, changes);

        BuildNotificationRule(rules, "Notification_Admin_Admin_Assignment", "Admin", "Assignment", settings.ActiveAssignmentAdmin,
            "Active assignment - admin notification", current.ActiveAssignmentAdminNotification, changes);
        BuildNotificationRule(rules, "Notification_Requestor_Admin_Assignment", "Requestor", "Assignment", settings.ActiveAssignmentAssignee,
            "Active assignment - assignee notification", current.ActiveAssignmentAssigneeNotification, changes);
        BuildNotificationRule(rules, "Notification_Approver_Admin_Assignment", "Approver", "Assignment", settings.ActiveAssignmentApprover,
            "Active assignment - approver notification", current.ActiveAssignmentApproverNotification, changes);

        BuildNotificationRule(rules, "Notification_Admin_EndUser_Assignment", "Admin", "Assignment", settings.ActivationAdmin,
            "Activation - admin notification", current.ActivationAdminNotification, changes, isActivation: true);
        BuildNotificationRule(rules, "Notification_Requestor_EndUser_Assignment", "Requestor", "Assignment", settings.ActivationRequestor,
            "Activation - requestor notification", current.ActivationAssigneeNotification, changes, isActivation: true);
        BuildNotificationRule(rules, "Notification_Approver_EndUser_Assignment", "Approver", "Assignment", settings.ActivationApprover,
            "Activation - approver notification", current.ActivationApproverNotification, changes, isActivation: true);

        return rules;
    }

    /// <summary>Renders the approver selection as readable text for the change log.</summary>
    private static string DescribeApprovers(string approversRaw)
    {
        if (string.IsNullOrWhiteSpace(approversRaw)) return string.Empty;

        var entries = approversRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return $"{entries.Length} approver(s)";
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

    private List<string> BuildEnablementRules(PimRolePolicy policy, BulkEditSettings settings, bool isActivation, List<string> changes)
    {
        var enabledRules = new List<string>();
        var current = policy.CurrentSettings;

        void Track(string settingName, bool oldValue, bool newValue)
        {
            if (oldValue == newValue) return;
            changes.Add($"{settingName}: '{oldValue}' -> '{newValue}'");
        }

        if (isActivation)
        {
            if (settings.RequireMfaOnActivation)
                enabledRules.Add("MultiFactorAuthentication");

            if (settings.RequireJustificationOnActivation)
                enabledRules.Add("Justification");

            if (settings.RequireTicketOnActivation)
                enabledRules.Add("Ticketing");

            Track("Require MFA on activation",
                current.RequireMfaOnActivation, settings.RequireMfaOnActivation);
            Track("Require justification on activation",
                current.RequireJustificationOnActivation, settings.RequireJustificationOnActivation);
            Track("Require ticket on activation",
                current.RequireTicketOnActivation, settings.RequireTicketOnActivation);
        }
        else
        {
            if (settings.RequireMfaOnActiveAssignment)
                enabledRules.Add("MultiFactorAuthentication");

            if (settings.RequireJustificationOnActiveAssignment)
                enabledRules.Add("Justification");

            Track("Require MFA on active assignment",
                current.RequireMfaOnActiveAssignment, settings.RequireMfaOnActiveAssignment);
            Track("Require justification on active assignment",
                current.RequireJustificationOnActiveAssignment, settings.RequireJustificationOnActiveAssignment);
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

    private static void BuildNotificationRule(List<PolicyRuleUpdate> rules, string ruleId, string recipientType, string level,
        NotificationRowSettings row, string displayName, NotificationSettings currentRow, List<string> changes, bool isActivation = false)
    {
        var additionalRecipients = string.IsNullOrWhiteSpace(row.AdditionalRecipients)
            ? Array.Empty<string>()
            : row.AdditionalRecipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var caller = ruleId.Contains("EndUser") ? "EndUser" : "Admin";

        if (currentRow.IsDefaultRecipientsEnabled != row.DefaultRecipients)
        {
            changes.Add($"{displayName} - default recipients: '{currentRow.IsDefaultRecipientsEnabled}' -> '{row.DefaultRecipients}'");
        }

        if (currentRow.CriticalEmailsOnly != row.CriticalOnly)
        {
            changes.Add($"{displayName} - critical emails only: '{currentRow.CriticalEmailsOnly}' -> '{row.CriticalOnly}'");
        }

        var before = string.Join("; ", currentRow.AdditionalRecipients);
        var after = string.Join("; ", additionalRecipients);
        if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"{displayName} - additional recipients: " +
                $"'{(before.Length == 0 ? "(none)" : before)}' -> '{(after.Length == 0 ? "(none)" : after)}'");
        }

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
