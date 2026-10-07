using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace BulkPimRoleSettings.Services;

public sealed class AuthService
{
    /// <summary>
    /// Delegated Microsoft Graph permissions the tool needs. Exposed so error
    /// messages can name the exact scopes instead of guessing.
    /// </summary>
    public static readonly string[] RequiredScopes = new[]
    {
        "RoleManagementPolicy.ReadWrite.Directory",
        "RoleManagement.ReadWrite.Directory",
        "RoleManagementPolicy.ReadWrite.AzureADGroup",
        "PrivilegedAccess.ReadWrite.AzureADGroup",
        "PrivilegedAccess.ReadWrite.AzureResources",
        "Directory.Read.All",
        "User.Read"
    };

    /// <summary>
    /// Scopes that must be present for the tool to function at all. The Azure
    /// resources scope is optional because that category is not yet enabled.
    /// </summary>
    private static readonly string[] EssentialScopes = new[]
    {
        "RoleManagementPolicy.ReadWrite.Directory",
        "RoleManagement.ReadWrite.Directory"
    };

    private static string[] Scopes => RequiredScopes;

    // Microsoft Graph PowerShell public client ID - works in any tenant
    private const string ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    private const string Authority = "https://login.microsoftonline.com/common";

    // MSAL client for handling authentication and token acquisition
    private readonly IPublicClientApplication _msalClient;
    private readonly LogService _log = LogService.Instance;
    private AuthenticationResult? _authResult;

    public string? AccessToken => _authResult?.AccessToken;
    public string? UserDisplayName => _authResult?.Account?.Username;
    public string? TenantId => _authResult?.TenantId;
    public DateTimeOffset? ExpiresOn => _authResult?.ExpiresOn;

    /// <summary>
    /// Scopes the identity provider actually issued. Fewer scopes than requested
    /// means consent was withheld for the remainder.
    /// </summary>
    public string[] GrantedScopes => _authResult?.Scopes?.ToArray() ?? Array.Empty<string>();

    /// <summary>
    /// Exact reason the last sign-in attempt failed, ready to show to the user.
    /// Empty when the last attempt succeeded or was cancelled by the user.
    /// </summary>
    public string LastErrorDetail { get; private set; } = string.Empty;

    /// <summary>
    /// Returns the essential scopes that were requested but not granted.
    /// </summary>
    public string[] GetMissingScopes()
    {
        var granted = GrantedScopes;
        if (granted.Length == 0)
            return Array.Empty<string>();

        // Graph returns fully-qualified scope URIs, so compare on the trailing segment.
        var grantedNames = granted
            .Select(s => s.Contains('/') ? s[(s.LastIndexOf('/') + 1)..] : s)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return EssentialScopes
            .Where(required => !grantedNames.Contains(required))
            .ToArray();
    }

    public AuthService()
    {
        // Sign in through the Windows Web Account Manager (WAM) broker.
        _msalClient = PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority(Authority)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .Build();
    }

