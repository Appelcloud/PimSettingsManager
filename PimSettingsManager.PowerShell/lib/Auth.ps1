<#
.SYNOPSIS
	Interactive Microsoft Graph authentication for the PowerShell edition of
	PIMSettings Manager. Uses the OAuth 2.0 authorization code flow with PKCE and a
	temporary local HTTP listener to capture the browser redirect. No modules required.
#>

Set-StrictMode -Version Latest

# Public Microsoft Graph PowerShell client id - works in any tenant.
$script:PimAuthClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$script:PimAuthAuthority = 'https://login.microsoftonline.com/common'
$script:PimAuthScopes = @(
	'RoleManagementPolicy.ReadWrite.Directory'
	'RoleManagement.ReadWrite.Directory'
	'RoleManagementPolicy.ReadWrite.AzureADGroup'
	'PrivilegedAccess.ReadWrite.AzureADGroup'
	'PrivilegedAccess.ReadWrite.AzureResources'
	'Directory.Read.All'
	'User.Read'
	'offline_access'
)

# Holds the current token state for the session.
$script:PimTokenState = $null

function New-PimCodeVerifier {
	# 43-128 char high-entropy string per RFC 7636.
	$bytes = New-Object 'System.Byte[]' 32
	[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
	return (ConvertTo-PimBase64Url -Bytes $bytes)
}

function ConvertTo-PimBase64Url {
	param([byte[]]$Bytes)
	return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-PimCodeChallenge {
	param([string]$Verifier)
	$sha = [System.Security.Cryptography.SHA256]::Create()
	try {
		$hash = $sha.ComputeHash([System.Text.Encoding]::ASCII.GetBytes($Verifier))
		return (ConvertTo-PimBase64Url -Bytes $hash)
	}
	finally {
		$sha.Dispose()
	}
}

function Start-PimLoopbackListener {
	<#
		Starts an HttpListener on a free loopback port and returns the listener plus
		its redirect uri. The redirect uri uses the standard public-client loopback
		pattern that the Graph PowerShell client accepts.
	#>
	$listener = New-Object System.Net.HttpListener
	$port = Get-PimFreePort
	$redirectUri = "http://localhost:$port/"
	$listener.Prefixes.Add($redirectUri)
	$listener.Start()
	return [pscustomobject]@{
		Listener    = $listener
		RedirectUri = $redirectUri
	}
}

function Get-PimFreePort {
	$tcp = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
	try {
		$tcp.Start()
		return ([System.Net.IPEndPoint]$tcp.LocalEndpoint).Port
	}
	finally {
		$tcp.Stop()
	}
}

function Wait-PimAuthorizationCode {
	<#
		Waits for the browser redirect, returns the authorization code, and writes a
		friendly response page back to the browser. Times out after the given seconds.
	#>
	param(
		[System.Net.HttpListener]$Listener,
		[string]$ExpectedState,
		[int]$TimeoutSeconds = 300
	)

	$contextTask = $Listener.GetContextAsync()
	if (-not $contextTask.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
		throw 'Sign-in timed out waiting for the browser response.'
	}

	$context = $contextTask.Result
	$request = $context.Request
	$query = [System.Web.HttpUtility]::ParseQueryString($request.Url.Query)

	$code = $query['code']
	$state = $query['state']
	$errorCode = $query['error']
	$errorDescription = $query['error_description']

	$isSuccess = [string]::IsNullOrEmpty($errorCode) -and -not [string]::IsNullOrEmpty($code)
	$message = if ($isSuccess) {
		'Signed in to PIMSettings Manager. You can close this tab and return to the console.'
	}
	else {
		'Sign-in failed or was cancelled. You can close this tab and try again in the console.'
	}

	$html = "<html><body style='font-family:Segoe UI;text-align:center;margin-top:40px'>" +
			"<h2>$message</h2></body></html>"
	$buffer = [System.Text.Encoding]::UTF8.GetBytes($html)
	$response = $context.Response
	$response.ContentType = 'text/html'
	$response.ContentLength64 = $buffer.Length
	$response.OutputStream.Write($buffer, 0, $buffer.Length)
	$response.OutputStream.Close()

	if (-not $isSuccess) {
		throw "Authorization failed: $errorCode $errorDescription"
	}
	if ($state -ne $ExpectedState) {
		throw 'Authorization state mismatch (possible CSRF); sign-in aborted.'
	}

	return $code
}

function Invoke-PimTokenRequest {
	<#
		Exchanges an authorization code or refresh token for tokens and stores the
		resulting session state.
	#>
	param([hashtable]$Body)

	$tokenEndpoint = "$script:PimAuthAuthority/oauth2/v2.0/token"
	try {
		$response = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -Body $Body -ContentType 'application/x-www-form-urlencoded'
	}
	catch {
		$detail = $_.ErrorDetails.Message
		if ($detail) { throw "Token request failed: $detail" }
		throw
	}

	$expiresOn = [DateTimeOffset]::UtcNow.AddSeconds([int]$response.expires_in)
	$script:PimTokenState = [pscustomobject]@{
		AccessToken  = $response.access_token
		RefreshToken = $response.refresh_token
		ExpiresOn    = $expiresOn
		Account      = Get-PimAccountFromIdToken -IdToken $response.id_token
	}
	return $script:PimTokenState
}

function Get-PimAccountFromIdToken {
	param([string]$IdToken)
	if ([string]::IsNullOrWhiteSpace($IdToken)) { return $null }
	try {
		$parts = $IdToken.Split('.')
		if ($parts.Count -lt 2) { return $null }
		$payload = $parts[1].Replace('-', '+').Replace('_', '/')
		switch ($payload.Length % 4) {
			2 { $payload += '==' }
			3 { $payload += '=' }
		}
		$json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
		$claims = $json | ConvertFrom-Json
		$upn = $claims.preferred_username
		if (-not $upn) { $upn = $claims.upn }
		return [pscustomobject]@{
			Username = $upn
			TenantId = $claims.tid
			Name     = $claims.name
		}
	}
	catch {
		return $null
	}
}

function Connect-PimGraph {
	<#
	.SYNOPSIS
		Signs the user in interactively via the browser and stores the session token.
	#>
	[CmdletBinding()]
	param()

	Add-Type -AssemblyName System.Web -ErrorAction SilentlyContinue

	$verifier = New-PimCodeVerifier
	$challenge = Get-PimCodeChallenge -Verifier $verifier
	$state = [Guid]::NewGuid().ToString('N')

	$loopback = Start-PimLoopbackListener
	try {
		$scopeParam = [System.Web.HttpUtility]::UrlEncode(($script:PimAuthScopes -join ' '))
		$redirectParam = [System.Web.HttpUtility]::UrlEncode($loopback.RedirectUri)
		$authUrl = "$script:PimAuthAuthority/oauth2/v2.0/authorize" +
			"?client_id=$script:PimAuthClientId" +
			"&response_type=code" +
			"&redirect_uri=$redirectParam" +
			"&response_mode=query" +
			"&scope=$scopeParam" +
			"&state=$state" +
			"&code_challenge=$challenge" +
			"&code_challenge_method=S256" +
			"&prompt=select_account"

		Start-Process $authUrl | Out-Null

		$code = Wait-PimAuthorizationCode -Listener $loopback.Listener -ExpectedState $state

		$body = @{
			client_id     = $script:PimAuthClientId
			grant_type    = 'authorization_code'
			code          = $code
			redirect_uri  = $loopback.RedirectUri
			code_verifier = $verifier
			scope         = ($script:PimAuthScopes -join ' ')
		}
		$tokenState = Invoke-PimTokenRequest -Body $body
		return $tokenState
	}
	finally {
		$loopback.Listener.Stop()
		$loopback.Listener.Close()
	}
}

function Get-PimAccessToken {
	<#
	.SYNOPSIS
		Returns a valid access token, silently refreshing it when close to expiry.
	#>
	[CmdletBinding()]
	param()

	if ($null -eq $script:PimTokenState) {
		throw 'Not signed in. Call Connect-PimGraph first.'
	}

	if ($script:PimTokenState.ExpiresOn -le [DateTimeOffset]::UtcNow.AddMinutes(5)) {
		if ([string]::IsNullOrEmpty($script:PimTokenState.RefreshToken)) {
			throw 'Session expired and no refresh token is available. Please sign in again.'
		}
		$body = @{
			client_id     = $script:PimAuthClientId
			grant_type    = 'refresh_token'
			refresh_token = $script:PimTokenState.RefreshToken
			scope         = ($script:PimAuthScopes -join ' ')
		}
		Invoke-PimTokenRequest -Body $body | Out-Null
	}

	return $script:PimTokenState.AccessToken
}

function Get-PimSignedInAccount {
	if ($null -eq $script:PimTokenState) { return $null }
	return $script:PimTokenState.Account
}

function Disconnect-PimGraph {
	$script:PimTokenState = $null
}
