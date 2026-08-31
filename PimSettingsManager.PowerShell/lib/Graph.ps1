<#
.SYNOPSIS
	Microsoft Graph REST plumbing for the PowerShell edition of PIMSettings Manager:
	request sending with retry on throttling/transient errors, and nextLink paging.
	Depends on Auth.ps1 (Get-PimAccessToken) and ConsoleUi.ps1 (Write-PimLog).
#>

Set-StrictMode -Version Latest

$script:PimGraphBetaBase = 'https://graph.microsoft.com/beta'
$script:PimGraphMaxRetryAttempts = 3

function Get-PimGraphErrorMessage {
	param($ResponseBody)
	if ($null -eq $ResponseBody) { return $null }
	try {
		$obj = if ($ResponseBody -is [string]) { $ResponseBody | ConvertFrom-Json } else { $ResponseBody }
		return $obj.error.message
	}
	catch {
		return $null
	}
}

function Invoke-PimGraphRequest {
	<#
	.SYNOPSIS
		Sends a Graph request with a per-request bearer token and retries throttling
		or transient server errors (429/503/504), honoring Retry-After when present.
		Returns the parsed JSON response (or $null for empty bodies).
	#>
	[CmdletBinding()]
	param(
		[ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')]
		[string]$Method = 'GET',

		[Parameter(Mandatory)]
		[string]$Uri,

		$Body,

		[switch]$Silent
	)

	$jsonBody = $null
	if ($null -ne $Body) {
		$jsonBody = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 20 }
	}

	if (-not $Silent) {
		Write-PimLog -Level 'DEBUG' -Category 'API' -Message ("{0} {1}" -f $Method, (Get-PimUrlPath -Uri $Uri))
	}

	for ($attempt = 1; ; $attempt++) {
		$token = Get-PimAccessToken
		$headers = @{ Authorization = "Bearer $token" }

		try {
			$params = @{
				Method      = $Method
				Uri         = $Uri
				Headers     = $headers
				ContentType = 'application/json'
				ErrorAction = 'Stop'
			}
			if ($null -ne $jsonBody) { $params['Body'] = $jsonBody }

			return Invoke-RestMethod @params
		}
		catch {
			$statusCode = Get-PimHttpStatusCode -ErrorRecord $_
			$isTransient = $statusCode -in 429, 503, 504

			if (-not $isTransient -or $attempt -ge $script:PimGraphMaxRetryAttempts) {
				$graphMessage = Get-PimGraphErrorMessage -ResponseBody $_.ErrorDetails.Message
				$message = if ($graphMessage) {
					"Graph request failed ($statusCode): $graphMessage"
				}
				else {
					"Graph request failed ($statusCode)."
				}
				Write-PimLog -Level 'ERROR' -Category 'API' -Message $message
				throw $message
			}

			$retryAfter = Get-PimRetryAfterSeconds -ErrorRecord $_ -Attempt $attempt
			Write-PimLog -Level 'WARN' -Category 'API' -Message ("Transient error {0}. Retrying in {1}s (attempt {2}/{3})..." -f $statusCode, $retryAfter, $attempt, $script:PimGraphMaxRetryAttempts)
			Start-Sleep -Seconds $retryAfter
		}
	}
}

function Get-PimHttpStatusCode {
	param($ErrorRecord)
	$response = $ErrorRecord.Exception.Response
	if ($null -ne $response -and $response.PSObject.Properties['StatusCode']) {
		try { return [int]$response.StatusCode } catch { }
	}
	return 0
}

function Get-PimRetryAfterSeconds {
	param($ErrorRecord, [int]$Attempt)
	try {
		$response = $ErrorRecord.Exception.Response
		if ($null -ne $response) {
			$retryAfter = $response.Headers['Retry-After']
			if ($retryAfter) {
				$seconds = 0
				if ([int]::TryParse($retryAfter, [ref]$seconds) -and $seconds -gt 0) {
					return $seconds
				}
			}
		}
	}
	catch { }
	return [int][Math]::Pow(2, $Attempt)
}

function Get-PimUrlPath {
	param([string]$Uri)
	try {
		return ([Uri]$Uri).AbsolutePath
	}
	catch {
		return $Uri
	}
}

function Get-PimGraphPaged {
	<#
	.SYNOPSIS
		Follows @odata.nextLink and returns items from every page. Only follows links
		that stay on the Microsoft Graph host.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$Uri
	)

	$items = New-Object System.Collections.Generic.List[object]
	$nextUrl = $Uri

	while (-not [string]::IsNullOrEmpty($nextUrl)) {
		$result = Invoke-PimGraphRequest -Method GET -Uri $nextUrl

		if ($null -ne $result -and $result.PSObject.Properties['value']) {
			foreach ($item in $result.value) { $items.Add($item) }
		}

		$nextUrl = $null
		if ($null -ne $result -and $result.PSObject.Properties['@odata.nextLink']) {
			$candidate = $result.'@odata.nextLink'
			if ($candidate -and ([Uri]$candidate).Host -eq 'graph.microsoft.com') {
				$nextUrl = $candidate
			}
		}
	}

	return $items
}
