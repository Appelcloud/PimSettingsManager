<#
.SYNOPSIS
	Console UI helpers and readable session logging for the PowerShell edition of
	PIMSettings Manager: colored logging to disk and console, menus, single/multi
	selection prompts, yes/no confirmation, and a settings preview renderer.
#>

Set-StrictMode -Version Latest

$script:PimLogPath = $null

function Initialize-PimLog {
	<#
	.SYNOPSIS
		Creates the session log file under %LOCALAPPDATA%\PIMSettingsManager\Logs.
	#>
	[CmdletBinding()]
	param()

	$baseDir = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { [System.IO.Path]::GetTempPath() }
	$logDir = Join-Path $baseDir 'PIMSettingsManager\Logs'
	if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

	$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
	$script:PimLogPath = Join-Path $logDir "PIMSettingsManager_$timestamp.log"
	Set-Content -Path $script:PimLogPath -Value "PIMSettings Manager (PowerShell) session started $(Get-Date -Format o)" -Encoding utf8
	return $script:PimLogPath
}

function Write-PimLog {
	<#
	.SYNOPSIS
		Writes a timestamped, categorized log line to disk and (except DEBUG) the console.
	#>
	[CmdletBinding()]
	param(
		[ValidateSet('DEBUG', 'INFO', 'SUCCESS', 'WARN', 'ERROR')]
		[string]$Level = 'INFO',
		[string]$Category = 'GENERAL',
		[Parameter(Mandatory)][string]$Message
	)

	$line = "{0} [{1,-7}] [{2,-10}] {3}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Category, $Message

	if ($script:PimLogPath) {
		try { Add-Content -Path $script:PimLogPath -Value $line -Encoding utf8 } catch { }
	}

	if ($Level -eq 'DEBUG') { return }

	$color = switch ($Level) {
		'SUCCESS' { 'Green' }
		'WARN'    { 'Yellow' }
		'ERROR'   { 'Red' }
		default   { 'Gray' }
	}
	Write-Host $line -ForegroundColor $color
}

function Get-PimLogPath { return $script:PimLogPath }

function Write-PimHeader {
	param([string]$Title)
	Write-Host ''
	Write-Host ('=' * 60) -ForegroundColor Cyan
	Write-Host "  $Title" -ForegroundColor Cyan
	Write-Host ('=' * 60) -ForegroundColor Cyan
}

function Read-PimMenuChoice {
	<#
	.SYNOPSIS
		Shows a numbered menu and returns the zero-based index of the chosen option.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$Title,
		[Parameter(Mandatory)][string[]]$Options
	)

	Write-PimHeader -Title $Title
	for ($i = 0; $i -lt $Options.Count; $i++) {
		Write-Host ("  {0}. {1}" -f ($i + 1), $Options[$i])
	}

	while ($true) {
		$answer = Read-Host 'Enter choice number'
		$index = 0
		if ([int]::TryParse($answer, [ref]$index) -and $index -ge 1 -and $index -le $Options.Count) {
			return $index - 1
		}
		Write-Host 'Invalid choice. Please try again.' -ForegroundColor Yellow
	}
}

function Read-PimMultiSelect {
	<#
	.SYNOPSIS
		Shows a numbered list and lets the user pick multiple items by index
		(comma/space separated, or "all"). Returns the selected item objects.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$Title,
		[Parameter(Mandatory)] $Items,
		[Parameter(Mandatory)][string]$DisplayProperty
	)

	$itemArray = @($Items)
	Write-PimHeader -Title $Title
	if ($itemArray.Count -eq 0) {
		Write-Host '  (no items available)' -ForegroundColor Yellow
		return @()
	}

	for ($i = 0; $i -lt $itemArray.Count; $i++) {
		Write-Host ("  {0}. {1}" -f ($i + 1), $itemArray[$i].$DisplayProperty)
	}
	Write-Host '  Enter numbers separated by comma/space, or type "all".'

	while ($true) {
		$answer = Read-Host 'Selection'
		if ([string]::IsNullOrWhiteSpace($answer)) {
			Write-Host 'Please select at least one item.' -ForegroundColor Yellow
			continue
		}
		if ($answer.Trim().ToLowerInvariant() -eq 'all') { return $itemArray }

		$tokens = $answer -split '[,\s]+' | Where-Object { $_ }
		$selected = New-Object System.Collections.Generic.List[object]
		$valid = $true
		foreach ($token in $tokens) {
			$index = 0
			if ([int]::TryParse($token, [ref]$index) -and $index -ge 1 -and $index -le $itemArray.Count) {
				$selected.Add($itemArray[$index - 1])
			}
			else {
				$valid = $false
				break
			}
		}
		if ($valid -and $selected.Count -gt 0) {
			return ($selected | Select-Object -Unique)
		}
		Write-Host 'Invalid selection. Please try again.' -ForegroundColor Yellow
	}
}

