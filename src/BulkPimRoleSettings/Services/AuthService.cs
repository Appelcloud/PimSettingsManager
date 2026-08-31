using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace BulkPimRoleSettings.Services;

public sealed class AuthService
{
    private static readonly string[] Scopes = new[]
    {
        "RoleManagementPolicy.ReadWrite.Directory",
        "RoleManagement.ReadWrite.Directory",
        "RoleManagementPolicy.ReadWrite.AzureADGroup",
        "PrivilegedAccess.ReadWrite.AzureADGroup",
        "PrivilegedAccess.ReadWrite.AzureResources",
        "Directory.Read.All",
        "User.Read"
    };

    // Microsoft Graph PowerShell public client ID - works in any tenant
    private const string ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    private const string Authority = "https://login.microsoftonline.com/common";

    private readonly IPublicClientApplication _msalClient;
    private readonly LogService _log = LogService.Instance;
    private AuthenticationResult? _authResult;

    public string? AccessToken => _authResult?.AccessToken;
    public string? UserDisplayName => _authResult?.Account?.Username;
    public string? TenantId => _authResult?.TenantId;
    public DateTimeOffset? ExpiresOn => _authResult?.ExpiresOn;

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
        catch (MsalException ex)
        {
            _authResult = null;
            _log.LogError(ex, "MSAL authentication failed.");
            return false;
        }
        catch (Exception ex)
        {
            _authResult = null;
            _log.LogError(ex, "Unexpected authentication error.");
            return false;
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
        var accounts = await _msalClient.GetAccountsAsync();
        foreach (var account in accounts)
        {
            await _msalClient.RemoveAsync(account);
        }
        _authResult = null;
        _log.Log(LogLevel.INFO, LogCategory.AUTH, "User logged out.");
    }
}