    public async Task<bool> LoginAsync(IntPtr windowHandle, CancellationToken cancellationToken = default)
    {
        try
        {
            LastErrorDetail = string.Empty;
            _log.Log(LogLevel.INFO, LogCategory.AUTH, "Starting interactive login...");

            // Try silent first
            var accounts = await _msalClient.GetAccountsAsync();
            var account = accounts.FirstOrDefault();

            if (account != null)
            {
                try
                {
                    _authResult = await _msalClient
                        .AcquireTokenSilent(Scopes, account)
                        .ExecuteAsync(cancellationToken);

                    _log.Log(LogLevel.SUCCESS, LogCategory.AUTH,
                        $"Silent token acquired for: {_authResult.Account.Username}");
                    LogGrantedScopes();
                    return true;
                }
                catch (MsalUiRequiredException)
                {
                    _log.Log(LogLevel.INFO, LogCategory.AUTH, "Silent auth failed, falling back to interactive.");
                }
            }

            // Cancel the interactive sign-in after 5 minutes or on user request.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            _authResult = await _msalClient
                .AcquireTokenInteractive(Scopes)
                .WithParentActivityOrWindow(windowHandle)
                .ExecuteAsync(linkedCts.Token);

            _log.Log(LogLevel.SUCCESS, LogCategory.AUTH,
                $"User signed in: {_authResult.Account.Username} | Tenant: {_authResult.TenantId}");
            LogGrantedScopes();

            return true;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            // Sign-in cancelled by the user.
            _authResult = null;
            _log.Log(LogLevel.INFO, LogCategory.AUTH, "Login was cancelled by the user.");
            return false;
        }
        catch (OperationCanceledException)
        {
            // Sign-in cancelled or timed out.
            _authResult = null;
            _log.Log(LogLevel.INFO, LogCategory.AUTH, "Login was cancelled or timed out.");
            return false;
        }
        catch (MsalServiceException ex)
        {
            // Service-side refusals carry the authoritative reason (consent
            // required, blocked by Conditional Access, tenant restrictions).
            _authResult = null;
            LastErrorDetail = DescribeMsalServiceError(ex);
            _log.Log(LogLevel.ERROR, LogCategory.AUTH, $"Sign-in rejected by Entra ID. {LastErrorDetail}");
            return false;
        }
        catch (MsalException ex)
        {
            _authResult = null;
            LastErrorDetail = $"MSAL error code: {ex.ErrorCode}. {ex.Message}";
            _log.Log(LogLevel.ERROR, LogCategory.AUTH, $"Sign-in failed. {LastErrorDetail}");
            _log.LogError(ex, "MSAL authentication failed.");
            return false;
        }
        catch (Exception ex)
        {
            _authResult = null;
            LastErrorDetail = $"{ex.GetType().Name}: {ex.Message}";
            _log.LogError(ex, "Unexpected authentication error.");
            return false;
        }
    }

    /// <summary>
    /// Builds a message containing every identifier Microsoft support asks for:
    /// the AADSTS error code, correlation id and the service message itself.
    /// </summary>
    private static string DescribeMsalServiceError(MsalServiceException ex)
    {
        var parts = new List<string>
        {
            $"Error code: {ex.ErrorCode}",
            $"HTTP status: {ex.StatusCode}"
        };

        if (!string.IsNullOrWhiteSpace(ex.CorrelationId))
            parts.Add($"Correlation id: {ex.CorrelationId}");

        parts.Add($"Message: {ex.Message}");

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Records which permissions were actually consented to, so a later Graph
    /// 403 can be traced back to a scope the tenant never granted.
    /// </summary>
    private void LogGrantedScopes()
    {
        var granted = GrantedScopes;
        _log.Log(LogLevel.INFO, LogCategory.PERMISSION,
            granted.Length > 0
                ? $"Granted scopes: {string.Join(", ", granted)}"
                : "The identity provider did not report any granted scopes.");

        var missing = GetMissingScopes();
        if (missing.Length > 0)
        {
            _log.Log(LogLevel.ERROR, LogCategory.PERMISSION,
                $"Consent is missing for required permission(s): {string.Join(", ", missing)}. "
                + "An administrator must grant consent before PIM settings can be read or changed.");
        }
    }

    public async Task<string?> GetValidTokenAsync()
    {
        // Work on a local snapshot so a concurrent logout can't null-ref us
        // while parallel Graph requests are in flight.
        var current = _authResult;
        if (current == null) return null;

        // Refresh if about to expire
        if (current.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            try
            {
                var accounts = await _msalClient.GetAccountsAsync();
                var account = accounts.FirstOrDefault();
                if (account == null)
                {
                    _log.Log(LogLevel.WARN, LogCategory.AUTH, "No cached account available for token refresh.");
                    return null;
                }

                current = await _msalClient
                    .AcquireTokenSilent(Scopes, account)
                    .ExecuteAsync();
                _authResult = current;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Token refresh failed.");
                return null;
            }
        }

        return current.AccessToken;
    }

    public async Task LogoutAsync()
    {
        // Clear the cached accounts and reset the authentication result.
        var accounts = await _msalClient.GetAccountsAsync();
        foreach (var account in accounts)
        {
            await _msalClient.RemoveAsync(account);
        }
        _authResult = null;
        _log.Log(LogLevel.INFO, LogCategory.AUTH, "User logged out.");
    }
}