function Read-PimYesNo {
	param([Parameter(Mandatory)][string]$Question, [bool]$Default = $false)
	$suffix = if ($Default) { '[Y/n]' } else { '[y/N]' }
	while ($true) {
		$answer = (Read-Host "$Question $suffix").Trim().ToLowerInvariant()
		if ([string]::IsNullOrEmpty($answer)) { return $Default }
		if ($answer -in 'y', 'yes') { return $true }
		if ($answer -in 'n', 'no') { return $false }
		Write-Host 'Please answer yes or no.' -ForegroundColor Yellow
	}
}

function Read-PimText {
	param([Parameter(Mandatory)][string]$Prompt, [string]$Default = '')
	$suffix = if ($Default) { " [$Default]" } else { '' }
	$answer = Read-Host "$Prompt$suffix"
	if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
	return $answer.Trim()
}

function Read-PimNumber {
	param([Parameter(Mandatory)][string]$Prompt, [double]$Default = 0)
	while ($true) {
		$answer = Read-Host "$Prompt [$Default]"
		if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
		$value = 0.0
		if ([double]::TryParse($answer, [ref]$value)) { return $value }
		Write-Host 'Please enter a number.' -ForegroundColor Yellow
	}
}

function Show-PimSettingsPreview {
	<#
	.SYNOPSIS
		Renders the desired settings and the list of affected targets before applying.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] $Settings,
		[Parameter(Mandatory)] $Targets
	)

	Write-PimHeader -Title 'Preview of changes'

	$rows = @(
		[pscustomobject]@{ Setting = 'Activation max duration (hours)';         Value = [int]$Settings.ActivationMaxDurationHours }
		[pscustomobject]@{ Setting = 'Require MFA on activation';               Value = $Settings.RequireMfaOnActivation }
		[pscustomobject]@{ Setting = 'Require auth context on activation';      Value = $Settings.RequireAuthContextOnActivation }
		[pscustomobject]@{ Setting = 'Auth context claim value';               Value = $Settings.AuthContextClaimValue }
		[pscustomobject]@{ Setting = 'Require justification on activation';     Value = $Settings.RequireJustificationOnActivation }
		[pscustomobject]@{ Setting = 'Require ticket on activation';            Value = $Settings.RequireTicketOnActivation }
		[pscustomobject]@{ Setting = 'Require approval to activate';            Value = $Settings.RequireApprovalToActivate }
		[pscustomobject]@{ Setting = 'Approvers';                               Value = $Settings.ApproversRaw }
		[pscustomobject]@{ Setting = 'Allow permanent eligible assignment';     Value = $Settings.AllowPermanentEligibleAssignment }
		[pscustomobject]@{ Setting = 'Expire eligible after (days)';            Value = [int]$Settings.ExpireEligibleAfterDays }
		[pscustomobject]@{ Setting = 'Allow permanent active assignment';       Value = $Settings.AllowPermanentActiveAssignment }
		[pscustomobject]@{ Setting = 'Expire active after (days)';              Value = [int]$Settings.ExpireActiveAfterDays }
		[pscustomobject]@{ Setting = 'Require MFA on active assignment';        Value = $Settings.RequireMfaOnActiveAssignment }
		[pscustomobject]@{ Setting = 'Require justification on active assign.'; Value = $Settings.RequireJustificationOnActiveAssignment }
	)
	$rows | Format-Table -AutoSize | Out-Host

	Write-Host ("Targets to update ({0}):" -f @($Targets).Count) -ForegroundColor Cyan
	foreach ($target in $Targets) {
		Write-Host "  - $($target.RoleDisplayName)"
	}
}

function Show-PimApplyResult {
	param([Parameter(Mandatory)] $Results)

	Write-PimHeader -Title 'Apply results'
	$succeeded = @($Results | Where-Object { $_.Success })
	$failed = @($Results | Where-Object { -not $_.Success })

	foreach ($r in $Results) {
		$status = if ($r.Success) { 'OK   ' } else { 'FAIL ' }
		$color = if ($r.Success) { 'Green' } else { 'Red' }
		Write-Host ("  [{0}] {1}" -f $status, $r.Target) -ForegroundColor $color
	}

	Write-Host ''
	Write-Host ("Succeeded: {0}   Failed: {1}" -f $succeeded.Count, $failed.Count) -ForegroundColor Cyan
}
